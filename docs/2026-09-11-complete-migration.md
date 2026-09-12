# Complete migration revision

User authorized autonomous implementation. The priority is preserving the relationship between sessions and source code across custom drives, not merely hashing selected files.

1. Discovery follows explicit configuration, custom CODEX_HOME, TOML project tables, configured session directories, SQLite rollout paths, and bounded session metadata on any local drive. Additional user-chosen data/search roots are supported. Session content is not exposed in diagnostics.
2. Default complete mode requires all discovered existing projects, sessions and dependencies. Missing referenced directories/transcripts and incomplete discovery block the complete label. Recovery/custom mode remains available but is clearly partial. A missing old path can be located with a replacement mapping retained in the manifest.
3. Packages retain session/project relations, logical source paths and aliases. Revalidate coverage against the actual file inventory, not selection checkboxes alone. Preserve old-package support without upgrading its completeness claim.
4. Restore offers original-layout, new-base layout, and isolation. Complete restoration requires all linked roots and maps project/source/session paths together. Verify mapped relations on staged files before writing and on restored files before reporting structural completeness. Re-login and external runtime availability are separate from byte/association checks.
5. UI states explain what happened, what could be lost and the specific next action. Warnings are grouped by missing files, running programs, incomplete discovery, and informational scope. No raw exception class names as primary instructions.
6. Regression tests for external roots, TOML quoted keys, session-only project discovery, excluded/missing linked sources, relocation mappings, strict restore coverage and user-facing explanations. Rebuild EXE and rerun diagnostics; never mutate actual Codex data.

Native application acceptance remains distinct from structural completeness. Do not claim arbitrary project dependencies or unknown future Codex schemas are runnable without testing them.

## Final local validation, 2026-09-12

Implemented and verified with 101 passing tests. Final published self-test, WPF navigation smoke and actual-user read-only scan exited 0. Actual scan resolved C and D drives, 143 project positions and 1154 session association entries (multiple locations/history may reference one session; not unique conversation count). No live user restore performed. Source scan UTF-8 clean. Independent review findings closed; nested ancestor Core remains explicitly blocked in complete mode and multiple Core homes remain independent.
