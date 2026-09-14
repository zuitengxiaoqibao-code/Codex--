# Simple Wizard and Official Config Audit Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a one-click default backup path and report current officially documented obsolete Codex configuration without modifying user files.

**Architecture:** A focused `OfficialConfigAudit` component inspects TOML statements already read by discovery and emits ordinary findings with official replacement guidance. The WPF page keeps existing controls and behavior inside collapsed advanced sections while exposing one primary action and plain-language summaries.

**Tech Stack:** C# 14, .NET 10, WPF, xUnit, self-contained Windows x64 publishing.

## Global Constraints

- Preserve all Chinese files as UTF-8 and edit them with `apply_patch`.
- Never modify or delete scanned configuration during official audit.
- Do not call filesystem discovery from any checkbox handler.
- Treat undocumented filenames as unconfirmed, not deprecated.
- Official source: `https://learn.chatgpt.com/docs/config-file/config-reference`.

---

### Task 1: Official configuration audit

**Files:**
- Create: `src/CodexBackup.Core/OfficialConfigAudit.cs`
- Modify: `src/CodexBackup.Core/Discovery/DiscoveryService.cs`
- Test: `tests/CodexBackup.Tests/OfficialConfigAuditTests.cs`

**Interfaces:**
- Produces: `OfficialConfigAudit.Inspect(IReadOnlyList<string> statements, string path)` returning `IReadOnlyList<Finding>`.
- Consumes: comment-stripped, multiline TOML statements from `ReadTomlStatements`.

- [ ] Write failing tests for unsupported, deprecated and legacy keys in global, table and dotted-key forms.
- [ ] Run the focused tests and confirm the auditor is missing.
- [ ] Implement exact-key matching and official replacement messages.
- [ ] Add findings for unconfirmed YAML and `managed_config.toml` filenames without calling them deprecated.
- [ ] Run the focused tests and complete test suite.

### Task 2: Default simple backup flow

**Files:**
- Modify: `src/CodexBackup.App/MainWindow.xaml`
- Modify: `src/CodexBackup.App/MainWindow.xaml.cs`
- Modify: `src/CodexBackup.App/App.xaml.cs`

**Interfaces:**
- Produces named controls `AdvancedScanExpander`, `AdvancedBackupExpander`, `ManualSelectionExpander`, `RecommendedSelectionSummaryText`, and `OfficialAuditSummaryText`.
- Reuses: `Scan_Click`, `ApplyBackupMode`, `CheckCoverage_Click`, and all existing manual filters.

- [ ] Extend WPF smoke to require all simple-flow and advanced controls.
- [ ] Run smoke and confirm missing controls fail the assertion.
- [ ] Replace the exposed path controls with a single “一键扫描并推荐” action and put custom paths under advanced scan settings.
- [ ] Show recommended category counts and official audit count after scan.
- [ ] Put tabs, tables, encryption and rescue mode under clearly named collapsed sections.
- [ ] Render welcome and backup screenshots and inspect spacing, text hierarchy and visibility.

### Task 3: Release verification

**Files:**
- Modify: `docs/release/0.3.5-验证说明.md`
- Rebuild: `outputs/v0.3.5/*`

**Interfaces:**
- Produces updated EXE, source ZIP, portable ZIP, release metadata and SHA-256 list.

- [ ] Run all xUnit tests and Release build.
- [ ] Run published EXE self-test and WPF smoke, including 1,000-session selection idle checks.
- [ ] Run a real read-only scan and record official audit counts.
- [ ] Run UTF-8/mojibake scan, `git diff --check`, PowerShell 5.1 release verification and portable ZIP extraction self-test.
- [ ] Update project memory with confirmed evidence and new hashes.
