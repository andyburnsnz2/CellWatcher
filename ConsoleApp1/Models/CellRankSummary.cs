namespace BatteryEMU.Models;

public sealed record CellRankSummary(
    DateTime AnalysedAt,
    int WindowMinutes,
    int CellNo,
    int ReadingCount,
    int LowestFiveCount,
    int HighestFiveCount,
    decimal AvgRank,
    decimal AvgReverseRank);
