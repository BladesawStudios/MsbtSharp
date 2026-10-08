using System.Buffers.Binary;
using System.Text;

namespace MsbtSharp;

public sealed partial class MsbtFile
{
    private const byte SectionPadding = 0xAB;
    private const uint DefaultLabelSlots = 101;

    /// <summary>The file as bytes. A file read from binary and not changed writes back the same bytes.</summary>
    public byte[] ToBinary()
    {
        List<string> order = [.. SectionOrder];
        if (Messages.Count > 0 && !order.Contains("TXT2")) order.Add("TXT2");
        if (Messages.Any(m => m.Label is not null) && !order.Contains("LBL1")) order.Insert(0, "LBL1");

        List<byte> output = new(0x20);
        output.AddRange(new byte[0x20]);

        foreach (string magic in order)
        {
            byte[] data = magic switch
            {
                "LBL1" => BuildLabels(),
                "ATR1" => BuildAttributes(),
                "TXT2" => BuildText(),
                "NLI1" => BuildNumberLines(),
                "TSY1" => BuildStyles(),
                _ => UnknownSections.FirstOrDefault(s => s.Magic == magic)?.Data ?? [],
            };

            output.AddRange(System.Text.Encoding.ASCII.GetBytes(magic));
            AddU32(output, (uint)data.Length);
            output.AddRange(new byte[8]);
            output.AddRange(data);
            while (output.Count % 16 != 0) output.Add(SectionPadding);
        }

        byte[] file = [.. output];
        "MsgStdBn"u8.CopyTo(file);
        file[8] = BigEndian ? (byte)0xFE : (byte)0xFF;
        file[9] = BigEndian ? (byte)0xFF : (byte)0xFE;
        Put(file, 0xA, HeaderReserved);
        file[0xC] = (byte)Encoding;
        file[0xD] = Version;
        Put(file, 0xE, (ushort)order.Count);
        Put(file, 0x12, (uint)file.Length);
        HeaderPadding.AsSpan(0, Math.Min(10, HeaderPadding.Length)).CopyTo(file.AsSpan(0x16));
        return file;
    }

    private byte[] BuildLabels()
    {
        uint slots = LabelSlotCount == 0 ? DefaultLabelSlots : LabelSlotCount;
        List<(string Name, int Index)>[] buckets = new List<(string, int)>[slots];
        for (int i = 0; i < buckets.Length; i++) buckets[i] = [];

        for (int i = 0; i < Messages.Count; i++)
            if (Messages[i].Label is { } label) buckets[LabelHash(label, slots)].Add((label, i));

        byte[] table = new byte[4 + slots * 8];
        Put(table, 0, slots);
        List<byte> entries = [];
        int start = 4 + (int)slots * 8;
        for (int s = 0; s < slots; s++)
        {
            Put(table, 4 + s * 8, (uint)buckets[s].Count);
            Put(table, 8 + s * 8, (uint)(start + entries.Count));
            foreach ((string name, int index) in buckets[s])
            {
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(name);
                entries.Add((byte)bytes.Length);
                entries.AddRange(bytes);
                AddU32(entries, (uint)index);
            }
        }

        return [.. table, .. entries];
    }

    internal static int LabelHash(string label, uint slots)
    {
        uint hash = 0;
        foreach (byte b in System.Text.Encoding.UTF8.GetBytes(label)) hash = unchecked(hash * 0x492 + b);
        return (int)(hash % slots);
    }

    private byte[] BuildAttributes()
    {
        int count = Messages.Count;
        int size = AttributeSize;
        List<byte> data = [];
        AddU32(data, (uint)count);
        AddU32(data, (uint)size);

        // With 4-byte attributes that point into the string pool, adding or removing messages moves the pool.
        int shift = size == 4 && AttributePool.Length > 0 ? (8 + count * 4) - (8 + AttributeCount * 4) : 0;
        foreach (MsbtMessage message in Messages)
        {
            byte[] attribute = message.Attribute ?? new byte[size];
            if (attribute.Length != size) Array.Resize(ref attribute, size);
            if (shift != 0 && size == 4)
            {
                attribute = (byte[])attribute.Clone();
                uint offset = BigEndian ? BinaryPrimitives.ReadUInt32BigEndian(attribute) : BinaryPrimitives.ReadUInt32LittleEndian(attribute);
                Put(attribute, 0, unchecked(offset + (uint)shift));
            }
            data.AddRange(attribute);
        }
        data.AddRange(AttributePool);
        return [.. data];
    }

