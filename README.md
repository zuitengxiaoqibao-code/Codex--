# Codex Backup (Windows preview)

Offline .NET 10 / WPF backup, restore and verification tool with a Chinese UI.

## Build

Use the .NET SDK specified in global.json (10.0.302). On a machine with another compatible .NET 10 SDK, install that SDK or deliberately update global.json.

```powershell
dotnet test tests/CodexBackup.Tests -c Release
dotnet publish src/CodexBackup.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:RuntimeFrameworkVersion=10.0.12
```

Runtime dependencies are Microsoft.Data.Sqlite.Core 10.0.12, SQLitePCLRaw 3.0.5 and SQLite 3.53.4. First restore needs NuGet access. The published application operates offline. It uses a fixed, bounded system PowerShell query for installed MSIX metadata when available; it never runs discovered executables.

## Release diagnostics

```powershell
Start-Process .\CodexBackup.exe -ArgumentList '--self-test C:\Temp\backup-self-test.json' -Wait
Start-Process .\CodexBackup.exe -ArgumentList '--smoke-test C:\Temp\backup-ui-smoke.json' -Wait
```

Self-test creates random synthetic fixtures beside the report. Its final corruption test deliberately invalidates the synthetic backup. Smoke checks window initialization, rendering and navigation handlers, not the full native-dialog workflow. `--scan-report` produces a private path inventory: do not publish it.

## Safety and acceptance

See docs/implementation-plan.md and the review reports. User data must never be used for destructive tests. Unknown active database schemas block managed Core migration; isolated restore remains available. Exact observed schema compatibility is not application-level acceptance. Current-user DPAPI protects rollback journals; they are not portable backups.

Release remains preview until clean-machine and real Codex application restoration have been validated. This is not a filesystem snapshot, a signed archive format, or a universal third-party migration solution.
