using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("MirrorPulse.Core.Tests")]
[assembly: InternalsVisibleTo("MirrorPulse.CfSharp.CrashProbe")]
#if HOST_ATOMIC_DIRECTORY_CANDIDATE
[assembly: InternalsVisibleTo("MirrorPulse.CloudFiles.CfSharp")]
[assembly: InternalsVisibleTo("MirrorPulse.CloudFiles.CfSharp.Tests")]
#endif
