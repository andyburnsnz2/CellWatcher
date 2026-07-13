namespace BatteryEMU.Models;

// One row per balancing run — see create_battery_control_history.sql and
// BatteryControlService, which writes these as a run starts, reaches target, and stops.
// Purely a log for later analysis; doesn't drive any live behavior.
public sealed record BatteryControlHistoryEntry(
    int Id,
    DateTime StartedAt,
    string ActivationMode,
    string Mode,
    decimal TargetSocPercent,
    DateTime? TargetReachedAt,
    DateTime? StoppedAt,
    string? StopReason);
