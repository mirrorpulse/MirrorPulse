# Content synchronization policy

MirrorPulse synchronizes file bytes and supported namespace operations. An
in-sync file means its content has been accepted by the configured source. It
does not promise that creation/access/write timestamps, DOS attributes, ACLs or
other platform metadata have been accepted by that source.

The product explicitly registers `CloudInSyncPolicy.None` and
the Shell's `StorageProviderInSyncPolicy.Default` value of zero (the WinRT enum
has no named `None` member).
This disables native metadata tracking for
the in-sync bit; native tracking of file data writes remains active. CfSharp's
default `TrackAll` remains unchanged for other consumers. Remote metadata may
be displayed or applied where supported, but it is not part of the product's
content acceptance proof. Local metadata notifications still pass through the
official change feed and product routing; they do not establish acceptance of
remote metadata or authorize deletion after an incomplete scan.

## Local metadata results

The current Worker protocol has no timestamp, DOS attribute or ACL mutation.
Unfiltered `MetadataUpdate` operations for files, directories and managed roots
remain in the official journal and are reported as `UnsupportedMetadataChange`
in `mp sync status --json`. They are retained across Host restarts and are not
acknowledged as remote acceptance. Supported child operations continue independently.

CfSharp owns the identity-verified echoes of remote creation and remote metadata
application. MirrorPulse does not treat every directory notification as an echo.
On the Windows watcher, file timestamp or attribute changes may instead arrive
as content observations. A current owned `InSync` snapshot and mutually accepted
content revision can settle that content observation without sending file bytes;
this is not a promise to replicate the timestamp or attribute. A data write that
clears `InSync` still requires a conditional upload and content acceptance proof.

## Existing roots

The Host updates only a registration with its own provider name, stable root
identity and matching path. Startup performs registration changes before starting
the CfSharp owner and Workers. It uses the existing-registration update contract,
preserves files and configuration, and queries the actual Cloud Files policy
after registration. A packaged Host also requires Shell readback of its path and
zero metadata-tracking policy. An unpackaged development Host may proceed only
when Shell registration succeeded and readback reports `0x80070490` (not found);
actual CFAPI policy verification is still mandatory. Other registration or
readback failures prevent startup. Another provider's registration is never
adopted.

Registration changes must be quiescent with respect to content confirmation.
MirrorPulse does not hot-change the root policy while a session runs. Item
identity writers go through the same CfSharp owner; arbitrary raw CFAPI identity
writes are outside the product coordination boundary.

## Confirmation and recovery

CfSharp preview.3's public managed confirmation operation requires the actual
registered policy to be exactly `None`. Content-only proof cannot safely confirm
tracked metadata under `TrackAll`. The operation rechecks actual registration
and rejects other policies before reading, preparation, marking or projection.
This restriction does not repair the Windows conditional-USN issue.

The local watcher can report hydration or confirmation metadata as a file
content observation. Under the actual `None` policy, MirrorPulse acknowledges
such an observation without uploading when the current native snapshot is
`InSync`, its encoded identity belongs to the Adapter instance, and its revision
matches both the official acknowledged baseline and a fresh remote Stat. This
does not mark native state or suppress a time window. A real data write clears
`InSync` and retains its separate journal operation. An incomplete historical
mutation still requires proof recovery before this observation policy runs.

MirrorPulse retains the upload-time native volume/root/file binding, complete
length and SHA-256, and the accepted identity before local confirmation. A
replacement's current binding cannot be substituted for a missing historical
binding. The Worker transport also hashes the bytes actually sent and compares
them with the durable intent before sending the final commit frame. A mismatch
aborts that upload rather than treating an earlier local hash as remote proof.
Native success, official projection and the product ledger remain
separate commits. Replaying the retained proof repairs projection without
blindly uploading again. The product alone advances its ledger, the official
journal acknowledgement and the rescan generation.

See [protected content confirmation](protected-content-confirmation.md) and
[Cloud Files lifecycle](cloud-files-lifecycle.md). Full native rescan and installed
MSIX evidence remain required before this recovery path is accepted.
