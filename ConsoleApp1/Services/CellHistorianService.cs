using BatteryEMU.Data;
using BatteryEMU.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BatteryEMU.Services;

public sealed class CellHistorianService : BackgroundService
{
    private readonly ILogger<CellHistorianService> _logger;
    private readonly IConfiguration _configuration;
    private readonly BatteryState _batteryState;
    private readonly MariaDbService _mariaDbService;

    private BatterySnapshot? _lastSavedSnapshot;
    private DateTime? _lastSavedAt;

    public CellHistorianService(
        ILogger<CellHistorianService> logger,
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
        var checkSeconds = _configuration.GetValue<int>("CellLogging:MinimumIntervalSeconds", 30);
        var expectedCells = _configuration.GetValue<int>("CellLogging:ExpectedCellCount", 108);

        _logger.LogInformation("Cell historian started. Check interval = {Seconds}s", checkSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(checkSeconds), stoppingToken);

                var snapshot = _batteryState.CreateSnapshot();

                if (snapshot.CellVoltages.Count != expectedCells)
                {
                    _logger.LogWarning(
                        "Expected {Expected} cells but received {Actual}. Cell snapshot skipped.",
                        expectedCells,
                        snapshot.CellVoltages.Count);

                    continue;
                }

                if (!ShouldSave(snapshot))
                {
                    _logger.LogInformation("Cell snapshot skipped. No meaningful cell change.");
                    continue;
                }

                await _mariaDbService.SaveCellSnapshotAsync(snapshot, stoppingToken);

                _lastSavedSnapshot = snapshot;
                _lastSavedAt = snapshot.ReadAt;

                _logger.LogInformation(
                    "Cell snapshot saved. Cells={Cells} Delta={Delta}mV MinCell={MinCell} MaxCell={MaxCell}",
                    snapshot.CellVoltages.Count,
                    snapshot.CellDeltaMv,
                    snapshot.MinCellNo,
                    snapshot.MaxCellNo);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed saving cell snapshot.");
            }
        }
    }

    private bool ShouldSave(BatterySnapshot current)
    {
        if (_lastSavedSnapshot == null || _lastSavedAt == null)
            return true;

        var heartbeatMinutes = _configuration.GetValue<int>("CellLogging:HeartbeatMinutes", 60);

        if ((DateTime.Now - _lastSavedAt.Value).TotalMinutes >= heartbeatMinutes)
            return true;

        if (Changed(_lastSavedSnapshot.CellDeltaMv, current.CellDeltaMv,
                _configuration.GetValue<decimal>("CellLogging:CellDeltaChangeMv", 1m)))
            return true;

        if (_lastSavedSnapshot.MinCellNo != current.MinCellNo)
            return true;

        if (_lastSavedSnapshot.MaxCellNo != current.MaxCellNo)
            return true;

        if (CellsChanged(_lastSavedSnapshot, current))
            return true;

        return false;
    }

    private bool CellsChanged(BatterySnapshot previous, BatterySnapshot current)
    {
        var thresholdMv = _configuration.GetValue<decimal>("CellLogging:CellVoltageChangeMv", 1m);
        var thresholdV = thresholdMv / 1000m;

        foreach (var currentCell in current.CellVoltages)
        {
            if (!previous.CellVoltages.TryGetValue(currentCell.Key, out var previousVoltage))
                return true;

            if (Math.Abs(currentCell.Value - previousVoltage) >= thresholdV)
                return true;
        }

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