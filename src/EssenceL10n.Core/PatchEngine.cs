using dnlib.DotNet;

namespace EssenceL10n.Core;

public sealed class PatchOptions
{
    public required string TargetDirectory { get; init; }
    public IReadOnlyList<TranslationCatalog> Catalogs { get; init; } = Array.Empty<TranslationCatalog>();
    public bool DryRun { get; init; }
    public bool CreateBackup { get; init; } = true;
    /// <summary>默认 0：不做模糊匹配。宁可回退原文，也不猜错——错译比未译更糟。</summary>
    public double FuzzyThreshold { get; init; }
    public bool AllowGlobalKey { get; init; }
    public Action<string>? Log { get; init; }
}

public sealed class AssemblyPatchResult
{
    public string Assembly { get; init; } = "";
    public string FileName { get; init; } = "";
    public string ResourceName { get; init; } = "";
    public int Total { get; set; }
    public int Patched { get; set; }
    public int Unresolved { get; set; }
    public int SkippedNonString { get; set; }
    public bool Written { get; set; }
    public string? Warning { get; set; }
    public Dictionary<string, int> MatchKinds { get; } = new();
}

public sealed class PatchReport
{
    public List<AssemblyPatchResult> Assemblies { get; } = new();
    public int TotalUnits => Assemblies.Sum(a => a.Total);
    public int PatchedUnits => Assemblies.Sum(a => a.Patched);
    public int UnresolvedUnits => Assemblies.Sum(a => a.Unresolved);
}

/// <summary>基于 dnlib 的资源级重写：不反编译方法体，只换 manifest resource 里的字符串值。</summary>
public static class PatchEngine
{
    public static PatchReport Apply(PatchOptions options)
    {
        var report = new PatchReport();
        var files = EditorScanner.EnumerateCandidates(options.TargetDirectory)
            .Select(f => (path: f, entries: EditorScanner.ScanFile(f, msg => options.Log?.Invoke(msg)).ToList()))
            .Where(x => x.entries.Count > 0)
            .ToList();

        foreach (var (path, entries) in files)
        {
            foreach (var entry in entries)
            {
                var result = new AssemblyPatchResult
                {
                    Assembly = entry.Assembly,
                    FileName = Path.GetFileName(path),
                    ResourceName = entry.ResourceName,
                };
                report.Assemblies.Add(result);

                var parsed = ResourceTextTable.Read(entry.Data);
                result.Total = parsed.Count;
                if (!parsed.IsPureStringTable)
                {
                    result.SkippedNonString = parsed.NonStringKeys.Count;
                    result.Warning = "非纯字符串资源，跳过重写";
                    continue;
                }

                var changed = false;
                var newEntries = new List<KeyValuePair<string, string>>(parsed.Count);
                foreach (var kv in parsed.Strings)
                {
                    var match = Resolve(options.Catalogs, entry.Assembly, entry.ResourceName, kv.Key, kv.Value,
                                        options.FuzzyThreshold, options.AllowGlobalKey);
                    if (match.IsHit && match.Value != kv.Value)
                    {
                        newEntries.Add(new KeyValuePair<string, string>(kv.Key, match.Value!));
                        result.Patched++;
                        var kind = match.Kind.ToString();
                        result.MatchKinds[kind] = result.MatchKinds.TryGetValue(kind, out var n) ? n + 1 : 1;
                        changed = true;
                    }
                    else
                    {
                        newEntries.Add(kv);
                        if (!match.IsHit) result.Unresolved++;
                    }
                }

                if (!changed || options.DryRun) continue;

                var backupPath = Backup(path, options);
                WriteModule(path, entry.ResourceName, ResourceTextTable.Write(newEntries), backupPath, options.Log);
                result.Written = true;
            }
        }

        return report;
    }

    public static Match ResolveChain(IReadOnlyList<TranslationCatalog> catalogs, string assembly, string resource, string key, string source,
                                     double fuzzy = 0, bool allowGlobalKey = false)
        => Resolve(catalogs, assembly, resource, key, source, fuzzy, allowGlobalKey);

    private static Match Resolve(IReadOnlyList<TranslationCatalog> catalogs, string assembly, string resource, string key, string source,
                                 double fuzzy, bool allowGlobalKey)
    {
        foreach (var catalog in catalogs)
        {
            var match = catalog.Resolve(assembly, resource, key, source, fuzzy, allowGlobalKey);
            if (match.IsHit) return match;
        }
        return Match.None;
    }

    private static string? Backup(string path, PatchOptions options)
    {
        if (!options.CreateBackup) return null;
        var backupDir = Path.Combine(options.TargetDirectory, "EssenceL10n.backup", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(backupDir);
        var destination = Path.Combine(backupDir, Path.GetFileName(path));
        File.Copy(path, destination, overwrite: true);
        return destination;
    }

    private static void WriteModule(string path, string resourceName, byte[] data, string? backupPath, Action<string>? log)
    {
        var temp = path + ".essencel10n.tmp";
        ModuleDefMD? module = null;
        try
        {
            module = ModuleDefMD.Load(path);
            var index = -1;
            for (int i = 0; i < module.Resources.Count; i++)
            {
                if (module.Resources[i] is EmbeddedResource e &&
                    string.Equals(e.Name.String, resourceName, StringComparison.Ordinal))
                {
                    index = i;
                    break;
                }
            }
            if (index < 0) throw new InvalidOperationException("resource not found after load: " + resourceName);

            var original = (EmbeddedResource)module.Resources[index];
            module.Resources[index] = new EmbeddedResource(original.Name, data, original.Attributes);
            module.Write(temp);
            module.Dispose();
            module = null;

            File.Move(temp, path, overwrite: true);
            log?.Invoke($"written {Path.GetFileName(path)} ({(backupPath is null ? "no backup" : "backup: " + Path.GetFileName(Path.GetDirectoryName(backupPath)))})");
        }
        finally
        {
            module?.Dispose();
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public static VerifiedSummary Verify(PatchOptions options)
    {
        var summary = new VerifiedSummary();
        foreach (var entry in EditorScanner.ScanDirectory(options.TargetDirectory))
        {
            var parsed = ResourceTextTable.Read(entry.Data);
            foreach (var kv in parsed.Strings)
            {
                var match = Resolve(options.Catalogs, entry.Assembly, entry.ResourceName, kv.Key, kv.Value, 0, options.AllowGlobalKey);
                if (match.IsHit && match.Value == kv.Value) summary.InCatalog++;
                else summary.NotFromCatalog++;
            }
            summary.Assemblies++;
        }
        return summary;
    }
}

public sealed class VerifiedSummary
{
    public int Assemblies { get; set; }
    public int InCatalog { get; set; }
    public int NotFromCatalog { get; set; }
}
