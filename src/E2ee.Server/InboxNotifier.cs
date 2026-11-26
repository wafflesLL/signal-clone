using System.Collections.Concurrent;

namespace E2ee.Server;

/// <summary>
/// Wakes long-poll requests (GET /v1/messages/wait) when mail arrives for a user.
/// Waiters register before checking the inbox, so a message that lands in between is not missed.
/// </summary>
public sealed class InboxNotifier
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _signals = new();

    /// <summary>A task that completes the next time <see cref="Notify"/> is called for the user.</summary>
    public Task NextAsync(string user) =>
        _signals.GetOrAdd(user, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

    public void Notify(string user)
    {
        if (_signals.TryRemove(user, out var signal)) signal.TrySetResult();
    }
}
