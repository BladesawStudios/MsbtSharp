# MsbtSharp

A C# reader and writer for **MSBT**, the message files (`MsgStdBn`) Nintendo titles keep their dialogue and
UI text in, with an editable text form and the *Tears of the Kingdom* tag definitions.

```csharp
MsbtFile file = MsbtFile.FromBinary(bytes);

string text = file.ToEditorText();                 // one "Label: text" line per message
file.ApplyEditorText(edited);                      // replaces those messages
byte[] rebuilt = file.ToBinary();                  // an unedited file writes back the same bytes
```

## The text form

```
Talk_001: {{setEmotion emotion="Pleasure" noVoice="false"}}{{setVoice asset="Humming"}}Hello!\nHow are you?
Buff_Name: Attack Up +{{number14 ref="SimpleIntForMessage2" index="-1" isDirect="true"}}
Odd_One: {cmd:0xe:77:5:0x010203}raw bytes for a tag nobody has named
```

- One line per labelled message, sorted by label. Blank lines and lines starting with `#` are ignored.
- In the text, `\n`, `\r`, `\t`, `\\` and `\{` are escapes. A value is wrapped in quotes only when it is empty, has
  leading or trailing whitespace, or starts with a quote; inside quotes `\\` and `\"` are the escapes.
- `{{name arg="value" ...}}` is a tag with a definition. Arguments with a value map show the name of the value.
  Bytes the definition doesn't account for show as `extra="HEX"`.
- `{cmd:0xe:group:type:0xHEX}` is a tag as raw bytes, and `{cmd:0xf:group:type:}` the end of one. A tag is only shown
  by name when decoding and re-encoding it gives back exactly its bytes, so the text of an unedited file always
  rebuilds the original.
- `ApplyEditorText` changes only the messages named in the text. A label the file doesn't have is an error unless
  `addNewLabels` is set. Errors are `FormatException`s that give the line.

## Tag parameters

A tag's parameter block is laid out as the definition lists its arguments: `u8` and `bool` take one byte, `u16` and
`s16` two, and a `str` is a 16-bit byte length followed by UTF-16 characters. The block is padded to an even length
with `0xCD`. The definitions are `MsbtTags.TotK`, from the game config file (`TotK.gcf`) that ships with TKVSC; any
other file with an `msbt.tags` list loads with `MsbtTags.FromGcfFile`.

## Checked against

All 21,140 message files in the Tears of the Kingdom dump (14 languages) rewrite to the same bytes, and go through
the text form and back to the same bytes. Every tag that has a definition shows by name (1,792,494 of them); the 368
without one (group 1, types 1 and 2) and the 39 end tags show as raw bytes.

## Limits

- Files with attributes (ATR1), styles (TSY1) or number lines (NLI1) are read and written, but none of the game's
  files use them, so that path is only tested on synthetic data.
- Named tags need UTF-16 files. A tag with a string argument in a UTF-8 or UTF-32 file stays raw.
- The label table's slot count is kept from the file read. A new file gets 101.
- Only TotK's tags are defined. BotW's message files read and write, but their tags show as raw bytes.

## Build

```bash
dotnet build MsbtSharp.sln -c Release
```

## Licence

AGPL-3.0-or-later. See [license.md](license.md).
