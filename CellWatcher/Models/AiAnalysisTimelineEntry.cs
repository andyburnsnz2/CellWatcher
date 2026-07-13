namespace CellWatcher.Models;

// A compact one-line-per-report view across the AI's entire analysis history — date, status,
// and a short gist — as opposed to AiAnalysisRecord's full response text. Lets a deep analysis
// see how its own past judgment has evolved (e.g. "flagged WATCH for three weeks straight")
// without paying the token cost of including many full past reports verbatim.
public sealed record AiAnalysisTimelineEntry(
    DateTime AnalysedAt,
    string? PeriodLabel,
    string? StatusLevel,
    string ResponseGist);
