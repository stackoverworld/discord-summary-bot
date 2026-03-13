using NetCord;
using NetCord.Gateway;

namespace DiscordSummaryBot;

public sealed class DiscordMeetingBotApp : IAsyncDisposable
{
    private readonly AppConfig _config;
    private readonly BotLogger _logger;
    private readonly HttpClient _summaryHttpClient;
    private readonly HttpClient _transcriptionHttpClient;
    private readonly FileSessionStore _store;
    private readonly OpenAiClient _openAiClient;
    private readonly GatewayClient _gatewayClient;
    private readonly VoiceSessionManager _voiceSessionManager;

    public DiscordMeetingBotApp(AppConfig config)
    {
        _config = config;
        _logger = new BotLogger(config.LogLevel);
        _summaryHttpClient = new HttpClient
        {
            BaseAddress = new Uri("https://openrouter.ai/api/v1/")
        };
        _summaryHttpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.OpenRouterApiKey);
        if (!string.IsNullOrWhiteSpace(config.OpenRouterHttpReferer))
            _summaryHttpClient.DefaultRequestHeaders.TryAddWithoutValidation("HTTP-Referer", config.OpenRouterHttpReferer);
        if (!string.IsNullOrWhiteSpace(config.OpenRouterAppName))
            _summaryHttpClient.DefaultRequestHeaders.TryAddWithoutValidation("X-Title", config.OpenRouterAppName);

        _transcriptionHttpClient = new HttpClient
        {
            BaseAddress = new Uri(config.TranscriptionApiBaseUrl, UriKind.Absolute)
        };
        if (!string.IsNullOrWhiteSpace(config.TranscriptionApiKey))
        {
            _transcriptionHttpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.TranscriptionApiKey);
        }

        _store = new FileSessionStore(config.DataDirectoryPath);
        _openAiClient = new OpenAiClient(_summaryHttpClient, _transcriptionHttpClient, config, _logger);

        _gatewayClient = new GatewayClient(
            new BotToken(config.DiscordBotToken),
            new GatewayClientConfiguration
            {
                Intents = GatewayIntents.Guilds | GatewayIntents.GuildVoiceStates | GatewayIntents.GuildUsers
            });

        _voiceSessionManager = new VoiceSessionManager(
            _config,
            _gatewayClient,
            _store,
            _openAiClient,
            _logger);

        _gatewayClient.Ready += OnReadyAsync;
        _gatewayClient.VoiceStateUpdate += OnVoiceStateUpdateAsync;
        _gatewayClient.Disconnect += args =>
        {
            _logger.Warn($"Gateway disconnected: {args}");
            return ValueTask.CompletedTask;
        };
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await _store.EnsureReadyAsync(cancellationToken);
        await _gatewayClient.StartAsync(cancellationToken: cancellationToken);

        using var timer = new PeriodicTimer(_config.VoiceReconcileInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await _voiceSessionManager.ReconcileAllAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            await _gatewayClient.CloseAsync(
                System.Net.WebSockets.WebSocketCloseStatus.NormalClosure,
                "Shutting down",
                CancellationToken.None);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _voiceSessionManager.DisposeAsync();
        _gatewayClient.Dispose();
        _summaryHttpClient.Dispose();
        _transcriptionHttpClient.Dispose();
    }

    private async ValueTask OnReadyAsync(ReadyEventArgs args)
    {
        _logger.Info($"Discord bot is ready as '{args.User.Username}'.");
        await _voiceSessionManager.ReconcileAllAsync(CancellationToken.None);
    }

    private async ValueTask OnVoiceStateUpdateAsync(VoiceState state)
    {
        await _voiceSessionManager.OnVoiceStateUpdateAsync(state, CancellationToken.None);
    }
}
