namespace BatteryEMU.Models;

public sealed record BatteryHealthMetric(
    DateTime AnalysedAt,
    int WindowMinutes,
    string Scope,
    int? CellNo,
    string MetricName,
    decimal? MetricValue,
    string? MetricValueText,
    string? MetricUnit,
    string Severity,
    string? Message);