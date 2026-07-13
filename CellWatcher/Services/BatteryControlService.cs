using CellWatcher.Data;
using CellWatcher.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CellWatcher.Services;

// Independent timer-driven override for the Fronius fake-meter: on a day/time schedule, either
// forces a full charge from the grid or just prevents discharge (letting real PV charge the
// battery naturally over the scheduled window), bypassing whatever the BE is actually reporting
// via MQTT for as long as it's active. This is the "own timer thread" half of the mechanism —
// see FroniusMeterService.ApplyOverrideReading/ClearOverride for the other half, which is what
// actually stops real MQTT data from reaching the registers while this runs.
//
// The override always stops if the real battery SOC (from BatteryState, sourced from the BE's own
// MQTT telemetry — separate from the Fronius meter feed) reaches the configured target, even if
// the scheduled window is still open. It never runs a charge cycle past a full pack.
public sealed class BatteryControlService : BackgroundService
{
    // How often the override pushes a fresh synthetic reading while active. Roughly matches the
    // cadence real MQTT meter updates arrive at, so the inverter never sees data go stale and the
    // last_updated-based watchdog in FroniusMeterService never trips during a long override.
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ScheduleRefreshInterval = TimeSpan.FromSeconds(30);

    // Persisted in battery_control_schedule — 500W is only ever the seed value for a brand-new
    // row (see create_battery_control_schedule.sql), never re-applied over whatever was actually
    // configured. An earlier version of this reset to 500W on every process start on purpose
    // ("never silently resume a high charge rate without re-confirming"); that turned out to be
    // more annoying than protective in practice, since every redeploy/restart threw away a
    // deliberately configured value — reverted in favour of just persisting it properly.
    private const int MaxChargeDischargePowerWatts = 10_000;

    private readonly MariaDbService _db;
    private readonly BatteryState _batteryState;
    private readonly FroniusMeterService _froniusMeterService;
    private readonly ILogger<BatteryControlService> _logger;

    private BatteryControlSchedule? _schedule;
    private DateTime _scheduleLoadedAtUtc = DateTime.MinValue;
    private bool _overrideActive;

    // Runtime state for the current run only — reset whenever a new run starts.
    private DateTime? _targetReachedAtUtc;
    private int? _historyId;

    // For the control page's live status panel.
    public bool IsOverrideActive => _overrideActive;
    public int ChargeDischargePowerWatts => _schedule?.ChargeDischargePowerWatts ?? 500;

    // Called by the API endpoint right after persisting to the database — updates this service's
    // own cached schedule immediately so the change takes effect on the very next tick (~2s)
    // instead of waiting for the next periodic schedule reload (up to 30s).
    public void ApplyChargeDischargePowerWattsLocally(int watts)
    {
        if (_schedule is not null)
            _schedule = _schedule with { ChargeDischargePowerWatts = watts };
    }

    // Called by the API endpoint right after a full schedule save (mode, days, target SOC, hold
    // time, etc.) — forces the next tick to reload from the database instead of running on a
    // stale cached schedule for up to ScheduleRefreshInterval (30s). Without this, e.g. lowering
    // the target SOC to below the pack's current level could keep commanding a force-charge for
    // up to 30 more seconds after being saved.
    public void InvalidateScheduleCache() => _scheduleLoadedAtUtc = DateTime.MinValue;

