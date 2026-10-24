# Remote identity generations

An Adapter's opaque remote ID identifies its backend address. The backend may
reuse that address after a completed transfer or original remote deletion removes the source. The
Host must preserve the retired public item and its historical receipts while
publishing a distinct replacement item at the reused backend address.

The default Host supplies the generation resolver to root routing and polling.
Incoming streamed batches select their public addresses under the running
library owner before entering the existing retained-batch apply path. Selection
does not grant namespace admission or claim successful native publication.

## Selection and historical addresses

The initial public address retains its existing encoding. A successor uses the
internal `mp3` encoding, binding the instance, stable root, original root key,
generation and exact backend ID. Changing a Label or remote revision does not
change that generation's public item ID. Worker calls decode the original backend
ID; the Worker receives neither a substituted path nor the public `mp3` value.

Product catalog schema 86 retains immutable successor history and an indexed
current pointer. Selection requires the exact completed transfer retirement,
native absence, official SDK tombstone, original source-name epoch and successful
original journal acknowledgement. It compares the expected predecessor before
advancing the pointer. A pending or ambiguous retirement cannot authorize a
successor. Historical member, transfer and retirement records stay unchanged.

Schema 88 indexes original remote file and empty-directory retirements by their
captured public item. New leaf admissions retain reference version 2, including
the original root key. Successful settlement records the name epoch in the same
transaction that retains the verified SDK outcome and releases the original
member name. Generation selection version 2 requires that exact completed
retirement, root, instance, public address and settled epoch. The original SDK
outcome proves native absence and its exact public tombstone. It does not borrow
a transfer journal acknowledgement. Rejected deletions keep their current item;
pending deletions reject hydration callbacks and replacement selection.

Population and current upload proofs select the current identity. Hydration
rejects a callback for a retired generation before reading replacement backend
bytes. A callback alone cannot allocate a successor. Transfer replay validates
its frozen original source and destination addresses; it does not substitute
today's current address into an old operation.

## Polling and incoming replay

Root polling projection 4 freezes each candidate observation's selected public
address in the durable pending intent. The first upsert of a replacement
generation carries no previous generation's revision. A delete uses the previous
snapshot's captured address. Replay reads the original addresses without fresh
source enumeration or new generation selection.

Projection 1, 2 and 3 pending work retains its original projection and cursor.
When all observations still use their initial public identities, projection 4
preserves the exact projection 3 fingerprint. Merely upgrading an unchanged
snapshot cannot advance the official SDK checkpoint.

Schema 87 adds immutable incoming projections keyed by instance and batch ID.
Each stores the original wire-input fingerprint, referenced root mappings,
selected per-entry addresses and the reconstructed SDK batch fingerprint. The
record remains after the incoming pending slot completes, so a completed retry
cannot bind to a later generation. A different wire input, changed referenced
mapping or different reconstructed fingerprint rejects replay. Adding an
unrelated root does not invalidate a nonempty batch.

Pre-upgrade retained batches and public SDK progress preserve their original
legacy projection, including already completed SDK batches. An earlier incoming
owner must converge before a later batch selects its addresses. A delete reads
the selected address without allocating a replacement. Native apply, actual SDK
progress, safe cursors and root admission remain separate verified boundaries.

## Migration and current limits

The migrations preserve existing generation and retirement history. Missing or
malformed required tables and populated future tables in an older catalog reject
opening. A newer catalog cannot be opened by an older Host. Preserve a stopped
Host's complete private data-root backup before changing Host versions; reverting
the executable alone does not downgrade the catalog.

The successor proofs cover completed transfer-source retirement and new original
remote leaf retirement. Legacy remote reference version 1 retains its original
bytes and outcome but has no captured root key and settled epoch; it cannot
authorize a successor. Opening an older catalog builds only the item index and
does not invent these facts from today's path. Recursive remote tree retirement,
controlled retirement, and delete followed by recreation within one incoming
batch still require their own lifecycle and recovery verification. These paths
must not infer an original proof from today's name or tombstone.

Managed SQLite, routing and replay tests cover these component boundaries. Full
signed Worker, clean Windows native, restart and later-generation acceptance must
use the same candidate source before claiming the complete workflow. See
[persistent namespace transfers](namespace-transfers.md) and
[root-scoped polling](root-scoped-remote-polling.md).
