using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class DeferredP079GateTests
{
    [TestMethod]
    public async Task S06BudgetExcludesOnlyTheNineDeferredTransferExecutionsAndRetainsLegacyAndOtherScopes()
    {
        string repository = SftpProtocolFixture.FindRepositoryRoot();
        string script = """
            param([string]$Repository)
            $ErrorActionPreference='Stop'
            . (Join-Path $Repository 'eng/namespace-nonadmin-controller.ps1')
            $catalog=Get-Content (Join-Path $Repository 'eng/test-suites.json') -Raw|ConvertFrom-Json
            # The new directory cohort owns a separate exact fourteen-execution budget.
            $directoryPrefix='MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseWindowsNamespacePermissionLeaseTests.'
            $directories=@('NativeDirectoryConfirmationRetainsOriginalBindingAndTypedIdentityAcrossOwners',
                'NativeProductionDirectoryGroupSettlesOriginalCreateMoveOrRetirementAcrossOwners',
                'NativeProductionDirectoryGroupRereadsRealLateChildOriginalsBeforeDispatchAcrossOwners',
                'NativeProductionDirectoryAbsencePreservesALaterBackendFileOrDirectoryAcrossOwners',
                'NativeProductionDirectoryGroupRecoversAcceptedEffectsBeforeOriginalAckAcrossFreshRoles')|ForEach-Object {$directoryPrefix+$_}
            $catalog.required.namespace=@($catalog.required.namespace|Where-Object {$_ -cnotin $directories})
            $catalog.required.native=@($catalog.required.native|Where-Object {$_ -cnotin $directories})
            # Preserve the historical selection below; the new explicit-rescan boundary has its own exact gate.
            $catalog.required.namespace=@($catalog.required.namespace|Where-Object {$_ -cne 'MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseJournalUploadSourceTests.NativeProductionPumpSettlesOriginalJournalBeforeAnExplicitRescanAcrossOwners'})
            $catalog.required.native=@($catalog.required.native|Where-Object {$_ -cne 'MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseJournalUploadSourceTests.NativeProductionPumpSettlesOriginalJournalBeforeAnExplicitRescanAcrossOwners'})
            $catalog.allowSkipped=@($catalog.allowSkipped|Where-Object {$_ -cne 'MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseJournalUploadSourceTests.NativeProductionPumpSettlesOriginalJournalBeforeAnExplicitRescanAcrossOwners'})
            $keepRemotePrefix='MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseWindowsNamespacePermissionLeaseTests.'
            # Keep this original transfer/deferred-scope budget unchanged. The new
            # received-Delete cohort has its own exact selection/row/deadline gate.
            $receivedDelete=@(
                'NativeMissingOriginalRemoteDeleteRetainsActualSdkConflictWithoutInventingMemberRetirementAcrossOwners',
                'NativeMiddleMissingRemoteDeleteSettlesOriginalRejectionBeforeLaterSdkEntryAndCursorAcrossOwners',
                'NativeMissingRemoteDeleteRecoversOriginalAdmissionBeforeSdkAcrossFreshRoles',
                'NativeMissingRemoteDeleteRecoversActualSdkConflictBeforeProductResultAcrossFreshRoles',
                'NativeDefaultRuntimeRecoversReceivedMissingDeleteBeforeNewRoleAuditWithoutAcknowledgingLocalContentOriginals',
                'NativeDefaultRuntimePublishesActualMissingDeleteConflictAndCursorAcrossFreshRolesWithoutChangingLocalOriginals',
                'NativeCfSharpPreservingDeleteCharacterizesItsActualLocalTombstoneBeforeAndAfterConsumerAcknowledgement',
                'NativeLocalTombstoneDeleteReconciliationPublishesOnlyItsOriginalConflictAcrossOwners',
                'NativeLocalTombstoneDeleteRecoversOriginalAdmissionAcrossFreshRoles',
                'NativeLocalTombstoneDeleteRecoversActualSdkReceiptAcrossFreshRoles'
            )|ForEach-Object {$keepRemotePrefix+$_}
            $catalog.required.namespace=@($catalog.required.namespace|Where-Object {$_ -cnotin $receivedDelete})
            $catalog.required.native=@($catalog.required.native|Where-Object {$_ -cnotin $receivedDelete})
            $keepRemote=@('NativeKeepRemoteFirstSealRequiresTheOriginalFileAndJournalAndReplaysWithoutWriting',
                'NativeKeepRemoteResidentByteCopyRetainsPlaceholderAndOriginalJournal',
                'NativeKeepRemoteResidentWriterKeepsItsWriteAndCompletionFactsAcrossRetry',
                'NativeKeepRemoteResidentWriterPreservesChangedLocalBytesBeforeItsFirstWrite',
                'NativeKeepRemoteConfirmationRetainsOriginalAttemptsAndRecoversLostSdkReturns',
                'NativeKeepRemoteAcknowledgementRecoversLostSdkReturnAndPreservesLaterEdit',
                'NativeKeepRemoteIndependentRuntimeSettlesOriginalCommandBeforeReadmission',
                'NativeKeepRemoteDefaultCommandResumesFirstPermissionIntentBeforeRootAudit',
                'NativeKeepRemoteCurrentRoleRestoresItsRegisteredInheritedDescriptor',
                'NativeKeepRemoteAfterAcknowledgedMoveRetainsItsPrefixAndSettlesTheOriginalJournal',
                'NativeKeepRemoteControlledCreatePreservesItsAbsentBaselineAndAllOriginalJournalEffects',
                'NativeKeepRemoteNewRoleResumesOriginalWriteAndConfirmsSelectedBytes',
                'NativeKeepRemoteRestorationRetainsActualAccessResultsAndPreservesOrdinaryLaterEdits',
                'NativeKeepRemoteRoleHandoffRecoversLostReadbackWithoutReapplyingContent',
                'NativeKeepRemoteUploadPumpResumesAcceptedCommandAfterRootAudit',
                'NativeKeepRemoteCfSharpAccessOnlyPreservesOriginalJournalAndRealSameLengthWrite')|ForEach-Object {$keepRemotePrefix+$_}
            $disabledCreation=$keepRemotePrefix+'NativeDisabledRootControlledCreationRetainsFilesDirectoriesAndOriginalsAcrossOwnersWithoutDispatch'
            $disabledCreationRole='MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseWindowsNamespacePermissionLeaseTests.NativeDisabledRootControlledCreationRetainsFilesDirectoriesAndOriginalsAcrossFreshRolesWithoutDispatch'
            $disabledMove='MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseWindowsNamespacePermissionLeaseTests.NativeDisabledRootControlledMoveRetainsFilesDirectoriesAndOriginalsAcrossFreshRolesWithoutDispatch'
            $disabledRetirement='MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseWindowsNamespacePermissionLeaseTests.NativeDisabledRootControlledRetirementRetainsOriginalJournalAcrossFreshRolesWithoutDispatch'
            $disabledReplacement='MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseWindowsNamespacePermissionLeaseTests.NativeDisabledRootControlledReplacementRetainsBytesBackupAndJournalAcrossFreshRolesWithoutDispatch'
            $residentReplacement=$keepRemotePrefix+'NativeResidentInSyncReplacementRetainsOriginalBytesJournalAndDirtyContentWithoutSourceCalls'
            $names=@($catalog.required.namespace | Where-Object {$_ -cnotin $keepRemote -and $_ -cne $disabledCreation -and $_ -cne $disabledCreationRole -and $_ -cne $disabledMove -and $_ -cne $disabledRetirement -and $_ -cne $disabledReplacement -and $_ -cne $residentReplacement})
            if($names.Count -ne 125 -or @($catalog.required.native|Where-Object {$_ -cnotin $keepRemote -and $_ -cne $disabledCreation -and $_ -cne $disabledCreationRole -and $_ -cne $disabledMove -and $_ -cne $disabledRetirement -and $_ -cne $disabledReplacement -and $_ -cne $residentReplacement}).Count -ne 150 -or @($catalog.required.deferredP079).Count -ne 5){throw 'Required scope names changed.'}
            if((Get-OrdinaryUserNamespaceBudgetMilliseconds -TestCount 125 -TestNames $names -Scope S06) -ne 5375000 -or
               (Get-OrdinaryUserNamespaceStepTimeoutMinutes -TestCount 125 -TestNames $names -Scope S06) -ne 95 -or
               (Get-OrdinaryUserNamespaceBudgetMilliseconds -TestCount 125 -TestNames $names) -ne 5780000 -or
               (Get-OrdinaryUserNamespaceStepTimeoutMinutes -TestCount 125 -TestNames $names) -ne 102){throw 'Selected or retained deadlines changed.'}
            $create=$keepRemotePrefix+'NativeKeepRemoteControlledCreatePreservesItsAbsentBaselineAndAllOriginalJournalEffects'
            if(@($catalog.required.namespace|Where-Object {$_ -ceq $create}).Count -ne 1 -or
               @($catalog.required.native|Where-Object {$_ -ceq $create}).Count -ne 1 -or
               (Get-OrdinaryUserNamespaceBudgetMilliseconds -TestCount 126 -TestNames @($names+$create) -Scope S06) -ne 5440000 -or
               (Get-OrdinaryUserNamespaceBudgetMilliseconds -TestCount 126 -TestNames @($names+$create)) -ne 5845000){throw 'The new Create must add only its one65-second execution in both retained scopes.'}
            if(@($catalog.required.namespace|Where-Object {$_ -ceq $disabledCreation}).Count -ne 1 -or
               @($catalog.required.native|Where-Object {$_ -ceq $disabledCreation}).Count -ne 1 -or
               (Get-OrdinaryUserNamespaceBudgetMilliseconds -TestCount 126 -TestNames @($names+$disabledCreation) -Scope S06) -ne 5440000 -or
               (Get-OrdinaryUserNamespaceBudgetMilliseconds -TestCount 126 -TestNames @($names+$disabledCreation)) -ne 5845000){throw 'The independent disabled creation must retain every original deferred scope and add only65seconds.'}
            if(@($catalog.required.namespace|Where-Object {$_ -ceq $disabledCreationRole}).Count -ne 1 -or
               @($catalog.required.native|Where-Object {$_ -ceq $disabledCreationRole}).Count -ne 1 -or
               (Get-OrdinaryUserNamespaceBudgetMilliseconds -TestCount 126 -TestNames @($names+$disabledCreationRole) -Scope S06) -ne 5440000 -or
               (Get-OrdinaryUserNamespaceBudgetMilliseconds -TestCount 126 -TestNames @($names+$disabledCreationRole)) -ne 5845000){throw 'The independent fresh-role creation must retain all original deferred deadlines and add only65seconds.'}
            if(@($catalog.required.namespace|Where-Object {$_ -ceq $disabledMove}).Count -ne 1 -or
               @($catalog.required.native|Where-Object {$_ -ceq $disabledMove}).Count -ne 1 -or
               (Get-OrdinaryUserNamespaceBudgetMilliseconds -TestCount 126 -TestNames @($names+$disabledMove) -Scope S06) -ne 5440000 -or
               (Get-OrdinaryUserNamespaceBudgetMilliseconds -TestCount 126 -TestNames @($names+$disabledMove)) -ne 5845000){throw 'The disabled controlled move must retain all original deferred deadlines and add only65seconds.'}
            if(@($catalog.required.namespace|Where-Object {$_ -ceq $disabledRetirement}).Count -ne 1 -or
               @($catalog.required.native|Where-Object {$_ -ceq $disabledRetirement}).Count -ne 1 -or
               (Get-OrdinaryUserNamespaceBudgetMilliseconds -TestCount 126 -TestNames @($names+$disabledRetirement) -Scope S06) -ne 5440000 -or
               (Get-OrdinaryUserNamespaceBudgetMilliseconds -TestCount 126 -TestNames @($names+$disabledRetirement)) -ne 5845000){throw 'The explicit disabled retirement must retain all original deferred deadlines and add only65seconds.'}
            if(@($catalog.required.namespace|Where-Object {$_ -ceq $disabledReplacement}).Count -ne 1 -or
               @($catalog.required.native|Where-Object {$_ -ceq $disabledReplacement}).Count -ne 1 -or
               (Get-OrdinaryUserNamespaceBudgetMilliseconds -TestCount 126 -TestNames @($names+$disabledReplacement) -Scope S06) -ne 5440000 -or
               (Get-OrdinaryUserNamespaceBudgetMilliseconds -TestCount 126 -TestNames @($names+$disabledReplacement)) -ne 5845000){throw 'The disabled replacement must retain all original deferred deadlines and add only65seconds.'}
            if(@($catalog.required.namespace|Where-Object {$_ -ceq $residentReplacement}).Count -ne 1 -or
               @($catalog.required.native|Where-Object {$_ -ceq $residentReplacement}).Count -ne 1 -or
               @($catalog.requiredCases.$residentReplacement).Count -ne 2 -or
               (Get-OrdinaryUserNamespaceBudgetMilliseconds -TestCount 126 -TestNames @($names+$residentReplacement) -Scope S06) -ne 5465000 -or
               (Get-OrdinaryUserNamespaceBudgetMilliseconds -TestCount 126 -TestNames @($names+$residentReplacement)) -ne 5870000){throw 'Both resident identity starts must retain their independent45-second deadlines without changing deferred transfer scope.'}
            $prefix='MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseWindowsNamespacePermissionLeaseTests.'
            $methods=@{
                NativeHostTransferRetainsSourceAndBothFencesAfterOriginalCopyAndWholeTargetVerification=1
                NativeHostTransferDeletesOnlyOriginalRemoteSourceWhileRetainingGuardedLocalBytes=1
                NativeHostTransferRetiresOriginalSourceThroughTheSdkAndKeepsPrivateBytesAndBothFences=1
                NativeHostTransferSourceRetirementResumesOriginalNativeAndReceiptBoundariesAcrossStoreOwners=5
                NativeHostTransferAcknowledgesOriginalClaimAndRecoversSdkReturnBeforeReceiptAcrossStoreOwners=1
            }
            $baseline=Get-OrdinaryUserNamespaceBudgetMilliseconds -TestCount 109
            foreach($method in $methods.Keys){
                $selection=@(1..108|ForEach-Object {'Fixture.Case'+$_})+@($prefix+$method)
                $selected=Get-OrdinaryUserNamespaceBudgetMilliseconds -TestCount 109 -TestNames $selection -Scope S06
                $legacy=Get-OrdinaryUserNamespaceBudgetMilliseconds -TestCount 109 -TestNames $selection
                if($selected -ne $baseline-15000+$methods[$method]*45000 -or
                   $legacy -ne $baseline-15000+2*$methods[$method]*45000){throw 'An original 45-second branch or retirement cut was lost.'}
            }
            $other=@($names|Where-Object {$_ -cnotin @($methods.Keys|ForEach-Object {$prefix+$_})})
            $current=Get-OrdinaryUserNamespaceBudgetMilliseconds -TestCount $other.Count -TestNames $other -Scope S06
            $legacy=Get-OrdinaryUserNamespaceBudgetMilliseconds -TestCount $other.Count -TestNames $other
            if($current -ne $legacy -or $other -cnotcontains ($prefix+'NativeScopedRecoveryKeepsABusyRootClosedWhileAnotherRootAcceptsControlledWork')){throw 'A different lifecycle, root-isolation or recovery budget changed.'}
            if(@($catalog.required.deferredP079|Where-Object {$_ -cin $names -or $_ -cin @($catalog.required.native)}).Count){throw 'A deferred method entered a required S06 selection.'}
            $refused=$false
            try{Get-OrdinaryUserNamespaceBudgetMilliseconds -TestCount 109 -Scope S06|Out-Null}catch{$refused=$true}
            if(-not $refused){throw 'Count-only selection cannot establish S06 scope.'}
            """;
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-p079-budget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "verify.ps1");
            await File.WriteAllTextAsync(path, script);
            var result = await RunAsync(path, "-Repository", repository);
            Assert.AreEqual(0, result.ExitCode, result.Output);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [DataRow("native", false)]
    [DataRow("deferredP079", true)]
    public async Task DeferredAndRequiredNativeResultsCannotSubstituteForEachOther(string suite, bool addForeign)
    {
        string repository = SftpProtocolFixture.FindRepositoryRoot();
        using var catalog = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(repository, "eng", "test-suites.json")));
        var required = catalog.RootElement.GetProperty("required");
        string[] names = required.GetProperty("deferredP079").EnumerateArray().Select(item => item.GetString()!).ToArray();
        if (addForeign) names = names.Append(required.GetProperty("native")[0].GetString()!).ToArray();
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-p079-results-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
            var definitions = new XElement(ns + "TestDefinitions");
            var results = new XElement(ns + "Results");
            foreach (string name in names)
            {
                string id = Guid.NewGuid().ToString("D");
                int separator = name.LastIndexOf('.');
                definitions.Add(new XElement(ns + "UnitTest", new XAttribute("id", id),
                    new XAttribute("storage", "MirrorPulse.CloudFiles.CfSharp.Tests.dll"),
                    new XElement(ns + "TestMethod", new XAttribute("className", name[..separator]), new XAttribute("name", name[(separator + 1)..]))));
                results.Add(new XElement(ns + "UnitTestResult", new XAttribute("testId", id), new XAttribute("outcome", "Passed")));
            }
            new XDocument(new XElement(ns + "TestRun", definitions, results)).Save(Path.Combine(root, "fixture.trx"));
            var result = await RunAsync(Path.Combine(repository, "eng", "verify-test-results.ps1"), "-Suite", suite, "-ResultsDirectory", root);
            Assert.AreNotEqual(0, result.ExitCode, result.Output);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string script, params string[] arguments)
    {
        var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "-NoProfile", "-File", script }.Concat(arguments)) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await output + await error);
    }
}
