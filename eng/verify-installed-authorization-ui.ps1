[CmdletBinding()]
param(
    [Parameter(Mandatory)][int]$ProcessId,
    [Parameter(Mandatory)][ValidateSet('Create', 'Renew')][string]$Action
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$process = Get-Process -Id $ProcessId
try {
    if ($process.ProcessName -cne 'MirrorPulse.App' -or $process.MainWindowHandle -eq [IntPtr]::Zero) {
        throw 'The authorization fixture requires the installed MirrorPulse window.'
    }
    $window = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    if ($window.Current.ProcessId -ne $ProcessId) { throw 'The authorization window belongs to another process.' }
    $descendants = [System.Windows.Automation.TreeScope]::Descendants
    $elementType = [System.Windows.Automation.AutomationElement]

    function Wait-Control($Property, $Value, $Within = $window) {
        $condition = [System.Windows.Automation.PropertyCondition]::new($Property, $Value)
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        do {
            $control = $Within.FindFirst($descendants, $condition)
            if ($control -and $control.Current.IsEnabled) { return $control }
            Start-Sleep -Milliseconds 200
        } while ([DateTime]::UtcNow -lt $deadline)
        throw "The installed authorization form did not expose $Value."
    }

    function Find-Control([string]$Id) {
        Wait-Control ($elementType::AutomationIdProperty) $Id
    }

    function Invoke-Control([string]$Id) {
        (Find-Control $Id).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    }

    function Read-ValuePattern([string]$Id) {
        (Find-Control $Id).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    }

    function Wait-Status([string]$Name) {
        $null = Wait-Control ($elementType::NameProperty) $Name
    }

    $account = 'Synthetic desktop account'
    if ($Action -ceq 'Create') {
        Invoke-Control 'GetStarted'
        $list = Find-Control 'AdapterInstallations'
        $item = Wait-Control ($elementType::ControlTypeProperty) ([System.Windows.Automation.ControlType]::ListItem) $list
        $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Invoke-Control 'AdapterInstanceCreate'
        (Read-ValuePattern 'AdapterField.account').SetValue($account)
        (Read-ValuePattern 'AdapterInstanceName').SetValue('Authorization desktop fixture')

        Invoke-Control 'AdapterCallback.desktop-authorize.Start'
        $null = Find-Control 'AdapterCallback.desktop-authorize.Cancel'
        Invoke-Control 'AdapterInstanceSave'
        Wait-Status 'Finish or cancel the pending authorization before saving.'
        Invoke-Control 'AdapterCallback.desktop-authorize.Cancel'
        Wait-Status 'Authorization cancelled.'
    } else {
        Invoke-Control 'AdapterInstanceConfigure'
        if ((Read-ValuePattern 'AdapterField.account').Current.Value -cne $account) {
            throw 'The installed form did not restore the account after Host restart.'
        }
    }

    Invoke-Control 'AdapterCallback.desktop-authorize.Start'
    Invoke-Control 'AdapterCallback.desktop-authorize.Check'
    Wait-Status 'Authorization is ready. Save to apply it.'
    Invoke-Control 'AdapterInstanceSave'
    $null = Find-Control 'AdapterInstanceConfigure'
    Write-Output "Verified $Action through the installed authorization form."
} finally { $process.Dispose() }
