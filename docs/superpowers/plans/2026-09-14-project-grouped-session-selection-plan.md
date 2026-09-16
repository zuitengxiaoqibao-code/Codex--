# Project-Grouped Session Selection Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make project-directory groups the default way to select sessions while retaining an optional individual-session view.

**Architecture:** A Core grouping component converts discovered session references into deterministic project groups without filesystem access. WPF wraps those groups in observable rows with tri-state selection; group clicks delegate to the existing indexed session-selection path so sources and duplicate session IDs remain synchronized.

**Tech Stack:** C# 14, .NET 10, WPF, xUnit.

## Global Constraints

- Grouping and checkbox operations must use data already in memory and never access the filesystem.
- Project paths compare case-insensitively after normalization.
- A session associated with multiple project paths may appear in multiple groups but remains one selection object.
- Individual session selection, lifecycle filters and cleanup behavior must remain available.
- Chinese files remain UTF-8 and receive a mojibake scan after edits.

---

### Task 1: Deterministic session project groups

**Files:**
- Create: `src/CodexBackup.Core/SessionProjectGrouping.cs`
- Create: `tests/CodexBackup.Tests/SessionProjectGroupingTests.cs`

**Interfaces:**
- Produces: `SessionProjectGrouping.Create(IEnumerable<SessionReference>)` returning `IReadOnlyList<SessionProjectGroup>`.
- Produces: `SessionProjectGroup` with `Key`, `Name`, `ProjectPath`, `SessionIds`, `LastActivityUtc`, lifecycle counts and problem counts.

- [ ] Write failing tests proving equal paths merge, path casing is ignored, missing paths form one final group, multi-project sessions appear in each related group, and groups sort by recent activity.
- [ ] Run `%USERPROFILE%\.dotnet\dotnet.exe test tests\CodexBackup.Tests -c Release --filter FullyQualifiedName~SessionProjectGroupingTests --no-restore` and confirm the missing component causes failure.
- [ ] Implement grouping from `SessionReference` data only. Normalize local paths with `Path.GetFullPath`; use a stable raw fallback if normalization fails; use `未识别项目` for empty paths.
- [ ] Run the focused tests and confirm all pass.
- [ ] Commit Core grouping and tests.

### Task 2: Observable project selection model

**Files:**
- Modify: `src/CodexBackup.App/MainWindow.xaml.cs`
- Modify: `src/CodexBackup.App/App.xaml.cs`

**Interfaces:**
- Produces: `ProjectSessionGroupRow.Create(IReadOnlyList<SessionProjectGroup>, IReadOnlyDictionary<string, SessionGroupRow>)`.
- Produces: `SelectionState`, `SelectedCount`, `SessionCount`, `SummaryText`, `Rows`, and `Refresh()`.
- Consumes: existing `SetSessionSelection(IReadOnlyCollection<SessionGroupRow>, bool, bool)`.

- [ ] Extend the WPF smoke contract before implementation to require `ProjectSessionSelectionPanel`, `ProjectSessionGroupsList`, `ProjectSessionFilterBox`, and `IndividualSessionSelectionPanel`; require project view visible and individual view hidden by default.
- [ ] Add synthetic groups to smoke data and assert same-directory sessions produce one project group with a checked state.
- [ ] Run the smoke test and confirm it fails because the controls/model are missing.
- [ ] Implement observable group rows and rebuild them immediately after session rows are created.
- [ ] Add a project checkbox handler: checked or partial groups select all; fully checked groups clear all; call the existing indexed selection method once per group.
- [ ] Refresh group summaries after any project, session or source selection change without invoking coverage evaluation.

### Task 3: Project-first session UI

**Files:**
- Modify: `src/CodexBackup.App/MainWindow.xaml`
- Modify: `src/CodexBackup.App/MainWindow.xaml.cs`

**Interfaces:**
- Produces handlers `ShowProjectSessionView_Click`, `ShowIndividualSessionView_Click`, `ProjectSessionSelection_Click`, and `ProjectSessionFilterChanged`.

- [ ] Replace the session panel's default table with a project-directory view containing two view buttons, project status/search filters, and project cards.
- [ ] Each card must show a tri-state checkbox, project name, full path, selected/total counts, lifecycle/problem summary, and a collapsed child-session list.
- [ ] Move the existing session filter controls, batch buttons and `SessionsGrid` into `IndividualSessionSelectionPanel`, hidden by default.
- [ ] Implement project filtering by name, path, child session title, lifecycle, problem state and selected state.
- [ ] Render screenshots and check that repeated directories appear once in the main view and that long paths wrap without hiding selection controls.

### Task 4: Regression and release

**Files:**
- Modify: `docs/release/0.3.5-使用说明.md`
- Modify: `docs/release/0.3.5-验收清单.md`
- Modify: `docs/release/0.3.5-验证说明.md`
- Rebuild: `outputs/v0.3.5/*`

**Interfaces:**
- Produces an updated Windows x64 self-contained EXE, source ZIP, portable ZIP, `RELEASE.json`, and `SHA256SUMS.txt`.

- [ ] Run the focused grouping tests, complete xUnit suite and Release build.
- [ ] Run published EXE self-test and WPF smoke with 1,000 sessions; require `selectionScheduledCoverage=false` and project-group selection below two seconds.
- [ ] Run the real read-only scan and record source/session/project counts.
- [ ] Run UTF-8/mojibake scan, `git diff --check`, PowerShell 5.1 release verification, ZIP integrity checks and extracted portable EXE self-test.
- [ ] Update the project memory card with confirmed evidence and new SHA-256 values.
