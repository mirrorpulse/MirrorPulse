#if MIRRORPULSE_SHELL_PROBE
using System.Security.Principal;
using MirrorPulse.CloudFiles.CfSharp;
using Windows.ApplicationModel;
using Windows.Storage;
using Windows.Storage.Provider;

namespace MirrorPulse.App;

internal static class MirrorPulsePackagedShellProbe
{
    public static async Task RunAsync(string resultPath, App app)
    {
        MirrorPulsePackagedShellProbeProgress.Record(resultPath, MirrorPulsePackagedShellProbeStage.ProbeEntered);
        try
        {
            if (Package.Current.Id.Name != "0B72358D-6DC9-479D-8C28-F0232B42A0B3")
            {
                throw new InvalidOperationException("The Shell probe process has the wrong package identity.");
            }

            MirrorPulsePackagedShellProbeProgress.Record(resultPath, MirrorPulsePackagedShellProbeStage.PackageValidated);
            string root = MirrorPulseShellProbeHostRoot.RequireExpectedRoot(
                Environment.GetEnvironmentVariable("MIRRORPULSE_PACKAGED_SHELL_PROBE_ROOT"));
            var definition = new MirrorPulseSyncRootDefinition(root, "0.1.0",
                Guid.Parse("d194eacd-c38c-49df-a238-49871f9c8d1b"), "MirrorPulse/cloud-files/v1"u8.ToArray());
            MirrorPulseShellRegistrationProfile profile = MirrorPulseShellSyncRootRegistrar.CreateCurrentUserProfile(definition);
            StorageProviderSyncRootInfo original = StorageProviderSyncRootManager.GetSyncRootInformationForId(profile.RegistrationId);
            MirrorPulseShellProbeHostRoot.RequireRegistration(root, definition.ProviderId, original.Path.Path, original.ProviderId,
                    () => MirrorPulseShellSyncRootRegistrar.RequireMatchingRegistration(profile));
            MirrorPulseShellRegistrationProfile restore = profile with
            {
                DisplayName = original.DisplayNameResource,
                IconResource = original.IconResource,
                ProviderVersion = original.Version,
            };
            await MirrorPulseShellProbeHostRoot.VerifyAndRestoreAsync(async () =>
            {
                MirrorPulsePackagedShellProbeProgress.Record(resultPath, MirrorPulsePackagedShellProbeStage.CustomRegistrationStarted);
                await MirrorPulseShellSyncRootRegistrar.RegisterAsync(profile with { DisplayName = "Personal drive" });
                MirrorPulsePackagedShellProbeProgress.Record(resultPath, MirrorPulsePackagedShellProbeStage.CustomRegistrationCompleted);
                MirrorPulsePackagedShellProbeProgress.Record(resultPath, MirrorPulsePackagedShellProbeStage.CustomReadbackStarted);
                StorageProviderSyncRootInfo custom = StorageProviderSyncRootManager.GetSyncRootInformationForId(profile.RegistrationId);
                if (custom.DisplayNameResource != "Personal drive")
                    throw new InvalidDataException("The custom Shell label did not appear.");

                MirrorPulsePackagedShellProbeProgress.Record(resultPath, MirrorPulsePackagedShellProbeStage.CustomReadbackVerified);
                MirrorPulsePackagedShellProbeProgress.Record(resultPath, MirrorPulsePackagedShellProbeStage.UnifiedRegistrationStarted);
                await MirrorPulseShellSyncRootRegistrar.RegisterAsync(profile);
                MirrorPulsePackagedShellProbeProgress.Record(resultPath, MirrorPulsePackagedShellProbeStage.UnifiedRegistrationCompleted);
                MirrorPulsePackagedShellProbeProgress.Record(resultPath, MirrorPulsePackagedShellProbeStage.UnifiedReadbackStarted);
                StorageProviderSyncRootInfo unified = StorageProviderSyncRootManager.GetSyncRootInformationForId(profile.RegistrationId);
                if (unified.DisplayNameResource != "MirrorPulse")
                    throw new InvalidDataException("The unified Shell label did not appear.");
                MirrorPulsePackagedShellProbeProgress.Record(resultPath, MirrorPulsePackagedShellProbeStage.UnifiedReadbackVerified);
            }, async () =>
            {
                await MirrorPulseShellSyncRootRegistrar.RegisterAsync(restore);
                StorageProviderSyncRootInfo restored = StorageProviderSyncRootManager.GetSyncRootInformationForId(profile.RegistrationId);
                MirrorPulseShellProbeHostRoot.RequireRegistration(root, definition.ProviderId, restored.Path.Path, restored.ProviderId,
                    () => MirrorPulseShellSyncRootRegistrar.RequireMatchingRegistration(profile));
                if (restored.DisplayNameResource != restore.DisplayName)
                    throw new InvalidDataException("The original Host Shell label was not restored.");
            });
            MirrorPulsePackagedShellProbeProgress.Record(resultPath, MirrorPulsePackagedShellProbeStage.WritingSuccess);
            await File.WriteAllTextAsync(resultPath, "success");
            MirrorPulsePackagedShellProbeProgress.Record(resultPath, MirrorPulsePackagedShellProbeStage.SuccessWritten);
        }
        catch (Exception exception)
        {
            MirrorPulsePackagedShellProbeProgress.Record(resultPath, MirrorPulsePackagedShellProbeStage.FailureHandling);
            string minimalResult = await TryMinimalRegistrationAsync(resultPath);
            MirrorPulsePackagedShellProbeProgress.Record(resultPath, MirrorPulsePackagedShellProbeStage.WritingFailure);
            await File.WriteAllTextAsync(resultPath,
                $"failure: 0x{exception.HResult:X8} {exception.GetType().Name}: {exception.Message}; " +
                $"minimal registration: {minimalResult}");
            MirrorPulsePackagedShellProbeProgress.Record(resultPath, MirrorPulsePackagedShellProbeStage.FailureWritten);
        }
        finally
        {
            MirrorPulsePackagedShellProbeProgress.Record(resultPath, MirrorPulsePackagedShellProbeStage.Exiting);
            app.Exit();
        }
    }

