# Zero-work Selection and Backup Layout Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make every post-scan checkbox interaction immediate and reorganize the backup page into a clear four-stage layout.

**Architecture:** WPF bindings receive property notifications for changed items. Direct checkbox handlers update the precomputed `SelectionCoordinator` and lightweight counters; coverage and filesystem validation stay behind explicit commands. XAML uses reusable card, section-title, tab, and grid styles.

**Tech Stack:** C# 14, .NET 10, WPF, xUnit, release diagnostic smoke tests.

## Global Constraints

- Preserve UTF-8 Chinese text and use `apply_patch` for edits.
- Do not read or write user source files in a checkbox handler.
- Keep mandatory-source locking and session dependency closure.
- Run full tests, Release build, WPF smoke, real read-only scan, mojibake scan, and release verification.

---

### Task 1: Observable source selection

**Files:**
- Modify: `src/CodexBackup.Core/Models.cs`
- Test: `tests/CodexBackup.Tests/MigrationCoverageTests.cs`

**Interfaces:**
- Produces: `SourceItem.Selected` and `SourceItem.Required` property notifications.

- [ ] Add an xUnit test that subscribes to `INotifyPropertyChanged`, changes `Selected`, and expects `Selected`, `StatusText`, and `HasProblem` notifications.
- [ ] Run the focused test and confirm it fails because `SourceItem` is not observable.
- [ ] Implement property-backed selection and required state with dependent notifications.
- [ ] Run the focused test and the complete xUnit suite.

### Task 2: Remove checkbox validation work

**Files:**
- Modify: `src/CodexBackup.App/MainWindow.xaml.cs`
- Modify: `src/CodexBackup.App/MainWindow.xaml`
- Modify: `src/CodexBackup.App/App.xaml.cs`

**Interfaces:**
- Produces: `SessionSelection_Click`, `SourceSelection_Click`, and `CleanupSelection_Click`.
- Removes: checkbox-driven `ScheduleCoverageRefresh` and grid-wide refreshes.

- [ ] Extend `--smoke-test` so it fails when session selection creates deferred coverage work.
- [ ] Run the published-shape smoke and confirm the current implementation fails the new assertion.
- [ ] Replace edit-ending hooks with direct checkbox handlers.
- [ ] Update only bound rows and counters; show that validation is pending without evaluating coverage.
- [ ] Run smoke after dispatcher idle and confirm selection remains responsive with no scheduled coverage.

### Task 3: Recompose the backup page

**Files:**
- Modify: `src/CodexBackup.App/App.xaml`
- Modify: `src/CodexBackup.App/MainWindow.xaml`

**Interfaces:**
- Consumes: all existing named controls and click handlers.
- Produces: four-stage card layout, compact session/source grids, collapsed findings detail.

- [ ] Add palette, card, section-heading, segmented-tab, compact-button and data-grid styles.
- [ ] Rebuild the backup page with consistent 24-pixel card spacing and a dominant selection region.
- [ ] Remove technical identifiers and transcript paths from the primary session table while preserving search and technical details.
- [ ] Render the WPF smoke screenshots and visually inspect welcome and backup pages.

### Task 4: Release verification

**Files:**
- Modify: `docs/release/0.3.5-验证说明.md`
- Rebuild: `outputs/v0.3.5/*`

**Interfaces:**
- Produces: updated self-contained EXE, source ZIP, portable ZIP, metadata, and hashes.

- [ ] Run 1,000-session batch and single-selection smoke through dispatcher idle.
- [ ] Run real read-only scan and verify session/project counts without modifying data.
- [ ] Run xUnit, Release build, UTF-8 scan, `git diff --check`, and `scripts/verify-release.ps1`.
- [ ] Rebuild release metadata and SHA-256 files from the verified artifacts.

