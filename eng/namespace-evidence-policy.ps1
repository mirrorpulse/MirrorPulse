function Assert-NamespaceTestExecution {
    param([Parameter(Mandatory)][object]$Evidence, [Parameter(Mandatory)][object]$Catalog)

    $required = @($Catalog.required.namespace)
    $nativeCount = @($required | Where-Object { $Catalog.required.native -contains $_ }).Count
    $managedCount = $required.Count - $nativeCount
    if ($required.Count -le 0 -or $nativeCount -le 0 -or $managedCount -le 0 -or $Evidence.suite -cne 'namespace') {
        throw 'The dedicated namespace suite is missing.'
    }
    foreach ($item in @($Evidence) + @($Evidence.categories)) {
        foreach ($field in @('selected', 'executed', 'skipped')) {
            if ($item.$field -isnot [int] -and $item.$field -isnot [long]) { throw 'Namespace execution counts must be integers.' }
        }
    }
    if ($Evidence.selected -ne $required.Count -or $Evidence.executed -ne $required.Count -or $Evidence.skipped -ne 0 -or
        @($Evidence.categories).Count -ne 2 -or
        @($Evidence.categories | Where-Object { $_.category -ceq 'native' -and $_.selected -eq $nativeCount -and $_.executed -eq $nativeCount -and $_.skipped -eq 0 }).Count -ne 1 -or
        @($Evidence.categories | Where-Object { $_.category -ceq 'managed' -and $_.selected -eq $managedCount -and $_.executed -eq $managedCount -and $_.skipped -eq 0 }).Count -ne 1) {
        throw 'Namespace verification must execute every required lifecycle and native test without skips.'
    }
}
