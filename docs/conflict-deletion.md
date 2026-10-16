# Deleting remote conflict content

`DeleteRemote` preserves the local file in the private conflict directory, removes
the protected original from the sync root, and conditionally deletes the observed
remote version. It does not queue the preserved bytes for upload. Both single
upload conflicts and owned journal groups use the same deletion service.

## Durable boundaries

1. Retain the explicit user command, logical item, complete original journal IDs,
   current conflict epoch, observed remote revision and local object/byte proof.
   Reserve a distinct remote mutation ID. Rejected effects remain unchanged.
   A preflight conflict may have no mutation ledger record: retain its exact
   public CfSharp observation instead, seeking one bounded page by its official
   sequence. Never invent a rejected mutation or substitute a later path identity.
   An existing unknown or accepted execution still requires reconciliation.
2. Preserve local bytes under the instance's conflict directory and command ID.
   Flush the data and manifest, then verify their hash against the decision. An
   earlier copy for another command cannot substitute for the latest local bytes.
3. Retire the same registered file through the protected namespace coordinator.
   Retain a read guard that denies writes while allowing the controlled deletion.
   The coordinator verifies current parent/child protection, native binding and
   public CfSharp tombstone before settling membership. Never bypass its DACL.
4. Execute the independent conditional remote deletion through the mutation
   ledger. An unknown outcome uses remote readback and does not authorize another
   delete. A changed remote revision keeps the decision and originals unresolved.
5. Reverify the archive and original local absence on replay. Acknowledge all
   retained original journal IDs through CfSharp's public feed, verify their
   absence, then resolve the complete conflict group and command atomically.
   Earlier retry commands for that group become `superseded`.

An interrupted archive, retirement, remote reply or original acknowledgement
retains the decision for bounded Host recovery. Disabled roots and instances
remain offline. Local namespace work uses the Host's execution role; Worker RPC
runs in the normal Host context. Diagnostics contain IDs and closed failure
categories, without file contents, credentials or exception text.

## Preconditions and current boundaries

Destructive file decisions require the current protected Host owner and a
registered file. A missing local file requires the original public tombstone;
absence at a path alone is insufficient. An existing local file must be resident
and match the retained identity, binding, length and hash. A later local edit or
replacement stops the operation rather than discarding newer bytes.

The current decision covers a rejected single file effect and supported owned
file programs. It does not infer permission to delete a second unobserved remote
occupant in a remaining verification step. Directory conflict decisions and
multiple remote occupants require further work.
Default Host tree protection and installed end-to-end acceptance remain separate
verification gates; passing the Core orchestration tests does not satisfy them.

## Another remote change after a deletion decision

A rejected deletion retains its original effect, local archive and retirement.
Its actual remote observation is immutable. Every original journal member receives
a distinct new conflict ID for that rejected effect, so a choice against an older
conflict list cannot silently accept an unseen remote revision. The conflict center
and normal notification path expose the new complete group.

Only a new explicit `DeleteRemote` choice against that current epoch creates a
new command and conditional effect ID. It retains the same target, original IDs
and preservation receipts, and uses exactly the displayed observed revision.
Background recovery cannot authorize a new attempt. A later rejection creates
another epoch; an ambiguous reply requires readback before any new decision.
All authorized commands resolve only after the complete original group is
acknowledged. Previous rejected effects and conflict facts remain available.

An accepted observation of remote absence is verification only: its new effect
checks absence and never issues an unconditional delete. If another object has
appeared, it creates another conflict instead. Older rejected decisions without
a saved remote observation obtain a separately identified current readback and
still wait for a fresh user choice.

## Conflict history queries

Selecting a conflict reads its primary key directly. Group decisions refresh the
conflict center from indexed, instance-scoped pages for each original journal
operation. Pages retain pending, resolved and dismissed epochs and advance by
canonical conflict ID, so status changes do not shift an offset or hide an old
notification that must be removed. Each query returns at most 256 records;
runtime publication uses pages of 64 and observes cancellation between pages.
It does not enumerate another instance's conflict history. These query bounds
do not replace the remaining limits for complete decision replay and journal work.
