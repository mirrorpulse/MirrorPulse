using System.Security.Cryptography;

namespace MirrorPulse.Core.Workers;

/// <summary>A fresh 256-bit routing nonce per launch, compatible with the version-one Worker arguments.</summary>
public static class WorkerLaunchNonce
{
    public static string CreatePipeName() => "mirrorpulse-adapter-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
}
