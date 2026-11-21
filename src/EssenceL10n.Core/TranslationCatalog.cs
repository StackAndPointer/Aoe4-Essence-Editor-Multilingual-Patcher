using System.Text.Json;
using System.Text.Json.Serialization;

namespace EssenceL10n.Core;

public enum MatchKind
{
    None = 0,
    Exact = 1,
    KeyInAssembly = 2,
    KeyGlobal = 3,
    SourceHash = 4,
    Fuzzy = 5,
}

public readonly record struct Match(string? Value, MatchKind Kind, double Score, string? UnitId)
{
    public static readonly Match None = new(null, MatchKind.None, 0, null);
    public bool IsHit => Kind != MatchKind.None && Value is not null;
}

/// <summary>
/// 单一语言目录。查找顺序即回退策略：
/// (asm,res,key) → (asm,key) → key(全局唯一) → sourceHash → 模糊(Dice) → 放弃(保留原文)。
/// </summary>
public sealed class TranslationCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        WriteIndented = false,
    };

    private readonly Dictionary<string, TextUnit> _exact = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextUnit> _byAsmKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<TextUnit>> _byKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<TextUnit>> _byHash = new(StringComparer.Ordinal);
    private readonly List<TextUnit> _units = new();

    public string Language { get; init; } = "zh-Hans";
    public IReadOnlyList<TextUnit> Units => _units;

    public static TranslationCatalog Load(string path)
    {
        var catalog = new TranslationCatalog { Language = Path.GetFileNameWithoutExtension(path) };
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var unit = JsonSerializer.Deserialize<TextUnit>(line, JsonOptions);
            if (unit is not null) catalog.Add(unit);
        }
        return catalog;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        using var writer = new StreamWriter(stream);
        foreach (var unit in _units.OrderBy(u => u.Assembly, StringComparer.Ordinal)
                                   .ThenBy(u => u.Resource, StringComparer.Ordinal)
                                   .ThenBy(u => u.Key, StringComparer.Ordinal))
        {
            writer.WriteLine(JsonSerializer.Serialize(unit, JsonOptions));
        }
    }

    public void Add(TextUnit unit)
    {
        if (string.IsNullOrEmpty(unit.SourceHash)) unit.RefreshHash();
        var previous = _units.FindIndex(u => u.Assembly == unit.Assembly && u.Resource == unit.Resource && u.Key == unit.Key);
        if (previous >= 0) _units[previous] = unit; else _units.Add(unit);

        _exact[Key(unit.Assembly, unit.Resource, unit.Key)] = unit;
        _byAsmKey[Key(unit.Assembly, "", unit.Key)] = unit;
        Bucket(_byKey, unit.Key).Add(unit);
        if (!string.IsNullOrEmpty(unit.SourceHash)) Bucket(_byHash, unit.SourceHash).Add(unit);
    }

    private static List<TextUnit> Bucket(Dictionary<string, List<TextUnit>> map, string key)
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = new List<TextUnit>();
        return list;
    }

    private static string Key(string asm, string res, string key) => asm + "\u001f" + res + "\u001f" + key;

    public Match Resolve(string assembly, string resource, string key, string source,
                         double fuzzyThreshold = 0.72, bool allowGlobalKey = false)
    {
        if (_exact.TryGetValue(Key(assembly, resource, key), out var exact) && HasValue(exact))
            return new Match(exact.Value, MatchKind.Exact, 1.0, exact.Id);

        if (_byAsmKey.TryGetValue(Key(assembly, "", key), out var asmKey) && HasValue(asmKey))
            return new Match(asmKey.Value, MatchKind.KeyInAssembly, 0.95, asmKey.Id);

        // 跨程序集同键名默认不采用：不同模块常复用同名 key（如 LocString.DisplayName），语义可能不同。
        if (allowGlobalKey && _byKey.TryGetValue(key, out var sameKey))
        {
            var withValue = sameKey.Where(HasValue).ToList();
            if (withValue.Count == 1)
                return new Match(withValue[0].Value, MatchKind.KeyGlobal, 0.9, withValue[0].Id);
        }

        var hash = Hashing.Of(source);
        if (_byHash.TryGetValue(hash, out var sameHash))
        {
            var hit = sameHash.FirstOrDefault(u => HasValue(u) && u.Source == source);
            if (hit is not null) return new Match(hit.Value, MatchKind.SourceHash, 0.85, hit.Id);
        }

        if (fuzzyThreshold > 0 && source.Length >= 4)
        {
            TextUnit? best = null;
            double bestScore = 0;
            foreach (var unit in _units)
            {
                if (!HasValue(unit) || string.IsNullOrEmpty(unit.Source)) continue;
                if (Math.Abs(unit.Source!.Length - source.Length) > Math.Max(12, source.Length / 2)) continue;
                var score = StringSimilarity.Dice(unit.Source, source);
                if (score > bestScore) { bestScore = score; best = unit; }
            }
            if (best is not null && bestScore >= fuzzyThreshold)
                return new Match(best.Value, MatchKind.Fuzzy, bestScore, best.Id);
        }

        return Match.None;
    }

    public bool TryGet(string assembly, string resource, string key, out string? value, out UnitState state)
    {
        if (_exact.TryGetValue(Key(assembly, resource, key), out var unit))
        {
            value = unit.Value;
            state = unit.State;
            return true;
        }
        value = null;
        state = UnitState.Missing;
        return false;
    }

    public bool IsIgnored(string assembly, string resource, string key)
        => _exact.TryGetValue(Key(assembly, resource, key), out var unit) && unit.State == UnitState.Ignored;

    private static bool HasValue(TextUnit unit) => !string.IsNullOrEmpty(unit.Value) && unit.State is UnitState.Translated or UnitState.Review;
}
