# Publication and recovery — Phase 2

Implemented on 10 October 2026. Applies to whole-game cooking, fakelib batch imports (including variants), and AMPR index updates. Analysis remains write-free. Existing standalone binary/file tools are outside this directory-publication protocol.

## What completion means

A cooked result is published only after all selected module/data/param/app0 operations and both legacy reports finish without a module error, relevant input fingerprints still match, and staged output passes inventory/hash verification. `GameResult.Published` becomes true after the commit record is written. A failed module produces reports in a retained `.incomplete` sibling directory; it does not replace the active output or receive a completion marker. The UI labels this incomplete and the CLI returns its existing error exit code.

Unresolved imports, lower-bound references, known-build warnings, and other existing warnings remain report findings. They do not prevent publication. A completion marker proves the file operation completed, not that the game works on a console or that unresolved imports are safe.

Each published directory has `backport_publication.json` version 1: a transaction ID and exact relative-file inventory with byte lengths and SHA-256 hashes. The marker excludes itself. `Publication.Verify(directory)` rejects changed, missing, or additional files. SHA-256 here detects accidental changes; the unsigned marker is not an authenticity mechanism. Legacy report formatting and transformed binary bytes remain unchanged.

Cooking also writes `backport_run.json` version 1. It records engine informational version/revision, runtime, title/content version, effective transform options, explicit selected NIDs/options, exact-versus-lower-bound reference status, reference NID-set hash, name-map hash, source module inventory, and relevant input lengths/hashes. Inputs include the loaded knowledge documents, top-level JSON/C firmware references, selected library inventories/manifests, library app0 files, source modules, param.json, and pattern/app0-related source files. Absence is recorded where it influences the result. The new evidence sidecar uses category labels and relative paths rather than private absolute paths. Existing legacy reports and library metadata retain their existing path/attribution fields; published output is not automatically anonymized.

Relevant inputs are guarded with read-only file handles during cooking and rechecked before publication. Inventory additions/removals are also checked. A loaded knowledge object whose disk files have changed must be reloaded. This deliberately costs file reads, hashing, and open handles; it does not hash every unrelated game asset. Performance measurement and caching remain Phase 5 work.

## Filesystem policy

- Stage and backup are unique siblings on the destination volume. Cooking starts from an empty stage, so files from an older output cannot leak into a new result.
- Existing parent links/junctions/reparse points and link entries inside relevant input/destination trees are refused. The source and output trees must be disjoint in both directions. Output cannot replace knowledge/reference directories or their ancestors.
- On Windows, directory-handle identity resolves normalized volume paths for overlap checks and writer-lock keys. Tests cover extended paths, short paths, SUBST drive aliases, junction parents/children, and output files hard-linked to an input. Hard-linked output children are copied into independent stage files before changes; source bytes remain intact.
- An exclusive stable `.lock` file serializes writers to the same physical destination. Fakelib imports additionally hold a store-wide lock, so imports to different firmware sets cannot race. Locks fail promptly rather than waiting; retry after the other operation ends. Readers are not given a globally atomic snapshot.
- A fakelib variant import stages and replaces the entire containing firmware set. Variant and parent completion records remain consistent. Batch files and their manifest publish together.
- Windows identity behavior was tested on local NTFS. Non-Windows overlap checks use normalized paths plus link refusal. Network shares, case-sensitive Windows directory modes, cloud-synchronized folders, other filesystems, and hostile concurrent filesystem mutation are not certified by these checks.

The implementation rechecks paths and inputs, but it is not a defense against an adversarial process swapping paths between checks and operations. Use local directories under the user's control, and do not edit inputs during a run. File sharing restrictions are strongest on Windows; metadata/content rechecks also detect ordinary changes.

## Replacement and recovery

Sibling control names start `.ps5bp-` and contain a hash of physical destination identity. Transaction stage/backup names additionally contain a random ID. The journal stores a version, ID, whether a destination existed, and whether the transaction committed; recovery derives sibling paths from these validated values rather than trusting paths supplied in JSON.

