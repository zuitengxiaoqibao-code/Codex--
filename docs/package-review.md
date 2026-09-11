# Package safety review

Scope: the current working-tree versions of Models.cs, BackupEngine.cs, PackageVerifier.cs, FileIO.cs, InventoryStore.cs, PathSafety.cs, WriterGuard.cs and PackageTests.cs, against implementation-plan.md global constraints and unit B. Restore implementation is outside this review. No real user data was scanned and no real backup/restore was run. This was a static review; the reported existing 18 PackageTests passes were not independently rerun.

Verdict: changes required before claiming complete selected-source coverage. The package has useful fail-closed checks (mandatory dependencies, copy hashes, second source pass, incomplete staging, inventory graph validation, relative-path restrictions, read-only SQLite and completion hashes), but the following omission paths violate the stated completeness contract.

## Findings

### P1 - Root deduplication applies Core exclusions to an unrelated ancestor

Location: `src/CodexBackup.Core/BackupEngine.cs:99-100`, with exclusion application at lines 120-121 and 136-139.

When a selected Core directory is nested inside a selected Project/Custom directory, NormalizeRoots keeps the ancestor but changes its Kind to Core. Snapshot then interprets the ancestor's top-level names as Codex runtime names. Example fixture: select `fixture/project` as Project and `fixture/project/.codex` as Core; place unique project data in `fixture/project/tmp/important.txt`. The normalized root becomes Core, and `project/tmp` is excluded even though it is unrelated to `.codex/tmp`. Both snapshots repeat the omission, the payload verifier only sees the reduced inventory, and BackupAsync publishes COMPLETE.json. The exclusion report is not a substitute for preserving selected project content.

A related case is explicitly selecting a required dependency inside a real Core excluded directory: dependency-ID validation passes, normalization folds it into the Core root, then Snapshot excludes it anyway. Selection currently does not guarantee coverage.

Fix: keep content exclusion scope tied to actual selected Core source paths, independently of writer-guard classification. Explicitly selected required sources must override an exclusion or cause a clear blocking conflict. Add synthetic regressions for an ancestor Project plus nested Core, and a required child under a default excluded directory. The tests involving Core need an injectable writer guard or equivalent isolated seam so a developer's running Codex does not invalidate them.

### P1 - Directory alternate data streams are silently omitted

Location: `src/CodexBackup.Core/FileIO.cs:66`.

ValidateSourceType returns for every directory before FindFirstStreamW. NTFS directories can carry named data streams, and the package records only an empty directory entry. Example fixture on NTFS: create `fixture/source/folder`, then use a FileStream to write unique bytes to `fixture/source/folder:important`; select `fixture/source` as Project. Enumeration does not yield ADS as child entries, both source snapshots skip the directory stream check, and a completed package contains none of those bytes. This contradicts the deliberate fail-closed policy for file ADS at lines 67-80.

Fix: enumerate streams for directories as well as files and block named streams unless they are explicitly preserved and verified. Add a synthetic NTFS directory-ADS fixture and require failure with no completion marker. Keep all fixture mutations under the generated test tree.

## Validation gaps relevant to unit B

- PackageTests tests pre-cancelled input, but not cancellation during copying or final verification. It does not test changing source bytes/metadata or added/deleted entries during the operation.
- Special-file coverage currently exercises an exclusive file lock; no ADS, sparse/EFS/offline, or reparse fixtures are present.
- Relative-name tests directly call ValidateRelative. They do not prove that a crafted inventory with refreshed completion hashes is rejected through VerifyAsync (duplicate paths, parent-file conflicts, traversal, unsupported table/schema).

These are missing evidence, not claims that every listed scenario currently fails. At minimum add regression tests for both findings before acceptance. A later verification pass should retain the explicit distinction between file-level package validation and unperformed real Codex/clean-machine restore validation.

## Additional boundedness observation

InventoryStore.Read checks file size and row count, but individual SQLite text values are materialized without length caps, and BackupEngine's inventory write has no cancellation checks. These can cause disproportionate memory use on hostile metadata or delayed cancellation on large inventories. Consider enforcing column lengths before materialization where feasible and passing the cancellation token through inventory writing. This is lower priority than the confirmed silent omissions above.
