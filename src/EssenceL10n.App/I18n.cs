using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace EssenceL10n.App;

/// <summary>GUI 自身的界面语言：按系统 UI 语言自动选择，缺省回退英文。</summary>
public sealed class I18n
{
    private readonly Dictionary<string, string> _map;

    private I18n(Dictionary<string, string> map, string culture)
    {
        _map = map;
        Culture = culture;
    }

    public string Culture { get; }

    public string this[string key] => _map.TryGetValue(key, out var value) ? value : key;

    public string F(string key, params object[] args)
    {
        var template = this[key];
        return args.Length == 0 ? template : string.Format(CultureInfo.CurrentCulture, template, args);
    }

    public static readonly (string Code, string NativeName)[] Supported =
    {
        ("en", "English"),
        ("zh-Hans", "简体中文"),
        ("zh-Hant", "繁體中文"),
        ("ja", "日本語"),
        ("de", "Deutsch"),
        ("ru", "Русский"),
        ("es", "Español"),
    };

    public static I18n ForCulture(string? culture)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(culture))
        {
            candidates.Add(culture);
            var dash = culture.LastIndexOf('-');
            while (dash > 0)
            {
                candidates.Add(culture[..dash]);
                culture = culture[..dash];
                dash = culture.LastIndexOf('-');
            }
        }
        candidates.Add("en");

        foreach (var candidate in candidates)
        {
            var map = LoadEmbedded(candidate);
            if (map is not null) return new I18n(map, candidate);
        }
        return new I18n(new Dictionary<string, string>(), "en");
    }

    private static Dictionary<string, string>? LoadEmbedded(string culture)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var suffix = ".i18n." + culture + ".json";
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        if (name is null) return null;
        using var stream = assembly.GetManifestResourceStream(name);
        if (stream is null) return null;
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
    }

    public static string DetectSystemCulture()
    {
        var ui = CultureInfo.CurrentUICulture;
        var name = ui.Name;
        if (string.IsNullOrWhiteSpace(name)) return "en";
        if (name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            return name.Contains("Hant", StringComparison.OrdinalIgnoreCase) || name.EndsWith("TW") || name.EndsWith("HK") || name.EndsWith("MO")
                ? "zh-Hant"
                : "zh-Hans";
        var two = name.Split('-')[0];
        return Supported.Any(s => s.Code == two) ? two : "en";
    }
}
