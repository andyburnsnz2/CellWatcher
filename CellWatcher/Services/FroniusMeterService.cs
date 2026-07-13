using System.Net;
using System.Text;
using System.Text.Json;
using CellWatcher.Models;
using FluentModbus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MQTTnet;

namespace CellWatcher.Services;

// .NET port of fake_meter.py: subscribes to the same MQTT meter-data topic the Python service
// used, encodes the payload into Fronius/Sungrow Modbus register layouts (FroniusMeterRegisterStore
// / FroniusMeterRegisterEncoder), and serves them over Modbus TCP so a Fronius inverter can poll
// this process as if it were a real smart meter.
//
// This is phase 1 of folding the standalone Python fake-meter into CellWatcher: a straight port
// with no mode-switching yet. It always mirrors whatever arrives on the MQTT topic. The planned
// passthrough / fake / forced-charge-intercept broker will decide what feeds these registers
// later; for now that's out of scope.
public sealed class FroniusMeterService : BackgroundService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<FroniusMeterService> _logger;
    private readonly FroniusMeterDebugState _debugState;

    private readonly object _registerWriteLock = new();

    private ModbusTcpServer? _server;
    private string _interfaceType = "";
    private string _mqttTopic = "";
    private byte _meterAddress;
    private bool _meterDebug;
    private int _meterTimeoutSeconds = 1120;
    private ushort[]? _previousDynamicRegisters;
    private int _repeatedValueCount;

    // Wall-clock receipt time of the last successfully-parsed MQTT reading — independent of
    // whatever timestamp HA itself put in the payload. Drives the heartbeat's freshness check.
    private DateTime? _lastMqttMessageReceivedAtUtc;
    private bool _mqttWasStale;

    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(2);

    // Set by BatteryControlService while a forced-charge/prevent-discharge override is running.
    // While true, real incoming MQTT data is recorded (for visibility) but not applied — the
    // override's own synthetic readings are the only thing driving the registers.
    private volatile bool _overrideActive;

    public FroniusMeterService(IConfiguration configuration, ILogger<FroniusMeterService> logger, FroniusMeterDebugState debugState)
    {
        _configuration = configuration;
        _logger = logger;
        _debugState = debugState;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_configuration.GetValue<bool>("FroniusMeter:Enabled"))
        {
            _logger.LogInformation("Fronius fake-meter service disabled (FroniusMeter:Enabled=false)");
            return;
        }

        _interfaceType = _configuration["FroniusMeter:InterfaceType"] ?? "Fronius_ts5ka3";
        _meterTimeoutSeconds = _configuration.GetValue("FroniusMeter:MeterTimeoutSeconds", 1120);
        var fakeUniqueId = _configuration["FroniusMeter:FakeUniqueId"] ?? "1";
        var listenAddress = _configuration["FroniusMeter:ListenAddress"] ?? "0.0.0.0";
        var listenPort = _configuration.GetValue("FroniusMeter:ListenPort", 502);
        _meterAddress = _configuration.GetValue<byte?>("FroniusMeter:MeterAddress")
            ?? FroniusMeterRegisterStore.DefaultMeterAddress(_interfaceType);
        _meterDebug = _configuration.GetValue<bool>("FroniusMeter:Debug");

        // Passing _logger surfaces FluentModbus's own internal diagnostics (malformed frames,
        // protocol errors, connection issues) — previously silent, since the parameterless
        // constructor was used and none of that ever reached our logs.
        _server = new ModbusTcpServer(_logger);
        // FluentModbus starts with only the implicit unit 0 active; the real Fronius/Sungrow
        // meter address (e.g. 33 for TS5KA-3) must be registered explicitly or every request
        // using it gets its connection dropped — see FroniusMeterRegisterStore's header comment.
        _server.AddUnit(_meterAddress);
        FroniusMeterRegisterStore.InitializeStaticRegisters(_server, _interfaceType, fakeUniqueId, _meterAddress);

        // Logs and records EVERY incoming request (not just the latest), while a real GEN24 is
        // being debugged — the debug panel/status pill only ever showed the single latest one,
        // which hid whether something earlier in the conversation was failing.
        //
        // Also replicates pymodbus's ModbusSparseDataBlock: it only serves the exact addresses
        // the Python original declared, returning "Illegal Data Address" for anything else.
        // FluentModbus's contiguous buffer has no such concept on its own (unwritten = zero,
        // always a "successful" read) — GEN24's identification sequence turned out to probe
        // addresses well outside the meter's actual data (e.g. 0, 1706, 40000, 50000), repeating
        // the whole sequence in a loop, consistent with it expecting — and not getting — a
        // rejection at those addresses.
        _server.RequestValidator = (unitIdentifier, functionCode, address, quantity) =>
        {
            _debugState.RecordInverterRequest(unitIdentifier, functionCode.ToString(), address, quantity);

            var inRange = FroniusMeterRegisterStore.IsAddressRangeValid(_interfaceType, address, quantity);
            _logger.LogInformation(
                "Fronius fake-meter Modbus request: unit={UnitIdentifier} fn={FunctionCode} addr={Address} qty={Quantity} inRange={InRange}",
                unitIdentifier, functionCode, address, quantity, inRange);

            return inRange ? ModbusExceptionCode.OK : ModbusExceptionCode.IllegalDataAddress;
        };

        // Surfaces any WRITE the inverter makes to our registers — the original Python code never
        // expected writes (a real meter is read-only from the inverter's perspective), but if GEN24
        // is trying to write something as part of its handshake/validation, we'd otherwise never know.
        _server.EnableRaisingEvents = true;
        _server.RegistersChanged += (sender, e) =>
            _logger.LogWarning(
                "Fronius fake-meter: inverter WROTE to holding registers on unit {UnitIdentifier}: addresses [{Addresses}]",
                e.UnitIdentifier, string.Join(", ", e.Registers));

        await FroniusMeterFirewallHelper.EnsureInboundRuleAsync(listenPort, _logger, stoppingToken);

        try
        {
            _server.Start(new IPEndPoint(IPAddress.Parse(listenAddress), listenPort));
            _logger.LogInformation(
                "Fronius fake-meter ready — inverter should connect (Modbus TCP) to {Address}:{Port} as {InterfaceType} (unit/meter address {MeterAddress})",
                listenAddress, listenPort, _interfaceType, _meterAddress);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start Fronius fake-meter Modbus TCP server on {Address}:{Port}", listenAddress, listenPort);
            return;
        }

        var heartbeatTask = RunHeartbeatAsync(stoppingToken);
        await RunMqttLoopAsync(stoppingToken);
        await heartbeatTask;
    }

    private async Task RunMqttLoopAsync(CancellationToken stoppingToken)
    {
        var factory = new MqttClientFactory();
        var mqttClient = factory.CreateMqttClient();

        mqttClient.ApplicationMessageReceivedAsync += e =>
        {
            var payload = Encoding.UTF8.GetString(e.ApplicationMessage.Payload);
            HandleMeterPayload(payload);
            return Task.CompletedTask;
        };

        var topic = _configuration["FroniusMeter:MqttTopic"] ?? "meter/data";
        _mqttTopic = topic;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Deliberately independent from the Connections tab's Mqtt: settings — mirrors
                // fake_meter.yaml, which had its own standalone mqtt_broker/port/username/password.
                // The two happen to point at the same broker in practice, but keeping them
                // separate avoids "which one is this actually using?" ambiguity.
                var broker = _configuration["FroniusMeter:MqttBroker"]
                    ?? throw new InvalidOperationException("FroniusMeter:MqttBroker must be configured");
                var port = _configuration.GetValue("FroniusMeter:MqttPort", 1883);
                var username = _configuration["FroniusMeter:MqttUsername"];
                var password = _configuration["FroniusMeter:MqttPassword"];

                var optionsBuilder = new MqttClientOptionsBuilder()
                    .WithTcpServer(broker, port)
                    .WithClientId($"CellWatcher-FroniusMeter-{Environment.MachineName}");

                if (!string.IsNullOrWhiteSpace(username))
                    optionsBuilder.WithCredentials(username, password);

                await mqttClient.ConnectAsync(optionsBuilder.Build(), stoppingToken);
                _logger.LogInformation("Fronius fake-meter MQTT connected to {Broker}:{Port}", broker, port);

                var subscribeOptions = factory.CreateSubscribeOptionsBuilder()
                    .WithTopicFilter(f => f.WithTopic(topic))
                    .Build();

                await mqttClient.SubscribeAsync(subscribeOptions, stoppingToken);
                _logger.LogInformation("Fronius fake-meter subscribed to MQTT topic {Topic}", topic);

                while (mqttClient.IsConnected && !stoppingToken.IsCancellationRequested)
                    await Task.Delay(1000, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fronius fake-meter MQTT error. Retrying in 10 seconds.");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }

    // The most recent reading that actually came from real MQTT data, kept even while an override
    // is active. BatteryControlService uses this to seed its synthetic readings' energy counters
    // (which must only ever increase) instead of resetting them to zero, which would look like a
    // meter fault to the inverter.
    public FroniusMeterReading? LastRealReading { get; private set; }

    // The net power (W) actually written to the registers most recently, whichever of the three
    // sources it came from (real MQTT, a BatteryControlService override, or the heartbeat's
    // neutral fallback) — this is "what the inverter is currently being told", used by the
    // dashboard's Pack Controlled By tile regardless of who's driving it right now.
    public double? CurrentAppliedPowerWatts { get; private set; }

    private void HandleMeterPayload(string payload)
    {
        _debugState.RecordIncoming(_mqttTopic, payload);

        FroniusMeterReading? reading;
        try
        {
            reading = JsonSerializer.Deserialize<FroniusMeterReading>(payload);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to decode Fronius fake-meter MQTT payload as JSON");
            return;
        }

        if (reading is null)
            return;

        LastRealReading = reading;
        _lastMqttMessageReceivedAtUtc = DateTime.UtcNow;

        if (_overrideActive)
        {
            // Still tracked above so the debug panel shows real MQTT traffic is arriving and the
            // energy baseline stays current — just not applied to the registers while
            // BatteryControlService's override is running.
            return;
        }

        ApplyReading(reading);
    }

    // Called by BatteryControlService, on its own timer, for as long as a forced-charge /
    // prevent-discharge override is active. Marks the override active so HandleMeterPayload stops
    // applying real MQTT data — this is the actual "override the BE" mechanism.
    public void ApplyOverrideReading(FroniusMeterReading reading)
    {
        _overrideActive = true;
        ApplyReading(reading);
    }

    // Called by BatteryControlService once its schedule/SOC condition ends, handing control back
    // to whatever arrives next on the real MQTT topic.
    public void ClearOverride()
    {
        _overrideActive = false;
        _logger.LogInformation("Fronius fake-meter: override cleared, resuming normal MQTT-driven data");
    }

    // Shared by both the real MQTT path and the override path — everything from here down is
    // "given a reading, from wherever it came from, write it to the registers".
    private void ApplyReading(FroniusMeterReading reading)
    {
        if (_server is null)
            return;

        lock (_registerWriteLock)
        {
            CurrentAppliedPowerWatts = reading.PTotal;

            var write = FroniusMeterRegisterStore.WriteDynamicRegisters(_server, _interfaceType, reading, _meterAddress);
            _debugState.RecordOutgoing(_interfaceType, write.AllBlocks);

            // Mirrors fake_meter.yaml's meter_debug: 1 — the Python original set its whole logger
            // to DEBUG and printed every converted register block on each update. Logged at
            // Information (not LogDebug) so toggling this actually changes visible output
            // regardless of the appsettings.json Logging:LogLevel minimum, same as the Python
            // flag reliably did.
            if (_meterDebug)
            {
                foreach (var (name, values) in write.AllBlocks)
                    _logger.LogInformation("Fronius fake-meter {Block}: [{Values}]", name, string.Join(", ", values));
            }

            if (_previousDynamicRegisters is not null && write.MainBlock.SequenceEqual(_previousDynamicRegisters))
            {
                _repeatedValueCount++;
                if (_repeatedValueCount > 6)
                    _logger.LogWarning("Fronius fake-meter Modbus register data repeated {Count} times", _repeatedValueCount);
            }
            else
            {
                _repeatedValueCount = 0;
            }

            _previousDynamicRegisters = write.MainBlock;
        }
    }

    // Keeps the registers updating at a steady cadence no matter what's driving them — the
    // inverter should never see stale data just because HA stopped publishing. While a
    // BatteryControlService override is active, that service's own timer is already pushing
    // readings at the same cadence, so this loop steps aside. Otherwise: if real MQTT data is
    // fresh, HandleMeterPayload's direct write already covers it and this is a no-op; once MQTT
    // data goes stale (or was never received at all), this switches to actively pushing a neutral
    // no-charge/no-discharge reading every tick, so the meter still looks "alive" and never
    // silently commands charge/discharge based on data nobody's actually sending anymore.
    private async Task RunHeartbeatAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(HeartbeatInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (_overrideActive)
                continue;

            var mqttFresh = _lastMqttMessageReceivedAtUtc is { } lastReceived
                && DateTime.UtcNow - lastReceived <= TimeSpan.FromSeconds(_meterTimeoutSeconds);

            if (mqttFresh)
            {
                _mqttWasStale = false;
                continue;
            }

            if (!_mqttWasStale)
            {
                _mqttWasStale = true;
                _logger.LogError(
                    "Fronius fake-meter: no fresh MQTT data (last received {LastReceived}, timeout={TimeoutSeconds}s) — " +
                    "falling back to a neutral no-charge/no-discharge reading until real data resumes",
                    _lastMqttMessageReceivedAtUtc?.ToString("o") ?? "never", _meterTimeoutSeconds);
            }

            ApplyReading(BuildNeutralFallbackReading());
        }
    }

    // Fabricates an internally-consistent neutral/zero reading rather than cloning the real
    // meter's last per-phase snapshot with only "pt" zeroed — see BatteryControlService.
    // BuildSyntheticReading for why the clone-and-patch approach was tried and reverted (it goes
    // stale and internally inconsistent under a sustained independent-timer override).
    private FroniusMeterReading BuildNeutralFallbackReading()
    {
        var lastReal = LastRealReading;
        const double voltage = 230.0;
        return new FroniusMeterReading
        {
            U1 = voltage, U2 = voltage, U3 = voltage,
            I1 = 0, I2 = 0, I3 = 0,
            Frequency = 50,
            P1 = 0, P2 = 0, P3 = 0, PTotal = 0,
            Pa1 = 0, Pa2 = 0, Pa3 = 0, PaTotal = 0,
            Pr1 = 0, Pr2 = 0, Pr3 = 0, PrTotal = 0,
            Pf1 = 1, Pf2 = 1, Pf3 = 1, PfTotal = 1,
            // Energy counters must only ever increase — freeze at the last real value rather than
            // reset to zero, which would look like a meter fault to the inverter.
            EConsumed = lastReal?.EConsumed ?? 0,
            EProduced = lastReal?.EProduced ?? 0,
            ErConsumed = lastReal?.ErConsumed ?? 0,
            ErProduced = lastReal?.ErProduced ?? 0,
            LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
    }

    public override void Dispose()
    {
        _server?.Dispose();
        base.Dispose();
    }
}
