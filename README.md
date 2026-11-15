# TattleTail

**See which programs on your Windows PC are talking to the network, and how much.**

TattleTail lists every open TCP and UDP connection along with the program that owns it,
shows which websites they're talking to, and how much data goes each way: live, and as
history charts going back 30 days.

## Features

- **Programs tab:** one row per program, with its open connections, current upload and
  download speed, and total bytes sent and received since TattleTail started.
- **Traffic history chart:** under the Programs table. Uploads are drawn above the line and
  downloads below it. Pick a range from the last 10 minutes (second by second) to the last
  30 days. Click a program to chart just that program; hover the chart to read exact numbers.
- **Servers list:** next to the chart, every website or address the selected program (or all
  programs) talked to in that time range, with bytes sent and received and when it was last seen.
- **Website names, or IP addresses, or both:** pick with **Show servers as**. Hover any
  server to see both.
- **Connections tab:** every TCP/UDP socket with local address, remote server, port, state,
  speed and totals. It shows the same sockets as `netstat -ano`, plus names and traffic.
- Filter box to search by program name, PID, IP address, port or state.
- Click any column header to sort. Rows re-sort live as the numbers change.
- Hover a row to see the program's full path.
- Programs that exit stay in the list, greyed out, so you can still see what they did.
  Untick **Show exited programs** to hide them.
- **Reset counters** sets every total back to zero.

## Download

Go to [Releases](../../releases) and download one of these:

| File | Size | Needs |
|---|---|---|
| `TattleTail-vX.Y.Z-win-x64.exe` | ~75 MB | Nothing, just run it |
| `TattleTail-vX.Y.Z-win-x64-requires-dotnet.zip` | ~6 MB | [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) |

Windows 10 or 11 (64-bit) is required.

> **Why does it ask for administrator rights?** Listing connections doesn't need them.
> Counting traffic does: TattleTail reads the Windows kernel's network events
> (Event Tracing for Windows, the same source Resource Monitor uses), and Windows only
> allows administrators to read those. TattleTail only reads data. It never blocks,
> changes or sends any traffic.

> **SmartScreen warning?** The releases aren't code-signed yet, so Windows may show
> "Windows protected your PC". Click **More info → Run anyway**, or build it yourself
> from the source below.

## Where the website names come from

1. **Watching DNS lookups.** Before a program connects to `github.com`, it asks Windows to
   look up the address. TattleTail listens to those lookups (the Windows DNS client's event
   log), so it knows the connection to `140.82.112.3` is really `github.com`. This is the most
   accurate name, because it's the one the program actually asked for. Many websites share one
   address (CDNs), so TattleTail remembers which program looked up which name.
2. **Reverse DNS, as a fallback.** Connections opened before TattleTail started, and browsers
   using their own encrypted DNS ("secure DNS"), skip step 1. For those, TattleTail asks DNS
   "what's the name of this address?". Answers are often server names like
   `lb-140-82-112-3-iad.github.com` rather than the friendly site name. Untick **Reverse DNS**
   if you don't want TattleTail to send any lookups of its own.

## Where history is kept

Traffic history is saved once a minute to `%LOCALAPPDATA%\TattleTail\history.db` (a SQLite
file) and kept for 30 days. Settings are in `settings.json` in the same folder. Nothing is
ever sent anywhere. **Clear history…** deletes it all.

## How it works

| Piece | What it does |
|---|---|
| `ConnectionTable` | Calls `GetExtendedTcpTable` / `GetExtendedUdpTable` (IP Helper API) to list sockets and their owning process IDs. |
| `EtwTrafficMonitor` | Starts a real-time kernel ETW session (`NetworkTCPIP` keyword) using [TraceEvent](https://www.nuget.org/packages/Microsoft.Diagnostics.Tracing.TraceEvent) and receives an event for every TCP/UDP send and receive. |
| `TrafficAggregator` | Thread-safe running totals per connection, per process, and per process + remote address. |
| `EtwDnsMonitor` / `HostNameCache` | Listens to `Microsoft-Windows-DNS-Client` event 3008 (query completed) to map addresses to names, with reverse DNS as a fallback. |
| `TrafficHistory` / `HistoryStore` | Keeps the last 10 minutes per second in memory and writes per-minute, per-program, per-server rows to SQLite. |
| `MainViewModel` | Once a second, merges the socket list with a traffic snapshot, records history and works out rates. |

TCP traffic is matched to a connection by its exact local and remote address and port.
UDP sockets usually listen on every address (`0.0.0.0`) and talk to many peers, so UDP
traffic is matched by program and local port.

## Building from source

You need the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). Visual Studio 2022
(Community edition is free) or VS Code with the C# Dev Kit extension both work.

```bash
git clone https://github.com/tbill/tattletail.git
```

```bash
cd tattletail
```

```bash
dotnet build
```

```bash
dotnet test
```

To run it, open `TattleTail.sln` in Visual Studio and press **F5**. Visual Studio will offer
to restart itself as administrator, because the app requires it.

To make a standalone `.exe`:

```bash
dotnet publish src/TattleTail.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
```

### Project layout

```
src/TattleTail.Core/        Connection table, ETW listener, traffic counting (no UI)
src/TattleTail.App/         WPF desktop app
tests/TattleTail.Core.Tests xUnit tests for the counting logic
```

## Releasing

Push a version tag and GitHub Actions builds both downloads and attaches them to a release:

```bash
git tag v0.1.0
```

```bash
git push origin v0.1.0
```

## Testing on Windows

Besides the unit tests, CI launches the real app on a Windows machine with
`TattleTail.exe --smoke-test report.json 25`, makes a few web requests while it runs, and
checks the report shows connections, traffic, website names and history.

## Ideas for later

- Group servers by domain (e.g. all `*.googlevideo.com` hosts under `googlevideo.com`)
- Country and a world map for remote servers
- Show whether each program is digitally signed, and by whom
- System-tray icon with an alert when a new program connects
- Export to CSV

## License

[MIT](LICENSE)
