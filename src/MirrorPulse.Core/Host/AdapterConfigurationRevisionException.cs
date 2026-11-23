namespace MirrorPulse.Core.Host;

/// <summary>The user must reread the deployment before applying an edit.</summary>
public sealed class AdapterConfigurationRevisionException()
    : InvalidOperationException("The Adapter configuration changed. Reload it before saving.");
