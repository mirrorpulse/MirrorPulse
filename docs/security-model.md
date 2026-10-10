# Security and ownership model

## Assets and owners

| Asset | Owner | Intended access |
| --- | --- | --- |
| Cloud Files session, native journal and official CfSharp SQLite store | Host through CloudFiles.CfSharp | No client database access or independent sync engine |
| Product catalog, configuration, installation and operation state | Host | Clients use typed Control requests |
| Credential values and references | Host / Windows Credential Manager | Send only the selected instance's required values to its Worker |
| User source files and sync-root contents | User, coordinated by Host | Adapter operations stay within configured roots and product policy |
| Installed Adapter executable and dependencies | Host package service | Validate signature/inventory before installation |
| Transfer/file cache | Host | Temporary operation leases; never confuse it with conflict preservation |
| Signing private keys and CI tokens | Repository owner / protected CI | Never distribute with the product or write to ordinary logs |

## Current controls

- One Host owns the current user's Cloud Files session and durable stores.
- CLI uses the versioned Control client. Existing WinUI database/status-pipe
  paths remain explicitly recorded migration exceptions.
- Worker executables run out of process, one per enabled instance.
- Named Pipes are restricted to the current Windows user.
- The production official-package installer validates the trusted signer,
  signed inventory, actual file hashes, entrypoint and extraction boundary.
- Official Adapter public trust is separate from package-identity/MSIX signing.
- Credentials are held by Host; Workers receive configuration and necessary
  credential values over the instance connection.
- Logs and exported diagnostics are local; the product has no telemetry upload.
  Writers and ZIP export use a closed list of registered descriptions and typed
  fields. Arbitrary messages, exception details, paths, URL values, unknown keys
  and log filenames are omitted. Legacy logs are reconstructed through the same
  policy. Diagnostic IDs and validated operation IDs/HRESULTs remain available
  for correlation.

## Limitations that must remain explicit

Process separation and a Job Object isolate lifetime and failures. They are not
a filesystem sandbox: code running as the current user can potentially read
other current-user files and environment/registry state. A signature does not
grant trustworthy behavior, and current-user ACLs do not distinguish every
process under the same account.

Workers receive a rebuilt environment containing Windows system paths, temporary
directories, and declared file/transfer cache paths. Host tokens, inherited PATH
entries, runtime hooks and undeclared launch variables are excluded. This does
not prevent current-user code from querying other system state.

## Pipe admission and peer identity

Each Worker launch receives a fresh 256-bit pipe-name nonce and a distinct
protocol session ID using the existing v1 arguments. Before sending configuration
or credentials, Host checks the connected client PID and Windows session against
the launched process using the kernel's
[client identity APIs](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getnamedpipeclientprocessid).
The Hello frame must match the instance and protocol session within five seconds.
Old sessions and different processes are rejected.

The control server claims the first pipe instance and retains a listening handle
during handover. A preclaimed name fails with a fixed diagnostic. The CLI checks
the connected server's PID/session and executable path against its selected Host
before sending any arguments. Control clients can supply an expected PID or
executable resolver; product clients must bind that identity. Current-user client
connections use identification rather than granting server impersonation.
The server ACL and client owner check use the actual Windows user SID. An elevated
token's default owner can be the Administrators group; it is not accepted as a
replacement for the user SID. An ownership rejection is a structured authorization
error, including during Host discovery.

Control admits at most 16 concurrent connections plus one admission slot, with
eight ordinary request slots and two reserved status/cancel/stop slots. Ordinary
mutations are serialized. Overload returns a retryable `mp.control.hostBusy`.
Frame read/write deadlines are five seconds and the connection deadline is two
minutes. Trusted Host handlers must honor cancellation; shutdown drains their
ownership before disposing stores. These checks prevent accidental peers and
stale sessions; they do not form a sandbox against fully privileged same-user code.

## Credential configuration transactions

Host creates and rotates credentials under new opaque references, then atomically
publishes the instance configuration in the product catalog. Failed writes and
failed catalog commits leave the previous credential unchanged. Ordinary
configuration patches cannot inject secret fields or managed references. Required
credentials cannot be removed.

The catalog stores an outbox of references awaiting cleanup, never credential
bytes. After commit, unreferenced credentials are retired. A failed retirement
remains pending and is retried when Host opens the catalog; the committed instance
keeps its valid new credential. Recovery preserves every currently referenced
credential. Credential removal detaches the pointer before retirement. Rotation
and removal are available through the typed control contract; CLI input wiring
and applying changes to running Workers are separate implementation steps.

