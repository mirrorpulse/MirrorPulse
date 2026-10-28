using System.Security.AccessControl;
using MirrorPulse.Core.Diagnostics;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Classifies existing immutable rejection facts without granting protection or logging their values.</summary>
internal static class MirrorPulseRootLabelCancellationObjectInspection
{
    internal static MirrorPulseCancellationObjectObservation Observe(MirrorPulseCancellationObjectScope scope,
        MirrorPulseCancellationObjectOrigin origin, string verified, string observed, string executionRole)
    {
        var difference = MirrorPulseCancellationDescriptorDifference.Unknown;
        var role = MirrorPulseCancellationRoleReference.Unknown;
        try
        {
            if (verified.Length is 0 or > 65_536 || observed.Length is 0 or > 65_536) return new(scope, origin, difference, role);
            var original = new RawSecurityDescriptor(verified); var actual = new RawSecurityDescriptor(observed);
            if (original.DiscretionaryAcl is null || actual.DiscretionaryAcl is null) return new(scope, origin, difference, role);
            role = actual.DiscretionaryAcl.Cast<GenericAce>().OfType<KnownAce>().Any(ace => ace.SecurityIdentifier.Value == executionRole)
                ? MirrorPulseCancellationRoleReference.Present : MirrorPulseCancellationRoleReference.Absent;
            difference = Difference(verified, observed, original, actual);
        }
        catch (Exception observationFailure) when (observationFailure is not StackOverflowException and not OutOfMemoryException) { }
        return new(scope, origin, difference, role);
    }

    private static MirrorPulseCancellationDescriptorDifference Difference(string verified, string observed,
        RawSecurityDescriptor original, RawSecurityDescriptor actual)
    {
        if (verified == observed) return MirrorPulseCancellationDescriptorDifference.Same;
        if (MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(verified, observed))
            return MirrorPulseCancellationDescriptorDifference.CompletionOnly;
        bool flags = original.ControlFlags != actual.ControlFlags;
        var left = original.DiscretionaryAcl!; var right = actual.DiscretionaryAcl!;
        byte[] before = new byte[left.BinaryLength]; left.GetBinaryForm(before, 0);
        byte[] after = new byte[right.BinaryLength]; right.GetBinaryForm(after, 0);
        if (before.AsSpan().SequenceEqual(after)) return MirrorPulseCancellationDescriptorDifference.ControlFlags;
        if (flags) return MirrorPulseCancellationDescriptorDifference.MultipleOrOther;
        if (left.Count != right.Count) return MirrorPulseCancellationDescriptorDifference.EntryCount;
        bool principals = false, rights = false;
        for (int index = 0; index < left.Count; index++)
        {
            if (left[index] is not CommonAce first || right[index] is not CommonAce second ||
                first.AceType != second.AceType || first.AceQualifier != second.AceQualifier || first.IsCallback != second.IsCallback ||
                !first.GetOpaque().AsSpan().SequenceEqual(second.GetOpaque())) return MirrorPulseCancellationDescriptorDifference.MultipleOrOther;
            principals |= !first.SecurityIdentifier.Equals(second.SecurityIdentifier);
            rights |= first.AccessMask != second.AccessMask || first.AceFlags != second.AceFlags;
        }
        if (principals && !rights) return MirrorPulseCancellationDescriptorDifference.Principals;
        if (rights && !principals) return MirrorPulseCancellationDescriptorDifference.RightsOrAceFlags;
        return MirrorPulseCancellationDescriptorDifference.MultipleOrOther;
    }
}
