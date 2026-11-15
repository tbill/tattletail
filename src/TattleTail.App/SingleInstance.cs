using System.Diagnostics;
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
/// see it, bring the existing window to the front, and exit. The name is in the Global
/// namespace because the event sessions are machine-wide, so a copy in another user's
/// Windows session would clash too.
/// </remarks>
internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = "TattleTail-SingleInstance-7c1e2a4b";

    private readonly Mutex _mutex;

    private SingleInstance(Mutex mutex)
    {
        _mutex = mutex;
    }

    /// <summary>Returns a guard if this is the only copy running, or null if another copy already is.</summary>
    public static SingleInstance? TryAcquire()
    {
        Mutex mutex;
        bool createdNew;
        try
        {
            mutex = new Mutex(initiallyOwned: true, @"Global\" + MutexName, out createdNew);
        }
        catch (UnauthorizedAccessException)
        {
            // Without rights to create global objects, fall back to this Windows session only.
            mutex = new Mutex(initiallyOwned: true, @"Local\" + MutexName, out createdNew);
        }

        if (createdNew)
        {
            return new SingleInstance(mutex);
        }

        mutex.Dispose();
        return null;
    }

    /// <summary>Brings the already-running copy's window to the front.</summary>
    public static void ActivateExistingWindow()
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
    }

    private const int SwRestore = 9;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);
}
