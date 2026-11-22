function Assert-OrdinaryUserNamespaceEvidence {
    param([Parameter(Mandatory)][object]$Evidence, [Parameter(Mandatory)][object]$Catalog,
        [Parameter(Mandatory)][string]$Architecture)

    if ($Evidence.schemaVersion -ne 1 -or $Evidence.architecture -cne $Architecture) {
        throw 'The ordinary-user namespace context is missing or has the wrong architecture.'
    }
    foreach ($field in @('nonAdministrator', 'freshUser', 'expectedUserMatched', 'profileLoaded',
        'accountRemoved', 'profileRemoved', 'workspaceRemoved')) {
        if ($Evidence.$field -isnot [bool] -or $Evidence.$field -ne $true) {
            throw 'The actual ordinary-user execution or disposable cleanup is incomplete.'
        }
    }
    foreach ($field in @('testAssemblySha256', 'contextSha256', 'trxSha256')) {
        if ($Evidence.$field -isnot [string] -or $Evidence.$field -cnotmatch '^[0-9a-f]{64}$') {
            throw 'The ordinary-user test payload or original results are not bound by digest.'
        }
    }
    . (Join-Path $PSScriptRoot 'namespace-evidence-policy.ps1')
    Assert-NamespaceTestExecution $Evidence.tests $Catalog
}
