# FluxReader development

[Back to the user README](../README.md)

Use this document to build, test, package, and release FluxReader. The project targets Windows 11 only; do not add Windows 10 or legacy compatibility paths.

## Prerequisites

- Windows 11
- The .NET 10 SDK selected by [`global.json`](../global.json)
- Windows SDK 10.0.26100
- Windows App SDK 2.4 Runtime
- Visual Studio with the **WinUI application development** workload, or another editor with PowerShell and the .NET CLI

## Build and run

1. Restore the solution:

   ```powershell
   dotnet restore FluxReader.slnx --configfile NuGet.Config
   ```

2. Build all projects in Release mode:

   ```powershell
   dotnet build FluxReader.slnx --configuration Release --no-restore
   ```

3. Run the unpackaged desktop application:

   ```powershell
   dotnet run --project src\FluxReader\FluxReader.csproj --no-restore
   ```

FluxReader uses the Windows App SDK unpackaged desktop model. Application notifications are registered at startup through `AppNotificationManager.Register()` and do not require manual COM or AUMID setup.

## Test

Run the core test suite after restoring the solution:

```powershell
dotnet test --project tests\FluxReader.Core.Tests\FluxReader.Core.Tests.csproj --configuration Release --no-restore --minimum-expected-tests 1
```

## Architecture

| Area | Choice |
| --- | --- |
| Language and target | C#, .NET 10, `net10.0-windows10.0.26100.0`, minimum Windows platform version `10.0.22000.0` |
| UI | WinUI 3, Windows App SDK component packages, CommunityToolkit.Mvvm |
| Article content | AngleSharp HTML sanitization and WebView2 rendering |
| Data | Microsoft.Data.Sqlite with SQLite WAL |
| Tests and dependencies | MSTest, Microsoft.Testing.Platform, `.slnx`, and NuGet Central Package Management |

Package versions are defined centrally in [`Directory.Packages.props`](../Directory.Packages.props). The default application version is defined in [`Directory.Build.props`](../Directory.Build.props).

```text
src/
  FluxReader.Core/       RSS, Atom, and OPML parsing; content processing and search; no UI dependencies
  FluxReader/            WinUI 3, MVVM, SQLite, refresh, notifications, and system tray
installer/
  FluxReader.Installer/  WiX MSI embedded in Setup
  FluxReader.Setup/      WiX Burn online installer
tests/
  FluxReader.Core.Tests/ Unit tests for core logic
```

## Local state and diagnostics

Application data is stored in `%LOCALAPPDATA%\FluxReader`.

| Path | Purpose |
| --- | --- |
| `reader.db` | Subscriptions, articles, and reading state |
| `settings.json` | Theme, language, refresh interval, and pane widths |
| `Logs\FluxReader-*.jsonl` | Recent session and crash diagnostics |
| `notifications.log` | Notification registration, delivery, or unregistration failures; created only after a failure |
| `notification-icons\`, `WebView2\` | Local notification icon cache and article-view data |

## Content safety boundaries

- RSS, Atom, and OPML parsing disables XML DTDs and external resolution and limits document size.
- Article HTML is sanitized to remove scripts, forms, frames, embedded objects, and unsafe attributes. Only approved elements and HTTP(S) / `mailto:` links remain.
- WebView2 disables scripts, developer tools, host objects, and web messages. Article links always open in the system browser.

## Build installers

1. Choose a three-part version and build the Setup EXE for each target architecture:

   ```powershell
   .\scripts\Build-Installer.ps1 -Version 1.0.0 -Architecture x64
   .\scripts\Build-Installer.ps1 -Version 1.0.0 -Architecture arm64
   ```

2. After one online build has populated the NuGet assets and prerequisite cache, verify an offline build when needed:

   ```powershell
   .\scripts\Build-Installer.ps1 -Version 1.0.0 -Architecture x64 -Offline
   ```

3. Collect `FluxReaderSetup-{version}-{architecture}.exe` from `artifacts\installers`.

The build script pins the .NET 10 Runtime, Visual C++ Runtime, and Windows App Runtime 2.4 payloads and caches them by architecture in `artifacts\cache\prerequisites`. Cached files are verified with SHA-256 before reuse. `-Offline` skips restore and fails when either NuGet assets or prerequisite payloads are incomplete.

Setup installs FluxReader to `Program Files\FluxReader`, creates a Start menu shortcut, supports uninstall from **Installed apps**, and upgrades older versions. A machine without the required runtimes needs internet access during installation. The bundle does not create a Windows system restore point; MSI transactional rollback remains enabled.

An MSI cannot overwrite an already published or installed build with the same version. Increment the version whenever published content changes.

## Release

1. Push a unique `vMAJOR.MINOR.PATCH` tag:

   ```powershell
   git tag v1.0.0
   git push origin v1.0.0
   ```

2. GitHub Actions validates the tag, runs the tests, and builds x64 and ARM64 Setup executables.
3. The workflow creates a GitHub Release and attaches both installers.

The release workflow is defined in [`.github/workflows/release.yml`](../.github/workflows/release.yml). Before public distribution, sign the application binaries, the internal MSI, and then the Setup EXE.
