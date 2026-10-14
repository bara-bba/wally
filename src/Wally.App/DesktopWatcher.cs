using System.Windows.Threading;
using Wally.Platform;

namespace Wally.App;

/// <summary>
/// Polls the current virtual-desktop GUID and raises <see cref="DesktopChanged"/>
/// when it changes. Windows' documented virtual-desktop API has no change
/// notification, so a light poll (default ~700ms) is the stable way to detect a
/// switch without the undocumented notification COM. The poll is cheap: one COM
/// call that returns a GUID.
/// </summary>
public sealed class DesktopWatcher : IDisposable
{
    private readonly VirtualDesktopService _service;
    private readonly DispatcherTimer _timer;
    private string _lastGuid = string.Empty;
    private bool _disposed;

    /// <summary>Raised on the UI thread when the current desktop GUID changes.
    /// The argument is the new current desktop GUID (never empty).</summary>
    public event Action<string>? DesktopChanged;

    public DesktopWatcher(VirtualDesktopService service, int intervalMs = 1200)
    {
        _service = service;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(intervalMs) };
        _timer.Tick += (_, _) => Poll();
    }

    /// <summary>The last known current desktop GUID (empty until first resolved).</summary>
    public string CurrentGuid => _lastGuid;

    public void Start()
    {
        // Seed the current guid WITHOUT raising the event, so startup auto-apply
        // is driven explicitly by the caller rather than as a phantom "change".
        _lastGuid = _service.CurrentDesktopGuid();
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    private void Poll()
    {
        var guid = _service.CurrentDesktopGuid();
        if (string.IsNullOrEmpty(guid) || guid == _lastGuid)
        {
            return;
        }

        _lastGuid = guid;
        DesktopChanged?.Invoke(guid);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _timer.Stop();
        _disposed = true;
    }
}
