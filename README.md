# TattleTail

**See which programs on your Windows PC are talking to the network, and how much.**

TattleTail lists every open TCP and UDP connection along with the program that owns it.
It also shows how many bytes each program and each connection has sent and received,
updated every second.

## Features

- **Programs tab:** one row per program, with its open connections, current upload and
  download speed, and total bytes sent and received since TattleTail started.
- **Connections tab:** every TCP/UDP socket with local and remote address, state,
  speed and totals. It shows the same sockets as `netstat -ano`, plus the traffic counts.
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

## How it works

| Piece | What it does |
|---|---|
| `ConnectionTable` | Calls `GetExtendedTcpTable` / `GetExtendedUdpTable` (IP Helper API) to list sockets and their owning process IDs. |
| `EtwTrafficMonitor` | Starts a real-time kernel ETW session (`NetworkTCPIP` keyword) using [TraceEvent](https://www.nuget.org/packages/Microsoft.Diagnostics.Tracing.TraceEvent) and receives an event for every TCP/UDP send and receive. |
| `TrafficAggregator` | Thread-safe running totals per connection and per process. |
| `MainViewModel` | Once a second, merges the socket list with a traffic snapshot and works out rates. |

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

## Ideas for later

- History and charts of traffic over time
- System-tray icon with an alert when a new program connects
- Reverse-DNS names and country for remote addresses
- Export to CSV

## License

[MIT](LICENSE)
