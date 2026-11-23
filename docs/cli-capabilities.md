# Capabilities and limits

MirrorPulse presents one Explorer sync root per Windows user. Each connected
storage source has its own Adapter instance and appears as a top-level directory.
The current configuration uses one declared root per instance. You can connect
multiple independent instances.

| Area | Available behavior and limits |
| --- | --- |
| Adapters | Install official signed `.mpadapter` packages from a local file. Adapters with update discovery can offer a version from a local path or HTTPS. You confirm installation and choose which deployment uses the version. Adding an instance, changing its configuration, or selecting a package version may require restarting the Host. |
| File access and synchronization | Browse directories, read files on demand, and synchronize supported content changes. Disabled sources remain visible and ordinary synchronization stays paused. Local metadata-only changes are outside the content sync policy. |
| Moves and transfers | Managed commands cover local file and directory moves and transfers between independent instances. Directory transfers require compatible source and destination Adapters. Transfers between roots of the same instance are unavailable. |
| Conflicts | List, inspect, defer, and submit a resolution. A submitted decision can remain pending. Some Retry, DeleteLocal, and DeleteRemote paths are incomplete. |
| Windows integration | The MSIX exposes the `mp` app execution alias. The Windows startup setting is stored without registering a startup task. Pinning and freeing space have no complete CLI command. |
| Diagnostics | Export a local archive on request. MirrorPulse does not upload it automatically. |

The capabilities an Adapter offers depend on its package and configured source.
In particular, remote write, delete, and directory move support vary by backend.
See the [CLI reference](cli.md) for the commands, setup example, and exit codes.
