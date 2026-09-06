namespace BLESerialTerminal;

internal sealed class SerialTaskQueue
{
    private readonly object _sync = new();
    private Task _tail = Task.CompletedTask;

    public Task Enqueue(Func<Task> work)
    {
        if (work == null)
            throw new ArgumentNullException(nameof(work));

        lock (_sync)
        {
            _tail = _tail.ContinueWith(
                    _ => work(),
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default)
                .Unwrap();
            return _tail;
        }
    }
}
