# Protected content confirmation

## Status

MirrorPulse is pinned to CfSharp `0.1.0-preview.3`. Its existing full-rescan path
does not acknowledge local content when a usable confirmation precondition is
unavailable. The alternative below has passed a disposable native experiment;
production recovery is being integrated with the newly published managed API.
The API requires the actual registered policy to be exactly `None`; MirrorPulse
adopts the [content-only policy](content-sync-policy.md). The library's default
`TrackAll` remains unchanged. Package publication does not replace full product
native and installed acceptance.

The [successful experiment](https://github.com/mirrorpulse/MirrorPulse/actions/runs/37132837677)
ran all 15 cases on each architecture at commit
`28a0892c771eae96b2233533692ab4448827aa74`: Windows Server x64
`10.0.26100.0` and Windows 11 desktop ARM64 `10.0.26200.0`.
Server provides an API comparison, not evidence of product support. Native x64
Windows 11 desktop remains a release acceptance requirement.

## Why a separate confirmation boundary is needed

In the preview.2 fixture, coordination calls can return an operation USN of
zero. An [independent USN experiment](https://github.com/mirrorpulse/MirrorPulse/actions/runs/37129586933)
also rejected current and refreshed positive file USNs with `0x80070179`
(`ERROR_CLOUD_FILE_NOT_IN_SYNC`) on both architectures, including a direct
same-handle native comparison. These observations do not establish a Windows
internal cause. Exposing a USN reader alone does not establish a usable
conditional confirmation contract for this path.

Remote acceptance proves what reached the source. Local confirmation must also
prove that the current local object, placeholder identity, length and complete
content still match that accepted result. A pathname, timestamp or remote
revision alone cannot establish that proof.

## Validated native sequence

1. Open an owned protected handle with `Exclusive | WriteAccess`, without
   `Foreground`.
2. Acquire a protected reference, then borrow its Win32 handle without owning it.
3. Verify native file ID, opaque placeholder identity and length; read and hash
   the complete local bytes through that same handle.
4. Recheck identity while the reference remains held, then mark the same handle
   in sync.
5. Drain outstanding I/O before releasing the reference; close the opaque
   handle with `CfCloseHandle`.

The prototype uses a null USN for the final native call. That native predicate
is unconditional. Safety comes from the complete exclusive verification and
commit sequence; an unprotected null-USN call is not a substitute. Windows
documents the protection and borrowing rules in
[CfOpenFileWithOplock](https://learn.microsoft.com/en-us/windows/win32/api/cfapi/nf-cfapi-cfopenfilewithoplock),
[CfReferenceProtectedHandle](https://learn.microsoft.com/en-us/windows/win32/api/cfapi/nf-cfapi-cfreferenceprotectedhandle),
and [CfGetWin32HandleFromProtectedHandle](https://learn.microsoft.com/en-us/windows/win32/api/cfapi/nf-cfapi-cfgetwin32handlefromprotectedhandle).
The USN predicate is described by
[CfSetInSyncState](https://learn.microsoft.com/en-us/windows/win32/api/cfapi/nf-cfapi-cfsetinsyncstate).

## Native reader ownership

The initial experiment failed when `RandomAccess.Read` attempted to bind the
borrowed asynchronous handle to the .NET thread pool. The handle already has
Cloud Files completion-port ownership. The corrected prototype uses explicit
`OVERLAPPED` reads, a separate event with its low bit set, and completion draining.
That event convention suppresses completion-port notifications for those reads,
as documented by
[GetQueuedCompletionStatus](https://learn.microsoft.com/en-us/windows/win32/api/ioapiset/nf-ioapiset-getqueuedcompletionstatus).

Buffers, events and `OVERLAPPED` storage must remain alive until I/O reaches a
terminal result. Production cancellation must cancel the individual pending
request and drain it before releasing its protected reference. See
[ReadFile](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-readfile)
and [CancelIoEx](https://learn.microsoft.com/en-us/windows/win32/api/ioapiset/nf-ioapiset-cancelioex).
The test prototype's synchronous completion wait does not establish production
asynchronous cancellation or bounded reference latency.

## Ownership of the managed operation

| Owner | Responsibility |
| --- | --- |
| CfSharp | Protected handle and reader lifetime, same-handle verification/commit, existing operation lease, official state projection and explicit native-applied recovery result |
| MirrorPulse anti-corruption layer | Translate remote acceptance into an immutable confirmation request and map typed outcomes to product policy |
| Host and Worker | Remote upload/readback, accepted revision, mutation ledger, retry scheduling and root policy |
| Clients | Query and request operations through `MirrorPulse.Control.Client` |

Reuse public `CloudItemSnapshot.LocalBinding`, placeholder identity and
availability fields. Preview.3 inspects ordinary files as well as placeholders;
the binding includes the native volume and full root/file IDs. Capture it before
upload and compare that binding after conversion. Missing historical bindings cannot be
replaced with the current pathname's ID and treated as proof of the old object.

`CloudContentConfirmationRequest` contains the expected local binding,
accepted placeholder identity, complete length and SHA-256, explicit guarded
preparation and bounded reading policy. Pass it with a cancellation token to
`CloudFile.ConfirmUploadedContentAsync`. The operation
must reject changed objects, mismatched content, incomplete local data and lost
protection. It must not hydrate missing bytes or execute remote requests.

Native commit and SQLite projection are separate boundaries. A projection
failure after native success must expose that fact and retain a repair path.
The product ledger stays `RemoteAccepted` until safe local confirmation and
projection complete; recovery must not blindly repeat the remote upload.

## Evidence and remaining work

| Experiment group | Cases | Observed result on both architectures |
| --- | --- | --- |
| Empty, small and 128 KiB files | 3 | Same object and identity confirmed; bytes preserved |
| Competing process write, rename and replacement | 3 | Competitor waits during the reference, then completes; later write becomes dirty and replacement does not inherit confirmation |
| Hash, length, identity and file ID mismatch | 4 | No confirmation; content and pending state preserved |
| Cancellation and injected failure before commit | 2 | No confirmation; handle can be reopened |
| Existing writer and surviving writable mapping | 2 | Exclusive open rejected with `0x80070020`; retry works after release |
| Oplock break between read segments | 1 | Re-reference rejected; stale verification cannot continue |

The rename fixture explicitly approves the owned placeholder through the public
Provider callback. Its earlier default rejection was a fixture policy issue.

Production gates still include ordinary-file identity across conversion,
replacement before acquisition, root/volume binding, partial-data refusal,
pending-I/O cancellation, large-file segmented fairness, internal operation
lease/shutdown races, native-applied SQLite failure recovery, and complete
full-rescan ACL/disabled-root/runtime-restart integration. Small fixture timings
are not throughput or latency guarantees. The experiment does not satisfy those
gates or complete full-rescan acceptance.

Run the manual `Protected content confirmation probe` workflow for the 15 native
cases. It exports only counts, outcomes and numeric observations in
`evidence-protected-win-x64` and `evidence-protected-win-arm64`; it does not export
user content, fixture paths or credentials. Test-only native code remains
outside the product boundary. Required native acceptance also covers migration
of an owned root from `TrackAll` to the actual content-only policy.
