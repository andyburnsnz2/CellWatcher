namespace CellWatcher.Models;

// One battery implementation imported from the Battery-Emulator source repository's
// Software/src/battery directory — see BatteryCanMappingImportService.
public sealed record BatteryType(
    int Id,
    string Name,
    string SourceFile,
    string SourceUrl,
    DateTime ImportedAt,
    uint MappingCount);

// One CAN frame mapping for a battery type — see BatteryType. All batteries in the source repo
// communicate over CAN (not Modbus, despite how this feature originated in conversation).
public sealed record BatteryCanMapping(
    long Id,
    int BatteryTypeId,
    uint CanId,
    string? FrameName);

// One assignment statement extracted from within a CAN mapping's case block — see
// alter_battery_can_mapping_add_signals.sql / alter_battery_can_signal_add_decode_fields.sql for
// what these actually mean. ByteIndices/BitMask/BitShift/Scale/OffsetValue are a best-effort
// structured parse of ExpressionText — not every expression fits the pattern they're extracted
// from (rx_frame.data.u8[N] combined via <<, |, &, >>, optionally scaled/offset), in which case
// they're null and ExpressionText remains the only record.
// MaskBeforeShift: true if the source applies "& mask" before ">> shift", false if after, null
// if only one of mask/shift (or neither) is present. Not cosmetic — "(x & mask) >> shift" and
// "(x >> shift) & mask" are different operations, and real source code uses both.
public sealed record BatteryCanSignal(
    long Id,
    long MappingId,
    string FieldName,
    string? ExpressionText,
    string? ByteIndices,
    string? BitMask,
    int? BitShift,
    bool? MaskBeforeShift,
    double? Scale,
    double? OffsetValue);

// Freshly-parsed signal, not yet persisted — see BatteryCanMappingImportService.ParseCanMappings.
public sealed record ParsedCanSignal(
    string FieldName,
    string? ExpressionText,
    string? ByteIndices,
    string? BitMask,
    int? BitShift,
    bool? MaskBeforeShift,
    double? Scale,
    double? OffsetValue);

// Freshly-parsed mapping, not yet persisted — see BatteryCanMappingImportService.ParseCanMappings.
public sealed record ParsedCanMapping(uint CanId, string? FrameName, IReadOnlyList<ParsedCanSignal> Signals);
