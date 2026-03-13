namespace DiscordSummaryBot;

public sealed class TaskQueue
{
    private readonly SemaphoreSlim _semaphore;

    public TaskQueue(int concurrency)
    {
        _semaphore = new SemaphoreSlim(Math.Max(1, concurrency), Math.Max(1, concurrency));
    }

    public async Task RunAsync(Func<Task> operation, CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            await operation();
        }
        finally
        {
            _semaphore.Release();
        }
    }
}
