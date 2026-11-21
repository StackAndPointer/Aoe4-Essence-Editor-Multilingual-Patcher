using EssenceL10n.Core;

namespace EssenceL10n.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            {
                PrintHelp();
                return args.Length == 0 ? 1 : 0;
            }

            var command = args[0].ToLowerInvariant();
            var options = ArgBag.Parse(args.Skip(1));
            return command switch
            {
                "scan" => Scan(options),
                "bootstrap" => Bootstrap(options),
                "apply" => Apply(options),
                "verify" => Verify(options),
                "diff" => Diff(options),
                "compare" => Compare(options),
                "report" => Report(options),
                "todo" => Todo(options),
                "merge" => Merge(options),
                "prune" => Prune(options),
                "import" => Import(options),
                "translate" => Translate(options),
                "export" => Export(options),
                _ => Unknown(command),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR " + ex.GetType().Name + ": " + ex.Message);
            return 2;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
essencel10n — Essence Editor 多语言热补丁工具

  scan      <编辑器目录> --out base.jsonl
            扫描所有 *Text.resources，导出基准文本表（Source=原文，Value=空）。

  bootstrap --original <英文目录> --translated <汉化目录> --lang <zh-Hans> --out <file.jsonl>
            从"原版 / 已汉化"两个版本对比生成初始翻译目录（相当于把旧补丁转成可复用资产）。

  apply     <编辑器目录> --catalog <file.jsonl> [--catalog c2.jsonl] [--lang zh-Hans]
            [--fuzzy 0.8] [--allow-global-key] [--no-backup] [--dry-run]
            按目录重写资源字符串；未命中条目自动回退为原文。

  verify    <编辑器目录> --catalog <file.jsonl>
            校验目标目录中每个字符串是否来自目录，并统计匹配率。

  diff      <old.jsonl> <new.jsonl>
            对比两个版本的 key 集合，输出新增/删除/变更。

  report    <编辑器目录> [--catalog <file.jsonl>]
            输出 Markdown 表格：哪些 dll/exe 含文本、各自的资源容器、条目数与覆盖率。

  todo      <编辑器目录> --catalog <file.jsonl> [--assembly <名>] [--out <file.jsonl>]
            列出目录未覆盖（仍为英文）的条目，格式可直接回填给 merge。

  prune     --catalog <file.jsonl> --assembly <名> [--out <file.jsonl>]
            删除目录中某个程序集的全部单元（修正索引错误时用）。

  merge     --catalog <file.jsonl> --add <patch.jsonl> [--out <file.jsonl>]
            把新增/修订译文合并进目录（按 asm+resource+key 覆盖 Value 与 State）。

  translate --catalog <base.jsonl> --lang <code> [--from-catalog <zh-Hans.jsonl>]
            [--reference <zh-Hans.jsonl>] [--out <file.jsonl>] [--limit N] [--concurrency N]
            机器翻译生成某语言的种子目录；--from-catalog 用来做"中文简→繁"这类同族转换。

  export    --catalog <file.jsonl> [--out <file>] [--format jsonl|csv|po|xliff]
  import    --catalog <file.jsonl> --from <file.po|file.xlf> --format po|xliff [--out <file.jsonl>]
            把目录导出为 CSV（Key / Source / Translation / State），供人工或 CAT 工具校对。

  compare   <目录A> <目录B>
            逐 key 对比两个构建的文本，用于"新流程产出 vs 旧手工补丁"的等价性验证。
""");
    }

    private static int Scan(ArgBag bag)
    {
        var directory = bag.Positional(0) ?? throw new ArgumentException("缺少编辑器目录");
        var outPath = bag.Value("out") ?? "base.jsonl";
        var catalog = new TranslationCatalog { Language = "en" };
        int entries = 0, files = 0;
        foreach (var file in EditorScanner.EnumerateCandidates(directory))
        {
            var any = false;
            foreach (var entry in EditorScanner.ScanFile(file, msg => Console.Error.WriteLine("  ! " + msg)))
            {
                files++;
                any = true;
                foreach (var kv in ResourceTextTable.Read(entry.Data).Strings)
                {
                    catalog.Add(new TextUnit
                    {
                        Assembly = entry.Assembly,
                        Resource = entry.ResourceName,
                        Key = kv.Key,
                        Source = kv.Value,
                        State = UnitState.Missing,
                    });
                    entries++;
                }
            }
            if (!any) continue;
        }
        catalog.Save(outPath);
        Console.WriteLine($"scan: assemblies-with-text={files} units={entries} -> {outPath}");
        return 0;
    }

    private static int Bootstrap(ArgBag bag)
    {
        var original = bag.Value("original") ?? throw new ArgumentException("缺少 --original");
        var translated = bag.Value("translated") ?? throw new ArgumentException("缺少 --translated");
        var lang = bag.Value("lang") ?? "zh-Hans";
        var outPath = bag.Value("out") ?? $"{lang}.jsonl";

        var baseUnits = new Dictionary<string, (TextUnit unit, string text)>(StringComparer.Ordinal);
        foreach (var entry in EditorScanner.ScanDirectory(original))
            foreach (var kv in ResourceTextTable.Read(entry.Data).Strings)
                baseUnits[entry.Assembly + "|" + entry.ResourceName + "|" + kv.Key] = (new TextUnit
                {
                    Assembly = entry.Assembly,
                    Resource = entry.ResourceName,
                    Key = kv.Key,
                    Source = kv.Value,
                }, kv.Value);

        var catalog = new TranslationCatalog { Language = lang };
        int translatedCount = 0, unchanged = 0, missing = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in EditorScanner.ScanDirectory(translated))
            foreach (var kv in ResourceTextTable.Read(entry.Data).Strings)
            {
                var id = entry.Assembly + "|" + entry.ResourceName + "|" + kv.Key;
                seen.Add(id);
                if (!baseUnits.TryGetValue(id, out var baseUnit))
                {
                    // 汉化版里有、原版没有：key 被改过，用源文哈希以外的方式无法对齐，先记为独立单元
                    catalog.Add(new TextUnit
                    {
                        Assembly = entry.Assembly,
                        Resource = entry.ResourceName,
                        Key = kv.Key,
                        Source = null,
                        Value = kv.Value,
                        State = UnitState.Review,
                        Note = "key 在原版中不存在，需人工确认",
                    });
                    missing++;
                    continue;
                }
                if (kv.Value == baseUnit.text) { unchanged++; continue; }
                catalog.Add(new TextUnit
                {
                    Assembly = entry.Assembly,
                    Resource = entry.ResourceName,
                    Key = kv.Key,
                    Source = baseUnit.text,
                    Value = kv.Value,
                    State = UnitState.Translated,
                });
                translatedCount++;
            }

        foreach (var kv in baseUnits)
        {
            if (seen.Contains(kv.Key)) continue;
            catalog.Add(new TextUnit
            {
                Assembly = kv.Value.unit.Assembly,
                Resource = kv.Value.unit.Resource,
                Key = kv.Value.unit.Key,
                Source = kv.Value.text,
                Value = null,
                State = UnitState.Missing,
            });
        }

        catalog.Save(outPath);
        Console.WriteLine($"bootstrap: translated={translatedCount} unchanged={unchanged} unmatchedKeys={missing} units={catalog.Units.Count} -> {outPath}");
        return 0;
    }

    private static int Apply(ArgBag bag)
    {
        var directory = bag.Positional(0) ?? throw new ArgumentException("缺少编辑器目录");
        var catalogPaths = bag.Values("catalog").ToList();
        if (catalogPaths.Count == 0) throw new ArgumentException("至少需要一个 --catalog");

        var catalogs = catalogPaths.Select(TranslationCatalog.Load).ToList();
        var options = new PatchOptions
        {
            TargetDirectory = directory,
            Catalogs = catalogs,
            DryRun = bag.Has("dry-run"),
            CreateBackup = !bag.Has("no-backup"),
            FuzzyThreshold = bag.Value("fuzzy") is { } f && double.TryParse(f, out var d) ? d : 0,
            AllowGlobalKey = bag.Has("allow-global-key"),
            Log = msg => Console.WriteLine("  " + msg),
        };

        var json = bag.Has("json");
        var report = PatchEngine.Apply(options);

        if (json)
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                ok = true,
                command = "apply",
                dryRun = options.DryRun,
                backup = options.CreateBackup,
                assemblies = report.Assemblies.Select(a => new
                {
                    assembly = a.Assembly,
                    file = a.FileName,
                    units = a.Total,
                    patched = a.Patched,
                    fallback = a.Unresolved,
                    written = a.Written,
                    matchKinds = a.MatchKinds,
                    warning = a.Warning,
                }),
                total = new { units = report.TotalUnits, patched = report.PatchedUnits, fallback = report.UnresolvedUnits },
            }, new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            return 0;
        }

        Console.WriteLine();
        foreach (var a in report.Assemblies)
        {
            var kinds = a.MatchKinds.Count == 0 ? "-" : string.Join(", ", a.MatchKinds.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}"));
            Console.WriteLine($"{a.Assembly,-42} {a.FileName,-40} units={a.Total,5} patched={a.Patched,5} fallback={a.Unresolved,5} written={a.Written,-5} [{kinds}]");
        }
        Console.WriteLine($"TOTAL units={report.TotalUnits} patched={report.PatchedUnits} fallback={report.UnresolvedUnits} dryRun={options.DryRun} backup={options.CreateBackup}");
        return 0;
    }

    private static int Verify(ArgBag bag)
    {
        var directory = bag.Positional(0) ?? throw new ArgumentException("缺少编辑器目录");
        var catalogPaths = bag.Values("catalog").ToList();
        if (catalogPaths.Count == 0) throw new ArgumentException("至少需要一个 --catalog");
        var catalogs = catalogPaths.Select(TranslationCatalog.Load).ToList();
        var summary = PatchEngine.Verify(new PatchOptions { TargetDirectory = directory, Catalogs = catalogs });
        var coverage = summary.InCatalog + summary.NotFromCatalog == 0
            ? 0
            : 100.0 * summary.InCatalog / (summary.InCatalog + summary.NotFromCatalog);

        if (bag.Has("json"))
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                ok = true,
                command = "verify",
                assemblies = summary.Assemblies,
                fromCatalog = summary.InCatalog,
                original = summary.NotFromCatalog,
                coverage = Math.Round(coverage, 1),
            }, new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            return 0;
        }

        Console.WriteLine($"verify: assemblies={summary.Assemblies} fromCatalog={summary.InCatalog} original={summary.NotFromCatalog} coverage={coverage:F1}%");
        return 0;
    }

    private static int Diff(ArgBag bag)
    {
        var oldPath = bag.Positional(0) ?? throw new ArgumentException("缺少 old.jsonl");
        var newPath = bag.Positional(1) ?? throw new ArgumentException("缺少 new.jsonl");
        var oldCatalog = TranslationCatalog.Load(oldPath);
        var newCatalog = TranslationCatalog.Load(newPath);
        var oldIds = oldCatalog.Units.ToDictionary(u => u.Id, StringComparer.Ordinal);
        var newIds = newCatalog.Units.ToDictionary(u => u.Id, StringComparer.Ordinal);

        var added = newIds.Keys.Except(oldIds.Keys).ToList();
        var removed = oldIds.Keys.Except(newIds.Keys).ToList();
        var changedSource = newIds.Keys.Intersect(oldIds.Keys).Where(id => oldIds[id].Source != newIds[id].Source).ToList();

        Console.WriteLine($"diff: old={oldIds.Count} new={newIds.Count} added={added.Count} removed={removed.Count} sourceChanged={changedSource.Count}");
        foreach (var id in added.Take(20)) Console.WriteLine("  + " + id.Replace('\u001f', '|'));
        foreach (var id in removed.Take(20)) Console.WriteLine("  - " + id.Replace('\u001f', '|'));
        foreach (var id in changedSource.Take(20))
        {
            Console.WriteLine("  ~ " + id.Replace('\u001f', '|'));
            Console.WriteLine("      old: " + oldIds[id].Source);
            Console.WriteLine("      new: " + newIds[id].Source);
        }
        return 0;
    }

    private static int Report(ArgBag bag)
    {
        var directory = bag.Positional(0) ?? throw new ArgumentException("缺少编辑器目录");
        var catalogPaths = bag.Values("catalog").ToList();
        var catalogs = catalogPaths.Select(TranslationCatalog.Load).ToList();
        var json = bag.Has("json");

        var jsonRows = new List<object>();
        if (!json)
        {
            Console.WriteLine("| 文件 | 程序集 | 资源容器 | 条目 | 已汉化 | 有意保留 | 未译 | 覆盖率 |");
            Console.WriteLine("|---|---|---|---:|---:|---:|---:|---:|");
        }
        int totalUnits = 0, totalHit = 0, totalKept = 0;
        foreach (var entry in EditorScanner.ScanDirectory(directory)
                     .GroupBy(e => (e.Assembly, e.ResourceName))
                     .Select(g => g.First())
                     .OrderBy(e => e.Assembly, StringComparer.Ordinal))
        {
            var parsed = ResourceTextTable.Read(entry.Data);
            int hit = 0, kept = 0;
            foreach (var kv in parsed.Strings)
            {
                // 目录里对该 key 有明确单元：值不同 → 真译文；值与当前英文同形 → 同形保留（符号/术语/缩写）
                var explicitUnit = false;
                var sameAsSource = false;
                foreach (var catalog in catalogs)
                {
                    if (!catalog.TryGet(entry.Assembly, entry.ResourceName, kv.Key, out var value, out var state)) continue;
                    if (state == UnitState.Ignored || value == kv.Value) { sameAsSource = true; explicitUnit = true; break; }
                    if (!string.IsNullOrEmpty(value)) { explicitUnit = true; break; }
                }
                if (explicitUnit) { if (sameAsSource) kept++; else hit++; continue; }

                // 目录里没有该 key（版本漂移）：退回哈希/模糊匹配
                var match = PatchEngine.ResolveChain(catalogs, entry.Assembly, entry.ResourceName, kv.Key, kv.Value);
                if (match.IsHit) hit++;
            }
            var coverage = parsed.Count == 0 ? 0 : 100.0 * hit / parsed.Count;
            var row = new
            {
                file = Path.GetFileName(entry.FilePath),
                assembly = entry.Assembly,
                resource = entry.ResourceName,
                total = parsed.Count,
                translated = hit,
                kept,
                missing = parsed.Count - hit - kept,
                coverage = Math.Round(coverage, 1),
            };
            jsonRows.Add(row);
            if (!json)
                Console.WriteLine($"| {row.file} | {row.assembly} | {row.resource} | {row.total} | {row.translated} | {row.kept} | {row.missing} | {row.coverage:F1}% |");
            totalUnits += parsed.Count;
            totalHit += hit;
            totalKept += kept;
        }

        if (json)
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                ok = true,
                command = "report",
                catalog = catalogPaths,
                language = catalogs.FirstOrDefault()?.Language,
                assemblies = jsonRows,
                total = new { units = totalUnits, translated = totalHit, kept = totalKept, missing = totalUnits - totalHit - totalKept,
                    coverage = Math.Round(totalUnits == 0 ? 0 : 100.0 * totalHit / totalUnits, 1) },
            }, new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            return 0;
        }

        Console.WriteLine();
        Console.WriteLine($"合计: 条目={totalUnits} 已汉化={totalHit} 有意保留={totalKept} 未译={totalUnits - totalHit - totalKept} 覆盖率={(totalUnits == 0 ? 0 : 100.0 * totalHit / totalUnits):F1}%");
        return 0;
    }

    private static int Todo(ArgBag bag)
    {
        var directory = bag.Positional(0) ?? throw new ArgumentException("缺少编辑器目录");
        var catalogPaths = bag.Values("catalog").ToList();
        var catalogs = catalogPaths.Select(TranslationCatalog.Load).ToList();
        var assemblyFilter = bag.Value("assembly");
        var outPath = bag.Value("out");

        var buffer = new System.Text.StringBuilder();
        int count = 0;
        foreach (var entry in EditorScanner.ScanDirectory(directory))
        {
            if (assemblyFilter is not null && !entry.Assembly.Equals(assemblyFilter, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var kv in ResourceTextTable.Read(entry.Data).Strings)
            {
                var match = PatchEngine.ResolveChain(catalogs, entry.Assembly, entry.ResourceName, kv.Key, kv.Value);
                if (match.IsHit && match.Value != kv.Value) continue;
                if (catalogs.Any(c => c.IsIgnored(entry.Assembly, entry.ResourceName, kv.Key))) continue;
                count++;
                buffer.AppendLine(System.Text.Json.JsonSerializer.Serialize(new
                {
                    Assembly = entry.Assembly,
                    Resource = entry.ResourceName,
                    Key = kv.Key,
                    Source = kv.Value,
                }, new System.Text.Json.JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }));
            }
        }

        if (outPath is null) Console.Write(buffer.ToString());
        else
        {
            File.WriteAllText(outPath, buffer.ToString(), new System.Text.UTF8Encoding(false));
            Console.WriteLine($"todo: {count} 条未覆盖 -> {outPath}");
        }
        Console.Error.WriteLine($"未覆盖条目数: {count}");
        return 0;
    }

    private static int Prune(ArgBag bag)
    {
        var catalogPath = bag.Value("catalog") ?? throw new ArgumentException("缺少 --catalog");
        var assembly = bag.Value("assembly") ?? throw new ArgumentException("缺少 --assembly");
        var outPath = bag.Value("out") ?? catalogPath;
        var catalog = TranslationCatalog.Load(catalogPath);
        var removed = catalog.Units.Count(u => u.Assembly == assembly);
        var kept = catalog.Units.Where(u => u.Assembly != assembly).ToList();
        var rebuilt = new TranslationCatalog { Language = catalog.Language };
        foreach (var unit in kept) rebuilt.Add(unit);
        rebuilt.Save(outPath);
        Console.WriteLine($"prune: 移除 {removed} 条 (assembly={assembly}) -> {Path.GetFileName(outPath)} (剩余 {rebuilt.Units.Count})");
        return 0;
    }

    private static int Merge(ArgBag bag)
    {
        var catalogPath = bag.Value("catalog") ?? throw new ArgumentException("缺少 --catalog");
        var addPath = bag.Value("add") ?? throw new ArgumentException("缺少 --add");
        var outPath = bag.Value("out") ?? catalogPath;

        var catalog = TranslationCatalog.Load(catalogPath);
        var added = 0;
        foreach (var line in File.ReadLines(addPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var unit = System.Text.Json.JsonSerializer.Deserialize<TextUnit>(line, new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
            if (unit is null || string.IsNullOrEmpty(unit.Key)) continue;
            if (string.IsNullOrEmpty(unit.Value)) continue;
            if (unit.State != UnitState.Ignored) unit.State = UnitState.Translated;
            unit.RefreshHash();
            catalog.Add(unit);
            added++;
        }
        catalog.Save(outPath);
        Console.WriteLine($"merge: 新增/更新 {added} 条 -> {outPath} (总 {catalog.Units.Count})");
        return 0;
    }

    private static readonly System.Text.RegularExpressions.Regex PlaceholderPattern =
        new(@"\{\d+\}", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static int Translate(ArgBag bag)
    {
        var basePath = bag.Value("catalog") ?? throw new ArgumentException("缺少 --catalog");
        var lang = bag.Value("lang") ?? throw new ArgumentException("缺少 --lang");
        var outPath = bag.Value("out") ?? Path.Combine("catalogs", lang, lang + ".jsonl");
        var baseCatalog = TranslationCatalog.Load(basePath);

        TranslationCatalog? sourceCatalog = bag.Value("from-catalog") is { } fromPath ? TranslationCatalog.Load(fromPath) : null;
        TranslationCatalog? reference = bag.Value("reference") is { } referencePath
            ? TranslationCatalog.Load(referencePath)
            : (baseCatalog.Language == "zh-Hans" ? baseCatalog : null);

        var sourceIndex = new Dictionary<string, string>(StringComparer.Ordinal);
        if (sourceCatalog is not null)
            foreach (var unit in sourceCatalog.Units)
                if (!string.IsNullOrEmpty(unit.Value)) sourceIndex[unit.Id] = unit.Value!;

        var limit = bag.Value("limit") is { } limitText && int.TryParse(limitText, out var parsedLimit) ? parsedLimit : int.MaxValue;
        var concurrency = bag.Value("concurrency") is { } concurrencyText && int.TryParse(concurrencyText, out var parsedConcurrency)
            ? parsedConcurrency
            : 3;

        var result = new TranslationCatalog { Language = lang };
        var pending = new List<(TextUnit unit, string text)>();
        int ignored = 0, empty = 0;

        foreach (var unit in baseCatalog.Units.Take(limit))
        {
            var text = sourceIndex.TryGetValue(unit.Id, out var translated) ? translated : unit.Source;
            if (string.IsNullOrEmpty(text))
            {
                result.Add(new TextUnit
                {
                    Assembly = unit.Assembly, Resource = unit.Resource, Key = unit.Key,
                    Source = unit.Source ?? "", State = UnitState.Missing,
                });
                empty++;
                continue;
            }

            if (reference is not null && reference.IsIgnored(unit.Assembly, unit.Resource, unit.Key))
            {
                result.Add(new TextUnit
                {
                    Assembly = unit.Assembly, Resource = unit.Resource, Key = unit.Key,
                    Source = unit.Source ?? text, Value = text, State = UnitState.Ignored,
                });
                ignored++;
                continue;
            }

            pending.Add((unit, text));
        }

        Console.WriteLine($"translate[{lang}]: 待译 {pending.Count} 条，沿用原文 {empty} 条，技术标识 {ignored} 条，并发 {concurrency}");
        var options = new MachineTranslationOptions
        {
            SourceLang = sourceCatalog is null ? "en" : (lang.StartsWith("zh") && sourceCatalog.Language.StartsWith("zh") ? "zh-CN" : sourceCatalog.Language),
            TargetLang = lang,
            Concurrency = concurrency,
            Log = message => Console.Error.WriteLine("  " + message),
        };

        var translatedTexts = MachineTranslator.Translate(pending.Select(p => p.text).ToList(), options);
        int done = 0, guarded = 0;
        for (int i = 0; i < pending.Count; i++)
        {
            var (unit, sourceText) = pending[i];
            var value = translatedTexts[i];

            var sourceTokens = PlaceholderPattern.Matches(sourceText).Select(m => m.Value).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            var valueTokens = PlaceholderPattern.Matches(value).Select(m => m.Value).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            var facts = new TextUnit
            {
                Assembly = unit.Assembly, Resource = unit.Resource, Key = unit.Key,
                Source = unit.Source ?? sourceText,
                Value = value,
                State = UnitState.Review,
                Note = "MT " + options.SourceLang + "->" + lang,
            };

            if (!sourceTokens.SequenceEqual(valueTokens))
            {
                facts.Value = sourceText;
                facts.State = UnitState.Review;
                facts.Note = "占位符不一致，保留原文";
                guarded++;
            }
            else if (value == sourceText)
            {
                guarded++;
            }

            result.Add(facts);
            done++;
            if (done % 200 == 0) Console.Error.WriteLine($"  ... {done}/{pending.Count}");
        }

        result.Save(outPath);
        Console.WriteLine($"translate[{lang}]: 生成 {result.Units.Count} 条（Review {done}，占位符回退 {guarded}）-> {outPath}");
        return 0;
    }

    private static int Import(ArgBag bag)
    {
        var catalogPath = bag.Value("catalog") ?? throw new ArgumentException("缺少 --catalog");
        var from = bag.Value("from") ?? throw new ArgumentException("缺少 --from");
        var format = (bag.Value("format") ?? Path.GetExtension(from).TrimStart('.').ToLowerInvariant()) switch
        {
            "po" or "pot" => "po",
            "xlf" or "xliff" => "xliff",
            _ => throw new ArgumentException("无法判断格式，请用 --format po|xliff"),
        };

        var units = format == "po" ? CatalogInterop.ImportPo(from) : CatalogInterop.ImportXliff(from);
        var catalog = TranslationCatalog.Load(catalogPath);
        var outPath = bag.Value("out") ?? catalogPath;
        int applied = 0, skipped = 0;
        foreach (var unit in units)
        {
            if (string.IsNullOrEmpty(unit.Value)) { skipped++; continue; }
            unit.State = UnitState.Translated;
            unit.RefreshHash();
            catalog.Add(unit);
            applied++;
        }
        catalog.Save(outPath);
        Console.WriteLine($"import[{format}]: 应用 {applied} 条，跳过空译文 {skipped} 条 -> {Path.GetFileName(outPath)} (总 {catalog.Units.Count})");
        return 0;
    }

    private static int Export(ArgBag bag)
    {
        var catalogPath = bag.Value("catalog") ?? throw new ArgumentException("缺少 --catalog");
        var catalog = TranslationCatalog.Load(catalogPath);
        var format = (bag.Value("format") ?? "csv").ToLowerInvariant();
        if (format is "xlf") format = "xliff";
        if (format is not ("csv" or "po" or "xliff")) throw new ArgumentException("--format 只能是 csv|po|xliff");
        var outPath = bag.Value("out") ?? Path.ChangeExtension(catalogPath,
            format switch { "po" => ".po", "xliff" => ".xlf", _ => ".csv" });

        if (format == "po")
        {
            CatalogInterop.ExportPo(catalog, outPath, catalog.Language);
            Console.WriteLine($"export[po]: units={catalog.Units.Count} -> {outPath}");
            return 0;
        }
        if (format == "xliff")
        {
            CatalogInterop.ExportXliff(catalog, outPath, catalog.Language);
            Console.WriteLine($"export[xliff]: units={catalog.Units.Count} -> {outPath}");
            return 0;
        }

        static string Q(string? s)
        {
            s ??= "";
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }

        using var writer = new StreamWriter(outPath, false, new System.Text.UTF8Encoding(true));
        writer.WriteLine("Assembly,Resource,Key,Source,Translation,State,SourceHash");
        foreach (var unit in catalog.Units.OrderBy(u => u.Assembly, StringComparer.Ordinal)
                                          .ThenBy(u => u.Key, StringComparer.Ordinal))
        {
            writer.WriteLine(string.Join(",",
                Q(unit.Assembly), Q(unit.Resource), Q(unit.Key), Q(unit.Source), Q(unit.Value), Q(unit.State.ToString()), Q(unit.SourceHash)));
        }
        Console.WriteLine($"export[csv]: units={catalog.Units.Count} -> {outPath}");
        return 0;
    }

    private static int Compare(ArgBag bag)
    {
        var dirA = bag.Positional(0) ?? throw new ArgumentException("缺少目录A");
        var dirB = bag.Positional(1) ?? throw new ArgumentException("缺少目录B");

        static Dictionary<string, string> Load(string dir)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in EditorScanner.ScanDirectory(dir))
                foreach (var kv in ResourceTextTable.Read(entry.Data).Strings)
                    map[entry.Assembly + "|" + entry.ResourceName + "|" + kv.Key] = kv.Value;
            return map;
        }

        var a = Load(dirA);
        var b = Load(dirB);
        var common = a.Keys.Intersect(b.Keys).ToList();
        var identical = common.Where(k => a[k] == b[k]).ToList();
        var differing = common.Where(k => a[k] != b[k]).ToList();
        var onlyA = a.Keys.Except(b.Keys).ToList();
        var onlyB = b.Keys.Except(a.Keys).ToList();

        Console.WriteLine($"compare: A={a.Count} B={b.Count} common={common.Count} identical={identical.Count} differing={differing.Count} onlyA={onlyA.Count} onlyB={onlyB.Count}");
        Console.WriteLine($"equivalence={(common.Count == 0 ? 0 : 100.0 * identical.Count / common.Count):F2}%");
        foreach (var k in differing.Take(10))
        {
            Console.WriteLine("  ~ " + k.Replace('\u001f', '|'));
            Console.WriteLine("      A: " + a[k]);
            Console.WriteLine("      B: " + b[k]);
        }
        return 0;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine("未知命令: " + command);
        PrintHelp();
        return 1;
    }
}

internal sealed class ArgBag
{
    private readonly List<string> _positional = new();
    private readonly Dictionary<string, List<string>> _named = new(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> BooleanFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "dry-run", "no-backup", "backup", "help", "verbose", "quiet", "allow-global-key", "json",
    };

    public static ArgBag Parse(IEnumerable<string> args)
    {
        var bag = new ArgBag();
        var list = args.ToList();
        for (int i = 0; i < list.Count; i++)
        {
            var arg = list[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                bag._positional.Add(arg);
                continue;
            }

            var body = arg[2..];
            var eq = body.IndexOf('=');
            string key, value;
            if (eq >= 0)
            {
                key = body[..eq];
                value = body[(eq + 1)..];
            }
            else if (!BooleanFlags.Contains(body) && i + 1 < list.Count && !list[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                key = body;
                value = list[++i];
            }
            else
            {
                key = body;
                value = "true";
            }

            if (!bag._named.TryGetValue(key, out var bucket)) bag._named[key] = bucket = new List<string>();
            bucket.Add(value);
        }
        return bag;
    }

    public string? Positional(int index) => index < _positional.Count ? _positional[index] : null;

    public string? Value(string name) => _named.TryGetValue(name, out var list) ? list[^1] : null;

    public IEnumerable<string> Values(string name) => _named.TryGetValue(name, out var list) ? list : Enumerable.Empty<string>();

    public bool Has(string name) => _named.ContainsKey(name);
}
