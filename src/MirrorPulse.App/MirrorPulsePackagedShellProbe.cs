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
        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "MirrorPulse-packaged-shell",
            Guid.NewGuid().ToString("N"));
        MirrorPulseShellRegistrationProfile? profile = null;
        try
        {
            if (Package.Current.Id.Name != "0B72358D-6DC9-479D-8C28-F0232B42A0B3")
            {
                throw new InvalidOperationException("The Shell probe process has the wrong package identity.");
            }

            MirrorPulsePackagedShellProbeProgress.Record(resultPath, MirrorPulsePackagedShellProbeStage.PackageValidated);
            Directory.CreateDirectory(root);
            MirrorPulsePackagedShellProbeProgress.Record(resultPath, MirrorPulsePackagedShellProbeStage.RootCreated);
            var definition = new MirrorPulseSyncRootDefinition(root, "0.1.0",
                Guid.Parse("89f1747b-62aa-48bd-a725-e33f20c271a5"), [1, 2, 3]);
            profile = MirrorPulseShellSyncRootRegistrar.CreateCurrentUserProfile(definition, "Personal drive");
            MirrorPulsePackagedShellProbeProgress.Record(resultPath, MirrorPulsePackagedShellProbeStage.CustomRegistrationStarted);
            await MirrorPulseShellSyncRootRegistrar.RegisterAsync(profile);
            MirrorPulsePackagedShellProbeProgress.Record(resultPath, MirrorPulsePackagedShellProbeStage.CustomRegistrationCompleted);
            MirrorPulsePackagedShellProbeProgress.Record(resultPath, MirrorPulsePackagedShellProbeStage.CustomReadbackStarted);
            StorageProviderSyncRootInfo custom = StorageProviderSyncRootManager
                .GetSyncRootInformationForId(profile.RegistrationId);
            if (custom.DisplayNameResource != "Personal drive")
            {
                throw new InvalidDataException("The custom Shell label did not appear.");
            }

            profile = MirrorPulseShellSyncRootRegistrar.CreateCurrentUserProfile(definition, "MirrorPulse");
            MirrorPulsePackagedShellProbeProgress.Record(resultPath, MirrorPulsePackagedShellProbeStage.CustomReadbackVerified);
            MirrorPulsePackagedShellProbeProgress.Record(resultPath, MirrorPulsePackagedShellProbeStage.UnifiedRegistrationStarted);
            await MirrorPulseShellSyncRootRegistrar.RegisterAsync(profile);
            MirrorPulsePackagedShellProbeProgress.Record(resultPath, MirrorPulsePackagedShellProbeStage.UnifiedRegistrationCompleted);
            MirrorPulsePackagedShellProbeProgress.Record(resultPath, MirrorPulsePackagedShellProbeStage.UnifiedReadbackStarted);
            StorageProviderSyncRootInfo unified = StorageProviderSyncRootManager
                .GetSyncRootInformationForId(profile.RegistrationId);
            if (unified.DisplayNameResource != "MirrorPulse")
            {
                throw new InvalidDataException("The unified Shell label did not appear.");
            }

            MirrorPulsePackagedShellProbeProgress.Record(resultPath, MirrorPulsePackagedShellProbeStage.UnifiedReadbackVerified);
            MirrorPulsePackagedShellProbeProgress.Record(resultPath, MirrorPulsePackagedShellProbeStage.WritingSuccess);
            await File.WriteAllTextAsync(resultPath, "success");
            MirrorPulsePackagedShellProbeProgress.Record(resultPath, MirrorPulsePackagedShellProbeStage.SuccessWritten);
        }
        catch (Exception exception)
        {
            MirrorPulsePackagedShellProbeProgress.Record(resultPath, MirrorPulsePackagedShellProbeStage.FailureHandling);
            string minimalResult = await TryMinimalRegistrationAsync(root, resultPath);
            MirrorPulsePackagedShellProbeProgress.Record(resultPath, MirrorPulsePackagedShellProbeStage.WritingFailure);
            await File.WriteAllTextAsync(resultPath,
                $"failure: 0x{exception.HResult:X8} {exception.GetType().Name}: {exception.Message}; " +
                $"minimal registration: {minimalResult}");
            MirrorPulsePackagedShellProbeProgress.Record(resultPath, MirrorPulsePackagedShellProbeStage.FailureWritten);
        }
        finally
        {
            if (profile is not null)
            {
                try
                {
                    MirrorPulsePackagedShellProbeProgress.Record(resultPath, MirrorPulsePackagedShellProbeStage.UnregisterStarted);
                    MirrorPulseShellSyncRootRegistrar.Unregister(profile);
                    MirrorPulsePackagedShellProbeProgress.Record(resultPath, MirrorPulsePackagedShellProbeStage.UnregisterCompleted);
                }
                catch (Exception)
                {
                }
            }

            if (Directory.Exists(root))
            {
                MirrorPulsePackagedShellProbeProgress.Record(resultPath, MirrorPulsePackagedShellProbeStage.DirectoryCleanupStarted);
                Directory.Delete(root, recursive: true);
                MirrorPulsePackagedShellProbeProgress.Record(resultPath, MirrorPulsePackagedShellProbeStage.DirectoryCleanupCompleted);
            }

            MirrorPulsePackagedShellProbeProgress.Record(resultPath, MirrorPulsePackagedShellProbeStage.Exiting);
            app.Exit();
        }
    }

    private static async Task<string> TryMinimalRegistrationAsync(string root, string resultPath)
    {
        string path = Path.Combine(root, "minimal");
        string? registrationId = null;
        bool registered = false;
        try
        {
            Directory.CreateDirectory(path);
            string sid = WindowsIdentity.GetCurrent().User?.Value
                ?? throw new InvalidOperationException("The Shell probe user has no SID.");
            registrationId = $"MirrorPulseProbe!{sid}!Minimum";
            MirrorPulsePackagedShellProbeProgress.Record(resultPath, MirrorPulsePackagedShellProbeStage.MinimalFolderStarted);
            StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(path);
            MirrorPulsePackagedShellProbeProgress.Record(resultPath, MirrorPulsePackagedShellProbeStage.MinimalFolderReady);
            var info = new StorageProviderSyncRootInfo
            {
                Id = registrationId,
                Path = folder,
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
            return actual.DisplayNameResource == info.DisplayNameResource
                ? "success"
                : "registered but display name differs";
        }
        catch (Exception exception)
        {
            return $"0x{exception.HResult:X8} {exception.GetType().Name}: {exception.Message}";
        }
        finally
        {
            if (registered && registrationId is not null)
            {
                try
                {
                    MirrorPulsePackagedShellProbeProgress.Record(resultPath, MirrorPulsePackagedShellProbeStage.MinimalUnregisterStarted);
                    StorageProviderSyncRootManager.Unregister(registrationId);
                    MirrorPulsePackagedShellProbeProgress.Record(resultPath, MirrorPulsePackagedShellProbeStage.MinimalUnregisterCompleted);
                }
                catch (Exception)
                {
                }
            }
        }
    }
}
#endif