    private byte[] BuildText()
    {
        int count = Messages.Count;
        List<byte> strings = [];
        List<byte> data = [];
        AddU32(data, (uint)count);
        int start = 4 + count * 4;
        foreach (MsbtMessage message in Messages)
        {
            AddU32(data, (uint)(start + strings.Count));
            strings.AddRange(EncodeMessage(message));
        }
        data.AddRange(strings);
        return [.. data];
    }

    private byte[] EncodeMessage(MsbtMessage message)
    {
        int unit = Encoding switch { MsbtEncoding.Utf8 => 1, MsbtEncoding.Utf16 => 2, _ => 4 };
        List<byte> bytes = [];

        void Unit(uint value)
        {
            byte[] buffer = new byte[4];
            switch (unit)
            {
                case 1: buffer[0] = (byte)value; break;
                case 2: Put(buffer, 0, (ushort)value); break;
                default: Put(buffer, 0, value); break;
            }
            bytes.AddRange(buffer.AsSpan(0, unit).ToArray());
        }

        foreach (MsbtRun run in message.Runs)
        {
            switch (run)
            {
                case MsbtText text:
                    bytes.AddRange(EncodeText(text.Text));
                    break;
                case MsbtTag tag:
                    Unit(0x0E);
                    AddU16(bytes, tag.Group);
                    AddU16(bytes, tag.Type);
                    AddU16(bytes, (ushort)tag.Params.Length);
                    bytes.AddRange(tag.Params);
                    break;
                case MsbtTagEnd end:
                    Unit(0x0F);
                    AddU16(bytes, end.Group);
                    AddU16(bytes, end.Type);
                    break;
            }
        }

        Unit(0);
        bytes.AddRange(message.Trailing);
        return [.. bytes];
    }

    private byte[] EncodeText(string text) => Encoding switch
    {
        MsbtEncoding.Utf8 => System.Text.Encoding.UTF8.GetBytes(text),
        MsbtEncoding.Utf16 => (BigEndian ? System.Text.Encoding.BigEndianUnicode : System.Text.Encoding.Unicode).GetBytes(text),
        _ => new UTF32Encoding(BigEndian, false).GetBytes(text),
    };

    private byte[] BuildNumberLines()
    {
        List<byte> data = [];
        var lines = NumberLines ?? [];
        AddU32(data, (uint)lines.Count);
        foreach (var (key, value) in lines)
        {
            AddU32(data, key);
            AddU32(data, (uint)value);
        }
        return [.. data];
    }

    private byte[] BuildStyles()
    {
        List<byte> data = [];
        foreach (MsbtMessage message in Messages) AddU32(data, message.Style ?? 0);
        return [.. data];
    }

    private void AddU16(List<byte> to, ushort value)
    {
        byte[] b = new byte[2];
        Put(b, 0, value);
        to.AddRange(b);
    }

    private void AddU32(List<byte> to, uint value)
    {
        byte[] b = new byte[4];
        Put(b, 0, value);
        to.AddRange(b);
    }

    private void Put(byte[] at, int offset, ushort value)
    {
        if (BigEndian) BinaryPrimitives.WriteUInt16BigEndian(at.AsSpan(offset), value);
        else BinaryPrimitives.WriteUInt16LittleEndian(at.AsSpan(offset), value);
    }

    private void Put(byte[] at, int offset, uint value)
    {
        if (BigEndian) BinaryPrimitives.WriteUInt32BigEndian(at.AsSpan(offset), value);
        else BinaryPrimitives.WriteUInt32LittleEndian(at.AsSpan(offset), value);
    }
}
