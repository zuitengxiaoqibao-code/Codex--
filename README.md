# Codex Backup (Windows 0.3 preview)

Offline .NET 10 / WPF backup, restore and verification tool with a Chinese UI. It follows the configured `CODEX_HOME`, session references and project paths across local drives; it does not assume that Codex lives on `C:`.

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

## Migration behavior

The backup wizard presents three preflight outcomes: `可以重装`, `需要处理后再重装`, and `仅可抢救`. A complete result requires every discovered session to have both its transcript and project directory, and preserves Git pointers and the external memory-vault pointer when they are readable. Unique session IDs, association rows and project locations are shown separately so repeated references are not mistaken for independent conversations.

The restore wizard requires an explicit primary Core when a package contains multiple Core homes. A managed replacement can be blocked when the source and target Codex versions are unknown or differ; isolated extraction remains available. Configuration, credentials, skills, plugins, automations and peripheral tools are retained for review but are never silently enabled. After file verification, the application layer remains pending until a user starts Codex, checks the sidebar and opens representative projects.

Password-protected packages use a chunked authenticated AES-GCM envelope. The password is held only in memory, is never written to `manifest.json` or reports, and cannot be recovered if lost. Keep two copies on different physical media and perform a second verification before deleting the old system.

The official Codex repository documents configuration as `config.toml` plus managed `requirements.toml` layers and directs users to the online configuration reference. The online reference may be unavailable behind access controls; fields that this tool cannot verify locally are reported as unknown and are not auto-restored.

Release checks:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\verify-release.ps1 -ReleaseDirectory C:\path\to\outputs\v0.3
powershell -ExecutionPolicy Bypass -File .\scripts\sign-release.ps1 -ReleaseDirectory C:\path\to\outputs\v0.3
```

## Safety and acceptance

See docs/implementation-plan.md and the review reports. User data must never be used for destructive tests. Unknown active database schemas block managed Core migration; isolated restore remains available. Exact observed schema compatibility is not application-level acceptance. Current-user DPAPI protects rollback journals; they are not portable backups.

Release remains preview until clean-machine and real Codex application restoration have been validated. This is not a filesystem snapshot, a signed archive format, or a universal third-party migration solution.
