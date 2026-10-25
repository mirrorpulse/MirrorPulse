# Headless SDK deployment errors in the CI Shell probe

The disposable MirrorPulseShellProbe build selects the pinned Windows App SDK
DeploymentManager auto-initializer's None options. It retains the initializer,
original status/error handling and generated WinUI startup. A deployment error
continues to fail with its original exit status, without waiting for an error
dialog. Ordinary App builds keep the original default error UI.

The two-minute verifier deadline, real Shell registration/readback, probe entry,
result validation and cleanup are unchanged. The option does not fabricate probe
completion or satisfy installed Shell acceptance.

The retained local probe PE demonstrated a module initializer calling SDK
DeploymentManager with error UI enabled before Main. This justified headless
error reporting, but did not identify the actual CI stall. Neither App is run on
the local personal profile. Real installed behavior still requires a clean CI user.

Windows App SDK remains 2.5.1 and the selected Foundation package remains 2.3.12.
No CfSharp API, dependency version or product synchronization behavior changes.