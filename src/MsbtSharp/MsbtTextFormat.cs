using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MsbtSharp;

// The text form of a message file, one line per labelled message:
//
//   Label: text with \n escapes, {{tagName arg="value"}} for known tags and {cmd:0xe:group:type:0xHEX} for the rest
//
// A tag's parameter bytes follow the layout its definition gives (see Decode).
//
// A value is written in double quotes when it holds a colon or #, is empty, or has outer whitespace or a bare
// quote. A tag is written by name only when decoding and re-encoding it gives back the same bytes, so the text of
// an unedited file always rebuilds the original.
internal static partial class MsbtTextFormat
{
    public static string Write(MsbtFile file, MsbtTags tags)
    {
        StringBuilder text = new();
        foreach (MsbtMessage message in file.Messages.Where(m => m.Label is not null).OrderBy(m => m.Label, StringComparer.Ordinal))
            text.Append(message.Label).Append(": ").Append(FormatValue(Display(file, message, tags))).Append('\n');
        return text.ToString();
    }

    public static void Apply(MsbtFile file, string text, MsbtTags tags, bool addNewLabels)
    {
        Dictionary<string, MsbtMessage> byLabel = [];
        foreach (MsbtMessage message in file.Messages)
            if (message.Label is { } label) byLabel[label] = message;

        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        for (int number = 0; number < lines.Length; number++)
        {
            string line = lines[number];
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;

            int colon = line.IndexOf(':');
            if (colon < 0) throw new FormatException($"Line {number + 1} has no 'label: text' form.");

            string label = line[..colon].Trim();
            List<MsbtRun> runs;
            try
            {
                runs = Parse(ParseValue(line[(colon + 1)..]), file, tags);
            }
            catch (FormatException e)
            {
                throw new FormatException($"Line {number + 1} ({label}): {e.Message}", e);
            }

            if (!byLabel.TryGetValue(label, out MsbtMessage? target))
            {
                if (!addNewLabels) throw new FormatException($"Unknown MSBT label: {label}");
                target = new MsbtMessage { Label = label, Attribute = file.AttributeSize > 0 ? new byte[file.AttributeSize] : null, Style = file.Messages.Any(m => m.Style is not null) ? 0u : null };
                file.Messages.Add(target);
                byLabel[label] = target;
            }

            target.Runs.Clear();
            target.Runs.AddRange(runs);
        }
    }

    // ---- writing ----------------------------------------------------------

    private static string Display(MsbtFile file, MsbtMessage message, MsbtTags tags)
    {
        StringBuilder text = new();
        foreach (MsbtRun run in message.Runs)
        {
            switch (run)
            {
                case MsbtText t: text.Append(Escape(t.Text)); break;
                case MsbtTag tag: text.Append(TagToText(file, tag, tags)); break;
                case MsbtTagEnd end: text.Append($"{{cmd:0xf:{end.Group}:{end.Type}:}}"); break;
            }
        }
        return text.ToString();
    }

