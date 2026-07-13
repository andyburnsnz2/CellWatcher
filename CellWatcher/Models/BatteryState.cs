using System.Collections.Concurrent;

namespace CellWatcher.Models;

public sealed class BatteryState
{
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<int, decimal> _cellVoltages = new();
    private readonly ConcurrentDictionary<int, bool> _cellBalancing = new();

    public decimal? SocPercent { get; private set; }
    public decimal? SocRealPercent { get; private set; }
    public decimal? StateOfHealthPercent { get; private set; }

    public decimal? PackVoltageV { get; private set; }
    public decimal? PackCurrentA { get; private set; }
    public decimal? PackPowerW { get; private set; }

    public decimal? TemperatureMinC { get; private set; }
    public decimal? TemperatureMaxC { get; private set; }

    public decimal? MaxDischargePowerW { get; private set; }
    public decimal? MaxChargePowerW { get; private set; }

    public decimal? RemainingCapacityWh { get; private set; }
    public decimal? TotalCapacityWh { get; private set; }

    public ulong? ChargedEnergyWh { get; private set; }
    public ulong? DischargedEnergyWh { get; private set; }

    public string? BmsStatus { get; private set; }
    public string? PauseStatus { get; private set; }
    public string? EmulatorStatus { get; private set; }
    public string? EventLevel { get; private set; }

    public decimal? CpuTempC { get; private set; }
    public ulong? EmulatorUptimeSeconds { get; private set; }

    // Set on every inbound MQTT message (regardless of topic/parse success) so the UI can
    // show a live "is MQTT actually flowing" indicator independent of whether any field changed.
    public DateTime? LastMqttMessageAt { get; private set; }

    public void RecordMqttMessageReceived()
    {
        lock (_lock)
        {
            LastMqttMessageAt = DateTime.Now;
        }
    }

    // Set only when the forwarding broker (Standalone mode's bridge to e.g. Home Assistant)
    // actually acknowledges a publish (QoS 1 PUBACK) — not just when a send was attempted —
    // so this reflects real delivery, not merely "we tried."
    public DateTime? LastMqttForwardAckAt { get; private set; }

    public void RecordMqttForwardAck()
    {
        lock (_lock)
        {
            LastMqttForwardAckAt = DateTime.Now;
        }
    }

    public void UpdatePackInfo(
        decimal? socPercent,
        decimal? socRealPercent,
        decimal? stateOfHealthPercent,
        decimal? packVoltageV,
        decimal? packCurrentA,
        decimal? packPowerW,
        decimal? temperatureMinC,
        decimal? temperatureMaxC,
        decimal? maxDischargePowerW,
        decimal? maxChargePowerW,
        decimal? remainingCapacityWh,
        decimal? totalCapacityWh,
        ulong? chargedEnergyWh,
        ulong? dischargedEnergyWh,
        string? bmsStatus,
        string? pauseStatus,
        string? emulatorStatus,
        string? eventLevel,
        decimal? cpuTempC,
        ulong? emulatorUptimeSeconds)
    {
        lock (_lock)
        {
            SocPercent = socPercent;
            SocRealPercent = socRealPercent;
            StateOfHealthPercent = stateOfHealthPercent;
            PackVoltageV = packVoltageV;
            PackCurrentA = packCurrentA;
            PackPowerW = packPowerW;
            TemperatureMinC = temperatureMinC;
            TemperatureMaxC = temperatureMaxC;
            MaxDischargePowerW = maxDischargePowerW;
            MaxChargePowerW = maxChargePowerW;
            RemainingCapacityWh = remainingCapacityWh;
            TotalCapacityWh = totalCapacityWh;
            ChargedEnergyWh = chargedEnergyWh;
            DischargedEnergyWh = dischargedEnergyWh;
            BmsStatus = bmsStatus;
            PauseStatus = pauseStatus;
            EmulatorStatus = emulatorStatus;
            EventLevel = eventLevel;
            CpuTempC = cpuTempC;
            EmulatorUptimeSeconds = emulatorUptimeSeconds;
        }
    }

    public void UpdateCellVoltage(int cellNumber, decimal voltage)
    {
        _cellVoltages[cellNumber] = voltage;
    }

    public void UpdateCellBalancing(int cellNumber, bool balancingActive)
    {
        _cellBalancing[cellNumber] = balancingActive;
    }

    public BatterySnapshot CreateSnapshot()
    {
        lock (_lock)
        {
            var snapshot = new BatterySnapshot
            {
                ReadAt = DateTime.Now,

                SocPercent = SocPercent,
                SocRealPercent = SocRealPercent,
                StateOfHealthPercent = StateOfHealthPercent,

                PackVoltageV = PackVoltageV,
                PackCurrentA = PackCurrentA,
                PackPowerW = PackPowerW,

                TemperatureMinC = TemperatureMinC,
                TemperatureMaxC = TemperatureMaxC,

                MaxDischargePowerW = MaxDischargePowerW,
                MaxChargePowerW = MaxChargePowerW,

                RemainingCapacityWh = RemainingCapacityWh,
                TotalCapacityWh = TotalCapacityWh,

                ChargedEnergyWh = ChargedEnergyWh,
                DischargedEnergyWh = DischargedEnergyWh,

                BmsStatus = BmsStatus,
                PauseStatus = PauseStatus,
                EmulatorStatus = EmulatorStatus,
                EventLevel = EventLevel,

                CpuTempC = CpuTempC,
                EmulatorUptimeSeconds = EmulatorUptimeSeconds,

                LastMqttMessageAt = LastMqttMessageAt,
                LastMqttForwardAckAt = LastMqttForwardAckAt
            };

            foreach (var cell in _cellVoltages.OrderBy(c => c.Key))
                snapshot.CellVoltages[cell.Key] = cell.Value;

            foreach (var cell in _cellBalancing.OrderBy(c => c.Key))
                snapshot.CellBalancing[cell.Key] = cell.Value;

            if (snapshot.CellVoltages.Count > 0)
            {
                var min = snapshot.CellVoltages.MinBy(c => c.Value);
                var max = snapshot.CellVoltages.MaxBy(c => c.Value);

                snapshot.MinCellNo = min.Key;
                snapshot.MaxCellNo = max.Key;
                snapshot.MinCellV = min.Value;
                snapshot.MaxCellV = max.Value;
                snapshot.CellDeltaMv = (max.Value - min.Value) * 1000m;
            }

            return snapshot;
        }
    }
}
