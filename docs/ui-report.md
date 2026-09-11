# WPF wizard implementation report

Date: 2026-09-11

## Implemented surface

- Native .NET 10 WPF `WinExe` project under `src/CodexBackup.App`, referencing `CodexBackup.Core` directly.
- Chinese welcome and risk acknowledgement screen, three-action home screen, backup, restore, check, shared progress/cancellation overlay, and result screen.
- Backup workflow calls `DiscoveryService.ScanAsync` and `BackupEngine.BackupAsync`. It shows required locked selections, discovery evidence, findings, coverage limits, manual file/folder additions, destination selection, and the unencrypted-package acknowledgement.
- Restore workflow calls `PackageVerifier.VerifyAsync`, `RestoreEngine.PreviewAsync`, and `RestoreEngine.RestoreAsync`. It defaults to an isolated target, exposes editable path mappings, requires a fresh preview after option changes, blocks on preview findings, and separately confirms replacement.
- Check workflow keeps environment scanning, complete package verification, application-level acceptance pending state, and mutating journal rollback visibly separate. Rollback calls `RestoreEngine.RollbackAsync` only after two confirmations.
- One operation can run at a time. The overlay intercepts interaction, repeated handlers are guarded, cancellation is shared with Core, and closing while busy requests cancellation while keeping the window open.
- Result wording distinguishes file integrity from pending Codex application acceptance. Error dialogs avoid writing logs and translate common filesystem failures without displaying stack traces.
- `--smoke-test <outputJsonPath>` creates and lays out the real window, checks expected named controls, attempts a `RenderTargetBitmap` screenshot, writes JSON, and exits without invoking backup, restore, or rollback.

## Verification evidence

- Release build command: `C:\Users\90090\.dotnet\dotnet.exe build src\CodexBackup.App\CodexBackup.App.csproj -c Release`
- Result after the final code build before this report: succeeded with 0 warnings and 0 errors.
- Smoke command: `src\CodexBackup.App\bin\Release\net10.0-windows\CodexBackup.exe --smoke-test docs\ui-smoke.json`
- Smoke result: exit code 0; `initialized: true`; `namedControlsValid: true`; no missing controls; screenshot written to `docs/ui-smoke.png`.
- The screenshot was visually inspected at 1120 x 780. The welcome screen rendered Chinese text, risk list, acknowledgement, navigation action, header, and footer without overlap or clipping.
- Mojibake scan over `src/CodexBackup.App` and the smoke JSON found no configured corruption patterns.

## Honest limits

- Smoke mode proves startup, named control availability, and one static render only. It is not keyboard traversal, dialog automation, destructive-operation, clean-machine, or real Codex acceptance testing.
- Core/application/tool roots are explicitly labeled as isolated-only. The UI does not claim automatic activation or undocumented state-path migration support. Generic project roots can request complete replacement only after Core preview, with the same-Windows-user sealed rollback journal handled by Core.
- No real user backup, restore, package verification, or rollback was launched during this work unit.
- The app project was built directly because adding it to the solution belongs to the main integration work unit.
