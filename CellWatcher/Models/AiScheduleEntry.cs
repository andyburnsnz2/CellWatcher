namespace CellWatcher.Models;

public sealed record AiScheduleEntry(
    long? AiScheduleId,
    bool RunClaudeQuick,
    bool RunClaudeDeep,
    bool RunChatGptQuick,
    bool RunChatGptDeep,
    string Frequency,       // 'daily' | 'weekly' | 'fortnightly' | 'monthly'
    TimeSpan TimeOfDay,
    int? DayOfWeek,          // 0=Sunday..6=Saturday, used when Frequency = 'weekly' or 'fortnightly'
    int? DayOfMonth,         // 1-31, used when Frequency = 'monthly'
    DateTime? LastRunAt);
