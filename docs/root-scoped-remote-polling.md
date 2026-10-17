# Root-scoped remote polling

The active poller takes one bounded root turn per enabled Adapter instance and
rotates by stable RootId. The turn advances even when that root is fenced, its
source fails, or an earlier apply still needs recovery. Disabled roots perform
no source enumeration and retain their previous snapshot and pending batch.
The instance scheduler continues to serialize remote apply with other work for
that Worker. This adds root fairness without issuing overlapping Worker calls.

Each production snapshot is bound to InstanceId, RootId and the original Worker
root key. Its filename uses stable IDs; changing a Label does not relocate or
discard the baseline. A pending batch also retains its original directory name,
previous and candidate observations, projection version and SDK fingerprint.
Neither a different source key nor a rename may retarget that pending batch.

Product catalog schema 67 adds a separate pending-owner slot per instance/root.
It preserves legacy instance-only rows without assigning them a new root. The
instance-wide pending query still reports any such owner, so streamed changes
cannot overtake pending polling work. Per-root completion clears only its exact
batch after SDK/product completion and durable snapshot publication.

New projection version 3 uses the reserved Host namespace
`<instance>/poll-root/<root>/<initial>/<final>`. Batch IDs, change IDs, snapshot
cursors and official SDK polling checkpoints are independent across roots,
including roots exposing identical remote IDs and paths. Opaque IDs and
revisions use unambiguous canonical encoding in snapshot fingerprints.
The Adapter wire mapper rejects this reserved namespace. Runtime checks the
current active root and every current/previous entry path before work admission;
the cursor namespace itself grants no dispatch authority.

Existing version 1/2 pending batches recreate their original IDs, fingerprints,
options and instance cursor before any newer source enumeration. With a single
configured root, a legacy snapshot can migrate to that root's durable snapshot;
the old hint is removed only after the scoped snapshot commits. Several
configured roots plus an unscoped legacy snapshot or pending intent remain
explicitly ambiguous: no root reads or adopts that state. Preserve the original
single-root mapping until its legacy work converges, rather than copying its
baseline into every root. Third-party stores exposing only instance slots retain
the single-root path and must implement the root-scoped interfaces for multiple
roots. Multiple roots also require the existing InstanceRoot identity policy.

Deterministic tests cover actual catalog reopen, same-ID roots, stable identity,
disabled and fenced siblings, unchanged content, opaque-value delimiters,
pending mapping changes, legacy replay and all four polling commit gaps. The
existing native cursor fixture additionally exercises interleaved root cursors;
that extension is compiled and still requires disposable native CI. This work
does not complete managed remote birth/replacement/retirement, new-role recovery,
default Host composition, the full P083 budget matrix or S06 acceptance.
