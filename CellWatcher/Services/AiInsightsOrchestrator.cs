using CellWatcher.Data;
using CellWatcher.Models;

namespace CellWatcher.Services;

// Fans a single insight request out to every configured AI engine so the frontend
// gets one combined response and can label which engine produced which result.
public sealed class AiInsightsOrchestrator
{
    private readonly ClaudeInsightsService _claude;
    private readonly OpenAiInsightsService _openAi;
    private readonly MariaDbService _db;

    public AiInsightsOrchestrator(ClaudeInsightsService claude, OpenAiInsightsService openAi, MariaDbService db)
    {
        _claude = claude;
        _openAi = openAi;
        _db = db;
    }

    public bool AnyConfigured => _claude.IsConfigured || _openAi.IsConfigured;

    // Prefers the in-memory cache (freshest, this run) and falls back to the last
    // saved analysis in the database — so the dashboard shows real data on first
    // load after a restart instead of an empty "not configured" state.
    public async Task<MultiEngineInsightResult> GetCachedQuickAsync(CancellationToken ct = default)
    {
        var claudeTask = _claude.IsConfigured ? _claude.GetCachedOrLastSavedAsync(ct) : Task.FromResult<InsightResult?>(null);
        var openAiTask = _openAi.IsConfigured ? _openAi.GetCachedOrLastSavedAsync(ct) : Task.FromResult<InsightResult?>(null);
        await Task.WhenAll(claudeTask, openAiTask);
        return new MultiEngineInsightResult(await claudeTask, await openAiTask);
    }

    public async Task<MultiEngineInsightResult> RefreshQuickAsync(CancellationToken ct = default)
    {
        var claudeTask = RunIfConfigured(_claude.IsConfigured, () => _claude.RefreshAsync(ct));
        var openAiTask = RunIfConfigured(_openAi.IsConfigured, () => _openAi.RefreshAsync(ct));
        await Task.WhenAll(claudeTask, openAiTask);
        return new MultiEngineInsightResult(await claudeTask, await openAiTask);
    }

    public async Task<MultiEngineInsightResult> AnalyzePeriodAsync(
        DateTime from, DateTime to, string periodLabel, bool fullDataset, CancellationToken ct = default)
    {
        var claudeTask = RunIfConfigured(_claude.IsConfigured, () => _claude.AnalyzePeriodAsync(from, to, periodLabel, fullDataset, ct));
        var openAiTask = RunIfConfigured(_openAi.IsConfigured, () => _openAi.AnalyzePeriodAsync(from, to, periodLabel, fullDataset, ct));
        await Task.WhenAll(claudeTask, openAiTask);
        return new MultiEngineInsightResult(await claudeTask, await openAiTask);
    }

    // Runs exactly the engine/report combination a schedule row asks for (independent per-cell
    // matrix, not "both engines get the same report") — still gated on each engine's IsConfigured
    // in case a key was removed after the schedule was created.
    public async Task RunScheduledAsync(
        AiScheduleEntry entry, DateTime from, DateTime to, string periodLabel, bool fullDataset, CancellationToken ct = default)
    {
        var tasks = new List<Task>();

        if (entry.RunClaudeQuick && _claude.IsConfigured) tasks.Add(_claude.RefreshAsync(ct));
        if (entry.RunClaudeDeep && _claude.IsConfigured) tasks.Add(_claude.AnalyzePeriodAsync(from, to, periodLabel, fullDataset, ct));
        if (entry.RunChatGptQuick && _openAi.IsConfigured) tasks.Add(_openAi.RefreshAsync(ct));
        if (entry.RunChatGptDeep && _openAi.IsConfigured) tasks.Add(_openAi.AnalyzePeriodAsync(from, to, periodLabel, fullDataset, ct));

        await Task.WhenAll(tasks);
    }

    // Routes a chat turn to whichever engine the frontend's selector picked — unlike the
    // quick/deep flows, chat only ever talks to one engine at a time.
    public Task<InsightResult> ChatAsync(string engine, IReadOnlyList<ChatMessage> history, CancellationToken ct = default) => engine switch
    {
        "claude" => _claude.ChatAsync(history, ct),
        "chatgpt" => _openAi.ChatAsync(history, ct),
        _ => Task.FromResult(new InsightResult($"Unknown engine '{engine}'.", DateTime.Now, false)),
    };

    // Lets the frontend show "N readings will be analysed" before actually running the
    // (much slower) deep analysis, per configured engine — since each engine can have a
    // different "last analysed" point, the incremental window can differ per engine.
    public async Task<CountPreviewResult> PreviewCountAsync(
        DateTime from, DateTime to, string periodLabel, bool fullDataset, CancellationToken ct = default)
    {
        var claudeTask = _claude.IsConfigured ? BuildPreviewAsync("claude", from, to, periodLabel, fullDataset, ct) : Task.FromResult<EngineCountPreview?>(null);
        var openAiTask = _openAi.IsConfigured ? BuildPreviewAsync("chatgpt", from, to, periodLabel, fullDataset, ct) : Task.FromResult<EngineCountPreview?>(null);
        await Task.WhenAll(claudeTask, openAiTask);
        return new CountPreviewResult(await claudeTask, await openAiTask);
    }

    private async Task<EngineCountPreview?> BuildPreviewAsync(
        string engine, DateTime from, DateTime to, string periodLabel, bool fullDataset, CancellationToken ct)
    {
        var range = await InsightPrompts.ResolveEffectiveRangeAsync(_db, engine, from, to, periodLabel, fullDataset, ct);
        var count = await _db.GetPackReadingCountAsync(range.From, to, ct);
        return new EngineCountPreview(count, range.Incremental, range.From, range.PriorAnalysis?.AnalysedAt);
    }

    private static async Task<InsightResult?> RunIfConfigured(bool configured, Func<Task<InsightResult>> call) =>
        configured ? await call() : null;
}

public sealed record MultiEngineInsightResult(InsightResult? Claude, InsightResult? ChatGpt);

public sealed record EngineCountPreview(int RowCount, bool Incremental, DateTime EffectiveFrom, DateTime? PriorAnalysedAt);

public sealed record CountPreviewResult(EngineCountPreview? Claude, EngineCountPreview? ChatGpt);
