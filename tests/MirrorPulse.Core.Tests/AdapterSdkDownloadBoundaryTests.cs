using System.Diagnostics;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class AdapterSdkDownloadBoundaryTests
{
    [TestMethod, DoNotParallelize]
    [DataRow("primary")]
    [DataRow("fallback")]
    [DataRow("timeout")]
    [DataRow("anonymous")]
    [DataRow("not-found")]
    [DataRow("metadata-error")]
    [DataRow("asset-error")]
    [DataRow("wrong-release")]
    [DataRow("wrong-asset")]
    [DataRow("wrong-digest")]
    [DataRow("wrong-bytes")]
    [DataRow("cache")]
    [DataRow("damaged-cache")]
    [DataRow("supplied")]
    [DataRow("required-cache")]
    [DataRow("required-cache-missing")]
    [DataRow("required-cache-damaged")]
    [DataRow("required-and-supplied")]
    public async Task DownloadRetainsTheExactPinAndBoundsPublicTransportWithoutRecordingResponseBodies(string scenario)
    {
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-sdk-restore-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string script = Path.Combine(root, "verify.ps1");
            await File.WriteAllTextAsync(script, """
                param([string]$Repository,[string]$Directory,[string]$Scenario)
                $ErrorActionPreference='Stop'
                $eng=Join-Path $Directory 'eng';New-Item -ItemType Directory -Path $eng|Out-Null
                Copy-Item -LiteralPath (Join-Path $Repository 'eng/restore-adapter-sdk.ps1') -Destination (Join-Path $eng 'restore-adapter-sdk.ps1')
                $pin=Get-Content -LiteralPath (Join-Path $Repository 'eng/adapter-sdk.lock.json') -Raw|ConvertFrom-Json
                $bytes=[Text.Encoding]::UTF8.GetBytes('owned fixture bytes for the immutable download boundary')
                $pin.length=$bytes.Length;$pin.sha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
                $pin|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $eng 'adapter-sdk.lock.json')
                $state=[pscustomobject]@{primary=0;metadata=0;asset=0}
                $bodyMarker='response-body-marker-781';$authentication='fixture-authentication-not-a-live-token'
                $env:GITHUB_ACTIONS=if($Scenario -ceq 'anonymous'){'false'}else{'true'}
                $env:GITHUB_TOKEN=$authentication
                function Throw-Response([int]$Status) {
                    throw [Microsoft.PowerShell.Commands.HttpResponseException]::new($bodyMarker,[Net.Http.HttpResponseMessage]::new([Net.HttpStatusCode]$Status))
                }
                function Assert-Transport([int]$Connection,[int]$Operation,[int]$Retries,[int]$ExpectedRetries) {
                    if($Connection -ne 15 -or $Operation -ne 30 -or $Retries -ne $ExpectedRetries){throw 'The external request lost its bounded transport settings.'}
                }
                function Assert-ApiHeaders([hashtable]$Headers,[string]$Accept) {
                    if($Headers.Accept -cne $Accept){throw 'The exact API representation changed.'}
                    $expected=if($Scenario -ceq 'anonymous'){$null}else{'Bearer '+$authentication}
                    if($Headers.Authorization -cne $expected){throw 'CI authentication was absent, exposed to an ordinary caller or changed.'}
                }
                function Invoke-WebRequest {
                    [CmdletBinding()]
                    param([string]$Uri,[string]$OutFile,[hashtable]$Headers,[int]$MaximumRetryCount,[int]$RetryIntervalSec,
                        [int]$ConnectionTimeoutSeconds,[int]$OperationTimeoutSeconds)
                    if($Uri -ceq ('https://github.com/MirrorPulse/adapter-template/releases/download/'+$pin.tag+'/'+$pin.asset)) {
                        $state.primary++;Assert-Transport $ConnectionTimeoutSeconds $OperationTimeoutSeconds $MaximumRetryCount 1
                        if($null -ne $Headers){throw 'The primary public download must not receive API credentials.'}
                        if($Scenario -ceq 'primary'){[IO.File]::WriteAllBytes($OutFile,$bytes);return}
                        if($Scenario -ceq 'not-found'){Throw-Response 404}
                        if($Scenario -ceq 'timeout'){throw [TimeoutException]::new($bodyMarker)}
                        Throw-Response 504
                    }
                    if($Uri -cne 'https://api.github.com/repos/mirrorpulse/adapter-template/releases/assets/123'){throw 'An unselected asset address was requested.'}
                    $state.asset++;Assert-Transport $ConnectionTimeoutSeconds $OperationTimeoutSeconds $MaximumRetryCount 2
                    Assert-ApiHeaders $Headers 'application/octet-stream'
                    if($Scenario -ceq 'asset-error'){Throw-Response 503}
                    [IO.File]::WriteAllBytes($OutFile, $(if($Scenario -ceq 'wrong-bytes'){[byte[]](9,8,7)}else{$bytes}))
                }
                function Invoke-RestMethod {
                    [CmdletBinding()]
                    param([string]$Uri,[hashtable]$Headers,[int]$MaximumRetryCount,[int]$RetryIntervalSec,
                        [int]$ConnectionTimeoutSeconds,[int]$OperationTimeoutSeconds)
                    $state.metadata++;Assert-Transport $ConnectionTimeoutSeconds $OperationTimeoutSeconds $MaximumRetryCount 2
                    Assert-ApiHeaders $Headers 'application/vnd.github+json'
                    if($Uri -cne ('https://api.github.com/repos/mirrorpulse/adapter-template/releases/tags/'+$pin.tag)){throw 'The fixed release lookup changed.'}
                    if($Scenario -ceq 'metadata-error'){Throw-Response 503}
                    [pscustomobject]@{tag_name=$(if($Scenario -ceq 'wrong-release'){'sdk-v9.9.9'}else{$pin.tag});draft=$false;
                        assets=@([pscustomobject]@{name=$pin.asset;size=$pin.length;
                            digest=$(if($Scenario -ceq 'wrong-digest'){'sha256:'+('a'*64)}else{'sha256:'+$pin.sha256});
                            url=$(if($Scenario -ceq 'wrong-asset'){'https://example.invalid/package'}else{'https://api.github.com/repos/mirrorpulse/adapter-template/releases/assets/123'})})}
                }
                $feed=Join-Path $Directory 'artifacts/sdk-feed';$destination=Join-Path $feed $pin.asset
                if($Scenario -cin @('cache','damaged-cache','required-cache','required-cache-damaged')) {
                    New-Item -ItemType Directory -Path $feed -Force|Out-Null
                    [IO.File]::WriteAllBytes($destination,$(if($Scenario -cin @('damaged-cache','required-cache-damaged')){[byte[]](1,2,3)}else{$bytes}))
                }
                $parameters=@{}
                if($Scenario -cin @('supplied','required-and-supplied')){
                    $source=Join-Path $Directory 'supplied.nupkg';[IO.File]::WriteAllBytes($source,$bytes);$parameters.PackagePath=$source
                }
                if($Scenario.StartsWith('required-',[StringComparison]::Ordinal)){$parameters.RequireCachedPackage=$true}
                $failure=$null;$messages=@()
                try{$messages=@(& (Join-Path $eng 'restore-adapter-sdk.ps1') @parameters 6>&1|ForEach-Object{[string]$_})}
                catch{$failure=$_.Exception.Message}
                $all=($messages -join [char]10)+$failure
                if($all.Contains($bodyMarker) -or $all.Contains($authentication)){throw 'A server body or authentication value escaped the closed build error.'}
                $failures=@{'not-found'='HTTP 404';'metadata-error'='HTTP 503';'asset-error'='HTTP 503';
                    'wrong-release'='does not match its original pin';'wrong-asset'='does not match its original pin';
                    'wrong-digest'='does not match its original pin';'wrong-bytes'='pinned length and SHA256';'damaged-cache'='pinned length and SHA256';
                    'required-cache-missing'='required frozen SDK package is missing';'required-cache-damaged'='pinned length and SHA256';
                    'required-and-supplied'='cannot be combined'}
                if($failures.ContainsKey($Scenario)){
                    if($null -eq $failure -or -not $failure.Contains($failures[$Scenario])){throw 'The failed boundary did not retain its exact closed classification.'}
                    if($Scenario -cnotin @('damaged-cache','required-cache-damaged') -and (Test-Path -LiteralPath $destination)){throw 'A failed download was promoted to the SDK feed.'}
                }else{
                    if($null -ne $failure){throw $failure}
                    if((Get-FileHash -LiteralPath $destination).Hash.ToLowerInvariant() -cne $pin.sha256){throw 'The selected bytes differ from the exact fixture pin.'}
                    $before=$state|ConvertTo-Json -Compress
                    & (Join-Path $eng 'restore-adapter-sdk.ps1')|Out-Null
                    if(($state|ConvertTo-Json -Compress) -cne $before){throw 'A verified cached package was downloaded again.'}
                }
                $expected=switch -Exact ($Scenario){
                    'primary'{@(1,0,0)};{$_ -cin @('cache','damaged-cache','supplied') -or $_.StartsWith('required-',[StringComparison]::Ordinal)}{@(0,0,0)};
                    'not-found'{@(1,0,0)};'metadata-error'{@(1,1,0)};
                    {$_ -cin @('wrong-release','wrong-asset','wrong-digest')}{@(1,1,0)};default{@(1,1,1)}
                }
                if($state.primary -ne $expected[0] -or $state.metadata -ne $expected[1] -or $state.asset -ne $expected[2]){throw 'A refused route or cached package caused an unexpected external request.'}
                if((Test-Path -LiteralPath $feed) -and @(Get-ChildItem -LiteralPath $feed -Filter '*.download').Count){throw 'A temporary download remained after the boundary returned.'}
                """);
            var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in new[] { "-NoProfile", "-File", script, "-Repository", SftpProtocolFixture.FindRepositoryRoot(), "-Directory", root, "-Scenario", scenario })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            Task<string> output = process.StandardOutput.ReadToEndAsync(); Task<string> error = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
            catch { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } throw; }
            Assert.AreEqual(0, process.ExitCode, await output + await error);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