## Owned namespace permission evidence

The ephemeral namespace token sets its default owner to the current user's SID
before admitting work. This prevents newly created objects from defaulting to an
administrator group under an elevated caller. It does not change existing object
owners, the normal source token, privileges or group membership. Failure to set
and verify this owner prevents session construction. Windows defines this setting
through [TOKEN_OWNER](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-token_owner).

Schema 27 adds immutable birth intents for controlled creation, import and remote
population under a protected parent. Admission requires that parent's latest
verified protection, native binding, stable root and current role. A separate
pending reservation uses the parent evidence and child name, so renaming the
parent cannot bypass a concurrent name reservation. Exact historical replay
preserves the original intent after role rotation; new stale-role admission fails.

These records contain no child native binding or original child DACL. Upgrading
an older catalog creates no historical birth evidence and leaves existing
permission baselines and sealed captures unchanged. Birth observation,
reservation completion and native execution remain separate work; the intent
alone does not authorize replay, prove protection or enable controlled CLI
operations.

Schema 28 separately retains the planned logical placeholder identity and a
start intent before native creation. Local creation and import cannot claim an
accepted remote revision. Planned identities and original protected-file
identities cannot adopt each other. Missing historical plans remain missing;
upgrades do not infer them from today's path.

A newly committed start is distinguished from historical replay. Neither fact
proves that an object exists or permits another native creation attempt. The Host
must reconcile the original plan against actual birth evidence after interruption.
The parent and pending name are checked again before the first start; exact
historical replay remains immutable after role rotation.

Schema 29 retains the first actual native birth observation against that original
plan and start. Its owner, root, path, logical identity and native binding must
agree with the admitted parent; multiply linked or ordinary non-placeholder
objects are rejected. Local births remain unaccepted. A birth cannot adopt an
original object's native binding, and a born object cannot be recaptured as a
pre-protection baseline.

The recorded birth DACL describes creation under protection. It supplies no
original restoration target. Observation leaves the pending name reserved and
does not mark the object protected, add it to a sealed original tree, or enqueue
an upload. Actual protection, current membership and native journal delivery are
independent steps. Missing historical observations are never inferred during an
upgrade.

The Windows birth observer retains the target and its ancestor names, reads the
actual handle link count and compares the native placeholder envelope with the
original public CfSharp identity encoding. It does not depend on a successful
durable item projection to invent an identity. A repeated observation preserves
the first descriptor and timestamp while rejecting a different native object.
Local births marked in sync without their required acceptance remain rejected.

Schema 30 separately retains the actual ordinary-object binding, owner, kind,
single-link observation and birth descriptor before local conversion. Its
expected converted descriptor is an intent, not proof that permission work
completed. A subsequent placeholder observation must retain that object and
cannot precede its preparation. Other births and pre-protection baselines cannot
adopt the prepared binding. Bounded recovery pages expose the original
preparation without creating a new journal sequence or accepting remote state.
Missing preparations in older catalogs remain missing; they cannot be backfilled
after an existing placeholder observation. Native conversion, current permission
verification and startup recovery still require their own admission and evidence.

Identity mapping preserves an absent revision as `null` in CfSharp's public
envelope. It does not turn absence into an explicit empty revision. Stable item
IDs and root scope remain independent of revisions; existing explicit empty
envelopes remain readable without rewriting them.

Startup recovery can enumerate every birth admission in bounded finite pages.
Each entry retains its original plan, start and observation separately, including
missing facts and missing name reservations. A missing reservation is visible
recovery work. Paging neither repairs history nor authorizes a new creation or
marks an admission complete. Scans are short-lived and cannot be reused across
a catalog migration.

