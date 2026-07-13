namespace BatteryEMU.Models;

public sealed record AiSpendSummary(
    string Engine,
    int AnalysisCount,
    long TotalInputTokens,
    long TotalOutputTokens,
    decimal TotalCostUsd);
