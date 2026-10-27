using System.Diagnostics;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed partial class NamespaceNonAdminControllerTests
{
    [TestMethod]
    [DataRow("allowed", 1)]
    [DataRow("allowed", 52)]
    [DataRow("allowed", 56)]
    [DataRow("allowed", 66)]
    [DataRow("allowed", 100)]
    [DataRow("allowed", 101)]
    [DataRow("allowed", 104)]
    [DataRow("allowed", 112)]
    [DataRow("rejected", 0)]
    [DataRow("rejected", 113)]
    [DataRow("rejected", int.MaxValue)]
    public Task ControllerBudgetCoversTheGrowingCatalogAndRejectsAnUnboundedSelection(string expectation, int count) =>
        RunAsync("budget-" + expectation, count);

    [TestMethod]
    [DataRow("NativeHostTransferRetainsSourceAndBothFencesAfterOriginalCopyAndWholeTargetVerification", 2)]
    [DataRow("NativeHostTransferDeletesOnlyOriginalRemoteSourceWhileRetainingGuardedLocalBytes", 2)]
    [DataRow("NativeHostTransferRetiresOriginalSourceThroughTheSdkAndKeepsPrivateBytesAndBothFences", 2)]
    [DataRow("NativeHostTransferSourceRetirementResumesOriginalNativeAndReceiptBoundariesAcrossStoreOwners", 10)]
    [DataRow("NativeHostTransferAcknowledgesOriginalClaimAndRecoversSdkReturnBeforeReceiptAcrossStoreOwners", 2)]
    public Task ControllerIncludesEverySelectedTransferScopeAndRetirementCutpoint(string method, int executions) =>
        RunAsync("bundle-" + method, executions);

    [TestMethod]
    [DataRow("mismatch")]
    [DataRow("duplicate")]
    [DataRow("empty")]
    [DataRow("malformed")]
    public Task ControllerRejectsSelectionsWhoseNamesCannotDescribeTheExecutedCatalog(string damage) => RunAsync("selection-" + damage, 109);

    [TestMethod]
    [DataRow("copy")]
    [DataRow("wrong-marker")]
    [DataRow("nested-evidence")]
    [DataRow("oversized")]
    public Task FailureCaptureRetainsOnlyBoundedOriginalDiagnosticsBeforeWorkspaceRemoval(string scenario) => RunAsync(scenario);

    private static async Task RunAsync(string scenario, int count = 0)
    {
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-controller-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string script = Path.Combine(directory, "verify.ps1");
            await File.WriteAllTextAsync(script, """
                param([string]$Policy,[string]$Directory,[string]$Scenario,[int]$Count)
                $ErrorActionPreference='Stop'
                . $Policy
                $legacyInitializer='MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseWindowsNamespacePermissionLeaseTests.NativePublicCancellationRetainsAndResumesItsOriginalPartiallyRotatedInitializer'
                if ($Scenario -like 'legacy-initializer-*') {
                    $catalog=Get-Content -Raw (Join-Path (Split-Path $Policy -Parent) 'test-suites.json') | ConvertFrom-Json
                    $names=@($catalog.required.namespace)
                    if ($Count -ne 123 -or $names.Count -ne $Count -or @($names|Where-Object {$_ -ceq $legacyInitializer}).Count -ne 1) {throw 'Require the complete explicit original-initializer cancellation catalog.'}
                    if ($Scenario -eq 'legacy-initializer-selected') {
                        $prior=@($names|Where-Object {$_ -cne $legacyInitializer})
                        if ((Get-OrdinaryUserNamespaceBudgetMilliseconds -TestCount 122 -TestNames $prior) -ne 4635000) {throw 'Every original deadline must remain exact.'}
                        if ((Get-OrdinaryUserNamespaceBudgetMilliseconds -TestCount $Count -TestNames $names) -ne 4755000+4*60000) {throw 'Retain both original executions and add only the four Prepared/Applied active/disabled60-second executions.'}
                        if ((Get-OrdinaryUserNamespaceStepTimeoutMinutes -TestCount $Count -TestNames $names) -ne 89) {throw 'Preserve the five-minute diagnostic/profile cleanup margin.'}
                    } else {
                        $parameters=@{TestCount=$Count;TestNames=$names}
                        switch ($Scenario) {
                            'legacy-initializer-missing' {$parameters.TestNames=@($names|ForEach-Object {if($_ -ceq $legacyInitializer){'Fixture.Unrelated'}else{$_}})}
                            'legacy-initializer-duplicate' {$parameters.TestNames=@($names|ForEach-Object {if($_ -ceq $legacyInitializer){$names[0]}else{$_}})}
                            'legacy-initializer-count-only' {$parameters.Remove('TestNames')}
                            default {throw 'Unknown explicit initializer selection test.'}
                        }
                        $rejected=$false
                        try {Get-OrdinaryUserNamespaceBudgetMilliseconds @parameters|Out-Null} catch {$rejected=$true}
                        if (-not $rejected) {throw 'An added slot cannot replace the exact original initializer method or omit its selected catalog.'}
                    }
                    exit 0
                }
                $unsealed='MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseWindowsNamespacePermissionLeaseTests.NativePublicRuntimeCancelsOriginalUnsealedRootPreparationsAfterIndependentRoleDisposal'
                if ($Scenario -like 'unsealed-cancellation-*') {
                    $catalog=Get-Content -Raw (Join-Path (Split-Path $Policy -Parent) 'test-suites.json') | ConvertFrom-Json
                    $names=@($catalog.required.namespace | Where-Object {$_ -cne $legacyInitializer})
                    if ($Count -ne 122 -or $names.Count -ne $Count -or @($names|Where-Object {$_ -ceq $unsealed}).Count -ne 1) {throw 'Require the complete explicit unsealed cancellation catalog.'}
                    if ($Scenario -eq 'unsealed-cancellation-selected') {
                        $legacy=@($names|Where-Object {$_ -cne $unsealed})
                        $prior=Get-OrdinaryUserNamespaceBudgetMilliseconds -TestCount 121 -TestNames $legacy
                        $actual=Get-OrdinaryUserNamespaceBudgetMilliseconds -TestCount $Count -TestNames $names
                        if ($prior -ne 4515000 -or $actual -ne $prior+2*60000) {throw 'Retain every prior deadline and both actual unsealed cancellation executions.'}
                        if ((Get-OrdinaryUserNamespaceStepTimeoutMinutes -TestCount $Count -TestNames $names) -ne 83) {throw 'Retain the five-minute diagnostic/profile cleanup margin.'}
                    } else {
                        $names=@($names|ForEach-Object {if($_ -ceq $unsealed){'Fixture.Unrelated'}else{$_}})
                        $rejected=$false
                        try {Get-OrdinaryUserNamespaceBudgetMilliseconds -TestCount $Count -TestNames $names|Out-Null} catch {$rejected=$true}
                        if (-not $rejected) {throw 'The added slot cannot substitute an unrelated method.'}
                    }
                    exit 0
                }
                if ($Scenario -like 'root-cancellation-*') {
                    $catalog=Get-Content -Raw (Join-Path (Split-Path $Policy -Parent) 'test-suites.json') | ConvertFrom-Json
                    $names=@($catalog.required.namespace | Where-Object {$_ -cne $legacyInitializer} | Where-Object {$_ -cne $unsealed})
                    $cancel='MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseWindowsNamespacePermissionLeaseTests.NativePublicRuntimeCancelsOnlyUnstartedOriginalRootLabelsAcrossRolesAndRetainsLaterNames'
                    if ($Count -ne 121 -or $names.Count -ne $Count -or @($names|Where-Object {$_ -ceq $cancel}).Count -ne 1) {throw 'Require the complete explicit cancellation catalog.'}
                    if ($Scenario -eq 'root-cancellation-selected') {
                        $legacy=@($names|Where-Object {$_ -cne $cancel})
                        $prior=Get-OrdinaryUserNamespaceBudgetMilliseconds -TestCount 120 -TestNames $legacy
                        $actual=Get-OrdinaryUserNamespaceBudgetMilliseconds -TestCount $Count -TestNames $names
                        if ($prior -ne 4155000 -or $actual -ne $prior+6*60000) {throw 'Keep all prior native deadlines and count each of the six actual cancellation/refusal executions.'}
                        if ((Get-OrdinaryUserNamespaceStepTimeoutMinutes -TestCount $Count -TestNames $names) -ne 81) {throw 'Preserve the five-minute diagnostic/profile cleanup margin.'}
                    } else {
                        $names=@($names|ForEach-Object {if($_ -ceq $cancel){'Fixture.Unrelated'}else{$_}})
                        $rejected=$false
                        try {Get-OrdinaryUserNamespaceBudgetMilliseconds -TestCount $Count -TestNames $names|Out-Null} catch {$rejected=$true}
                        if (-not $rejected) {throw 'The added slot cannot substitute an unrelated method.'}
                    }
                    exit 0
                }
                if ($Scenario -like 'root-public-*') {
                    $catalog=Get-Content -Raw (Join-Path (Split-Path $Policy -Parent) 'test-suites.json') | ConvertFrom-Json
                    $names=@($catalog.required.namespace | Where-Object {$_ -cne $legacyInitializer} | Where-Object {$_ -cne $unsealed} | Where-Object {$_ -cne 'MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseWindowsNamespacePermissionLeaseTests.NativePublicRuntimeCancelsOnlyUnstartedOriginalRootLabelsAcrossRolesAndRetainsLaterNames'})
                    $public='MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseWindowsNamespacePermissionLeaseTests.NativePublicRuntimeExecutesOnlyOriginalProtectedRootLabelsAndAuditsCurrentReadmission'
                    if ($Count -ne 120 -or $names.Count -ne $Count -or @($names|Where-Object {$_ -ceq $public}).Count -ne 1) {throw 'Require the exact additive public runtime catalog.'}
                    if ($Scenario -eq 'root-public-selected') {
                        $actual=Get-OrdinaryUserNamespaceBudgetMilliseconds -TestCount $Count -TestNames $names
                        if ($actual -ne 3915000+4*60000) {throw 'All four active/disabled and case-only public runtime executions retain sixty-second deadlines.'}
                        if ((Get-OrdinaryUserNamespaceStepTimeoutMinutes -TestCount $Count -TestNames $names) -ne 75) {throw 'Retain diagnostics and disposable-user cleanup after the bounded child.'}
                    } else {
                        $names=@($names|ForEach-Object {if($_ -ceq $public){'Fixture.Unrelated'}else{$_}})
                        $rejected=$false
                        try {Get-OrdinaryUserNamespaceBudgetMilliseconds -TestCount $Count -TestNames $names|Out-Null} catch {$rejected=$true}
                        if (-not $rejected) {throw 'The expanded slot must require the actual public runtime method.'}
                    }
                    exit 0
                }
                if ($Scenario -like 'root-admission-*') {
                    $catalog=Get-Content -Raw (Join-Path (Split-Path $Policy -Parent) 'test-suites.json') | ConvertFrom-Json
                    $names=@($catalog.required.namespace | Where-Object {$_ -cne $legacyInitializer} | Where-Object {$_ -cne $unsealed} | Where-Object {$_ -cne 'MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseWindowsNamespacePermissionLeaseTests.NativePublicRuntimeCancelsOnlyUnstartedOriginalRootLabelsAcrossRolesAndRetainsLaterNames'} | Where-Object {$_ -cne 'MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseWindowsNamespacePermissionLeaseTests.NativePublicRuntimeExecutesOnlyOriginalProtectedRootLabelsAndAuditsCurrentReadmission'})
                    $admission='MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseWindowsNamespacePermissionLeaseTests.NativeRootLabelJournalAdmissionRequiresCurrentTreeAndActualSdkReturnBeforeReadmission'
                    if ($Count -ne 119 -or $names.Count -ne $Count -or @($names|Where-Object {$_ -ceq $admission}).Count -ne 1) {throw 'Require the exact additive admission catalog.'}
                    if ($Scenario -eq 'root-admission-selected') {
                        $actual=Get-OrdinaryUserNamespaceBudgetMilliseconds -TestCount $Count -TestNames $names
                        if ($actual -ne 3735000+6*30000) {throw 'All six enabled/disabled ACK cuts retain their existing thirty-second deadlines.'}
                        if ((Get-OrdinaryUserNamespaceStepTimeoutMinutes -TestCount $Count -TestNames $names) -ne 71) {throw 'Preserve diagnostics and disposable-user cleanup after the actual bounded child budget.'}
                    } else {
                        $names=@($names|ForEach-Object {if($_ -ceq $admission){'Fixture.Unrelated'}else{$_}})
                        $rejected=$false
                        try {Get-OrdinaryUserNamespaceBudgetMilliseconds -TestCount $Count -TestNames $names|Out-Null} catch {$rejected=$true}
                        if (-not $rejected) {throw 'The expanded slot must require the actual root admission method.'}
                    }
                    exit 0
                }
                if ($Scenario -like 'root-runtime-*') {
                    $names=@(1..112|ForEach-Object {'Fixture.Case'+$_})
                    $names+='MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseManagedRootNamespaceTests.NativeRootRenamePreparationCutsRetainOnlyCompleteOriginalProductProofAcrossOwners'
                    $names+='MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseManagedRootNamespaceTests.NativeDefaultProviderApprovesOnlyTheCurrentOriginalRootRenameAndRetainsRecoveryAcrossOwners'
                    $names+='MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseWindowsNamespacePermissionLeaseTests.NativeRootLabelPreparationSealsOnlyItsOriginalProtectedSubtreeAcrossOwners'
                    $names+='MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseWindowsNamespacePermissionLeaseTests.NativeProtectedRootLabelExecutionRecoversOriginalSubtreeAndCaseOnlyHistoryAcrossOwners'
                    $names+='MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseWindowsNamespacePermissionLeaseTests.NativeObservedRootLabelRecoverySettlesOnlyOriginalMovedMembersUnderANewRoleAcrossOwners'
                    if ($Scenario -eq 'root-runtime-selected') {
                        $names+='MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseWindowsNamespacePermissionLeaseTests.NativeRuntimeSettlesOriginalObservedRootLabelBeforeNewRoleStartupAudit'
                        $actual=Get-OrdinaryUserNamespaceBudgetMilliseconds -TestCount 118 -TestNames $names
                        if ($actual -ne 120000+112*15000+28*30000+6*60000) {throw 'Retain all original matrix deadlines and six actual sixty-second runtime startup executions.'}
                    } else {
                        $names+='Fixture.Unrelated';$rejected=$false
                        try {Get-OrdinaryUserNamespaceBudgetMilliseconds -TestCount 118 -TestNames $names|Out-Null} catch {$rejected=$true}
                        if (-not $rejected) {throw 'The expanded slot did not require the actual runtime startup matrix.'}
                    }
                    exit 0
                }

                if ($Scenario -like 'root-observed-*') {
                    $names=@(1..112|ForEach-Object {'Fixture.Case'+$_})
                    $names+='MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseManagedRootNamespaceTests.NativeRootRenamePreparationCutsRetainOnlyCompleteOriginalProductProofAcrossOwners'
                    $names+='MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseManagedRootNamespaceTests.NativeDefaultProviderApprovesOnlyTheCurrentOriginalRootRenameAndRetainsRecoveryAcrossOwners'
                    $names+='MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseWindowsNamespacePermissionLeaseTests.NativeRootLabelPreparationSealsOnlyItsOriginalProtectedSubtreeAcrossOwners'
                    $names+='MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseWindowsNamespacePermissionLeaseTests.NativeProtectedRootLabelExecutionRecoversOriginalSubtreeAndCaseOnlyHistoryAcrossOwners'
                    if ($Scenario -eq 'root-observed-selected') {
                        $names+='MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseWindowsNamespacePermissionLeaseTests.NativeObservedRootLabelRecoverySettlesOnlyOriginalMovedMembersUnderANewRoleAcrossOwners'
                        $actual=Get-OrdinaryUserNamespaceBudgetMilliseconds -TestCount 117 -TestNames $names
                        if ($actual -ne 120000+112*15000+28*30000) {throw 'Retain all existing pairs and fourteen executions plus eight actual observed-recovery executions.'}
                    } else {
                        $names+='Fixture.Unrelated';$rejected=$false
                        try {Get-OrdinaryUserNamespaceBudgetMilliseconds -TestCount 117 -TestNames $names|Out-Null} catch {$rejected=$true}
                        if (-not $rejected) {throw 'The expanded slot did not require the actual new-role observed-recovery matrix.'}
                    }
                    exit 0
                }
                if ($Scenario -like 'root-execution-*') {
                    $names=@(1..112|ForEach-Object {'Fixture.Case'+$_})
                    $names+='MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseManagedRootNamespaceTests.NativeRootRenamePreparationCutsRetainOnlyCompleteOriginalProductProofAcrossOwners'
                    $names+='MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseManagedRootNamespaceTests.NativeDefaultProviderApprovesOnlyTheCurrentOriginalRootRenameAndRetainsRecoveryAcrossOwners'
                    $names+='MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseWindowsNamespacePermissionLeaseTests.NativeRootLabelPreparationSealsOnlyItsOriginalProtectedSubtreeAcrossOwners'
                    if ($Scenario -eq 'root-execution-selected') {
                        $names+='MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseWindowsNamespacePermissionLeaseTests.NativeProtectedRootLabelExecutionRecoversOriginalSubtreeAndCaseOnlyHistoryAcrossOwners'
                        $actual=Get-OrdinaryUserNamespaceBudgetMilliseconds -TestCount 116 -TestNames $names
                        if ($actual -ne 120000+112*15000+20*30000) {throw 'Retain all original preparation pairs and fourteen actual execution state/fault deadlines.'}
                    } else {
                        $names+='Fixture.Unrelated';$rejected=$false
                        try {Get-OrdinaryUserNamespaceBudgetMilliseconds -TestCount 116 -TestNames $names|Out-Null} catch {$rejected=$true}
                        if (-not $rejected) {throw 'The expanded slot did not require the actual root execution matrix.'}
                    }
                    exit 0
                }
                if ($Scenario -like 'root-label-*') {
                    $names=@(1..112|ForEach-Object {'Fixture.Case'+$_})
                    $names+='MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseManagedRootNamespaceTests.NativeRootRenamePreparationCutsRetainOnlyCompleteOriginalProductProofAcrossOwners'
                    $names+='MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseManagedRootNamespaceTests.NativeDefaultProviderApprovesOnlyTheCurrentOriginalRootRenameAndRetainsRecoveryAcrossOwners'
                    if ($Scenario -eq 'root-label-selected') {
                        $names+='MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseWindowsNamespacePermissionLeaseTests.NativeRootLabelPreparationSealsOnlyItsOriginalProtectedSubtreeAcrossOwners'
                        $actual=Get-OrdinaryUserNamespaceBudgetMilliseconds -TestCount 115 -TestNames $names
                        if ($actual -ne 120000+112*15000+6*30000) {throw 'All three actual preparation/approval pairs must retain both thirty-second state executions.'}
                    } else {
                        $names+='Fixture.Unrelated';$rejected=$false
                        try {Get-OrdinaryUserNamespaceBudgetMilliseconds -TestCount 115 -TestNames $names|Out-Null} catch {$rejected=$true}
                        if (-not $rejected) {throw 'The expanded slot did not require the actual protected-root preparation composite.'}
                    }
                    exit 0
                }
                if ($Scenario -like 'approval-*') {
                    $names=@(1..112|ForEach-Object {'Fixture.Case'+$_})
                    $names+='MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseManagedRootNamespaceTests.NativeRootRenamePreparationCutsRetainOnlyCompleteOriginalProductProofAcrossOwners'
                    if ($Scenario -eq 'approval-selected') {
                        $names+='MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseManagedRootNamespaceTests.NativeDefaultProviderApprovesOnlyTheCurrentOriginalRootRenameAndRetainsRecoveryAcrossOwners'
                        $actual=Get-OrdinaryUserNamespaceBudgetMilliseconds -TestCount 114 -TestNames $names
                        if ($actual -ne 120000+112*15000+4*30000) {throw 'Both approval and preparation must retain both thirty-second availability executions.'}
                    } else {
                        $names+='Fixture.Unrelated';$rejected=$false
                        try {Get-OrdinaryUserNamespaceBudgetMilliseconds -TestCount 114 -TestNames $names|Out-Null} catch {$rejected=$true}
                        if (-not $rejected) {throw 'The second expanded catalog slot did not require the actual new callback test.'}
                    }
                    exit 0
                }
                if ($Scenario -like 'preparation-*') {
                    $names=@(for ($index=1;$index -lt $Count;$index++) { 'Fixture.Case'+$index })
                    $names+='MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseManagedRootNamespaceTests.NativeRootRenamePreparationCutsRetainOnlyCompleteOriginalProductProofAcrossOwners'
                    if ($Scenario -eq 'preparation-missing') {
                        $names[-1]='Fixture.Unrelated';$rejected=$false
                        try {Get-OrdinaryUserNamespaceBudgetMilliseconds -TestCount $Count -TestNames $names|Out-Null} catch {$rejected=$true}
                        if (-not $rejected) {throw 'The expanded selection did not require the actual new preparation case.'}
                    } else {
                        $actual=Get-OrdinaryUserNamespaceBudgetMilliseconds -TestCount $Count -TestNames $names
                        $expected=[Math]::Max(600000,120000+($Count-1)*15000+2*30000)
                        if ($actual -ne $expected) {throw 'Both actual preparation availability states must retain their original thirty-second deadlines.'}
                    }
                    exit 0
                }
                if ($Scenario -like 'bundle-*') {
                    $names=@(1..108 | ForEach-Object { 'Fixture.Case' + $_ })
                    $names+='MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseWindowsNamespacePermissionLeaseTests.'+$Scenario.Substring(7)
                    $old=Get-OrdinaryUserNamespaceBudgetMilliseconds -TestCount 109
                    $actual=Get-OrdinaryUserNamespaceBudgetMilliseconds -TestCount 109 -TestNames $names
                    if ($actual -ne $old-15000+$Count*45000) { throw 'A selected transfer execution or cutpoint lost its existing 45-second native deadline.' }
                    exit 0
                }
                if ($Scenario -like 'selection-*') {
                    $names=@(1..$Count | ForEach-Object { 'Fixture.Case' + $_ })
                    switch ($Scenario) {
                        'selection-mismatch' { $names=$names[1..($Count-1)] }
                        'selection-duplicate' { $names[1]=$names[0] }
                        'selection-empty' { $names[0]='' }
                        'selection-malformed' { $names[0]="Fixture.`nCase" }
                    }
                    $rejected=$false
                    try { Get-OrdinaryUserNamespaceBudgetMilliseconds -TestCount $Count -TestNames $names | Out-Null }
                    catch { $rejected=$true }
                    if (-not $rejected) { throw 'An invalid catalog selection was accepted.' }
                    exit 0
                }
                if ($Scenario -like 'budget-*') {
                    $rejected=$false
                    try {
                        $parameters=@{TestCount=$Count}
                        if ($Scenario -eq 'budget-catalog') {
                            $catalog=Get-Content -Raw (Join-Path (Split-Path $Policy -Parent) 'test-suites.json') | ConvertFrom-Json
                            $parameters.TestNames=@($catalog.required.namespace)
                        }
                        $budget=Get-OrdinaryUserNamespaceBudgetMilliseconds @parameters
                        $stepMinutes=Get-OrdinaryUserNamespaceStepTimeoutMinutes @parameters
                    }
                    catch { $rejected=$true }
                    if ($Scenario -eq 'budget-rejected') {
                        if (-not $rejected) { throw 'An invalid or unbounded selection was accepted.' }
                    } else {
                        $maximum=if ($Scenario -eq 'budget-catalog') {4755000+4*60000} else {1800000}
                        if ($rejected -or $budget -lt 600000 -or $budget -gt $maximum) { throw 'The whole-suite envelope is missing or unbounded.' }
                        if ($Scenario -eq 'budget-catalog' -and $budget -ne 120000+($Count+51)*15000+18*30000+28*15000+6*45000+6*15000+4*45000+5*15000+6*45000+2*45000+2*45000+4*60000) {
                            throw 'The actual catalog lost a same/cross-instance transfer or one of its five retirement cutpoints.'
                        }
                        if ($Count -ge 52 -and $budget -le 600000) { throw 'The growing suite still has the obsolete fixed ten-minute envelope.' }
                        if ($Count -eq 56 -and $budget -le (Get-OrdinaryUserNamespaceBudgetMilliseconds -TestCount 52)) {
                            throw 'New required cases did not receive execution budget.'
                        }
                        $maximumMinutes=[int][Math]::Ceiling($maximum/60000.0)+5
                        if ($stepMinutes -lt 15 -or $stepMinutes -gt $maximumMinutes -or
                            $stepMinutes -ne [int][Math]::Ceiling($budget/60000.0)+5 -or $stepMinutes*60000 - $budget -lt 300000) {
                            throw 'The workflow can terminate the controller before its child timeout and diagnostic cleanup.'
                        }
                    }
                    exit 0
                }
                $id=[guid]::NewGuid().ToString('N')
                $workspace=Join-Path $Directory $id
                $evidence=Join-Path $Directory 'evidence'
                New-Item -ItemType Directory -Path (Join-Path $workspace 'work/results') -Force | Out-Null
                [IO.File]::WriteAllText((Join-Path $workspace '.mp-namespace-ci'),$id)
                [IO.File]::WriteAllText((Join-Path $workspace 'work/context.json'),'original-context')
                [IO.File]::WriteAllText((Join-Path $workspace 'work/results/namespace.trx'),'original-trx')
                [IO.File]::WriteAllText((Join-Path $workspace 'stdout.log'),'original-stdout')
                [IO.File]::WriteAllText((Join-Path $workspace 'stderr.log'),'original-stderr')
                [IO.File]::WriteAllText((Join-Path $workspace 'configuration.json'),'synthetic-secret-must-not-be-copied')
                if ($Scenario -eq 'wrong-marker') { [IO.File]::WriteAllText((Join-Path $workspace '.mp-namespace-ci'),'changed') }
                if ($Scenario -eq 'nested-evidence') { $evidence=Join-Path $workspace 'must-not-be-created' }
                if ($Scenario -eq 'oversized') {
                    $large=[IO.File]::OpenWrite((Join-Path $workspace 'work/results/namespace.trx'))
                    try { $large.SetLength(10MB+1) } finally { $large.Dispose() }
                }
                $failed=$false
                try { Copy-OrdinaryUserNamespaceFailureEvidence -Workspace $workspace -Identifier $id -EvidenceDirectory $evidence }
                catch { $failed=$true }
                if ($Scenario -ne 'copy') {
                    if (-not $failed) { throw 'Invalid evidence input was accepted.' }
                    if (Test-Path -LiteralPath (Join-Path $evidence 'namespace.trx')) { throw 'Invalid or unbounded evidence was copied.' }
                    exit 0
                }
                if ($failed) { throw 'Valid marked failure evidence was rejected.' }
                $expected=@{'context.json'='original-context';'namespace.trx'='original-trx';'child-stdout.log'='original-stdout';'child-stderr.log'='original-stderr'}
                if (@(Get-ChildItem -LiteralPath $evidence -File).Count -ne $expected.Count) { throw 'Failure capture copied an unexpected file.' }
                foreach ($name in $expected.Keys) {
                    if ([IO.File]::ReadAllText((Join-Path $evidence $name)) -cne $expected[$name]) { throw 'An original diagnostic changed during capture.' }
                }
                """);
            var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in new[] { "-NoProfile", "-File", script, "-Policy",
                Path.Combine(SftpProtocolFixture.FindRepositoryRoot(), "eng", "namespace-nonadmin-controller.ps1"),
                "-Directory", directory, "-Scenario", scenario, "-Count", count.ToString(System.Globalization.CultureInfo.InvariantCulture) })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            Task<string> output = process.StandardOutput.ReadToEndAsync(); Task<string> error = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
            catch { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } throw; }
            Assert.AreEqual(0, process.ExitCode, await output + await error);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public async Task ControllerAdmitsTheActualRequiredNamespaceCatalogWithinTheExistingChildBound()
    {
        string catalog = await File.ReadAllTextAsync(Path.Combine(SftpProtocolFixture.FindRepositoryRoot(), "eng", "test-suites.json"));
        using var document = System.Text.Json.JsonDocument.Parse(catalog);
        int count = document.RootElement.GetProperty("required").GetProperty("namespace").GetArrayLength();
        Assert.IsGreaterThan(0, count);
        await RunAsync("budget-catalog", count);
    }

    [TestMethod]
    public async Task WorkflowUsesTheControllerEnvelopeBeforeLaunchingTheDisposableUser()
    {
        string workflow = await File.ReadAllTextAsync(Path.Combine(SftpProtocolFixture.FindRepositoryRoot(), ".github", "workflows", "ci.yml"));
        StringAssert.Contains(workflow, "Get-OrdinaryUserNamespaceStepTimeoutMinutes -TestCount @($catalog.required.namespace).Count");
        StringAssert.Contains(workflow, "timeout-minutes: ${{ fromJSON(steps.namespace-nonadmin-budget.outputs.timeout_minutes) }}");
        Assert.IsLessThan(workflow.IndexOf("Verify strict Cloud Files ACLs as a fresh non-administrator user", StringComparison.Ordinal),
            workflow.IndexOf("id: namespace-nonadmin-budget", StringComparison.Ordinal));
    }
}