    private static async Task<string> TryMinimalRegistrationAsync(string resultPath)
    {
        try { return await RunMinimalRegistrationAsync(resultPath); }
        catch (Exception failure)
        {
            // A diagnostic cleanup failure must not replace the original product failure.
            return $"0x{failure.HResult:X8} {failure.GetType().Name}: {failure.Message}";
        }
    }

    private static async Task<string> RunMinimalRegistrationAsync(string resultPath)
    {
        // This diagnostic owns a separate directory and ID, never a child of the Host root.
        string suffix = Guid.NewGuid().ToString("N");
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "MirrorPulse-packaged-shell", suffix);
        string? registrationId = null;
        bool registered = false;
        try
        {
            Directory.CreateDirectory(path);
            string sid = WindowsIdentity.GetCurrent().User?.Value
                ?? throw new InvalidOperationException("The Shell probe user has no SID.");
            registrationId = $"MirrorPulseProbe!{sid}!{suffix}";
            MirrorPulsePackagedShellProbeProgress.Record(resultPath, MirrorPulsePackagedShellProbeStage.MinimalFolderStarted);
            StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(path);
            MirrorPulsePackagedShellProbeProgress.Record(resultPath, MirrorPulsePackagedShellProbeStage.MinimalFolderReady);
            Guid expectedProvider = Guid.Parse("d194eacd-c38c-49df-a238-49871f9c8d1b");
            var info = new StorageProviderSyncRootInfo
            {
                Id = registrationId,
                Path = folder,
                ProviderId = expectedProvider,
                DisplayNameResource = "MirrorPulse probe",
                IconResource = "%SystemRoot%\\system32\\charmap.exe,0",
                HydrationPolicy = StorageProviderHydrationPolicy.Full,
                HydrationPolicyModifier = StorageProviderHydrationPolicyModifier.None,
                PopulationPolicy = StorageProviderPopulationPolicy.AlwaysFull,
                InSyncPolicy = StorageProviderInSyncPolicy.FileCreationTime |
                    StorageProviderInSyncPolicy.DirectoryCreationTime,
                HardlinkPolicy = StorageProviderHardlinkPolicy.None,
                Version = "0.1.0",
                ShowSiblingsAsGroup = false,
            };
            MirrorPulsePackagedShellProbeProgress.Record(resultPath, MirrorPulsePackagedShellProbeStage.MinimalRegistrationStarted);
            StorageProviderSyncRootManager.Register(info);
            MirrorPulsePackagedShellProbeProgress.Record(resultPath, MirrorPulsePackagedShellProbeStage.MinimalRegistrationCompleted);
            registered = true;
            MirrorPulsePackagedShellProbeProgress.Record(resultPath, MirrorPulsePackagedShellProbeStage.MinimalReadbackStarted);
            StorageProviderSyncRootInfo actual = StorageProviderSyncRootManager
                .GetSyncRootInformationForId(registrationId);
            MirrorPulsePackagedShellProbeProgress.Record(resultPath, MirrorPulsePackagedShellProbeStage.MinimalReadbackCompleted);
            bool labelMatches = actual.DisplayNameResource == info.DisplayNameResource;
            bool providerMatches = actual.ProviderId == expectedProvider;
            string outcome = labelMatches && providerMatches ? "success" : "registered with differing readback";
            return $"{outcome}; labelMatches={labelMatches}; expectedProvider={expectedProvider:D}; actualProvider={actual.ProviderId:D}";
        }
        catch (Exception exception)
        {
            return $"0x{exception.HResult:X8} {exception.GetType().Name}: {exception.Message}";
        }
        finally
        {
            if (registered && registrationId is not null)
            {
                MirrorPulsePackagedShellProbeProgress.Record(resultPath, MirrorPulsePackagedShellProbeStage.MinimalUnregisterStarted);
                StorageProviderSyncRootManager.Unregister(registrationId);
                MirrorPulsePackagedShellProbeProgress.Record(resultPath, MirrorPulsePackagedShellProbeStage.MinimalUnregisterCompleted);
            }
            if (Directory.Exists(path))
            {
                MirrorPulsePackagedShellProbeProgress.Record(resultPath, MirrorPulsePackagedShellProbeStage.DirectoryCleanupStarted);
                Directory.Delete(path, recursive: true);
                MirrorPulsePackagedShellProbeProgress.Record(resultPath, MirrorPulsePackagedShellProbeStage.DirectoryCleanupCompleted);
            }
        }
    }
}
#endif
