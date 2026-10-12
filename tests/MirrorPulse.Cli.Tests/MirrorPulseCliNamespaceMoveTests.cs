namespace MirrorPulse.Cli.Tests;

[TestClass]
public sealed class MirrorPulseCliNamespaceMoveTests
{
    [TestMethod]
    public void NamespaceMemberMoveParsesAStableOperationId()
    {
        Guid operationId = Guid.NewGuid();
        var parsed = MirrorPulse.Cli.MirrorPulseCliCommandLine.Parse(
            ["namespace", "member", "move", operationId.ToString("D")]);

        Assert.IsTrue(parsed.Succeeded);
        Assert.IsNotNull(parsed.Command);
        Assert.AreEqual("namespace member move", string.Join(' ', parsed.Command!.Path));
        Assert.AreEqual(operationId.ToString("D"), string.Join(' ', parsed.Command.Arguments));
    }

    [TestMethod]
    public void NamespaceMemberRequiresTheMoveVerb()
    {
        var parsed = MirrorPulse.Cli.MirrorPulseCliCommandLine.Parse(["namespace", "member", "delete", Guid.NewGuid().ToString("D")]);

        Assert.IsFalse(parsed.Succeeded);
        StringAssert.Contains(parsed.Error!, "Unknown subcommand");
    }
}
