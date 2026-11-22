# One runtime model at the MSIX package root

RID-specific App builds are explicitly self-contained. The package already puts
the self-contained CLI and its .NET runtime at the root beside MirrorPulse.App.exe.
The App runtime configuration must therefore describe that deployment model.
Generic builds without a RuntimeIdentifier remain framework-dependent.

Previously, `--runtime win-arm64` with the default AnyCPU platform did not select
the ARM64 publish profile and left SelfContained false. That mixed a
shared-framework App configuration with app-local hostfxr from the CLI. The
[.NET 10 resolver](https://github.com/dotnet/runtime/blob/v10.0.0/src/native/corehost/fxr_resolver.cpp)
selects app-local hostfxr before global or environment runtime locations. A private
GUI apphost reproduction with identical executable and runtime configuration
entered its fixture normally without that DLL, but returned FrameworkMissingFailure
(`0x80008096`) when the DLL was placed beside it. This establishes the layout
defect, while the original installed CI stall's causal attribution remains open.

Before installing a test package, the verifier now checks the actual archive for
one nonempty copy of the four root runtime files and one bounded App runtime
configuration. It requires an included Microsoft.NETCore.App version and refuses
shared-framework requests, missing runtime files, oversized or invalid JSON and
case-ambiguous runtime configuration entries. Archive checks never report Shell
success; the original real installed launch, registration/readback, two-minute
deadline and cleanup still apply.

No CfSharp API or package version changes. Local checks compile and inspect App
outputs only. Cloud Files registration and installed App execution remain confined
to clean Windows CI users.
