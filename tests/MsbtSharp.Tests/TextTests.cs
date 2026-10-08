namespace MsbtSharp.Tests;

public class TextTests
{
    private static MsbtFile File(params (string Label, MsbtRun[] Runs)[] messages)
    {
        MsbtFile file = new() { LabelSlotCount = 11 };
        file.SectionOrder.AddRange(["LBL1", "TXT2"]);
        foreach (var (label, runs) in messages)
        {
            MsbtMessage message = new() { Label = label };
            message.Runs.AddRange(runs);
            file.Messages.Add(message);
        }
        return file;
    }

    private static MsbtFile Reread(MsbtFile file) => MsbtFile.FromBinary(file.ToBinary());

    private static MsbtFile ApplyTo(MsbtFile original, string text)
    {
        MsbtFile copy = MsbtFile.FromBinary(original.ToBinary());
        copy.ApplyEditorText(text);
        return copy;
    }

    [Fact]
    public void AFileWritesAndReadsBack()
    {
        MsbtFile file = File(("B", [new MsbtText("two")]), ("A", [new MsbtText("one"), new MsbtTag(3, 0, [8, 0]), new MsbtText("!")]));

        MsbtFile back = Reread(file);

        Assert.Equal(["B", "A"], back.Messages.Select(m => m.Label));
        Assert.Equal("one!", back.Messages[1].PlainText);
        Assert.Equal(file.ToBinary(), back.ToBinary());
    }

    [Theory]
    [InlineData("plain text")]
    [InlineData("two\nlines")]
    [InlineData("tab\there and \\ backslash")]
    [InlineData("a { brace and {{not a tag}} and {cmd:0xe:1:1:}")]
    [InlineData("colon: and # hash")]
    [InlineData("  leading and trailing  ")]
    [InlineData("\"quoted\"")]
    [InlineData("ends with a quote\"")]
    [InlineData("")]
    public void TextSurvivesTheEditorForm(string text)
    {
        MsbtFile file = File(("Label", text.Length == 0 ? [] : [new MsbtText(text)]));

        MsbtFile back = ApplyTo(file, file.ToEditorText());

        Assert.Equal(text, back.Messages[0].PlainText);
        Assert.Equal(file.ToBinary(), back.ToBinary());
    }

    [Fact]
    public void KnownTagsAreWrittenByNameAndReadBack()
    {
        // setEmotion: a u8 with names, then a bool, both one byte. setVoice: a length-prefixed UTF-16 string.
        MsbtFile file = File(("T", [
            new MsbtTag(3, 0, [8, 0]),
            new MsbtTag(4, 0, [6, 0, (byte)'A', 0, (byte)'b', 0, (byte)'c', 0]),
            new MsbtText("Hello"),
        ]));

        string text = file.ToEditorText();

        Assert.Equal("T: {{setEmotion emotion=\"Pleasure\" noVoice=\"false\"}}{{setVoice asset=\"Abc\"}}Hello\n", text);
        Assert.Equal(file.ToBinary(), ApplyTo(file, text).ToBinary());
    }

    [Fact]
    public void ATagWithAnOddNumberOfParameterBytesIsPaddedWithCd()
    {
        // icon: one u8, so the parameter block is that byte and a padding byte.
        MsbtFile file = File(("T", [new MsbtTag(1, 4, [0x19, 0xCD])]));

        string text = file.ToEditorText();

        Assert.StartsWith("T: {{icon type=", text);
        Assert.Equal(file.ToBinary(), ApplyTo(file, text).ToBinary());
    }

    [Fact]
    public void UnexplainedTrailingBytesAreShownAndKept()
    {
        MsbtFile file = File(("T", [new MsbtTag(1, 3, [0x1E, 0, 0, 0])]));

        string text = file.ToEditorText();

        Assert.Contains("{{playSound id=\"30\" extra=\"0000\"}}", text);
        Assert.Equal(file.ToBinary(), ApplyTo(file, text).ToBinary());
    }

    [Fact]
    public void TagsWithoutADefinitionAndEndTagsStayRaw()
    {
        MsbtFile file = File(("T", [new MsbtTag(77, 5, [1, 2, 3]), new MsbtText("x"), new MsbtTagEnd(77, 5)]));

        string text = file.ToEditorText();

        Assert.Equal("T: {cmd:0xe:77:5:0x010203}x{cmd:0xf:77:5:}\n", text);
        Assert.Equal(file.ToBinary(), ApplyTo(file, text).ToBinary());
    }

    [Fact]
    public void AnEditChangesOnlyThatMessage()
    {
        MsbtFile file = File(("A", [new MsbtText("one")]), ("B", [new MsbtText("two")]));

        MsbtFile edited = ApplyTo(file, "B: {{setEmotion emotion=\"Angry\" noVoice=\"true\"}}changed\n");

        Assert.Equal("one", edited.Messages[0].PlainText);
        Assert.Equal("changed", edited.Messages[1].PlainText);
        Assert.IsType<MsbtTag>(edited.Messages[1].Runs[0]);
    }

    [Fact]
    public void CommentsAndBlankLinesAreIgnored()
    {
        MsbtFile file = File(("A", [new MsbtText("one")]));
        Assert.Equal("one", ApplyTo(file, "# a note\n\n  \nA: one\n").Messages[0].PlainText);
    }

    [Fact]
    public void ErrorsNameTheLine()
    {
        MsbtFile file = File(("A", [new MsbtText("one")]));

        var unknownLabel = Assert.Throws<FormatException>(() => file.ApplyEditorText("Nope: x\n"));
        Assert.Contains("Nope", unknownLabel.Message);

        var unknownTag = Assert.Throws<FormatException>(() => file.ApplyEditorText("A: ok\nA: {{notATag}}\n"));
        Assert.Contains("Line 2", unknownTag.Message);
        Assert.Contains("notATag", unknownTag.Message);

        var noColon = Assert.Throws<FormatException>(() => file.ApplyEditorText("just words\n"));
        Assert.Contains("Line 1", noColon.Message);

        Assert.Throws<FormatException>(() => file.ApplyEditorText("A: {{color id=\"99999\"}}\n"));
    }

    [Fact]
    public void ANewLabelCanBeAddedOnRequest()
    {
        MsbtFile file = File(("A", [new MsbtText("one")]));

        file.ApplyEditorText("New_Label: fresh\n", addNewLabels: true);
        MsbtFile back = Reread(file);

        Assert.Equal(["A", "New_Label"], back.Messages.Select(m => m.Label));
        Assert.Equal("fresh", back.Messages[1].PlainText);
    }

    [Fact]
    public void ADefinitionFileCanBeLoaded()
    {
        MsbtTags tags = MsbtTags.FromGcf("""
            msbt:
              tags:
                - name: shout
                  group: 9
                  type: 1
                  arguments:
                    - name: volume
                      dataType: s16
                      valueMap:
                        -1: Quiet
                        1: Loud
            """);

        MsbtFile file = File(("T", [new MsbtTag(9, 1, [0xFF, 0xFF])]));

        Assert.Equal("T: {{shout volume=\"Quiet\"}}\n", file.ToEditorText(tags));

        MsbtFile back = MsbtFile.FromBinary(file.ToBinary());
        back.ApplyEditorText(file.ToEditorText(tags), tags);
        Assert.Equal(file.ToBinary(), back.ToBinary());
    }
}
