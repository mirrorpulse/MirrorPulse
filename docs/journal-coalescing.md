# Journal coalescing

The production journal pump can combine an unsent file's operations within a
complete, finite CfSharp journal scan. Original operation IDs remain authoritative
for acknowledgement. MP stores a product decision and separate remote effect
IDs; it does not create another local journal.

## Admission and execution

- The planning window includes held operations, metadata and managed-root
  dependencies. An unregistered observation disables new planning for that scan.
- Every member must be dispatchable. Disabled roots, pending conflicts, namespace
  recovery and earlier dependent work retain their original IDs.
- A new plan cannot adopt a previously prepared mutation. Explicit transfer of
  proven never-started intent is a separate catalog operation.
- The original IDs, final content proof, upload-time native binding and remote
  effect IDs are prepared in one product SQLite transaction.
- Upload execution reopens and verifies the same object and bytes. A changed
  object or content cannot replace the retained proof.
- Remote effects use the existing conditional Worker transports and durable
  mutation executor. Content acceptance is persisted before public CfSharp
  confirmation and official projection. An accepted effect is not uploaded again
  while its local completion is being recovered.
- After every effect completes, one public CfSharp acknowledgement call settles
  all original IDs. Later notifications are outside that immutable group.

Already accepted content keeps the existing observation-only policy. Repeated
notifications do not require another upload when the owned native identity,
in-sync state and mutually acknowledged revision still agree.

## Recovery boundaries

The original-ID index finds a retained plan after restart. Official identity
projection may clear a journal item's reference; this nullable change does not
replace the plan's original observation fingerprint or upload-time binding.

Uncertain remote outcomes remain subject to the existing readback and conflict
policy. A conflict retains the entire original group. Retry requires a matching
pending user command and creates a distinct remote-effect attempt. The rejected
effect, original program, content proof and native binding remain unchanged.
Keep-local can authorize the observed remote baseline for an upload or deletion,
including a move followed by an upload. Verification-only and move-only programs
require a complete content decision before keep-local can be implemented safely.

Every rejected attempt has a separate conflict epoch. Authorizing a retry resolves
the whole group's current conflict projection in one product transaction. The
production conflict action shares the instance scheduler with the pump. After the
entire original group is acknowledged, the user command is marked resolved; a
restart can recover that projection after verifying the original IDs are absent.
Other group decisions remain pending until their complete local operation can be
applied; rejecting an action does not acknowledge any group member.

The native production-pump regression injects a failure after effect completion
and before original acknowledgement. It then reopens the library owner and
requires one remote upload, retained native confirmation, and acknowledgement of
every original ID. The normal native, ARM64 and ordinary-user gates require this
test. Passing the component case does not establish installed Host integration
or complete conflict-resolution coverage.
