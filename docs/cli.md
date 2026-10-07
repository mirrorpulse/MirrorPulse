# MirrorPulse CLI

`mp` is the no-UI control surface for MirrorPulse. It sends versioned requests to the current-user Host over a per-user named pipe. If the Host is not running, the CLI starts the installed Host automatically. `--no-start` disables that behavior for callers that require an already-running service.

`mp host stop` and `mp host restart` return after the previous Host process has exited. A later ordinary command can then start the Host with the updated instance topology.

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

Global options must precede the command. Scalar command options accept `--name value` or `--name=value`; repeatable `--config`, `--root`, and `--enable-installation` options use separate values. Creation and configuration patches use the same Adapter field validation. A patch retains unspecified settings and the Host-managed credential reference. Secret fields and credential references are rejected in ordinary `--config` values. Secrets are accepted through `--secret` during creation. Avoid shell history and log capture. Other credential-input helpers and rotation/removal CLI options are not fully wired into these commands.

See the [capability baseline](cli-capabilities.md) for limitations. Operation watch currently reads one snapshot, and cancel does not stop the underlying work. Startup and developer-mode commands persist settings but do not yet register Windows startup or allow unsigned packages. A conflict response does not prove the queued action completed.

## Automation examples

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
