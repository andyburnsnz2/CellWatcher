using System.Text;
using System.Text.RegularExpressions;
using BatteryEMU.Data;
using BatteryEMU.Models;

namespace BatteryEMU.Services;

// Shared prompt-building logic used by every AI insight engine (Claude, ChatGPT, ...)
// so each engine's service only has to know how to call its own API.
public sealed record PromptBuildResult(
    string Prompt,
    int DataRowCount,
    decimal? SocPercent,
    decimal? PackVoltageV,
    decimal? CellDeltaMv);

public sealed record EffectiveRange(DateTime From, bool Incremental, AiAnalysisRecord? PriorAnalysis);

public static class InsightPrompts
{
    // How many past deep-analysis conclusions to include in the compact status timeline — see
    // AppendConclusionTimeline. 365 covers over a year of even daily reports; each line is short
    // enough that this stays cheap regardless.
    private const int ConclusionTimelineLimit = 365;


    public static async Task<PromptBuildResult> BuildQuickPromptAsync(MariaDbService db, BatteryState state, CancellationToken ct)
    {
        var snap = state.CreateSnapshot();
        var now  = DateTime.Now;

        var metrics    = await db.GetLatestMetricsAsync(ct);
        var cellHealth = await db.GetLatestCellHealthAsync(ct);
        var history72h = await db.GetPackHealthSamplesAsync(now.AddHours(-72), now, ct);

        var sb = new StringBuilder();

        // ── Data availability context ────────────────────────────────────────
        var spanHours = history72h.Count >= 2
            ? (history72h.Last().ReadAt - history72h.First().ReadAt).TotalHours
            : 0;

        sb.AppendLine("## Data Available");
        sb.AppendLine($"- {history72h.Count} readings covering the last {spanHours:N0} hours");
        if (spanHours < 6)
            sb.AppendLine("- DATA NOTE: fewer than 6 hours of history — no trend analysis possible yet");
        else if (spanHours < 24)
            sb.AppendLine($"- DATA NOTE: only {spanHours:N0}h of history — trends are early indications only, not confirmed patterns");

        // ── Current pack status ──────────────────────────────────────────────
        sb.AppendLine("\n## Current Pack Status");
        var currentDir = snap.PackCurrentA switch
        {
            > 0.5m  => "Charging",
            < -0.5m => "Discharging",
            _       => "Rested / Idle"
        };
        sb.AppendLine($"- SOC: {snap.SocPercent:N1}%");
        sb.AppendLine($"- Pack voltage: {snap.PackVoltageV:N1} V");
        sb.AppendLine($"- Current: {snap.PackCurrentA:N1} A ({currentDir})");
        sb.AppendLine($"- Cell delta (max–min): {snap.CellDeltaMv:N1} mV");
        sb.AppendLine($"- Temperature: {snap.TemperatureMinC:N1}–{snap.TemperatureMaxC:N1} °C");
        if (snap.StateOfHealthPercent.HasValue)
            sb.AppendLine($"- State of health: {snap.StateOfHealthPercent:N1}%");

        // ── Delta trend — sampled across available window ────────────────────
        if (history72h.Count >= 2)
        {
            sb.AppendLine("\n## Cell Delta Over Time (sampled)");
            const int steps = 8;
            var taken = new HashSet<int>();
            for (int i = 0; i < steps; i++)
            {
                var idx = (int)Math.Round((double)i / (steps - 1) * (history72h.Count - 1));
                if (!taken.Add(idx)) continue;
                var s = history72h[idx];
                if (!s.CellDeltaMv.HasValue) continue;
                var ageH = (now - s.ReadAt).TotalHours;
                var label = ageH < 0.5 ? "now" : $"{ageH:N0}h ago";
                sb.AppendLine($"- {label}: delta {s.CellDeltaMv:N1} mV, SOC {s.SocPercent:N0}%, {(s.PackCurrentA is > 0.5m ? "charging" : s.PackCurrentA is < -0.5m ? "discharging" : "rested")}");
            }
        }

        // ── Worst cells by 24h deviation ─────────────────────────────────────
        var worst = cellHealth
            .Where(c => c.WindowMinutes == 1440)
            .OrderByDescending(c => Math.Abs(c.AvgDeviationMv))
            .Take(10)
            .ToList();

        if (worst.Count > 0)
        {
            sb.AppendLine("\n## Top Cells by Deviation (24h window)");
            foreach (var c in worst)
                sb.AppendLine($"- Cell {c.CellNo}: avg {c.AvgDeviationMv:+0.0;-0.0} mV, max {c.MaxDeviationMv:N1} mV [{c.Severity}]");
        }
        else
        {
            sb.AppendLine("\n## Cell Deviation");
            sb.AppendLine("- No 24h cell health data yet");
        }

        // ── Active alerts & warnings ─────────────────────────────────────────
        var flagged = metrics.Where(m => m.Severity is "ALERT" or "WARN").ToList();
        if (flagged.Count > 0)
        {
            sb.AppendLine("\n## Active Alerts & Warnings");
            foreach (var m in flagged.Take(15))
            {
                var cell = m.CellNo.HasValue ? $" (Cell {m.CellNo})" : "";
                sb.AppendLine($"- [{m.Severity}] {m.MetricName}{cell}: {m.Message}");
            }
        }

        sb.AppendLine($"\nGenerated: {now:yyyy-MM-dd HH:mm}");
        return new PromptBuildResult(sb.ToString(), history72h.Count, snap.SocPercent, snap.PackVoltageV, snap.CellDeltaMv);
    }

