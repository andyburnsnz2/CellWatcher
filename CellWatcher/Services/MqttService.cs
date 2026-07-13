using System.Text;
using CellWatcher.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MQTTnet;

namespace CellWatcher.Services;

// Client mode (default): connects out to an external broker (e.g. Home Assistant's Mosquitto)
// and subscribes for battery telemetry. See MqttBrokerService for standalone mode, where this
// app hosts its own broker instead.
public sealed class MqttService : BackgroundService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<MqttService> _logger;
    private readonly MqttMessageProcessor _processor;
    private readonly BatteryEmulatorCommandPublisher _commandPublisher;
    private readonly DateTime _startedAtUtc = DateTime.UtcNow;

    public MqttService(
        IConfiguration configuration,
        ILogger<MqttService> logger,
        BatteryState batteryState,
        NotificationService notifications,
        BatteryEmulatorCommandPublisher commandPublisher)
    {
        _configuration = configuration;
        _logger = logger;
        _processor = new MqttMessageProcessor(logger, batteryState, notifications);
        _commandPublisher = commandPublisher;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var factory = new MqttClientFactory();
        var mqttClient = factory.CreateMqttClient();

        mqttClient.ApplicationMessageReceivedAsync += e =>
        {
            var topic = e.ApplicationMessage.Topic;
            var payload = Encoding.UTF8.GetString(e.ApplicationMessage.Payload);

            _processor.ProcessMessage(topic, payload);

            return Task.CompletedTask;
        };

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var broker = _configuration["Mqtt:Broker"] ?? throw new InvalidOperationException("Missing Mqtt:Broker");
                var port = _configuration.GetValue<int>("Mqtt:Port");
                var username = _configuration["Mqtt:Username"];
                var password = _configuration["Mqtt:Password"];
                var topic = _configuration["Mqtt:Topic"] ?? "BE/#";

                var optionsBuilder = new MqttClientOptionsBuilder()
                    .WithTcpServer(broker, port)
                    .WithClientId($"CellWatcher-{Environment.MachineName}");

                if (!string.IsNullOrWhiteSpace(username))
                    optionsBuilder.WithCredentials(username, password);

                await mqttClient.ConnectAsync(optionsBuilder.Build(), stoppingToken);

                _logger.LogInformation("Connected to MQTT broker {Broker}:{Port}", broker, port);

                var subscribeOptions = factory.CreateSubscribeOptionsBuilder()
                    .WithTopicFilter(f => f.WithTopic(topic))
                    .Build();

                await mqttClient.SubscribeAsync(subscribeOptions, stoppingToken);

                _logger.LogInformation("Subscribed to MQTT topic {Topic}", topic);

                // Client mode: the BE is (presumably) also connected to this same external
                // broker, so reaching it is just a normal outbound publish — unlike standalone
                // mode (see MqttBrokerService), where this app IS the broker the BE connects to.
                _commandPublisher.SetPublishFunction((command, ct) =>
                {
                    var message = new MqttApplicationMessageBuilder()
                        .WithTopic($"BE/command/{command}")
                        .WithPayload(command)
                        .Build();
                    return mqttClient.PublishAsync(message, ct);
                });

                while (mqttClient.IsConnected && !stoppingToken.IsCancellationRequested)
                    await Task.Delay(1000, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                var level = MqttStartupGrace.IsActive(_startedAtUtc) ? LogLevel.Warning : LogLevel.Error;
                _logger.Log(level, ex, "MQTT error. Retrying in 10 seconds.");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }
}
