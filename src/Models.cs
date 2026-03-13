namespace DiscordSummaryBot;

public sealed record TranscriptEntry(
    string Id,
    string SessionId,
    ulong UserId,
    string DisplayName,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    int DurationMs,
    string Text,
    string AudioFilePath);

public sealed record SummaryCheckpoint(
    string Id,
    DateTimeOffset CreatedAt,
    string ThroughEntryId,
    string Overview,
    IReadOnlyList<string> Decisions,
    IReadOnlyList<string> ActionItems,
    IReadOnlyList<string> Blockers);

public sealed record SummaryActionItem(
    string Task,
    string? Owner,
    string? Deadline);

public sealed record MeetingSummary(
    string Title,
    string Summary,
    IReadOnlyList<string> Decisions,
    IReadOnlyList<SummaryActionItem> ActionItems,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<string> OpenQuestions,
    IReadOnlyList<string> NotablePoints);

public sealed record PersistedSession(
    string Id,
    ulong GuildId,
    ulong VoiceChannelId,
    string VoiceChannelName,
    ulong SummaryChannelId,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    IReadOnlyList<TranscriptEntry> TranscriptEntries,
    IReadOnlyList<SummaryCheckpoint> Checkpoints,
    MeetingSummary? FinalSummary);
