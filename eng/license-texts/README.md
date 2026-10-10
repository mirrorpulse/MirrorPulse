# Upstream license texts

These files preserve license texts for packages that declare a license expression without including the text in their NuGet archive. `../dependency-license-sources.json` binds each text to an exact package version and upstream commit.

- CfSharp 0.1.0-preview.5: [LICENSE at the package source commit](https://github.com/MirrorPulse/CfSharp/blob/adad62f2eb232ec381e537cd5c4c9e45eccc1095/LICENSE).
- Microsoft.Windows.SDK.BuildTools.WinApp 0.7.1: [LICENSE at the package source commit](https://github.com/microsoft/WinAppCli/blob/e3cd0b94f3e7b776ac51ba0564f4908094e979b5/LICENSE).

The dependency collector copies these texts into the generated distribution notices. CfSharp package NOTICE files are copied from the restored packages separately.
