using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BatteryEMU.Data;
using BatteryEMU.Models;
using Microsoft.Extensions.Logging;

namespace BatteryEMU.Services;

public sealed record BatteryImportResult(int BatteriesImported, int FilesSkipped, int TotalMappings);

// Trawls github.com/dalathegreat/Battery-Emulator's Software/src/battery directory and extracts
// CAN ID -> frame name -> per-signal decode formula mappings per battery. Every battery in that
// repo communicates over CAN, not Modbus, despite how this feature is often described informally
// in conversation — see create_battery_type.sql.
//
// Extraction is regex-based scraping of source code, not a real C++ parser. Verified against
// Tesla and BMW i3's battery drivers (structurally very different otherwise), both of which
// consistently use a "switch (rx_frame.ID) { case 0x...: //comment ... }" receive dispatcher.
// Within each case block, every assignment statement is captured (field name + raw right-hand
// side expression) — this is the real byte-level decode formula when the source computes it
// directly from rx_frame.data.u8[N] (bit shifts/masks/scale factors), or a reference to an
// intermediate variable that some other code (possibly a different function, e.g. update_values(),
// not traced by this scraper) later feeds into the standardized DATALAYER_BATTERY_*_TYPE interface
// every battery driver implements against (see Software/src/datalayer/datalayer.h). Source-code
// scraping, not a guaranteed-correct structured decode — always cross-check the real upstream
// file before relying on it for anything safety-relevant.
public sealed class BatteryCanMappingImportService
{
    private const string ApiListingUrl = "https://api.github.com/repos/dalathegreat/Battery-Emulator/contents/Software/src/battery";
    private const string RawUrlTemplate = "https://raw.githubusercontent.com/dalathegreat/Battery-Emulator/main/Software/src/battery/{0}";

    // Matches "case 0x1A2:" optionally followed by a "// comment" — the receive-dispatch pattern
    // every battery file checked uses inside handle_incoming_can_frame's switch block.
    private static readonly Regex CaseLineRegex = new(@"^case\s+0x([0-9A-Fa-f]+)\s*:\s*(?://\s*(.*))?$", RegexOptions.Compiled);

