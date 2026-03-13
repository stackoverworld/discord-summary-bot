using System.Globalization;

namespace DiscordSummaryBot;

public sealed class AppConfig
{
    public required string DiscordBotToken { get; init; }
    public required ulong DiscordGuildId { get; init; }
    public required IReadOnlySet<ulong> MonitoredVoiceChannelIds { get; init; }
    public required ulong SummaryTextChannelId { get; init; }
    public required string OpenRouterApiKey { get; init; }
    public required string OpenRouterModel { get; init; }
    public required double OpenRouterTemperature { get; init; }
    public required string? OpenRouterHttpReferer { get; init; }
    public required string? OpenRouterAppName { get; init; }
    public required string TranscriptionApiBaseUrl { get; init; }
    public required string? TranscriptionApiKey { get; init; }
    public required string TranscriptionModel { get; init; }
    public required string? TranscriptionLanguage { get; init; }
    public required TimeSpan VoiceReadyTimeout { get; init; }
    public required TimeSpan SessionEndGrace { get; init; }
    public required TimeSpan VoiceReconcileInterval { get; init; }
    public required TimeSpan UtteranceSilence { get; init; }
    public required TimeSpan MinUtterance { get; init; }
    public required TimeSpan StartupRetryCooldown { get; init; }
    public required int CheckpointIntervalUtterances { get; init; }
    public required int MaxTranscriptionConcurrency { get; init; }
    public required string DataDirectoryPath { get; init; }
    public required string LogLevel { get; init; }

    public static AppConfig LoadFromEnvironment()
    {
        static string Require(string name)
            => Environment.GetEnvironmentVariable(name) switch
            {
                { Length: > 0 } value => value,
                _ => throw new InvalidOperationException($"Missing required environment variable '{name}'."),
            };

        static string Optional(string name, string defaultValue)
            => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : defaultValue;

        static string? OptionalNullable(params string[] names)
        {
            foreach (var name in names)
            {
                if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
                    return value;
            }

            return null;
        }

        static string RequireAny(params string[] names)
        {
            foreach (var name in names)
            {
                if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
                    return value;
            }

            throw new InvalidOperationException(
                $"Missing required environment variable. Expected one of: {string.Join(", ", names.Select(name => $"'{name}'"))}.");
        }

        static string OptionalAny(string defaultValue, params string[] names)
        {
            foreach (var name in names)
            {
                if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
                    return value;
            }

            return defaultValue;
        }

        static string EnsureTrailingSlash(string value)
            => value.EndsWith("/", StringComparison.Ordinal) ? value : $"{value}/";

        static ulong ParseUlong(string name)
            => ulong.Parse(Require(name), CultureInfo.InvariantCulture);

        static int ParseInt(string name, int defaultValue)
            => int.Parse(Optional(name, defaultValue.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);

        static double ParseDouble(string name, double defaultValue)
            => double.Parse(Optional(name, defaultValue.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);

        var channelIds = Require("MONITORED_VOICE_CHANNEL_IDS")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => ulong.Parse(value, CultureInfo.InvariantCulture))
            .ToHashSet();

        if (channelIds.Count == 0)
            throw new InvalidOperationException("MONITORED_VOICE_CHANNEL_IDS must contain at least one voice channel id.");

        return new AppConfig
        {
            DiscordBotToken = Require("DISCORD_BOT_TOKEN"),
            DiscordGuildId = ParseUlong("DISCORD_GUILD_ID"),
            MonitoredVoiceChannelIds = channelIds,
            SummaryTextChannelId = ParseUlong("SUMMARY_TEXT_CHANNEL_ID"),
            OpenRouterApiKey = RequireAny("OPENROUTER_API_KEY", "OPENAI_API_KEY"),
            OpenRouterModel = OptionalAny("openrouter/free", "OPENROUTER_MODEL", "OPENAI_SUMMARY_MODEL"),
            OpenRouterTemperature = ParseDouble("OPENROUTER_TEMPERATURE", 0.2),
            OpenRouterHttpReferer = OptionalNullable("OPENROUTER_HTTP_REFERER"),
            OpenRouterAppName = OptionalNullable("OPENROUTER_APP_NAME"),
            TranscriptionApiBaseUrl = EnsureTrailingSlash(OptionalAny("http://localhost:8000/v1/", "TRANSCRIPTION_API_BASE_URL")),
            TranscriptionApiKey = OptionalNullable("TRANSCRIPTION_API_KEY"),
            TranscriptionModel = OptionalAny(
                "Systran/faster-distil-whisper-large-v3",
                "TRANSCRIPTION_MODEL",
                "OPENAI_TRANSCRIPTION_MODEL"),
            TranscriptionLanguage = OptionalNullable("TRANSCRIPTION_LANGUAGE"),
            VoiceReadyTimeout = TimeSpan.FromMilliseconds(ParseInt("VOICE_READY_TIMEOUT_MS", 15000)),
            SessionEndGrace = TimeSpan.FromMilliseconds(ParseInt("SESSION_END_GRACE_MS", 30000)),
            VoiceReconcileInterval = TimeSpan.FromMilliseconds(ParseInt("VOICE_RECONCILE_INTERVAL_MS", 10000)),
            UtteranceSilence = TimeSpan.FromMilliseconds(ParseInt("UTTERANCE_SILENCE_MS", 1500)),
            MinUtterance = TimeSpan.FromMilliseconds(ParseInt("MIN_UTTERANCE_MS", 1200)),
            StartupRetryCooldown = TimeSpan.FromMilliseconds(ParseInt("STARTUP_RETRY_COOLDOWN_MS", 15000)),
            CheckpointIntervalUtterances = ParseInt("CHECKPOINT_INTERVAL_UTTERANCES", 24),
            MaxTranscriptionConcurrency = ParseInt("MAX_TRANSCRIPTION_CONCURRENCY", 2),
            DataDirectoryPath = Optional("DATA_DIR", "./data"),
            LogLevel = Optional("LOG_LEVEL", "Information")
        };
    }
}
