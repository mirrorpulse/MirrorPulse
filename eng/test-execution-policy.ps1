function Get-RequiredTestCaseNames {
    param([Parameter(Mandatory)][object]$Catalog, [Parameter(Mandatory)][string]$Name)

    $property = if ($null -ne $Catalog.requiredCases) { $Catalog.requiredCases.PSObject.Properties[$Name] } else { $null }
    if ($null -eq $property) { return }
    $cases = @($property.Value)
    $distinct = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    if ($property.Value -isnot [array] -or $cases.Count -lt 1 -or $cases.Count -gt 32) {
        throw 'Explicit required cases must be a bounded nonempty array.'
    }
    foreach ($case in $cases) {
        if ($case -isnot [string] -or [string]::IsNullOrWhiteSpace($case) -or $case.Length -gt 512 -or
            $case.Contains("`r") -or $case.Contains("`n") -or -not $distinct.Add($case)) {
            throw 'Explicit required cases must have distinct bounded original display names.'
        }
        $case
    }
}

function Get-RequiredTestExecutionCount {
    param([Parameter(Mandatory)][object]$Catalog, [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$Names)

    $count = 0
    foreach ($name in $Names) {
        $cases = @(Get-RequiredTestCaseNames -Catalog $Catalog -Name $name)
        $count += if ($cases.Count) { $cases.Count } else { 1 }
    }
    return $count
}

function Assert-RequiredTestResults {
    param([Parameter(Mandatory)][object]$Catalog, [Parameter(Mandatory)][string]$Suite,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Results)

    foreach ($required in $Catalog.required.$Suite) {
        $matched = @($Results | Where-Object name -CEQ $required)
        $cases = @(Get-RequiredTestCaseNames -Catalog $Catalog -Name $required)
        if ($cases.Count -eq 0) {
            if ($matched.Count -ne 1 -or $matched[0].outcome -cne 'Passed') {
                throw "Required $Suite test did not execute successfully exactly once: $required"
            }
        } else {
            if ($matched.Count -ne $cases.Count -or @($matched | Where-Object outcome -CNE Passed).Count -or
                @(Compare-Object @($cases | Sort-Object -CaseSensitive) @($matched.case | Sort-Object -CaseSensitive) -CaseSensitive).Count) {
                throw "Required $Suite data rows must execute each original case successfully exactly once: $required"
            }
            foreach ($field in @('testId', 'executionId')) {
                $ids = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
                foreach ($row in $matched) {
                    if ([string]::IsNullOrWhiteSpace($row.$field) -or -not $ids.Add($row.$field)) {
                        throw 'Required data rows must retain distinct original test and execution IDs.'
                    }
                }
            }
        }
    }
}
