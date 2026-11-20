# Existing Shell registration ownership

Before changing an existing Explorer registration, MirrorPulse requires its
exact current-user registration ID and absolute sync-root path. A nonempty
provider GUID must equal the product GUID. A nonempty foreign GUID is refused
before any additional lookup or write.

In [run 40262688799](https://github.com/mirrorpulse/MirrorPulse/actions/runs/40262688799),
both the product registration and a separate WinRT-only diagnostic registration
read back an empty provider GUID after explicitly requesting the product GUID.
The independent registration's display label matched. The product probe
correctly failed before changing its label. These observations do not establish
a CfSharp defect or the underlying cause of the WinRT readback.

An empty GUID remains insufficient ownership evidence. In that case the Host
also requires a fresh public `CloudSyncRoot.GetInfo()` lookup to match the
configured absolute root path, a nonzero native root file ID, the product
provider name and the exact configured Cloud Files root identity bytes. The
Shell profile retains an immutable copy of those expected bytes when it is
created from the Host definition. A manually constructed profile without the
expected identity cannot use this path. Failed or foreign readbacks refuse the
change; a cached receipt, label, path alone or registration request is not proof.

This read-only check uses CfSharp's public registration API. It starts no
provider session, edits no identity or database and performs no Adapter work.
New registration still follows the existing paired Shell/Cloud Files
coordinator. This check neither repairs nor adopts an unrelated registration.

The packaged App probe uses the same production check when WinRT does not
report the GUID. It still requires the existing expected Host root and actual
custom, unified and restored label readbacks. The original verification error
and any restoration error remain distinct. Its independent minimal registration
is diagnostic; its success cannot substitute for product acceptance.

Microsoft describes the [ProviderId property](https://learn.microsoft.com/en-us/uwp/api/windows.storage.provider.storageprovidersyncrootinfo.providerid?view=winrt-26100)
as the storage provider GUID. Its documentation does not explain this observed
empty readback. Managed boundary tests simulate the public registration facts;
actual packaged registration and restoration require the clean installed CI.

The Shell `Context` buffer contains the sync-root identity, as specified by
[Microsoft's Context documentation](https://learn.microsoft.com/en-us/uwp/api/windows.storage.provider.storageprovidersyncrootinfo.context?view=winrt-26100).
Every registration writes the same configured identity bytes retained in the
profile, including custom-label updates and restoration. Encoding the directory
path in that field would supply a different identity. Missing or damaged
configured bytes refuse before the Windows registration call; each buffer is a
fresh copy and a display-name change cannot replace its contents. This product
correction does not establish a CfSharp defect; the clean installed probe must
still confirm actual custom, unified and restored registration behavior.
