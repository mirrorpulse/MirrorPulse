[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RegistrationId,
    [Parameter(Mandatory)][string]$ExpectedPath,
    [Parameter(Mandatory)][guid]$ExpectedProviderId,
    [switch]$Unregister
)

$ErrorActionPreference = 'Stop'
[void][Windows.Storage.Provider.StorageProviderSyncRootManager, Windows.Storage.Provider, ContentType=WindowsRuntime]
$registered = @([Windows.Storage.Provider.StorageProviderSyncRootManager]::GetCurrentSyncRoots() |
    Where-Object Id -CEQ $RegistrationId)
if ($registered.Count -eq 0) {
    'Absent'
    return
}
if ($registered.Count -ne 1 -or $registered[0].ProviderId -ne $ExpectedProviderId -or
    $registered[0].Path.Path -ine $ExpectedPath) {
    throw 'The selected Shell registration belongs to a different provider or path.'
}

if ($Unregister) {
    [Windows.Storage.Provider.StorageProviderSyncRootManager]::Unregister($RegistrationId)
    if ([Windows.Storage.Provider.StorageProviderSyncRootManager]::GetCurrentSyncRoots() |
        Where-Object Id -CEQ $RegistrationId) {
        throw 'The test Shell root remained registered.'
    }
    'Absent'
} else {
    'Present'
}
