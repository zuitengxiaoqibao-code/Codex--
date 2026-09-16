# Codex Migration Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把 0.2 预览版升级为可判定、可解释、可复核的 Codex 重装迁移工具，并保留未知结构 fail-closed 的安全边界。

**Architecture:** 在现有 .NET 10 WPF + Core 分层上增加环境清单、preflight 判定、版本兼容和恢复后结构验收。备份仍以目录包、逐文件 SHA-256、SQLite inventory、暂存发布和回滚为基础；敏感内容通过独立加密包保护，普通包继续兼容。

**Tech Stack:** C# / .NET 10 / WPF / Microsoft.Data.Sqlite / SQLitePCLRaw / xUnit / Windows Cryptography APIs.

## Global Constraints

- 完整迁移只覆盖本次发现且可读取的本地来源及关联，缺失或未知必须阻止完整结论。
- 未知 Core schema、活动写入者、非 NTFS、特殊文件和路径冲突不能静默跳过。
- 凭据、令牌、Cookie、私钥和密码不写入环境清单；加密包密码不落盘。
- 配置、凭据、技能、插件和自动化恢复后默认停用，不自动执行未知脚本。
- 真实用户数据不得用于破坏性测试；所有测试变更使用临时合成目录。
- 每项行为先写失败测试并确认失败，再写最小实现并运行覆盖测试。
- clean Windows 和真实 Codex 应用层验收未完成前，发布版本保持 preview。

---

### Task 1: Data Model, Versioning, Counts and Preflight

**Files:**
- Modify: `src/CodexBackup.Core/Models.cs`
- Modify: `src/CodexBackup.Core/BackupEngine.cs`
- Modify: `src/CodexBackup.Core/MigrationCoverage.cs`
- Create: `src/CodexBackup.Core/PreflightReport.cs`
- Test: `tests/CodexBackup.Tests/MigrationCoverageTests.cs`
- Test: `tests/CodexBackup.Tests/DiscoveryTests.cs`

**Interfaces:**
- Add `ScanResult.UniqueSessionCount`, `ScanResult.SessionAssociationCount`, `ScanResult.ProjectLocationCount`.
- Add `ScanResult.EnvironmentManifest` and `BackupManifest.EnvironmentManifest`, `BackupManifest.SourceCodexVersion`.
- Add `PreflightStatus` (`Ready`, `Blocked`, `SalvageOnly`) and `PreflightReport Build(ScanResult, BackupRequest)`.
- Define one public `ProductInfo.Version = "0.3.0-preview"` constant and use it for manifest/tool diagnostics.

- [ ] **Step 1: Write failing tests** for unique session counting, association counting, project location counting, blocked preflight when a transcript is missing, and manifest source-version persistence.
- [ ] **Step 2: Run the focused tests** with `%USERPROFILE%\.dotnet\dotnet.exe test tests/CodexBackup.Tests -c Release --filter FullyQualifiedName~MigrationCoverageTests` and confirm the new tests fail for the missing members/behavior.
- [ ] **Step 3: Implement the model and report types** without changing existing complete-mode semantics. Count unique IDs separately from association rows; preserve all findings and cap only UI rendering, never the report data.
- [ ] **Step 4: Pass `ScanResult.CodexVersion` into `BackupRequest` and `BackupManifest`**, remove the stale `0.1.0-preview` default, and update UI/result code to use `ProductInfo.Version`.
- [ ] **Step 5: Run focused tests and then the full suite**; expected result is all tests passing and no change to fail-closed coverage behavior.
- [ ] **Step 6: Commit** with `git add src tests && git commit -m "feat: add migration preflight and versioned counts"`.

### Task 2: Environment Inventory and Sensitive Data Classification

**Files:**
- Modify: `src/CodexBackup.Core/Discovery/DiscoveryService.cs`
- Modify: `src/CodexBackup.Core/Discovery/WindowsEnvironment.cs`
- Create: `src/CodexBackup.Core/EnvironmentInventory.cs`
- Modify: `src/CodexBackup.Core/Models.cs`
- Test: `tests/CodexBackup.Tests/DiscoveryTests.cs`

**Interfaces:**
- Add `EnvironmentManifest Collect(string profile, IReadOnlyList<SourceItem> items, CancellationToken)`.
- Add `EnvironmentEntry` with `Category`, `DisplayName`, `ValueSummary`, `Risk`, `SourcePath` and no secret value field.
- Add categories for runtimes, package managers, lockfiles, environment variable names, services, scheduled tasks, WSL, Docker, Git, GPU/model hints and file associations.

