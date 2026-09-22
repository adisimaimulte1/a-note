namespace ANote.Services;

public sealed class DebouncedAutosave : IDisposable
{
    private readonly Func<Task> _save;
    private readonly TimeSpan _delay;
    private CancellationTokenSource? _cts;

    public DebouncedAutosave(Func<Task> save, TimeSpan? delay = null)
    {
        _save = save;
        _delay = delay ?? TimeSpan.FromMilliseconds(650);
    }

    public void Request()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        var cts = _cts = new CancellationTokenSource();
        _ = RunAsync(cts.Token);
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(_delay, token);
            await _save();
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
