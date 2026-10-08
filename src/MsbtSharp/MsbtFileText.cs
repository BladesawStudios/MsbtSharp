namespace MsbtSharp;

public sealed partial class MsbtFile
{
    /// <summary>
    /// The messages as editable text, one <c>Label: text</c> line each, sorted by label. Tags with a definition in
    /// <paramref name="tags"/> (Tears of the Kingdom's by default) are written by name, the rest as raw
    /// <c>{cmd:0xe:group:type:0xHEX}</c>, so <see cref="ApplyEditorText"/> on the result rebuilds the same messages.
    /// </summary>
    public string ToEditorText(MsbtTags? tags = null) => MsbtTextFormat.Write(this, tags ?? MsbtTags.TotK);

    /// <summary>
    /// Replaces the contents of the messages named in <paramref name="text"/>. Messages not mentioned are left alone,
    /// and so are their attributes and styles. A label the file doesn't have is an error unless
    /// <paramref name="addNewLabels"/> is set, which appends it. Throws <see cref="FormatException"/> with the line
    /// number for text it can't read.
    /// </summary>
    public void ApplyEditorText(string text, MsbtTags? tags = null, bool addNewLabels = false)
        => MsbtTextFormat.Apply(this, text, tags ?? MsbtTags.TotK, addNewLabels);
}
