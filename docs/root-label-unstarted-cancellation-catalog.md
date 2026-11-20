# Guarded unstarted root Label cancellation catalog

Schema 91 adds immutable ownership for cancellation of a sealed version 3 root
Label before its first durable native start. The existing intent, public SDK
proof, sealed subtree, original members and protection history remain retained.
One transaction inserts the complete cancellation witness, marks the original
Label Cancelled, and releases only that operation's pending-root, reserved-name
and predecessor indexes. A failed final Label write rolls every change back.

The Host must supply an actual public NotMoved result for the original SDK proof
while retaining the complete source and physical anchor guards. The catalog
requires every original sealed member, unchanged source positions and public
identities, fresh final guarded facts matching each exact protection epoch, and
no first start, native observation, projection, completion or remote rejection.
The anchor may retain its current verified protection without rewriting the
original move's role or proof. This record supplies no native authority.

Historical reads validate the original cancellation and terminal index state.
They permit later independent Label operations without restoring an old mapping
or taking another operation's reservations. Missing or damaged cancellation
ownership, subtree or schema remains an error. Schema 90 migration preserves old
intents and cannot adopt pre-existing cancellation rows. Older Hosts reject the
new schema instead of interpreting the released ownership as ordinary readiness.

This is a catalog boundary. It does not wire a public cancellation command,
decode or execute an SDK operation, inspect physical objects, rotate permissions,
readmit root work, acknowledge a journal entry or resume an interrupted rename.
The Host/native boundary and clean process/role/fault/concurrency verification
remain required. A written first-start marker is deliberately outside this
unstarted cancellation route, even when a caller claims a later NotMoved result.
