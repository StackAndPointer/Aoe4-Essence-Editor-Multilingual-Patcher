using dnlib.DotNet;

namespace EssenceL10n.Core;

public sealed record TextResourceEntry(string Assembly, string FilePath, string ResourceName, byte[] Data, int Count);

/// <summary>枚举目录里所有含 *Text.resources 的 .NET 程序集（.dll/.exe）。</summary>
public static class EditorScanner
{
    public static readonly string[] DefaultExtensions = { "*.dll", "*.exe" };

    public static IEnumerable<string> EnumerateCandidates(string directory)
    {
        foreach (var pattern in DefaultExtensions)
            foreach (var file in Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly))
                yield return file;
    }

    public static IEnumerable<TextResourceEntry> ScanFile(string path, Action<string>? warn = null)
    {
        ModuleDefMD? module = null;
        try
        {
            module = ModuleDefMD.Load(path);
        }
        catch (Exception ex)
        {
            warn?.Invoke($"skip {Path.GetFileName(path)}: {ex.GetType().Name}");
            yield break;
        }

        var assembly = module.Assembly?.Name?.String ?? Path.GetFileNameWithoutExtension(path);
        var resources = module.Resources.ToList();
        foreach (var resource in resources)
        {
            if (resource is not EmbeddedResource embedded) continue;
            var name = embedded.Name.String;
            if (!name.EndsWith("Text.resources", StringComparison.OrdinalIgnoreCase)) continue;
            byte[] data;
            try { data = embedded.CreateReader().ToArray(); }
            catch (Exception ex) { warn?.Invoke($"read {name}: {ex.Message}"); continue; }
            ResourceTextTable.ReadResult parsed;
            try { parsed = ResourceTextTable.Read(data); }
            catch (Exception ex) { warn?.Invoke($"parse {name}: {ex.Message}"); continue; }
            yield return new TextResourceEntry(assembly, path, name, data, parsed.Count);
        }
        module.Dispose();
    }

    public static IEnumerable<TextResourceEntry> ScanDirectory(string directory, Action<string>? warn = null)
    {
        foreach (var file in EnumerateCandidates(directory))
            foreach (var entry in ScanFile(file, warn))
                yield return entry;
    }
}
