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
- A cross-instance observation retains both source and destination instance/root
  pairs. A blocked routable move still holds later work at either affected name or
  subtree, even if another unregistered observation makes the planning window
  incomplete. Equal root keys in unrelated instances remain independent. Ordinary
  coalescing cannot adopt a cross-instance move; its persistent transfer lifecycle
  must first prove the copy and preserve the source through any uncertain result.
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

## Unchanged bytes with update notifications

CfSharp conservatively reports file modification notifications as content updates.
A round-trip rename can therefore retain both move and update observations. MP
keeps that original `UpdateFile` plan and every original ID and kind. It can select
a version 2 verification execution only when guarded resident bytes, the exact
native object and current placeholder identity match a real acknowledged upload,
its accepted revision and successful native confirmation receipt. The execution
retains this observation separately from upload content; it cannot discard a
prepared original intent or replace an already frozen execution.

The conditional remote version is still checked. An unchanged source requires no
upload. Public CfSharp content confirmation rechecks the accepted bytes with
identity preparation disabled. Reconfirmation does not create another remote
acceptance proof or overwrite the historical receipt. A changed source retains
the entire conflict group; only an explicit keep-local decision authorizes a new
conditional upload and content proof.

Already accepted notifications without moves keep the existing fast observation
path, preventing confirmation echoes from producing another program. Accepted
online-only placeholders are not hydrated merely to classify a notification.
The pump attaches to CfSharp's single shared feed and stops it only when it started
that feed itself; the filesystem remains its final lifetime owner.

## Recovery boundaries

The original-ID index finds a retained plan after restart. Official identity
projection may clear a journal item's reference; this nullable change does not
replace the plan's original observation fingerprint or upload-time binding.

Uncertain remote outcomes remain subject to the existing readback and conflict
policy. A conflict retains the entire original group. Retry requires a matching
pending user command and creates a distinct remote-effect attempt. The rejected
effect, original program, content proof and native binding remain unchanged.
Keep-local can authorize the observed remote baseline for an upload or deletion,
including a move followed by an upload. A keep-local absence decision replaces
the rejected verification with a conditional remote deletion. A keep-local
unchanged-name decision captures resident local bytes, the current logical item
and native object, then retains an explicit upload proof. Its versioned retry
record preserves the original verification program. Normal retries retain that
authorized effect and proof, including after a lost reply or restart. Every prior
authorization and the original rejected baseline are checked during replay.
For a move-only program, keep-local captures resident bytes from the original
logical item and reserves two distinct effect identities in a version 3 decision.
The original move preparation and rejected mutation remain immutable. A validated
dispatch view adds a conditional upload after the authorized move; its baseline
is the revision actually accepted by that move. No upload is prepared until the
move and its local completion are acknowledged. A lost upload reply uses the
existing readback path without moving or uploading again. Retrying a rejected
move retains the content suffix and proof. A later explicit keep-local decision
can capture new bytes after either the move or upload conflicts.
If the observed remote source has disappeared, the decision instead verifies its
absence before conditionally creating the target. A newly occupied source or
target becomes another conflict; local bytes and the unrelated target remain.

Catalog schema 63 reserves each future upload identity independently of the
immutable original steps. Upgrade retains old retries without inventing a content
decision. Replay checks the suffix index, payload digest, complete authorization
history and original rejected baseline. Every current effect must complete before
the original group can be acknowledged or its user command resolved.

Group preparation reads the mutually acknowledged revision from the official
store before the executor checks the remote condition. A changed source therefore
becomes a conflict of the durably owned group. The ordinary single-operation path
continues to reject a changed remote during its preflight.

Every rejected attempt has a separate conflict epoch. Authorizing a retry resolves
the whole group's current conflict projection in one product transaction. The
production conflict action shares the instance scheduler with the pump. After the
entire original group is acknowledged, the user command is marked resolved; a
restart can recover that projection after verifying the original IDs are absent.
Recovery checks at most 64 groups per pump cycle and retains a cursor across
cycles. An incomplete, ambiguous or damaged group keeps its pending command and
reports `JournalGroupRecoveryFailed` in pump health. Independent groups and
ordinary journal dispatch continue. Repairing the group clears its recovery
fault only after its current authorized effects and original acknowledgements
are verified. Shared official-store failures still fail the source read, and
cancellation propagates. Diagnostics retain the plan ID, failure category and
HRESULT without paths, content or exception text.
`DeleteLocal` uses a separate [durable local-only workflow](conflict-local-removal.md).
It preserves the original bytes and retires the registered object before the
whole group's acknowledgement, with no remote effect. Other group decisions
remain pending until their complete local operation can be applied; rejecting an
action does not acknowledge any group member.

