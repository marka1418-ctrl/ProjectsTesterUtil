# ProjectsTesterUtil

Utilities for automated testing of ENCY projects on Windows.

- `ProjectTestingUtility` — console test runner.
- `ProjectTestingMonitor` — WPF interface for configuring runs and viewing results.
- `tests` — focused verification programs.
- `scripts` — packaging and diagnostic scripts.

## Build

Requires Windows x64, the .NET 10 SDK and access to the ENCY SDK NuGet feed. ENCY must be installed and configured to run project tests.

From the repository root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Publish-Portable.ps1
```

This publishes the tester, embeds it in the monitor, and creates a portable package under `artifacts/releases`. The script downloads and verifies the required FFmpeg bundle. Generated executables and local settings are not included in this source repository.

Configure your credentials through the application settings before running tests. The included presets contain no credentials.

See [portable packaging](docs/PORTABLE-RELEASE.md), [recording diagnostics](docs/RDP-RECORDING.md), and the [PDF manual](README.pdf).

This repository starts from a snapshot of the current source files; earlier Git history is not included.