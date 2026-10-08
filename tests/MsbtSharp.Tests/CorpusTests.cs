using System.Collections.Concurrent;
using SarcSharp;
using ZsDicSharp;

namespace MsbtSharp.Tests;

// Runs over every message file in a Tears of the Kingdom romfs when TOTK_ROMFS points at one.
public class CorpusTests
{
    private static string? Root
        => Environment.GetEnvironmentVariable("TOTK_ROMFS") is { } root && Directory.Exists(Path.Combine(root, "Mals")) ? root : null;

    [Fact]
    public void EveryMessageFileRewritesAndRoundTripsThroughText()
    {
        if (Root is not { } root) return;

        using ZsDic zs = ZsDic.FromRomfs(root);
        ConcurrentBag<string> failures = [];
        int files = 0;

        Parallel.ForEach(Directory.EnumerateFiles(Path.Combine(root, "Mals"), "*.sarc.zs"), path =>
        {
            foreach (SarcEntry entry in SarcFile.FromBinary(zs.DecompressFile(path)).Entries)
            {
                if (!MsbtFile.IsMsbt(entry.Data)) continue;
                Interlocked.Increment(ref files);
                string name = $"{Path.GetFileName(path)[..4]}/{entry.Name}";

                try
                {
                    MsbtFile file = MsbtFile.FromBinary(entry.Data);
                    if (!file.ToBinary().AsSpan().SequenceEqual(entry.Data)) failures.Add($"{name}: rewrite differs");

                    string text = file.ToEditorText();
                    MsbtFile again = MsbtFile.FromBinary(entry.Data);
                    again.ApplyEditorText(text);
                    if (!again.ToBinary().AsSpan().SequenceEqual(entry.Data)) failures.Add($"{name}: text round trip differs");
                }
                catch (Exception e)
                {
                    failures.Add($"{name}: {e.GetType().Name}: {e.Message}");
                }
            }
        });

        Assert.True(files >= 20000, $"Expected the full corpus, found {files} MSBT files.");
        Assert.True(failures.IsEmpty, $"{failures.Count} failures:{Environment.NewLine}{string.Join(Environment.NewLine, failures.Order().Take(20))}");
    }

    [Fact]
    public void AlmostEveryTagIsShownByName()
    {
        if (Root is not { } root) return;

        using ZsDic zs = ZsDic.FromRomfs(root);
        string path = Directory.EnumerateFiles(Path.Combine(root, "Mals"), "USen*.sarc.zs").First();
        long named = 0, raw = 0;
        foreach (SarcEntry entry in SarcFile.FromBinary(zs.DecompressFile(path)).Entries.Where(e => MsbtFile.IsMsbt(e.Data)))
        {
            string text = MsbtFile.FromBinary(entry.Data).ToEditorText();
            named += text.Split("{{").Length - 1;
            raw += text.Split("{cmd:0xe").Length - 1;
        }

        Assert.True(named > 50000);
        Assert.True(raw * 100 < named, $"{raw} raw tags against {named} named.");
    }
}
