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

Worker environment inheritance and peer verification still need hardening.

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

## Remaining work

Third-party publisher trust, unsigned developer-mode installation, Windows
path canonicalization and installed-package revalidation are incomplete.
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
