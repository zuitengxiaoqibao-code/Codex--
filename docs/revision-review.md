# Complete migration revision review

Reviewed 2026-09-12, independently, against the current uncommitted revision. Static review only; no live restore or live data modifications. Discovery was being revised concurrently, so line numbers describe the inspected snapshot.

Final follow-up status: bounded source recheck confirms all five findings addressed. Finding 1 is resolved by explicit refusal of complete mode for the unsupported nested-Core arrangement, with salvage remaining available; it does not implement linked restoration of that arrangement. Additional Core homes remain independent and are not merged into the active home. Finding 5 now rejects both non-first-line metadata and missing/nonobject payloads. The parent reports 101 tests passing; those tests were not independently rerun by this reviewer. Original finding descriptions below are retained as review history, not descriptions of outstanding defects.

## P1: Preserve the identity of a nested Core directory

Status: closed by fail-closed coverage check (`complete-nested-core`) and explanatory UI guidance.

`BackupEngine.NormalizeRoots`, lines 103-107, folds a Core source into any selected ancestor and promotes the ancestor to `SourceKind.Core`. If a session's project is `D:\work` and CODEX_HOME is `D:\work\.codex`, the complete package contains one Core root at `D:\work`, with state under `.codex/state_5.sqlite`. `CoreRestoreAdapter.IsCandidate` only accepts top-level state/session entries, and `RestorePlanner.CreateMappings` moves the whole ancestor to the runtime Codex home. Thus a package accepted as complete cannot undergo linked restoration. Preserve nested Core location explicitly or preserve its independent physical root; never treat the ancestor as the home. Validate with a synthetic project ancestor containing a Core home.

## P1: Multiple Core homes collide in both planned layouts

Status: closed. One primary Core maps to the active home; additional homes receive separate `additional-codex-homes/<root-id>` destinations. The UI explains that databases are not merged.

`RestorePlanner.CreateMappings`, line 22, maps every Core root to the same `runtimeCoreHome`. Discovery can legitimately return both the default home and an explicit CODEX_HOME/additional home. Both original-layout and new-layout actions consequently produce overlapping destinations that `RestoreEngine.BuildPreview` rejects. The user can create a complete backup of this supported source arrangement but neither standard linked restore action works. Choose an explicit primary home and distinct preserved destinations for additional homes, with an explanation of which home is active; do not merge independent SQLite databases. Test at least two Core roots.

## P1: Relocating a project does not discover its external Git dependencies

Status: closed. Newly located directories join additional scan roots and trigger scanning; Git project detection invokes dependency discovery at the replacement location.

`MainWindow.LocateMissing_Click`, lines 178-181, adds the replacement directory, changes its kind, and refreshes coverage. Only a Core relocation causes another scan. When the old project is absent, `DiscoveryService.AddReferencedProject` could not inspect its `.git` file; locating a moved worktree therefore never invokes `AddGitDependencies` on the new location. `MigrationCoverage.Evaluate` can report zero gaps and inventory validation only requires the project directory, allowing a complete package with the `.git` pointer but none of its external gitdir/commondir data. Later structural restore fails because the referenced metadata has no mapping. Rescan the replacement project and discover dependencies before declaring coverage complete. Test a moved worktree whose Git metadata is outside the replacement directory.

## P2: The missing transcript relocation UI cannot repair its advertised case

Status: closed. `AddSession` now explicitly sets transcript `IsDirectory = false`, and coverage handles both actual missing-session finding codes.

`DiscoveryService.Add`, line 555, sets `IsDirectory = directory || !file`; an absent transcript is therefore represented as a directory. `MainWindow.LocateMissing_Click`, line 174, opens only a folder picker for it. Additionally, `MigrationCoverage.Evaluate`, line 56, exempts `session-file-missing` after successful alias coverage, but discovery actually emits `session-transcript-missing` (line 469) and `configured-session-missing` (line 398). Even a correctly supplied alias leaves those findings as unconditional blockers. Carry expected source type separately from existence and use actual finding codes, requiring replacement content to be rescanned where needed. Test absent individual JSONL and relocated configured session directory separately.

## P2: Discovery and restoration accept different session metadata positions

Status: closed by static recheck. Non-first-line metadata and absent/nonobject `payload` now generate `session-metadata-unsupported` Blockers, which complete coverage rejects. Empty transcript references also block. Truncation is only reported when metadata search fails, avoiding false incomplete reports from a large dialogue after valid metadata. Association deduplication now preserves distinct Core/transcript/project/id combinations.

`DiscoveryService.ScanSessionJsonl`, lines 438-454, accepts `session_meta` in any of the first 32 lines (including root-level metadata fallback). `RestorePlanner.RewriteSessionMetaAsync`, lines 142-151, only rewrites a first-line object with a `payload.cwd`, and silently returns for other accepted shapes. A transcript with a JSON event followed by valid session metadata can be included in a complete package and restored with a success result while its metadata still references the old project path. Align the supported format contracts: either reject unsupported metadata placement during discovery, or perform bounded rewriting of the same accepted shapes and verify the resulting cwd. Test a two-line preamble/metadata transcript and the accepted root-level fallback.

## Scope

The already reported broad skip of missing findings was confirmed narrowed; malformed/truncated metadata now blocks complete coverage. This review did not find evidence of a new overwrite-before-staging or rollback bypass in the touched restore flow. Existing test results were not independently rerun during this review, and this is not an application-level migration acceptance result.
