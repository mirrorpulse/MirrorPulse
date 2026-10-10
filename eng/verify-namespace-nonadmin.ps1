[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('X64', 'Arm64')][string]$ExpectedArchitecture)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows -or $env:GITHUB_ACTIONS -cne 'true' -or $env:RUNNER_ENVIRONMENT -cne 'github-hosted') {
    throw 'Ordinary-user namespace verification is restricted to disposable GitHub-hosted Windows runners.'
}
$caller = [Security.Principal.WindowsIdentity]::GetCurrent()
try {
    if (-not [Security.Principal.WindowsPrincipal]::new($caller).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'The disposable account controller needs the hosted runner administrator token.'
    }
} finally { $caller.Dispose() }
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sourceSha = (& git -C $repository rev-parse HEAD | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $sourceSha -notmatch '^[0-9a-f]{40}$' -or $sourceSha -cne $env:GITHUB_SHA) {
    throw 'The ordinary-user test payload must describe the checked-out GitHub source.'
}
$payload = Join-Path $repository 'tests/MirrorPulse.CloudFiles.CfSharp.Tests/bin/Release/net10.0-windows10.0.26100.0'
$assemblyName = 'MirrorPulse.CloudFiles.CfSharp.Tests.dll'
if (-not (Test-Path -LiteralPath (Join-Path $payload $assemblyName) -PathType Leaf)) {
    throw 'Build the Release namespace test assembly before ordinary-user verification.'
}
$catalog = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'test-suites.json') -Raw | ConvertFrom-Json
$filter = (@($catalog.required.namespace) | ForEach-Object { 'FullyQualifiedName=' + $_ }) -join '|'
$evidenceDirectory = Join-Path $repository ('artifacts/test-results/namespace-nonadmin-' + $ExpectedArchitecture.ToLowerInvariant())
New-Item -ItemType Directory -Path $evidenceDirectory -Force | Out-Null
$identifier = [guid]::NewGuid().ToString('N')
$baseDirectory = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) 'MirrorPulseCi'
$scratch = [IO.Path]::GetFullPath((Join-Path $baseDirectory $identifier))
if ([IO.Path]::GetDirectoryName($scratch) -cne [IO.Path]::GetFullPath($baseDirectory)) { throw 'The disposable workspace is outside its designated parent.' }
$userName = 'mp-ci-' + $identifier.Substring(0, 10)
$user = $null
$child = $null
$marker = Join-Path $scratch '.mp-namespace-ci'
$success = $false
$context = $null
$userRemoved = $false
$profileRemoved = $false
$workspaceRemoved = $false
try {
    if (Test-Path -LiteralPath $scratch) { throw 'The disposable namespace workspace already exists.' }
    if ((Test-Path -LiteralPath $baseDirectory) -and
        ((Get-Item -LiteralPath $baseDirectory).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'The disposable workspace parent must not be a reparse point.'
    }
    New-Item -ItemType Directory -Path $scratch | Out-Null
    [IO.File]::WriteAllText($marker, $identifier)
    $copiedPayload = Join-Path $scratch 'payload'
    $work = Join-Path $scratch 'work'
    New-Item -ItemType Directory -Path $copiedPayload, $work | Out-Null
    $entries = @(Get-ChildItem -LiteralPath $payload -Recurse -Force)
    $files = @($entries | Where-Object { -not $_.PSIsContainer })
    if ($files.Count -le 0 -or $files.Count -gt 4096 -or ($files | Measure-Object Length -Sum).Sum -gt 512MB -or
        @($entries | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count -gt 0 -or
        @($files | Where-Object { $_.Extension -in @('.pfx', '.p12', '.pem', '.key') -or $_.Name -like '.env*' }).Count -gt 0) {
        throw 'The compiled namespace payload contains an unexpected or private file.'
    }
    foreach ($file in $files) {
        $relative = [IO.Path]::GetRelativePath($payload, $file.FullName)
        $destination = Join-Path $copiedPayload $relative
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $destination
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'run-namespace-nonadmin.ps1') -Destination (Join-Path $scratch 'run.ps1')
    $assemblyHash = (Get-FileHash -LiteralPath (Join-Path $payload $assemblyName) -Algorithm SHA256).Hash.ToLowerInvariant()
    if ((Get-FileHash -LiteralPath (Join-Path $copiedPayload $assemblyName) -Algorithm SHA256).Hash.ToLowerInvariant() -cne $assemblyHash) {
        throw 'The copied test assembly differs from the compiled source.'
    }
    # Passwords remain in memory and are never passed as arguments, written, or printed.
    $password = ConvertTo-SecureString ('Aa1!' + [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))) -AsPlainText -Force
    try {
        $user = New-LocalUser -Name $userName -Password $password -AccountExpires ([datetime]::UtcNow.AddHours(1)) -UserMayNotChangePassword
        $usersGroup = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-545')
        if (@(Get-LocalGroupMember -SID $usersGroup | Where-Object { $_.SID.Value -ceq $user.SID.Value }).Count -eq 0) {
            Add-LocalGroupMember -SID $usersGroup -Member $user
        }
        $credential = [pscredential]::new($env:COMPUTERNAME + '\' + $userName, $password)
        $acl = [IO.DirectoryInfo]::new($scratch).GetAccessControl([Security.AccessControl.AccessControlSections]::Access)
        $inheritance = [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($user.SID, 'ReadAndExecute', $inheritance, 'None', 'Allow'))
        [IO.DirectoryInfo]::new($scratch).SetAccessControl($acl)
        $acl = [IO.DirectoryInfo]::new($work).GetAccessControl([Security.AccessControl.AccessControlSections]::Access)
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($user.SID, 'FullControl', $inheritance, 'None', 'Allow'))
        [IO.DirectoryInfo]::new($work).SetAccessControl($acl)
        $configuration = Join-Path $scratch 'configuration.json'
        [ordered]@{ schemaVersion=1; expectedArchitecture=$ExpectedArchitecture; expectedUserSid=$user.SID.Value;
            expectedUserName=$userName; workDirectory=$work; testAssembly=(Join-Path $copiedPayload $assemblyName);
            filter=$filter; dotnetPath=(Get-Command dotnet -CommandType Application).Source;
            systemRoot=$env:SystemRoot; computerName=$env:COMPUTERNAME } |
            ConvertTo-Json | Set-Content -LiteralPath $configuration -Encoding utf8
        $childEnvironment = @{}
        foreach ($variable in @(Get-ChildItem Env:)) {
            if ($variable.Name -notin @('SystemRoot', 'WINDIR', 'SystemDrive', 'COMSPEC', 'PATH', 'ProgramFiles',
                'ProgramFiles(x86)', 'ProgramW6432', 'CommonProgramFiles', 'CommonProgramFiles(x86)', 'CommonProgramW6432')) {
                $childEnvironment[$variable.Name] = $null
            }
        }
        $childEnvironment['TEMP'] = $work
        $childEnvironment['TMP'] = $work
        $child = Start-Process -FilePath (Get-Command pwsh -CommandType Application).Source -Credential $credential -LoadUserProfile `
            -Environment $childEnvironment `
            -WindowStyle Hidden -PassThru -WorkingDirectory $scratch `
            -ArgumentList @('-NoProfile', '-NonInteractive', '-File', ('"' + (Join-Path $scratch 'run.ps1') + '"'),
                '-ConfigurationPath', ('"' + $configuration + '"')) `
            -RedirectStandardOutput (Join-Path $scratch 'stdout.log') -RedirectStandardError (Join-Path $scratch 'stderr.log')
        if (-not $child.WaitForExit(600000)) {
            $child.Kill($true)
            $child.WaitForExit()
            throw 'The ordinary-user namespace child did not finish within its CI step budget.'
        }
        $child.WaitForExit()
        $contextPath = Join-Path $work 'context.json'
        if (Test-Path -LiteralPath $contextPath -PathType Leaf) {
            Copy-Item -LiteralPath $contextPath -Destination (Join-Path $evidenceDirectory 'context.json')
            $context = Get-Content -LiteralPath $contextPath -Raw | ConvertFrom-Json
        }
        $results = Join-Path $work 'results'
        if (Test-Path -LiteralPath $results -PathType Container) {
            foreach ($file in @(Get-ChildItem -LiteralPath $results -File -Filter '*.trx')) {
                Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $evidenceDirectory $file.Name)
            }
        }
        if ($child.ExitCode -ne 0) {
            # Output contains only the credential-free test child's own synthetic diagnostics.
            Get-Content -LiteralPath (Join-Path $scratch 'stdout.log') -Tail 60
            Get-Content -LiteralPath (Join-Path $scratch 'stderr.log') -Tail 60
            throw 'The ordinary-user namespace child failed; its original results are retained.'
        }
        if ($context.schemaVersion -ne 1 -or $context.architecture -cne $ExpectedArchitecture -or
            $context.nonAdministrator -ne $true -or $context.expectedUserMatched -ne $true -or $context.profileLoaded -ne $true) {
            throw 'The actual ordinary-user execution context was not verified.'
        }
        & (Join-Path $PSScriptRoot 'verify-test-results.ps1') -Suite namespace -ResultsDirectory $evidenceDirectory `
            -OutputPath (Join-Path $evidenceDirectory 'test-evidence.json')
        $success = $true
    } finally { $credential = $null; $password.Dispose() }
} finally {
    try {
        if ($null -ne $child) {
            if (-not $child.HasExited) { $child.Kill($true); $child.WaitForExit() }
            $child.Dispose()
        }
        if ($null -ne $user) {
            $profile = @(Get-CimInstance Win32_UserProfile -Filter ("SID='" + $user.SID.Value + "'"))
            foreach ($entry in $profile) {
                $profilePath = [IO.Path]::GetFullPath($entry.LocalPath)
                $profileParent = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetPathRoot($env:SystemRoot)) 'Users'))
                if ($entry.Loaded -or [IO.Path]::GetDirectoryName($profilePath) -cne $profileParent -or
                    [IO.Path]::GetFileName($profilePath) -cne $userName -or $entry.Special) {
                    throw 'The disposable profile is loaded or outside its verified user directory; cleanup was refused.'
                }
                $entry | Remove-CimInstance
            }
            $profileRemoved = @(Get-CimInstance Win32_UserProfile -Filter ("SID='" + $user.SID.Value + "'")).Count -eq 0
            $actual = Get-LocalUser -Name $userName
            if ($actual.SID.Value -cne $user.SID.Value) { throw 'The disposable account identity changed; cleanup was refused.' }
            Remove-LocalUser -InputObject $actual
            $userRemoved = $null -eq (Get-LocalUser -Name $userName -ErrorAction SilentlyContinue)
        }
    } finally {
        if (Test-Path -LiteralPath $scratch) {
            if ([IO.Path]::GetDirectoryName($scratch) -cne [IO.Path]::GetFullPath($baseDirectory) -or
                [IO.Path]::GetFileName($scratch) -cne $identifier -or
                ((Get-Item -LiteralPath $scratch).Attributes -band [IO.FileAttributes]::ReparsePoint) -or
                -not (Test-Path -LiteralPath $marker -PathType Leaf) -or [IO.File]::ReadAllText($marker) -cne $identifier) {
                throw 'The disposable workspace marker or resolved path changed; cleanup was refused.'
            }
            Remove-Item -LiteralPath $scratch -Recurse -Force
            $workspaceRemoved = -not (Test-Path -LiteralPath $scratch)
        }
    }
}
if ($success -and $userRemoved -and $profileRemoved -and $workspaceRemoved) {
    [ordered]@{ schemaVersion=1; sourceSha=$sourceSha; architecture=$ExpectedArchitecture; nonAdministrator=$true;
        freshUser=$true; expectedUserMatched=$true; profileLoaded=$true; accountRemoved=$true; profileRemoved=$true;
        workspaceRemoved=$true; testAssemblySha256=$assemblyHash;
        contextSha256=(Get-FileHash -LiteralPath (Join-Path $evidenceDirectory 'context.json') -Algorithm SHA256).Hash.ToLowerInvariant();
        trxSha256=(Get-FileHash -LiteralPath (Join-Path $evidenceDirectory 'namespace.trx') -Algorithm SHA256).Hash.ToLowerInvariant();
        tests=(Get-Content -LiteralPath (Join-Path $evidenceDirectory 'test-evidence.json') -Raw | ConvertFrom-Json) } |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $evidenceDirectory 'ordinary-user-evidence.json') -Encoding utf8
    Write-Output 'The actual ordinary-user namespace suite and disposable cleanup passed.'
} else { throw 'The ordinary-user namespace verification or cleanup was incomplete.' }