The production planner may transfer explicitly supplied original `Prepared`
intents only when their retained execution evidence is `NeverStarted`. It commits
the superseding history, original ID ownership, final content and complete effect
program in one product transaction. A later effect-ID collision or a changed
baseline or object binding rolls back the entire transfer. Unknown, started,
accepted and rejected original intents retain their existing execution path.
The transferred original payloads remain immutable even when later edits change
the final content hash; a current path cannot replace their historical binding.

The native production-pump regression injects a failure after effect completion
and before original acknowledgement. It then reopens the library owner and
requires one remote upload, retained native confirmation, and acknowledgement of
every original ID. The normal native, ARM64 and ordinary-user gates require this
test. Passing the component case does not establish installed Host integration
or complete conflict-resolution coverage.

The move-only KeepLocal case retains the complete real rename-notification window.
It requires an original single-move execution, a distinct authorized move and upload,
and the move's actual accepted revision as the upload precondition. It checks
native content confirmation, retained rejected history and seed proof, and all
original IDs across a second library owner. An unexpected content notification
is retained in the original plan; the fixture does not remove it to manufacture
a move-only plan. An immutable MoveAndUpdateFile plan can select a version 3
single-move execution only when a prior completed upload, confirmation receipt,
current native binding/identity, and complete byte hash prove unchanged content.
The original plan and every notification remain unchanged.

Catalog schema 65 prevents an older runtime from interpreting the new execution
or its move-content identity provenance. Ordinary observed moves confirm the
same bytes against the actual move-accepted revision through public CfSharp
content confirmation, before original acknowledgement. This is an accepted move,
not another upload. A narrow catalog check binds its content proof and registered
identity transition to the original observed execution. Explicit KeepLocal
move-and-upload decisions confirm only their later upload. Historical seed
acceptance and receipts remain immutable. Lost move replies without a retained
accepted revision stay unresolved; target presence alone is insufficient.

## Final admission scheduling

The Host captures a finite public journal boundary, reads that complete window,
and classifies its original observations before final admission. Final admission
then processes unsupported-result bookkeeping, held dependency indexing, Worker
request fingerprints and filtered complete-window projection in bounded turns.
`admissionOperationsPerRead` defaults to 2048 and accepts values from 1 through
4096. Each unit visits one original record in its current admission stage.

An unfinished turn returns `AdmissionInProgress`, the original window boundary,
empty ready commands and no exposed coalescing window. Durable request
fingerprints may already exist at this stage, after complete classification.
Worker execution and ordinary file acknowledgement wait for the complete batch.
Later notifications retain their original journal entries for another finite
scan. Feed loss is checked on every continuation turn.

Unsupported metadata retains its durable product result before a null-revision
CfSharp acknowledgement under namespace work admission. This records the
content-only policy without accepting remote timestamps or attributes. Each
original is settled separately so one large metadata group cannot occupy the
whole turn. Already settled metadata keeps its result if cancellation or failure
discards the ephemeral continuation; retry reads the official pending journal.
All file IDs, sequences, immutable request fingerprints and dispatch ownership
retain their existing boundaries. A failed admission never publishes a partial
batch, and concurrent advancement cannot take ownership from the current call.

Dispatch rechecks current root availability, Worker availability, durable
namespace fences and the operation's pending conflict. A request admitted in an
earlier scheduling turn cannot bypass a conflict created before dispatch. The
existing instance scheduler, native binding checks, conditional effects and
accepted-effect recovery still govern execution and acknowledgement.

These budgets limit admission units per turn. The complete input and plan are
still retained, global namespace-fence reads still materialize their results,
and the scheduler still performs complete-window dependency checks. End-to-end
memory, database-work bounds and the full recovery/fairness matrix remain
separate acceptance requirements.
