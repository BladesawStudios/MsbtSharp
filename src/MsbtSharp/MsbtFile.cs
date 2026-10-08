using System.Buffers.Binary;
using System.Text;

namespace MsbtSharp;

public enum MsbtEncoding : byte { Utf8, Utf16, Utf32 }

public abstract record MsbtRun;
public sealed record MsbtText(string Text) : MsbtRun;
public sealed record MsbtTag(ushort Group, ushort Type, byte[] Params) : MsbtRun;
public sealed record MsbtTagEnd(ushort Group, ushort Type) : MsbtRun;

public sealed class MsbtMessage
{
    public string? Label { get; set; }
    public byte[]? Attribute { get; set; }
    public uint? Style { get; set; }
    public List<MsbtRun> Runs { get; } = [];
    public byte[] Trailing { get; set; } = [];

    public string PlainText => string.Concat(Runs.OfType<MsbtText>().Select(t => t.Text));
}

public sealed record MsbtSection(string Magic, byte[] Data);

public sealed partial class MsbtFile
{
    public bool BigEndian { get; set; }
    public byte Version { get; set; } = 3;
    public MsbtEncoding Encoding { get; set; } = MsbtEncoding.Utf16;
    public ushort HeaderReserved { get; set; }
    public byte[] HeaderPadding { get; set; } = new byte[10];
    public List<string> SectionOrder { get; } = [];
    public uint LabelSlotCount { get; set; }
    public int AttributeCount { get; set; }
    public int AttributeSize { get; set; }
    public byte[] AttributePool { get; set; } = [];
    public SortedDictionary<uint, int>? NumberLines { get; set; }
    public List<MsbtMessage> Messages { get; } = [];
    public List<MsbtSection> UnknownSections { get; } = [];

    public static bool IsMsbt(ReadOnlySpan<byte> data) => data.Length >= 0x20 && data[..8].SequenceEqual("MsgStdBn"u8);

    public static MsbtFile FromBinary(ReadOnlySpan<byte> data)
    {
        if (!IsMsbt(data)) throw new InvalidDataException("Not an MSBT file.");
        bool big = data[8] == 0xFE;
        var r = new R(data, big);
        var f = new MsbtFile
        {
            BigEndian = big, HeaderReserved = r.U16(0xA), Encoding = (MsbtEncoding)data[0xC], Version = data[0xD],
            HeaderPadding = data.Slice(0x16, 10).ToArray()
        };
        int count = r.U16(0xE);
        var sections = new Dictionary<string, (int Start, int Size)>();
        int pos = 0x20;
        for (int i = 0; i < count; i++)
        {
            string magic = System.Text.Encoding.ASCII.GetString(data.Slice(pos, 4));
            int size = (int)r.U32(pos + 4);
            f.SectionOrder.Add(magic);
            sections[magic] = (pos + 0x10, size);
            if (magic is not ("LBL1" or "ATR1" or "TXT2" or "NLI1" or "TSY1"))
                f.UnknownSections.Add(new MsbtSection(magic, data.Slice(pos + 0x10, size).ToArray()));
            pos = (pos + 0x10 + size + 15) & ~15;
        }

        if (!sections.TryGetValue("TXT2", out var txt)) throw new InvalidDataException("MSBT has no TXT2 section.");
        int n = (int)r.U32(txt.Start);
        for (int i = 0; i < n; i++)
        {
            int start = txt.Start + (int)r.U32(txt.Start + 4 + i * 4);
            int end = i + 1 < n ? txt.Start + (int)r.U32(txt.Start + 8 + i * 4) : txt.Start + txt.Size;
            f.Messages.Add(f.ReadMessage(data.Slice(start, end - start), big));
        }

        if (sections.TryGetValue("LBL1", out var lbl))
        {
            f.LabelSlotCount = r.U32(lbl.Start);
            for (int s = 0; s < f.LabelSlotCount; s++)
            {
                int cnt = (int)r.U32(lbl.Start + 4 + s * 8);
                int p = lbl.Start + (int)r.U32(lbl.Start + 8 + s * 8);
                for (int j = 0; j < cnt; j++)
                {
                    int len = data[p];
                    string name = System.Text.Encoding.UTF8.GetString(data.Slice(p + 1, len));
                    int idx = (int)r.U32(p + 1 + len);
                    p += 5 + len;
                    if (idx >= f.Messages.Count) throw new InvalidDataException($"Label {name} points past the message table.");
                    f.Messages[idx].Label = name;
                }
            }
        }

        if (sections.TryGetValue("ATR1", out var atr))
        {
            int cnt = (int)r.U32(atr.Start);
            f.AttributeCount = cnt;
            f.AttributeSize = (int)r.U32(atr.Start + 4);
            int table = atr.Start + 8;
            for (int i = 0; i < Math.Min(cnt, f.Messages.Count); i++)
                f.Messages[i].Attribute = data.Slice(table + i * f.AttributeSize, f.AttributeSize).ToArray();
            int poolStart = 8 + cnt * f.AttributeSize;
            f.AttributePool = poolStart < atr.Size ? data.Slice(atr.Start + poolStart, atr.Size - poolStart).ToArray() : [];
        }

        if (sections.TryGetValue("TSY1", out var tsy))
            for (int i = 0; i < Math.Min(tsy.Size / 4, f.Messages.Count); i++)
                f.Messages[i].Style = r.U32(tsy.Start + i * 4);

        if (sections.TryGetValue("NLI1", out var nli))
        {
            f.NumberLines = [];
            int cnt = (int)r.U32(nli.Start);
            for (int i = 0; i < cnt; i++)
                f.NumberLines[r.U32(nli.Start + 4 + i * 8)] = (int)r.U32(nli.Start + 8 + i * 8);
        }
        return f;
    }

