namespace CellWatcher.Models;

public sealed record PackHealthSample(
    DateTime ReadAt,
    decimal? SocPercent,
    decimal? PackVoltageV,
    decimal? PackCurrentA,
    decimal? PackPowerW,
    decimal? TemperatureMinC,
    decimal? TemperatureMaxC,
    decimal? MinCellV,
    decimal? MaxCellV,
    decimal? CellDeltaMv,
    int? MinCellNo,
    int? MaxCellNo);
