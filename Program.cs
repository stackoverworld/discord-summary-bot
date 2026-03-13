using DiscordSummaryBot;

DotEnv.Load();
var config = AppConfig.LoadFromEnvironment();
await using var app = new DiscordMeetingBotApp(config);

using var shutdownCts = new CancellationTokenSource();

Console.CancelKeyPress += (_, args) =>
{
    args.Cancel = true;
    shutdownCts.Cancel();
};

AppDomain.CurrentDomain.UnhandledException += (_, args) =>
{
    Console.Error.WriteLine($"[fatal] Unhandled exception: {args.ExceptionObject}");
};

try
{
    await app.RunAsync(shutdownCts.Token);
}
catch (OperationCanceledException) when (shutdownCts.IsCancellationRequested)
{
}
