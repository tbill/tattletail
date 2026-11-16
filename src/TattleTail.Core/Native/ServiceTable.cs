using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace TattleTail.Core;

/// <summary>A running Windows service and the process hosting it.</summary>
/// <param name="Name">Short internal name, e.g. "wuauserv".</param>
/// <param name="DisplayName">Friendly name, e.g. "Windows Update".</param>
public sealed record ServiceInfo(string Name, string DisplayName, int ProcessId);

/// <summary>
/// Lists running Windows services and which process each one lives in (the same data as
/// <c>tasklist /svc</c>), using the Service Control Manager.
/// </summary>
/// <remarks>
/// Many Windows services don't have a program of their own: they run inside a generic host,
/// <c>svchost.exe</c>, like shops inside one shopping centre that all share its street address.
/// This is the centre's directory: it says which shop is behind which door (process id).
/// </remarks>
[SupportedOSPlatform("windows")]
public static class ServiceTable
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ScManagerEnumerateService = 0x0004;
    private const int ScEnumProcessInfo = 0;
    private const uint ServiceWin32 = 0x00000030;
    private const uint ServiceActive = 0x00000001;
    private const int ErrorMoreData = 234;

    private static readonly TimeSpan CacheTime = TimeSpan.FromSeconds(2);
    private static readonly object Gate = new();
    private static List<ServiceInfo> _cached = [];
    private static long _cachedAtTicks = long.MinValue;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct EnumServiceStatusProcess
    {
        public IntPtr ServiceName;
        public IntPtr DisplayName;
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManagerW(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumServicesStatusExW(IntPtr scManager, int infoLevel, uint serviceType, uint serviceState,
        IntPtr services, int bufferSize, out int bytesNeeded, out int servicesReturned, ref uint resumeHandle, string? groupName);

    /// <summary>
    /// Services running in <paramref name="processId"/>. Looking up many processes in a row
    /// (as happens when TattleTail starts) reuses one reading of the list.
    /// </summary>
    public static List<ServiceInfo> GetForProcess(int processId)
    {
        List<ServiceInfo> all;
        lock (Gate)
        {
            if (Environment.TickCount64 - _cachedAtTicks > CacheTime.TotalMilliseconds || _cachedAtTicks == long.MinValue)
            {
                _cached = GetAll();
                _cachedAtTicks = Environment.TickCount64;
            }

            all = _cached;
        }

        return all.Where(s => s.ProcessId == processId).ToList();
    }

    public static List<ServiceInfo> GetAll()
    {
        var manager = OpenSCManagerW(null, null, ScManagerConnect | ScManagerEnumerateService);
        if (manager == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var result = new List<ServiceInfo>();
        var buffer = IntPtr.Zero;
        try
        {
            uint resume = 0;
            var size = 64 * 1024;
            var rowSize = Marshal.SizeOf<EnumServiceStatusProcess>();

            // Each call fills the buffer with as many services as fit and says whether more remain.
            while (true)
            {
                buffer = Marshal.AllocHGlobal(size);
                var ok = EnumServicesStatusExW(manager, ScEnumProcessInfo, ServiceWin32, ServiceActive,
                    buffer, size, out var needed, out var returned, ref resume, null);
                var error = ok ? 0 : Marshal.GetLastWin32Error();
                if (!ok && error != ErrorMoreData)
                {
                    throw new Win32Exception(error);
                }

                for (var i = 0; i < returned; i++)
                {
                    var row = Marshal.PtrToStructure<EnumServiceStatusProcess>(buffer + i * rowSize);
                    var name = Marshal.PtrToStringUni(row.ServiceName) ?? string.Empty;
                    var display = Marshal.PtrToStringUni(row.DisplayName);
                    if (row.ProcessId != 0 && name.Length > 0)
                    {
                        result.Add(new ServiceInfo(name, string.IsNullOrWhiteSpace(display) ? name : display, (int)row.ProcessId));
                    }
                }

                Marshal.FreeHGlobal(buffer);
                buffer = IntPtr.Zero;
                if (ok)
                {
                    break;
                }

                size = Math.Max(size, needed);
            }
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }

            CloseServiceHandle(manager);
        }

        return result;
    }
}
