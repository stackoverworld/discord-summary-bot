using System.Collections.Concurrent;
using System.Text;
using NetCord;
using NetCord.Gateway;
using NetCord.Logging;
using NetCord.Gateway.Voice;
using NetCord.Rest;

namespace DiscordSummaryBot;

public sealed class VoiceSession
{
    private readonly AppConfig _config;
    private readonly GatewayClient _gatewayClient;
    private readonly Guild _guild;
    private readonly VoiceGuildChannel _voiceChannel;
    private readonly TextGuildChannel _summaryChannel;
    private readonly FileSessionStore _store;
    private readonly OpenAiClient _openAiClient;
    private readonly TaskQueue _transcriptionQueue;
    private readonly BotLogger _logger;
    private readonly Action<VoiceSession> _onEnded;
    private readonly ConcurrentDictionary<ulong, UserCaptureBuffer> _buffersByUserId = new();
    private readonly ConcurrentDictionary<uint, ulong> _ssrcToUserId = new();
    private readonly ConcurrentDictionary<uint, byte> _unmappedSsrcLog = new();
    private readonly object _stateGate = new();
    private readonly List<TranscriptEntry> _entries = [];
    private readonly List<SummaryCheckpoint> _checkpoints = [];
    private readonly object _sync = new();
    private readonly string _sessionId = Guid.NewGuid().ToString("D");
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private readonly PeriodicTimer _flushTimer = new(TimeSpan.FromMilliseconds(250));
    private readonly CancellationTokenSource _sessionCts = new();

    private readonly HashSet<Task> _pendingFinalizeTasks = [];
    private Task? _flushLoopTask;
    private VoiceClient? _voiceClient;
    private bool _ending;
    private Task? _endTask;
    private CancellationTokenSource? _scheduledStopCts;
    private int _lastCheckpointCount;
    private bool _checkpointInProgress;
    private MeetingSummary? _finalSummary;

    public VoiceSession(
        AppConfig config,
        GatewayClient gatewayClient,
        Guild guild,
        VoiceGuildChannel voiceChannel,
        TextGuildChannel summaryChannel,
        FileSessionStore store,
        OpenAiClient openAiClient,
        TaskQueue transcriptionQueue,
        BotLogger logger,
        Action<VoiceSession> onEnded)
    {
        _config = config;
        _gatewayClient = gatewayClient;
        _guild = guild;
        _voiceChannel = voiceChannel;
        _summaryChannel = summaryChannel;
        _store = store;
        _openAiClient = openAiClient;
        _transcriptionQueue = transcriptionQueue;
        _logger = logger;
        _onEnded = onEnded;
    }

    public ulong VoiceChannelId => _voiceChannel.Id;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var session = new PersistedSession(
            _sessionId,
            _guild.Id,
            _voiceChannel.Id,
            _voiceChannel.Name,
            _summaryChannel.Id,
            _startedAt,
            null,
            [],
            [],
            null);

        await _store.InitializeSessionAsync(session, cancellationToken);

        _voiceClient = await _gatewayClient.JoinVoiceChannelAsync(
            _guild.Id,
            _voiceChannel.Id,
            new VoiceClientConfiguration
            {
                ReceiveHandler = new VoiceReceiveHandler(),
                Logger = new ConsoleLogger(NetCord.Logging.LogLevel.Debug)
            },
            cancellationToken);

        var readyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _voiceClient.Ready += () =>
        {
            readyTcs.TrySetResult();
            return ValueTask.CompletedTask;
        };
        _voiceClient.Speaking += OnSpeakingAsync;
        _voiceClient.VoiceReceive += OnVoiceReceiveAsync;
        _voiceClient.UserDisconnect += args =>
        {
            FlushUser(args.UserId, DateTimeOffset.UtcNow);
            return ValueTask.CompletedTask;
        };

