using NetCord;
using NetCord.Gateway;
using System.Collections.Concurrent;

namespace DiscordSummaryBot;

public sealed class VoiceSessionManager : IAsyncDisposable
{
    private readonly AppConfig _config;
    private readonly GatewayClient _gatewayClient;
    private readonly FileSessionStore _store;
    private readonly OpenAiClient _openAiClient;
    private readonly BotLogger _logger;
    private readonly TaskQueue _transcriptionQueue;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<ulong, VoiceSession> _sessionsByChannelId = new();
    private readonly Dictionary<ulong, DateTimeOffset> _startupCooldownByChannelId = new();

    public VoiceSessionManager(
        AppConfig config,
        GatewayClient gatewayClient,
        FileSessionStore store,
        OpenAiClient openAiClient,
        BotLogger logger)
    {
        _config = config;
        _gatewayClient = gatewayClient;
        _store = store;
        _openAiClient = openAiClient;
        _logger = logger;
        _transcriptionQueue = new TaskQueue(config.MaxTranscriptionConcurrency);
    }

    public async Task ReconcileAllAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            foreach (var channelId in _config.MonitoredVoiceChannelIds)
            {
                await ReconcileChannelAsync(channelId, cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task OnVoiceStateUpdateAsync(VoiceState _, CancellationToken cancellationToken)
        => await ReconcileAllAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        VoiceSession[] sessions;
        await _gate.WaitAsync();
        try
        {
            sessions = _sessionsByChannelId.Values.ToArray();
            foreach (var session in sessions)
                _sessionsByChannelId.TryRemove(session.VoiceChannelId, out _);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }

        foreach (var session in sessions)
            await session.EndAsync("shutdown", CancellationToken.None);
    }

    private async Task ReconcileChannelAsync(ulong channelId, CancellationToken cancellationToken)
    {
        if (!_gatewayClient.Cache.Guilds.TryGetValue(_config.DiscordGuildId, out var guild))
            return;

        if (!guild.Channels.TryGetValue(channelId, out var rawChannel) || rawChannel is not VoiceGuildChannel voiceChannel)
            return;

        var activeHumanVoiceStates = guild.VoiceStates.Values
            .Where(state => state.ChannelId == channelId)
            .Where(state => state.User is not null && !state.User.IsBot)
            .ToList();

        _sessionsByChannelId.TryGetValue(channelId, out var existingSession);
        if (activeHumanVoiceStates.Count > 0)
        {
            existingSession?.CancelScheduledStop();

            if (existingSession is null)
            {
                if (_startupCooldownByChannelId.TryGetValue(channelId, out var cooldownUntil) &&
                    cooldownUntil > DateTimeOffset.UtcNow)
                {
                    _logger.Info(
                        $"Пропускаю автоподключение к '{voiceChannel.Name}': действует cooldown до {cooldownUntil.ToLocalTime():HH:mm:ss}.");
                    return;
                }

                if (!guild.Channels.TryGetValue(_config.SummaryTextChannelId, out var summaryRawChannel) ||
                    summaryRawChannel is not TextGuildChannel summaryChannel)
                {
                    _logger.Error($"Summary text channel '{_config.SummaryTextChannelId}' is missing or is not a text channel.");
                    return;
                }

                var session = new VoiceSession(
                    _config,
                    _gatewayClient,
                    guild,
                    voiceChannel,
                    summaryChannel,
                    _store,
                    _openAiClient,
                    _transcriptionQueue,
                    _logger,
                    onEnded: finished => _sessionsByChannelId.TryRemove(finished.VoiceChannelId, out _));

                _sessionsByChannelId[channelId] = session;

                try
                {
                    _logger.Info($"Запускаю voice session для '{voiceChannel.Name}'.");
                    await session.StartAsync(cancellationToken);
                    _startupCooldownByChannelId.Remove(channelId);
                }
                catch (Exception exception)
                {
                    session.DisposeWithoutFinalize();
                    _sessionsByChannelId.TryRemove(channelId, out _);
                    var retryAt = DateTimeOffset.UtcNow.Add(_config.StartupRetryCooldown);
                    _startupCooldownByChannelId[channelId] = retryAt;
                    _logger.Error($"Voice session startup failed for channel '{voiceChannel.Name}'.", exception);

                    await summaryChannel.SendMessageAsync(new NetCord.Rest.MessageProperties
                    {
                        Content =
                            $"Я зашёл в **{voiceChannel.Name}**, но Discord voice capture так и не перешёл в рабочее состояние, поэтому записать аудио не удалось. Повторная попытка для этого канала будет не раньше чем через {(int)_config.StartupRetryCooldown.TotalSeconds} секунд."
                    }, cancellationToken: cancellationToken);
                }
            }

            return;
        }

        if (existingSession is not null)
            existingSession.ScheduleStop();
    }
}
