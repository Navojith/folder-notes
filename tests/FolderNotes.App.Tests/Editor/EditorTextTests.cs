using FolderNotes.App.Editor;

namespace FolderNotes.App.Tests.Editor;

/// <summary>05 § Editor behaviour: the body is stored with LF line breaks, whatever the TextBox reports.</summary>
public sealed class EditorTextTests
{
    [Theory]
    [InlineData("a\rb", "a\nb")]
    [InlineData("a\r\nb", "a\nb")]
    [InlineData("a\nb", "a\nb")]
    [InlineData("a\r\rb\r", "a\n\nb\n")]
    [InlineData("", "")]
    public void REQ007_LineBreaks_AreStoredAsLf(string input, string expected) =>
        EditorText.NormalizeNewlines(input).ShouldBe(expected);
}
