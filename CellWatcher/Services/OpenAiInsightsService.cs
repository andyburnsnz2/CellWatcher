using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CellWatcher.Data;
using CellWatcher.Models;

namespace CellWatcher.Services;

public sealed class OpenAiInsightsService
{
    private const string Engine = "chatgpt";

    private readonly IConfiguration _config;
    private readonly MariaDbService _db;
    private readonly BatteryState _state;
    private readonly HttpClient _http;
    private readonly ILogger<OpenAiInsightsService> _logger;
    private readonly NotificationService _notifications;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private InsightResult? _cached;

    public OpenAiInsightsService(
        IConfiguration config,
        MariaDbService db,
        BatteryState state,
        IHttpClientFactory httpClientFactory,
        ILogger<OpenAiInsightsService> logger,
        NotificationService notifications)
    {
        _config = config;
        _db = db;
        _state = state;
        _http = httpClientFactory.CreateClient("openai");
        _logger = logger;
        _notifications = notifications;
    }

    public bool IsConfigured =>
        _config.GetValue<bool>("OpenAi:Enabled", true) && !string.IsNullOrWhiteSpace(_config["OpenAi:ApiKey"]);

    public InsightResult? GetCached() => _cached;

    // In-memory cache is empty right after a restart — fall back to the last
    // saved quick analysis so the dashboard doesn't look "unconfigured" on load.
    public async Task<InsightResult?> GetCachedOrLastSavedAsync(CancellationToken ct = default)
    {
        if (_cached != null) return _cached;
        if (!IsConfigured) return null;

        var record = await _db.GetLatestAiAnalysisAsync(Engine, "quick", ct);
        return record == null ? null : new InsightResult(record.ResponseText, record.AnalysedAt, record.Success, StatusLevel: record.StatusLevel);
    }

    public async Task<InsightResult> RefreshAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (!IsConfigured)
                return _cached = Fail("ChatGPT API key not configured. Add it in the Config page under Integrations.");

            var model = _config["OpenAi:Model"] ?? "gpt-5";
            var built = await InsightPrompts.BuildQuickPromptAsync(_db, _state, ct);
            var systemPrompt = InsightPrompts.GetQuickSystemPrompt(_config);
            var result = await AskAsync(model, systemPrompt, [new ChatMessage("user", built.Prompt)], 350, ct);

