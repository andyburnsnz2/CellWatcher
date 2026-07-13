namespace CellWatcher.Models;

public sealed record ApplicationErrorRecord(
    long? ApplicationErrorId,
    DateTime OccurredAt,
    string Source,
    string Message,
    string? ExceptionType,
    string? StackTrace);
