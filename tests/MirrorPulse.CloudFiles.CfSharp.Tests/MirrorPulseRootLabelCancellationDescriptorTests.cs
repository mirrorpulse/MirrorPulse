using System.Security.AccessControl;
using System.Security.Principal;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
public sealed class MirrorPulseRootLabelCancellationDescriptorTests
{
    private const string Owner = "S-1-5-21-100-200-300-1001";
    private const string Role = "S-1-5-5-123-456";
    private static string Requested => new RawSecurityDescriptor($"D:P(A;OICI;FRFW;;;{Owner})(A;OICI;FA;;;{Role})").GetSddlForm(AccessControlSections.Access);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CancellationUsesTheVerifiedAndRequestedReadbackPolicies(bool completion)
    {
        string requested = Requested; string observed = completion ? Complete(requested) : requested;
        Assert.IsTrue(MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(requested, observed));
        Assert.IsTrue(MirrorPulseRootLabelCancellationDescriptor.MatchesVerified(requested, requested, observed));
    }

    [TestMethod]
    [DataRow("role")]
    [DataRow("owner-policy")]
    [DataRow("rights")]
    [DataRow("add-ace")]
    [DataRow("remove-protection")]
    [DataRow("add-request")]
    [DataRow("remove-completion")]
    [DataRow("requested-policy")]
    public void CancellationCannotAdoptDifferentRightsRolesOrControlFlags(string change)
    {
        string requested = Requested; string verified = requested; string observed = Complete(requested);
        if (change == "role") observed = observed.Replace(Role, "S-1-5-5-123-457", StringComparison.Ordinal);
        else if (change == "owner-policy") observed = observed.Replace(Owner, "S-1-5-21-100-200-300-1002", StringComparison.Ordinal);
        else if (change == "remove-completion") { verified = observed; observed = requested; }
        else if (change == "requested-policy") requested = requested.Replace(Role, "S-1-5-5-123-457", StringComparison.Ordinal);
        else
        {
            var descriptor = new RawSecurityDescriptor(observed);
            if (change == "rights") ((CommonAce)descriptor.DiscretionaryAcl![0]).AccessMask ^= 0x10;
            else if (change == "add-ace") descriptor.DiscretionaryAcl!.InsertAce(descriptor.DiscretionaryAcl.Count,
                new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, 0x120089, new SecurityIdentifier(WellKnownSidType.WorldSid, null), false, null));
            else descriptor.SetFlags(change == "remove-protection" ? descriptor.ControlFlags & ~ControlFlags.DiscretionaryAclProtected
                : descriptor.ControlFlags | ControlFlags.DiscretionaryAclAutoInheritRequired);
            observed = descriptor.GetSddlForm(AccessControlSections.Access);
        }
        Assert.IsFalse(MirrorPulseRootLabelCancellationDescriptor.MatchesVerified(requested, verified, observed));
    }

    private static string Complete(string value)
    {
        var descriptor = new RawSecurityDescriptor(value);
        descriptor.SetFlags(descriptor.ControlFlags | ControlFlags.DiscretionaryAclAutoInherited);
        return descriptor.GetSddlForm(AccessControlSections.Access);
    }
}
