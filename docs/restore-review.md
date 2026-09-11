# Restore safety review

Scope: current working-tree RestoreEngine.cs, JournalProtection.cs, RestoreTests.cs, and resolution of the earlier two package findings. Static review only: no real backup/restore, no private source inspection, and no full-suite rerun while discovery is changing. Line references refer to the reviewed source before follow-up edits.

Verdict: changes required. Staging all roots before switching, retaining both an independent rollback copy and renamed original, non-overwriting moves, and a sealed journal are good foundations. They do not by themselves establish live-target isolation or recovery for every accepted package size.

## Findings

### P1 - Live-home protection depends on an untrusted package classification

Location: `src/CodexBackup.Core/RestoreEngine.cs:35-41`.

The live CODEX_HOME/default .codex/AppData overlap guard executes only for package roots declared Core, Application or Tool. Project, Memory, Skill, Plugin, Environment and Custom roots bypass it, even with Isolated=true. Package Kind is attacker-controlled metadata; refreshing the manifest completion hash makes a changed classification structurally valid. A normal Custom package has the same bypass without tampering. RestoreEngine also never invokes WriterGuard.

Concrete safe reproduction: create a generated fixture directory and temporarily point CODEX_HOME at it within an isolated test process. Construct a valid Project package, map it to the fixture CODEX_HOME (or a child), and set Isolated=true and ReplaceExisting=true. BuildPreview permits it. This writes into the purported live Codex home under an isolated-mode promise. Do not reproduce against actual user homes.

Fix: derive live-directory restrictions from target paths for every mapping, irrespective of package Kind. In this preview release, block automatic writes to live Codex/application locations for all kinds. Keep Kind-based warnings as additional policy, not the security boundary. Add target-overlap regressions for Project and Custom, including targets that contain the live home.

### P2 - A damaged retained original blocks an intact verified fallback

Location: `src/CodexBackup.Core/RestoreEngine.cs:156` and `:166`.

Rollback selects .original solely by existence and never considers .rollback when that first copy fails its fingerprint. A restore deliberately creates and verifies an independent .rollback, but the recovery path cannot use it in this common failure case. A process with a surviving handle to the renamed original, or accidental edits to .original, can trigger it.

Reproduction: restore a synthetic target with replacement; edit one file under the generated .original sibling; leave both restored target and .rollback unchanged. RollbackAsync rejects the operation despite the independent rollback copy matching OriginalFiles. The original target content remains recoverable manually, so this is an availability failure rather than demonstrated byte loss.

Fix: validate candidates independently and select a matching one; preserve the damaged candidate without deletion. Pin/recheck the selected candidate before moving it and verify the final restored original after the move. Add regression for corrupt .original plus intact .rollback, and reject when neither matches.

### P2 - Accepted inventories can produce journals the rollback reader refuses

Location: `src/CodexBackup.Core/RestoreEngine.cs:139`, `:228`, and `src/CodexBackup.Core/JournalProtection.cs:18-20`.

The writer has no journal size limit, while RollbackAsync rejects envelopes above 256 MiB. Restore embeds every FileRecord in RestoredFiles and additionally OriginalFiles for replacement; JournalProtection serializes that indented JSON again as an escaped string inside the envelope. The accepted one-million-entry inventory easily exceeds 256 MiB in this representation, especially with an existing tree or long relative paths. Restore can therefore switch targets successfully and return a journal its own rollback command cannot read. The multiple full string/byte copies also amplify peak memory during every state update.

Reproduction design: generate a journal from many small synthetic FileRecord entries (no need for one million actual files), serialize it through the production envelope path, and compare UTF-8 envelope size with 256 MiB. A boundary test should show that any journal accepted for switching can be opened by the reader. Do not allocate an oversized fixture merely to establish the arithmetic in routine CI.

Fix: introduce a consistent bounded journal representation, or preflight the final prepared envelope against the reader's limit with room for subsequent state changes before the first original is moved. Surface a clear unsupported-size failure while all originals remain at their targets. If separate inventory files are used, authenticate and validate them as part of the journal.

## Prior package findings

- Resolved in inspected source: Snapshot no longer has implicit Core name exclusions, so promoting an ancestor's writer classification cannot drop its tmp/.sandbox content. The new CoreChildCannotCauseParentProjectTemporaryNamedDataToDisappear regression exercises normalization plus snapshot.
- Resolved in inspected source: ValidateSourceType now enumerates ADS for directories. DirectoryAlternateDataStreamsMustNotBeSilentlyLost exercises the prior omission.
- New source-mutation and post-copy cancellation regressions are present. The main agent reported red/green validation; this reviewer did not independently rerun those tests.

## Crash and race assessment

The ordinary interrupted switch states retain recoverable bytes: after moving an original but before installing a stage, .original remains; after installing, the target and .original/.rollback remain. Repeated rollback can recover an interrupted move when the original target is absent, and non-overwriting Move guards protect existing auxiliary paths. No deletion path was found in the reviewed restore implementation.

Missing acceptance evidence remains for process termination at each saved switch/rollback state, multi-root partial application, interruption during rollback, and post-preflight concurrent changes. Fingerprint checks are preflight observations, not held filesystem locks; notably all entries are checked before later sequential moves. Preserve the limited guarantee in user-facing claims, and prefer operation fault-injection tests over real data experiments. Source timestamps and attributes are applied, but MatchTreeAsync compares only structure, length and file content, so it does not establish complete metadata fidelity.
