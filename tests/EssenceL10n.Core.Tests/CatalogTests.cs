using EssenceL10n.Core;
using Xunit;

namespace EssenceL10n.Core.Tests;

public class CatalogTests
{
    private static TextUnit Unit(string asm, string res, string key, string source, string? value) => new()
    {
        Assembly = asm,
        Resource = res,
        Key = key,
        Source = source,
        Value = value,
        State = value is null ? UnitState.Missing : UnitState.Translated,
    };

    [Fact]
    public void ExactMatchWins()
    {
        var catalog = new TranslationCatalog { Language = "zh-Hans" };
        catalog.Add(Unit("A", "A.Resources.Text.resources", "K", "Open", "打开"));
        catalog.Add(Unit("A", "A.Resources.Text.resources", "K2", "Open", "开启"));

        var match = catalog.Resolve("A", "A.Resources.Text.resources", "K", "Open");
        Assert.Equal(MatchKind.Exact, match.Kind);
        Assert.Equal("打开", match.Value);
    }

    [Fact]
    public void ResourceRenamedStillMatchesByKeyInsideAssembly()
    {
        var catalog = new TranslationCatalog { Language = "zh-Hans" };
        catalog.Add(Unit("A", "A.Resources.Text.resources", "K", "Open", "打开"));

        var match = catalog.Resolve("A", "A.Resources.Text2.resources", "K", "Open");
        Assert.Equal(MatchKind.KeyInAssembly, match.Kind);
        Assert.Equal("打开", match.Value);
    }

    [Fact]
    public void CrossAssemblyKeyIsOptIn()
    {
        var catalog = new TranslationCatalog { Language = "zh-Hans" };
        catalog.Add(Unit("A", "A.Resources.Text.resources", "LocString.DisplayName", "LocString", "本地化字符串"));

        Assert.Equal(MatchKind.None, catalog.Resolve("B", "B.Resources.Text.resources", "LocString.DisplayName", "locstring").Kind);

        var optedIn = catalog.Resolve("B", "B.Resources.Text.resources", "LocString.DisplayName", "LocString", allowGlobalKey: true);
        Assert.Equal(MatchKind.KeyGlobal, optedIn.Kind);
    }

    [Fact]
    public void ChangedSourceFallsBackToHashOfNewText()
    {
        var catalog = new TranslationCatalog { Language = "zh-Hans" };
        catalog.Add(Unit("A", "A.Resources.Text.resources", "OldKey", "Deleted {0} items.", "已删除 {0} 项。"));

        var match = catalog.Resolve("A", "A.Resources.Text.resources", "RenamedKey", "Deleted {0} items.");
        Assert.Equal(MatchKind.SourceHash, match.Kind);
        Assert.Equal("已删除 {0} 项。", match.Value);
    }

    [Fact]
    public void MissingUnitFallsBackToNothing()
    {
        var catalog = new TranslationCatalog { Language = "zh-Hans" };
        catalog.Add(Unit("A", "A.Resources.Text.resources", "K", "Open", "打开"));

        var match = catalog.Resolve("A", "A.Resources.Text.resources", "Unknown", "Something entirely new here");
        Assert.False(match.IsHit);
    }

    [Fact]
    public void JsonlRoundTripKeepsChineseUnescaped()
    {
        var path = Path.Combine(Path.GetTempPath(), "essencel10n_" + Guid.NewGuid().ToString("N") + ".jsonl");
        var catalog = new TranslationCatalog { Language = "zh-Hans" };
        catalog.Add(Unit("A", "A.Resources.Text.resources", "K", "Create new localization string", "创建新本地化字符串"));
        catalog.Save(path);

        var raw = File.ReadAllText(path);
        Assert.Contains("创建新本地化字符串", raw);

        var loaded = TranslationCatalog.Load(path);
        Assert.Single(loaded.Units);
        Assert.Equal("创建新本地化字符串", loaded.Units[0].Value);
        File.Delete(path);
    }
}

public class ResourceTextTableTests
{
    [Fact]
    public void RoundTripPreservesEntries()
    {
        var entries = new List<KeyValuePair<string, string>>
        {
            new("A.B", "值一"),
            new("C.D", "Value two"),
        };

        var bytes = ResourceTextTable.Write(entries);
        var read = ResourceTextTable.Read(bytes);

        Assert.Equal(2, read.Count);
        Assert.True(read.IsPureStringTable);
        Assert.Contains(read.Strings, kv => kv.Key == "A.B" && kv.Value == "值一");
    }

    [Fact]
    public void ApplyReportsChangesAndKeepsFallback()
    {
        var source = new List<KeyValuePair<string, string>>
        {
            new("Hit", "Open"),
            new("Miss", "Close"),
        };

        var changed = ResourceTextTable.Apply(source, (key, value) => key == "Hit" ? "打开" : null, out var output, out var count);

        Assert.True(changed);
        Assert.Equal(1, count);
        Assert.Equal("打开", output[0].Value);
        Assert.Equal("Close", output[1].Value);
    }
}
