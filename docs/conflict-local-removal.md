# Local-only upload-conflict removal

`DeleteLocal` is a durable local-only choice for a rejected file operation or a
complete coalesced file group. It preserves the authorized resident bytes in the
private conflict-copy store, retires the same registered native object, then
acknowledges all original official journal IDs together. It sends no remote
mutation, creates no remote-acceptance receipt, and leaves remote content intact.
An enabled Worker is unnecessary; an explicitly admitted disabled root can finish
this local recovery.

Catalog schema 64 retains the command, exact conflict epochs, complete original
IDs, original rejected effect, group fingerprint where applicable, and the local
object/byte proof. The original execution and rejected effects remain unchanged.
The local phases are `Prepared`, `Preserved`, `Retiring`, `Retired`, and `Completed`.
The conflict and user command resolve only after official whole-group
acknowledgement. A lost acknowledgement reply can replay that boundary; it does
not authorize another local deletion or a remote delete.

The Host reuses protected preservation and retirement. Before removal it checks
the native binding, logical identity, current member location, latest captured
bytes, and parent/child protection. Existing absence requires the same started
retirement and the original public tombstone; path absence alone is insufficient.
Recovery revalidates the private copy and original absence before acknowledgement.
A replacement object or later content change leaves the choice incomplete.

Immediately before the controlled native deletion, the Host arms one stable,
item-scoped public CfSharp delete-echo suppression. Its identity belongs to the
original local-only command, so a pre-native retry updates the same suppression
rather than creating duplicates. The native regression waits for that exact
public suppression record to be consumed and checks that no pending delete echo
can reach the remote transport. This is distinct from acknowledging the original
user observations. No SDK private table or payload is decoded.

A retained choice owns its original IDs and excludes competing retry or remote
deletion decisions. Pump recovery reads at most 64 local-only decisions per page,
rotates through failures, and reports sanitized recovery faults without paths or
content. Cancellation remains cancellation. The existing remote-deletion recovery
uses the same bounded reader with its own phase and remote-availability policy.

The native component cases cover a group and a single preflight conflict,
interruption before original acknowledgement, two SDK/store owners using the same
execution role, preserved local bytes, unchanged remote bytes, disabled-root
recovery, and consumed deletion echo. Their presence in the required catalog does
not establish that they have passed. New-role handoff, default Host composition,
directory decisions, and installed-process coverage remain separate acceptance
requirements.
