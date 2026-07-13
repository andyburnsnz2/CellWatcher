using System.Text;
using BatteryEMU.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Protocol;
using MQTTnet.Server;

namespace BatteryEMU.Services;

// Standalone mode: hosts its own MQTT broker so the Fronius inverter can publish directly here
// instead of via Home Assistant's Mosquitto. Every message received is (a) processed in-process
// for this app's own state, exactly like MqttService does in client mode, and (b) forwarded
// onward to the external broker (Mqtt:Broker/Port, e.g. HA's Mosquitto) so anything relying on
// that data there — Home Assistant automations/dashboards — keeps working unchanged. See
// MqttService for client mode (the default), where this app connects out instead of hosting.
//
// Two distinct credential pairs are involved, for two distinct trust relationships:
// Mqtt:BrokerUsername/BrokerPassword is what a client (e.g. Fronius) must present to connect
// to the broker hosted here; Mqtt:Username/Password is what this app presents when it dials
// out to the external broker. They're seeded to the same value so this works out of the box,
// but can be changed independently — e.g. to tighten local auth without touching HA's creds.
public sealed class MqttBrokerService : BackgroundService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<MqttBrokerService> _logger;
    private readonly MqttMessageProcessor _processor;
    private readonly BatteryState _batteryState;
    private readonly BatteryEmulatorCommandPublisher _commandPublisher;
    private readonly DateTime _startedAtUtc = DateTime.UtcNow;

    private IMqttClient? _bridgeClient;

    public MqttBrokerService(
        IConfiguration configuration,
        ILogger<MqttBrokerService> logger,
        BatteryState batteryState,
        NotificationService notifications,
        BatteryEmulatorCommandPublisher commandPublisher)
    {
        _configuration = configuration;
        _logger = logger;
        _batteryState = batteryState;
        _processor = new MqttMessageProcessor(logger, batteryState, notifications);
        _commandPublisher = commandPublisher;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var brokerPort = _configuration.GetValue("Mqtt:BrokerPort", 1883);
        var username = _configuration["Mqtt:BrokerUsername"];
        var password = _configuration["Mqtt:BrokerPassword"];

        var serverOptions = new MqttServerOptionsBuilder()
            .WithDefaultEndpoint()
            .WithDefaultEndpointPort(brokerPort)
            .Build();

        using var server = new MqttServerFactory().CreateMqttServer(serverOptions);

        server.ValidatingConnectionAsync += e =>
        {
            if (!string.IsNullOrWhiteSpace(username) && (e.UserName != username || e.Password != password))
            {
                e.ReasonCode = MqttConnectReasonCode.BadUserNameOrPassword;
                // Surfaced as an Error (after the startup grace period) so it lands in the
                // Errors tab — a rejected connection attempt is exactly the kind of silent
                // failure that's otherwise invisible (the MQTT protocol handles it, so nothing
                // would otherwise throw).
                var mismatch = e.UserName != username ? "username" : "password";
                var level = MqttStartupGrace.IsActive(_startedAtUtc) ? LogLevel.Warning : LogLevel.Error;
                _logger.Log(level,
                    "Rejected MQTT connection from client '{ClientId}' (presented username '{PresentedUsername}') — {Mismatch} did not match the configured Mqtt:BrokerUsername/BrokerPassword.",
                    e.ClientId, e.UserName, mismatch);
            }
            else
            {
                _logger.LogInformation("Accepted MQTT connection from client '{ClientId}' (username '{PresentedUsername}').", e.ClientId, e.UserName);
            }

            return Task.CompletedTask;
        };

        server.InterceptingPublishAsync += e =>
        {
            var topic = e.ApplicationMessage.Topic;
            var payload = Encoding.UTF8.GetString(e.ApplicationMessage.Payload);

            _processor.ProcessMessage(topic, payload);
            _ = ForwardToBridgeAsync(topic, payload);

            return Task.CompletedTask;
        };

        // Mirrors Program.cs's own retry loop for the web server's port bind: a config save
        // triggers a self-respawn, and the old process may not have released this port yet.
        // Without this, a race here would throw out of ExecuteAsync uncaught, which crashes
        // the whole app (not just this service) per BackgroundService's default failure mode.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await server.StartAsync();
                break;
            }
            catch (Exception ex) when (attempt < 10 && !stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Local MQTT broker port {Port} not free yet (attempt {Attempt}/10) — retrying…", brokerPort, attempt);
                await Task.Delay(500, stoppingToken);
            }
        }
        _logger.LogInformation("Standalone MQTT broker listening on port {Port}", brokerPort);

        // The BE connects to this broker directly in standalone mode, so reaching it means
        // injecting a message as if a client had published it — there's no outbound "publish"
        // call here the way there is in client mode (see MqttService).
        _commandPublisher.SetPublishFunction((command, ct) =>
        {
            var message = new MqttApplicationMessageBuilder()
                .WithTopic($"BE/command/{command}")
                .WithPayload(command)
                .Build();
            return server.InjectApplicationMessage(
                new InjectedMqttApplicationMessage(message) { SenderClientId = "BatteryEMU-ContactorControl" },
                ct);
        });

        try
        {
            await RunBridgeAsync(stoppingToken);
        }
        finally
        {
            await server.StopAsync(new MqttServerStopOptions());
        }
    }

    // Keeps a persistent outbound connection to the external broker (e.g. Home Assistant's
    // Mosquitto) alive for as long as this service runs, so ForwardToBridgeAsync can publish
    // whatever Fronius sends us onward to it. Best-effort: messages are dropped (not queued)
    // while the bridge is disconnected, consistent with how transient MQTT drops are already
    // tolerated elsewhere in this app (see MqttService's own reconnect loop).
    private async Task RunBridgeAsync(CancellationToken stoppingToken)
    {
        _bridgeClient = new MqttClientFactory().CreateMqttClient();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var broker = _configuration["Mqtt:Broker"] ?? throw new InvalidOperationException("Missing Mqtt:Broker");
                var port = _configuration.GetValue<int>("Mqtt:Port");
                var username = _configuration["Mqtt:Username"];
                var password = _configuration["Mqtt:Password"];

                var optionsBuilder = new MqttClientOptionsBuilder()
                    .WithTcpServer(broker, port)
                    .WithClientId($"BatteryEMU-Bridge-{Environment.MachineName}")
                    // This connection is meant to stay up permanently (it's always going to be
                    // talked to), so ping often: keeps any NAT/firewall state on the path alive
                    // and lets MQTTnet notice a dead connection well within the ~2-3 minute gaps
                    // observed between drops, instead of only after a long default keep-alive.
                    .WithKeepAlivePeriod(TimeSpan.FromSeconds(15));

                if (!string.IsNullOrWhiteSpace(username))
                    optionsBuilder.WithCredentials(username, password);

                await _bridgeClient.ConnectAsync(optionsBuilder.Build(), stoppingToken);

                _logger.LogInformation("Bridge connected to external MQTT broker {Broker}:{Port}", broker, port);

                while (_bridgeClient.IsConnected && !stoppingToken.IsCancellationRequested)
                    await Task.Delay(1000, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                var level = MqttStartupGrace.IsActive(_startedAtUtc) ? LogLevel.Warning : LogLevel.Error;
                _logger.Log(level, ex, "MQTT bridge error. Retrying in 10 seconds.");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }

    private async Task ForwardToBridgeAsync(string topic, string payload)
    {
        try
        {
            if (_bridgeClient is not { IsConnected: true }) return;

            // QoS 1 (AtLeastOnce) instead of the default QoS 0 so the broker actually sends
            // back a PUBACK we can check — QoS 0 has no acknowledgment defined by the MQTT
            // protocol at all, so failures were previously only visible if the whole bridge
            // connection dropped, not per-message. This is negotiated per-publish and needs
            // no change on the Home Assistant/Mosquitto side to accept it.
            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();

            var result = await _bridgeClient.PublishAsync(message, CancellationToken.None);

            if (result.IsSuccess)
            {
                _batteryState.RecordMqttForwardAck();
            }
            else
            {
                var level = MqttStartupGrace.IsActive(_startedAtUtc) ? LogLevel.Warning : LogLevel.Error;
                _logger.Log(level,
                    "Forward to external broker not acknowledged for topic {Topic}: {ReasonCode} {ReasonString}",
                    topic, result.ReasonCode, result.ReasonString);
            }
        }
        catch (Exception ex)
        {
            var level = MqttStartupGrace.IsActive(_startedAtUtc) ? LogLevel.Warning : LogLevel.Error;
            _logger.Log(level, ex, "Failed to forward MQTT message {Topic} to external broker", topic);
        }
    }
}