    private static string Escape(string text)
        => text.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t").Replace("{", "\\{");

    private static string Raw(MsbtTag tag)
        => $"{{cmd:0xe:{tag.Group}:{tag.Type}:{(tag.Params.Length == 0 ? "" : "0x" + Convert.ToHexStringLower(tag.Params))}}}";

    private static string TagToText(MsbtFile file, MsbtTag tag, MsbtTags tags)
    {
        if (tags.Find(tag.Group, tag.Type) is not { } definition) return Raw(tag);
        if (file.Encoding is not MsbtEncoding.Utf16 && definition.Arguments.Any(a => a.DataType == "str")) return Raw(tag);

        // Only a form that rebuilds the same bytes may stand in for them. The padding byte at the end is either padding
        // or part of the trailing bytes; both read back the same, so the first that does is used.
        bool couldBePadded = tag.Params.Length > 0 && tag.Params[^1] == Pad && tag.Params.Length % 2 == 0;
        foreach (bool padded in couldBePadded ? new[] { true, false } : new[] { false })
        {
            string? content = Decode(definition, tag.Params, file.BigEndian, padded);
            if (content is null || content.IndexOfAny(['\n', '\r', '}']) >= 0) continue;

            try
            {
                var (group, type, bytes) = EncodeTag(content, tags, file.BigEndian);
                if (group == tag.Group && type == tag.Type && bytes.AsSpan().SequenceEqual(tag.Params)) return "{{" + content + "}}";
            }
            catch (FormatException)
            {
            }
        }
        return Raw(tag);
    }

    // Parameter bytes to the text of a tag's arguments, or null if they don't fit the definition. The layout: u8 and
    // bool are one byte, u16 and s16 two, a str is a 16-bit byte length and then UTF-16 characters, and the whole
    // block is padded to an even length with 0xCD.
    private static string? Decode(MsbtTagDefinition definition, byte[] data, bool big, bool padded)
    {
        List<string> parts = [definition.Name];
        int offset = 0;

        foreach (MsbtTagArgument arg in definition.Arguments)
        {
            object value;
            switch (arg.DataType)
            {
                case "u8" when offset + 1 <= data.Length:
                    value = (long)data[offset];
                    offset += 1;
                    break;
                case "bool" when offset + 1 <= data.Length:
                    value = data[offset] != 0;
                    offset += 1;
                    break;
                case "u16" when offset + 2 <= data.Length:
                    value = (long)U16(data, offset, big);
                    offset += 2;
                    break;
                case "s16" when offset + 2 <= data.Length:
                    value = (long)(short)U16(data, offset, big);
                    offset += 2;
                    break;
                case "str" when offset + 2 <= data.Length:
                    int length = U16(data, offset, big);
                    if (length % 2 != 0 || offset + 2 + length > data.Length) return null;
                    StringBuilder text = new();
                    for (int i = 0; i < length; i += 2) text.Append((char)U16(data, offset + 2 + i, big));
                    value = text.ToString();
                    offset += 2 + length;
                    break;
                default:
                    return null;
            }

            string shown;
            if (arg.ValueMap is { } map && value is long or bool && map.TryGetValue(value is bool flag ? (flag ? 1 : 0) : (long)value, out string? name)) shown = name;
            else shown = value switch { bool b => b ? "true" : "false", long l => l.ToString(CultureInfo.InvariantCulture), _ => (string)value };
            parts.Add($"{arg.Name}=\"{shown}\"");
        }

        // Whatever the definition doesn't account for, less the padding byte, is shown as hex.
        int end = padded ? data.Length - 1 : data.Length;
        if (end < offset) return null;
        if (end > offset) parts.Add($"{ExtraName}=\"{Convert.ToHexString(data.AsSpan(offset, end - offset))}\"");
        return string.Join(' ', parts);
    }

    private const byte Pad = 0xCD;
    private const string ExtraName = "extra";

    private static ushort U16(byte[] data, int at, bool big)
        => big ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at)) : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at));

    // Quotes are only there so the value reads back as typed: nothing, outer whitespace, or a leading quote would be lost.
    private static string FormatValue(string display)
    {
        bool needsQuotes = display.Length == 0 || display != display.Trim() || display[0] == '"';
        return needsQuotes ? "\"" + display.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"" : display;
    }

    // ---- reading ----------------------------------------------------------

    // The text after "label:" with its quotes, if it has them, taken off.
    private static string ParseValue(string raw)
    {
        string value = raw.Trim();
        if (value.Length < 2 || value[0] != '"' || value[^1] != '"') return value;

        StringBuilder text = new();
        string inner = value[1..^1];
        for (int i = 0; i < inner.Length; i++)
        {
            if (inner[i] == '\\' && i + 1 < inner.Length && inner[i + 1] is '\\' or '"') i++;
            text.Append(inner[i]);
        }
        return text.ToString();
    }

    private static List<MsbtRun> Parse(string display, MsbtFile file, MsbtTags tags)
    {
        List<MsbtRun> runs = [];
        int position = 0;

        void AddText(string chunk)
        {
            string text = Unescape(chunk);
            if (text.Length > 0) runs.Add(new MsbtText(text));
        }

        while (position < display.Length)
        {
            Match match = Command().Match(display, position);
            while (match.Success && EscapedAt(display, match.Index)) match = Command().Match(display, match.Index + 1);
            if (!match.Success)
            {
                AddText(display[position..]);
                break;
            }

            AddText(display[position..match.Index]);

            if (match.Groups[5].Success)
            {
                var (group, type, bytes) = EncodeTag(match.Groups[5].Value, tags, file.BigEndian);
                runs.Add(new MsbtTag(group, type, bytes));
            }
            else
            {
                ushort group = ushort.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                ushort type = ushort.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
                string hex = match.Groups[4].Value.Replace("0x", "").Replace(" ", "");
                if (hex.Length % 2 != 0) throw new FormatException($"The data of {match.Value} has an odd number of hex digits.");
                byte[] bytes = Convert.FromHexString(hex);

                string kind = match.Groups[1].Value.ToLowerInvariant().Replace("0x", "");
                runs.Add(kind switch
                {
                    "e" => new MsbtTag(group, type, bytes),
                    "f" when bytes.Length == 0 => new MsbtTagEnd(group, type),
                    _ => throw new FormatException($"{match.Value} must start {{cmd:0xe: or {{cmd:0xf:."),
                });
            }

            position = match.Index + match.Length;
        }

        return runs;
    }

    // A brace after an odd number of backslashes is escaped text, not the start of a tag.
    private static bool EscapedAt(string text, int index)
    {
        int slashes = 0;
        while (index - 1 - slashes >= 0 && text[index - 1 - slashes] == '\\') slashes++;
        return slashes % 2 == 1;
    }

    private static string Unescape(string text)
    {
        StringBuilder result = new(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                char next = text[i + 1];
                char? mapped = next switch { 'n' => '\n', 'r' => '\r', 't' => '\t', '\\' => '\\', '{' => '{', _ => null };
                if (mapped is { } c)
                {
                    result.Append(c);
                    i++;
                    continue;
                }
            }
            result.Append(text[i]);
        }
        return result.ToString();
    }

    // "name arg=value ..." to the group, type and parameter bytes of the tag.
    private static (ushort Group, ushort Type, byte[] Params) EncodeTag(string content, MsbtTags tags, bool big)
    {
        string[] parts = Split(content);
        if (parts.Length == 0) throw new FormatException("An empty {{ }} tag.");
        if (tags.Find(parts[0]) is not { } definition) throw new FormatException($"Unknown tag {parts[0]}. Use {{cmd:0xe:group:type:0xHEX}} for a tag without a definition.");

        Dictionary<string, string> given = [];
        foreach (string part in parts.Skip(1))
        {
            int equals = part.IndexOf('=');
            if (equals > 0) given[part[..equals]] = part[(equals + 1)..];
        }

        List<byte> bytes = [];
        void Put16(ushort value)
        {
            Span<byte> b = stackalloc byte[2];
            if (big) BinaryPrimitives.WriteUInt16BigEndian(b, value); else BinaryPrimitives.WriteUInt16LittleEndian(b, value);
            bytes.AddRange(b.ToArray());
        }

        foreach (MsbtTagArgument arg in definition.Arguments)
        {
            given.TryGetValue(arg.Name, out string? text);

            if (arg.DataType == "str")
            {
                text ??= "";
                Put16((ushort)(text.Length * 2));
                foreach (char c in text) Put16(c);
                continue;
            }

            long value = 0;
            if (text is not null)
            {
                if (arg.ValueMap?.FirstOrDefault(e => e.Value == text) is { Value: not null } named) value = named.Key;
                else if (arg.DataType == "bool") value = text.Equals("true", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                else if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value))
                    throw new FormatException($"{definition.Name}: {arg.Name}=\"{text}\" is not a number.");
            }

            switch (arg.DataType)
            {
                case "u8":
                    if (value is < 0 or > 255) throw new FormatException($"{definition.Name}: {arg.Name} must be 0 to 255.");
                    bytes.Add((byte)value);
                    break;
                case "u16":
                    if (value is < 0 or > ushort.MaxValue) throw new FormatException($"{definition.Name}: {arg.Name} must be 0 to 65535.");
                    Put16((ushort)value);
                    break;
                case "s16":
                    if (value is < short.MinValue or > short.MaxValue) throw new FormatException($"{definition.Name}: {arg.Name} must be -32768 to 32767.");
                    Put16(unchecked((ushort)(short)value));
                    break;
                case "bool":
                    bytes.Add((byte)(value != 0 ? 1 : 0));
                    break;
                default:
                    throw new FormatException($"{definition.Name}: unknown argument type {arg.DataType}.");
            }
        }

        if (given.TryGetValue(ExtraName, out string? extra) && definition.Arguments.All(a => a.Name != ExtraName))
        {
            if (extra.Length % 2 != 0 || !extra.All(Uri.IsHexDigit)) throw new FormatException($"{definition.Name}: extra=\"{extra}\" is not hex bytes.");
            bytes.AddRange(Convert.FromHexString(extra));
        }

        if (bytes.Count % 2 != 0) bytes.Add(Pad);

        return (definition.Group, definition.Type, [.. bytes]);
    }

    // Splits on spaces, keeping what is inside double quotes together and taking the quotes off.
    private static string[] Split(string content)
    {
        List<string> parts = [];
        StringBuilder current = new();
        bool quoted = false, any = false;
        foreach (char c in content)
        {
            if (c == '"')
            {
                quoted = !quoted;
                any = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any || current.Length > 0) parts.Add(current.ToString());
                current.Clear();
                any = false;
            }
            else
            {
                current.Append(c);
            }
        }
        if (any || current.Length > 0) parts.Add(current.ToString());
        return [.. parts];
    }

    [GeneratedRegex(@"\{cmd:([^:}]+):(\d+):(\d+):([^}]*)\}|\{\{([^}]+)\}\}")]
    private static partial Regex Command();
}
