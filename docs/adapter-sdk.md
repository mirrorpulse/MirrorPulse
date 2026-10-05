# SDK dependency verification

MirrorPulse consumes `MirrorPulse.Adapter.Sdk` from a fixed GitHub Release in [MirrorPulse/adapter-template](https://github.com/MirrorPulse/adapter-template). `eng/adapter-sdk.lock.json` pins the package ID, exact version, source commit, release tag, file name, byte length and SHA256. This package is not fetched from `latest` or from nuget.org.

## Restore

```powershell
pwsh -File eng/restore-adapter-sdk.ps1
dotnet restore MirrorPulse.sln --locked-mode
```

The bootstrap verifies the complete archive before moving it into `artifacts/sdk-feed`. NuGet package source mapping binds the exact SDK ID to this feed; other package IDs use nuget.org. A corrupt existing feed entry fails verification. Package lock files additionally pin NuGet content hashes.

For an offline SDK archive, run `pwsh -File eng/restore-adapter-sdk.ps1 -PackagePath C:\Packages\MirrorPulse.Adapter.Sdk.0.2.0.nupkg`. The archive must match the same committed length and SHA256. Other dependencies still need a cache or network access.

## Update

1. Select an already published fixed SDK version and verify its release manifest, source commit and asset hash.
2. Update the pin and exact central package version in the same change. Use a fresh NuGet package cache so an older candidate with the same version cannot satisfy restore.
3. Regenerate affected package locks, then run locked restore, Release build, formatting and real Worker interoperability tests.
4. Regenerate dependency notices, preserving the package license and its fixed GitHub Release source. Run the native and installed product gates from the same reviewed product commit.

Previously published versions are not replaced by the release workflow. SHA256 verification detects later asset changes even if a repository administrator modifies a release.

The Host retains v1 single-root compatibility. The v2 SDK and conformance profile do not imply that the existing official protocol Workers have already been migrated to v2.
