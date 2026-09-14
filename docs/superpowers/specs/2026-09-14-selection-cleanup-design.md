# Responsive selection and safe cleanup design

## Problem

Selecting a session performed repeated full scans of every session/source relationship, refreshed both grids for each row, and ran the complete migration coverage calculation several times. Bulk selection therefore blocked the WPF dispatcher. The backup list also mixed unrelated content types, and there was no safe way to remove rebuildable leftovers from archived projects.

## Selection model

Discovery produces a stable in-memory session-to-source index. The reverse source-to-session index and selected reference counts make checkbox changes proportional to the affected relationships. A bulk command mutates all rows inside one update scope and refreshes the source view, session view, summaries, and coverage once. Coverage refresh is coalesced at dispatcher idle priority.

Shared sources remain selected until the last selected session that references them is cleared. Required personal data cannot be cleared. Deselecting a source clears sessions that depend on it so the UI cannot claim that a session is complete while its project or transcript is omitted.

## Backup categories

The backup page uses top tabs as a type switch: sessions, projects, memories, Codex personal data, skills, plugins/tools, reinstallable/other items, and cleanup. Each tab shows selected and total counts after scanning. The session tab retains lifecycle and completeness filters plus bulk actions.

Codex/ChatGPT binaries, installation folders, application caches, and ordinary logs are reinstallable and are not required. Sessions and their local index/state, project source, Git metadata, personal skills, memory vaults, plugins, and personal path/configuration data remain protected. System policy files are preserved for review when they contain configuration, but restore does not write them directly into ProgramData.

## Cleanup safety

Cleanup is a plan made before backup and an action taken only after the current process has created and verified a package containing the candidate. Low-risk candidates are recognized rebuildable top-level directories under an archived session's project, such as `bin`, `obj`, `node_modules`, cache folders, and build output. A full archived project can also appear as a red high-risk candidate when it has a Git or build-system identity marker; it contains source and is never selected by the bulk action. Transcripts, Core databases, memory roots, user-library roots, and system roots are never candidates. A directory shared with, containing, or contained by an active session project is locked.

The action moves selected candidates to a same-volume `.codex-backup-quarantine` folder. It does not permanently delete them. A UTF-8 JSON journal records original and quarantine paths, and restore refuses to overwrite a recreated original path.

## Verification

Core regression tests cover shared selection references, a 1,000-session batch, backup-scope policy, candidate boundaries, active-session locks, quarantine, and journal restoration. WPF smoke checks all tabs and runs 1,000-session batch and single-selection timing through the real window code. Build, self-test, real read-only scan, mojibake scan, release verification, and package hashes remain release gates.
