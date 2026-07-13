namespace CellWatcher.Models;

// One recorded change — battery_cell_reading only stores a row when a cell's voltage or
// balancing state actually changed, so this is a sparse delta, not a per-snapshot dump.
public sealed record CellChangeEvent(
    DateTime ReadAt,
    int CellNo,
    decimal VoltageV,
    bool? BalancingActive);

// Everything needed to reconstruct the cell grid at any point within [Baseline.TargetTime, ...]
// without another round-trip: the full state as of the start of the period, plus every change
// event since. The Cells page's time-scrub slider fetches this once per selected period and then
// replays events in-memory for instant scrubbing.
public sealed record CellHistoryEvents(
    CellStateSnapshot Baseline,
    IReadOnlyList<CellChangeEvent> Events);
