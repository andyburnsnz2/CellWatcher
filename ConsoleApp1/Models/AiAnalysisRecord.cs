namespace BatteryEMU.Models;

public sealed record AiAnalysisRecord(
    long? AiAnalysisId,
    DateTime AnalysedAt,
    string Engine,
    string? EngineModel,
    string AnalysisType,
    string? PeriodLabel,
    DateTime? PeriodFrom,
    DateTime? PeriodTo,
    bool Success,
    string ResponseText,
    int? DataRowCount,
    decimal? SocPercentAtAnalysis,
    decimal? PackVoltageVAtAnalysis,
    decimal? CellDeltaMvAtAnalysis,
    int? InputTokens = null,
    int? OutputTokens = null,
    decimal? EstimatedCostUsd = null,
    string? StatusLevel = null,
    // What was actually sent to the AI for this call, captured alongside the response so
    // old rows stay reproducible even after the Config page's system prompt override changes.
    string? SystemPrompt = null,
    string? RequestPrompt = null);