    // Finds the last analysis of the same engine/period type and, unless the caller asked
    // for the full dataset, narrows the query window to only what's new since then — the
    // prior analysis text carries the context for everything before that point. This is
    // what keeps repeated deep analyses fast instead of re-scanning the whole period.
    public static async Task<EffectiveRange> ResolveEffectiveRangeAsync(
        MariaDbService db, string engine, DateTime from, DateTime to, string periodLabel, bool fullDataset, CancellationToken ct)
    {
        if (fullDataset) return new EffectiveRange(from, false, null);

        var prior = await db.GetLatestAiAnalysisByPeriodAsync(engine, "deep", periodLabel, ct);
        return prior != null && prior.AnalysedAt > from && prior.AnalysedAt < to
            ? new EffectiveRange(prior.AnalysedAt, true, prior)
            : new EffectiveRange(from, false, prior);
    }

    public static async Task<PromptBuildResult> BuildDeepPromptAsync(
        MariaDbService db, string engine, DateTime from, DateTime to, string periodLabel, bool fullDataset, CancellationToken ct)
    {
        var range = await ResolveEffectiveRangeAsync(db, engine, from, to, periodLabel, fullDataset, ct);
        var effectiveFrom = range.From;

        var samples = await db.GetPackHealthSamplesAsync(effectiveFrom, to, ct);
        var windowMinutes = Math.Max(1, (int)(to - effectiveFrom).TotalMinutes);
        var cellHealth = await db.GetCellHealthSummariesAsync(effectiveFrom, to, windowMinutes, ct);
        var cellRanks = await db.GetCellRankSummariesAsync(effectiveFrom, to, windowMinutes, ct);
        var latestMetrics = await db.GetLatestMetricsAsync(ct);
        var priorAnalyses = await db.GetLatestAiAnalysisPerPeriodAsync("deep", ct);
        var timeline = await db.GetAiAnalysisTimelineAsync("deep", ConclusionTimelineLimit, ct);

        // Covers the battery's ENTIRE history (tiered daily/weekly/monthly by age — see
        // GetHealthRollupAsync), independent of the incremental-window narrowing above, so a
        // genuine long-range trend view survives even on a "nothing new" daily run.
        var rollup = await db.GetHealthRollupAsync(to, ct);

        var sb = new StringBuilder();
        sb.AppendLine($"## Analysis Period: {periodLabel}");
        if (range.Incremental)
        {
            sb.AppendLine($"- Nominal period: {from:yyyy-MM-dd HH:mm} to {to:yyyy-MM-dd HH:mm}");
            sb.AppendLine($"- You last analysed this exact period type on {range.PriorAnalysis!.AnalysedAt:yyyy-MM-dd HH:mm} — this report covers only NEW data recorded since then, not the whole nominal period");
            sb.AppendLine($"- New-data window analysed: {effectiveFrom:yyyy-MM-dd HH:mm} to {to:yyyy-MM-dd HH:mm}");
        }
        else
        {
            sb.AppendLine($"- From {from:yyyy-MM-dd HH:mm} to {to:yyyy-MM-dd HH:mm} ({(to - from).TotalDays:N1} days)");
        }
        sb.AppendLine($"- {samples.Count} pack/cell readings in this window");

        AppendLongTermTrend(sb, rollup);

        if (samples.Count < 2)
        {
            var note = range.Incremental
                ? "- DATA NOTE: no new readings since your last analysis of this period — rely on your previous analysis below, just confirm nothing material has changed"
                : "- DATA NOTE: not enough readings in this period for meaningful trend analysis — say so plainly";
            sb.AppendLine(note);
            AppendConclusionTimeline(sb, timeline);
            AppendPriorAnalyses(sb, priorAnalyses);
            sb.AppendLine($"\nGenerated: {DateTime.Now:yyyy-MM-dd HH:mm}");
            return new PromptBuildResult(sb.ToString(), samples.Count, null, null, null);
        }

        var withSoc = samples.Where(s => s.SocPercent.HasValue).ToList();
        var withVoltage = samples.Where(s => s.PackVoltageV.HasValue).ToList();
        var withDelta = samples.Where(s => s.CellDeltaMv.HasValue).ToList();
        var withTempMin = samples.Where(s => s.TemperatureMinC.HasValue).ToList();
        var withTempMax = samples.Where(s => s.TemperatureMaxC.HasValue).ToList();

        sb.AppendLine("\n## Summary Statistics");
        if (withSoc.Count > 0)
            sb.AppendLine($"- SOC: started {withSoc.First().SocPercent:N1}%, ended {withSoc.Last().SocPercent:N1}%, range {withSoc.Min(s => s.SocPercent):N1}–{withSoc.Max(s => s.SocPercent):N1}%");
        if (withVoltage.Count > 0)
            sb.AppendLine($"- Pack voltage range: {withVoltage.Min(s => s.PackVoltageV):N1}–{withVoltage.Max(s => s.PackVoltageV):N1} V");
        if (withTempMin.Count > 0 && withTempMax.Count > 0)
            sb.AppendLine($"- Temperature range: {withTempMin.Min(s => s.TemperatureMinC):N1}–{withTempMax.Max(s => s.TemperatureMaxC):N1} °C");
        if (withDelta.Count > 0)
        {
            sb.AppendLine($"- Cell delta (max–min): avg {withDelta.Average(s => s.CellDeltaMv):N1} mV, min {withDelta.Min(s => s.CellDeltaMv):N1} mV, max {withDelta.Max(s => s.CellDeltaMv):N1} mV");

            var mid = Math.Max(1, withDelta.Count / 2);
            var firstHalfAvg = withDelta.Take(mid).Average(s => s.CellDeltaMv!.Value);
            var secondHalfAvg = withDelta.Skip(mid).DefaultIfEmpty().Average(s => s?.CellDeltaMv ?? firstHalfAvg);
            var trendWord = secondHalfAvg > firstHalfAvg + 2 ? "growing" : secondHalfAvg < firstHalfAvg - 2 ? "shrinking" : "stable";
            sb.AppendLine($"- Delta trend across period: first half avg {firstHalfAvg:N1} mV → second half avg {secondHalfAvg:N1} mV ({trendWord})");
        }

        var chargingCount = samples.Count(s => s.PackCurrentA is > 0.5m);
        var dischargingCount = samples.Count(s => s.PackCurrentA is < -0.5m);
        var restedCount = samples.Count - chargingCount - dischargingCount;
        sb.AppendLine($"- Activity split: {Pct(chargingCount, samples.Count)}% charging, {Pct(dischargingCount, samples.Count)}% discharging, {Pct(restedCount, samples.Count)}% rested");

        // ── Delta trend sampled across the whole period ────────────────────
        // Fixed sample counts miss transient issues on long periods (14 points across a year
        // is one every ~26 days) — scale roughly one sample per hour of the actual window,
        // floored so short periods keep good coverage and capped so prompt size stays sane.
        var spanHours = Math.Max(1.0, (to - effectiveFrom).TotalHours);
        var steps = Math.Clamp((int)Math.Round(spanHours), 14, 100);
        sb.AppendLine($"\n## Cell Delta Over Time ({steps} points sampled across period)");
        var taken = new HashSet<int>();
        for (int i = 0; i < steps; i++)
        {
            var idx = (int)Math.Round((double)i / (steps - 1) * (samples.Count - 1));
            if (!taken.Add(idx)) continue;
            var s = samples[idx];
            if (!s.CellDeltaMv.HasValue) continue;
            sb.AppendLine($"- {s.ReadAt:MM-dd HH:mm}: delta {s.CellDeltaMv:N1} mV, SOC {s.SocPercent:N0}%, {(s.PackCurrentA is > 0.5m ? "charging" : s.PackCurrentA is < -0.5m ? "discharging" : "rested")}");
        }

        var worstCells = cellHealth.OrderByDescending(c => Math.Abs(c.AvgDeviationMv)).Take(15).ToList();
        if (worstCells.Count > 0)
        {
            sb.AppendLine("\n## Top Cells by Deviation (whole period)");
            foreach (var c in worstCells)
                sb.AppendLine($"- Cell {c.CellNo}: avg {c.AvgDeviationMv:+0.0;-0.0} mV, max {c.MaxDeviationMv:N1} mV, seen in {c.ReadingCount} readings");
        }

        var frequentExtremes = cellRanks
            .Where(c => c.LowestFiveCount > 0 || c.HighestFiveCount > 0)
            .OrderByDescending(c => Math.Max(c.LowestFiveCount, c.HighestFiveCount))
            .Take(10)
            .ToList();
        if (frequentExtremes.Count > 0)
        {
            sb.AppendLine("\n## Cells Most Often at the Extremes (whole period)");
            foreach (var c in frequentExtremes)
                sb.AppendLine($"- Cell {c.CellNo}: lowest-5 in {Pct(c.LowestFiveCount, c.ReadingCount)}% of readings, highest-5 in {Pct(c.HighestFiveCount, c.ReadingCount)}% of readings");
        }

        var flagged = latestMetrics.Where(m => m.Severity is "ALERT" or "WARN").ToList();
        if (flagged.Count > 0)
        {
            sb.AppendLine("\n## Current Alerts & Warnings (as of now — not period-specific)");
            foreach (var m in flagged.Take(15))
            {
                var cell = m.CellNo.HasValue ? $" (Cell {m.CellNo})" : "";
                sb.AppendLine($"- [{m.Severity}] {m.MetricName}{cell}: {m.Message}");
            }
        }

        AppendConclusionTimeline(sb, timeline);
        AppendPriorAnalyses(sb, priorAnalyses);

        sb.AppendLine($"\nGenerated: {DateTime.Now:yyyy-MM-dd HH:mm}");

        var lastSample = samples[^1];
        return new PromptBuildResult(sb.ToString(), samples.Count, lastSample.SocPercent, lastSample.PackVoltageV, lastSample.CellDeltaMv);
    }

