namespace MirrorPulse.App;

/// <summary>Validates the verifier's expected Host root without taking ownership of it.</summary>
internal static class MirrorPulseShellProbeHostRoot
{
    internal static string RequireExpectedRoot(string? expectedRoot)
    {
        if (string.IsNullOrWhiteSpace(expectedRoot) || !Path.IsPathFullyQualified(expectedRoot))
            throw new InvalidDataException("The Shell probe requires an absolute expected Host root.");
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedRoot));
        if (string.Equals(root, Path.TrimEndingDirectorySeparator(Path.GetPathRoot(root)!), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A volume root cannot be the Shell probe's Host root.");
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException("The expected Host root must already exist.");
        return root;
    }

    internal static void RequireRegistration(string expectedRoot, Guid expectedProvider, string registeredRoot, Guid registeredProvider)
    {
        if (!string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(registeredRoot)), expectedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The Shell probe registration does not match the expected Host root.");
        if (registeredProvider != expectedProvider)
            throw new InvalidOperationException($"The Shell probe registration does not match the Host provider: expected={expectedProvider:D}; actual={registeredProvider:D}.");
    }

    internal static async Task VerifyAndRestoreAsync(Func<Task> verify, Func<Task> restore)
    {
        ArgumentNullException.ThrowIfNull(verify);
        ArgumentNullException.ThrowIfNull(restore);
        try { await verify(); }
        catch (Exception original)
        {
            try { await restore(); }
            catch (Exception restoration)
            {
                throw new AggregateException("Shell verification and label restoration both failed.", original, restoration);
            }
            throw;
        }
        await restore();
    }
}
