# MirrorPulse CLI

`mp` is the no-UI control surface for MirrorPulse. It sends versioned requests to the current-user Host over a per-user named pipe. If the Host is not running, the CLI starts the installed Host automatically. `--no-start` disables that behavior for callers that require an already-running service.

`mp host stop` and `mp host restart` return after the previous Host process has exited. A later ordinary command can then start the Host with the updated instance topology.

During initialization, `mp --json --no-start host status` reports `Starting`
and an optional `startupPhase` (`CloudFiles`, `Adapters`, or `RemotePolling`).
Lifecycle status and stop remain available while protected roots recover.
SDK-dependent requests return a retryable `mp.control.hostBusy` until startup
finishes; each managed root still requires its own fresh namespace admission.

Each instance in `mp --json status` reports `workerConnected` from its live
negotiated Worker connection. Its `phase` and last error still describe journal,
transfer or recovery activity; a phase such as `Journal failed` does not itself
mean that the Worker disconnected. Older Host payloads may omit the optional
connection field, which means that this observation is unavailable.

Automatic startup and `mp host start` wait for `Running` or `Degraded` within
the existing 30-second request budget after the control channel opens. The
control-channel startup deadline remains 10 seconds. A readiness timeout leaves
the Host and its retained recovery state running; inspect lifecycle status or
request a normal stop rather than launching a second Host.

## Installation and builds

The MSIX package exposes an `mp.exe` app execution alias and carries the architecture-matched Host under `host\MirrorPulse.Host.exe`. Development builds can run the published executable directly:

```powershell
dotnet run --project src/MirrorPulse.Cli -- --version
pwsh -File eng/verify-cli-host-publish.ps1 -Runtime win-x64
pwsh -File eng/verify-cli-host-publish.ps1 -Runtime win-arm64
```

Use the ARM64 package on ARM64 Windows. The CLI and Host must come from the same build so their Control protocol and runtime dependencies match.

Product operations require Windows 11 desktop 24H2 (build 26100) or later,
x64/ARM64, while the OS release is Microsoft-supported. On older builds or
Windows Server, commands return exit 8 (`mp.platform.unsupported` in JSON)
before resolving or starting a Host. Help and version remain available for
inspecting a build. The Host also rejects unsupported systems before opening
state or registering Cloud Files. See the [support matrix](windows-support.md).

## Global options

```text
-h, --help       Show help.
    --version    Print the CLI version.
    --json       Emit the CLI schema-1 JSON envelope.
    --no-start    Require an already-running Host.
    --developer-mode
                 Allow the configured development Host executable.
    --timeout SEC
                 Parse a requested timeout; propagation is not implemented yet.
```

The default output is concise human-readable text. Automation should use `--json`. Successful data commands emit `{"schemaVersion":"1","kind":"result","data":...}` to standard output; failed commands emit `{"schemaVersion":"1","kind":"error","exitCode":N,"code":"...","message":"..."}` to standard error. Help and version use the same schema with `kind` values `help` and `version`. Error details never include credentials or access tokens.

## Command reference

