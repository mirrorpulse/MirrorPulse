using System.Security.AccessControl;
using MirrorPulse.Core.Diagnostics;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
public sealed class MirrorPulseRootLabelCancellationObjectInspectionTests
{
    private const string Owner = "S-1-5-21-100-200-300-1001";
    private const string Role = "S-1-5-5-123-456";
    private const string Current = "S-1-5-5-123-457";
    private static string Original => new RawSecurityDescriptor($"D:P(A;OICI;FRFW;;;{Owner})(A;OICI;FA;;;{Role})").GetSddlForm(AccessControlSections.Access);

    [TestMethod]
    [DataRow("same", MirrorPulseCancellationDescriptorDifference.Same, MirrorPulseCancellationRoleReference.Absent)]
    [DataRow("completion", MirrorPulseCancellationDescriptorDifference.CompletionOnly, MirrorPulseCancellationRoleReference.Absent)]
    [DataRow("flags", MirrorPulseCancellationDescriptorDifference.ControlFlags, MirrorPulseCancellationRoleReference.Absent)]
    [DataRow("principal", MirrorPulseCancellationDescriptorDifference.Principals, MirrorPulseCancellationRoleReference.Present)]
    [DataRow("rights", MirrorPulseCancellationDescriptorDifference.RightsOrAceFlags, MirrorPulseCancellationRoleReference.Absent)]
    [DataRow("ace-flags", MirrorPulseCancellationDescriptorDifference.RightsOrAceFlags, MirrorPulseCancellationRoleReference.Absent)]
    [DataRow("count", MirrorPulseCancellationDescriptorDifference.EntryCount, MirrorPulseCancellationRoleReference.Absent)]
    [DataRow("multiple", MirrorPulseCancellationDescriptorDifference.MultipleOrOther, MirrorPulseCancellationRoleReference.Present)]
    [DataRow("malformed", MirrorPulseCancellationDescriptorDifference.Unknown, MirrorPulseCancellationRoleReference.Unknown)]
    [DataRow("oversized", MirrorPulseCancellationDescriptorDifference.Unknown, MirrorPulseCancellationRoleReference.Unknown)]
    public void ImmutableDescriptorDifferencesProduceOnlyClosedClasses(string change, MirrorPulseCancellationDescriptorDifference difference,
        MirrorPulseCancellationRoleReference role)
    {
        string before = Original; var descriptor = new RawSecurityDescriptor(before);
        if (change == "completion") descriptor.SetFlags(descriptor.ControlFlags | ControlFlags.DiscretionaryAclAutoInherited);
        if (change == "flags") descriptor.SetFlags(descriptor.ControlFlags & ~ControlFlags.DiscretionaryAclProtected);
        if (change == "rights") ((CommonAce)descriptor.DiscretionaryAcl![0]).AccessMask ^= 0x10;
        if (change == "ace-flags") descriptor.DiscretionaryAcl![0].AceFlags ^= AceFlags.ObjectInherit;
        if (change == "count") descriptor.DiscretionaryAcl!.RemoveAce(0);
        string observed = change switch
        {
            "principal" => before.Replace(Role, Current, StringComparison.Ordinal),
            "multiple" => descriptor.GetSddlForm(AccessControlSections.Access).Replace(Role, Current, StringComparison.Ordinal).Replace("D:P", "D:PAI", StringComparison.Ordinal),
            "malformed" => "PRIVATE_DACL PRIVATE_SECRET",
            "oversized" => new string('X', 65_537),
            _ => descriptor.GetSddlForm(AccessControlSections.Access)
        };
        var actual = MirrorPulseRootLabelCancellationObjectInspection.Observe(MirrorPulseCancellationObjectScope.Root,
            MirrorPulseCancellationObjectOrigin.ProtectedBirth, before, observed, Current);
        Assert.AreEqual(difference, actual.Difference); Assert.AreEqual(role, actual.CurrentExecutionRoleReference);
        Assert.AreEqual(MirrorPulseCancellationObjectScope.Root, actual.Scope);
        Assert.AreEqual(MirrorPulseCancellationObjectOrigin.ProtectedBirth, actual.Origin);
    }
}
