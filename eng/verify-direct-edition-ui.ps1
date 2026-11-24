[CmdletBinding()]
param(
    [Parameter(Mandatory)][int]$ProcessId,
    [Parameter(Mandatory)][string]$SourcePath,
    [Parameter(Mandatory)][string]$RootKey,
    [Parameter(Mandatory)][string]$RootLabel,
    [Parameter(Mandatory)][string]$InstanceName
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$process = Get-Process -Id $ProcessId
try {
    if ($process.ProcessName -cne 'MirrorPulse.App' -or $process.MainWindowHandle -eq [IntPtr]::Zero) {
        throw 'The direct edition UI probe requires the activated MirrorPulse window.'
    }
    $window = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    if ($window.Current.ProcessId -ne $ProcessId) { throw 'The UI window belongs to another process.' }

    function Find-Control([string]$AutomationId) {
        $condition = [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $AutomationId)
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        do {
            $control = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
            if ($control -and $control.Current.IsEnabled) { return $control }
            Start-Sleep -Milliseconds 200
        } while ([DateTime]::UtcNow -lt $deadline)
        throw "The installed direct edition did not expose the enabled control $AutomationId."
    }

    function Read-ValuePattern([string]$AutomationId) {
        (Find-Control $AutomationId).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    }

    function Invoke-Control([string]$AutomationId) {
        (Find-Control $AutomationId).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    }

    $fields = [ordered]@{
        'AdapterField.sourceDirectory' = $SourcePath
        'AdapterInstanceName' = $InstanceName
        "AdapterRoot.$RootKey" = $RootLabel
    }
    foreach ($field in $fields.GetEnumerator()) {
        (Read-ValuePattern $field.Key).SetValue($field.Value)
    }
    Invoke-Control 'AdapterInstanceSave'
    Invoke-Control 'DirectConfigureStorage'
    foreach ($field in $fields.GetEnumerator()) {
        if ((Read-ValuePattern $field.Key).Current.Value -cne $field.Value) {
            throw "The installed form did not restore the saved value for $($field.Key)."
        }
    }
    Invoke-Control 'AdapterInstanceCancel'
    $null = Find-Control 'DirectConfigureStorage'
    Write-Output 'Verified first configuration, save, direct status and form readback through the installed UI.'
} finally { $process.Dispose() }
