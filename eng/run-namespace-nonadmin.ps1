[CmdletBinding()]
param([Parameter(Mandatory)][string]$ConfigurationPath)

$ErrorActionPreference = 'Stop'
$configuration = Get-Content -LiteralPath $ConfigurationPath -Raw | ConvertFrom-Json
if ($configuration.schemaVersion -ne 1 -or $configuration.expectedArchitecture -notin @('X64', 'Arm64')) {
    throw 'The ordinary-user namespace configuration is invalid.'
}
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try {
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) -or
        $identity.User.Value -cne $configuration.expectedUserSid) {
        throw 'The namespace child is not the intended non-administrator user.'
    }
    $architecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
    if ($architecture -cne $configuration.expectedArchitecture) { throw 'The namespace child has the wrong architecture.' }
    $profileKey = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\' + $identity.User.Value)
    $loadedHive = [Microsoft.Win32.Registry]::Users.OpenSubKey($identity.User.Value)
    try { $profile = [string]$profileKey.GetValue('ProfileImagePath') }
    finally { if ($null -ne $profileKey) { $profileKey.Dispose() } }
    $profileLoaded = $null -ne $loadedHive
    if ($null -ne $loadedHive) { $loadedHive.Dispose() }
    if (-not $profileLoaded -or [string]::IsNullOrWhiteSpace($profile) -or -not (Test-Path -LiteralPath $profile -PathType Container) -or
        [IO.Path]::GetFileName($profile) -cne $configuration.expectedUserName) {
        throw 'The fresh Windows user profile was not loaded.'
    }
    # The child needs neither repository credentials nor the runner's private profile.
    # Clear inherited variables before launching any test or namespace consumer.
    foreach ($variable in @(Get-ChildItem Env:)) { [Environment]::SetEnvironmentVariable($variable.Name, $null, 'Process') }
    $env:SystemRoot = $configuration.systemRoot
    $env:WINDIR = $configuration.systemRoot
    $env:COMSPEC = Join-Path $configuration.systemRoot 'System32/cmd.exe'
    $env:PATH = @([IO.Path]::GetDirectoryName($configuration.dotnetPath), (Join-Path $configuration.systemRoot 'System32'),
        (Join-Path $configuration.systemRoot 'System32/Wbem')) -join ';'
    $env:USERPROFILE = $profile
    $env:SystemDrive = [IO.Path]::GetPathRoot($configuration.systemRoot).TrimEnd('\')
    $env:USERNAME = $configuration.expectedUserName
    $env:USERDOMAIN = $configuration.computerName
    $env:APPDATA = Join-Path $profile 'AppData/Roaming'
    $env:LOCALAPPDATA = Join-Path $profile 'AppData/Local'
    $env:TEMP = Join-Path $configuration.workDirectory 'temp'
    $env:TMP = $env:TEMP
    $env:DOTNET_CLI_HOME = $profile
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_NOLOGO = '1'
    $env:MIRRORPULSE_NATIVE_TEST = '1'
    $env:MIRRORPULSE_NAMESPACE_TEST_ARCHITECTURE = $architecture
    $env:MIRRORPULSE_NAMESPACE_REQUIRE_NONADMIN = '1'
    New-Item -ItemType Directory -Path $env:TEMP -Force | Out-Null
    [ordered]@{ schemaVersion=1; architecture=$architecture; nonAdministrator=$true; expectedUserMatched=$true;
        profileLoaded=$true } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $configuration.workDirectory 'context.json') -Encoding utf8
    & $configuration.dotnetPath test $configuration.testAssembly --filter $configuration.filter --logger 'trx;LogFileName=namespace.trx' `
        --results-directory (Join-Path $configuration.workDirectory 'results')
    if ($LASTEXITCODE -ne 0) { throw 'The ordinary-user namespace tests failed.' }
} finally { $identity.Dispose() }