| Command | Purpose | Common arguments |
| --- | --- | --- |
| `mp status` | Read sync status, pending uploads, conflicts, instances, and transfer state. | `--json` |
| `mp host status\|start\|stop\|restart` | Inspect or control the current-user Host. | `mp --json host status` |
| `mp sync status\|refresh` | Read status or request a synchronization pass. | `mp --timeout 120 sync refresh` |
| `mp adapter list` | List installed Adapter packages, versions, and instances. | `--json` |
| `mp adapter install <package>` | Install an official signed `.mpadapter` package. | `--developer-mode` selects a development Host; unsigned packages are not currently accepted |
| `mp adapter update <package>` | Install a newer package while retaining eligible versions. | package path |
| `mp adapter remove --adapter-id ID [--install-id ID] [--purge]` | Remove an installation after Host reference checks. `uninstall` is an alias. | `--purge` removes retained package data when safe |
| `mp instance list` | List Adapter instances and mapped roots. | `--json` |
| `mp root list` | Inspect managed root IDs, labels, availability, required rescans, and pending namespace recovery. | `mp --json root list` |
| `mp root recover OPERATION_ID` | Retry the original managed root rename proof through the Host. | operation ID from `root list`; `--json` includes separate native and durable projection facts |
| `mp root prepare-rename ROOT_ID LABEL` | Prepare a Label-only managed root rename. | preserves the source directory and returns an operation ID for recovery |
| `mp namespace member create PARENT_ID NAME [--directory] [--operation-id GUID]` | Create an empty local file or directory under a registered protected directory. | retain `operationId` for retry; this does not prove remote acceptance |
| `mp namespace member prepare-import PARENT_ID NAME SOURCE_PATH [--operation-id GUID]` | Capture a private input and retain an immutable file import plan without creating the destination. | `SOURCE_PATH` is resolved by the CLI; keep `operationId` |
| `mp namespace member import OPERATION_ID` | Execute or resume the original import without reopening the source file. | completed replay preserves newer local bytes; no remote acceptance claim |
| `mp namespace member import-status OPERATION_ID` | Read retained binding, content, conversion, protection and membership phases. | `--json`; incomplete started operations report the pending exit code |
| `mp namespace member prepare-replace MEMBER_ID SOURCE_PATH [--operation-id GUID]` | Materialize an enabled target, retain private input and atomically prepare replacement authority. | offline targets and inputs inside MirrorPulse must already be resident; keep `operationId` |
| `mp namespace member replace OPERATION_ID` | Execute the retained local replacement and protect the same original object. | creates the first backup before writing; completion requests a rescan rather than claiming remote acceptance |
| `mp namespace member replace-recover OPERATION_ID` | Resume the same replacement execution. | completed replay preserves subsequent local edits |
| `mp namespace member replacement-status OPERATION_ID` | Read replacement preparation, backup, write, content, restoration and completion phases. | incomplete started operations return exit `11` |
| `mp namespace protection captures ROOT_ID [--after MANIFEST_ID]` | Read up to 256 historical original captures for a stable root. | continue after the last returned manifest ID; includes open and cancelled captures |
| `mp namespace protection recover-tree MANIFEST_ID` | Restore and audit one sealed original tree under shared Host scheduling. | changed membership requires recovery; verified original inventory does not establish whole-product readiness |
| `mp namespace protection initialize [--operation-id GUID]` | Initialize the configured existing original root inventories through the Host. | retain the operation ID for retry; missing roots or evolving moved/born inventories require recovery |
| `mp namespace protection recover-current [--operation-id GUID]` | Recover registered original and born members at their current positions, audit the complete local graph and complete its initializer. | use the same operation ID for pending retries; original roots require sealed captures, born roots require their registered birth and anchor; partially executed operations and new root bootstrap still have separate recovery |
| `mp namespace protection recover-root ROOT_ID [--operation-id GUID]` | Recover and audit one registered root and its shared anchor, then admit that root for controlled work. | returns `scopeRootId`; other roots retain their fences and admission state; reuse the operation ID only with the same root; completion is historical evidence, not whole-tree readiness |
| `mp namespace protection initialization-status OPERATION_ID` | Read durable initialization admission and historical completion. | pending attempts return exit `11`; completion does not authorize a later Host session |
| `mp namespace protection initializations [--after OPERATION_ID]` | Find up to 256 retained pending and completed initialization attempts. | continue after the last returned operation ID; a successful query returns `0`, including when some attempts are pending |
| `mp namespace member prepare-move MEMBER_ID PARENT_ID NAME [--operation-id GUID]` | Prepare a protected member move. | the source name remains fenced until the move receipt is complete |
| `mp namespace member tree MEMBER_ID` | Read the validated current registered subtree. | read-only; depth and current product paths are returned |
| `mp namespace member move OPERATION_ID` | Execute or retry an admitted protected file or directory move. | directories require the original public proof and a sealed registered subtree; all descendant positions settle together |
| `mp namespace member move-status OPERATION_ID` | Read durable move progress. | reports preparation, native observation, projection, and name settlement |
| `mp namespace member prepare-retire MEMBER_ID [--recursive] [--operation-id GUID]` | Prepare a protected deletion. | without `--recursive`, registered descendants are rejected; managed roots are always protected |
| `mp namespace member retire OPERATION_ID [--recursive]` | Execute or retry an admitted protected deletion. | use `--recursive` for a sealed subtree batch; native deletion and membership settlement have separate receipts |
| `mp namespace member retirement-status OPERATION_ID [--recursive]` | Read a protected deletion receipt. | reports the leaf phase or overall batch phase and sealed member count |
| `mp instance create --install-id ID --name NAME [--config k=v] [--root k=v] [--secret VALUE] [--disabled]` | Create an independently identifiable instance. | repeat `--config`/`--root` for multiple values |
| `mp instance configure --instance-id ID --name NAME [--config k=v] [--root k=v]` | Update instance configuration and roots. | adapter-defined keys |
| `mp instance enable\|disable --instance-id ID` | Change whether an instance participates in synchronization. | `--json` |
| `mp instance select-version --instance-id ID --install-id ID` | Select the installed Adapter version for an instance. | `--json` |
| `mp conflict list\|show\|snooze\|resolve` | Inspect, defer, or resolve conflicts. | `--conflict-id ID`, `--action KeepLocal\|KeepRemote\|KeepBoth\|Retry\|DeleteLocal\|DeleteRemote`, `--preserved-path PATH` |
| `mp operation get\|watch\|cancel --operation-id ID` | Inspect or cancel a long-running operation. | `--timeout SEC` |
| `mp config` | Read settings, or update them with `--locale`, `--developer-mode`, `--start-with-windows`, `--enable-installation ID`, and `--sync-root-display-name NAME`. | `--json` |
| `mp developer-mode [--developer-mode true\|false]` | Read or change the global unsigned-Adapter/development Host switch. | `--developer-mode false` |
| `mp startup [--start-with-windows true\|false]` | Read or change optional startup. | `--start-with-windows true` |
| `mp diagnostics [--include-logs] [--output PATH]` | Create a local diagnostic archive. | logs stay local unless the user shares them |

