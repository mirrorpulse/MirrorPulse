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

This is a preservation boundary for conflict decisions, not a completed
`KeepBoth` or `KeepRemote` operation. It issues no mutation, changes no conflict
status and acknowledges no journal ID. The production group decision still needs
to persist its authorization and original membership, verify preservation before
authorizing subsequent effects, and settle the complete original group after
their actual completion. The existing group guards remain until that workflow is
connected and verified.