- [ ] **Step 1: Write failing tests** proving that environment variable names are retained while values containing key/token/password patterns are redacted, that lockfiles are listed by path, and that the collector never launches discovered project executables.
- [ ] **Step 2: Run the focused discovery tests** and verify they fail before implementation.
- [ ] **Step 3: Implement bounded collectors** using registry/environment APIs and fixed commands only for version queries. Bound process count, output bytes and elapsed time; record unknown when access is denied.
- [ ] **Step 4: Attach the manifest to `ScanResult` and `BackupManifest`**, include a human-readable `environment-report.json`, and include the redaction policy in coverage notes.
- [ ] **Step 5: Run discovery and full tests**; verify the source tree and real user files remain unmodified.
- [ ] **Step 6: Commit** with `git add src tests && git commit -m "feat: record redacted runtime environment inventory"`.

### Task 3: Core Selection and Version Compatibility

**Files:**
- Modify: `src/CodexBackup.Core/RestorePlanner.cs`
- Modify: `src/CodexBackup.Core/RestoreEngine.cs`
- Modify: `src/CodexBackup.Core/Models.cs`
- Modify: `src/CodexBackup.App/MainWindow.xaml`
- Modify: `src/CodexBackup.App/MainWindow.xaml.cs`
- Test: `tests/CodexBackup.Tests/RestoreTests.cs`
- Test: `tests/CodexBackup.Tests/CoreIntegrationTests.cs`

**Interfaces:**
- Add `RestoreRequest.PrimaryCoreRootId` and `RestoreRequest.TargetCodexVersion`.
- Add `RestorePlanner.ValidateCoreSelection(BackupManifest, RestoreRequest)`.
- Add a restore finding for source/target version mismatch and unknown target version.

- [ ] **Step 1: Write failing tests** for two Core roots requiring exactly one primary selection, for the selected primary mapping to runtime `CODEX_HOME`, and for a target version mismatch blocking managed replacement while allowing isolated extraction.
- [ ] **Step 2: Run the focused restore tests** and verify failure.
- [ ] **Step 3: Implement explicit primary-Core validation** and preserve independent destinations for non-primary Core roots. Do not merge databases.
- [ ] **Step 4: Compare persisted source version with the detected target version**; unknown or mismatched versions produce a plain-language blocker for controlled replacement.
- [ ] **Step 5: Update the WPF restore grid** to display source version, target version, primary-Core radio selection and the exact consequence of choosing isolation.
- [ ] **Step 6: Run focused and full tests**, then commit with `git add src tests && git commit -m "feat: gate restore by primary core and version"`.

### Task 4: Optional Encrypted Backup Envelope

**Files:**
- Create: `src/CodexBackup.Core/EncryptedPackage.cs`
- Modify: `src/CodexBackup.Core/BackupEngine.cs`
- Modify: `src/CodexBackup.Core/PackageVerifier.cs`
- Modify: `src/CodexBackup.Core/Models.cs`
- Modify: `src/CodexBackup.App/MainWindow.xaml`
- Modify: `src/CodexBackup.App/MainWindow.xaml.cs`
- Test: `tests/CodexBackup.Tests/PackageTests.cs`

**Interfaces:**
- Add `BackupRequest.EncryptionPassword` as an in-memory-only value.
- Add `PackageVerifier.VerifyAsync(path, password, ...)` overload for encrypted packages.
- Use an authenticated envelope with random salt, nonce and per-package key derivation; never store the password or plaintext key.

- [ ] **Step 1: Write failing tests** for successful encrypted round-trip, wrong-password rejection, tamper rejection, and absence of password material in manifest/report files.
- [ ] **Step 2: Run package tests** and confirm failure.
- [ ] **Step 3: Implement the envelope** with platform cryptography primitives, authenticated metadata, bounded plaintext extraction and secure cleanup of temporary plaintext.
- [ ] **Step 4: Add the UI choice**: ordinary package or password-protected package. Explain that a lost password is unrecoverable and that encrypted packages still require target-version checks.
- [ ] **Step 5: Run package and full tests**, including cancellation and disk-full synthetic cases.
- [ ] **Step 6: Commit** with `git add src tests && git commit -m "feat: support optional encrypted backup packages"`.

### Task 5: Restore Structural Acceptance and Activation Guidance

**Files:**
- Create: `src/CodexBackup.Core/RestoreAcceptance.cs`
- Modify: `src/CodexBackup.Core/RestoreEngine.cs`
- Modify: `src/CodexBackup.App/MainWindow.xaml`
- Modify: `src/CodexBackup.App/MainWindow.xaml.cs`
- Test: `tests/CodexBackup.Tests/CompleteRestoreTests.cs`
- Test: `tests/CodexBackup.Tests/RestoreTests.cs`

**Interfaces:**
- Add `RestoreAcceptanceReport Validate(VerifiedPackage, RestoreRequest, IReadOnlyDictionary<string,string> physicalRoots)`.
- Report separate `StructuralStatus` and `ApplicationStatus` values; application status starts `PendingManualCheck`.
- Add a list of disabled integration entries with source path, risk class and manual review action.