    // Any assignment statement (not == comparison, not a compound += etc — the LHS character
    // class excludes operators, so those simply fail to match rather than being misparsed).
    private static readonly Regex AssignRegex = new(@"^([A-Za-z_][\w.]*(?:->[\w.]+)*(?:\[[^\]]*\])?)\s*=(?!=)\s*(.+)$", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    // Present in nearly every case block ("we heard from this battery, mark it alive") —
    // zero frame-specific decode value, filtered out as pure noise.
    private const string NoiseTarget = "datalayer_battery->status.CAN_battery_still_alive";
    private const int MaxExpressionLength = 200;

    // Structured decode-field extraction from the raw expression — see
    // alter_battery_can_signal_add_decode_fields.sql for what these mean and their limits.
    private static readonly Regex ByteIndexRegex = new(@"u8\s*\[\s*(\d+)\s*\]", RegexOptions.Compiled);
    private static readonly Regex BitMaskRegex = new(@"&\s*\(?0x([0-9A-Fa-f]+)U?\)?", RegexOptions.Compiled);
    private static readonly Regex BitShiftRegex = new(@">>\s*(\d+)", RegexOptions.Compiled);
    private static readonly Regex TrailingOffsetRegex = new(@"([+\-])\s*(\d+\.?\d*)\s*$", RegexOptions.Compiled);
    private static readonly Regex TrailingScaleRegex = new(@"\*\s*(-?\d+\.?\d*)\s*$", RegexOptions.Compiled);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly MariaDbService _db;
    private readonly ILogger<BatteryCanMappingImportService> _logger;

    public BatteryCanMappingImportService(IHttpClientFactory httpClientFactory, MariaDbService db, ILogger<BatteryCanMappingImportService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _db = db;
        _logger = logger;
    }

    public async Task<BatteryImportResult> ImportAllAsync(CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("github");
        // GitHub's REST API (unlike raw.githubusercontent.com, used for the per-file fetches
        // below) requires a User-Agent or every request is rejected outright.
        if (!client.DefaultRequestHeaders.Contains("User-Agent"))
            client.DefaultRequestHeaders.Add("User-Agent", "BatteryEMU-Importer");

        var listing = await client.GetFromJsonAsync<List<GitHubContentEntry>>(ApiListingUrl, ct) ?? [];
        var cppFiles = listing.Where(f => f.Type == "file" && f.Name.EndsWith(".cpp", StringComparison.OrdinalIgnoreCase)).ToList();

        var batteriesImported = 0;
        var filesSkipped = 0;
        var totalMappings = 0;

        foreach (var file in cppFiles)
        {
            var rawUrl = string.Format(RawUrlTemplate, file.Name);
            string content;
            try
            {
                content = await client.GetStringAsync(rawUrl, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Battery import: failed to fetch {File}", file.Name);
                filesSkipped++;
                continue;
            }

            var mappings = ParseCanMappings(content);
            if (mappings.Count == 0)
            {
                // Shared/base-class files (Battery.cpp, CanBattery.cpp, Shunts.cpp, ...) have no
                // "switch (rx_frame.ID)" receive dispatcher — correctly not a battery, not a
                // parse failure.
                filesSkipped++;
                continue;
            }

            var batteryName = file.Name[..^".cpp".Length];
            await _db.ReplaceBatteryTypeMappingsAsync(batteryName, file.Name, rawUrl, mappings, ct);
            batteriesImported++;
            totalMappings += mappings.Count;
        }

        _logger.LogInformation(
            "Battery import: {Imported} batteries imported, {Skipped} files skipped, {Mappings} total mappings",
            batteriesImported, filesSkipped, totalMappings);

        return new BatteryImportResult(batteriesImported, filesSkipped, totalMappings);
    }

    internal static List<ParsedCanMapping> ParseCanMappings(string sourceCode)
    {
        var results = new List<ParsedCanMapping>();

        // Scoped to the receive-side switch statement only — case 0x... appears elsewhere too
        // (e.g. building outgoing frames in transmit_can), and only the receive dispatcher is a
        // real "this CAN ID means this" mapping. Simple substring scoping, not real brace
        // matching — good enough given every file checked has exactly one such switch block.
        var switchStart = sourceCode.IndexOf("switch (rx_frame.ID)", StringComparison.Ordinal);
        if (switchStart < 0) return results;

        var lines = sourceCode[switchStart..].Split('\n');

        var caseStarts = new List<(int LineIndex, uint Id, string? Name)>();
        for (var i = 0; i < lines.Length; i++)
        {
            var m = CaseLineRegex.Match(lines[i].Trim());
            if (m.Success)
            {
                caseStarts.Add((i, Convert.ToUInt32(m.Groups[1].Value, 16),
                    m.Groups[2].Success && m.Groups[2].Value.Length > 0 ? m.Groups[2].Value.Trim() : null));
            }
        }

        for (var c = 0; c < caseStarts.Count; c++)
        {
            var (startLine, id, name) = caseStarts[c];
            var endLine = c + 1 < caseStarts.Count ? caseStarts[c + 1].LineIndex : lines.Length;
            var blockLines = lines[(startLine + 1)..endLine]; // exclude the "case 0x...:" line itself

            // Strip // comments per line, then join everything into one blob so multi-line
            // assignments (very common in this codebase) reconstruct into a single statement
            // before splitting on ';' — a line-by-line regex misses these entirely.
            var blob = string.Join(" ", blockLines.Select(l =>
            {
                var idx = l.IndexOf("//", StringComparison.Ordinal);
                return idx >= 0 ? l[..idx] : l;
            }));
            blob = WhitespaceRegex.Replace(blob, " ").Trim();

            var signals = new List<ParsedCanSignal>();
            foreach (var stmtRaw in blob.Split(';'))
            {
                var stmt = stmtRaw.Trim();
                if (stmt.Length == 0) continue;

                var m = AssignRegex.Match(stmt);
                if (!m.Success) continue;

                var target = m.Groups[1].Value;
                if (target == NoiseTarget) continue;

                var expr = m.Groups[2].Value.Trim();
                if (expr.Length > MaxExpressionLength) expr = expr[..MaxExpressionLength] + "...";
                var expression = expr.Length > 0 ? expr : null;

                var (byteIndices, bitMask, bitShift, maskBeforeShift, scale, offsetValue) = ExtractDecodeFields(expression);
                signals.Add(new ParsedCanSignal(target, expression, byteIndices, bitMask, bitShift, maskBeforeShift, scale, offsetValue));
            }

            results.Add(new ParsedCanMapping(id, name, signals));
        }

        return results;
    }

    // Best-effort structured parse of a raw C++ expression into "which bytes, what mask/shift in
    // what order, what scale/offset" — the actual recipe for decoding a raw CAN frame's bytes
    // into a real value. Not every expression fits (boolean comparisons, references to other
    // variables, function calls) — those correctly come back all-null, with expression_text
    // remaining the only record. Multi-mask/multi-shift expressions (combining several bytes
    // with different masks each) only capture the FIRST mask/shift found — full fidelity is
    // always in expression_text, this is a convenience extraction, not a replacement for it.
    //
    // MaskBeforeShift is not optional to get right: "(x & mask) >> shift" and "(x >> shift) &
    // mask" are different operations. Verified against real source — Tesla's BMS_alertMatrix
    // (0x320) is almost entirely shift-then-mask; HVP_alertMatrix1 (0x3AA) is almost entirely
    // mask-then-shift. Assuming one order always is a real bug, not a rare edge case: for a
    // single-bit flag (mask 0x01) after a nonzero shift, the wrong order always evaluates to 0
    // regardless of the real bit value — it doesn't fail loudly, it just silently reports every
    // such flag as permanently off.
    internal static (string? ByteIndices, string? BitMask, int? BitShift, bool? MaskBeforeShift, double? Scale, double? OffsetValue) ExtractDecodeFields(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression)) return (null, null, null, null, null, null);

        var byteIndexMatches = ByteIndexRegex.Matches(expression);
        string? byteIndices = byteIndexMatches.Count > 0
            ? string.Join(",", byteIndexMatches.Select(m => m.Groups[1].Value))
            : null;

        var maskMatch = BitMaskRegex.Match(expression);
        string? bitMask = maskMatch.Success ? "0x" + maskMatch.Groups[1].Value.ToUpperInvariant() : null;

        var shiftMatch = BitShiftRegex.Match(expression);
        int? bitShift = shiftMatch.Success ? int.Parse(shiftMatch.Groups[1].Value, CultureInfo.InvariantCulture) : null;

        // Only meaningful (and only recorded) when both are present — with just one operation,
        // order is moot.
        bool? maskBeforeShift = maskMatch.Success && shiftMatch.Success
            ? maskMatch.Index < shiftMatch.Index
            : null;

        // Peeled from the end in two passes so a combined "* scale + offset" tail (e.g.
        // "(...) * -0.05 + 822") resolves both parts instead of just whichever regex runs first.
        var remainder = expression.TrimEnd();
        double? offsetValue = null;
        var offsetMatch = TrailingOffsetRegex.Match(remainder);
        if (offsetMatch.Success)
        {
            var sign = offsetMatch.Groups[1].Value == "-" ? -1 : 1;
            offsetValue = sign * double.Parse(offsetMatch.Groups[2].Value, CultureInfo.InvariantCulture);
            remainder = remainder[..offsetMatch.Index].TrimEnd();
        }

        double? scale = null;
        var scaleMatch = TrailingScaleRegex.Match(remainder);
        if (scaleMatch.Success)
            scale = double.Parse(scaleMatch.Groups[1].Value, CultureInfo.InvariantCulture);

        return (byteIndices, bitMask, bitShift, maskBeforeShift, scale, offsetValue);
    }
}

internal sealed class GitHubContentEntry
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";
}