    public string? AttributeString(MsbtMessage m)
    {
        if (m.Attribute is not { Length: 4 } a || AttributePool.Length == 0) return null;
        int off = (int)(BigEndian ? BinaryPrimitives.ReadUInt32BigEndian(a) : BinaryPrimitives.ReadUInt32LittleEndian(a));
        int poolBase = 8 + AttributeCount * 4;
        int at = off - poolBase;
        if (at < 0 || at > AttributePool.Length) return null;
        return ReadString(AttributePool.AsSpan(at), out _);
    }

    string ReadString(ReadOnlySpan<byte> d, out int consumed)
    {
        int unit = Encoding switch { MsbtEncoding.Utf8 => 1, MsbtEncoding.Utf16 => 2, _ => 4 };
        int end = 0;
        while (end + unit <= d.Length && Unit(d, end, unit) != 0) end += unit;
        consumed = Math.Min(d.Length, end + unit);
        return Decode(d[..end]);
    }

    uint Unit(ReadOnlySpan<byte> d, int at, int unit) => unit switch
    {
        1 => d[at],
        2 => BigEndian ? BinaryPrimitives.ReadUInt16BigEndian(d[at..]) : BinaryPrimitives.ReadUInt16LittleEndian(d[at..]),
        _ => BigEndian ? BinaryPrimitives.ReadUInt32BigEndian(d[at..]) : BinaryPrimitives.ReadUInt32LittleEndian(d[at..])
    };

    string Decode(ReadOnlySpan<byte> d) => Encoding switch
    {
        MsbtEncoding.Utf8 => System.Text.Encoding.UTF8.GetString(d),
        MsbtEncoding.Utf16 => (BigEndian ? System.Text.Encoding.BigEndianUnicode : System.Text.Encoding.Unicode).GetString(d),
        _ => new UTF32Encoding(BigEndian, false).GetString(d)
    };

    MsbtMessage ReadMessage(ReadOnlySpan<byte> d, bool big)
    {
        var m = new MsbtMessage();
        int unit = Encoding switch { MsbtEncoding.Utf8 => 1, MsbtEncoding.Utf16 => 2, _ => 4 };
        var r = new R(d, big);
        int p = 0, textStart = 0;
        void Flush(MsbtMessage msg, int to, ReadOnlySpan<byte> src)
        {
            if (to > textStart) msg.Runs.Add(new MsbtText(Decode(src[textStart..to])));
        }
        while (p + unit <= d.Length)
        {
            uint c = Unit(d, p, unit);
            if (c == 0) { Flush(m, p, d); p += unit; textStart = p; m.Trailing = d[p..].ToArray(); return m; }
            if (c == 0x0E)
            {
                Flush(m, p, d);
                int q = p + unit;
                ushort g = r.U16(q), t = r.U16(q + 2), sz = r.U16(q + 4);
                m.Runs.Add(new MsbtTag(g, t, d.Slice(q + 6, sz).ToArray()));
                p = q + 6 + sz;
                textStart = p;
                continue;
            }
            if (c == 0x0F)
            {
                Flush(m, p, d);
                int q = p + unit;
                m.Runs.Add(new MsbtTagEnd(r.U16(q), r.U16(q + 2)));
                p = q + 4;
                textStart = p;
                continue;
            }
            p += unit;
        }
        Flush(m, p, d);
        return m;
    }

    readonly ref struct R(ReadOnlySpan<byte> d, bool big)
    {
        readonly ReadOnlySpan<byte> _d = d;
        public ushort U16(int at) => big ? BinaryPrimitives.ReadUInt16BigEndian(_d[at..]) : BinaryPrimitives.ReadUInt16LittleEndian(_d[at..]);
        public uint U32(int at) => big ? BinaryPrimitives.ReadUInt32BigEndian(_d[at..]) : BinaryPrimitives.ReadUInt32LittleEndian(_d[at..]);
    }
}
