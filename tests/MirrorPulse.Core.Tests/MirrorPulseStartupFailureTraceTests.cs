using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Diagnostics;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseStartupFailureTraceTests
{
    [TestMethod]
    [DataRow(MirrorPulseStartupFailureSite.AnchorProtectedPermission, MirrorPulseStartupFailureSite.InitializationAnchorProtection)]
    [DataRow(MirrorPulseStartupFailureSite.LibraryStoreOpen, MirrorPulseStartupFailureSite.LibraryStart)]
    public void ClosestRecordedStartupBoundarySurvivesCleanupWrappersWithoutChangingTheOriginalFailure(
        MirrorPulseStartupFailureSite closest, MirrorPulseStartupFailureSite enclosing)
    {
        var original = new FileNotFoundException("PRIVATE_FAILURE_MESSAGE", "PRIVATE_FILENAME");
        int hresult = original.HResult;
        MirrorPulseStartupFailureTrace.Record(original, closest);
        var wrapper = new IOException("PRIVATE_WRAPPER_MESSAGE", original);
        MirrorPulseStartupFailureTrace.Record(wrapper, enclosing);
        var cleanup = new AggregateException(wrapper);
        Assert.AreSame(original, MirrorPulseStartupFailureTrace.ReadOriginalException(cleanup)); Assert.AreEqual(hresult, original.HResult);
        Assert.AreEqual("PRIVATE_FILENAME", original.FileName);
        Assert.AreEqual(closest, MirrorPulseStartupFailureTrace.ReadSite(cleanup));
        Assert.AreEqual("FileNotFound", MirrorPulseStartupFailureTrace.ReadKind(cleanup));
        var safe = SafeDiagnosticPolicy.Sanitize(new LogEntry(LogLevel.Error, "host", "HostStartupFailed", DateTimeOffset.UtcNow,
            [new("failureSite", MirrorPulseStartupFailureTrace.ReadSite(cleanup).ToString()),
             new("hresult", MirrorPulseStartupFailureTrace.ReadOriginalException(cleanup).HResult.ToString("X8", CultureInfo.InvariantCulture)),
             new("failureKind", MirrorPulseStartupFailureTrace.ReadKind(cleanup)), new("message", original.Message), new("path", original.FileName!)]));
        Assert.AreEqual(closest.ToString(), safe.Fields["failureSite"]); Assert.AreEqual("FileNotFound", safe.Fields["failureKind"]);
        Assert.AreEqual("80070002", safe.Fields["hresult"]);
        Assert.IsFalse(JsonSerializer.Serialize(safe).Contains("PRIVATE", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("file", "FileNotFound")]
    [DataRow("directory", "DirectoryNotFound")]
    [DataRow("dll", "DllNotFound")]
    [DataRow("native", "Native")]
    [DataRow("other", "Other")]
    public void StartupFailureKindsAreClosedAndUnrecordedFailuresRemainUnknown(string kind, string expected)
    {
        Exception error = kind switch
        {
            "file" => new FileNotFoundException("PRIVATE_MESSAGE", "PRIVATE_FILE"),
            "directory" => new DirectoryNotFoundException("PRIVATE_DIRECTORY"),
            "dll" => new DllNotFoundException("PRIVATE_LIBRARY"),
            "native" => new Win32Exception(2, "PRIVATE_NATIVE"),
            _ => new IOException("PRIVATE_OTHER")
        };
        Assert.AreEqual(expected, MirrorPulseStartupFailureTrace.ReadKind(new IOException("PRIVATE_WRAPPER", error)));
        Assert.AreEqual(MirrorPulseStartupFailureSite.Unknown, MirrorPulseStartupFailureTrace.ReadSite(error));
    }

    [TestMethod]
    [DataRow("C:\\PRIVATE_DIRECTORY\\cfsharp.db", MirrorPulseStartupMissingResource.StateDatabase)]
    [DataRow("C:\\PRIVATE_DIRECTORY\\cfsharp.db-wal", MirrorPulseStartupMissingResource.StateWal)]
    [DataRow("C:\\PRIVATE_DIRECTORY\\cfsharp.db-shm", MirrorPulseStartupMissingResource.StateSharedMemory)]
    [DataRow("C:\\PRIVATE_DIRECTORY\\cfsharp.db.cfsharp.lock", MirrorPulseStartupMissingResource.StateOwnerLock)]
    [DataRow("CfSharp.Storage.Sqlite, Version=0.1.0.0, Culture=neutral", MirrorPulseStartupMissingResource.CfSharpAssembly)]
    [DataRow("SQLitePCLRaw.core, Version=2.1.12.0, Culture=neutral", MirrorPulseStartupMissingResource.SqliteAssembly)]
    [DataRow("System.ComponentModel.Primitives, Version=10.0.0.0, Culture=neutral", MirrorPulseStartupMissingResource.ComponentModelPrimitivesAssembly)]
    [DataRow("PRIVATE_ASSEMBLY, Version=2.1.12.0, Culture=neutral", MirrorPulseStartupMissingResource.OtherAssembly)]
    [DataRow("C:\\PRIVATE_DIRECTORY\\private.db", MirrorPulseStartupMissingResource.Unclassified)]
    [DataRow(null, MirrorPulseStartupMissingResource.Unclassified)]
    public void StoreFailuresRetainOnlyAnApprovedResourceCategory(string? filename, MirrorPulseStartupMissingResource expected)
    {
        var original = new FileNotFoundException("PRIVATE_MESSAGE", filename);
        Assert.AreEqual(MirrorPulseStartupMissingResource.Unclassified, MirrorPulseStartupFailureTrace.ReadMissingResource(original));
        MirrorPulseStartupFailureTrace.Record(original, MirrorPulseStartupFailureSite.LibraryStoreOpen);
        var classified = MirrorPulseStartupFailureTrace.ReadMissingResource(new IOException("PRIVATE_WRAPPER", original));
        Assert.AreEqual(expected, classified);
        var safe = SafeDiagnosticPolicy.Sanitize(new LogEntry(LogLevel.Error, "host", "HostStartupFailed", DateTimeOffset.UtcNow,
            [new("failureResource", classified.ToString()), new("path", filename ?? "PRIVATE_NAME")]));
        Assert.AreEqual(expected.ToString(), safe.Fields["failureResource"]);
        Assert.IsFalse(JsonSerializer.Serialize(safe).Contains("PRIVATE", StringComparison.Ordinal));
    }

    [TestMethod]
    public void IndependentFailuresDoNotBorrowAnotherErrorsBoundaryFromAMultipleFailureAggregate()
    {
        var first = new FileNotFoundException("PRIVATE_FIRST"); var second = new IOException("PRIVATE_SECOND");
        MirrorPulseStartupFailureTrace.Record(first, MirrorPulseStartupFailureSite.AnchorHistory);
        var combined = new AggregateException(first, second);
        Assert.AreSame(combined, MirrorPulseStartupFailureTrace.ReadOriginalException(new IOException("PRIVATE_OUTER", combined)));
        Assert.AreEqual(MirrorPulseStartupFailureSite.Unknown, MirrorPulseStartupFailureTrace.ReadSite(combined));
        Assert.AreEqual("Other", MirrorPulseStartupFailureTrace.ReadKind(combined));
        Assert.AreSame(first, combined.InnerExceptions[0]); Assert.AreSame(second, combined.InnerExceptions[1]);
    }
}
