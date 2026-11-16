using System.Reflection;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
public sealed class CfSharpPublicationProvenanceTests
{
    [TestMethod]
    [DataRow("CfSharp")]
    [DataRow("CfSharp.Native")]
    [DataRow("CfSharp.Storage.Sqlite")]
    public void ActualRuntimePublicationMatchesCurrentCatalogEvidence(string assemblyName)
    {
        string expected = MirrorPulseRemoteLibraryProvenance.CurrentPackageVersion + "+" + MirrorPulseRemoteLibraryProvenance.CurrentSourceCommit;
        var assembly = Assembly.Load(new AssemblyName(assemblyName));
        Assert.AreEqual(expected, assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            "New protocol evidence must identify the actual loaded SDK publication.");
    }
}
