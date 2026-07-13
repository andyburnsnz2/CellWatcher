namespace BatteryEMU.Models;

// Reconstructed per-cell voltage/balancing state as of a given point in time — used by the Cells
// page's time-scrub slider. battery_cell_reading only stores a row when a cell's voltage or
// balancing state actually changed (see MariaDbService.ShouldInsertCellReading), so "as of" a
// given time means the latest reading at or before it, per cell, not a single row lookup.
public sealed record CellStateSnapshot(
    DateTime TargetTime,
    IReadOnlyDictionary<int, decimal> CellVoltages,
    IReadOnlyDictionary<int, bool> CellBalancing);
