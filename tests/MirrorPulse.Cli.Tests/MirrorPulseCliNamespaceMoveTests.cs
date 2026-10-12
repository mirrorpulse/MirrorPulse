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

    [TestMethod]
    public void NamespaceMemberRetirementParsesPreparationAndExecution()
    {
        Guid memberId = Guid.NewGuid(); Guid operationId = Guid.NewGuid();
        var prepare = MirrorPulse.Cli.MirrorPulseCliCommandLine.Parse(
            ["namespace", "member", "prepare-retire", memberId.ToString("D"), "--operation-id", operationId.ToString("D")]);
        Assert.IsTrue(prepare.Succeeded);
        Assert.AreEqual("namespace member prepare-retire", string.Join(' ', prepare.Command!.Path));
        var execute = MirrorPulse.Cli.MirrorPulseCliCommandLine.Parse(
            ["namespace", "member", "retire", operationId.ToString("D")]);
        Assert.IsTrue(execute.Succeeded);
        Assert.AreEqual("namespace member retire", string.Join(' ', execute.Command!.Path));
    }

    [TestMethod]
    public void NamespaceMemberRetirementStatusParsesAnOperationId()
    {
        Guid operationId = Guid.NewGuid();
        var parsed = MirrorPulse.Cli.MirrorPulseCliCommandLine.Parse(
            ["namespace", "member", "retirement-status", operationId.ToString("D")]);

        Assert.IsTrue(parsed.Succeeded);
        Assert.IsNotNull(parsed.Command);
        Assert.AreEqual("namespace member retirement-status", string.Join(' ', parsed.Command!.Path));
        Assert.AreEqual(operationId.ToString("D"), string.Join(' ', parsed.Command.Arguments));
    }

    [TestMethod]
    public void StagedBirthRecoveryParsesAnActionIdAndRejectsAMoveVerb()
    {
        Guid recoveryId = Guid.NewGuid();
        var parsed = MirrorPulse.Cli.MirrorPulseCliCommandLine.Parse(["namespace", "birth", "recover", recoveryId.ToString("D")]);
        Assert.IsTrue(parsed.Succeeded);
        Assert.IsNotNull(parsed.Command);
        Assert.AreEqual("namespace birth recover", parsed.Command.Name);
        Assert.AreEqual(recoveryId.ToString("D"), string.Join(' ', parsed.Command.Arguments));
        var wrong = MirrorPulse.Cli.MirrorPulseCliCommandLine.Parse(["namespace", "birth", "move", recoveryId.ToString("D")]);
        Assert.IsFalse(wrong.Succeeded);
    }
}
