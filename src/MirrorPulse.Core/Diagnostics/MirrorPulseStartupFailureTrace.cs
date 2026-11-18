using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MirrorPulse.Core.Configuration;

namespace MirrorPulse.Core.Diagnostics;

public enum MirrorPulseStartupFailureSite
{
    Unknown, InitializationRead, InitializationAdmission, InitializationInventory, InitializationAnchorProtection,
    InitializationAnchorRegistration, InitializationAudit, AnchorGuard, AnchorHistory, AnchorPermissionPreparation,
    AnchorProtectedPermission, AnchorVerification, AnchorPermissionPolicy, AnchorPermissionCatalog,
    LibraryStart, LibraryStoreOpen
}

public enum MirrorPulseStartupMissingResource
{
    Unclassified, StateDatabase, StateWal, StateSharedMemory, StateOwnerLock, CfSharpAssembly, SqliteAssembly, OtherAssembly, ComponentModelPrimitivesAssembly
}

/// <summary>Retains only the first closed startup call boundary without wrapping or changing the original exception.</summary>
public static partial class MirrorPulseStartupFailureTrace
{
    private sealed record Boundary(MirrorPulseStartupFailureSite Site);
    private sealed record RuntimeInformationRequester(string? NameSha256, string Context);
    private static readonly ConditionalWeakTable<Exception, Boundary> Boundaries = new();
    private static readonly ConditionalWeakTable<Exception, RuntimeInformationRequester> RuntimeInformationRequesters = new();