        await _voiceClient.StartAsync(cancellationToken);
        await _voiceClient.EnterSpeakingStateAsync(new SpeakingProperties(SpeakingFlags.Microphone), cancellationToken: cancellationToken);

        using var readyTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readyTimeoutCts.CancelAfter(_config.VoiceReadyTimeout);

        await readyTcs.Task.WaitAsync(readyTimeoutCts.Token);

        _flushLoopTask = RunFlushLoopAsync(_sessionCts.Token);
        _logger.Info($"Voice session started for '{_voiceChannel.Name}' ({_sessionId}).");
    }

    public void ScheduleStop()
    {
        if (_ending || _scheduledStopCts is not null)
            return;

        _scheduledStopCts = new CancellationTokenSource();
        var token = _scheduledStopCts.Token;
        _logger.Info($"Scheduled voice session shutdown for '{_voiceChannel.Name}' in {_config.SessionEndGrace.TotalSeconds:0}s.");

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_config.SessionEndGrace, token);
                await EndAsync("channel-empty", CancellationToken.None);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
        }, CancellationToken.None);
    }

    public void CancelScheduledStop()
    {
        _scheduledStopCts?.Cancel();
        _scheduledStopCts?.Dispose();
        _scheduledStopCts = null;
    }

    public Task EndAsync(string reason, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            _endTask ??= FinishAsync(reason, cancellationToken);
            return _endTask;
        }
    }

    public void DisposeWithoutFinalize()
    {
        _ending = true;
        CancelScheduledStop();
        _sessionCts.Cancel();
        _ = DisconnectFromVoiceAsync(CancellationToken.None);
        _voiceClient?.Dispose();
        _voiceClient = null;
        _onEnded(this);
    }

    private async Task FinishAsync(string reason, CancellationToken cancellationToken)
    {
        _ending = true;
        CancelScheduledStop();
        _logger.Info($"Finishing voice session '{_sessionId}' for reason '{reason}'.");

        _sessionCts.Cancel();

        foreach (var userId in _buffersByUserId.Keys.ToArray())
            FlushUser(userId, DateTimeOffset.UtcNow);

        if (_flushLoopTask is not null)
            await _flushLoopTask;

        Task[] pending;
        lock (_pendingFinalizeTasks)
            pending = _pendingFinalizeTasks.ToArray();

        await Task.WhenAll(pending);

        var finalCheckpointEntries = GetPendingCheckpointEntries();
        if (finalCheckpointEntries.Count > 0)
            await CreateCheckpointAsync(finalCheckpointEntries, cancellationToken);

        var endedAt = DateTimeOffset.UtcNow;
        var entriesSnapshot = GetEntriesSnapshot();
        if (entriesSnapshot.Length > 0)
        {
            var checkpointsSnapshot = GetCheckpointsSnapshot();
            var finalSummary = await _openAiClient.BuildFinalSummaryAsync(
                _voiceChannel.Name,
                _startedAt,
                endedAt,
                entriesSnapshot,
                checkpointsSnapshot,
                cancellationToken);

            SetFinalSummary(finalSummary);
            await _store.WriteSummaryMarkdownAsync(_sessionId, finalSummary, checkpointsSnapshot, cancellationToken);
            await PublishSummaryAsync(finalSummary, endedAt, cancellationToken);
        }
        else
        {
            await _summaryChannel.SendMessageAsync(new MessageProperties
            {
                Content = $"Сессия в канале **{_voiceChannel.Name}** завершилась, но распознаваемую речь сохранить не удалось."
            }, cancellationToken: cancellationToken);
        }

        await PersistSessionAsync(endedAt, cancellationToken);

        await DisconnectFromVoiceAsync(cancellationToken);
        _voiceClient?.Dispose();
        _voiceClient = null;
        _flushTimer.Dispose();
        _sessionCts.Dispose();
        _scheduledStopCts?.Dispose();
        _onEnded(this);
        _logger.Info($"Voice session finished for '{_voiceChannel.Name}' ({_sessionId}).");
    }

    private async ValueTask OnSpeakingAsync(SpeakingEventArgs args)
    {
        if (_ending)
            return;

        _ssrcToUserId[args.Ssrc] = args.UserId;
        _unmappedSsrcLog.TryRemove(args.Ssrc, out _);
        var displayName = ResolveDisplayName(args.UserId);
        _logger.Debug($"Speaking detected: {displayName} ({args.UserId})");
        await ValueTask.CompletedTask;
    }

    private ValueTask OnVoiceReceiveAsync(VoiceReceiveEventArgs args)
    {
        if (_ending)
            return ValueTask.CompletedTask;

        if (!TryResolveUserId(args.Ssrc, out var userId))
        {
            if (_unmappedSsrcLog.TryAdd(args.Ssrc, 0))
                _logger.Debug($"Dropping voice packet for unmapped SSRC {args.Ssrc}.");
            return ValueTask.CompletedTask;
        }

        if (args.Timestamp is null)
        {
            _logger.Debug($"Voice packet loss detected for SSRC {args.Ssrc}.");
            return ValueTask.CompletedTask;
        }

        var buffer = _buffersByUserId.GetOrAdd(userId, id => new UserCaptureBuffer(id, ResolveDisplayName(id)));
        buffer.WriteFrame(args.Frame, DateTimeOffset.UtcNow);
        return ValueTask.CompletedTask;
    }

    private async Task RunFlushLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _flushTimer.WaitForNextTickAsync(cancellationToken))
            {
                var now = DateTimeOffset.UtcNow;
                foreach (var pair in _buffersByUserId)
                {
                    if (now - pair.Value.LastFrameAt >= _config.UtteranceSilence)
                        FlushUser(pair.Key, now);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void FlushUser(ulong userId, DateTimeOffset endedAt)
    {
        if (!_buffersByUserId.TryGetValue(userId, out var buffer))
            return;

        if (!buffer.TryRotate(out var utterance))
            return;

        _logger.Debug(
            $"Flushing utterance for {utterance.DisplayName}: {utterance.FrameCount} frames, {utterance.PcmBytes.Length} bytes.");

        var task = FinalizeUtteranceAsync(utterance with { EndedAt = endedAt }, CancellationToken.None);
        lock (_pendingFinalizeTasks)
            _pendingFinalizeTasks.Add(task);

        _ = task.ContinueWith(_ =>
        {
            lock (_pendingFinalizeTasks)
                _pendingFinalizeTasks.Remove(task);
        }, TaskScheduler.Default);
    }

    private async Task FinalizeUtteranceAsync(UserUtterance utterance, CancellationToken cancellationToken)
    {
        if (utterance.PcmBytes.Length == 0)
        {
            _logger.Debug($"Skipping zero-byte utterance for {utterance.DisplayName} after {utterance.FrameCount} frames.");
            return;
        }

        var durationMs = (int)(utterance.PcmBytes.Length / 192.0);
        if (durationMs < _config.MinUtterance.TotalMilliseconds)
        {
            _logger.Debug($"Skipping short utterance for {utterance.DisplayName}: {durationMs}ms.");
            return;
        }

        var entryId = Guid.NewGuid().ToString("D");
        var safeTimestamp = utterance.StartedAt.UtcDateTime.ToString("yyyy-MM-ddTHH-mm-ss.fff");
        var fileName = $"{safeTimestamp}-{utterance.UserId}-{entryId}.wav";
        var filePath = Path.Combine(_store.GetAudioDirectoryPath(_sessionId), fileName);
        var persistedAudioPath = _store.GetAudioEntryPath(fileName).Replace('\\', '/');
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllBytesAsync(filePath, WavUtility.PcmToWav(utterance.PcmBytes), cancellationToken);

        await _transcriptionQueue.RunAsync(async () =>
        {
            var text = await _openAiClient.TranscribeAsync(filePath, utterance.DisplayName, cancellationToken);
            if (string.IsNullOrWhiteSpace(text))
            {
                _logger.Warn($"Transcription returned empty text for {utterance.DisplayName}.");
                return;
            }

            var entry = new TranscriptEntry(
                entryId,
                _sessionId,
                utterance.UserId,
                utterance.DisplayName,
                utterance.StartedAt,
                utterance.EndedAt,
                durationMs,
                text,
                persistedAudioPath);

            AddEntry(entry);

            await _store.WriteTranscriptMarkdownAsync(_sessionId, GetEntriesSnapshot(), cancellationToken);
            await PersistSessionAsync(null, cancellationToken);

            await TryCreateCheckpointAsync(cancellationToken);

            _logger.Info($"Transcription completed for {utterance.DisplayName}: {text.Length} chars.");
        }, cancellationToken);
    }

    private async Task CreateCheckpointAsync(IReadOnlyList<TranscriptEntry> recentEntries, CancellationToken cancellationToken)
    {
        if (recentEntries.Count == 0)
            return;

        var checkpointPayload = await _openAiClient.BuildCheckpointAsync(
            _voiceChannel.Name,
            GetCheckpointsSnapshot(),
            recentEntries,
            cancellationToken);

        var checkpoint = new SummaryCheckpoint(
            Guid.NewGuid().ToString("D"),
            DateTimeOffset.UtcNow,
            recentEntries[^1].Id,
            checkpointPayload.Overview,
            checkpointPayload.Decisions,
            checkpointPayload.ActionItems,
            checkpointPayload.Blockers);

        MarkCheckpointCompleted(checkpoint, recentEntries[^1].Id);
        await PersistSessionAsync(null, cancellationToken);
    }

    private async Task PublishSummaryAsync(
        MeetingSummary finalSummary,
        DateTimeOffset endedAt,
        CancellationToken cancellationToken)
    {
        var summaryText = Formatters.BuildDiscordSummary(
            _voiceChannel.Name,
            endedAt - _startedAt,
            finalSummary);
        var bodyLimit = Formatters.DiscordMessageCharacterLimit - 32;
        var chunks = Formatters.SplitForDiscordMessages(summaryText, bodyLimit);

        for (var index = 0; index < chunks.Count; index++)
        {
            var content = chunks.Count == 1
                ? chunks[index]
                : $"Часть {index + 1}/{chunks.Count}\n{chunks[index]}";

            await _summaryChannel.SendMessageAsync(new MessageProperties
            {
                Content = content
            }, cancellationToken: cancellationToken);
        }
    }

    private async Task PersistSessionAsync(DateTimeOffset? endedAt, CancellationToken cancellationToken)
    {
        TranscriptEntry[] entries;
        SummaryCheckpoint[] checkpoints;
        MeetingSummary? finalSummary;

        lock (_stateGate)
        {
            entries = _entries.ToArray();
            checkpoints = _checkpoints.ToArray();
            finalSummary = _finalSummary;
        }

        var session = new PersistedSession(
            _sessionId,
            _guild.Id,
            _voiceChannel.Id,
            _voiceChannel.Name,
            _summaryChannel.Id,
            _startedAt,
            endedAt,
            entries,
            checkpoints,
            finalSummary);

        await _store.WriteSessionAsync(session, cancellationToken);
    }

    private string ResolveDisplayName(ulong userId)
    {
        if (_guild.Users.TryGetValue(userId, out var user))
            return user.Nickname ?? user.GlobalName ?? user.Username;

        return userId.ToString();
    }

    private async Task DisconnectFromVoiceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _gatewayClient.UpdateVoiceStateAsync(
                new VoiceStateProperties(_guild.Id, null)
                    .WithChannelId(null)
                    .WithSelfMute(false)
                    .WithSelfDeaf(false),
                cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.Warn($"Failed to send explicit voice disconnect for '{_voiceChannel.Name}': {exception.Message}");
        }
    }

    private void AddEntry(TranscriptEntry entry)
    {
        lock (_stateGate)
            _entries.Add(entry);
    }

    private TranscriptEntry[] GetEntriesSnapshot()
    {
        lock (_stateGate)
            return _entries.ToArray();
    }

    private SummaryCheckpoint[] GetCheckpointsSnapshot()
    {
        lock (_stateGate)
            return _checkpoints.ToArray();
    }

    private List<TranscriptEntry> GetPendingCheckpointEntries()
    {
        lock (_stateGate)
        {
            if (_entries.Count <= _lastCheckpointCount)
                return [];

            return _entries.Skip(_lastCheckpointCount).ToList();
        }
    }

    private void SetFinalSummary(MeetingSummary summary)
    {
        lock (_stateGate)
            _finalSummary = summary;
    }

    private async Task TryCreateCheckpointAsync(CancellationToken cancellationToken)
    {
        List<TranscriptEntry>? recentEntries;

        lock (_stateGate)
        {
            if (_checkpointInProgress || _entries.Count - _lastCheckpointCount < _config.CheckpointIntervalUtterances)
                return;

            recentEntries = _entries.Skip(_lastCheckpointCount).ToList();
            _checkpointInProgress = true;
        }

        try
        {
            await CreateCheckpointAsync(recentEntries, cancellationToken);
        }
        finally
        {
            lock (_stateGate)
                _checkpointInProgress = false;
        }
    }

    private void MarkCheckpointCompleted(SummaryCheckpoint checkpoint, string throughEntryId)
    {
        lock (_stateGate)
        {
            _checkpoints.Add(checkpoint);

            var index = _entries.FindIndex(entry => entry.Id == throughEntryId);
            if (index >= 0)
                _lastCheckpointCount = Math.Max(_lastCheckpointCount, index + 1);
        }
    }

    private bool TryResolveUserId(uint ssrc, out ulong userId)
    {
        if (_voiceClient?.Cache.SsrcUsers.TryGetValue(ssrc, out var cachedUserId) is true)
        {
            _ssrcToUserId[ssrc] = cachedUserId;
            userId = cachedUserId;
            return true;
        }

        return _ssrcToUserId.TryGetValue(ssrc, out userId);
    }

    private sealed class UserCaptureBuffer
    {
        private readonly MemoryStream _pcmStream = new();
        private readonly OpusDecodeStream _decoder;
        private readonly object _sync = new();
        private DateTimeOffset? _startedAt;
        private int _frameCount;

        public UserCaptureBuffer(ulong userId, string displayName)
        {
            UserId = userId;
            DisplayName = displayName;
            _decoder = new OpusDecodeStream(_pcmStream, PcmFormat.Short, VoiceChannels.Stereo);
        }

        public ulong UserId { get; }
        public string DisplayName { get; }
        public DateTimeOffset LastFrameAt { get; private set; } = DateTimeOffset.UtcNow;

        public void WriteFrame(ReadOnlySpan<byte> frame, DateTimeOffset now)
        {
            lock (_sync)
            {
                _startedAt ??= now;
                _decoder.Write(frame);
                _frameCount++;
                LastFrameAt = now;
            }
        }

        public bool TryRotate(out UserUtterance utterance)
        {
            lock (_sync)
            {
                if (_startedAt is null || _pcmStream.Length == 0)
                {
                    utterance = default;
                    return false;
                }

                utterance = new UserUtterance(
                    UserId,
                    DisplayName,
                    _startedAt.Value,
                    LastFrameAt,
                    _frameCount,
                    _pcmStream.ToArray());

                _pcmStream.SetLength(0);
                _startedAt = null;
                _frameCount = 0;
                return true;
            }
        }
    }

    private readonly record struct UserUtterance(
        ulong UserId,
        string DisplayName,
        DateTimeOffset StartedAt,
        DateTimeOffset EndedAt,
        int FrameCount,
        byte[] PcmBytes);
}