            await SaveAsync(result, model, "quick", null, null, null, built, systemPrompt, null, ct);
            return _cached = result;
        }
        finally
        {
            _lock.Release();
        }
    }

    // Comprehensive analysis over an arbitrary date range — used by the Health page's AI Search tab.
    // Independent of the quick dashboard summary; does not touch the cached result.
    public async Task<InsightResult> AnalyzePeriodAsync(DateTime from, DateTime to, string periodLabel, bool fullDataset, CancellationToken ct = default)
    {
        if (!IsConfigured)
            return Fail("ChatGPT API key not configured. Add it in the Config page under Integrations.");

        var model = _config["OpenAi:Model"] ?? "gpt-5";
        var built = await InsightPrompts.BuildDeepPromptAsync(_db, Engine, from, to, periodLabel, fullDataset, ct);
        var systemPrompt = InsightPrompts.GetDeepSystemPrompt(_config);
        var result = await AskAsync(model, systemPrompt, [new ChatMessage("user", built.Prompt)], 1200, ct);

        await SaveAsync(result, model, "deep", periodLabel, from, to, built, systemPrompt, null, ct);
        return result;
    }

    // Multi-turn chat — the caller (AiInsightsOrchestrator) owns the running history; this
    // rebuilds the data digest fresh on every turn so a long-open chat always sees current
    // live data, not a stale snapshot from when the conversation started.
    public async Task<InsightResult> ChatAsync(IReadOnlyList<ChatMessage> history, CancellationToken ct)
    {
        if (!IsConfigured)
            return Fail("ChatGPT API key not configured. Add it in the Config page under Integrations.");

        var model = _config["OpenAi:Model"] ?? "gpt-5";
        var built = await InsightPrompts.BuildQuickPromptAsync(_db, _state, ct);
        var systemPrompt = InsightPrompts.GetChatSystemPrompt(built.Prompt);
        var result = await AskAsync(model, systemPrompt, history, 800, ct);

        // Unlike quick/deep, the live data digest here is embedded inside the system prompt
        // (see GetChatSystemPrompt) rather than sent as the user message — so the meaningful
        // "request" to record is the actual conversation turns, not built.Prompt again.
        var requestPrompt = string.Join("\n\n", history.Select(m => $"[{m.Role}] {m.Content}"));
        await SaveAsync(result, model, "chat", null, null, null, built, systemPrompt, requestPrompt, ct);
        return result;
    }

    private async Task<InsightResult> AskAsync(string model, string systemPrompt, IReadOnlyList<ChatMessage> messages, int maxTokens, CancellationToken ct)
    {
        try
        {
            var apiKey = _config["OpenAi:ApiKey"];

            var body = JsonSerializer.Serialize(new
            {
                model,
                max_completion_tokens = maxTokens,
                messages = new[] { new { role = "system", content = systemPrompt } }
                    .Concat(messages.Select(m => new { role = m.Role, content = m.Content }))
            });
            var bodyBytes = Encoding.UTF8.GetByteCount(body);

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
            req.Headers.Add("Authorization", $"Bearer {apiKey}");
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");

            var sw = Stopwatch.StartNew();
            var resp = await _http.SendAsync(req, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            sw.Stop();

            _logger.LogInformation(
                "OpenAI API call ({Model}): {RequestBytes} bytes sent, {ResponseBytes} bytes received, {ElapsedMs} ms elapsed",
                model, bodyBytes, Encoding.UTF8.GetByteCount(json), sw.ElapsedMilliseconds);

            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogError("OpenAI API {Status}: {Body}", resp.StatusCode, json);
                return Fail($"ChatGPT API returned {(int)resp.StatusCode}. Check your API key.");
            }

            using var doc = JsonDocument.Parse(json);
            var rawText = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "No response received.";

            var usage = doc.RootElement.GetProperty("usage");
            var inputTokens = usage.GetProperty("prompt_tokens").GetInt32();
            var outputTokens = usage.GetProperty("completion_tokens").GetInt32();

            var (text, statusLevel) = InsightPrompts.ExtractStatus(rawText);
            return new InsightResult(text, DateTime.Now, true, inputTokens, outputTokens, statusLevel);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OpenAiInsightsService request failed");
            return Fail($"Request failed: {ex.Message}");
        }
    }

    private async Task SaveAsync(
        InsightResult result, string model, string analysisType,
        string? periodLabel, DateTime? periodFrom, DateTime? periodTo,
        PromptBuildResult built, string systemPrompt, string? requestPromptOverride, CancellationToken ct)
    {
        if (!result.Success) return;

        try
        {
            var inputPrice = _config.GetValue<decimal?>("OpenAi:InputPricePerMillionUsd") ?? 0m;
            var outputPrice = _config.GetValue<decimal?>("OpenAi:OutputPricePerMillionUsd") ?? 0m;
            var cost = result.InputTokens / 1_000_000m * inputPrice + result.OutputTokens / 1_000_000m * outputPrice;

            await _db.SaveAiAnalysisAsync(new AiAnalysisRecord(
                null, result.GeneratedAt, Engine, model, analysisType,
                periodLabel, periodFrom, periodTo, true, result.Text,
                built.DataRowCount, built.SocPercent, built.PackVoltageV, built.CellDeltaMv,
                result.InputTokens, result.OutputTokens, cost, result.StatusLevel,
                systemPrompt, requestPromptOverride ?? built.Prompt), ct);

            // Chat is a live conversation, not a report — emailing every reply would be noise,
            // so only quick/deep analyses (the "did the AI produce a finding" kind) notify.
            if (analysisType != "chat")
                await _notifications.NotifyAiResponseAsync(Engine, analysisType, result, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save ChatGPT AI analysis to history");
        }
    }

    private static InsightResult Fail(string msg) => new(msg, DateTime.Now, false);
}