- [ ] **Step 1: Write failing tests** for session/transcript pairing, mapped `cwd`, project existence, Git pointer existence, memory pointer remapping, duplicate project targets and explicit pending application status.
- [ ] **Step 2: Run restore acceptance tests** and confirm failure.
- [ ] **Step 3: Implement validation against staged files before write and restored files after write**. Keep historical transcript body text unchanged and record that limitation.
- [ ] **Step 4: Surface the report in the result page** with beginner text first and technical details collapsed. Add a guided checklist for login, sidebar, session open, project open, memory, skills, plugins and representative project execution.
- [ ] **Step 5: Run restore tests and full suite**, ensuring rollback remains available after a failed acceptance check.
- [ ] **Step 6: Commit** with `git add src tests && git commit -m "feat: add structural restore acceptance report"`.

### Task 6: User Guidance, Counts, Temporary Directory Cleanup and Release Metadata

**Files:**
- Modify: `src/CodexBackup.Core/UserGuidance.cs`
- Modify: `src/CodexBackup.Core/Discovery/DiscoveryService.cs`
- Create: `src/CodexBackup.Core/TemporaryArtifactManager.cs`
- Modify: `src/CodexBackup.App/MainWindow.xaml`
- Modify: `src/CodexBackup.App/MainWindow.xaml.cs`
- Modify: `outputs/v0.2/使用说明.md`
- Create: `docs/release/0.3-验收清单.md`
- Test: `tests/CodexBackup.Tests/DiscoveryTests.cs`
- Test: `tests/CodexBackup.Tests/RestoreTests.cs`

**Interfaces:**
- Add `UserGuidance.ExplainException(Exception)` mapping filesystem, path, permission, space, schema, password and active-writer errors to cause/impact/action.
- Add `TemporaryArtifactManager.List`, `ValidateForCleanup` and `DeleteAfterVerification`.

- [ ] **Step 1: Write failing tests** for redacted human guidance, no exception type names in primary text, correct count labels, and cleanup refusing unverified artifacts.
- [ ] **Step 2: Run focused tests** and confirm failure.
- [ ] **Step 3: Implement guidance mapping and artifact lifecycle**; preserve raw details only in technical diagnostics.
- [ ] **Step 4: Update UI labels** so association counts are called associations, unique sessions are separate, and the preflight result uses “可以重装 / 需要处理后再重装 / 仅可抢救”.
- [ ] **Step 5: Update the Chinese manual and release checklist** with official-document uncertainty, target-version checks, two-copy backup advice, isolation restore, and clean-system acceptance steps.
- [ ] **Step 6: Run full tests and UTF-8/mojibake scans**, then commit with `git add src tests docs outputs && git commit -m "feat: improve guidance and artifact lifecycle"`.

### Task 7: Build, Isolated Acceptance, Signing Hook and Final Review

**Files:**
- Modify: `README.md`
- Create: `scripts/verify-release.ps1`
- Create: `scripts/sign-release.ps1`
- Modify: `outputs/v0.2/RELEASE.json`
- Create: `outputs/v0.3/RELEASE.json`
- Create: `outputs/v0.3/验证说明.md`

- [ ] **Step 1: Run the complete source regression** with `%USERPROFILE%\.dotnet\dotnet.exe test tests/CodexBackup.Tests -c Release`; expected result is zero failures.
- [ ] **Step 2: Build and publish self-contained win-x64** using the pinned .NET SDK and run `--self-test`, `--smoke-test`, and read-only scan diagnostics.
- [ ] **Step 3: Run `scripts/verify-release.ps1`** to check hashes, manifest version, package verifier, UTF-8 text and absence of private user paths.
- [ ] **Step 4: Run an isolated restore rehearsal** from a synthetic package and, when a clean Windows/Sandbox is available, record real Codex application acceptance separately. Do not mutate live user data.
- [ ] **Step 5: Run `scripts/sign-release.ps1`** as a no-op with a clear message when no certificate is present; sign only when an explicit certificate path is supplied.
- [ ] **Step 6: Write final verification evidence** including what passed, what remains unverified, the actual package hash and the current machine preflight status.
- [ ] **Step 7: Commit** with `git add README.md scripts outputs && git commit -m "release: publish Codex migration hardening preview"`.

## Plan Self-Review

- The plan preserves the existing discovery, package, restore and UI boundaries and assigns every design requirement to a task.
- Complete-mode fail-closed behavior is tested in Tasks 1, 3 and 5; encryption is optional and cannot weaken ordinary package verification.
- Real clean-system application acceptance is explicitly a release gate and is never represented as a passing automated test.
- No task silently enables credentials, plugins or automation, and no task treats unknown official documentation as a supported field.