Initialization failures after durable admission return the operation ID with
pending status and a recovery code. Retrying with `--operation-id` resumes that
admission. Transport failures also include the generated ID in their error
message. Supply an explicit ID when the calling process may be interrupted.
If an interrupted caller loses its ID, use `namespace protection initializations`
to find the retained admission and retry with that ID. Pages are ordered by the
canonical operation ID, rather than time, and include historical completions.
Discovery reads only the Host catalog and does not change permissions, release
pending fences or establish readiness for the current Host session.

Global options must precede the command. Scalar command options accept `--name value` or `--name=value`; repeatable `--config`, `--root`, and `--enable-installation` options use separate values. Creation and configuration patches use the same Adapter field validation. A patch retains unspecified settings and the Host-managed credential reference. Secret fields and credential references are rejected in ordinary `--config` values. Secrets are accepted through `--secret` during creation. Avoid shell history and log capture. Other credential-input helpers and rotation/removal CLI options are not fully wired into these commands.

See the [capability baseline](cli-capabilities.md) for limitations. Operation watch currently reads one snapshot, and cancel does not stop the underlying work. Startup and developer-mode commands persist settings but do not yet register Windows startup or allow unsigned packages. A conflict response does not prove the queued action completed.

`mp namespace transfer move OPERATION_ID SOURCE_MEMBER_ID TARGET_PARENT_ID NAME`
starts one Host-owned file transfer between distinct managed roots. The current
delivery baseline uses distinct single-root Adapter instances; transfer between
multiple roots of the same instance is deferred for later review. Different
packages/backends remain subject to their declared and negotiated capabilities.
Use a unique operation ID and retain it for `mp namespace transfer resume OPERATION_ID`.
`namespace transfer status OPERATION_ID` and `namespace transfer list ROOT_ID`
read retained progress. Completion requires accepted copy and whole-target
verification, protected target publication, original conditional source deletion,
native retirement and the actual original acknowledgement receipt. An ambiguous
remote result stays pending. The current path requires a V2 source with its
original non-null revision; directory, local-only, legacy-protocol and unfinished
new-role recovery remain separate requirements. See [namespace transfers](namespace-transfers.md).

