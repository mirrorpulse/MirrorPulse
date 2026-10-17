using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseWindowsNamespacePermissionLeaseTests
{
    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public async Task NativeAutomaticStartupIsolatesAccessDeniedAndRecoversTheOriginalRootAdmission()
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var roots = AdapterRootRegistrationMapper.MapAll(AdapterId.Parse("example.startup-access"), InstanceId.New(),
            [new("existing", "Existing", "Existing", false), new("denied", "New", "New", false)],
            RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot).ToArray();
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, roots);
        RootRegistration denied = roots.Single(root => root.DirectoryName == "New");
        RootRegistration independent = roots.Single(root => root.DirectoryName == "Existing");
        var registry = new CfSharpMirrorPulseCloudRootRegistry(); using var user = WindowsIdentity.GetCurrent();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(65));
        DirectorySecurity? original = null; bool changed = false;
        SafeFileHandle? restorationHandle = null;
        string deniedPath = Path.Combine(paths.SyncRootPath, "New"); string? firstRole = null;
        Dictionary<string, Guid> members = [];
        string phase = "Setup";
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [1, 2, 3]));
            await File.WriteAllTextAsync(Path.Combine(directory, ".mp-permission-fixture"), string.Empty, timeout.Token);
            var setupState = new MirrorPulseCfSharpStateSession(paths);
            await using (var setup = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(setupState)
                .WithContentProvider(MirrorPulseDemandProvider.CreateWithoutAdapters(paths.SyncRootPath)).Build())
            {
                await setup.StartAsync(timeout.Token); SetProtectedFixtureUserOwner(setup.Root, user.User!);
            }
            for (int owner = 0; owner < 2; owner++)
            {
                await using var execution = new MirrorPulseNamespaceExecutionSession(); await using var actor = new MirrorPulseNamespaceActor();
                await using var scheduler = new MirrorPulseInstanceScheduler();
                await using var work = new MirrorPulseNamespaceWorkAdmission(roots.Select(root => root.RootId));
                await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, timeout.Token);
                var worker = new MirrorPulseNormalUserWorkerTransportTests.IdentityProbeWorker(execution);
                var source = new MirrorPulseNormalUserWorkerTransport(execution, worker, worker, worker, worker, worker);
                var provider = new MirrorPulseDemandProvider(router, source, new MirrorPulseAdapterDirectoryPageSource(source), work);
                var factory = new CfSharpMirrorPulseCloudRuntimeFactory(provider: provider, router: router, catalog: catalog,
                    namespaceExecution: execution, namespaceActor: actor, scheduler: scheduler, namespaceWork: work, initializeNamespaceOnStart: true);
                await using var runtime = factory.Create(paths);
                phase = owner == 0 ? "InitialStartup" : "DeniedRootStartup";
                await runtime.StartAsync(timeout.Token);
                if (owner == 0)
                {
                    firstRole = execution.RoleSid.Value;
                    Assert.IsTrue(work.IsAdmitted(denied.RootId)); Assert.IsTrue(work.IsAdmitted(independent.RootId));
                    var anchor = await runtime.InspectNamespaceMemberAsync(string.Empty, true, timeout.Token);
                    members = (await catalog.ReadNamespaceMemberTreeAsync(anchor.MemberId, timeout.Token)).ToDictionary(entry => entry.RelativePath, entry => entry.MemberId);
                    original = new DirectoryInfo(deniedPath).GetAccessControl(AccessControlSections.Access);
                }
                else
                {
                    Assert.AreNotEqual(firstRole, execution.RoleSid.Value);
                    var failure = runtime.NamespaceStartupResults.Single(result => result.RootId == denied.RootId);
                    TestContext.WriteLine($"StartupAccessDenialObservation: admitted={failure.Admitted}; code={failure.RecoveryCode}; native={failure.NativeErrorCode}; retryIdPresent={failure.OperationId is not null}.");
                    Assert.IsFalse(failure.Admitted); Assert.IsFalse(work.IsAdmitted(denied.RootId));
                    Assert.IsNotNull(failure.OperationId); Assert.AreEqual(5, failure.NativeErrorCode);
                    Assert.IsTrue(work.IsAdmitted(independent.RootId), "One denied root must not stop another root's fresh audit.");
                    phase = "RestoreOriginalDescriptor";
                    RestoreStartupAccessDenialFixture(restorationHandle!, original!); changed = false;
                    phase = "RecoverOriginalAdmission";
                    var restored = await runtime.RecoverNamespaceRootAsync(failure.OperationId.Value, denied.RootId, timeout.Token);
                    Assert.AreEqual(denied.RootId, restored.ScopeRoot); Assert.IsTrue(work.IsAdmitted(denied.RootId));
                    foreach (var entry in await catalog.ReadNamespaceMemberTreeAsync(members[string.Empty], timeout.Token))
                        Assert.AreEqual(members[entry.RelativePath], entry.MemberId);
                    Assert.IsEmpty(await catalog.ReadNamespaceDispatchFencedRootsAsync(timeout.Token));
                    TestContext.WriteLine("StartupAccessDenial: actualRuntime=True; distinctNamespaceRoles=True; actualWin32=5; " +
                        "failedRootRemainsClosed=True; independentRootAdmitted=True; originalRecoveryIdReused=True; " +
                        "originalMemberIdsPreserved=True; sourceCalls=0; remoteAcceptance=False.");
                }
                Assert.IsEmpty(worker.Calls);
                // Inject the ACL fault only after this owner's runtime has drained.
                if (owner == 0)
                {
                    // Retain only metadata and owner DACL authority while the
                    // public SDK is connected. LIST_DIRECTORY on an offline
                    // cloud directory can require a provider name request.
                    phase = "RetainRestorationHandle";
                    restorationHandle = MirrorPulseWindowsNamespacePermissionLease.Open(deniedPath, target: true,
                        metadataOnlyTarget: true, ownerMetadataWrite: true);
                    phase = "DrainInitialRuntime";
                    await runtime.DisposeAsync();
                    var deniedAccess = new DirectorySecurity();
                    deniedAccess.SetSecurityDescriptorSddlForm(original!.GetSecurityDescriptorSddlForm(AccessControlSections.Access), AccessControlSections.Access);
                    // OWNER RIGHTS disables implicit owner READ_CONTROL. The saved
                    // object handle retains WRITE_DAC across the injected denial.
                    // https://learn.microsoft.com/en-us/windows-server/identity/ad-ds/manage/understand-security-identifiers
                    var ownerRights = new SecurityIdentifier("S-1-3-4");
                    deniedAccess.AddAccessRule(new(ownerRights, FileSystemRights.ChangePermissions, AccessControlType.Allow));
                    deniedAccess.AddAccessRule(new(ownerRights, FileSystemRights.ReadPermissions, AccessControlType.Deny));
                    phase = "InjectPermissionDenial";
                    new DirectoryInfo(deniedPath).SetAccessControl(deniedAccess); changed = true;
                    phase = "VerifyRealDenial";
                    Assert.ThrowsExactly<UnauthorizedAccessException>(() => new DirectoryInfo(deniedPath).GetAccessControl(AccessControlSections.Access));
                }
            }
        }
        catch (Exception error)
        {
            TestContext.WriteLine($"StartupAccessDenialFailure: phase={phase}; type={error.GetType().Name}; hresult={error.HResult:X8}; " +
                $"nativeErrorCode={(error as System.ComponentModel.Win32Exception)?.NativeErrorCode}.");
            throw;
        }
        finally
        {
            if (changed && original is not null) RestoreStartupAccessDenialFixture(restorationHandle!, original);
            restorationHandle?.Dispose();
            registry.Unregister(paths.SyncRootPath);
            RestoreAutomaticStartupFixtureForCleanup(directory, paths.SyncRootPath, user.User!); DeleteFixture(directory);
        }
    }

    private static void RestoreStartupAccessDenialFixture(SafeFileHandle handle, DirectorySecurity original)
    {
        // A descriptor read from disk has no dirty sections. Explicitly set the
        // saved access section so Persist performs the actual restoration.
        var restored = new StartupAccessSecurity();
        restored.SetSecurityDescriptorSddlForm(original.GetSecurityDescriptorSddlForm(AccessControlSections.Access), AccessControlSections.Access);
        restored.Apply(handle);
    }

    private sealed class StartupAccessSecurity() : NativeObjectSecurity(true, ResourceType.FileObject)
    {
        public override Type AccessRightType => typeof(FileSystemRights);
        public override Type AccessRuleType => typeof(StartupAccessRule);
        public override Type AuditRuleType => typeof(StartupAuditRule);
        public override AccessRule AccessRuleFactory(IdentityReference identity, int mask, bool inherited,
            InheritanceFlags inheritance, PropagationFlags propagation, AccessControlType type) =>
            new StartupAccessRule(identity, mask, inherited, inheritance, propagation, type);
        public override AuditRule AuditRuleFactory(IdentityReference identity, int mask, bool inherited,
            InheritanceFlags inheritance, PropagationFlags propagation, AuditFlags flags) =>
            new StartupAuditRule(identity, mask, inherited, inheritance, propagation, flags);
        public void Apply(SafeFileHandle handle) => Persist(handle, AccessControlSections.Access);
    }
    private sealed class StartupAccessRule(IdentityReference identity, int mask, bool inherited,
        InheritanceFlags inheritance, PropagationFlags propagation, AccessControlType type)
        : AccessRule(identity, mask, inherited, inheritance, propagation, type);
    private sealed class StartupAuditRule(IdentityReference identity, int mask, bool inherited,
        InheritanceFlags inheritance, PropagationFlags propagation, AuditFlags flags)
        : AuditRule(identity, mask, inherited, inheritance, propagation, flags);
}
