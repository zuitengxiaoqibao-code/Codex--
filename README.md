# Codex Backup (Windows 0.3.6 preview)

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

The default backup path is one-click discovery and recommended selection, followed by destination choice and verified backup. Custom profiles, additional roots, salvage mode, encryption and per-item tables remain available in collapsed advanced sections. The wizard presents three preflight outcomes: `可以重装`, `需要处理后再重装`, and `仅可抢救`. A complete result requires every discovered session to have both its transcript and project directory, and preserves Git pointers and the external memory-vault pointer when they are readable. Unique session IDs, association rows and project locations are shown separately so repeated references are not mistaken for independent conversations.

Backup selection is divided into tabs for sessions, projects, memories, Codex personal data, skills, plugins/tools, reinstallable/other items, and archived-project cleanup. The default session view groups conversations by normalized project directory; one tri-state project checkbox selects every session in that directory, while an expandable child list and an optional individual-session view preserve fine-grained control. Session/source relationships are indexed once after discovery; a checkbox update changes reference counts and refreshes the UI once, including for bulk selection. Reinstallable Codex/ChatGPT application files are not required backup items. The selected Codex personal-data root automatically omits known top-level logs, caches, temporary locks, sandbox runtime state and login-token files while preserving session indexes, transcripts, source trees, Git metadata, skills, memories and configuration. Unknown entries are retained to avoid discarding unrecognized personal data.

Archived-project cleanup is opt-in and disabled until the current run has created and verified a backup that contains the candidate. Its bulk action only selects known rebuildable top-level directories such as `bin`, `obj`, `node_modules`, caches and build outputs. Any safe existing project path referenced by an archived session can appear as a red source-containing candidate, even when it has no Git or build-system marker, but it must be selected individually. Transcripts, memory vaults, Codex databases, system roots and user-library roots are never candidates, and projects used by or nested with an active session are locked. Cleanup moves data to a same-volume `.codex-backup-quarantine` directory and writes a journal; the UI can restore that journal without overwriting an existing path.

The restore wizard requires an explicit primary Codex personal-data root when a package contains multiple homes. A managed replacement can be blocked when the source and target Codex versions are unknown or differ; isolated extraction remains available. Configuration, skills, plugins, automations and peripheral tools are retained for review but are never silently enabled. Standalone login-token and sandbox-secret files are deliberately omitted; the user signs in again after reinstall. After file verification, the application layer remains pending until a user starts Codex, checks the sidebar and opens representative projects.

Password-protected packages use a chunked authenticated AES-GCM envelope. The password is held only in memory, is never written to `manifest.json` or reports, and cannot be recovered if lost. Keep two copies on different physical media and perform a second verification before deleting the old system.

The official Codex documentation describes configuration as layered `config.toml` files plus managed `requirements.toml`. On Windows the system layer is `%ProgramData%\OpenAI\Codex\config.toml` and `%ProgramData%\OpenAI\Codex\requirements.toml`; the user layer is `${CODEX_HOME}/config.toml`, profile overrides use `${CODEX_HOME}/<name>.config.toml`, and project loading can include the current directory `config.toml` plus parent or repository `.codex/config.toml`. The scanner follows those layers for every discovered project while keeping custom-profile scans away from the host user's unrelated `.codex` directory. It also reports officially documented unsupported, deprecated and legacy settings with their replacements. This audit is read-only, never blocks backup, and keeps the original configuration. Undocumented filenames remain unconfirmed and are never treated as safe to delete. The state runtime can be moved independently with `CODEX_SQLITE_HOME` or `sqlite_home`, logs can move with `log_dir`, and the tool lists the fixed `history.jsonl`, official runtime databases, external model-instruction/catalog paths, `[[skills.config]]` skill files, and local marketplace sources when they are present.

The environment report labels each entry so the result is unambiguous: `已纳入备份` means the path is a required source in the package, `随对应来源选择` means it follows the project's checkbox, `仅检测到` means the tool found a location but did not copy it automatically, `仅保存名称` or `仅保存状态` means sensitive environment values were deliberately redacted, and `需在新系统重建` or `外部来源，未复制` means the item requires manual work after reinstall.

System-level Codex configuration files are backed up when readable but are never written directly to `ProgramData` during restore. Application binaries and general operating-system files are outside the backup scope. Cloud, MDM, domain policy, keyring credentials and other external services must be re-established on the new system by the user or administrator.

Release checks:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\verify-release.ps1 -ReleaseDirectory C:\path\to\outputs\v0.3.6
powershell -ExecutionPolicy Bypass -File .\scripts\sign-release.ps1 -ReleaseDirectory C:\path\to\outputs\v0.3.6
```

## Safety and acceptance

See docs/implementation-plan.md and the review reports. User data must never be used for destructive tests. Unknown active database schemas block managed Core migration; isolated restore remains available. Exact observed schema compatibility is not application-level acceptance. Current-user DPAPI protects rollback journals; they are not portable backups.

Release remains preview until clean-machine and real Codex application restoration have been validated. This is not a filesystem snapshot, a signed archive format, or a universal third-party migration solution.
