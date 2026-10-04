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

## Existing roots

The Host updates only a registration with its own provider name, stable root
identity and matching path. Startup performs registration changes before starting
the CfSharp owner and Workers. It uses the existing-registration update contract,
preserves files and configuration, and queries the actual Cloud Files policy
after registration. Shell registration is also read back when that integration
is available. A mismatched or unverified policy prevents startup; another
provider's registration is never adopted.

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

MirrorPulse retains the upload-time native volume/root/file binding, complete
length and SHA-256, and the accepted identity before local confirmation. A
replacement's current binding cannot be substituted for a missing historical
binding. Native success, official projection and the product ledger remain
separate commits. Replaying the retained proof repairs projection without
blindly uploading again. The product alone advances its ledger, the official
journal acknowledgement and the rescan generation.

See [protected content confirmation](protected-content-confirmation.md) and
[Cloud Files lifecycle](cloud-files-lifecycle.md). Full native rescan and installed
MSIX evidence remain required before this recovery path is accepted.
