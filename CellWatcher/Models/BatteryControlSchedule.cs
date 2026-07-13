namespace CellWatcher.Models;

// Single-row config (always id=1) for the forced-charge / prevent-discharge override — see
// BatteryControlService for how this drives the actual override behavior, and
// create_battery_control_schedule.sql for the backing table.
public sealed record BatteryControlSchedule(
    string ActivationMode,  // "off" | "manual" | "scheduled"
    bool ManualRunRequested, // set by the Start/Stop control on the Battery Balancing page — only meaningful when ActivationMode = "manual"
    string Mode,       // "force_charge" | "prevent_discharge" — applies regardless of activation mode
    bool Monday,
    bool Tuesday,
    bool Wednesday,
    bool Thursday,
    bool Friday,
    bool Saturday,
    bool Sunday,
    TimeSpan StartTime,
    TimeSpan EndTime,
    decimal TargetSocPercent,
    int HoldAtTargetMinutes, // once target SOC is reached, keep running (on a balanced/neutral signal) this long before actually stopping — 0 stops immediately
    int ChargeDischargePowerWatts, // persisted — 500 is only ever the seed value for a brand-new row, never re-applied over a previously-set value on restart
    DateTime? LastStartedAt,
    DateTime? LastStoppedAt)
{
    public bool IsDayEnabled(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => Monday,
        DayOfWeek.Tuesday => Tuesday,
        DayOfWeek.Wednesday => Wednesday,
        DayOfWeek.Thursday => Thursday,
        DayOfWeek.Friday => Friday,
        DayOfWeek.Saturday => Saturday,
        DayOfWeek.Sunday => Sunday,
        _ => false,
    };

    // Null when activation isn't "scheduled" at all, or no day is checked. Otherwise the next
    // future point in time (today included) where an enabled day's window opens — used for the
    // dashboard summary. Doesn't account for "we're currently inside today's window" specially;
    // it just reports the next window-open moment, which is what "next scheduled start" means.
    public DateTime? NextScheduledStart(DateTime now)
    {
        if (ActivationMode != "scheduled")
            return null;

        for (var offset = 0; offset < 7; offset++)
        {
            var candidateDate = now.Date.AddDays(offset);
            if (!IsDayEnabled(candidateDate.DayOfWeek))
                continue;

            var candidate = candidateDate + StartTime;
            if (candidate > now)
                return candidate;
        }

        return null; // no day enabled at all
    }
}
