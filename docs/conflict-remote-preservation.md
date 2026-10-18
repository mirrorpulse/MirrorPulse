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
