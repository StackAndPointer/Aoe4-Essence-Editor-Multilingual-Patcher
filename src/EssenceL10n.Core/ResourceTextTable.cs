using System.Collections;
using System.Resources;

namespace EssenceL10n.Core;

/// <summary>.resources 读写。只处理纯字符串表；遇到非字符串条目则拒绝重写（保持原始字节）。</summary>
public static class ResourceTextTable
{
    public sealed class ReadResult
    {
        public List<KeyValuePair<string, string>> Strings { get; } = new();
        public List<string> NonStringKeys { get; } = new();
        public bool IsPureStringTable => NonStringKeys.Count == 0;
        public int Count => Strings.Count;
    }

    public static ReadResult Read(byte[] data)
    {
        var result = new ReadResult();
        using var reader = new ResourceReader(new MemoryStream(data, writable: false));
        foreach (DictionaryEntry entry in reader)
        {
            if (entry.Key is not string key) continue;
            if (entry.Value is string s) result.Strings.Add(new KeyValuePair<string, string>(key, s));
            else result.NonStringKeys.Add(key);
        }
        return result;
    }

    public static byte[] Write(IEnumerable<KeyValuePair<string, string>> entries)
    {
        using var ms = new MemoryStream();
        using (var writer = new ResourceWriter(ms))
        {
            foreach (var kv in entries) writer.AddResource(kv.Key, kv.Value);
        }
        return ms.ToArray();
    }

    /// <summary>原地覆盖 value，保持顺序；返回是否有改动。</summary>
    public static bool Apply(IReadOnlyList<KeyValuePair<string, string>> source,
                             Func<string, string, string?> resolve,
                             out List<KeyValuePair<string, string>> output,
                             out int changed)
    {
        output = new List<KeyValuePair<string, string>>(source.Count);
        changed = 0;
        foreach (var kv in source)
        {
            var replacement = resolve(kv.Key, kv.Value);
            if (replacement is null || replacement == kv.Value)
            {
                output.Add(kv);
                continue;
            }
            output.Add(new KeyValuePair<string, string>(kv.Key, replacement));
            changed++;
        }
        return changed > 0;
    }
}
