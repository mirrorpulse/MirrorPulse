using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace MirrorPulse.Core.Transport;

/// <summary>
/// Builds a Named Pipe security descriptor that grants access only to the current user.
/// </summary>
public static class CurrentUserPipeSecurity
{
    public static PipeSecurity Create()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException("The current Windows identity has no security identifier.");

        var security = new PipeSecurity();
        security.SetAccessRule(new PipeAccessRule(
            user,
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance | PipeAccessRights.Synchronize,
            AccessControlType.Allow));
        security.SetOwner(user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        return security;
    }

    /// <summary>Checks the actual user SID, including when the token's default owner is a group.</summary>
    public static void ValidateOwner(NamedPipeClientStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new UnauthorizedAccessException("The current Windows user could not be verified.");
        if (pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) != user)
        {
            throw new UnauthorizedAccessException("The pipe is not owned by the current Windows user.");
        }
    }
}

public static class SecureNamedPipeServerFactory
{
    public static NamedPipeServerStream Create(NamedPipeServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        return NamedPipeServerStreamAcl.Create(
            options.PipeName,
            PipeDirection.InOut,
            options.MaxInstances,
            options.TransmissionMode,
            options.PipeOptions,
            options.InBufferSize,
            options.OutBufferSize,
            CurrentUserPipeSecurity.Create());
    }
}
