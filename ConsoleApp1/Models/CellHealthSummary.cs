namespace BatteryEMU.Models;

public sealed record CellHealthSummary(
    DateTime AnalysedAt,
    int WindowMinutes,
    int CellNo,
    int ReadingCount,
    int TimesMinCell,
    int TimesMaxCell,
    decimal AvgVoltageV,
    decimal AvgDeviationMv,
    decimal MaxDeviationMv,
    string Severity,
    string Message);
