namespace VigilWatt.Core.Devices;

/// <summary>
/// 功 能： 为单个监控所有者保留唯一在途任务，限时等待不释放所有权。
/// 2026/10/20 20:59:22 CodexSandboxOffline
/// </summary>
public sealed class MonitorWorkSlot
{
    private readonly object _sync = new();
    private Task _current = Task.CompletedTask;
    private bool _stopped;

    public Task Run(Func<Task> work)
    {
        lock (_sync)
        {
            if (_stopped) return Task.CompletedTask;
            if (!_current.IsCompleted) return _current;
            _current = Task.Run(work);
            return _current;
        }
    }

    public Task Stop()
    {
        lock (_sync)
        {
            _stopped = true;
            return _current;
        }
    }

    public static async Task<bool> ObserveAsync(IEnumerable<Task> work, TimeSpan budget)
    {
        var completion = Task.WhenAll(work);
        try
        {
            await completion.WaitAsync(budget).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            // 超时仅结束调用方等待；旧任务仍由各 slot 持有，不得另开同一所有者。
            _ = completion.ContinueWith(task => _ = task.Exception,
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            return false;
        }
    }
}
