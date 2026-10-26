# Durable ownership of root Label notifications

This catalog boundary retains the meaning of an original CfSharp root-directory
move notification before the Host invokes the public null-revision ACK. It does
not invoke the SDK ACK or grant namespace readiness itself. The Host's
original-only replay invokes the public ACK as described below. Accepting new
live notifications, public root execution, and their complete native and signed
Host checks remain separate S06 work.

## Accepting an original notification

Acceptance requires an assigned, nonempty original operation ID, a positive
sequence, a directory `Move`, and two valid single-component names. An unassigned
SDK ItemId cannot be filled from a discovered path. The SDK's loss and rescan
signals retain their original meaning.

The caller supplies a fresh complete root inventory, its current role SID, and
the actual SDK root ItemId, full local object binding, raw placeholder identity,
current directory name and inspection time read under protected handles. The
catalog rereads the same unfenced scoped inventory and verifies its fingerprint,
anchor child, every member's role and owner, current placement, topology Label,
name epoch, completed original version-3 Label proof and complete subtree move
receipt. Active and explicitly disabled roots are valid catalog states.

Captured original directories may have no product catalog ItemId. This does not
make the fresh SDK identity optional: it must match the assigned notification,
the retained protected Label proof and the actual directory binding. No file
identity acceptance is fabricated for a captured directory.

The witness records the latest completed protected Label operation, current
inventory fingerprint, role, protection operation, native identity, name epoch,
and hashes of the original Label history and directory receipt. It defines a
local root policy. It does not attribute an SDK notification to the native call
that caused it. A late notification may retain an earlier name pair while the
same native root now has a later verified Label, including a reused name. The
original notification bytes and its observation time stay unchanged.

## Original ownership and ACK return

The original notification ID has one global durable owner. Ordinary Worker
requests, namespace transfers, mutations and coalescing cannot adopt that ID.
One root can retain at most one unfinished root-notification ACK. Its durable
pending row fences ordinary namespace dispatch and another Label preparation.
Repeated acceptance returns the same first start only for the exact original
observation and root; it cannot replace the witness or reset its clock.

Only completion of that immutable first start can record an actual successful
SDK ACK return. It preserves the first return timestamp, accepts idempotent
replay, and clears only the original root-reconciliation or identity-mismatch
block. Absence from a journal page, an empty SDK queue, or a proposed timestamp
does not create this receipt. Completing the row removes its catalog fence but
does not reopen Host work admission or authorize native work.

Historical replay validates the original Label, member, full binding and whole
subtree receipt. Later Labels, name reuse or role rotation cannot reinterpret an
already accepted notification. Reading its metadata grants no current native
authority and never turns an old role SID into a usable token.

The Host binds `MirrorPulseRootLabelJournalRecovery` to its existing running
public feed. Replay holds the shared namespace actor and invokes the exact
original ID with a null revision under the normal Host owner. A successful SDK
return precedes the catalog receipt. Caller cancellation after that return does
not abandon the receipt write. A failed or lost return retains the first start;
if receipt persistence fails after SDK success, another actual idempotent ACK
return is required. An earlier completed receipt retains its original timestamp.

Startup settles already owned pending IDs before role rotation and the current
tree audit. The upload and transfer journal sources classify owned original IDs
before checking nullable SDK ItemId or ordinary/retired Worker ownership. A
stale finite window can therefore retain a completed owned ID without creating
an ordinary Worker request. Replay does not accept an unknown notification,
derive an ItemId from a path, drain caller-held work, inspect native objects,
change revisions, or reopen admission. Disabled-root metadata can settle while
the configured instance remains disabled. New live acceptance still requires
its retained fresh whole-tree/current-role/native proof.

## Storage and bounded discovery

Schema 90 adds `root_label_journal_acknowledgements`. Its canonical payload is
limited to 262144 bytes and has a SHA-256 fingerprint. Indexed original ID, root,
member, Label operation, first-start time and completion status must agree with
that payload. Historical facts are independently checked rather than trusting
only the payload hash.

Recovery pages contain 1 through 64 originals, default 8, ordered by canonical
GUID with an exclusive continuation cursor. Separate partial indexes cover
unfinished status and unfinished JSON payload. Their bounded union makes a
disagreement visible instead of allowing a corrupt status to hide a pending
owner. Selected-root recovery uses the unique pending-status root index and a
derived `(root_id, operation_id)` unfinished-payload index. Both branches seek
this root and read at most two IDs. An exclusive recovery cursor uses an indexed
`operation_id > cursor` predicate, so later pages do not rescan the earlier
pending prefix. The additional index can be rebuilt from original payloads
without changing schema 90, owner bytes or ACK receipts; a substituted definition
is refused. The original schema-90 table and three mandatory indexes retain
their strict loss checks.

`ReadPendingRootLabelJournalAcknowledgementAsync` reads only one stable root's
immutable pending metadata. It grants no current-role, dispatch or native
authority. A healthy selected-root result cannot replace the global integrity
and fresh admission checks, which still reject damaged foreign originals.
Global fence discovery rejects
more than the Host's 4096-root inventory bound. These limits do not close the
remaining S06 requirements for total inventory, SQL, dependency, sorting,
coalescing and per-turn fairness across the complete runtime.

Opening schema 90 refuses a missing or substituted ownership table or recovery
index. A catalog claiming an earlier version cannot adopt a nonempty later ACK
table. An empty table left before migration stamping can be completed without
changing original namespace facts or inventing an ACK owner.

## Validation and remaining integration

Focused private-catalog checks cover enabled and disabled roots across three
catalog owners; original-ID and timestamp retention; dispatch fences; invalid,
unassigned, foreign or stale evidence; index and payload disagreement; rehashed
foreign identity; schema loss and interrupted migration; later Labels and name
reuse. Corruption fixtures explicitly disable SQLite foreign-key enforcement
only on their private injection connection so the reader sees damaged persisted
facts that normal writes already refuse. Canonical identity corruption retains
all original bytes outside the replaced identity fields.

The first expanded check retained 48 passes and five fixture failures. The
corrected 53-case check retained every original case and passed without skips.
Neither check creates a sync-root directory or a CfSharp database, executes a
native rename, installs an App, or proves a successful public SDK ACK.

Runtime integration must classify root notifications before ordinary ownership,
recover accepted original ACKs before startup role rotation, and retain the
normal-owner actor plus fresh protected anchor and whole-tree handles through
new acceptance. It must close and drain root work before adding a durable fence,
avoid waiting on an admission lease held by the same caller, preserve original
SDK loss signals and exact ACK IDs, and require a fresh complete current-role
audit before reopening admission. Public execution, fault cuts, concurrent work,
independent Host restarts and signed Worker behavior remain open.
