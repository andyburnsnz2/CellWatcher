namespace BatteryEMU.Models;

public sealed class BatterySnapshot
{
    public DateTime ReadAt { get; set; } = DateTime.Now;

    public decimal? SocPercent { get; set; }
    public decimal? SocRealPercent { get; set; }
    public decimal? StateOfHealthPercent { get; set; }

    public decimal? PackVoltageV { get; set; }
    public decimal? PackCurrentA { get; set; }
    public decimal? PackPowerW { get; set; }

    public decimal? TemperatureMinC { get; set; }
    public decimal? TemperatureMaxC { get; set; }

    public decimal? MaxDischargePowerW { get; set; }
    public decimal? MaxChargePowerW { get; set; }

    public decimal? RemainingCapacityWh { get; set; }
    public decimal? TotalCapacityWh { get; set; }

    public ulong? ChargedEnergyWh { get; set; }
    public ulong? DischargedEnergyWh { get; set; }

    public string? BmsStatus { get; set; }
    public string? PauseStatus { get; set; }
    public string? EmulatorStatus { get; set; }
    public string? EventLevel { get; set; }

    public decimal? CpuTempC { get; set; }
    public ulong? EmulatorUptimeSeconds { get; set; }

    public decimal? MinCellV { get; set; }
    public decimal? MaxCellV { get; set; }
    public decimal? CellDeltaMv { get; set; }

    public int? MinCellNo { get; set; }
    public int? MaxCellNo { get; set; }

    public Dictionary<int, decimal> CellVoltages { get; set; } = new();
    public Dictionary<int, bool> CellBalancing { get; set; } = new();

    public DateTime? LastMqttMessageAt { get; set; }
    public DateTime? LastMqttForwardAckAt { get; set; }
}
