using BatteryEMU.Data;
using BatteryEMU.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BatteryEMU.Services;

public sealed class PackHistorianService : BackgroundService
{
    private readonly ILogger<PackHistorianService> _logger;
    private readonly IConfiguration _configuration;
    private readonly BatteryState _batteryState;
    private readonly MariaDbService _mariaDbService;

    private BatterySnapshot? _lastSavedSnapshot;
    private DateTime? _lastSavedAt;

    public PackHistorianService(
        ILogger<PackHistorianService> logger,
        IConfiguration configuration,
        BatteryState batteryState,
        MariaDbService mariaDbService)
    {
        _logger = logger;
        _configuration = configuration;
        _batteryState = batteryState;
        _mariaDbService = mariaDbService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var checkSeconds = _configuration.GetValue<int>("PackLogging:MinimumIntervalSeconds", 30);

        _logger.LogInformation("Pack historian started. Check interval = {Seconds}s", checkSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(checkSeconds), stoppingToken);

                var snapshot = _batteryState.CreateSnapshot();

                if (!ShouldSave(snapshot))
                {
                    _logger.LogInformation("Pack reading skipped. No meaningful pack change.");
                    continue;
                }

                await _mariaDbService.SavePackReadingAsync(snapshot, stoppingToken);

                _lastSavedSnapshot = snapshot;
                _lastSavedAt = snapshot.ReadAt;

                _logger.LogInformation(
                    "Pack reading saved. SOC={Soc}% V={Voltage} I={Current} P={Power}",
                    snapshot.SocPercent,
                    snapshot.PackVoltageV,
                    snapshot.PackCurrentA,
                    snapshot.PackPowerW);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed saving pack reading.");
            }
        }
    }

    private bool ShouldSave(BatterySnapshot current)
    {
        if (_lastSavedSnapshot == null || _lastSavedAt == null)
            return true;

        var heartbeatMinutes = _configuration.GetValue<int>("PackLogging:HeartbeatMinutes", 15);

        if ((DateTime.Now - _lastSavedAt.Value).TotalMinutes >= heartbeatMinutes)
            return true;

        if (Changed(_lastSavedSnapshot.SocPercent, current.SocPercent,
                _configuration.GetValue<decimal>("PackLogging:SocChangePercent", 0.1m)))
            return true;

        if (Changed(_lastSavedSnapshot.PackVoltageV, current.PackVoltageV,
                _configuration.GetValue<decimal>("PackLogging:PackVoltageChangeV", 0.5m)))
            return true;

        if (Changed(_lastSavedSnapshot.PackCurrentA, current.PackCurrentA,
                _configuration.GetValue<decimal>("PackLogging:PackCurrentChangeA", 1.0m)))
            return true;

        if (Changed(_lastSavedSnapshot.PackPowerW, current.PackPowerW,
                _configuration.GetValue<decimal>("PackLogging:PackPowerChangeW", 250m)))
            return true;

        if (Changed(_lastSavedSnapshot.TemperatureMinC, current.TemperatureMinC,
                _configuration.GetValue<decimal>("PackLogging:TemperatureChangeC", 1.0m)))
            return true;

        if (Changed(_lastSavedSnapshot.TemperatureMaxC, current.TemperatureMaxC,
                _configuration.GetValue<decimal>("PackLogging:TemperatureChangeC", 1.0m)))
            return true;

        if (Changed(_lastSavedSnapshot.RemainingCapacityWh, current.RemainingCapacityWh,
                _configuration.GetValue<decimal>("PackLogging:CapacityChangeWh", 100m)))
            return true;

        if (_lastSavedSnapshot.BmsStatus != current.BmsStatus)
            return true;

        if (_lastSavedSnapshot.PauseStatus != current.PauseStatus)
            return true;

        if (_lastSavedSnapshot.EmulatorStatus != current.EmulatorStatus)
            return true;

        if (_lastSavedSnapshot.EventLevel != current.EventLevel)
            return true;

        return false;
    }

    private static bool Changed(decimal? previous, decimal? current, decimal threshold)
    {
        if (!previous.HasValue && current.HasValue)
            return true;

        if (previous.HasValue && !current.HasValue)
            return true;

        if (!previous.HasValue && !current.HasValue)
            return false;

        return Math.Abs(current!.Value - previous!.Value) >= threshold;
    }
}