# Discovery implementation report

## Implemented coverage

- Current-profile `.codex` and distinct `CODEX_HOME` roots; an explicit noncurrent profile does not consume the process user's `CODEX_HOME`.
- Required missing roots and referenced projects, memory vaults, Git worktree metadata, and Git common metadata remain visible as `Exists=false` items with blocker findings.
- Project references from top-level Codex JSON metadata, explicit path-like TOML/YAML settings, and path/root/cwd/workspace columns across supported SQLite schemas.
- SQLite opens with a read-only immutable URI. An adjacent WAL produces an explicit coverage warning because uncheckpointed rows may be omitted. Discovery does not create SHM files.
- Memory vault pointer, direct skill/plugin/marketplace children, `.cc-connect`, `.cc-switch`, Codex/Codex++ AppData, matching MSIX cache/roaming locations, and projectless `Documents/Codex`.
- `.git` directories plus `.git` gitdir and commondir pointer dependencies.
- Installed-location candidates, read-only Codex process-name observation, volume notes, and version files/package metadata without executing discovered programs.
- Codex-managed `.codex/worktrees` are navigated only to bounded depth/count; `.git` pointers and commondir dependencies are retained as required entries. JSON `rootPaths`/`updatedAt` and SQLite `cwd`/`updated_at` provide activity timestamps when parseable; directory mtime is never used as recent activity.
- MSIX package directories and readable `AppxManifest.xml` identity versions are recorded without launching executables. Volume notes include free space and disk numbers only when native probing returns them; unknown is preserved as unknown.

## Deliberate limits

- Discovery scans bounded known locations and top-level Codex metadata; it does not crawl arbitrary user directories or the full memory vault.
- JSON keys and SQLite column names must clearly identify a path/root/cwd/directory/workspace. Unknown schemas remain unclaimed, and unreadable databases/configuration produce findings.
- Immutable SQLite access favors source safety. Active WAL contents are reported as uncertain rather than treated as complete.
- Installed executable version metadata is not obtained by launching the executable. Missing static version evidence produces an informational finding.
- Shared scan state is serialized per service instance, metadata reads are size-bounded, and cancellation propagates through SQLite/worktree traversal.

## Verification

Synthetic fixtures cover explicit-profile isolation, mandatory missing roots, JSON projects, memory pointers, skills/plugins, peripheral and AppData locations, projectless documents, Git worktree dependencies, SQLite project metadata, immutable read-only behavior, and WAL uncertainty.