| Persisted state | Recovery behavior |
| --- | --- |
| Journal created; stage partially written | Remove that transaction's validated stage; leave previous output intact. |
| Prior output moved to backup | Restore backup; discard incomplete stage/new output belonging to that transaction. |
| Stage moved into place; commit record not written | Roll back to backup, or remove the uncommitted new output if no prior output existed. |
| Commit record written | Verify and retain the new output; retain nonempty prior backup; remove the journal when possible. |
| Invalid journal, mismatched new-output identity, or invalid committed output | Stop for manual inspection; retain recovery evidence/backup rather than guessing or deleting unrelated files. |

Files and the completion marker are flushed before replacement. Cancellation is honored before replacement begins. The short critical section moves old output to backup, moves stage to output, and writes the committed journal. Cancellation is deferred during these steps so rollback/recovery has a definite state. This is a recoverable protocol, not one atomic rename of multiple directories: during interruption the active path may be temporarily absent or contain an uncommitted result until recovery runs.

Each next writer recovers its target under the lock before staging. Cooking and AMPR index creation also recover before checking/using the active output. The Core API exposes `Publication.Recover(destination)` for explicit recovery, and recovery is idempotent. A resumed fakelib import recovers the containing firmware set.

After an interrupted run, stop other writers and invoke recovery for the same destination, or rerun the operation that writes it. For manual inspection, identify the destination's journal and its referenced ID, preserve a copy of the journal and backup, and inspect the completion record before changing anything. An invalid journal or marker should be resolved manually; do not delete all `.ps5bp-*` files as a shortcut.

AMPR indexing uses the same staged replacement. It verifies an existing completion marker, checks cooked source fingerprints/module inventory, compares source path/size/mtime layout before and after indexing, and records that layout hash in the run sidecar. Completion/run sidecars are excluded from the AMPR file index. Legacy overlays without provenance can still be indexed; their source identity cannot be established retrospectively. Layout checks are not a content snapshot of every unrelated game asset.

## Retention and space

Nonempty prior outputs are retained as `.backup` siblings after successful replacement. Incomplete module runs are retained as `.incomplete` siblings. The UI logs the prior-output backup path and diagnostic path; Core returns them in `GameResult`. An empty prior directory is discarded after a successful commit. Store/index replacements also retain previous nonempty directories under the same sibling naming convention.

There is no automatic age/count eviction in Phase 2: avoid silently deleting a user's recovery copy. After verifying and accepting the current output, the owner can remove a specifically identified old backup or diagnostic folder. Validate its exact path and ensure no operation is using it first. Stable lock files stay in place, including while unlocked; deleting them could let concurrent processes acquire different lock objects. Committed cleanup that fails is retried by recovery. Journal write temporaries left by an abrupt process exit may require manual inspection/cleanup.

Budget space for the current destination plus a full staged replacement and all retained backups. Variant imports and AMPR updates copy the whole set/overlay. Hashing and flushing add I/O. This favors correctness and recovery over minimal disk usage; measuring and optimizing these costs is later work. Transaction artifacts are Git-ignored.

## What verification demonstrates

Synthetic regressions exercise injected disk-full/write/copy/access failures, a real file-sharing denial, cancellation, competing writers in one and two processes, stale-output replacement, input inventory changes, loaded-knowledge changes, output tampering, malformed journals, batch/variant rollback, and Windows physical aliases/links. Child processes deliberately exit without Dispose/finally at stage creation, backup rename, new-output rename, and committed-journal checkpoints, for existing and new destinations. Recovery is then run twice and verified.

The protocol was tested with process interruption and simulated failure classes on this Windows/NTFS machine. It was not tested by filling a physical disk, cutting machine power, or forcing storage-controller failures. File flushes and recovery journals do not establish directory-entry durability under every power-loss/filesystem configuration. Physical power-loss guarantees require a dedicated hardware/storage test campaign; none are claimed here.
