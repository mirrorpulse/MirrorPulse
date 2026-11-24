function Invoke-InstalledAuthorization($Edition) {
    $evidence.authorization = @{ fixtureSigner='ephemeral'; phase='Build Workers'; verified=$false }
    $workspaceFixture = Join-Path $scratch 'authorization'
    $worker = Join-Path $workspace 'tests/MirrorPulse.Worker.ProtocolFixture/MirrorPulse.Worker.ProtocolFixture.csproj'
    foreach ($target in 'win-arm64', 'win-x64') {
        $build = @("-p:DefaultAppHostRuntimeIdentifier=$target", "-p:AppHostRuntimeIdentifier=$target",
            "-p:ArtifactsPath=$(Join-Path $workspaceFixture "build/$target")")
        & dotnet restore $worker --locked-mode @build | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Could not restore the authorization Worker for $target." }
        $destination = Join-Path $workspaceFixture "payload/worker/$target"
        & dotnet publish $worker -c Release --self-contained false --no-restore -o $destination @build | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Could not publish the authorization Worker for $target." }
    }

    # Seed only the empty verification catalog with a signed package verified against an ephemeral publisher.
    # The installed application and Host keep the ordinary product trust configuration.
    $evidence.authorization.phase = 'Seed the empty installation'
    $data = @(Get-EditionDataRoots $Edition | Where-Object { Test-Path -LiteralPath (Join-Path $_ 'state/product.db') -PathType Leaf })
    if ($data.Count -ne 1) { throw 'Could not identify the unique installed verification catalog.' }
    $syncRoot = Join-Path $user $Edition.Prefix
    $dataRoot = $data[0]
    $topology = Invoke-EditionCli $Edition @('--no-start','adapter','list')
    if ($topology.installations.Count -ne 0 -or $topology.instances.Count -ne 0) { throw 'The authorization catalog is not empty.' }
    $null = Invoke-EditionCli $Edition @('--no-start','host','stop')
    $logo = Join-Path $workspace 'src/MirrorPulse.App/Assets/Square44x44Logo.scale-200.png'
    $seed = & dotnet $probeAssembly --desktop-authorization-seed $syncRoot $dataRoot $workspaceFixture $logo
    if ($LASTEXITCODE -ne 0) { throw 'The signed authorization fixture could not seed the installed catalog.' }
    $receipt = $seed | ConvertFrom-Json
    if ($receipt.installId -cnotmatch '^[0-9a-f-]{36}$' -or $receipt.sha256 -cnotmatch '^[a-fA-F0-9]{64}$') {
        throw 'The signed authorization fixture provided an invalid receipt.'
    }

    function Read-AuthorizedDeployment {
        $current = Invoke-EditionCli $Edition @('--no-start','adapter','list')
        if ($current.installations.Count -ne 1 -or $current.installations[0].installId -cne $receipt.installId -or
            $current.installations[0].packageSha256 -ine $receipt.sha256 -or !$current.installations[0].isSigned -or
            $current.instances.Count -ne 1 -or $current.roots.Count -ne 1) {
            throw 'The installed Host did not retain the signed authorization fixture and its deployment.'
        }
        $instance = $current.instances[0]
        if ($instance.installId -cne $receipt.installId -or $instance.displayName -cne 'Authorization desktop fixture' -or
            $instance.configuration.account -cne 'Synthetic desktop account' -or $null -ne $instance.configuration.token -or
            $instance.configuredPasswordFields.Count -ne 1 -or $instance.configuredPasswordFields[0] -cne 'token' -or
            $instance.credentialReferences.Count -ne 1 -or $current.roots[0].instanceId -cne $instance.instanceId) {
            throw 'The installed authorization result did not retain the expected form values and protected credential.'
        }
        [pscustomobject]@{ id=$instance.instanceId; rootId=$current.roots[0].rootId
            revision=$instance.configurationRevision; credential=$instance.credentialReferences[0] }
    }

    $evidence.authorization.packageSha256 = $receipt.sha256
    $finished = $false
    try {
        $null = Invoke-EditionCli $Edition @('status')
        $Edition.HostId = Get-EditionHost $Edition
        $ui = Join-Path $PSScriptRoot 'verify-installed-authorization-ui.ps1'
        $evidence.authorization.phase = 'First configuration'
        & powershell.exe -NoLogo -NoProfile -NonInteractive -File $ui -ProcessId $Edition.WindowId -Action Create | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'The installed callback did not complete first authorization.' }
        $original = Read-AuthorizedDeployment
        $evidence.authorization.created = $true
        $evidence.authorization.phase = 'Restart the installed Host'
        $originalHost = $Edition.HostId
        $null = Invoke-EditionCli $Edition @('--no-start','host','stop')
        $null = Invoke-EditionCli $Edition @('status')
        $Edition.HostId = Get-EditionHost $Edition
        $reopened = Read-AuthorizedDeployment
        if ($Edition.HostId -eq $originalHost -or $reopened.id -cne $original.id -or $reopened.rootId -cne $original.rootId -or
            $reopened.revision -cne $original.revision -or $reopened.credential -cne $original.credential) {
            throw 'The created authorization did not survive Host restart intact.'
        }
        $evidence.authorization.reopened = $true
        $evidence.authorization.phase = 'Reauthorize'
        & powershell.exe -NoLogo -NoProfile -NonInteractive -File $ui -ProcessId $Edition.WindowId -Action Renew | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'The installed callback did not complete reauthorization.' }
        $renewed = Read-AuthorizedDeployment
        if ($renewed.id -cne $original.id -or $renewed.rootId -cne $original.rootId -or
            $renewed.revision -ceq $original.revision -or $renewed.credential -ceq $original.credential) {
            throw 'Reauthorization changed the deployment or did not advance its credential revision.'
        }
        $evidence.authorization.phase = 'Verify Windows Credential Manager'
        $null = Invoke-EditionCli $Edition @('--no-start','host','stop')
        & dotnet $probeAssembly --desktop-authorization-finish $syncRoot $dataRoot $receipt.installId $original.credential | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'Could not verify and remove the test credential in Windows Credential Manager.' }
        $finished = $true
        $evidence.authorization.verified = $true
        $evidence.authorization.phase = 'Complete'
    } finally {
        if (!$finished) {
            if ($Edition.HostId -and (Get-Process -Id $Edition.HostId -ErrorAction SilentlyContinue)) {
                $null = Invoke-EditionCli $Edition @('--no-start','host','stop')
            }
            & dotnet $probeAssembly --desktop-authorization-finish $syncRoot $dataRoot $receipt.installId cleanup | Out-Host
            if ($LASTEXITCODE -ne 0) { Write-Warning 'The disposable runner could not clean the test authorization.' }
        }
    }
}
