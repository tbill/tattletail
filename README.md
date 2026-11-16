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
- **Group by domain:** the servers list shows one row per domain, so `github.com`,
  `api.github.com` and `lb-140-82-113-23-iad.github.com` are all under **github.com**. Click a
  domain to see its individual hosts. The Connections tab also has a Domain column.
- **Website names, or IP addresses, or both:** pick with **Show servers as**. Hover any
  server to see both.
- **Connections tab:** every TCP/UDP socket with local address, remote server, port, state,
  speed and totals. It shows the same sockets as `netstat -ano`, plus names and traffic.
- Filter box to search by program name, PID, IP address, port or state.
- Click any column header to sort. Rows re-sort live as the numbers change.
- Hover a row to see the program's full path.
- Programs that exit stay in the list, greyed out, so you can still see what they did. Even a
  program that runs for a split second is listed by name.
  Untick **Show exited programs** to hide them.
- **Reset counters** sets every total back to zero.
- **Runs in the background:** closing the window hides TattleTail in the system tray (next to
  the clock), where it keeps recording. Hover the tray icon for current speeds, click it to
  bring the window back, or right-click it and choose **Exit** to quit.
- **Start with Windows:** tick it under **Options** and TattleTail starts hidden in the tray
  every time you sign in, so history has no gaps.
- **New-program alerts:** a notification pops up the first time a program uses the network.

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

## How domains are worked out

Keeping just the last two parts of a name works for `api.github.com`, but not for
`news.bbc.co.uk`, whose domain is `bbc.co.uk`. TattleTail uses the
[Public Suffix List](https://publicsuffix.org/), the same list browsers use, to know which
endings (`.com`, `.co.uk`, `.com.au`, …) are public. Only the list's official (ICANN) section
is used, so hosting services group together: every `*.cloudfront.net` server shows under
`cloudfront.net` instead of as hundreds of separate domains. A GitHub Action refreshes the list on the 1st of
every month and commits the update only if all tests still pass.

## Running in the background

All three switches are under **Options** in the toolbar.

- **Start with Windows (in the tray).** TattleTail needs administrator rights, and Windows
  won't start such a program at sign-in without asking every time. So TattleTail creates a
  scheduled task named `TattleTail` (visible in Windows' Task Scheduler) that is allowed to
  start it without the prompt. Unticking the option deletes the task. If you move
  `TattleTail.exe`, the task is re-pointed the next time you run it.
- **Keep running in the tray when the window is closed.** Untick it if you'd rather the close
  button quit.
- **Alert when a new program uses the network.** TattleTail remembers every program it has seen
  send or receive anything. The first time ever, it spends five minutes just learning what's
  normal, without alerts. Traffic that stays on your own PC (localhost) doesn't count.
  An updated program that installs to a new folder counts as new.

## Where history is kept

Traffic history is saved once a minute to `%LOCALAPPDATA%\TattleTail\history.db` (a SQLite
file) and kept for 30 days, along with the list of programs already seen on the network.
Settings are in `settings.json` in the same folder. Nothing is ever sent anywhere.
**Clear history…** deletes it all.

## How it works

| Piece | What it does |
|---|---|
| `ConnectionTable` | Calls `GetExtendedTcpTable` / `GetExtendedUdpTable` (IP Helper API) to list sockets and their owning process IDs. |
| `EtwTrafficMonitor` | Starts a real-time kernel ETW session (`NetworkTCPIP` keyword) using [TraceEvent](https://www.nuget.org/packages/Microsoft.Diagnostics.Tracing.TraceEvent) and receives an event for every TCP/UDP send and receive. |
| `ProcessRegistry` | Knows which program owns each process ID right now. Windows reuses process IDs, so the kernel's process start/stop events (same ETW session) are used to keep a new program from inheriting an old one's name and totals. |
| `TrafficAggregator` | Thread-safe running totals per connection, per process, and per process + remote address. |
| `EtwDnsMonitor` / `HostNameCache` | Listens to `Microsoft-Windows-DNS-Client` event 3008 (query completed) to map addresses to names, with reverse DNS as a fallback. |
| `TrafficHistory` / `HistoryStore` | Keeps the last 10 minutes per second in memory and writes per-minute, per-program, per-server rows to SQLite on a background thread. A minute that can't be saved is kept and retried. |
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

Versions are named **year-month.Release**: `v26-11.R2.0` is the 2nd release, made in November 2026
(the last number is for patches: `v26-11.R2.1`).

To release, go to **Releases → Draft a new release**, type a new tag in that form, target `main`,
and click **Publish release**. GitHub Actions builds both downloads and attaches them, usually
within about 3 minutes. Inside the app, the window title and the file's
**Properties → Details** show the version: product version `26-11.R2.0`, file version `26.11.2.0`
(Windows needs four plain numbers). Builds that aren't releases say "dev build" and their commit.

## Testing on Windows

Besides the unit tests, CI launches the real app on a Windows machine with
`TattleTail.exe --smoke-test report.json 35 --background`, makes a few web requests while it
runs, and checks the report shows connections, traffic, website names and history. It also
checks that the app started hidden with a tray icon, that starting a second copy brought the
window back, and that Windows accepts the sign-in task.

## Ideas for later

- Country and a world map for remote servers
- Show whether each program is digitally signed, and by whom
- Export to CSV

## License

[MIT](LICENSE). Third-party components are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
