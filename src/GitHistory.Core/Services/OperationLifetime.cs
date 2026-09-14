namespace GitHistory.Core.Services;

/// <summary>Tracks every operation until shutdown, including superseded requests.</summary>
internal sealed class OperationLifetime : IDisposable
{
    private readonly object gate = new();
    private readonly HashSet<Task> pending = [];
    private readonly CancellationTokenSource cancellation = new();
    private bool closed;
    private Task? shutdown;
    public CancellationToken Token => cancellation.Token;
    public bool IsClosed => closed;
    public Task Run(Func<CancellationToken, Task> action)
    {
        lock (gate)
        {
            if (closed) return Task.CompletedTask;
            Task task = action(cancellation.Token);
            if (!task.IsCompleted) { pending.Add(task); _ = ObserveAsync(task); }
            return task;
        }
    }
    private async Task ObserveAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception) { /* Each view model reports recoverable errors. */ }
        finally { lock (gate) pending.Remove(task); }
    }
    public Task ShutdownAsync()
    {
        lock (gate)
        {
            if (shutdown is not null) return shutdown;
            Dispose();
            return shutdown = DrainAsync(pending.ToArray());
        }
    }
    private async Task DrainAsync(Task[] tasks)
    {
        try { await Task.WhenAll(tasks); }
        finally { cancellation.Dispose(); }
    }
    public void Dispose()
    {
        lock (gate) { if (closed) return; closed = true; cancellation.Cancel(); }
    }
}