    public BatteryControlService(
        MariaDbService db,
        BatteryState batteryState,
        FroniusMeterService froniusMeterService,
        ILogger<BatteryControlService> logger)
    {
        _db = db;
        _batteryState = batteryState;
        _froniusMeterService = froniusMeterService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await ReconcileStartupStateAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Battery control: error reconciling startup state");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Battery control: error while evaluating schedule");
            }

            try
            {
                await Task.Delay(UpdateInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        if (_overrideActive)
        {
            _froniusMeterService.ClearOverride();
            if (_historyId is { } historyId)
            {
                try { await _db.RecordBatteryControlHistoryStoppedAsync(historyId, DateTime.Now, "app_shutdown", CancellationToken.None); }
                catch (Exception ex) { _logger.LogError(ex, "Battery control: failed to record shutdown in history"); }
            }
        }
    }

    // Picks up an in-progress run that was still active when the process last stopped, instead of
    // starting a brand-new history row for it — without this, every restart while a run was
    // active (very common during active development/redeploys) inserted a duplicate "running"
    // row, since _overrideActive/_historyId are in-memory and reset to false/null on every fresh
    // instance. The normal TickAsync logic below then evaluates the adopted run against the
    // current schedule exactly as it would any other tick — closing it out immediately if the
    // schedule no longer wants it active (window closed / manual stop while the app was down), or
    // just continuing it seamlessly if it does.
    private async Task ReconcileStartupStateAsync(CancellationToken stoppingToken)
    {
        var openEntries = await _db.GetOpenBatteryControlHistoryEntriesAsync(stoppingToken);
        if (openEntries.Count == 0)
            return;

        var adopted = openEntries[0]; // newest-first
        _historyId = adopted.Id;
        _targetReachedAtUtc = adopted.TargetReachedAt?.ToUniversalTime();
        _overrideActive = true;
        _logger.LogWarning(
            "Battery control: resuming in-progress run from history id {HistoryId} (started {StartedAt})",
            adopted.Id, adopted.StartedAt);

        // Any other still-open rows are stale duplicates from previous restarts (the bug this
        // method fixes) — close them out so the history doesn't show multiple simultaneous
        // "running" entries.
        for (var i = 1; i < openEntries.Count; i++)
        {
            await _db.RecordBatteryControlHistoryStoppedAsync(openEntries[i].Id, DateTime.Now, "duplicate_on_restart", stoppingToken);
            _logger.LogWarning("Battery control: closed stale duplicate history row id {HistoryId}", openEntries[i].Id);
        }
    }

    private async Task TickAsync(CancellationToken stoppingToken)
    {
        if (_schedule is null || DateTime.UtcNow - _scheduleLoadedAtUtc > ScheduleRefreshInterval)
        {
            _schedule = await _db.GetBatteryControlScheduleAsync(stoppingToken);
            _scheduleLoadedAtUtc = DateTime.UtcNow;
        }

        var schedule = _schedule;

        // The manual/window gate — independent of whether target SOC has been reached. Used both
        // to decide whether a run should start at all, and (once running) whether something other
        // than the hold timer wants it stopped (window closed, or manual Stop was pressed).
        var scheduleWantsActive = schedule.ActivationMode switch
        {
            "manual" => schedule.ManualRunRequested,
            "scheduled" => IsWithinScheduledWindow(schedule),
            _ => false, // "off"
        };

        var soc = _batteryState.SocPercent;
        var targetReached = soc is not null && soc >= schedule.TargetSocPercent;

        if (_overrideActive && scheduleWantsActive && targetReached && _targetReachedAtUtc is null)
        {
            _targetReachedAtUtc = DateTime.UtcNow;
            _logger.LogWarning("Battery control: target SOC reached, holding for {HoldMinutes} minute(s)", schedule.HoldAtTargetMinutes);
            if (_historyId is { } reachedHistoryId)
                await _db.RecordBatteryControlHistoryTargetReachedAsync(reachedHistoryId, DateTime.Now, stoppingToken);
        }

        var holdExpired = _targetReachedAtUtc is not null
            && DateTime.UtcNow - _targetReachedAtUtc >= TimeSpan.FromMinutes(schedule.HoldAtTargetMinutes);

        // Stop once told to (window closed / manual stop) OR once the post-target hold has
        // elapsed — whichever comes first. Reaching target alone doesn't stop it; the hold does.
        var shouldBeActive = scheduleWantsActive && !holdExpired;

        if (shouldBeActive && !_overrideActive)
        {
            _overrideActive = true;
            _targetReachedAtUtc = null;
            _logger.LogWarning(
                "Battery control: starting override (mode={Mode}, target SOC={TargetSoc}%, hold={HoldMinutes}min)",
                schedule.Mode, schedule.TargetSocPercent, schedule.HoldAtTargetMinutes);
            await _db.RecordBatteryControlStartedAsync(DateTime.Now, stoppingToken);
            _historyId = await _db.RecordBatteryControlHistoryStartAsync(
                DateTime.Now, schedule.ActivationMode, schedule.Mode, schedule.TargetSocPercent, stoppingToken);
        }
        else if (!shouldBeActive && _overrideActive)
        {
            _overrideActive = false;
            _froniusMeterService.ClearOverride();
            _logger.LogWarning("Battery control: stopping override");
            await _db.RecordBatteryControlStoppedAsync(DateTime.Now, stoppingToken);

            if (_historyId is { } stoppedHistoryId)
            {
                var stopReason = targetReached && holdExpired
                    ? "target_reached_hold_expired"
                    : schedule.ActivationMode == "manual" ? "manual_stop" : "window_closed";
                await _db.RecordBatteryControlHistoryStoppedAsync(stoppedHistoryId, DateTime.Now, stopReason, stoppingToken);
            }
            _historyId = null;
            _targetReachedAtUtc = null;

            // Manual runs don't auto-repeat — clear the request so the Start/Stop button
            // correctly shows "stopped" and it doesn't immediately restart next tick.
            if (schedule.ActivationMode == "manual" && schedule.ManualRunRequested)
                await _db.SetManualRunRequestedAsync(false, stoppingToken);
        }

        if (_overrideActive)
        {
            // Once target is reached, switch to the balanced/neutral signal regardless of the
            // configured mode — never keep commanding a full-power charge into an already-full
            // pack just because it's still within its hold window.
            var effectiveMode = _targetReachedAtUtc is not null ? "prevent_discharge" : schedule!.Mode;
            var reading = BuildSyntheticReading(effectiveMode, schedule!.ChargeDischargePowerWatts, _froniusMeterService.LastRealReading);
            _froniusMeterService.ApplyOverrideReading(reading);
        }
    }

    private static bool IsWithinScheduledWindow(BatteryControlSchedule schedule)
    {
        var now = DateTime.Now;
        if (!schedule.IsDayEnabled(now.DayOfWeek))
            return false;

        var timeOfDay = now.TimeOfDay;
        return schedule.StartTime <= schedule.EndTime
            ? timeOfDay >= schedule.StartTime && timeOfDay <= schedule.EndTime
            : timeOfDay >= schedule.StartTime || timeOfDay <= schedule.EndTime; // overnight window, e.g. 22:00-06:00
    }

    // Sign convention confirmed against real hardware: a positive reading here made the inverter
    // discharge, not charge — so force_charge uses a NEGATIVE value (fake grid import/consumption)
    // to make the inverter's self-consumption logic charge the battery to cover it.
    //
    // Deliberately fabricates an internally-consistent synthetic reading — evenly split across
    // all three phases, unity power factor, zero reactive power — rather than cloning the real
    // meter's last per-phase snapshot and only touching the total. That clone-and-patch approach
    // was tried and reverted: it matches power_controller.py's adjust_inverter_power literally,
    // but that function only works because it's purely reactive, republishing within milliseconds
    // of every real message so the per-phase data it reuses is always current. Our override runs
    // on its own independent timer, potentially for a long time, so reusing a real snapshot means
    // the per-phase figures go stale and stop relating to the new commanded total at all — the
    // aggregate and per-phase numbers end up internally inconsistent, which produced a bizarre
    // Grid/Load split on the real inverter's own display in testing. Fabricating consistent values
    // (verified correct via real 4kW/6kW charge tests, matching perfectly) is the proven approach.
    private static FroniusMeterReading BuildSyntheticReading(string mode, int chargeDischargePowerWatts, FroniusMeterReading? lastReal)
    {
        var totalPowerW = mode == "force_charge" ? -(double)chargeDischargePowerWatts : 0.0;
        var perPhaseW = totalPowerW / 3.0;
        const double voltage = 230.0;

        return new FroniusMeterReading
        {
            U1 = voltage, U2 = voltage, U3 = voltage,
            I1 = perPhaseW / voltage, I2 = perPhaseW / voltage, I3 = perPhaseW / voltage,
            Frequency = 50,
            P1 = perPhaseW, P2 = perPhaseW, P3 = perPhaseW, PTotal = totalPowerW,
            Pa1 = perPhaseW, Pa2 = perPhaseW, Pa3 = perPhaseW, PaTotal = totalPowerW,
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
}
