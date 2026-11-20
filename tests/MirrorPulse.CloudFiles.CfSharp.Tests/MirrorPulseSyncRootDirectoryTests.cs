using System.Security.AccessControl;
using System.Security.Principal;
using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
public sealed class MirrorPulseSyncRootDirectoryTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task RegistrationCreatesAUserOwnedRootAndPreservesExistingSecurityAndBytes(bool coordinator, bool existing)
    {
        string parent = Path.Combine(Path.GetTempPath(), "MirrorPulse-root-owner-tests", Guid.NewGuid().ToString("N"));
        using var user = WindowsIdentity.GetCurrent();
        var owner = user.User!;
        var parentSecurity = new DirectorySecurity(); parentSecurity.SetOwner(owner);
        parentSecurity.SetAccessRuleProtection(true, false);
        parentSecurity.AddAccessRule(new(owner, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(parent).Create(parentSecurity);
        string path = Path.Combine(parent, "sync"), note = Path.Combine(path, "original.bin");
        byte[] bytes = [3, 5, 7, 11]; string? original = null;
        DateTime created = default;
        try
        {
            if (existing)
            {
                Directory.CreateDirectory(path);
                await File.WriteAllBytesAsync(note, bytes);
                original = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)
                    .GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access);
                created = Directory.GetCreationTimeUtc(path);
            }
            var boundary = new InspectingRegistration(path, owner, original);
            var definition = new MirrorPulseSyncRootDefinition(path, "0.1.0", Guid.NewGuid(), [1, 2, 3]);
            if (coordinator)
            {
                var registration = new MirrorPulseSyncRootRegistrationCoordinator(boundary, boundary);
                await registration.EnsureRegisteredAsync(definition, MirrorPulseShellSyncRootRegistrar.CreateProfile(definition, owner.Value));
                Assert.AreEqual(3, boundary.Observations);
            }
            else
            {
                _ = new MirrorPulseSyncRootRegistrationService(boundary).Register(definition);
                Assert.AreEqual(1, boundary.Observations);
            }
            if (existing)
            {
                Assert.AreEqual(created, Directory.GetCreationTimeUtc(path));
                CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(note));
            }
            else
            {
                await File.WriteAllBytesAsync(note, bytes);
                CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(note));
            }
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    private sealed class InspectingRegistration(string path, SecurityIdentifier owner, string? original) :
        IMirrorPulseSyncRootRegistrar, IMirrorPulseShellRootRegistry, IMirrorPulseCloudRootRegistry
    {
        internal int Observations;
        private void Inspect()
        {
            var security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
            if (original is not null)
                Assert.AreEqual(original, security.GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access));
            else
            {
                Assert.AreEqual(owner, security.GetOwner(typeof(SecurityIdentifier)));
                Assert.IsFalse(security.AreAccessRulesProtected);
                Assert.IsTrue(security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
                    .Any(rule => rule.IsInherited && rule.IdentityReference.Equals(owner) &&
                        rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl));
            }
            Observations++;
        }
        public MirrorPulseSyncRootRegistrationResult Register(string root, SyncRootRegistrationOptions options)
        { Assert.AreEqual(path, root); Inspect(); return new(root, options.ProviderId, options.ProviderName, options.ProviderVersion); }
        public ValueTask<bool> RegisterAsync(MirrorPulseShellRegistrationProfile profile, CancellationToken token)
        { Assert.AreEqual(path, profile.SyncRootPath); Inspect(); return ValueTask.FromResult(false); }
        public void EnsureCompatible(MirrorPulseSyncRootDefinition definition) { Assert.AreEqual(path, definition.Path); Inspect(); }
        public void Register(MirrorPulseSyncRootDefinition definition) { Assert.AreEqual(path, definition.Path); Inspect(); }
        public void Unregister(string root) => throw new AssertFailedException("Root creation must not unregister.");
        public void Unregister(MirrorPulseShellRegistrationProfile profile) => throw new AssertFailedException("Root creation must not compensate a successful registration.");
    }
}
