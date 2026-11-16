using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace TattleTail.App;

/// <summary>
/// Makes sure only one TattleTail runs at a time.
/// </summary>
/// <remarks>
/// Windows allows only one event-tracing session per name, so a second copy would take over
/// the first copy's network and DNS feeds and the first would silently stop counting.
/// A named mutex works like an "occupied" sign: the first copy puts it up, and later copies
/// see it, ask the first copy to show its window (it may be hidden in the system tray), and exit. The name is in the Global
/// namespace because the event sessions are machine-wide, so a copy in another user's
/// Windows session would clash too.
/// </remarks>
internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = "TattleTail-SingleInstance-7c1e2a4b";

    // The doorbell a later copy rings to make the first copy show its window.
    private const string ShowEventName = "TattleTail-ShowWindow-7c1e2a4b";
    private static readonly string[] Namespaces = [@"Global\", @"Local\"];

    private readonly Mutex _mutex;
    private readonly EventWaitHandle? _showEvent;
    private readonly RegisteredWaitHandle? _showWait;

    private SingleInstance(Mutex mutex, string @namespace)
    {
        _mutex = mutex;
        try
        {
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @namespace + ShowEventName);
            _showWait = ThreadPool.RegisterWaitForSingleObject(
                _showEvent, (_, _) => ShowRequested?.Invoke(), null, Timeout.Infinite, executeOnlyOnce: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            // Without the doorbell a later copy falls back to finding the window itself.
        }
    }

    /// <summary>Another copy was started and wants this one's window shown. Raised on a background thread.</summary>
    public event Action? ShowRequested;

    /// <summary>Returns a guard if this is the only copy running, or null if another copy already is.</summary>
    public static SingleInstance? TryAcquire()
    {
        Mutex mutex;
        bool createdNew;
        var @namespace = Namespaces[0];
        try
        {
            mutex = new Mutex(initiallyOwned: true, @namespace + MutexName, out createdNew);
        }
        catch (UnauthorizedAccessException)
        {
            // Without rights to create global objects, fall back to this Windows session only.
            @namespace = Namespaces[1];
            mutex = new Mutex(initiallyOwned: true, @namespace + MutexName, out createdNew);
        }

        if (createdNew)
        {
            return new SingleInstance(mutex, @namespace);
        }

        mutex.Dispose();
        return null;
    }

    /// <summary>Asks the already-running copy to show its window, wherever it is.</summary>
    public static void ShowExistingWindow()
    {
        foreach (var @namespace in Namespaces)
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(@namespace + ShowEventName, out var showEvent))
                {
                    using (showEvent)
                    {
                        // Windows only lets the program you're using take the foreground; pass that right on.
                        AllowSetForegroundWindow(AsfwAny);
                        showEvent.Set();
                        return;
                    }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // Try the next namespace, then the fallback below.
            }
        }

        ActivateExistingWindow();
    }

    /// <summary>Fallback: finds the already-running copy's window and brings it to the front.</summary>
    private static void ActivateExistingWindow()
    {
        using var current = Process.GetCurrentProcess();
        foreach (var other in Process.GetProcessesByName(current.ProcessName))
        {
            using (other)
            {
                if (other.Id == current.Id)
                {
                    continue;
                }

                var window = other.MainWindowHandle;
                if (window == IntPtr.Zero)
                {
                    continue;
                }

                if (IsIconic(window))
                {
                    ShowWindow(window, SwRestore);
                }

                SetForegroundWindow(window);
                return;
            }
        }

        // The window wasn't found (e.g. it's still starting up); say so rather than vanish silently.
        MessageBox.Show("TattleTail is already running.", "TattleTail", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    public void Dispose()
    {
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not owned by this thread any more; disposing still closes it.
        }

        _mutex.Dispose();
        _showWait?.Unregister(null);
        _showEvent?.Dispose();
    }

    private const int SwRestore = 9;
    private const int AsfwAny = -1;

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);
}
