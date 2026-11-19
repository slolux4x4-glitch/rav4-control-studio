using System.Windows.Threading;

namespace RAV4ControlStudio.Services;

/// <summary>
/// Runs an action once, a short time after the last request. Used so that typing does not trigger a search or a
/// settings save on every keystroke. Runs on the dispatcher it was created for (the UI thread).
/// </summary>
public sealed class Debouncer
{
    private readonly DispatcherTimer _timer;
    private readonly Action _action;

    public Debouncer(TimeSpan delay, Action action, Dispatcher dispatcher)
    {
        _action = action;
        _timer = new DispatcherTimer(delay, DispatcherPriority.Background, (_, _) => Run(), dispatcher);
        _timer.Stop();
    }

    /// <summary>True while a run is waiting for the delay to pass.</summary>
    public bool IsPending => _timer.IsEnabled;

    /// <summary>Starts (or restarts) the delay.</summary>
    public void Request()
    {
        _timer.Stop();
        _timer.Start();
    }

    /// <summary>Drops a waiting run.</summary>
    public void Cancel() => _timer.Stop();

    /// <summary>Runs straight away if a run is waiting (for example at exit).</summary>
    public void Flush()
    {
        if (IsPending) Run();
    }

    private void Run()
    {
        _timer.Stop();
        _action();
    }
}
