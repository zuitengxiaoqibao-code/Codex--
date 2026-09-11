# Final independent review

Scope: current uncommitted working-tree CoreRestoreAdapter, RestoreEngine, DirectoryLease, FileIO, JournalProtection, WriterGuard and WPF MainWindow code, with the related synthetic tests. This is not a review of the scaffold commit alone. No live restore or private data scan was performed. Final re-review rebuilt and ran the CoreAdapterTests independently: 13 passed, 0 failed. The delivery agent owns the complete release verification.

## Original blocking finding - resolved on re-review

### P1 - Controlled Core activation accepts unrecognized database schemas

Reviewed locations: `src/CodexBackup.Core/CoreRestoreAdapter.cs:128-147` and `src/CodexBackup.Core/RestoreEngine.cs:28-30`, `:94-102`.

The adapter's schema gate checks table names and only requires TEXT cwd/rollout_path columns in threads. It does not establish a supported migration version/checksum, required identity columns, or a known set of remaining columns. For example, a synthetic SQLite database containing only `CREATE TABLE threads(cwd TEXT, rollout_path TEXT)` passes the gate despite lacking a thread identity column. A known-name threads table with additional unknown state columns is also accepted and those values remain in the active database. Existing unknown-schema regressions add a table or trigger and therefore do not exercise this bypass.

IsCandidate checks that a Core root has state_5.sqlite or sessions entries; RestoreEngine uses that candidate to permit controlled replacement of the actual CODEX_HOME. After PrepareAsync accepts the weak shape, it fingerprints the resulting stage and installs it. Byte verification proves the output was copied, not that this is a recognized Codex state schema. This violates the stated requirement to block unknown semantic migrations before replacement.

Required resolution was to validate against an explicitly supported schema/migration fingerprint with trustworthy provenance, reject missing required or unknown columns/versions, and retain isolated extraction as the fallback for unrecognized production formats. This is now implemented; see the strict-schema follow-up below. A synthetic fixture remains evidence of adapter behavior, not production application compatibility.

## Previously reported findings resolved in inspected code

- Core normalization no longer silently excludes ancestor project tmp/.sandbox content.
- Directory ADS are checked and blocked instead of omitted.
- Restore checks protected target locations independently of package Kind; the deliberate controlled-Core exception is the subject of the finding above.
- Rollback independently checks .original and .rollback and can select an intact fallback.
- SaveJournal now validates the serialized envelope below the reader's limit before writing; prepared journal growth fails before the original switch.

## Directory leases and journal assessment

DirectoryLease opens each existing ancestor with reparse-point semantics and without delete sharing. FileIO holds leases during hashes/copies and durable JSON access; restores hold a parent lease during each switch. This improves the original path check/open race, and the test ParentLeasePreventsDirectoryReplacement directly exercises an ancestor rename failure. Moves refuse existing destinations, and the reviewed code does not delete original/rollback/undo trees.

The sealed journal saves intent before moving originals or installing stages. Synthetic fault-injection tests now cover Prepared, BeforeOriginalMove, AfterOriginalMove and AfterTargetInstall. Tampered journal and intact-fallback regressions are present. These are useful process-level interruption tests, not evidence of power-loss durability or fault injection during rollback itself.

The final rollback follow-up rechecks the current target and selected recovery candidate immediately before the moves, then verifies the recovered original tree before saving RolledBack. The RollbackPrepared mutation regression checks changes between the initial preflight and mutation loop. This closes the previously noted missing final-tree verification. Residual limitation: leases pin directory identities, not every file's contents throughout the operation; preserve the stop-writers requirement and do not claim an atomic filesystem snapshot or universally race-free rollback. No additional deterministic data-loss blocker was identified in this bounded static pass.

## UI async and preview assessment

The reviewed UI captures request values on the UI thread before Task.Run, including the rollback journal path. Awaited continuations update controls on the UI context. RunBusyAsync gates concurrent operations, disables the work pages, and the close handler requests cancellation while keeping the window open. These changes address the obvious cross-thread control-access and concurrent-operation risks.

Restore preview serializes the request as a fingerprint; ExecuteRestore commits edits, reconstructs the request and rejects changed paths/options. Core also re-verifies the package and rebuilds the preview before writing. The fingerprint binds request fields, not the package's bytes, so it should not be described as a cryptographic package identity check. No additional blocking UI bug was found in the inspected handlers.

## Verdict

No outstanding blocking finding in this bounded review after the strict-schema follow-up. The prior omission and rollback-availability fixes are present. Controlled database adaptation is limited to the observed exact schema/migration fingerprint; other formats fail closed with isolated recovery available. This verdict does not establish destination Codex version compatibility, real application activation, clean-machine restore, power-loss behavior or a filesystem-wide atomic snapshot. Complete the delivery verification on the final source and preserve those limitations in the report.

## Strict-schema follow-up

The follow-up adds an exact sqlite_schema fingerprint, user_version=0 and an ordered migration version/checksum/success fingerprint before adapter updates. The fixture now uses parent-authorized observed schema and nonsecret migration metadata with synthetic rows, and regressions cover missing identity, unknown columns, user_version and modified checksums. This addresses the original weak-shape gate. PathSafety also now blocks descendants of Windows and both Program Files directories.

Re-review found and closed a P1 bypass in the initial filter: SQL LIKE treated the underscore in `sqlite_%` as a wildcard, hiding user-created names such as `sqliteX_evil`. Both schema queries now use `name NOT GLOB 'sqlite_*'`, excluding only the literal internal prefix. The hostile `sqliteX_evil` trigger regression is present and unknown objects change the complete schema fingerprint before any path updates.

Independent final verification: `dotnet test tests/CodexBackup.Tests -c Release --filter FullyQualifiedName~CoreAdapterTests --verbosity minimal` rebuilt the current source and passed all 13 tests. This includes the observed-schema positive case, incomplete threads, unknown column, unknown table/trigger (including the wildcard-name attack), changed user_version and migration checksum. Source inspection confirmed both GLOB filters and the pre-update gate ordering. Report whitespace verification also passed.
