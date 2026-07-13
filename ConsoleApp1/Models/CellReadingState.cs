namespace BatteryEMU.Models;

public sealed record CellReadingState(decimal VoltageV, bool BalancingActive);