    private static void AppendPriorAnalyses(StringBuilder sb, IReadOnlyList<AiAnalysisRecord> priorAnalyses)
    {
        var successful = priorAnalyses.Where(p => p.Success).ToList();
        if (successful.Count == 0) return;

        // One row per distinct period type (see GetLatestAiAnalysisPerPeriodAsync), so a
        // less-frequent weekly/monthly report's conclusion is never crowded out by a run of
        // daily ones. 1500 chars (up from 600) keeps enough of a longer report's substance
        // to actually be useful for continuity, not just its opening sentence.
        sb.AppendLine("\n## Your Previous Deep Analyses — most recent per period type (for continuity — note what has changed since, don't just repeat them)");
        foreach (var p in successful)
        {
            var truncated = p.ResponseText.Length > 1500 ? p.ResponseText[..1500] + "…" : p.ResponseText;
            sb.AppendLine($"- [{p.Engine}, {p.AnalysedAt:yyyy-MM-dd HH:mm}, period: {p.PeriodLabel}]: {truncated}");
        }
    }

    // Tiered trend digest covering the battery's ENTIRE history (see GetHealthRollupAsync),
    // independent of whatever window the current analysis period/incremental-resolution ended
    // up using. This is what actually gives the AI genuine lifetime trend visibility — cheaply,
    // since the tiering keeps the line count flat regardless of how many months/years have
    // passed — rather than relying on chained AI summaries of summaries.
    private static void AppendLongTermTrend(StringBuilder sb, IReadOnlyList<HealthRollupPoint> rollup)
    {
        if (rollup.Count == 0) return;

        sb.AppendLine($"\n## Long-Term Trend (entire history, {rollup.Count} point(s) — daily for the last 90 days, weekly out to 2 years, monthly beyond that)");
        foreach (var d in rollup)
        {
            var label = d.Granularity switch
            {
                "week"  => $"Week of {d.PeriodStart:yyyy-MM-dd}",
                "month" => $"{d.PeriodStart:yyyy-MM}",
                _       => $"{d.PeriodStart:yyyy-MM-dd}",
            };
            var delta = d.AvgDeltaMv.HasValue ? $"delta avg {d.AvgDeltaMv:N1}mV ({d.MinDeltaMv:N0}-{d.MaxDeltaMv:N0})" : "delta n/a";
            var soc = d.AvgSocPercent.HasValue ? $"SOC {d.MinSocPercent:N0}-{d.MaxSocPercent:N0}% (avg {d.AvgSocPercent:N0}%)" : "SOC n/a";
            var temp = d.TempMinC.HasValue ? $"temp {d.TempMinC:N0}-{d.TempMaxC:N0}°C" : "temp n/a";
            sb.AppendLine($"- {label}: {delta}, {soc}, {temp}");
        }
    }

