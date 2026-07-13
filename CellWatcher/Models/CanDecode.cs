namespace CellWatcher.Models;

// Precompiled from a battery_can_signal row (see BatteryDecodeLookupService) — string parsing
// (byte_indices split, bit_mask hex parse) happens once at load time, not per incoming frame,
// so the hot per-frame decode path is just array indexing and arithmetic.
//
// MaskBeforeShift matters and is not optional: "(x & mask) >> shift" and "(x >> shift) & mask"
// are different operations, and real source code uses both (Tesla's BMS_alertMatrix is almost
// entirely shift-then-mask; HVP_alertMatrix1 is almost entirely mask-then-shift). Applying the
// wrong order for a single-bit flag mask (e.g. 0x01) after a nonzero shift always yields 0
// regardless of the real bit value — this is not a rare edge case, it silently breaks the
// majority of single-bit alert/status flags if assumed constant.
public sealed record CompiledCanSignal(
    string FieldName,
    int[] ByteIndices, // in the order to combine them, high byte first (empty = not byte-decodable)
    uint? BitMask,
    int? BitShift,
    bool MaskBeforeShift,
    double Scale,
    double OffsetValue);

public sealed record DecodedSignalValue(string FieldName, double Value);

// One frame as shown in the Canbus tab's live view — carries both raw and (if identified)
// decoded data, so the frontend's Raw/Decoded toggle needs no extra round trip. Held in
// CanLiveViewState's in-memory ring buffer; never persisted (can_frame already has the raw
// bytes — decoding can always be redone from that plus the mapping tables later).
public sealed record LiveCanFrameEntry(
    DateTime ReceivedAt,
    uint CanId,
    bool IsIdentified,
    string? FrameName,
    byte Dlc,
    byte[] Data,
    IReadOnlyList<DecodedSignalValue> DecodedSignals);
