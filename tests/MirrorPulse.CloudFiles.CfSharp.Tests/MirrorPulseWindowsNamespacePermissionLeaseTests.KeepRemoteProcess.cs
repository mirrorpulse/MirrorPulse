using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using MirrorPulse.CfSharp.CrashProbe;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseWindowsNamespacePermissionLeaseTests
{
    private async Task VerifyIndependentKeepRemoteRuntimeAsync(string directory, MirrorPulseStoragePaths paths,
        MirrorPulseConflictKeepRemoteFileStart first, MirrorPulseConflictKeepRemoteFileAction firstAction,
        int expectedOriginals, byte[] selected, byte[] preserved, CancellationToken token)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(typeof(ProbeMarker).Assembly.Location);
        start.ArgumentList.Add("--resume-keep-remote");
        start.ArgumentList.Add(directory);
        start.ArgumentList.Add(first.CommandId.ToString("D"));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The independent KeepRemote runtime did not start.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(token);
        Task<string> error = process.StandardError.ReadToEndAsync(token);
        try
        {
            await process.WaitForExitAsync(token);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
        Assert.AreEqual(0, process.ExitCode, await error);
        var evidence = JsonSerializer.Deserialize<KeepRemoteRestartEvidence>(await output);
        Assert.IsNotNull(evidence);
        Assert.AreEqual(process.Id, evidence.ProcessId);
        Assert.AreNotEqual(Environment.ProcessId, evidence.ProcessId);
        Assert.AreEqual(RuntimeInformation.ProcessArchitecture.ToString(), evidence.Architecture);
        Assert.AreEqual(first.CommandId, evidence.CommandId);
        Assert.AreEqual(first.StoreFileId, evidence.StoreFileId);
        Assert.AreEqual(first.Original.Context.Member.LocalObject.LocalFileId, evidence.LocalFileId);
        Assert.AreEqual(expectedOriginals, evidence.OriginalCount);
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(selected)), evidence.SelectedSha256);
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(preserved)), evidence.PreservedSha256);

        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(paths, token);
        var settled = await reopened.ReadConflictKeepRemoteAcknowledgementAsync(first.CommandId, token);
        Assert.IsNotNull(settled?.Result);
        Assert.AreEqual(evidence.InvocationId, settled.Start.InvocationId);
        Assert.AreEqual(firstAction, await reopened.ReadConflictKeepRemoteFileActionAsync(first.CommandId, token));
        Assert.AreEqual("resolved", (await reopened.ReadUserCommandAsync(first.CommandId, token))!.State);
        Assert.HasCount(0, await reopened.ReadNamespaceDispatchFencedRootsAsync(token));
        TestContext.WriteLine($"Independent KeepRemote runtime process {evidence.ProcessId} recovered the original command and audited its root.");
    }
}
