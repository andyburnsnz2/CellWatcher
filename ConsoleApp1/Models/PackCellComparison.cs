namespace BatteryEMU.Models;

public sealed record CellVoltagePoint(
    int CellNo,
    decimal? FromVoltageV,
    decimal? ToVoltageV)
{
    public decimal? ChangeMv =>
        FromVoltageV is null || ToVoltageV is null
            ? null
            : (ToVoltageV.Value - FromVoltageV.Value) * 1000m;
}

public sealed record PackCellComparison(
    DateTime FromTime,
    DateTime ToTime,
    IReadOnlyList<CellVoltagePoint> Cells);