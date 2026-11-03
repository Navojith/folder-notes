using FolderNotes.App.Explorer;

namespace FolderNotes.App.Tests.Explorer;

/// <summary>04 § ExplorerTracker design B.5 (REQ-031, NFR-01). The table is the spec; these pin it.</summary>
[Trait("Kind", "Deterministic")]
public class ComFailureClassifierTests
{
    [Theory]
    [InlineData(0x80010001u)] // RPC_E_CALL_REJECTED
    [InlineData(0x8001010Au)] // RPC_E_SERVERCALL_RETRYLATER
    [InlineData(0x80010002u)] // RPC_E_CALL_CANCELED
    public void REQ031_Transient_codes_keep_the_entry(uint hr) =>
        ComFailureClassifier.Classify(unchecked((int)hr)).ShouldBe(ComFailureClass.Transient);

    [Theory]
    [InlineData(0x80010108u)] // RPC_E_DISCONNECTED
    [InlineData(0x800401FDu)] // CO_E_OBJNOTCONNECTED
    [InlineData(0x80010007u)] // RPC_E_SERVER_DIED
    [InlineData(0x80010012u)] // RPC_E_SERVER_DIED_DNE
    [InlineData(0x800706BAu)] // RPC_S_SERVER_UNAVAILABLE
    [InlineData(0x800706BEu)] // RPC_S_CALL_FAILED
    [InlineData(0x80010114u)] // RPC_E_INVALID_OBJECT
    public void REQ031_Gone_codes_drop_the_entry(uint hr) =>
        ComFailureClassifier.Classify(unchecked((int)hr)).ShouldBe(ComFailureClass.Gone);

    [Theory]
    [InlineData(0x80004005u)] // E_FAIL
    [InlineData(0x80004002u)] // E_NOINTERFACE
    [InlineData(0x80004001u)] // E_NOTIMPL
    [InlineData(0x8000FFFFu)] // E_UNEXPECTED
    [InlineData(0x80070057u)] // E_INVALIDARG: not in the table
    [InlineData(0x8001010Du)] // RPC_E_CANTCALLOUT_ININPUTSYNCCALL: not in the table
    public void REQ031_Other_and_unknown_codes_are_Other(uint hr) =>
        ComFailureClassifier.Classify(unchecked((int)hr)).ShouldBe(ComFailureClass.Other);

    [Fact]
    public void REQ031_E_FAIL_for_a_closed_window_is_Gone()
    {
        // S5/P1: a closed window's objects return E_FAIL, not a disconnect code.
        ComFailureClassifier.Classify(unchecked((int)0x80004005), windowAlive: false).ShouldBe(ComFailureClass.Gone);
        ComFailureClassifier.Classify(unchecked((int)0x80004005), windowAlive: true).ShouldBe(ComFailureClass.Other);
    }

    [Fact]
    public void REQ031_windowAlive_does_not_change_other_codes()
    {
        ComFailureClassifier.Classify(unchecked((int)0x80010002), windowAlive: false).ShouldBe(ComFailureClass.Transient);
        ComFailureClassifier.Classify(unchecked((int)0x80010108), windowAlive: false).ShouldBe(ComFailureClass.Gone);
    }

    [Theory]
    [InlineData(0)]  // S_OK
    [InlineData(1)]  // S_FALSE (Item out of range, S5/P1): a result, not a failure
    [InlineData(int.MaxValue)]
    public void REQ031_Success_codes_are_not_failures(int hr) =>
        ComFailureClassifier.Classify(hr).ShouldBe(ComFailureClass.None);
}
