# Implementation ledger — approved Codex backup tool

Goal: build the approved Chinese offline Windows backup / restore / check assistant. User authorized autonomous implementation and verification on 2026-09-11.

Architecture: .NET 10 WPF UI calls a standalone Core; SQLite is used for package inventory and read-only source metadata. A local self-contained EXE, source archive, manual, hashes and evidence report are required. No real user restore tests.

Global constraints: UTF-8 edits via apply_patch; do not modify real Codex or project data; unknown or unreadable data is not success; mandatory dependencies cannot be deselected; writer detection must not kill processes; no network activity at runtime; input paths are untrusted; restore requires staging and rollback before replace. Unverified clean-machine/application scenarios must remain visibly unverified.

## Work units

- [x] A. Discovery: `Discovery/DiscoveryService.cs`, `Discovery/WindowsEnvironment.cs`, `DiscoveryTests.cs`. API `Task<ScanResult> ScanAsync(string? profile, IProgress<OperationProgress>?, CancellationToken)`. Detect default/env homes, project roots in metadata and SQLite, memory pointers, tools, AppData package redirection, skills, worktree dependencies. Preserve discovery evidence and unknown coverage. Synthetic fixture tests precede implementation.
- [x] B. Core safety and package: `Security/PathSafety.cs`, `Backup/BackupEngine.cs`, `Formats/InventoryStore.cs`, `Verification/PackageVerifier.cs`, tests. `BackupAsync(BackupRequest, IProgress<OperationProgress>?, CancellationToken)` and `VerifyAsync(string, IProgress<OperationProgress>?, CancellationToken)`. First test Unicode/empty-directory round trip and mandatory selection, then corruption, traversal, changing sources, cancellations, overlap and special files. Never mark incomplete data complete.
- [x] C. Restore: `Restore/RestoreEngine.cs`, tests. `PreviewAsync(RestoreRequest, CancellationToken)` and `RestoreAsync(RestoreRequest, IProgress<OperationProgress>?, CancellationToken)`. Tests cover isolated output, conflict protection, complete replacement, rollback and hostile mappings; write durable journal before each mutation and verify original rollback data. Unknown semantic migrations remain blocked rather than guessed.
- [x] D. WPF wizard: `CodexBackup.App` with welcome/risk screen, three actions, scan/selection/target/review/progress/result screens, restored path mappings, check modes and explicit incomplete/preview labels. UI state prevents concurrent writes and losing operations on close.
- [x] E. Independent review and regression: bounded review of Core and discovery; fix important findings, add regression tests. Verify source trees are unmodified by backup/check; all test mutations under generated fixtures.
- [x] F. Release: self-contained win-x64 publish, actual EXE smoke and UI automation, Unicode scan, full tests and source snapshot; report actual limits without claiming unperformed clean OS / real Codex restore validation.

Build tools: SDK found at `C:\Users\90090\.dotnet\dotnet.exe` (10.0.302), not system PATH dotnet. Runtime 10.0.10. Pin SDK; use cached NuGet packages where possible. `dotnet test tests/CodexBackup.Tests -c Release` is the regression gate. Publish command will use `-r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true`.

## Progress

- 2026-09-11: approved design reviewed, isolated new git repository initialized on feature/codex-backup; no existing source to regress. Initial models and build descriptors established.
- 2026-09-11: A-D implemented: discovery, verified directory packages, restore/rollback, Chinese WPF UI. Initial suite passed 61 tests. Independent package and restore reviews found and fixed data omission, ADS, protected-target, fallback and journal-limit issues with regression tests.
- 2026-09-11: Live read-only discovery found Core, external memory, skills, project references and MSIX redirected state. Missing historical references and active-WAL coverage are surfaced. No real user backup/restore mutation performed.
- 2026-09-11: E-F in progress: final controlled-Core schema gate review, runtime mapping UI, published EXE diagnostics, fresh regression, source archive and documentation. Clean OS and real Codex application restore remain unvalidated; release must stay preview.

- 2026-09-11 final local release: 72 tests passed; self-contained .NET 10.0.12 EXE self-test, UI navigation smoke and live read-only discovery passed. Clean-OS/real Codex restore and complete native-dialog UI acceptance remain pending; these are not marked passed.
