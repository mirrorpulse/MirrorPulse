# Version-bound remote conflict copies

`MirrorPulseConflictRemoteCopyStore` captures and preserves the exact remote file
selected by a pending conflict. A durable decision must retain the captured
`MirrorPulseConflictRemoteSource` before starting preservation. It contains the
instance, root key, canonical Worker path, remote object ID, opaque revision and
length. The remote path may differ from the local conflict path after a rejected
move; the caller supplies the selected remote path explicitly.

Capture checks the conflict's observed revision before and after metadata lookup.
Preservation checks the retained object and length before reading, checks the
revision around every range, and rechecks the complete object metadata at EOF.
Empty files still require the final check. Reads are at most 1 MiB; metadata
lookup permits at most 256 pages of 256 entries, rejects oversized or repeated
cursors, and uses the original root key on every Worker request. Short or
overlong content, replacement objects, version changes and cancellation cannot
produce a completed copy.

The private copy store commits this source in its version 2 pending and complete
manifests together with the command ID, content length and SHA-256. A manifest
copied from another command cannot authorize this choice. After the data or manifest commit,
recovery verifies the same private bytes and original source without reopening
the Worker; the remote can subsequently be unavailable or different. A partial
copy must read only the originally selected source again. An older version 1 copy
cannot acquire historical source evidence by upgrading its manifest.

The copy store supplies source and byte verification to conflict decisions.
The production coalesced file-group `KeepBoth` path retains local content and
archives the selected remote version. It first persists the complete original
group, conflict epoch, rejected effect and selected remote source in the Host
catalog. A verified private copy handle excludes writers until the distinct
conditional local retry is authorized. The original execution and rejected
mutation history remain unchanged.

Before dispatching that current retry program, the Host independently reopens
and verifies its selected private copies again. It retains those handles through
remote effects, native confirmation, SDK projection and original acknowledgement.
A missing or modified archive blocks dispatch instead of trusting only its
earlier catalog receipt.

Preservation leaves the original conflicts pending. Authorizing the retry
resolves that exact conflict epoch atomically and releases its active decision
reservation; historical membership and archived bytes remain available across
later choices. The command stays pending while the existing coalesced executor
performs actual remote acceptance, native content confirmation, public SDK
identity projection and whole-group acknowledgement. Only those completed
boundaries resolve the command.

The upload pump recovers up to 64 retained preservation decisions per scan and
continues after an individual failure. Disabled instances and roots awaiting
fresh namespace admission defer source access. Source metadata and range bodies
run in the normal-user transport context. A durable original-retry preparation
receipt avoids rereading complete private copies on every subsequent pump cycle.

This path requires an observed existing remote file version. `KeepRemote`,
single-observation `KeepBoth`, absent-remote variants, directory choices and
multiple remote occupants retain their separate implementation and acceptance
requirements; the coalesced file path does not certify those cases.

## Separate post-rejection version observations

An Adapter's conflict response can omit either revision field. The rejected
request's expected revision and the optional error fields retain their original
meaning. A later successful `Stat` is a separate observation of the current
remote version; it cannot supply missing historical error metadata.

The Host catalog supports an immutable observation history for a complete
coalesced file group. `ReadCoalescedConflictRemoteTargetAsync` captures its
current rejected effect and selected epochs without preparing another effect.
The owner performs the actual root-scoped `Stat` outside the catalog transaction.
`PrepareCoalescedConflictRemoteObservationAsync` can retain the first observation
for a group whose error omitted the actual revision. The transaction creates
one new pending conflict epoch per original and dismisses the previous epochs.
It preserves the original execution, mutation, nullable error facts and journal
IDs. It neither authorizes a retry nor acknowledges any SDK operation.

Selecting another version requires a distinct explicit `refreshconflict` user
command targeting a current pending epoch. The catalog's
`RefreshCoalescedConflictRemoteObservationAsync` appends an observation, links
the complete previous selection, atomically advances the current head and
resolves that refresh command. Active preservation or deletion decisions refuse
replacement. Existing observations remain readable by observation ID. Replaying
an earlier refresh command returns its retained observation without changing a
later current head or contacting the Worker again.

Schema 93 retains observations, their complete epoch membership and the current
head separately. Missing ownership, altered layouts, mixed groups and stale
heads cannot authorize KeepBoth or another retry. An older catalog containing
these future records refuses upgrade; a current catalog missing one of these
tables refuses to recreate it. Failed writes roll back epochs, the head and
command completion together.

These catalog APIs are the persistence boundary. This change does not add a
Control or CLI refresh command or an automatic Host observation pass. Callers
must retain normal root admission, enabled-instance policy and normal-user
transport execution. A null or failed `Stat` cannot select an empty revision or
authorize absence, and refreshing cannot bypass an existing decision owner.
