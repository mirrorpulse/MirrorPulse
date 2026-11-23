# Contributing to MirrorPulse

Thank you for contributing. MirrorPulse is a Windows-first Cloud Files application with independently packaged Adapter workers.

## License

Contributions to MirrorPulse are accepted under the [Apache License 2.0](LICENSE). By submitting a contribution, you agree that it may be distributed under that license unless a separate written agreement says otherwise.

## Language

Use English for tracked source code, configuration, documentation, commit
messages, issues, and pull requests.

## Before opening a pull request

1. Keep one logical behavior or fix in each commit.
2. Add or update tests for behavior changes.
3. Run the smallest relevant validation, followed by the full solution build when practical.
4. Do not commit credentials, tokens, certificates, private paths, or user data.
5. Describe Cloud Files, Adapter protocol, configuration, migration, and release impact.

## Local validation

Use the .NET SDK selected by `global.json` and run these commands from the
repository root.

```powershell
pwsh ./eng/verify-build.ps1
pwsh ./eng/verify-format.ps1
```

The build script verifies the locked Adapter SDK, restores the solution, and
runs a Release build and the ordinary tests. Native Cloud Files registration
and installed MSIX checks require a clean test account on a
[supported Windows release](docs/windows-support.md).

The build restores the exact SDK archive pinned in `eng/adapter-sdk.lock.json`
from the [Adapter SDK releases](https://github.com/mirrorpulse/adapter-sdk/releases).
For an offline archive, run `pwsh ./eng/restore-adapter-sdk.ps1 -PackagePath PATH`
before restoring. The archive must match the committed length and SHA-256.

## Adapter changes

Adapter Workers use the versioned Host protocol and run in their own process.
Update the associated contract tests and package manifest when changing
capabilities, roots, installation policy, or protocol behavior. Start an
independent Worker with the [Adapter template](https://github.com/mirrorpulse/adapter-template).

## Pull requests

Keep pull requests reviewable. Include the verification command and explain any known limitation or CfSharp preview issue. Release, signing, and Microsoft Store changes require explicit evidence in the pull request.
