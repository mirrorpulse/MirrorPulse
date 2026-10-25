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

The verifier also sets `DOTNET_DISABLE_GUI_ERRORS=1` in the disposable probe
child's environment. This covers native .NET apphost errors before CLR or SDK
initialization. The parent environment and ordinary App launches are unchanged;
an apphost failure keeps its original exit code and cannot satisfy Shell success.
See the [.NET environment variable documentation](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-environment-variables#dotnet_disable_gui_errors)
and the [.NET 10 apphost implementation](https://github.com/dotnet/runtime/blob/v10.0.0/src/native/corehost/apphost/apphost.windows.cpp).

A synthetic private copy of the existing test apphost uses the Windows GUI PE
subsystem and an unavailable framework version. Through the real verifier launch
and observation functions, it must exit with `FrameworkMissingFailure`
(`0x80008096`), leaving both precreated outputs empty. It does not run the App,
register Cloud Files, install a package or prove the installed CI failure's cause.

The process-boundary test runs outside MSTest's parallel method queue so its
cold PowerShell/native interop compilation does not compete with that queue.
Its original 30-second process deadline and every observation assertion remain.
The prior CI timeout is retained; scheduling contention is a hypothesis, not a
proven root cause. Installed acceptance still requires the original clean-user gate.
