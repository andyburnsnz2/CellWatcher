namespace BatteryEMU.Models;

// One bucket of the tiered long-term trend digest fed to deep AI analyses (see
// InsightPrompts.BuildDeepPromptAsync / MariaDbService.GetHealthRollupAsync). Recent history is
// bucketed by day, older history by week, and the oldest by month — so the line count stays
// roughly flat (a few hundred lines) regardless of whether the battery has months or decades of
// history, instead of growing unboundedly if every bucket stayed daily forever.
public sealed record HealthRollupPoint(
    DateTime PeriodStart,
    string Granularity, // "day" | "week" | "month"
    decimal? AvgDeltaMv,
    decimal? MinDeltaMv,
    decimal? MaxDeltaMv,
    decimal? AvgSocPercent,
    decimal? MinSocPercent,
    decimal? MaxSocPercent,
    decimal? TempMinC,
    decimal? TempMaxC);