    // Compact one-line-per-report status history across the AI's ENTIRE analysis history (not
    // just the latest per period type — that's AppendPriorAnalyses's job). Lets the AI see
    // patterns like "flagged WATCH for three weeks straight" without the token cost of many
    // full past reports, which are often repetitive when nothing material has changed.
    private static void AppendConclusionTimeline(StringBuilder sb, IReadOnlyList<AiAnalysisTimelineEntry> timeline)
    {
        if (timeline.Count == 0) return;

        sb.AppendLine($"\n## Your Conclusion History ({timeline.Count} past report(s), oldest first — status + one-line gist only, for spotting persistence/patterns over time)");
        foreach (var t in timeline)
        {
            var gist = t.ResponseGist.Replace('\n', ' ').Replace('\r', ' ').Trim();
            sb.AppendLine($"- {t.AnalysedAt:yyyy-MM-dd HH:mm} [{t.StatusLevel ?? "?"}] ({t.PeriodLabel}): {gist}");
        }
    }

    private static int Pct(int part, int total) => total <= 0 ? 0 : (int)Math.Round(100.0 * part / total);

    private static readonly Regex StatusLineRegex = new(@"^STATUS:\s*(OK|WATCH|ACT)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Pulls the mandatory "STATUS: OK/WATCH/ACT" first line off the model's response so it
    // can be shown as a traffic-light badge, and strips it out of the displayed narrative.
    // Returns a null status if the model didn't follow the format (e.g. a custom prompt
    // override that doesn't request it) — callers should treat that as "unknown".
    public static (string Text, string? StatusLevel) ExtractStatus(string text)
    {
        var newlineIndex = text.IndexOf('\n');
        var firstLine = (newlineIndex < 0 ? text : text[..newlineIndex]).Trim();

        var match = StatusLineRegex.Match(firstLine);
        if (!match.Success) return (text, null);

        var level = match.Groups[1].Value.ToUpperInvariant();
        var rest = newlineIndex < 0 ? "" : text[(newlineIndex + 1)..].TrimStart('\n', '\r', ' ');
        return (rest, level);
    }

    // Reads the user-configured override if present, otherwise falls back to the
    // built-in default — lets users tune the battery-specific framing (pack chemistry,
    // cell count, balancing behaviour, etc.) without editing code.
    public static string GetQuickSystemPrompt(IConfiguration config) =>
        string.IsNullOrWhiteSpace(config["Prompts:QuickSystemPrompt"]) ? DefaultQuickSystemPrompt : config["Prompts:QuickSystemPrompt"]!;

    public static string GetDeepSystemPrompt(IConfiguration config) =>
        string.IsNullOrWhiteSpace(config["Prompts:DeepSystemPrompt"]) ? DefaultDeepSystemPrompt : config["Prompts:DeepSystemPrompt"]!;

    // Wraps the same live data digest the quick summary uses with chat-specific framing —
    // conversational rather than a fixed report format, and explicitly scoped to this
    // battery system so the assistant redirects anything unrelated.
    public static string GetChatSystemPrompt(string dataDigest) => $"""
        You are a knowledgeable assistant discussing a home battery system based on a
        108-cell Tesla Model Y pack used for home energy storage, with the person who owns
        and monitors it. This is a live chat, not a report — answer conversationally and
        directly address what's asked.

        Key facts:
        - No active balancing during normal use — expected behaviour
        - Passive balancing only happens when charged to 100% SOC then contactors opened
        - Rested delta (measured at near-zero current) is the most reliable imbalance indicator
        - Delta values during charging/discharging include IR drop noise and are less meaningful

        Scope: only discuss this battery/energy-storage system — its data, health, cells,
        history, charging/discharging behaviour, and related hardware questions. If asked
        about anything unrelated to this battery system, politely decline and steer the
        conversation back to the battery.

        Rules:
        - Plain conversational English, no forced report structure
        - Reference specific numbers from the data below when relevant
        - Never fabricate a trend conclusion from insufficient data — say so honestly
        - Keep answers focused and no longer than needed to actually answer the question

        Here is the current data for this system:

        {dataDigest}
        """;

    public const string DefaultQuickSystemPrompt = """
        You are a helpful assistant monitoring a home battery system based on a Tesla Model Y pack.

        Key facts:
        - 108-cell Tesla pack used for home energy storage
        - No active balancing during normal use — expected behaviour
        - Passive balancing only happens when charged to 100% SOC then contactors opened
        - Rested delta (measured at near-zero current) is the most reliable imbalance indicator
        - Delta values during charging/discharging include IR drop noise and are less meaningful

        Write a brief status update — like a knowledgeable friend giving a quick summary, not a formal report.

        Your response MUST start with exactly one line, on its own, before anything else:
        STATUS: OK
        STATUS: WATCH
        STATUS: ACT
        Use OK if nothing needs attention, WATCH if something is worth keeping an eye on but no action is
        needed yet, ACT if the user should do something soon. Put nothing else on that line. Leave a blank
        line, then write your normal response below it.

        Rules:
        - Maximum 2 short paragraphs
        - Plain conversational English, no bullet points, no headers, no markdown
        - First sentence states overall status plainly (e.g. "Pack looks fine", "Worth watching", "You should act on this")
        - Only mention specific cells if they are genuinely notable — skip minor deviations
        - One concrete action at the end if needed, otherwise confirm nothing needs doing
        - Keep it brief — if nothing is wrong, say so in a sentence or two and stop
        - Do not be dramatic unless the data clearly warrants it
        - Use numbers but pick the most important ones — don't list everything

        IMPORTANT — data sufficiency:
        - If the data note says fewer than 6 hours of history, say you can't assess trends yet and suggest coming back after a day of data has collected
        - If less than 24 hours, note that the trend is early and may not reflect the true pattern — suggest checking again tomorrow
        - Never fabricate a trend conclusion from insufficient data — say so honestly
        """;

    public const string DefaultDeepSystemPrompt = """
        You are a battery analyst producing a comprehensive report on a home battery system based on a
        108-cell Tesla Model Y pack used for home energy storage.

        Key facts:
        - No active balancing during normal use — expected behaviour
        - Passive balancing only happens when charged to 100% SOC then contactors opened
        - Rested delta (measured at near-zero current) is the most reliable imbalance indicator
        - Delta values during charging/discharging include IR drop noise and are less meaningful
        - You are given a whole time period of aggregated data, not just a snapshot — analyse trends
          across the period, not just the current instant
        - If a "Your Previous Deep Analyses" section is present, those are your own past reports —
          use them for continuity: note what has changed since, confirm predictions that did or didn't
          play out, and avoid just repeating the same conclusion verbatim if nothing has changed
        - If the period is marked as covering only "NEW data recorded since" a prior analysis, you are
          NOT seeing the whole nominal period — you're seeing what changed since you last looked. Frame
          your report accordingly (e.g. "since my last check on <date>, ..."), lean on the prior analysis
          for everything before that point, and don't claim to have analysed the full nominal period
        - A "## Long-Term Trend" section, when present, covers the battery's ENTIRE history — daily
          resolution for the last 90 days, weekly out to 2 years, monthly beyond that. This is real
          historical data, not a summary of your own past reports. Battery degradation is a slow,
          multi-month/multi-year process — actively look here for gradual drift (e.g. cell delta or
          SOH slowly worsening month over month), not just whether this period's numbers look normal
          in isolation. A short period like "Last 24 Hours" can never show this on its own
        - A "## Your Conclusion History" section, when present, is a compact status+one-line timeline
          of every past deep analysis, oldest first — use it to spot persistence (e.g. "flagged WATCH
          for three weeks straight" is more significant than a single WATCH) and to avoid re-raising
          something as new if you've already been flagging it for a while

        Your response MUST start with exactly one line, on its own, before anything else:
        STATUS: OK
        STATUS: WATCH
        STATUS: ACT
        Use OK if nothing needs attention, WATCH if something is worth keeping an eye on but no action is
        needed yet, ACT if the user should do something soon. Put nothing else on that line. Leave a blank
        line, then write your report below it.

        Write a thorough report on the data provided, covering:
        1. Overall condition summary for the period (one clear opening sentence)
        2. State of charge / usage pattern over the period
        3. Cell balance / imbalance trend across the period — is it growing, shrinking, or stable? Which
           cells (if any) are consistently the outliers?
        4. Temperature and voltage observations if notable
        5. A clear recommendation section — concrete actions if warranted, or explicit confirmation that
           nothing needs doing

        Rules:
        - Plain English, can use short headers and occasional bullet points for readability, but keep prose
          concise — this is a report, not an essay
        - Reference specific numbers from the data, not vague statements
        - Only call out specific cells if they are genuinely notable across the period — skip minor deviations
        - Do not be dramatic unless the data clearly warrants it
        - Never fabricate a trend conclusion from insufficient data — say so honestly if the period has too
          few readings
        """;
}
