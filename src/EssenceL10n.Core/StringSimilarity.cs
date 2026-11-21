namespace EssenceL10n.Core;

public static class StringSimilarity
{
    /// <summary>Sørensen–Dice 二元组相似度，0..1。用于源文本改动后的模糊回退。</summary>
    public static double Dice(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0;
        if (a == b) return 1;
        if (a.Length < 2 || b.Length < 2) return 0;
        var grams = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < a.Length - 1; i++)
        {
            var g = a.Substring(i, 2);
            grams[g] = grams.TryGetValue(g, out var c) ? c + 1 : 1;
        }
        int hits = 0;
        for (int i = 0; i < b.Length - 1; i++)
        {
            var g = b.Substring(i, 2);
            if (grams.TryGetValue(g, out var c) && c > 0)
            {
                grams[g] = c - 1;
                hits++;
            }
        }
        return 2.0 * hits / (a.Length - 1 + b.Length - 1);
    }
}
