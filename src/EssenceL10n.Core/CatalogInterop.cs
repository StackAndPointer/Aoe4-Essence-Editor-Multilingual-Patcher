using System.Text;
using System.Xml.Linq;

namespace EssenceL10n.Core;

/// <summary>PO(gettext) 与 XLIFF 1.2 的导入导出，用于接 Weblate / Crowdin / Poedit。</summary>
public static class CatalogInterop
{
    private const string CtxSeparator = "|";

    // ---------- PO ----------

    public static void ExportPo(TranslationCatalog catalog, string path, string lang)
    {
        var sb = new StringBuilder();
        sb.AppendLine("msgid \"\"");
        sb.AppendLine("msgstr \"\"");
        sb.AppendLine("\"Project-Id-Version: EssenceL10n\\n\"");
        sb.AppendLine("\"Language: " + lang + "\\n\"");
        sb.AppendLine("\"MIME-Version: 1.0\\n\"");
        sb.AppendLine("\"Content-Type: text/plain; charset=UTF-8\\n\"");
        sb.AppendLine("\"Content-Transfer-Encoding: 8bit\\n\"");
        sb.AppendLine("\"X-Domain: EssenceEditor\\n\"");
        sb.AppendLine();

        foreach (var unit in catalog.Units.OrderBy(u => u.Assembly, StringComparer.Ordinal)
                                          .ThenBy(u => u.Key, StringComparer.Ordinal))
        {
            var ctx = unit.Assembly + CtxSeparator + unit.Resource + CtxSeparator + unit.Key;
            sb.AppendLine("#. state=" + unit.State);
            sb.AppendLine("msgctxt \"" + EscapePo(ctx) + "\"");
            sb.AppendLine("msgid \"" + EscapePo(unit.Source ?? "") + "\"");
            sb.AppendLine("msgstr \"" + EscapePo(unit.Value ?? "") + "\"");
            sb.AppendLine();
        }

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    public static List<TextUnit> ImportPo(string path)
    {
        var units = new List<TextUnit>();
        string? ctx = null, id = null, str = null;
        string? field = null;

        void Flush()
        {
            if (ctx is null || id is null) { ctx = id = str = null; field = null; return; }
            var parts = ctx.Split(CtxSeparator, 3);
            if (parts.Length == 3)
            {
                units.Add(new TextUnit
                {
                    Assembly = parts[0],
                    Resource = parts[1],
                    Key = parts[2],
                    Source = id,
                    Value = string.IsNullOrEmpty(str) ? null : str,
                    State = string.IsNullOrEmpty(str) ? UnitState.Missing : UnitState.Translated,
                });
            }
            ctx = id = str = null;
            field = null;
        }

        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0) { Flush(); continue; }
            if (line.StartsWith("#")) continue;

            if (line.StartsWith("msgctxt "))
            {
                ctx = UnescapePo(Quoted(line["msgctxt ".Length..]));
                field = "ctx";
                continue;
            }
            if (line.StartsWith("msgid "))
            {
                id = UnescapePo(Quoted(line["msgid ".Length..]));
                field = "id";
                continue;
            }
            if (line.StartsWith("msgstr "))
            {
                str = UnescapePo(Quoted(line["msgstr ".Length..]));
                field = "str";
                continue;
            }
            if (line.StartsWith('"'))
            {
                var more = UnescapePo(Quoted(line));
                switch (field)
                {
                    case "ctx": ctx += more; break;
                    case "id": id += more; break;
                    case "str": str += more; break;
                }
            }
        }
        Flush();
        return units;
    }

    private static string Quoted(string value)
    {
        value = value.Trim();
        if (value.StartsWith('"') && value.EndsWith('"') && value.Length >= 2) return value[1..^1];
        return value;
    }

    private static string EscapePo(string value) => value
        .Replace("\\", "\\\\")
        .Replace("\"", "\\\"")
        .Replace("\r", "\\r")
        .Replace("\n", "\\n")
        .Replace("\t", "\\t");

    private static string UnescapePo(string value)
    {
        var sb = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\' || i + 1 >= value.Length) { sb.Append(value[i]); continue; }
            i++;
            sb.Append(value[i] switch
            {
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                '"' => '"',
                '\\' => '\\',
                _ => value[i],
            });
        }
        return sb.ToString();
    }

    // ---------- XLIFF 1.2 ----------

    private static readonly XNamespace Ns = "urn:oasis:names:tc:xliff:document:1.2";

    public static void ExportXliff(TranslationCatalog catalog, string path, string lang)
    {
        var file = new XElement(Ns + "file",
            new XAttribute("source-language", "en"),
            new XAttribute("target-language", lang),
            new XAttribute("datatype", "plaintext"),
            new XAttribute("original", "EssenceEditor"),
            new XElement(Ns + "body"));

        var body = file.Element(Ns + "body")!;
        foreach (var unit in catalog.Units.OrderBy(u => u.Assembly, StringComparer.Ordinal)
                                          .ThenBy(u => u.Key, StringComparer.Ordinal))
        {
            var id = unit.Assembly + CtxSeparator + unit.Resource + CtxSeparator + unit.Key;
            body.Add(new XElement(Ns + "trans-unit",
                new XAttribute("id", id),
                new XAttribute("resname", unit.Key),
                new XElement(Ns + "source", new XAttribute(XNamespace.Xml + "space", "preserve"), unit.Source ?? ""),
                new XElement(Ns + "target", new XAttribute(XNamespace.Xml + "space", "preserve"), unit.Value ?? ""),
                new XElement(Ns + "note", new XAttribute("from", "essencel10n"), "state=" + unit.State)));
        }

        var doc = new XDocument(new XDeclaration("1.0", "utf-8", null), new XElement(Ns + "xliff",
            new XAttribute("version", "1.2"), file));
        doc.Save(path, SaveOptions.None);
    }

    public static List<TextUnit> ImportXliff(string path)
    {
        var doc = XDocument.Load(path);
        var units = new List<TextUnit>();
        foreach (var tu in doc.Descendants(Ns + "trans-unit"))
        {
            var id = (string?)tu.Attribute("id") ?? "";
            var parts = id.Split(CtxSeparator, 3);
            if (parts.Length != 3) continue;
            var source = (string?)tu.Element(Ns + "source") ?? "";
            var target = (string?)tu.Element(Ns + "target");
            units.Add(new TextUnit
            {
                Assembly = parts[0],
                Resource = parts[1],
                Key = parts[2],
                Source = source,
                Value = string.IsNullOrEmpty(target) ? null : target,
                State = string.IsNullOrEmpty(target) ? UnitState.Missing : UnitState.Translated,
            });
        }
        return units;
    }
}
