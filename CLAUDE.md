# TattleTail: notes for Claude

Windows network monitor (C# / .NET 8 / WPF). See README.md for features and layout.

## Workflow
- Work on a feature branch and open a pull request against `main`.
- **Merge pull requests yourself once CI passes** (squash merge). The owner has approved this
  as the standing rule for this repo.
- CI (`.github/workflows/build.yml`) builds and tests on Windows, then runs the real app with
  `--smoke-test` and checks it sees connections, traffic, names and history. Read the smoke
  report from the job log ("Smoke test" step); artifact downloads may be blocked.

## Building on Linux
- Needs Microsoft's .NET 8 SDK (it includes the WindowsDesktop SDK); Ubuntu's own `dotnet-sdk-8.0`
  package lacks it. `EnableWindowsTargeting` lets the WPF app build on Linux.
- `dotnet test` runs the core tests on Linux. The app itself only runs on Windows.

## Feature queue (agreed with the owner, not started)
- Country / world map for remote servers
- Show whether each program is digitally signed, and by whom
- System-tray icon with an alert when a new program connects
