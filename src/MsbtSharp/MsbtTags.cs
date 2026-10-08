using System.Globalization;
using YamlDotNet.RepresentationModel;

namespace MsbtSharp;

/// <summary>One argument of a tag. <see cref="DataType"/> is u8, u16, s16, bool or str.</summary>
public sealed record MsbtTagArgument(string Name, string DataType, IReadOnlyDictionary<long, string>? ValueMap);

public sealed record MsbtTagDefinition(string Name, ushort Group, ushort Type, IReadOnlyList<MsbtTagArgument> Arguments);

/// <summary>The named tags a game's messages use, from a game config (.gcf) file's <c>msbt.tags</c> list.</summary>
public sealed class MsbtTags
{
    private static readonly Lazy<MsbtTags> TotKTags = new(() =>
    {
        using Stream stream = typeof(MsbtTags).Assembly.GetManifestResourceStream("MsbtSharp.TotK.gcf")
            ?? throw new InvalidOperationException("MsbtSharp was built without its tag list.");
        using StreamReader reader = new(stream);
        return FromGcf(reader.ReadToEnd());
    });

    private readonly Dictionary<(ushort, ushort), MsbtTagDefinition> _byId = [];
    private readonly Dictionary<string, MsbtTagDefinition> _byName = new(StringComparer.Ordinal);

    /// <summary>The tags Tears of the Kingdom uses.</summary>
    public static MsbtTags TotK => TotKTags.Value;

    public IEnumerable<MsbtTagDefinition> All => _byId.Values;

    public MsbtTagDefinition? Find(ushort group, ushort type) => _byId.GetValueOrDefault((group, type));

    public MsbtTagDefinition? Find(string name) => _byName.GetValueOrDefault(name);

    public static MsbtTags FromGcfFile(string path) => FromGcf(File.ReadAllText(path));

    public static MsbtTags FromGcf(string yaml)
    {
        YamlStream stream = [];
        stream.Load(new StringReader(yaml));

        MsbtTags tags = new();
        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root) return tags;
        if (Child(root, "msbt") is not YamlMappingNode msbt || Child(msbt, "tags") is not YamlSequenceNode list) return tags;

        foreach (YamlNode node in list)
        {
            if (node is not YamlMappingNode tag) continue;
            string? name = Text(tag, "name");
            if (name is null || !ushort.TryParse(Text(tag, "group"), CultureInfo.InvariantCulture, out ushort group)
                             || !ushort.TryParse(Text(tag, "type"), CultureInfo.InvariantCulture, out ushort type)) continue;

            List<MsbtTagArgument> arguments = [];
            if (Child(tag, "arguments") is YamlSequenceNode args)
            {
                foreach (YamlNode argNode in args)
                {
                    if (argNode is not YamlMappingNode arg || Text(arg, "name") is not { } argName || Text(arg, "dataType") is not { } dataType) continue;

                    Dictionary<long, string>? map = null;
                    if (Child(arg, "valueMap") is YamlMappingNode values)
                    {
                        map = [];
                        foreach (var (key, value) in values.Children)
                            if (long.TryParse((key as YamlScalarNode)?.Value, CultureInfo.InvariantCulture, out long number) && (value as YamlScalarNode)?.Value is { } label)
                                map[number] = label;
                    }
                    arguments.Add(new MsbtTagArgument(argName, dataType, map));
                }
            }

            MsbtTagDefinition definition = new(name, group, type, arguments);
            tags._byId[(group, type)] = definition;
            tags._byName[name] = definition;
        }

        return tags;
    }

    private static YamlNode? Child(YamlMappingNode node, string key)
        => node.Children.TryGetValue(new YamlScalarNode(key), out YamlNode? child) ? child : null;

    private static string? Text(YamlMappingNode node, string key) => (Child(node, key) as YamlScalarNode)?.Value;
}
