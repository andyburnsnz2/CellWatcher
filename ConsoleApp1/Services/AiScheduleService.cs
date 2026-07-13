using BatteryEMU.Data;

namespace BatteryEMU.Services;

// Fires scheduled AI reports (quick dashboard summary, deep AI Search report, or both) on a
// daily/weekly/monthly cadence, driven entirely by the battery_ai_schedule DB rows — no restart
// needed to add/edit/remove a schedule. Each row is evaluated independently. Deep reports use the
// period that matches the cadence (Last 24 Hours / Last Week / Last Month), so scheduled runs
// share incremental-analysis continuity with manual runs of the same period type.
public sealed class AiScheduleService : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);

    private readonly MariaDbService _db;
    private readonly AiInsightsOrchestrator _insights;
    private readonly ILogger<AiScheduleService> _logger;

    public AiScheduleService(MariaDbService db, AiInsightsOrchestrator insights, ILogger<AiScheduleService> logger)
    {
        _db = db;
        _insights = insights;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AI schedule service started, checking every {Minutes} minute(s)", CheckInterval.TotalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAndRunAsync(stoppingToken);
                await Task.Delay(CheckInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AI schedule check failed");
            }
        }
    }

    private async Task CheckAndRunAsync(CancellationToken ct)
    {
        var schedules = await _db.GetAiSchedulesAsync(ct);
        var now = DateTime.Now;

        foreach (var entry in schedules)
        {
            var target = ComputeCurrentPeriodTarget(now, entry.Frequency, entry.TimeOfDay, entry.DayOfWeek, entry.DayOfMonth);

            if (target == DateTime.MinValue) continue;
            if (now < target) continue;
            if (entry.LastRunAt is not null && entry.LastRunAt >= target) continue;

            // Weekly targets are exactly 7 days apart, so a fortnightly schedule needs an extra
            // gate to skip every other week's target. The 13-day threshold tolerates minor clock
            // drift / self-healing after downtime while still reliably blocking the next week's target.
            if (entry.Frequency == "fortnightly" && entry.LastRunAt is not null
                && (target - entry.LastRunAt.Value).TotalDays < 13) continue;

            _logger.LogInformation(
                "Running scheduled AI report #{Id}: frequency={Frequency}, target={Target}",
                entry.AiScheduleId, entry.Frequency, target);

            var (from, to, label) = PeriodForFrequency(entry.Frequency, now);
            try { await _insights.RunScheduledAsync(entry, from, to, label, fullDataset: false, ct); }
            catch (Exception ex) { _logger.LogError(ex, "Scheduled AI report #{Id} failed", entry.AiScheduleId); }

            await _db.UpdateAiScheduleLastRunAsync(entry.AiScheduleId!.Value, now, ct);
        }
    }

    private static (DateTime From, DateTime To, string Label) PeriodForFrequency(string frequency, DateTime now) => frequency switch
    {
        "daily"       => (now.AddHours(-24), now, "Last 24 Hours"),
        "weekly"      => (now.AddDays(-7),   now, "Last Week"),
        "fortnightly" => (now.AddDays(-14),  now, "Last 2 Weeks"),
        "monthly"     => (now.AddDays(-30),  now, "Last Month"),
        _             => (now.AddHours(-24), now, "Last 24 Hours"),
    };

    // The most recent scheduled fire time that should have already happened by "now". Comparing
    // LastRunAt against this (rather than an exact-minute match) makes the service self-healing
    // after downtime — if the app was offline at the scheduled minute, it fires on the next check
    // as long as the target is still within the current day/week/month.
    private static DateTime ComputeCurrentPeriodTarget(
        DateTime now, string frequency, TimeSpan timeOfDay, int? dayOfWeek, int? dayOfMonth)
    {
        switch (frequency)
        {
            case "daily":
                return now.Date + timeOfDay;

            case "weekly":
            case "fortnightly":
            {
                var dow = Math.Clamp(dayOfWeek ?? 0, 0, 6);
                var diff = ((int)now.DayOfWeek - dow + 7) % 7;
                return now.Date.AddDays(-diff) + timeOfDay;
            }

            case "monthly":
            {
                var dom = Math.Clamp(dayOfMonth ?? 1, 1, DateTime.DaysInMonth(now.Year, now.Month));
                var target = new DateTime(now.Year, now.Month, dom) + timeOfDay;
                if (target <= now) return target;

                var prevMonth = now.AddMonths(-1);
                var prevDom = Math.Clamp(dayOfMonth ?? 1, 1, DateTime.DaysInMonth(prevMonth.Year, prevMonth.Month));
                return new DateTime(prevMonth.Year, prevMonth.Month, prevDom) + timeOfDay;
            }

            default:
                return DateTime.MinValue;
        }
    }
}
