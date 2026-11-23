namespace MirrorPulse.Core.Host;

/// <summary>A validation failure with a message that only uses declared field keys.</summary>
public sealed class AdapterConfigurationValidationException : ArgumentException
{
    internal AdapterConfigurationValidationException(string message) : base(message) { }
}
