# MirrorPulse CLI

The Windows package exposes `mp` as an app execution alias. Open PowerShell and
run `mp --help`. Use the package that matches your system architecture. See
[Windows support](windows-support.md) and [capabilities and limits](cli-capabilities.md).

Most commands start the current user's MirrorPulse Host if needed. Pass
`--no-start` to require a running Host. `mp host restart` stops the current
Host. The next ordinary command starts it with the updated configuration.
During startup, `mp --json --no-start host status` can report `Starting` with
an optional startup phase. Requests that need the storage session may return
`mp.control.hostBusy` until startup finishes.

## Connect an Adapter

Install an official signed `.mpadapter` package, find its installation ID,
and create an instance. The configuration keys depend on the Adapter. This
example connects the Local Adapter to a Documents directory.

```powershell
mp adapter install .\YourLocalAdapter.mpadapter
mp --json adapter list
mp instance create --install-id INSTALL_ID --name Documents `
  --config 'sourceDirectory=C:\Users\me\Documents'
mp --json status
```

Replace the package path, installation ID, and directory with your own values.
Pass repeated `--config k=v` options for settings. Use `--root KEY=LABEL`
to choose the folder name that MirrorPulse displays. For a signed setup form,
use `--secret-field KEY=VALUE` for a declared password field and
`--clear-secret-field KEY` to remove it when editing. Legacy packages take a
single secret through `--secret`. Avoid shell history and log capture when
entering a credential. A configuration patch preserves omitted fields.

## Commands

Global options precede the command. Use `--json` for automation, `--version`
for the version, and `-h` or `--help` for help. `--developer-mode` allows the
configured development Host. Official package signature checks still apply.
The CLI accepts `--timeout SEC`. Host requests use their built-in deadline.

| Command | Purpose |
| --- | --- |
| `mp status`, `mp sync status`, `mp sync refresh [--force]` | Inspect work or request a synchronization pass. Use `--force` to retry immediately during backoff. |
| `mp host status\|start\|stop\|restart` | Inspect or control the current user's Host. |
| `mp adapter list` | List packages, versions, and instances. |
| `mp adapter install PACKAGE`, `mp adapter update PACKAGE` | Install a signed file or add a newer version. |
| `mp adapter remove --adapter-id ID [--install-id ID] [--purge]` | Remove an unused installation. `uninstall` is an alias. |
| `mp instance list` | List Adapter instances and their roots. |
| `mp instance create --install-id ID --name NAME [--config k=v] [--root k=v] [--disabled]` | Connect a source. For a signed setup form, include `--secret-field KEY=VALUE` or `--authorization ID` when the Adapter requires it. |
| `mp instance configure --instance-id ID --name NAME [--config k=v] [--root k=v]` | Update the instance. For a signed form, also pass `--revision GUID` from `mp --json instance list`. |
| `mp instance auth-start --install-id ID --callback ID [--config k=v]` | Start a signed form's authorization callback. |
| `mp instance auth-poll AUTHORIZATION_ID`, `mp instance auth-cancel AUTHORIZATION_ID` | Check or cancel authorization. Pass the completed authorization ID when saving the form. |
| `mp instance enable\|disable --instance-id ID` | Turn synchronization for an instance on or off. |
| `mp instance check-update --instance-id ID` | Ask a running Adapter whether a new package is available. |
| `mp instance install-update OFFER_ID` | Confirm and install the signed update alongside existing versions. |
| `mp instance select-version --instance-id ID --install-id ID` | Select an installed package version. |
| `mp conflict list\|show\|snooze\|resolve` | Inspect, defer, or submit a decision. Use `--conflict-id ID` and, for resolve, `--action ACTION` and optional `--preserved-path PATH`. |
| `mp operation get\|watch\|cancel --operation-id ID` | Read operation state or request cancellation. Watch reads one snapshot. Cancellation records the request without stopping work in progress. |
| `mp config` | Read or update settings. Options include `--locale`, `--developer-mode`, `--start-with-windows`, `--enable-installation ID`, and `--sync-root-display-name NAME`. |
| `mp developer-mode [--developer-mode true\|false]` | Read or change the development setting. |
| `mp startup [--start-with-windows true\|false]` | Read or change the saved startup preference. |
| `mp diagnostics [--include-logs] [--output PATH]` | Export a local diagnostic archive. |

Conflict actions include `KeepLocal`, `KeepRemote`, `KeepBoth`, `Retry`,
`DeleteLocal`, and `DeleteRemote`. See [capabilities and limits](cli-capabilities.md)
for the scope of those actions.

## Managed files and roots

Use `mp root list` to find a root ID, see its current label, and inspect
pending recovery. A label change updates the name shown in MirrorPulse. The
storage source directory stays in place.

| Command | Purpose |
| --- | --- |
| `mp root prepare-rename ROOT_ID LABEL` | Prepare a label change and return its operation ID. |
| `mp root execute-rename OPERATION_ID`, `mp root recover OPERATION_ID` | Execute or recover that change. |
| `mp root cancel-rename OPERATION_ID` | Cancel a preparation before the move starts. |
| `mp namespace member inspect RELATIVE_PATH [--directory]`, `mp namespace member tree MEMBER_ID` | Find a managed item or list its current subtree. |
| `mp namespace member create PARENT_ID NAME [--directory] [--operation-id GUID]` | Create an empty file or directory in a managed parent. |
| `mp namespace member prepare-import PARENT_ID NAME SOURCE_PATH [--operation-id GUID]` | Stage a file and obtain an import operation ID. |
| `mp namespace member import OPERATION_ID`, `mp namespace member import-status OPERATION_ID` | Run or inspect the import. |
| `mp namespace member prepare-replace MEMBER_ID SOURCE_PATH [--operation-id GUID]` | Stage replacement of an existing file. Managed inputs and targets must be available locally. |
| `mp namespace member replace OPERATION_ID`, `mp namespace member replace-recover OPERATION_ID`, `mp namespace member replacement-status OPERATION_ID` | Run, resume, or inspect the replacement. |
| `mp namespace member prepare-move MEMBER_ID PARENT_ID NAME [--operation-id GUID]` | Prepare a move within a managed root. |
| `mp namespace member move OPERATION_ID`, `mp namespace member move-status OPERATION_ID` | Run or inspect the move. |
| `mp namespace member prepare-retire MEMBER_ID [--recursive] [--operation-id GUID]` | Prepare deletion of a managed item. Use `--recursive` for a subtree. |
| `mp namespace member retire OPERATION_ID [--recursive]`, `mp namespace member retirement-status OPERATION_ID [--recursive]` | Run or inspect the deletion. Use the same recursive mode when resuming. |

Save the original operation ID and reuse it on retry. Local completion is
reported separately from synchronization to the storage source. Explicit local
operations on a disabled root keep ordinary synchronization paused.

### Transfers

Transfers require distinct Adapter instances. Directory transfers require
compatible source and destination Adapters. Use a unique operation ID and keep
it for retries or status queries.

| Command | Purpose |
| --- | --- |
| `mp namespace transfer move OPERATION_ID SOURCE_MEMBER_ID TARGET_PARENT_ID NAME` | Start a transfer. |
| `mp namespace transfer prepare OPERATION_ID SOURCE_MEMBER_ID TARGET_PARENT_ID NAME` | Prepare a transfer for later execution. |
| `mp namespace transfer resume OPERATION_ID` | Continue the original transfer, including after a Host restart. |
| `mp namespace transfer status OPERATION_ID`, `mp namespace transfer list ROOT_ID` | Inspect transfer progress. |

### Namespace recovery

| Command | Purpose |
| --- | --- |
| `mp namespace protection initialize [--operation-id GUID]` | Initialize protection for the configured roots. |
| `mp namespace protection recover-current [--operation-id GUID]` | Audit and recover the current registered tree. |
| `mp namespace protection recover-root ROOT_ID [--operation-id GUID]` | Audit and recover one root. |
| `mp namespace protection initialization-status OPERATION_ID` | Check an initialization or recovery attempt. |
| `mp namespace protection initializations [--after OPERATION_ID]` | Find saved attempts, including one whose response was lost. |
| `mp namespace protection captures ROOT_ID [--after MANIFEST_ID]` | List saved tree captures. |
| `mp namespace protection recover-tree MANIFEST_ID` | Recover one sealed capture. Changes to the tree can require current-tree recovery. |

## JSON output and exit codes

Successful data commands write a JSON object with `schemaVersion: "1"`,
`kind: "result"`, and `data` to stdout. Request errors write an object with
`kind: "error"`, `exitCode`, `code`, and `message` to stderr. Help and version
use the `help` and `version` kinds.

For conflict resolutions, inspect `data.conflictCommand.state`. A pending
decision returns exit `11`, a resolved decision returns `0`, and a failed
decision can return `7` with its result on stdout.

| Code | Meaning |
| ---: | --- |
| 0 | Success |
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
| 11 | Pending or completion unknown |
| 70 | Internal failure |
