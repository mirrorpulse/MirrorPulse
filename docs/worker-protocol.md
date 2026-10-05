# MirrorPulse Worker Protocol

For legacy v1 sessions, Host journal mutations use the durable operation ID as the request ID, including
after Worker restarts. Upload streams also use that ID. This is correlation,
not an idempotency guarantee: protocol v1 does not promise duplicate suppression.
The Host records execution intent before dispatch and retains unknown outcomes
for reconciliation instead of blindly sending the mutation again.

This document retains the version-one control contract between MirrorPulse and
one Adapter Worker process. Core owns the Host envelope codec, and the Adapter
SDK implements the corresponding version-one frame format for out-of-process
Workers. Protocol messages listed below include target contract elements that
are not yet wired into the product Host.

## Implemented v2 boundary

The Host also negotiates v2 using the signed package's version range and the
Worker's Hello capabilities. It rejects a v1 session with multiple configured
roots, including disabled roots. V2 read, list, stat and mutation requests carry
an explicit rootKey; move carries source and destination roots. Durable
operationId remains stable across retries while request and stream IDs are fresh.
Responses and binary chunks must match the selected version, root and all IDs.

The canonical language-neutral specification and golden vectors are owned by
[adapter-template](https://github.com/MirrorPulse/adapter-template/blob/0c6398635a7680691d4bcff418cbbd6b951ab0a4/spec/worker-v2.md).
The independently built memory Worker is tested through production signed
installation and the actual Supervisor on x64 and ARM64. A separate SDK-free
wire process checks interoperability and negative root/capability/cancel cases.
The five currently published official providers retain their v1 storage behavior;
the memory profile does not establish their v2 or crash-recovery support.

New v2 roots use root-scoped placeholder identities. Existing single-root v1
bindings retain their exact CfSharp identity bytes and ItemId; adding a scoped
root does not rewrite them. Multiple preexisting legacy roots require an explicit
migration rather than assigning their ambiguous identities to whichever root
currently occupies a path. Cross-root raw remote IDs therefore do not collide.

Interrupted v2 uploads send Cancel and retain response correlation until the
target terminates and CancelAck confirms lease cleanup. Failed acknowledgment or
partial-frame writes terminate the session. Bounded range reads continue to drain
and validate an abandoned result before the next range. Acceptance may race a
cancel; recovery uses the durable operation binding rather than assuming rollback.
Directory transport support does not mean the local journal's directory execution
and multi-root background remote polling are complete.

## Process and identity boundary

MirrorPulse starts one Worker process for each Adapter Instance. A Worker is
identified by its `adapterId`, `installId`, `instanceId`, and
`workerSessionId`. A session ID changes every time the Worker process starts.
Root registrations carry an Adapter-provided uniqueness key so MirrorPulse can
reject a first-level directory collision without renaming the directory.

## Handshake

The expected sequence is:

1. MirrorPulse creates a current-user Named Pipe and starts the Worker.
2. The Worker sends `Hello` with its Adapter identity, package version,
   runtime identifier, supported protocol range, and manifest SHA-256.
3. MirrorPulse selects one common protocol version or rejects the session with
   a stable error code.
4. MirrorPulse sends `Ready` with the selection and the root registrations that
   the instance may expose.

`Hello` and `Ready` use the same instance and Worker session context. A failed
selection never becomes a healthy Worker session.

## Control frames

Named Pipe control frames use a four-byte unsigned little-endian payload length
followed by the payload bytes. The maximum control payload is 4 MiB. Control
payloads are UTF-8 JSON and contain a `ControlFrameEnvelope` with:

- protocol version;
- message type;
- request ID and response flag;
- instance ID and Worker session ID;
- message-specific JSON payload.

Requests and responses share a correlation request ID. Cancellation targets the
request ID of the operation being cancelled. Each Worker event has its own event
ID and monotonic per-session sequence.

## Implemented FTP Worker subset

The official FTP Worker is a separate EXE. Its process arguments carry only the
instance ID, Worker session ID, and current-user pipe name. After `Hello`, the
Host sends `Ready` with an FTP endpoint, username, security mode, and credential
reference. The Worker sends `CredentialRequest` for that reference; the Host
returns `CredentialResponse` over the pipe. Neither the secret nor the CfSharp
database path is placed on the process command line. The Worker reports
`Connected` or a stable `Error` code and accepts `Stop`/`Stopped`.

The current transfer subset accepts `Stat`, `List`, `ReadRange`, and `Upload`
control messages. The product Host now routes CfSharp hydration through the
active instance's `ReadRange` command, validates the response identity, offset,
length, end marker, and SHA-256 digest, and limits each request to 1 MiB. A
disconnected instance fails the read. `List` carries a relative directory path,
an opaque base64 continuation cursor, and a bounded page size; `DirectoryPage`
returns stable remote IDs, revisions, item kinds, paths, optional lengths and
timestamps, and the next cursor. The CfSharp anti-corruption layer maps these
pages to its native remote directory catalog contract.

The Host also continuously consumes CfSharp's durable local journal for file
create/content-update operations: it requests `Stat`, sends a conditional
`Upload` with bounded hashed chunks, and acknowledges the CfSharp operation
only after `UploadComplete`. Delete/move dispatch still requires the next
protocol slice. `StatResult` supplies the FTP size and modification-time
revision when available. `ReadRangeReady` precedes one bounded binary chunk.
`UploadReady`
precedes binary chunks whose offset, session IDs, stream ID, and SHA-256 digest
are validated before data is written to the transfer cache. `UploadComplete`
returns the resulting revision. `OperationError` distinguishes invalid requests,
remote conflicts, unavailable server capabilities, and retryable transfer
failures.

The Local, WebDAV, SMB, FTP, and SFTP official Workers publish this directory
page contract in their signed packages. Local and SMB enumerate the confined
filesystem root, WebDAV uses a bounded `PROPFIND` depth-one response, and FTP
and SFTP map their native directory listings. Every implementation sorts pages
deterministically and encodes its page offset as an opaque cursor; the Host
never interprets a cursor returned by a different Worker instance.

FTP uploads stage a temporary remote file, compare the destination's revision
before transfer and again before rename, then rename the staged file. Standard
FTP offers no atomic compare-and-swap operation. This is an optimistic check,
not a strong conditional write: another client can change the destination
between the second check and rename, and some servers cannot overwrite on
rename. The Adapter capability matrix must expose that limitation; MirrorPulse
must retain conflicting content until the user resolves it.

## Implemented SFTP Worker subset

The official SFTP Worker is another independent EXE using the same current-user
pipe, credential-reference exchange, bounded binary chunks, and transfer cache.
On its first connection it sends a `HostKeyChallenge` containing the endpoint
and SSH SHA-256 host-key fingerprint. The Host must explicitly return
`HostKeyDecision` with the same fingerprint and an approval. The Host owns the
persistent pin; subsequent `Ready` frames provide that pin, and a changed key
is rejected before authentication with `HostKeyRejected`.

`Stat`, `ReadRange`, and `Upload` use the FTP subset's control message shapes.
SFTP ranges use a remote file handle and seek, then return one bounded chunk.
Uploads stage locally and at a request-specific remote path, compare the
destination's size and modification time before and after transfer, then use
the server's POSIX rename extension when available, falling back to standard
rename. A server that cannot replace the destination on rename reports a
transfer failure. SFTP also has no atomic compare-and-swap in this protocol
subset, so concurrent writes can still race after the second revision check.
The Host retains both sides for explicit conflict resolution.

## Lifecycle messages

- `Heartbeat` and `Health` provide liveness and a health status of healthy,
  degraded, or unhealthy.
- `Cancel` identifies a target request, reason, and optional force behavior.
- `Shutdown` carries a reason, grace period, and optional restart request.
- Retry directives carry an attempt number, delay, retry time, and optional
  reason. Backoff settings declare initial and maximum delays and a multiplier.

## Errors, diagnostics, and logs

`ErrorInfo` carries a stable code, category, retryability, authentication,
conflict, unsupported, native error, and diagnostic ID fields. Host-side codes
use the `mp.` namespace; Worker-originated codes use `worker.`.

Diagnostic events retain structured diagnostics and correlation metadata.
Structured log fields classify common password, token, secret, API key, private
key, and credential-value names as sensitive and replace their values with
`[REDACTED]` before the log entry is created. Credential references identify
current-user secure-store entries and never contain secret material.

## Compatibility rules

Protocol ranges are negotiated before a Worker is considered ready. Unknown
message types and unsupported protocol versions must produce structured errors;
they must not silently downgrade security or execute package code. New optional
fields may be added with a compatible schema revision, while changes to frame
length, identity, or correlation semantics require a protocol revision.
