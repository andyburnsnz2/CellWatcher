using System.Globalization;
using CellWatcher.Data;
using CellWatcher.Models;
using Microsoft.Extensions.Logging;

namespace CellWatcher.Services;

// High-performance CAN ID -> decode rules lookup for the currently selected battery type (see
// battery_selection). Reload (triggered by ApiEndpoints whenever the selection or an import
// changes) does all the string parsing (byte_indices split, bit_mask hex parse) once; the actual
// per-frame decode path used by CanFrameUdpListenerService is just a dictionary lookup plus
// simple arithmetic — no string work on the hot path, which is what makes this fast enough to
// keep up with a live CAN bus.
//
// The lookup table itself is swapped in as a single atomic reference assignment on reload, so
// concurrent Decode() calls from the UDP receive loop never see a half-built table and never
// need to take a lock on the hot path.
public sealed class BatteryDecodeLookupService
{
    private sealed record CompiledFrame(string? FrameName, IReadOnlyList<CompiledCanSignal> Signals);

    private readonly ILogger<BatteryDecodeLookupService> _logger;
    private volatile Dictionary<uint, CompiledFrame> _lookup = new();

    public BatteryDecodeLookupService(ILogger<BatteryDecodeLookupService> logger)
    {
        _logger = logger;
    }

    public async Task ReloadAsync(MariaDbService db, CancellationToken ct)
    {
        var selectedId = await db.GetSelectedBatteryTypeIdAsync(ct);
        if (selectedId is null)
        {
            _lookup = new Dictionary<uint, CompiledFrame>();
            _logger.LogInformation("CAN decode lookup cleared — no battery selected");
            return;
        }

        var mappings = await db.GetBatteryCanMappingsAsync(selectedId.Value, ct);
        var table = new Dictionary<uint, CompiledFrame>(mappings.Count);
        var totalSignals = 0;

        foreach (var (mapping, signals) in mappings)
        {
            var compiled = new List<CompiledCanSignal>(signals.Count);
            foreach (var s in signals)
            {
                var byteIndices = ParseByteIndices(s.ByteIndices);
                var bitMask = ParseHexMask(s.BitMask);
                // Defaulting to true (mask-before-shift) only matters when both mask and shift
                // are actually present but the order somehow wasn't recorded (shouldn't happen
                // via the current importer, but this is read from data, not guaranteed fresh).
                compiled.Add(new CompiledCanSignal(
                    s.FieldName, byteIndices, bitMask, s.BitShift, s.MaskBeforeShift ?? true,
                    s.Scale ?? 1.0, s.OffsetValue ?? 0.0));
                totalSignals++;
            }
            table[mapping.CanId] = new CompiledFrame(mapping.FrameName, compiled);
        }

        _lookup = table; // atomic swap — readers never see a partially-built table
        _logger.LogInformation(
            "CAN decode lookup reloaded: battery type {BatteryTypeId}, {FrameCount} CAN IDs, {SignalCount} signals",
            selectedId, table.Count, totalSignals);
    }

    // O(1) lookup + simple arithmetic only — no allocation-heavy or string work here, called once
    // per incoming CAN frame.
    public (bool IsIdentified, string? FrameName, List<DecodedSignalValue> Decoded) Decode(uint canId, byte[] data, byte dlc)
    {
        var table = _lookup; // single read of the volatile field — consistent snapshot for this call
        if (!table.TryGetValue(canId, out var frame))
            return (false, null, []);

        var decoded = new List<DecodedSignalValue>(frame.Signals.Count);
        foreach (var signal in frame.Signals)
        {
            if (signal.ByteIndices.Length == 0) continue; // not byte-decodable (e.g. references another variable)
            if (signal.ByteIndices.Any(i => i >= dlc)) continue; // frame too short for this signal

            ulong raw = 0;
            foreach (var byteIndex in signal.ByteIndices)
                raw = (raw << 8) | data[byteIndex];

            // Order matters — "(x & mask) >> shift" and "(x >> shift) & mask" are different
            // operations, and real source uses both (see CompiledCanSignal's doc comment for
            // why getting this wrong silently zeros out real flag values instead of erroring).
            if (signal.MaskBeforeShift)
            {
                if (signal.BitMask is { } mask) raw &= mask;
                if (signal.BitShift is { } shift) raw >>= shift;
            }
            else
            {
                if (signal.BitShift is { } shift) raw >>= shift;
                if (signal.BitMask is { } mask) raw &= mask;
            }

            var value = raw * signal.Scale + signal.OffsetValue;
            decoded.Add(new DecodedSignalValue(signal.FieldName, value));
        }

        return (true, frame.FrameName, decoded);
    }

    private static int[] ParseByteIndices(string? byteIndices)
    {
        if (string.IsNullOrWhiteSpace(byteIndices)) return [];
        return byteIndices.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var i) ? i : -1)
            .Where(i => i >= 0)
            .ToArray();
    }

    private static uint? ParseHexMask(string? bitMask)
    {
        if (string.IsNullOrWhiteSpace(bitMask)) return null;
        var hex = bitMask.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? bitMask[2..] : bitMask;
        return uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value) ? value : null;
    }
}
