using System.Threading;

namespace Wally.App;

/// <summary>
/// Guarantees one wally per Windows user session, whichever build (Debug,
/// Release, dist exe) is launched. The first instance owns a named mutex and
/// listens on a named event; a later launch signals that event so the running
/// instance shows its window, then exits without creating any UI.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    // Fixed names shared by every build. "Local\" scopes them to the current
    // logon session, so another signed-in user can still run their own wally.
    private const string MutexName = @"Local\wally-single-instance-7f3c2a91";
    private const string ShowEventName = @"Local\wally-show-window-7f3c2a91";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _showEvent;
    private RegisteredWaitHandle? _wait;

    private SingleInstance(Mutex mutex, EventWaitHandle showEvent)
    {
        _mutex = mutex;
        _showEvent = showEvent;
    }

    /// <summary>
    /// Returns the guard when this process is the first instance, or null when
    /// another wally is already running (that instance has been asked to show
    /// its window, and the caller should shut down).
    /// </summary>
    public static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);

        if (createdNew)
        {
            return new SingleInstance(mutex, showEvent);
        }

        // Another instance owns the mutex. This process was just launched by the
        // user, so it may hand foreground rights to the running instance; without
        // that, Windows only flashes its taskbar button instead of raising it.
        AllowSetForegroundWindow(AsfwAny);
        showEvent.Set();
        showEvent.Dispose();
        mutex.Dispose();
        return null;
    }

    private const int AsfwAny = -1;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    /// <summary>
    /// Runs <paramref name="onShowRequested"/> (on a thread-pool thread) each
    /// time a second launch asks this instance to show itself.
    /// </summary>
    public void ListenForShowRequests(Action onShowRequested)
    {
        _wait = ThreadPool.RegisterWaitForSingleObject(
            _showEvent,
            (_, _) => onShowRequested(),
            state: null,
            millisecondsTimeOutInterval: Timeout.Infinite,
            executeOnlyOnce: false);
    }

    public void Dispose()
    {
        _wait?.Unregister(null);
        _showEvent.Dispose();
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not owned by this thread (e.g. disposed from another thread);
            // the OS releases it when the process exits.
        }
        _mutex.Dispose();
    }
}