Inherited birth descriptors are predicted by Windows'
[CreatePrivateObjectSecurityEx](https://learn.microsoft.com/en-us/windows/win32/api/securitybaseapi/nf-securitybaseapi-createprivateobjectsecurityex),
using the exact canonical parent access descriptor and the current creator token.
MP does not implement its own ACE inheritance rules. Prediction writes no ACL and
does not prove the child's binding, actual permissions or current membership.
Restoration and role rotation require independent native inspection of descendants;
the first birth descriptor remains historical evidence, never an original DACL.

Before recording a native birth start, MP commits the original logical identity
to CfSharp's public item repository. This prevents a creation notification from
discovering a different item ID before the native batch projects its result.
The planned row has no native file ID or acceptance claim. Existing conflicting
rows are retained and rejected; a started birth with a missing row requires
native recovery. Replay preserves forward progress. The Host must keep the birth
offline under its namespace gate until actual creation, protection and content
completion have been verified. All watcher journals remain encoded by CfSharp.

Product catalog schema 22 retains each object's original volume, registered-root
and file binding, capture location, owner SID and canonical DACL. The baseline
and the first change intent commit together before any permission write. A
different object at the same path cannot replace that evidence. Role rotation
and restoration retain the first baseline and follow the previous verified
DACL; restoration requests the exact original access descriptor.

Application and independent read-back verification are separate durable facts.
Verification must match the original object and owner and the intended DACL.
Only one unresolved permission change may exist per object. A recovery record
retains the original and application facts and blocks subsequent changes.
Descriptors and SIDs remain private catalog data, outside client status and
ordinary diagnostic output. These records do not authorize namespace operations
or prove that an entire subtree is protected.

Windows can add the auto-inheritance completion marker during an access write,
including restoration of a descriptor captured at protected object creation.
Read-back permits that added marker only; ACEs, protection and every other flag
must still match. The catalog retains the original request and the actual
observed descriptor separately. Subsequent changes and audits use the recorded
observation, preserving the original evidence and preventing a later descriptor
change from being mistaken for an already verified write.

Original evidence and intents can be prepared in bounded batches of up to 4096
objects in one SQLite transaction. A later conflict rolls back every new record
from that batch and retains previously committed history. Replay preserves each
member's existing application and verification facts. Batch preparation does
not write permissions, seal a complete tree manifest, or authorize operations.

The permission coordinator reconciles a prepared intent through a caller-owned
stable object lease. A pending intent may write only when the observed DACL is
the expected value. If the original object already has the exact target DACL,
recovery records application and performs a separate fresh read without another
write. Changed object, owner, scope or unknown DACL requires recovery. Failed
read-back retains the application fact; cancellation after a successful write
does not discard verification. Shutdown rejects new admission and drains
accepted reconciliation before releasing its gate.

The Windows permission lease retains the final object and its ancestor chain
without delete sharing, and rejects foreign reparse points and unrouted scope.
Its handles request read-data or directory-list access so they participate in
native sharing checks. They open reparse objects without recall and never read
content through those handles; cold-file inspection requires separate native
verification.
The lease also reads native standard information before capture, fresh inspection
and a permission write. A delete-pending object or a file with multiple hard links
is rejected because its access descriptor also applies to aliases outside the
managed namespace. This is a fresh observation, not an atomic freeze of link
creation: Windows tests permit a new hard link even without file sharing, and an
exclusive referenced CFAPI oplock did not prevent it on the tested system.
Whole-tree initialization must resolve this concurrency boundary before admission.
CfSharp's public inspection supplies object bindings and placeholder
classification. .NET `NativeObjectSecurity` reads and writes the access section
through the retained handle; MP does not implement another Cloud Files binding
or projection mechanism. Target descriptors use `FileSystemSecurity` canonical
ACE ordering and the expected Windows auto-inheritance completion flag before
the write. A directory write can propagate inheritance, so every original
descriptor must be retained before applying any tree permissions.
The policy exposes a separate creation descriptor because Windows sets different
auto-inheritance flags at creation. Controlled creation supplies it to the native
creation call. Directory descriptors also inherit content access and the role's
namespace rights, so CFAPI placeholder creation can inherit protection without
a subsequent permission write. Existing descendants still require original
evidence before parent inheritance changes.

The catalog also retains an immutable permission-tree capture for an existing
managed root. Capture pages have contiguous sequences, original object bindings,
access descriptors and planned changes, with the immediate directory captured
before each child. Each page commits atomically and can be replayed without
replacing originals. The seal covers the definition and every member with a
SHA256 fingerprint; it requires the declared complete count and validates all
bounded pages. Schema migration preserves earlier permission history.

An open capture blocks preparation and application of permission changes in that
root and its sync-root parent, including alternate operation IDs and intents
prepared before the capture or a process restart. Application reads recheck the
durable fence without replacing existing permission history. Other roots can
still prepare their own changes. Capturing and sealing do not write ACLs or create
executable changes. After sealing, only the captured intent can use its operation
ID. Sealed describes retained evidence, not independent completeness of the
live tree or permission to admit namespace operations. Retained
whole-tree handles, independent membership audits and protected-birth restoration
evidence still require integration. An unsealed capture can be cancelled through
the Host-owned catalog. Schema 24 adds a separate immutable cancellation receipt
without rewriting the original capture or permission history. Its count and
SHA-256 cover every retained member in bounded pages, including an empty prefix.
Admission verifies that retained evidence before releasing only the cancelled
capture's admission fence; another open capture continues to block the root
and its sync-root parent. Cancelled operation IDs remain non-executable. A new
capture uses fresh operation IDs and retains the old evidence for recovery.
Cancellation does not restore ACLs, cancel already executable changes, prove
that existing work drained or admit namespace operations. Sealed captures cannot
be cancelled by this API.

The permission coordinator supplies a separate runtime capture boundary. One
Host-owned coordinator rejects new applications for the target stable root and
its sync-root parent, then drains previously admitted reconciliation before
invoking the anchor-definition and capture callbacks. It does not hold the
application gate across capture, so other roots can continue. Callbacks may
append, seal or cancel catalog evidence; a failed open capture stays fenced
across restart. Cancellation and disposal wait for callbacks to actually exit.
Capture creation must use this owner rather than bypassing its runtime fence.
This protects MP permission admission; it does not freeze external writers or
establish physical tree completeness. Host integration remains pending.

The execution-session component and catalog evidence do not yet enable strict
protection in the Host. Applying and auditing object permissions, rotating the
Host role, controlled file operations, and their client commands require
separate integration and acceptance.

## Remaining work

Package inventories, entrypoints and locale resources use one canonical Windows
path policy: no ADS, device names, trailing dots/spaces, short-name aliases,
decomposed Unicode, case aliases or file/parent collisions. Before publishing an
installation, Host checks the minimum product version, supported v1/v2 protocol intersection,
supported RIDs and both executable PE machine types. Canonical Unicode names and
ordinary spaces within a filename remain supported. Embedded and detached signed
inventory formats remain compatible.

Third-party publisher trust, unsigned developer-mode installation and
installed-package revalidation are incomplete.
Credential CLI input wiring and end-to-end safe error
presentation still require further work. Do not describe those pending controls
as implemented.

The current developer-mode setting does not enable unsigned installation.
The current official trust anchor remains supported; rotation and revocation
must preserve explicit version/instance state rather than silently replacing
keys or deleting user files.

CLI credential arguments are accepted. Shell history and process inspection can
retain command-line values; alternative input paths must be wired and verified.
Secrets must not be placed in ordinary Adapter configuration fields.

## Boundary threats and required acceptance

| Boundary | Threat | Required evidence |
| --- | --- | --- |
| CLI / UI to Host | Incorrect routing, stalled client, disclosure in responses | Versioned typed requests, bounded frames/deadlines, parallel-control progress, safe errors |
| Host to Worker | Wrong peer/session/root or inherited secrets | Peer/session correlation, environment allowlist, root-scoped IDs, negative cross-instance tests |
| Package to installed files | Untrusted signer, altered payload, aliases/traversal, interrupted install | Production verification, Windows canonical paths, transaction recovery and tamper rejection |
| Worker to source | Credentials to wrong origin, unchecked overwrite, unsafe path or host key | Authorized-path/origin checks, TLS/key policy, revision preconditions and readback |
| Journal/apply to persistent state | Lost ack, partial remote apply, premature checkpoint | Stable IDs, pending replay and failure injection at actual durable boundaries |
| Conflict preservation | Incomplete copy mistaken for safety, destructive retry | Verified hashes/tree manifest before mutation, idempotent recovery and space-failure tests |
| Diagnostics / publishing | Secret values, keys or user data in artifacts | Structured safe fields, redacted synthetic tests and artifact inventory |

Some protocols lack atomic conditional writes. Optimistic revision checks must
not be advertised as compare-and-swap or guaranteed conflict-free replay.

## Scope and non-goals

The supported product boundary is the current user. It does not defend against
an administrator, a compromised Windows account, or a user deliberately running
malicious trusted code. Stronger OS restrictions require a separate design
decision and compatibility tests, especially for SMB integrated identity.

The anti-corruption layer must reuse mature CfSharp public APIs and the official
SQLite provider. It must not create a second native journal or bypass ownership
to implement a security or recovery workaround.

See [SECURITY.md](../SECURITY.md) for private reporting and
[client architecture](client-architecture.md) for the reference gate.
