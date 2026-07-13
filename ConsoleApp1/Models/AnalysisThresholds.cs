namespace BatteryEMU.Models;

public sealed record AnalysisThresholds
{
    // Single-cell deviation from pack average (mV), also used for 1h/24h voltage change
    public decimal CellDeviationInfoMv { get; init; } = 15m;
    public decimal CellDeviationWarnMv { get; init; } = 25m;
    public decimal CellDeviationAlertMv { get; init; } = 40m;

    // Pack-level max-minus-min cell delta (mV)
    public decimal PackDeltaInfoMv { get; init; } = 30m;
    public decimal PackDeltaWarnMv { get; init; } = 50m;
    public decimal PackDeltaAlertMv { get; init; } = 80m;

    // Delta growth rate (mV/hour)
    public decimal DeltaGrowthInfoMvPerHour { get; init; } = 5m;
    public decimal DeltaGrowthWarnMvPerHour { get; init; } = 10m;
    public decimal DeltaGrowthAlertMvPerHour { get; init; } = 20m;

    // Single-snapshot delta step (mV)
    public decimal DeltaStepInfoMv { get; init; } = 5m;
    public decimal DeltaStepWarnMv { get; init; } = 10m;
    public decimal DeltaStepAlertMv { get; init; } = 20m;

    // Persistence: % of time a cell is the min or max cell
    public decimal PersistenceInfoPercent { get; init; } = 25m;
    public decimal PersistenceWarnPercent { get; init; } = 50m;
    public decimal PersistenceAlertPercent { get; init; } = 75m;

    // Pack balance risk score (0–100)
    public decimal RiskScoreInfo { get; init; } = 25m;
    public decimal RiskScoreWarn { get; init; } = 50m;
    public decimal RiskScoreAlert { get; init; } = 75m;

    // Predicted hours until pack reaches 50 mV delta
    public decimal PredictedHoursInfo { get; init; } = 24m;
    public decimal PredictedHoursWarn { get; init; } = 8m;
    public decimal PredictedHoursAlert { get; init; } = 2m;

    // Manual balance action: rested delta thresholds (mV)
    // Tesla does not auto-balance; action = charge to 100% SOC and open contactors
    public decimal ManualBalanceInfoMv { get; init; } = 20m;
    public decimal ManualBalanceWarnMv { get; init; } = 40m;
    public decimal ManualBalanceAlertMv { get; init; } = 60m;
}
