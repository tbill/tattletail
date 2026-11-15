using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace TattleTail.Core;

/// <summary>
/// Reads the list of open TCP and UDP sockets (the same data as <c>netstat -ano</c>)
/// using the Windows IP Helper API.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ConnectionTable
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int TcpTableOwnerPidAll = 5;
    private const int UdpTableOwnerPid = 1;
    private const uint NoError = 0;
    private const uint ErrorInsufficientBuffer = 122;

    // Row sizes of MIB_TCPROW_OWNER_PID, MIB_TCP6ROW_OWNER_PID, MIB_UDPROW_OWNER_PID, MIB_UDP6ROW_OWNER_PID.
    private const int Tcp4RowSize = 24;
    private const int Tcp6RowSize = 56;
    private const int Udp4RowSize = 12;
    private const int Udp6RowSize = 28;

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int ipVersion, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool sort, int ipVersion, int tableClass, uint reserved);

    public static List<ConnectionInfo> GetAll()
    {
        var result = new List<ConnectionInfo>();
        ReadTable(GetExtendedTcpTable, AfInet, TcpTableOwnerPidAll, Tcp4RowSize, result, ParseTcp4);
        ReadTable(GetExtendedTcpTable, AfInet6, TcpTableOwnerPidAll, Tcp6RowSize, result, ParseTcp6);
        ReadTable(GetExtendedUdpTable, AfInet, UdpTableOwnerPid, Udp4RowSize, result, ParseUdp4);
        ReadTable(GetExtendedUdpTable, AfInet6, UdpTableOwnerPid, Udp6RowSize, result, ParseUdp6);
        return result;
    }

    private delegate uint TableFunc(IntPtr table, ref int size, bool sort, int ipVersion, int tableClass, uint reserved);

    private static void ReadTable(TableFunc func, int family, int tableClass, int rowSize,
        List<ConnectionInfo> output, Func<IntPtr, ConnectionInfo> parseRow)
    {
        var size = 0;
        var buffer = IntPtr.Zero;
        try
        {
            // The table can grow between calls, so retry until the buffer is big enough.
            while (true)
            {
                var status = func(buffer, ref size, false, family, tableClass, 0);
                if (status == NoError)
                {
                    break;
                }

                if (status != ErrorInsufficientBuffer)
                {
                    throw new Win32Exception((int)status);
                }

                if (buffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(buffer);
                }

                buffer = Marshal.AllocHGlobal(size);
            }

            if (buffer == IntPtr.Zero)
            {
                return;
            }

            // Layout: DWORD dwNumEntries, followed by the rows.
            var count = Marshal.ReadInt32(buffer);
            var row = buffer + 4;
            for (var i = 0; i < count; i++)
            {
                output.Add(parseRow(row));
                row += rowSize;
            }
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    // MIB_TCPROW_OWNER_PID: state, localAddr, localPort, remoteAddr, remotePort, owningPid
    private static ConnectionInfo ParseTcp4(IntPtr row) => new(
        Protocol.Tcp,
        new IPEndPoint(ReadIPv4(row + 4), ReadPort(row + 8)),
        new IPEndPoint(ReadIPv4(row + 12), ReadPort(row + 16)),
        TcpStates.Describe(Marshal.ReadInt32(row)),
        Marshal.ReadInt32(row + 20));

    // MIB_TCP6ROW_OWNER_PID: localAddr[16], localScopeId, localPort, remoteAddr[16], remoteScopeId, remotePort, state, owningPid
    private static ConnectionInfo ParseTcp6(IntPtr row) => new(
        Protocol.Tcp,
        new IPEndPoint(ReadIPv6(row), ReadPort(row + 20)),
        new IPEndPoint(ReadIPv6(row + 24), ReadPort(row + 44)),
        TcpStates.Describe(Marshal.ReadInt32(row + 48)),
        Marshal.ReadInt32(row + 52));

    // MIB_UDPROW_OWNER_PID: localAddr, localPort, owningPid
    private static ConnectionInfo ParseUdp4(IntPtr row) => new(
        Protocol.Udp,
        new IPEndPoint(ReadIPv4(row), ReadPort(row + 4)),
        null,
        string.Empty,
        Marshal.ReadInt32(row + 8));

    // MIB_UDP6ROW_OWNER_PID: localAddr[16], localScopeId, localPort, owningPid
    private static ConnectionInfo ParseUdp6(IntPtr row) => new(
        Protocol.Udp,
        new IPEndPoint(ReadIPv6(row), ReadPort(row + 20)),
        null,
        string.Empty,
        Marshal.ReadInt32(row + 24));

    private static IPAddress ReadIPv4(IntPtr p)
    {
        var bytes = new byte[4];
        Marshal.Copy(p, bytes, 0, 4);
        return new IPAddress(bytes);
    }

    private static IPAddress ReadIPv6(IntPtr p)
    {
        var bytes = new byte[16];
        Marshal.Copy(p, bytes, 0, 16);
        return new IPAddress(bytes);
    }

    // Ports are stored in network byte order in the low 16 bits of a DWORD.
    private static int ReadPort(IntPtr p) => (Marshal.ReadByte(p) << 8) | Marshal.ReadByte(p + 1);
}

public static class TcpStates
{
    public static string Describe(int state) => state switch
    {
        1 => "Closed",
        2 => "Listen",
        3 => "SynSent",
        4 => "SynReceived",
        5 => "Established",
        6 => "FinWait1",
        7 => "FinWait2",
        8 => "CloseWait",
        9 => "Closing",
        10 => "LastAck",
        11 => "TimeWait",
        12 => "DeleteTcb",
        _ => $"Unknown ({state})",
    };
}
