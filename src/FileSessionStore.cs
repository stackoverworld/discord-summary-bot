using System.Text.Json;

namespace DiscordSummaryBot;

public sealed class FileSessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _baseDirectoryPath;

    public FileSessionStore(string baseDirectoryPath)
    {
        _baseDirectoryPath = Path.GetFullPath(baseDirectoryPath);
    }

    public Task EnsureReadyAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_baseDirectoryPath);
        return Task.CompletedTask;
    }

    public string GetSessionDirectoryPath(string sessionId)
        => Path.Combine(_baseDirectoryPath, sessionId);

    public string GetAudioDirectoryPath(string sessionId)
        => Path.Combine(GetSessionDirectoryPath(sessionId), "audio");

    public string GetAudioEntryPath(string fileName)
        => Path.Combine("audio", fileName);

    public string GetSessionFilePath(string sessionId)
        => Path.Combine(GetSessionDirectoryPath(sessionId), "session.json");

    public string GetTranscriptFilePath(string sessionId)
        => Path.Combine(GetSessionDirectoryPath(sessionId), "transcript.md");

    public string GetSummaryFilePath(string sessionId)
        => Path.Combine(GetSessionDirectoryPath(sessionId), "summary.md");

    public async Task InitializeSessionAsync(PersistedSession session, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(GetAudioDirectoryPath(session.Id));
        await WriteSessionAsync(session, cancellationToken);
    }

    public async Task WriteSessionAsync(PersistedSession session, CancellationToken cancellationToken)
    {
        await using var stream = File.Create(GetSessionFilePath(session.Id));
        await JsonSerializer.SerializeAsync(stream, session, JsonOptions, cancellationToken);
    }

    public Task WriteTranscriptMarkdownAsync(string sessionId, IReadOnlyList<TranscriptEntry> entries, CancellationToken cancellationToken)
        => File.WriteAllTextAsync(GetTranscriptFilePath(sessionId), Formatters.BuildTranscriptMarkdown(sessionId, entries), cancellationToken);

    public Task WriteSummaryMarkdownAsync(
        string sessionId,
        MeetingSummary summary,
        IReadOnlyList<SummaryCheckpoint> checkpoints,
        CancellationToken cancellationToken)
        => File.WriteAllTextAsync(GetSummaryFilePath(sessionId), Formatters.BuildSummaryMarkdown(summary, checkpoints), cancellationToken);
}
