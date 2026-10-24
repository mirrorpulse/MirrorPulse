using System.Text.Json.Nodes;

namespace MirrorPulse.Core.Tests;

public sealed partial class CliTransferEvidencePolicyTests
{
    [TestMethod]
    [DataRow(1, false)]
    [DataRow(1, true)]
    [DataRow(2, false)]
    [DataRow(2, true)]
    [DataRow(3, false)]
    [DataRow(3, true)]
    public async Task ActualProducerDictionariesRetainTwoOriginalCasesAndOneReceiptPerDirection(int schema, bool liveReceipts)
    {
        var result = await ExerciseAsync(DictionaryEvidence(schema), requireSingleRoot: schema != 1,
            liveDictionaries: true, liveReceiptDictionaries: liveReceipts);
        Assert.AreEqual(0, result.ExitCode, result.Output + result.Error);
    }

    [TestMethod]
    [DataRow(1, false)]
    [DataRow(1, true)]
    [DataRow(2, false)]
    [DataRow(2, true)]
    [DataRow(3, false)]
    [DataRow(3, true)]
    public async Task ProducerDictionariesCannotHideAMissingOrDuplicatedOriginalReceipt(int schema, bool duplicate)
    {
        var evidence = DictionaryEvidence(schema);
        var receipts = evidence["audit"]!["transfers"]!.AsArray();
        if (duplicate) receipts.Add(receipts[0]!.DeepClone());
        else receipts.RemoveAt(1);
        var result = await ExerciseAsync(evidence, requireSingleRoot: schema != 1,
            liveDictionaries: true, liveReceiptDictionaries: true);
        Assert.AreEqual(1, result.ExitCode, result.Output + result.Error);
    }

    private static JsonObject DictionaryEvidence(int schema) => schema switch
    {
        1 => CompleteEvidence(),
        2 => SingleRootEvidence(),
        3 => CrossPackageEvidence(),
        _ => throw new ArgumentOutOfRangeException(nameof(schema))
    };
}
