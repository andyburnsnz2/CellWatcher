using System.Text.Json;
using BatteryEMU.Models;
using Microsoft.Extensions.Logging;

namespace BatteryEMU.Services;

// Shared BE/info + BE/spec_data parsing, used by both MqttService (client mode, connects out to
// an external broker) and MqttBrokerService (standalone mode, embedded broker) so the message
// format is only parsed in one place regardless of which mode is active.
public sealed class MqttMessageProcessor
{
    private readonly ILogger _logger;
    private readonly BatteryState _batteryState;
    private readonly NotificationService _notifications;

    public MqttMessageProcessor(ILogger logger, BatteryState batteryState, NotificationService notifications)
    {
        _logger = logger;
        _batteryState = batteryState;
        _notifications = notifications;
    }

    public void ProcessMessage(string topic, string payload)
    {
        _batteryState.RecordMqttMessageReceived();

        try
        {
            if (topic.Equals("BE/info", StringComparison.OrdinalIgnoreCase))
            {
                ProcessInfoPayload(payload);
                return;
            }

            if (topic.Equals("BE/spec_data", StringComparison.OrdinalIgnoreCase))
            {
                ProcessSpecDataPayload(payload);
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process MQTT message {Topic}: {Payload}", topic, payload);
        }
    }

    private void ProcessInfoPayload(string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;

        _batteryState.UpdatePackInfo(
            socPercent: GetDecimal(root, "SOC"),
            socRealPercent: GetDecimal(root, "SOC_real"),
            stateOfHealthPercent: GetDecimal(root, "state_of_health"),

            packVoltageV: GetDecimal(root, "battery_voltage"),
            packCurrentA: GetDecimal(root, "battery_current"),
            packPowerW: GetDecimal(root, "stat_batt_power"),

            temperatureMinC: GetDecimal(root, "temperature_min"),
            temperatureMaxC: GetDecimal(root, "temperature_max"),

            maxDischargePowerW: GetDecimal(root, "max_discharge_power"),
            maxChargePowerW: GetDecimal(root, "max_charge_power"),

            remainingCapacityWh: GetDecimal(root, "remaining_capacity"),
            totalCapacityWh: GetDecimal(root, "total_capacity"),

            chargedEnergyWh: GetUlong(root, "charged_energy"),
            dischargedEnergyWh: GetUlong(root, "discharged_energy"),

            bmsStatus: GetString(root, "bms_status"),
            pauseStatus: GetString(root, "pause_status"),
            emulatorStatus: GetString(root, "emulator_status"),
            eventLevel: GetString(root, "event_level"),

            cpuTempC: GetDecimal(root, "cpu_temp"),
            emulatorUptimeSeconds: GetUlong(root, "emulator_uptime")
        );
    }

    private void ProcessSpecDataPayload(string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;

        if (!root.TryGetProperty("cell_voltages", out var cells))
            return;

        var balancingCells = GetCellBalancingArray(root);
        var cellNo = 1;

        foreach (var cell in cells.EnumerateArray())
        {
            _batteryState.UpdateCellVoltage(cellNo, cell.GetDecimal());

            if (balancingCells is not null && balancingCells.Value.GetArrayLength() >= cellNo)
            {
                var balancingCell = balancingCells.Value[cellNo - 1];
                _batteryState.UpdateCellBalancing(cellNo, GetBoolean(balancingCell));
            }

            cellNo++;
        }

        var snapshot = _batteryState.CreateSnapshot();

        _logger.LogInformation(
            "Cells updated. Count={Count} Delta={Delta}mV Min={MinCell} Max={MaxCell}",
            snapshot.CellVoltages.Count,
            snapshot.CellDeltaMv,
            snapshot.MinCellNo,
            snapshot.MaxCellNo);

        // Checked on every message rather than on a timer — this is as close to "instant" as
        // this system's data ever gets, since nothing arrives faster than the emulator's own
        // publish interval anyway. Fire-and-forget: notification failures shouldn't block
        // message processing, and NotifyRealTimeAlarmAsync already logs its own failures.
        _ = _notifications.NotifyRealTimeAlarmAsync(snapshot, CancellationToken.None);
    }

    private static decimal? GetDecimal(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDecimal()
            : null;
    }

    private static ulong? GetUlong(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetUInt64()
            : null;
    }

    private static string? GetString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static JsonElement? GetCellBalancingArray(JsonElement root)
    {
        string[] propertyNames =
        [
            "cell_balancing",
            "cell_balancing_active",
            "cell_balancing_status",
            "balancing_active"
        ];

        foreach (var propertyName in propertyNames)
        {
            if (root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Array)
                return value;
        }

        return null;
    }

    private static bool GetBoolean(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => value.GetDecimal() != 0m,
            JsonValueKind.String => bool.TryParse(value.GetString(), out var parsed) && parsed,
            _ => false
        };
    }
}
