using BatteryEMU.Data;
using BatteryEMU.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BatteryEMU.Services;

public sealed class BatteryHealthAnalysisService : BackgroundService
{
    private readonly MariaDbService _mariaDbService;
    private readonly ILogger<BatteryHealthAnalysisService> _logger;
    private readonly TimeSpan _analysisInterval;
    private readonly AnalysisThresholds _thresholds;
    private readonly NotificationService _notifications;

    public BatteryHealthAnalysisService(
        IConfiguration configuration,
        MariaDbService mariaDbService,
        ILogger<BatteryHealthAnalysisService> logger,
        AnalysisThresholds thresholds,
        NotificationService notifications)
    {
        _mariaDbService = mariaDbService;
        _logger = logger;
        _thresholds = thresholds;
        _notifications = notifications;

        _analysisInterval = TimeSpan.FromMinutes(
            configuration.GetValue<int>("BatteryAnalysis:IntervalMinutes", 5));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Battery health analysis service started. Interval: {IntervalMinutes} minutes",
            _analysisInterval.TotalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var analysedAt = DateTime.Now;
                var oneHourCellHealth = await _mariaDbService.GetCellHealthSummariesAsync(
                    analysedAt.AddHours(-1),
                    analysedAt,
                    60,
                    stoppingToken);
                var oneDayCellHealth = await _mariaDbService.GetCellHealthSummariesAsync(
                    analysedAt.AddDays(-1),
                    analysedAt,
                    1440,
                    stoppingToken);
                var oneDayCellRanks = await _mariaDbService.GetCellRankSummariesAsync(
                    analysedAt.AddDays(-1),
                    analysedAt,
                    1440,
                    stoppingToken);
                var metrics = await BuildMetricsAsync(
                    analysedAt,
                    oneHourCellHealth,
                    oneDayCellHealth,
                    oneDayCellRanks,
                    stoppingToken);

                await _mariaDbService.SaveCellHealthAsync(
                    oneHourCellHealth.Concat(oneDayCellHealth),
                    stoppingToken);

                await _mariaDbService.SaveBatteryHealthMetricsAsync(
                    metrics,
                    stoppingToken);

                await _notifications.NotifyPeriodicAlertsAsync(metrics, stoppingToken);

                _logger.LogInformation(
                    "Saved {MetricCount} battery health metrics and {CellHealthCount} cell health rows",
                    metrics.Count,
                    oneHourCellHealth.Count + oneDayCellHealth.Count);

                await Task.Delay(_analysisInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Battery health analysis failed");
            }
        }
    }

    private async Task<List<BatteryHealthMetric>> BuildMetricsAsync(
        DateTime analysedAt,
        IReadOnlyList<CellHealthSummary> oneHourCellHealth,
        IReadOnlyList<CellHealthSummary> oneDayCellHealth,
        IReadOnlyList<CellRankSummary> oneDayCellRanks,
        CancellationToken cancellationToken)
    {
        var oneHour = await _mariaDbService.GetPackCellComparisonAsync(
            analysedAt.AddHours(-1),
            analysedAt,
            cancellationToken);

        var oneDay = await _mariaDbService.GetPackCellComparisonAsync(
            analysedAt.AddDays(-1),
            analysedAt,
            cancellationToken);
        var oneHourPackSamples = await _mariaDbService.GetPackHealthSamplesAsync(
            analysedAt.AddHours(-1),
            analysedAt,
            cancellationToken);
        var oneDayPackSamples = await _mariaDbService.GetPackHealthSamplesAsync(
            analysedAt.AddDays(-1),
            analysedAt,
            cancellationToken);

        var currentVoltages = oneDay.Cells
            .Where(c => c.ToVoltageV is not null)
            .Select(c => c.ToVoltageV!.Value)
            .ToList();

        var metrics = new List<BatteryHealthMetric>();

        if (currentVoltages.Count == 0)
            return metrics;

        var packAverageV = currentVoltages.Average();

        foreach (var cell24h in oneDay.Cells)
        {
            var cell1h = oneHour.Cells.FirstOrDefault(c => c.CellNo == cell24h.CellNo);

            if (cell24h.ToVoltageV is null)
                continue;

            var deviationMv = (cell24h.ToVoltageV.Value - packAverageV) * 1000m;
            var change1hMv = cell1h?.ChangeMv;
            var change24hMv = cell24h.ChangeMv;

            var healthScore = CalculateCellHealthScore(deviationMv);

            metrics.Add(Metric(
                analysedAt,
                0,
                "CELL",
                cell24h.CellNo,
                "cell_deviation_from_pack_average_mv",
                deviationMv,
                "mV",
                SeverityFromDeviation(deviationMv),
                $"Cell {cell24h.CellNo} is {deviationMv:N2} mV from pack average"));

            if (change1hMv is not null)
            {
                metrics.Add(Metric(
                    analysedAt,
                    60,
                    "CELL",
                    cell24h.CellNo,
                    "cell_voltage_change_1h_mv",
                    change1hMv.Value,
                    "mV",
                    SeverityFromChange(change1hMv.Value),
                    $"Cell {cell24h.CellNo} changed {change1hMv:N2} mV over 1 hour"));
            }

            if (change24hMv is not null)
            {
                metrics.Add(Metric(
                    analysedAt,
                    1440,
                    "CELL",
                    cell24h.CellNo,
                    "cell_voltage_change_24h_mv",
                    change24hMv.Value,
                    "mV",
                    SeverityFromChange(change24hMv.Value),
                    $"Cell {cell24h.CellNo} changed {change24hMv:N2} mV over 24 hours"));
            }

            metrics.Add(Metric(
                analysedAt,
                0,
                "CELL",
                cell24h.CellNo,
                "cell_health_score",
                healthScore,
                "score",
                SeverityFromHealthScore(healthScore),
                $"Cell {cell24h.CellNo} health score {healthScore:N0}/100"));
        }

        AddPackSummaryMetrics(metrics, analysedAt);
        AddCellHealthSummaryMetrics(metrics, analysedAt, oneHourCellHealth, 60);
        AddCellHealthSummaryMetrics(metrics, analysedAt, oneDayCellHealth, 1440);
        AddPackWindowMetrics(metrics, analysedAt, oneHourPackSamples, 60);
        AddPackWindowMetrics(metrics, analysedAt, oneDayPackSamples, 1440);
        AddPackContextMetrics(metrics, analysedAt, oneDayPackSamples);
        AddRestDeltaMetrics(metrics, analysedAt, oneDayPackSamples);
        AddHighSocDeltaMetrics(metrics, analysedAt, oneDayPackSamples);
        AddManualBalanceActionMetrics(metrics, analysedAt, oneDayPackSamples);
        AddLowCellContextMetrics(metrics, analysedAt, oneDayPackSamples);
        AddCellRankMetrics(metrics, analysedAt, oneDayCellRanks);
        AddSocDecileMetrics(metrics, analysedAt, oneDayPackSamples);
        AddTemperatureBandMetrics(metrics, analysedAt, oneDayPackSamples);
        AddSuddenDeltaStepMetrics(metrics, analysedAt, oneDayPackSamples);
        AddPackBalanceRiskMetrics(metrics, analysedAt, oneHourPackSamples, oneDayPackSamples, oneDayCellHealth);

        return metrics;
    }

    private void AddPackSummaryMetrics(
        List<BatteryHealthMetric> metrics,
        DateTime analysedAt)
    {
        var latestCellMetrics = metrics
            .Where(m => m.Scope == "CELL")
            .ToList();

        var worstDeviation = latestCellMetrics
            .Where(m => m.MetricName == "cell_deviation_from_pack_average_mv")
            .OrderByDescending(m => Math.Abs(m.MetricValue ?? 0))
            .FirstOrDefault();

        if (worstDeviation is not null)
        {
            metrics.Add(Metric(
                analysedAt,
                0,
                "PACK",
                null,
                "pack_worst_cell_deviation_mv",
                worstDeviation.MetricValue,
                "mV",
                worstDeviation.Severity,
                $"Worst cell deviation is cell {worstDeviation.CellNo}: {worstDeviation.MetricValue:N2} mV"));
        }

        var worst24hDrift = latestCellMetrics
            .Where(m => m.MetricName == "cell_voltage_change_24h_mv")
            .OrderByDescending(m => Math.Abs(m.MetricValue ?? 0))
            .FirstOrDefault();

        if (worst24hDrift is not null)
        {
            metrics.Add(Metric(
                analysedAt,
                1440,
                "PACK",
                null,
                "pack_worst_cell_24h_change_mv",
                worst24hDrift.MetricValue,
                "mV",
                worst24hDrift.Severity,
                $"Worst 24h cell movement is cell {worst24hDrift.CellNo}: {worst24hDrift.MetricValue:N2} mV"));
        }

        var worstHealthScore = latestCellMetrics
            .Where(m => m.MetricName == "cell_health_score")
            .OrderBy(m => m.MetricValue ?? 100)
            .FirstOrDefault();

        if (worstHealthScore is not null)
        {
            metrics.Add(Metric(
                analysedAt,
                0,
                "PACK",
                null,
                "pack_worst_cell_health_score",
                worstHealthScore.MetricValue,
                "score",
                worstHealthScore.Severity,
                $"Worst cell health score is cell {worstHealthScore.CellNo}: {worstHealthScore.MetricValue:N0}/100"));
        }
    }

    private void AddCellHealthSummaryMetrics(
        List<BatteryHealthMetric> metrics,
        DateTime analysedAt,
        IReadOnlyList<CellHealthSummary> summaries,
        int windowMinutes)
    {
        if (summaries.Count == 0)
            return;

        var worstDeviation = summaries
            .OrderByDescending(s => Math.Abs(s.MaxDeviationMv))
            .First();

        metrics.Add(Metric(
            analysedAt,
            windowMinutes,
            "PACK",
            null,
            $"pack_worst_cell_max_deviation_{windowMinutes}m_mv",
            worstDeviation.MaxDeviationMv,
            "mV",
            SeverityFromDeviation(worstDeviation.MaxDeviationMv),
            $"Worst max deviation over {windowMinutes} minutes is cell {worstDeviation.CellNo}: {worstDeviation.MaxDeviationMv:N2} mV"));

        AddPersistenceMetric(metrics, analysedAt, summaries, windowMinutes, true);
        AddPersistenceMetric(metrics, analysedAt, summaries, windowMinutes, false);
    }

    private void AddPersistenceMetric(
        List<BatteryHealthMetric> metrics,
        DateTime analysedAt,
        IReadOnlyList<CellHealthSummary> summaries,
        int windowMinutes,
        bool minCell)
    {
        var worst = summaries
            .Where(s => s.ReadingCount > 0)
            .Select(s => new
            {
                Summary = s,
                Percent = (minCell ? s.TimesMinCell : s.TimesMaxCell) * 100m / s.ReadingCount
            })
            .OrderByDescending(s => s.Percent)
            .FirstOrDefault();

        if (worst is null)
            return;

        var metricName = minCell
            ? $"pack_most_frequent_min_cell_{windowMinutes}m_percent"
            : $"pack_most_frequent_max_cell_{windowMinutes}m_percent";
        var severity = SeverityFromPersistence(worst.Percent);
        var lowHigh = minCell ? "minimum" : "maximum";

        metrics.Add(Metric(
            analysedAt,
            windowMinutes,
            "CELL",
            worst.Summary.CellNo,
            metricName,
            worst.Percent,
            "%",
            severity,
            $"Cell {worst.Summary.CellNo} was the {lowHigh} cell {worst.Percent:N1}% of the {windowMinutes} minute window"));
    }

    private void AddPackWindowMetrics(
        List<BatteryHealthMetric> metrics,
        DateTime analysedAt,
        IReadOnlyList<PackHealthSample> samples,
        int windowMinutes)
    {
        var deltaSamples = samples
            .Where(s => s.CellDeltaMv is not null)
            .ToList();

        if (deltaSamples.Count == 0)
            return;

        var latest = deltaSamples[^1];
        var first = deltaSamples[0];
        var deltas = deltaSamples.Select(s => s.CellDeltaMv!.Value).ToList();
        var avgDelta = deltas.Average();
        var maxDelta = deltas.Max();
        var p95Delta = Percentile(deltas, 0.95m);
        var deltaChange = latest.CellDeltaMv!.Value - first.CellDeltaMv!.Value;
        var elapsedHours = Math.Max((decimal)(latest.ReadAt - first.ReadAt).TotalHours, windowMinutes / 60m);
        var growthRate = deltaChange / elapsedHours;

        metrics.Add(Metric(
            analysedAt,
            windowMinutes,
            "PACK",
            null,
            $"pack_cell_delta_latest_{windowMinutes}m_mv",
            latest.CellDeltaMv,
            "mV",
            SeverityFromDelta(latest.CellDeltaMv!.Value),
            $"Latest cell delta in the {windowMinutes} minute window is {latest.CellDeltaMv:N2} mV"));

        metrics.Add(Metric(
            analysedAt,
            windowMinutes,
            "PACK",
            null,
            $"pack_cell_delta_avg_{windowMinutes}m_mv",
            avgDelta,
            "mV",
            SeverityFromDelta(avgDelta),
            $"Average cell delta over {windowMinutes} minutes is {avgDelta:N2} mV"));

        metrics.Add(Metric(
            analysedAt,
            windowMinutes,
            "PACK",
            null,
            $"pack_cell_delta_max_{windowMinutes}m_mv",
            maxDelta,
            "mV",
            SeverityFromDelta(maxDelta),
            $"Maximum cell delta over {windowMinutes} minutes is {maxDelta:N2} mV"));

        metrics.Add(Metric(
            analysedAt,
            windowMinutes,
            "PACK",
            null,
            $"pack_cell_delta_p95_{windowMinutes}m_mv",
            p95Delta,
            "mV",
            SeverityFromDelta(p95Delta),
            $"95th percentile cell delta over {windowMinutes} minutes is {p95Delta:N2} mV"));

        metrics.Add(Metric(
            analysedAt,
            windowMinutes,
            "PACK",
            null,
            $"pack_cell_delta_change_{windowMinutes}m_mv",
            deltaChange,
            "mV",
            SeverityFromDeltaGrowth(growthRate),
            $"Cell delta changed {deltaChange:N2} mV over {windowMinutes} minutes"));

        metrics.Add(Metric(
            analysedAt,
            windowMinutes,
            "PACK",
            null,
            $"pack_cell_delta_growth_rate_{windowMinutes}m_mv_per_hour",
            growthRate,
            "mV/h",
            SeverityFromDeltaGrowth(growthRate),
            $"Cell delta growth rate over {windowMinutes} minutes is {growthRate:N2} mV/h"));

        if (growthRate > 0m && latest.CellDeltaMv < 50m)
        {
            var hoursTo50Mv = (50m - latest.CellDeltaMv.Value) / growthRate;

            metrics.Add(Metric(
                analysedAt,
                windowMinutes,
                "PACK",
                null,
                $"pack_predicted_hours_to_50mv_delta_{windowMinutes}m",
                hoursTo50Mv,
                "hours",
                SeverityFromPredictedHours(hoursTo50Mv),
                $"At the current {windowMinutes} minute growth rate, 50 mV delta is estimated in {hoursTo50Mv:N1} hours"));
        }
    }

    private void AddPackContextMetrics(
        List<BatteryHealthMetric> metrics,
        DateTime analysedAt,
        IReadOnlyList<PackHealthSample> samples)
    {
        AddDeltaBandMetric(metrics, analysedAt, samples, "soc_low_lt20", s => s.SocPercent is < 20m);
        AddDeltaBandMetric(metrics, analysedAt, samples, "soc_mid_20_80", s => s.SocPercent is >= 20m and < 80m);
        AddDeltaBandMetric(metrics, analysedAt, samples, "soc_high_ge80", s => s.SocPercent is >= 80m);
        AddDeltaBandMetric(metrics, analysedAt, samples, "soc_top_ge90", s => s.SocPercent is >= 90m);
        AddDeltaBandMetric(metrics, analysedAt, samples, "rest_abs_current_le1a", s => s.PackCurrentA is not null && Math.Abs(s.PackCurrentA.Value) <= 1m);
        AddDeltaBandMetric(metrics, analysedAt, samples, "light_current_abs_1_10a", s => s.PackCurrentA is not null && Math.Abs(s.PackCurrentA.Value) > 1m && Math.Abs(s.PackCurrentA.Value) <= 10m);
        AddDeltaBandMetric(metrics, analysedAt, samples, "load_abs_current_gt10a", s => s.PackCurrentA is not null && Math.Abs(s.PackCurrentA.Value) > 10m);
        AddDeltaBandMetric(metrics, analysedAt, samples, "positive_current", s => s.PackCurrentA is > 1m);
        AddDeltaBandMetric(metrics, analysedAt, samples, "negative_current", s => s.PackCurrentA is < -1m);
    }

    private void AddRestDeltaMetrics(
        List<BatteryHealthMetric> metrics,
        DateTime analysedAt,
        IReadOnlyList<PackHealthSample> samples)
    {
        var restSamples = samples
            .Where(s => s.PackCurrentA is not null && Math.Abs(s.PackCurrentA.Value) <= 1m && s.CellDeltaMv is not null)
            .ToList();

        if (restSamples.Count == 0)
            return;

        var latestRestRun = LatestContinuousRun(restSamples, TimeSpan.FromMinutes(45));

        if (latestRestRun.Count > 0)
        {
            var durationMinutes = (decimal)(latestRestRun[^1].ReadAt - latestRestRun[0].ReadAt).TotalMinutes;

            metrics.Add(Metric(
                analysedAt,
                1440,
                "PACK",
                null,
                "pack_rest_delta_latest_mv",
                latestRestRun[^1].CellDeltaMv,
                "mV",
                SeverityFromDelta(latestRestRun[^1].CellDeltaMv!.Value),
                $"Latest rested cell delta is {latestRestRun[^1].CellDeltaMv:N2} mV after {durationMinutes:N0} minutes near zero current"));

            if (durationMinutes >= 10m)
            {
                var restChange = latestRestRun[^1].CellDeltaMv!.Value - latestRestRun[0].CellDeltaMv!.Value;

                metrics.Add(Metric(
                    analysedAt,
                    1440,
                    "PACK",
                    null,
                    "pack_rest_delta_change_mv",
                    restChange,
                    "mV",
                    SeverityFromDeltaGrowth(restChange),
                    $"Cell delta changed {restChange:N2} mV during the latest rested period"));
            }
        }

        var avgRestDelta = restSamples.Average(s => s.CellDeltaMv!.Value);

        metrics.Add(Metric(
            analysedAt,
            1440,
            "PACK",
            null,
            "pack_rest_delta_avg_24h_mv",
            avgRestDelta,
            "mV",
            SeverityFromDelta(avgRestDelta),
            $"Average rested cell delta over 24h is {avgRestDelta:N2} mV"));
    }

    // Tracks delta behaviour at high SOC — the most sensitive window for detecting cell divergence.
    // At high SOC, small capacity differences between cells produce larger voltage spreads.
    private void AddHighSocDeltaMetrics(
        List<BatteryHealthMetric> metrics,
        DateTime analysedAt,
        IReadOnlyList<PackHealthSample> samples)
    {
        var highSocSamples = samples
            .Where(s => s.SocPercent is >= 90m && s.CellDeltaMv is not null)
            .ToList();

        if (highSocSamples.Count == 0)
            return;

        var avgDelta = highSocSamples.Average(s => s.CellDeltaMv!.Value);
        var maxDelta = highSocSamples.Max(s => s.CellDeltaMv!.Value);

        metrics.Add(Metric(
            analysedAt,
            1440,
            "PACK",
            null,
            "pack_high_soc_avg_delta_mv",
            avgDelta,
            "mV",
            SeverityFromDelta(avgDelta),
            $"Average cell delta at SOC ≥90% was {avgDelta:N2} mV across {highSocSamples.Count} snapshots"));

        metrics.Add(Metric(
            analysedAt,
            1440,
            "PACK",
            null,
            "pack_high_soc_max_delta_mv",
            maxDelta,
            "mV",
            SeverityFromDelta(maxDelta),
            $"Peak cell delta at SOC ≥90% was {maxDelta:N2} mV"));

        var chargingSamples = highSocSamples
            .Where(s => s.PackCurrentA is > 1m)
            .ToList();

        if (chargingSamples.Count >= 2)
        {
            var first = chargingSamples[0];
            var latest = chargingSamples[^1];
            var change = latest.CellDeltaMv!.Value - first.CellDeltaMv!.Value;
            var elapsedHours = Math.Max((decimal)(latest.ReadAt - first.ReadAt).TotalHours, 0.01m);
            var rate = change / elapsedHours;

            metrics.Add(Metric(
                analysedAt,
                1440,
                "PACK",
                null,
                "pack_high_soc_charge_delta_rate_mv_per_hour",
                rate,
                "mV/h",
                rate > 0m ? SeverityFromDeltaGrowth(rate) : "OK",
                $"Cell delta changed at {rate:N2} mV/h during high-SOC (≥90%) charging"));
        }
    }

    // Emits an explicit action recommendation based on rested cell delta.
    // Tesla battery does not auto-balance during normal operation. The only way to equalise
    // cells is to charge to 100% SOC and open the pack contactors so the onboard BMS can equalise.
    private void AddManualBalanceActionMetrics(
        List<BatteryHealthMetric> metrics,
        DateTime analysedAt,
        IReadOnlyList<PackHealthSample> samples)
    {
        var restSamples = samples
            .Where(s => s.PackCurrentA is not null && Math.Abs(s.PackCurrentA.Value) <= 1m && s.CellDeltaMv is not null)
            .ToList();

        if (restSamples.Count == 0)
            return;

        var avgRestDelta = restSamples.Average(s => s.CellDeltaMv!.Value);
        var maxRestDelta = restSamples.Max(s => s.CellDeltaMv!.Value);

        var (severity, recommendation) = avgRestDelta >= _thresholds.ManualBalanceAlertMv
            ? ("ALERT", "Balance immediately: charge to 100% SOC and open pack contactors")
            : avgRestDelta >= _thresholds.ManualBalanceWarnMv
            ? ("WARN",  "Balance soon: charge to 100% SOC and open pack contactors")
            : avgRestDelta >= _thresholds.ManualBalanceInfoMv
            ? ("INFO",  "Monitor drift: next full charge will confirm whether action is needed")
            : ("OK",    "Pack is well balanced — no action needed");

        metrics.Add(Metric(
            analysedAt,
            1440,
            "PACK",
            null,
            "pack_manual_balance_action",
            avgRestDelta,
            "mV",
            severity,
            $"{recommendation}. Avg rested delta {avgRestDelta:N2} mV, peak {maxRestDelta:N2} mV over 24h"));
    }

    private void AddLowCellContextMetrics(
        List<BatteryHealthMetric> metrics,
        DateTime analysedAt,
        IReadOnlyList<PackHealthSample> samples)
    {
        AddMostFrequentMinCellByContext(metrics, analysedAt, samples, "rest_abs_current_le1a", s => s.PackCurrentA is not null && Math.Abs(s.PackCurrentA.Value) <= 1m);
        AddMostFrequentMinCellByContext(metrics, analysedAt, samples, "load_abs_current_gt10a", s => s.PackCurrentA is not null && Math.Abs(s.PackCurrentA.Value) > 10m);
    }

    private void AddMostFrequentMinCellByContext(
        List<BatteryHealthMetric> metrics,
        DateTime analysedAt,
        IReadOnlyList<PackHealthSample> samples,
        string contextName,
        Func<PackHealthSample, bool> predicate)
    {
        var contextSamples = samples
            .Where(predicate)
            .Where(s => s.MinCellNo is not null)
            .ToList();

        if (contextSamples.Count == 0)
            return;

        var worst = contextSamples
            .GroupBy(s => s.MinCellNo!.Value)
            .Select(g => new { CellNo = g.Key, Count = g.Count(), Percent = g.Count() * 100m / contextSamples.Count })
            .OrderByDescending(g => g.Percent)
            .First();

        metrics.Add(Metric(
            analysedAt,
            1440,
            "CELL",
            worst.CellNo,
            $"cell_min_persistence_24h_{contextName}_percent",
            worst.Percent,
            "%",
            SeverityFromPersistence(worst.Percent),
            $"Cell {worst.CellNo} was the minimum cell {worst.Percent:N1}% of {contextName} snapshots"));
    }

    private void AddCellRankMetrics(
        List<BatteryHealthMetric> metrics,
        DateTime analysedAt,
        IReadOnlyList<CellRankSummary> summaries)
    {
        if (summaries.Count == 0)
            return;

        var worstLow = summaries
            .Where(s => s.ReadingCount > 0)
            .Select(s => new { Summary = s, Percent = s.LowestFiveCount * 100m / s.ReadingCount })
            .OrderByDescending(s => s.Percent)
            .FirstOrDefault();

        if (worstLow is not null)
        {
            metrics.Add(Metric(
                analysedAt,
                1440,
                "CELL",
                worstLow.Summary.CellNo,
                "cell_lowest5_persistence_24h_percent",
                worstLow.Percent,
                "%",
                SeverityFromPersistence(worstLow.Percent),
                $"Cell {worstLow.Summary.CellNo} was in the lowest five cells {worstLow.Percent:N1}% of the 24h window"));
        }

        var worstHigh = summaries
            .Where(s => s.ReadingCount > 0)
            .Select(s => new { Summary = s, Percent = s.HighestFiveCount * 100m / s.ReadingCount })
            .OrderByDescending(s => s.Percent)
            .FirstOrDefault();

        if (worstHigh is not null)
        {
            metrics.Add(Metric(
                analysedAt,
                1440,
                "CELL",
                worstHigh.Summary.CellNo,
                "cell_highest5_persistence_24h_percent",
                worstHigh.Percent,
                "%",
                SeverityFromPersistence(worstHigh.Percent),
                $"Cell {worstHigh.Summary.CellNo} was in the highest five cells {worstHigh.Percent:N1}% of the 24h window"));
        }
    }

    private void AddSocDecileMetrics(
        List<BatteryHealthMetric> metrics,
        DateTime analysedAt,
        IReadOnlyList<PackHealthSample> samples)
    {
        for (var lower = 0; lower < 100; lower += 10)
        {
            var upper = lower + 10;
            AddDeltaBandMetric(metrics, analysedAt, samples, $"soc_{lower}_{upper}", s => s.SocPercent is not null && s.SocPercent >= lower && s.SocPercent < upper);
        }
    }

    private void AddTemperatureBandMetrics(
        List<BatteryHealthMetric> metrics,
        DateTime analysedAt,
        IReadOnlyList<PackHealthSample> samples)
    {
        AddDeltaBandMetric(metrics, analysedAt, samples, "temp_cold_lt10c", s => AverageTemperature(s) is < 10m);
        AddDeltaBandMetric(metrics, analysedAt, samples, "temp_mild_10_30c", s => AverageTemperature(s) is >= 10m and < 30m);
        AddDeltaBandMetric(metrics, analysedAt, samples, "temp_hot_ge30c", s => AverageTemperature(s) is >= 30m);
    }

    private void AddSuddenDeltaStepMetrics(
        List<BatteryHealthMetric> metrics,
        DateTime analysedAt,
        IReadOnlyList<PackHealthSample> samples)
    {
        var deltaSamples = samples
            .Where(s => s.CellDeltaMv is not null)
            .ToList();

        if (deltaSamples.Count < 2)
            return;

        var maxStep = 0m;
        PackHealthSample? stepSample = null;

        for (var i = 1; i < deltaSamples.Count; i++)
        {
            var step = Math.Abs(deltaSamples[i].CellDeltaMv!.Value - deltaSamples[i - 1].CellDeltaMv!.Value);

            if (step > maxStep)
            {
                maxStep = step;
                stepSample = deltaSamples[i];
            }
        }

        metrics.Add(Metric(
            analysedAt,
            1440,
            "PACK",
            null,
            "pack_max_single_snapshot_delta_step_24h_mv",
            maxStep,
            "mV",
            SeverityFromDeltaStep(maxStep),
            $"Largest single-snapshot delta step over 24h was {maxStep:N2} mV at {stepSample?.ReadAt:yyyy-MM-dd HH:mm:ss}"));
    }

    private void AddDeltaBandMetric(
        List<BatteryHealthMetric> metrics,
        DateTime analysedAt,
        IReadOnlyList<PackHealthSample> samples,
        string bandName,
        Func<PackHealthSample, bool> predicate)
    {
        var bandSamples = samples
            .Where(predicate)
            .Where(s => s.CellDeltaMv is not null)
            .ToList();

        if (bandSamples.Count == 0)
            return;

        var avgDelta = bandSamples.Average(s => s.CellDeltaMv!.Value);
        var maxDelta = bandSamples.Max(s => s.CellDeltaMv!.Value);

        metrics.Add(Metric(
            analysedAt,
            1440,
            "PACK",
            null,
            $"pack_cell_delta_avg_24h_{bandName}_mv",
            avgDelta,
            "mV",
            SeverityFromDelta(avgDelta),
            $"Average 24h cell delta for {bandName} is {avgDelta:N2} mV across {bandSamples.Count} snapshots"));

        metrics.Add(Metric(
            analysedAt,
            1440,
            "PACK",
            null,
            $"pack_cell_delta_max_24h_{bandName}_mv",
            maxDelta,
            "mV",
            SeverityFromDelta(maxDelta),
            $"Maximum 24h cell delta for {bandName} is {maxDelta:N2} mV across {bandSamples.Count} snapshots"));
    }

    private void AddPackBalanceRiskMetrics(
        List<BatteryHealthMetric> metrics,
        DateTime analysedAt,
        IReadOnlyList<PackHealthSample> oneHourSamples,
        IReadOnlyList<PackHealthSample> oneDaySamples,
        IReadOnlyList<CellHealthSummary> oneDayCellHealth)
    {
        var latestDelta = oneHourSamples.LastOrDefault(s => s.CellDeltaMv is not null)?.CellDeltaMv;
        var growthRate = CalculateGrowthRate(oneHourSamples);
        var worstMinPercent = MaxPersistencePercent(oneDayCellHealth, true);
        var highSocAvgDelta = oneDaySamples
            .Where(s => s.SocPercent is >= 90m && s.CellDeltaMv is not null)
            .Select(s => s.CellDeltaMv!.Value)
            .DefaultIfEmpty(0m)
            .Average();

        var riskScore = 0m;

        if (latestDelta is not null)
            riskScore += Math.Min(latestDelta.Value * 0.8m, 40m);

        if (growthRate is not null && growthRate.Value > 0m)
            riskScore += Math.Min(growthRate.Value * 3m, 25m);

        riskScore += Math.Min(worstMinPercent * 0.25m, 20m);
        riskScore += Math.Min(highSocAvgDelta * 0.3m, 15m);
        riskScore = Math.Clamp(riskScore, 0m, 100m);

        metrics.Add(Metric(
            analysedAt,
            60,
            "PACK",
            null,
            "pack_balance_risk_score",
            riskScore,
            "score",
            SeverityFromRiskScore(riskScore),
            $"Pack imbalance risk {riskScore:N0}/100. Latest delta {latestDelta:N2} mV, 1h growth {growthRate:N2} mV/h, worst min-cell persistence {worstMinPercent:N1}%, high-SOC avg delta {highSocAvgDelta:N2} mV. If elevated: charge to 100% and open contactors."));
    }

    private static BatteryHealthMetric Metric(
        DateTime analysedAt,
        int windowMinutes,
        string scope,
        int? cellNo,
        string metricName,
        decimal? metricValue,
        string? metricUnit,
        string severity,
        string? message)
    {
        return new BatteryHealthMetric(
            analysedAt,
            windowMinutes,
            scope,
            cellNo,
            metricName,
            metricValue,
            null,
            metricUnit,
            severity,
            message);
    }

    private static decimal? CalculateGrowthRate(IReadOnlyList<PackHealthSample> samples)
    {
        var deltaSamples = samples
            .Where(s => s.CellDeltaMv is not null)
            .ToList();

        if (deltaSamples.Count < 2)
            return null;

        var first = deltaSamples[0];
        var latest = deltaSamples[^1];
        var elapsedHours = (decimal)(latest.ReadAt - first.ReadAt).TotalHours;

        if (elapsedHours <= 0m)
            return null;

        return (latest.CellDeltaMv!.Value - first.CellDeltaMv!.Value) / elapsedHours;
    }

    private static IReadOnlyList<PackHealthSample> LatestContinuousRun(
        IReadOnlyList<PackHealthSample> samples,
        TimeSpan maxGap)
    {
        if (samples.Count == 0)
            return [];

        var run = new List<PackHealthSample> { samples[^1] };

        for (var i = samples.Count - 2; i >= 0; i--)
        {
            if (run[0].ReadAt - samples[i].ReadAt > maxGap)
                break;

            run.Insert(0, samples[i]);
        }

        return run;
    }

    private static decimal? AverageTemperature(PackHealthSample sample)
    {
        if (sample.TemperatureMinC is not null && sample.TemperatureMaxC is not null)
            return (sample.TemperatureMinC.Value + sample.TemperatureMaxC.Value) / 2m;

        return sample.TemperatureMinC ?? sample.TemperatureMaxC;
    }

    private static decimal MaxPersistencePercent(
        IReadOnlyList<CellHealthSummary> summaries,
        bool minCell)
    {
        return summaries
            .Where(s => s.ReadingCount > 0)
            .Select(s => (minCell ? s.TimesMinCell : s.TimesMaxCell) * 100m / s.ReadingCount)
            .DefaultIfEmpty(0m)
            .Max();
    }

    private static decimal Percentile(IReadOnlyList<decimal> values, decimal percentile)
    {
        var sorted = values.OrderBy(v => v).ToList();

        if (sorted.Count == 0)
            return 0m;

        var index = (int)Math.Ceiling((double)(percentile * sorted.Count)) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }

    private static decimal CalculateCellHealthScore(decimal deviationMv)
    {
        // Score is purely based on how much the cell deviates from the pack average.
        // Absolute voltage change over time is not a health signal — the whole pack
        // moves together when charging/discharging, which would unfairly penalise
        // every cell. 100 at 0 mV deviation, 0 at ≥40 mV deviation.
        return Math.Clamp(100m - Math.Abs(deviationMv) * 2.5m, 0m, 100m);
    }

    private string SeverityFromDeviation(decimal deviationMv)
    {
        var abs = Math.Abs(deviationMv);
        return abs >= _thresholds.CellDeviationAlertMv ? "ALERT" :
               abs >= _thresholds.CellDeviationWarnMv  ? "WARN"  :
               abs >= _thresholds.CellDeviationInfoMv  ? "INFO"  :
               "OK";
    }

    private string SeverityFromChange(decimal changeMv)
    {
        var abs = Math.Abs(changeMv);
        return abs >= _thresholds.CellDeviationAlertMv ? "ALERT" :
               abs >= _thresholds.CellDeviationWarnMv  ? "WARN"  :
               abs >= _thresholds.CellDeviationInfoMv  ? "INFO"  :
               "OK";
    }

    private string SeverityFromDelta(decimal deltaMv)
    {
        return deltaMv >= _thresholds.PackDeltaAlertMv ? "ALERT" :
               deltaMv >= _thresholds.PackDeltaWarnMv  ? "WARN"  :
               deltaMv >= _thresholds.PackDeltaInfoMv  ? "INFO"  :
               "OK";
    }

    private string SeverityFromDeltaGrowth(decimal growthRateMvPerHour)
    {
        return growthRateMvPerHour >= _thresholds.DeltaGrowthAlertMvPerHour ? "ALERT" :
               growthRateMvPerHour >= _thresholds.DeltaGrowthWarnMvPerHour  ? "WARN"  :
               growthRateMvPerHour >= _thresholds.DeltaGrowthInfoMvPerHour  ? "INFO"  :
               "OK";
    }

    private string SeverityFromDeltaStep(decimal stepMv)
    {
        return stepMv >= _thresholds.DeltaStepAlertMv ? "ALERT" :
               stepMv >= _thresholds.DeltaStepWarnMv  ? "WARN"  :
               stepMv >= _thresholds.DeltaStepInfoMv  ? "INFO"  :
               "OK";
    }

    private string SeverityFromPersistence(decimal percent)
    {
        return percent >= _thresholds.PersistenceAlertPercent ? "ALERT" :
               percent >= _thresholds.PersistenceWarnPercent  ? "WARN"  :
               percent >= _thresholds.PersistenceInfoPercent  ? "INFO"  :
               "OK";
    }

    private string SeverityFromPredictedHours(decimal hours)
    {
        return hours <= _thresholds.PredictedHoursAlert ? "ALERT" :
               hours <= _thresholds.PredictedHoursWarn  ? "WARN"  :
               hours <= _thresholds.PredictedHoursInfo  ? "INFO"  :
               "OK";
    }

    private string SeverityFromRiskScore(decimal score)
    {
        return score >= _thresholds.RiskScoreAlert ? "ALERT" :
               score >= _thresholds.RiskScoreWarn  ? "WARN"  :
               score >= _thresholds.RiskScoreInfo  ? "INFO"  :
               "OK";
    }

    private string SeverityFromHealthScore(decimal score)
    {
        // Derive thresholds from deviation config so health score and deviation stay in sync
        var alertScore = Math.Max(0m, 100m - _thresholds.CellDeviationAlertMv * 2.5m);
        var warnScore  = Math.Max(0m, 100m - _thresholds.CellDeviationWarnMv  * 2.5m);
        var infoScore  = Math.Max(0m, 100m - _thresholds.CellDeviationInfoMv  * 2.5m);

        return score <= alertScore ? "ALERT" :
               score <  warnScore  ? "WARN"  :
               score <  infoScore  ? "INFO"  :
               "OK";
    }
}