Root recovery never moves an Adapter source directory or captures a replacement
object as an old rename proof. Runtime startup attempts the same historical
recovery before starting uploads. Pending or incomplete reconciliation returns
exit 11 and keeps the root fenced; a completed receipt returns 0, and a cancelled
historical intent returns 10. Legacy records without original public evidence
remain fenced and return an unsupported error. Historical receipts do not revert
a later Label. The CLI can prepare a root Label change; native callback
authorization and execution of that preparation remain pending capabilities.

## Automation examples

Import status uses one short Host catalog snapshot, so a large native copy does not
hold the status request behind the namespace actor. These are historical facts,
not current filesystem authority or remote acceptance.

Controlled creation requires a Host that already owns the parent's registered
protected namespace. It supports empty files, directories and children of born
directories. The Host retains the birth intent, identity, start and first native
binding before conversion and independently verifies protection before membership.
Retry with the original operation ID. A start without its original native binding
requires explicit recovery and is never repeated by path. Controlled deletion
supports leaves and sealed subtrees. A subtree retains its root fence until every
member is settled, deletes children before parents, and resumes with the same
operation ID and `--recursive`. It does not bypass a member's protection checks.
Import and replacement entry points share the Host's operation state machines.
Full installed strict-tree startup remains pending.

Controlled directory movement uses CfSharp's public preparation, move and
reconciliation APIs. Every actual descendant must have retained membership,
identity, binding and protection; an unregistered local object blocks preparation.
Recovery uses the original public proof and operation ID. A successful native move
or official projection alone keeps the product fence held until fresh inspection
and atomic settlement of every member. Movement never acknowledges a source upload.

```powershell
# Read a stable machine-readable status.
$status = mp --json status | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $status.kind -ne 'result') { throw 'MirrorPulse status failed.' }

# Request a refresh and inspect the resulting status.
mp --json sync refresh | ConvertFrom-Json

# Install and configure an Adapter instance.
mp --json adapter install .\MirrorPulse.Adapter.Local.mpadapter | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Adapter installation failed.' }
$topology = mp --json adapter list | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw 'Adapter list failed.' }
$installation = @($topology.data.installations) | Where-Object adapterId -eq 'com.mirrorpulse.adapter.local' |
  Sort-Object installedAt -Descending | Select-Object -First 1
if ($null -eq $installation) { throw 'The Local Adapter installation was not found.' }
mp instance create --install-id $installation.installId --name Documents `
  --config 'sourceDirectory=C:\Users\me\Documents'
```

The repository contains no-UI integration and regression fixtures in `eng/verify-cli-integration.ps1` and `eng/verify-cli-regression.ps1`. They are intended for a clean test user because Cloud Files registers one sync root per user.

## Exit codes and protocol

Successful commands return `0`. The process exit code table is:

Conflict actions report `data.conflictCommand` with the durable command ID and
`state` (`pending`, `resolved`, or `failed`). A queued action returns exit `11`;
only a completed action reports resolved and returns `0`. A failed action result
returns `7` with its state on stdout; request/transport failures use the ordinary
error envelope on stderr. Missing outcome information from an older Host is
reported as completion unknown, with exit `11`. Upload retry and keep-local
choices schedule a transfer; accepting that choice does not complete the transfer.

| Code | Meaning |
| ---: | --- |
| 1 | Usage |
| 2 | Validation |
| 3 | Unavailable |
| 4 | Timeout |
| 5 | Authentication |
| 6 | Authorization |
| 7 | Conflict |
| 8 | Unsupported |
| 9 | Storage |
| 10 | Cancelled |
| 11 | Accepted, pending completion or completion unknown |
| 70 | Internal failure |

These values are defined by `MirrorPulse.Control.Contracts.MirrorPulseControlExitCodes`. CLI JSON schema 1 is distinct from Control protocol version 1, although both currently use version 1. The named pipe is restricted to the current user; Adapter workers remain isolated processes and are never addressed directly by the CLI.

The CLI is the supported automation surface. Future UI work must call the same Control client and preserve the schema-1 request and response contracts.
