function ConvertTo-CompiledTestCase {
    param([Parameter(Mandatory)]$Case, [Parameter(Mandatory)][string]$AssemblyPath)
    $id = [guid]::Empty
    if (-not [guid]::TryParseExact([string]$Case.testId, 'D', [ref]$id) -or $id -eq [guid]::Empty -or
        [string]::IsNullOrWhiteSpace($Case.fullyQualifiedName) -or [string]::IsNullOrWhiteSpace($Case.displayName) -or
        $Case.fullyQualifiedName.Length -gt 16384 -or $Case.displayName.Length -gt 16384 -or
        -not [IO.Path]::IsPathFullyQualified([string]$Case.source) -or
        [IO.Path]::GetFullPath($Case.source) -ine [IO.Path]::GetFullPath($AssemblyPath)) {
        throw 'A compiled test case lost its exact ID, names or original assembly.'
    }
    [ordered]@{ testId = $id.ToString('D'); fullyQualifiedName = [string]$Case.fullyQualifiedName
        displayName = [string]$Case.displayName; source = [IO.Path]::GetFullPath($Case.source) }
}

function Read-CompiledTestDiscovery {
    param([Parameter(Mandatory)][string]$TracePath, [Parameter(Mandatory)][string]$AssemblyPath)
    $file = Get-Item -LiteralPath $TracePath
    if ($file.PSIsContainer -or $file.Length -gt 128MB -or $file.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw 'Compiled discovery requires one bounded original trace file.'
    }
    $prefix = 'TestRequestSender.OnDiscoveryMessageReceived: Received message: '
    $cases = [Collections.Generic.List[object]]::new()
    $ids = [Collections.Generic.HashSet[guid]]::new()
    $complete = $null
    foreach ($line in [IO.File]::ReadLines($file.FullName)) {
        if ($line.Length -gt 16MB) { throw 'A compiled discovery message exceeds its bound.' }
        $offset = $line.IndexOf($prefix, [StringComparison]::Ordinal)
        if ($offset -lt 0) { continue }
        $message = $line.Substring($offset + $prefix.Length) | ConvertFrom-Json -Depth 100
        if ($message.MessageType -cnotin @('TestDiscovery.TestFound', 'TestDiscovery.Completed')) { continue }
        if ($null -ne $complete) { throw 'Compiled discovery continued after its completion.' }
        $batch = if ($message.MessageType -ceq 'TestDiscovery.Completed') {
            $complete = $message.Payload
            if ($complete.IsAborted -ne $false -or @($complete.FullyDiscoveredSources).Count -ne 1 -or
                $complete.FullyDiscoveredSources[0] -ine [IO.Path]::GetFullPath($AssemblyPath) -or
                @($complete.PartiallyDiscoveredSources).Count -or @($complete.NotDiscoveredSources).Count -or
                @($complete.SkippedDiscoverySources).Count) { throw 'Compiled discovery is aborted, partial or foreign.' }
            @($complete.LastDiscoveredTests | Where-Object { $null -ne $_ })
        } else { @($message.Payload) }
        foreach ($test in $batch) {
            $case = ConvertTo-CompiledTestCase -AssemblyPath $AssemblyPath -Case @{
                testId = $test.Id; fullyQualifiedName = $test.FullyQualifiedName
                displayName = $test.DisplayName; source = $test.Source
            }
            if (-not $ids.Add([guid]$case.testId)) { throw 'Compiled discovery contains a duplicate case ID.' }
            $cases.Add($case)
            if ($cases.Count -gt 100000) { throw 'The compiled discovery case bound was exceeded.' }
        }
    }
    if ($null -eq $complete -or $cases.Count -eq 0 -or $complete.TotalTests -ne $cases.Count) {
        throw 'Compiled discovery has no complete original case inventory.'
    }
    return $cases.ToArray()
}

function Assert-CompiledTestCaseInventory {
    param([Parameter(Mandatory)][object[]]$Discovered, [Parameter(Mandatory)][object[]]$Actual,
        [Parameter(Mandatory)][string]$AssemblyPath)
    $expected = [Collections.Generic.Dictionary[guid, string]]::new()
    foreach ($value in $Discovered) {
        $case = ConvertTo-CompiledTestCase -Case $value -AssemblyPath $AssemblyPath
        if (-not $expected.TryAdd([guid]$case.testId, ($case.fullyQualifiedName + [char]0 + $case.displayName))) {
            throw 'A discovered test case ID is duplicated.'
        }
    }
    $seen = [Collections.Generic.HashSet[guid]]::new()
    foreach ($value in $Actual) {
        $case = ConvertTo-CompiledTestCase -Case $value -AssemblyPath $AssemblyPath
        $id = [guid]$case.testId
        if (-not $seen.Add($id) -or -not $expected.ContainsKey($id) -or
            $expected[$id] -cne ($case.fullyQualifiedName + [char]0 + $case.displayName)) {
            throw 'An original result substitutes or duplicates a compiled case.'
        }
    }
    if ($seen.Count -eq 0 -or $seen.Count -ne $expected.Count) { throw 'An original result omits a compiled case.' }
}
