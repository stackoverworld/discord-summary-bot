namespace DiscordSummaryBot;

public sealed class BotLogger
{
    private readonly LogLevel _minimumLevel;
    private readonly object _gate = new();

    public BotLogger(string minimumLevel)
    {
        _minimumLevel = Enum.TryParse<LogLevel>(minimumLevel, ignoreCase: true, out var parsed)
            ? parsed
            : LogLevel.Information;
    }

    public void Debug(string message) => Write(LogLevel.Debug, message);
    public void Info(string message) => Write(LogLevel.Information, message);
    public void Warn(string message) => Write(LogLevel.Warning, message);
    public void Error(string message) => Write(LogLevel.Error, message);
    public void Error(string message, Exception exception) => Write(LogLevel.Error, $"{message}\n{exception}");

    private void Write(LogLevel level, string message)
    {
        if (level < _minimumLevel)
            return;

        lock (_gate)
        {
            Console.WriteLine($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] {level.ToString().ToUpperInvariant()}: {message}");
        }
    }

    private enum LogLevel
    {
        Debug = 0,
        Information = 1,
        Warning = 2,
        Error = 3
    }
}
