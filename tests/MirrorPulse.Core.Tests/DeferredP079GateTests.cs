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
            $names=@($catalog.required.namespace)
            if($names.Count -ne 124 -or @($catalog.required.native).Count -ne 149 -or @($catalog.required.deferredP079).Count -ne 5){throw 'Required scope names changed.'}
            if((Get-OrdinaryUserNamespaceBudgetMilliseconds -TestCount 124 -TestNames $names -Scope S06) -ne 5310000 -or
               (Get-OrdinaryUserNamespaceStepTimeoutMinutes -TestCount 124 -TestNames $names -Scope S06) -ne 94 -or
               (Get-OrdinaryUserNamespaceBudgetMilliseconds -TestCount 124 -TestNames $names) -ne 5715000 -or
               (Get-OrdinaryUserNamespaceStepTimeoutMinutes -TestCount 124 -TestNames $names) -ne 101){throw 'Selected or retained deadlines changed.'}
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
