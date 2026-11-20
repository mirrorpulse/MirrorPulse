# Cancellation admission after a Host anchor changes role

ARM64 CI40363669794 at `f6766a7` completed the enabled/current-role cancellation
scenario, then failed its older-role sealed cancellation at
`ValidateNamespaceMemberMoveExecutionAsync`. The current physical anchor had
already acquired the new Host role. Its execution lineage check correctly
refused to grant an original old-role move new execution authority.

Cancellation now uses a separate coherent metadata inventory and admission.
`ReadRootLabelCancellationTreeAsync` requires the exact prepared public Label,
original registered source, pending ownership and absence of native progress.
It reads the physical anchor and complete bounded tree in one transaction.
Ordinary protection snapshots and actual execution retain their strict parent
role checks. Only the original first-level root in this cancellation inventory
can retain its old role while its exact physical anchor has a later epoch;
every other current direct-parent role and scope check remains required.

The inventory retains the original
historical intent and every member, parent, proof and predecessor reference;
requires the exact pending Label, target reservation, unfenced original root,
no durable start/progress/terminal result and the complete unchanged sealed
descendant inventory. Only the same physical current-user anchor may have a
newer verified epoch, at its unchanged position. This admission grants no move,
permission write, readiness, SDK reconciliation result or journal ACK.

The Host still holds root/instance/library/actor quiescence and complete
metadata-only native anchor/tree guards through actual public SDK `NotMoved`,
repeated physical/public/identity/descriptor/child-set inspection and the final
atomic cancellation. The final transaction repeats cancellation admission, so
an earlier metadata read cannot authorize later changed ownership or progress.
Actual move execution retains its original lineage checks unchanged.

Catalog tests reproduce an independently retained new anchor epoch and old
descendants across three SQLite owners for sealed and unsealed preparations,
then distinguish rejected execution and ordinary snapshots from permitted
cancellation metadata. They also introduce a durable start, changed
descendant epoch and lost reservation after a valid admission and require final
refusal. These are synthetic catalog facts. The original six-case native
producer and independently restarted signed Host evidence remain required;
managed results alone do not close P078 or S06.

Startup pending discovery retains an original unsealed Label as its own recovery
reference. It must not reserve a new initializer just because no member-move seal
exists. A seal already represented as the same original Move remains one reference;
terminal history cannot hide a newer pending Label. Existing reference values,
ordering, bounded discovery and ownership validation remain in force.

After cancellation, durable selected-root fence metadata determines whether a
separate current-role recovery may begin. The preflight does not require an
already recovered current-role inventory. Recovery still holds its original
ownership and must pass the complete fresh native audit before Active readmission;
Disabled remains closed. Historical cancellation cannot clear a later fence.

Legacy partial initialization and mixed per-object role recovery require their
own retained-owner and native acceptance. The discovery prevention and metadata
preflight alone do not establish that remaining boundary.