    public static void Record(Exception error, MirrorPulseStartupFailureSite site)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (!Enum.IsDefined(site)) throw new ArgumentOutOfRangeException(nameof(site));
        _ = Boundaries.GetValue(ReadOriginalException(error), _ => new(site));
    }

    public static MirrorPulseStartupFailureSite ReadSite(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return Boundaries.TryGetValue(ReadOriginalException(error), out var boundary) ? boundary.Site : MirrorPulseStartupFailureSite.Unknown;
    }

    public static string ReadKind(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return ReadOriginalException(error) switch
        {
            FileNotFoundException => "FileNotFound",
            DirectoryNotFoundException => "DirectoryNotFound",
            DllNotFoundException => "DllNotFound",
            Win32Exception => "Native",
            _ => "Other"
        };
    }

    public static MirrorPulseStartupMissingResource ReadMissingResource(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (ReadSite(error) != MirrorPulseStartupFailureSite.LibraryStoreOpen ||
            ReadOriginalException(error) is not FileNotFoundException { FileName: { Length: > 0 and <= 32768 } resource })
            return MirrorPulseStartupMissingResource.Unclassified;

        string name = Path.GetFileName(resource);
        string database = MirrorPulseStoragePaths.CfSharpDatabaseFileName;
        if (name.Equals(database, StringComparison.OrdinalIgnoreCase)) return MirrorPulseStartupMissingResource.StateDatabase;
        if (name.Equals(database + "-wal", StringComparison.OrdinalIgnoreCase)) return MirrorPulseStartupMissingResource.StateWal;
        if (name.Equals(database + "-shm", StringComparison.OrdinalIgnoreCase)) return MirrorPulseStartupMissingResource.StateSharedMemory;
        if (name.Equals(database + ".cfsharp.lock", StringComparison.OrdinalIgnoreCase)) return MirrorPulseStartupMissingResource.StateOwnerLock;
        int version = name.IndexOf(", Version=", StringComparison.OrdinalIgnoreCase);
        string assembly = version < 0 ? name : name[..version].TrimEnd();
        if (assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) assembly = assembly[..^4];
        return assembly switch
        {
            "CfSharp" or "CfSharp.Native" or "CfSharp.Storage.Sqlite" => MirrorPulseStartupMissingResource.CfSharpAssembly,
            "Microsoft.Data.Sqlite" or "SQLitePCLRaw.core" or "SQLitePCLRaw.provider.e_sqlite3" or
                "SQLitePCLRaw.batteries_v2" or "e_sqlite3" => MirrorPulseStartupMissingResource.SqliteAssembly,
            "System.ComponentModel.Primitives" => MirrorPulseStartupMissingResource.ComponentModelPrimitivesAssembly,
            _ when version >= 0 => MirrorPulseStartupMissingResource.OtherAssembly,
            _ => MirrorPulseStartupMissingResource.Unclassified
        };
    }

    [GeneratedRegex(@"\A(?<name>[A-Za-z0-9_.-]{1,96}), Version=[0-9]{1,5}(?:\.[0-9]{1,5}){3}, Culture=(?:neutral|[A-Za-z0-9-]{1,32}), PublicKeyToken=(?:null|[0-9a-fA-F]{16})\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex ManagedAssemblyIdentity();

    /// <summary>Correlates a CLR assembly name with the installed package manifest without logging a path or display name.</summary>
    public static string? ReadMissingAssemblyNameSha256(Exception error)
    {
        if (ReadSite(error) != MirrorPulseStartupFailureSite.LibraryStoreOpen ||
            ReadOriginalException(error) is not FileNotFoundException { FileName: { Length: > 0 and <= 192 } resource })
            return null;
        Match identity = ManagedAssemblyIdentity().Match(resource);
        if (!identity.Success) return null;
        return AssemblyNameSha256(identity.Groups["name"].Value);
    }

    /// <summary>Reads the requested public framework version and any matching requester observed during the failed store open.</summary>
    public static (string? Version, string? RequesterSha256, string? RequesterContext) ReadRuntimeInformationBinding(Exception error)
    {
        if (ReadSite(error) != MirrorPulseStartupFailureSite.LibraryStoreOpen ||
            ReadOriginalException(error) is not FileNotFoundException missing ||
            ReadRuntimeInformationVersion(missing.FileName) is not { } version)
            return default;
        RuntimeInformationRequesters.TryGetValue(missing, out var requester);
        return (version.ToString(), requester?.NameSha256, requester?.Context);
    }

    private static string AssemblyNameSha256(string name) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name.ToUpperInvariant())));

    private static Version? ReadRuntimeInformationVersion(string? identity)
    {
        const string prefix = "System.Runtime.InteropServices.RuntimeInformation, Version=";
        const string suffix = ", Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a";
        if (identity is null || identity.Length <= prefix.Length + suffix.Length || identity.Length > prefix.Length + suffix.Length + 23 ||
            !identity.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !identity.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return null;
        return ParseAssemblyVersion(identity.AsSpan(prefix.Length, identity.Length - prefix.Length - suffix.Length));
    }

    internal static Version? ParseAssemblyVersion(ReadOnlySpan<char> text) =>
        Version.TryParse(text, out var version) && version.Major <= ushort.MaxValue && version.Minor <= ushort.MaxValue &&
            version.Build is >= 0 and <= ushort.MaxValue && version.Revision is >= 0 and <= ushort.MaxValue ? version : null;

    /// <summary>Observes the one known framework bind while the official store opens, leaving resolution to the runtime.</summary>
    internal sealed class StoreAssemblyResolution : IDisposable
    {
        private sealed record Request(Version Version, Assembly? Requester);
        private readonly object _gate = new();
        private Request? _request;
        private bool _ambiguous;

        internal StoreAssemblyResolution() => AppDomain.CurrentDomain.AssemblyResolve += Observe;

        public void Dispose() => AppDomain.CurrentDomain.AssemblyResolve -= Observe;

        private Assembly? Observe(object? _, ResolveEventArgs args)
        {
            if (ReadRuntimeInformationVersion(args.Name) is { } version)
            {
                lock (_gate)
                {
                    if (_request is null) _request = new(version, args.RequestingAssembly);
                    else if (_request.Version != version || !ReferenceEquals(_request.Requester, args.RequestingAssembly)) _ambiguous = true;
                }
            }
            return null;
        }

        internal void RecordFailure(Exception error)
        {
            if (ReadOriginalException(error) is not FileNotFoundException missing ||
                ReadRuntimeInformationVersion(missing.FileName) is not { } version)
                return;
            Request request;
            lock (_gate)
            {
                if (_ambiguous || _request is null || _request.Version != version) return;
                request = _request;
            }
            try
            {
                var requester = request.Requester;
                var context = requester is null ? null : AssemblyLoadContext.GetLoadContext(requester);
                string? name = requester?.GetName().Name;
                string? fingerprint = name is { Length: > 0 and <= 96 } &&
                    name.All(value => char.IsAsciiLetterOrDigit(value) || value is '.' or '-' or '_') ? AssemblyNameSha256(name) : null;
                _ = RuntimeInformationRequesters.GetValue(missing, _ => new(fingerprint,
                    requester is null ? "NoRequester" : context is null ? "Unknown" : context == AssemblyLoadContext.Default ? "Default" : "Custom"));
            }
            catch (Exception diagnosticFailure) when (diagnosticFailure is not OutOfMemoryException)
            {
                // Preserve the original store-open failure when the loader cannot describe its requester.
            }
        }
    }

    /// <summary>Unwraps single-error cleanup chains while keeping multiple independent failures together.</summary>
    public static Exception ReadOriginalException(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        // AggregateException.GetBaseException stops at a non-aggregate wrapper.
        // Inspect each wrapper explicitly; never select one child of multiple failures.
        while (true)
        {
            if (error is AggregateException aggregate)
            {
                if (aggregate.InnerExceptions.Count != 1) return aggregate;
                error = aggregate.InnerExceptions[0];
            }
            else if (error.InnerException is { } inner) error = inner;
            else return error;
        }
    }
}
