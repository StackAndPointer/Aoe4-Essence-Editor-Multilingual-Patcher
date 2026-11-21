namespace EssenceL10n.Core;

public enum UnitState
{
    Missing = 0,
    Translated = 1,
    Review = 2,
    Ignored = 3,
}

/// <summary>一条可翻译文本单元。Assembly 用程序集名（不是文件名），因此文件改名/换版本不影响主键。</summary>
public sealed class TextUnit
{
    public string Assembly { get; set; } = "";
    public string Resource { get; set; } = "";
    public string Key { get; set; } = "";
    public string? Source { get; set; }
    public string? Value { get; set; }
    public string? SourceHash { get; set; }
    public UnitState State { get; set; } = UnitState.Missing;
    public string? Note { get; set; }

    public string Id => Assembly + "\u001f" + Resource + "\u001f" + Key;

    public void RefreshHash() => SourceHash = Hashing.Of(Source);
}
