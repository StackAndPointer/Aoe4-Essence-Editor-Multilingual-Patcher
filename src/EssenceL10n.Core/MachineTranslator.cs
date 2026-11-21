using System.Text;
using System.Text.Json;

namespace EssenceL10n.Core;

public sealed class MachineTranslationOptions
{
    public string SourceLang { get; init; } = "en";
    public required string TargetLang { get; init; }
    public string Endpoint { get; init; } = "https://translate.googleapis.com/translate_a/single?client=gtx&dt=t";
    public int BatchLines { get; init; } = 20;
    public int MaxUrlLength { get; init; } = 1600;
    public int Concurrency { get; init; } = 3;
    public int DelayMs { get; init; } = 120;
    public int MaxRetries { get; init; } = 4;
    public Action<string>? Log { get; init; }
}

/// <summary>
/// 机器翻译（默认 Google gtx 公开端点）。批量策略：多条文本用换行拼成一个 q，
/// 返回后按换行切回，行数不匹配则该批退化逐条请求——宁可慢，不能错位。
/// </summary>
public static class MachineTranslator
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("EssenceL10n/1.0 (+localization tool)");
        return client;
    }

    public static string[] Translate(IReadOnlyList<string> texts, MachineTranslationOptions options, CancellationToken cancellationToken = default)
    {
        var result = new string?[texts.Count];
        var batches = BuildBatches(texts, options);
        using var gate = new SemaphoreSlim(options.Concurrency);
        var tasks = new List<Task>();

        foreach (var batch in batches)
        {
            tasks.Add(Task.Run(async () =>
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var translated = await TranslateBatchAsync(batch.Select(i => texts[i]).ToList(), options, cancellationToken)
                        .ConfigureAwait(false);
                    if (translated is not null)
                    {
                        for (int k = 0; k < batch.Count; k++) result[batch[k]] = translated[k];
                    }
                    else
                    {
                        foreach (var index in batch)
                        {
                            var single = await TranslateBatchAsync(new List<string> { texts[index] }, options, cancellationToken)
                                .ConfigureAwait(false);
                            if (single is not null && single.Count == 1) result[index] = single[0];
                        }
                    }
                }
                finally
                {
                    gate.Release();
                }
            }, cancellationToken));
        }

        Task.WaitAll(tasks.ToArray(), cancellationToken);
        return result.Select((value, index) => value ?? texts[index]).ToArray();
    }

    private static List<List<int>> BuildBatches(IReadOnlyList<string> texts, MachineTranslationOptions options)
    {
        var batches = new List<List<int>>();
        var current = new List<int>();
        int length = 0;

        for (int i = 0; i < texts.Count; i++)
        {
            var text = texts[i];
            if (text.Length == 0) continue;

            // 含换行的文本单独成批，避免切行对齐失败
            if (text.Contains('\n') || text.Contains('\r'))
            {
                if (current.Count > 0) { batches.Add(current); current = new List<int>(); length = 0; }
                batches.Add(new List<int> { i });
                continue;
            }

            var encoded = Uri.EscapeDataString(text).Length + 3;
            if (current.Count >= options.BatchLines || length + encoded > options.MaxUrlLength)
            {
                if (current.Count > 0) batches.Add(current);
                current = new List<int>();
                length = 0;
            }
            current.Add(i);
            length += encoded;
        }
        if (current.Count > 0) batches.Add(current);
        return batches;
    }

    private static async Task<List<string>?> TranslateBatchAsync(IReadOnlyList<string> batch, MachineTranslationOptions options, CancellationToken cancellationToken)
    {
        var joined = string.Join("\n", batch);
        var url = options.Endpoint
                  + "&sl=" + Uri.EscapeDataString(options.SourceLang)
                  + "&tl=" + Uri.EscapeDataString(options.TargetLang)
                  + "&q=" + Uri.EscapeDataString(joined);

        for (int attempt = 0; attempt <= options.MaxRetries; attempt++)
        {
            try
            {
                if (options.DelayMs > 0) await Task.Delay(options.DelayMs, cancellationToken).ConfigureAwait(false);
                using var response = await Http.GetAsync(url, cancellationToken).ConfigureAwait(false);
                if ((int)response.StatusCode == 429)
                {
                    await Task.Delay(3000 * (attempt + 1), cancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                {
                    options.Log?.Invoke($"HTTP {(int)response.StatusCode} retry={attempt}");
                    await Task.Delay(800 * (attempt + 1), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var parsed = Parse(payload);
                if (parsed is null) continue;

                var lines = parsed.Split('\n');
                if (lines.Length == batch.Count) return lines.ToList();

                options.Log?.Invoke($"行数不匹配 {lines.Length}/{batch.Count}，退化逐条");
                return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                options.Log?.Invoke(ex.GetType().Name + ": " + ex.Message);
                await Task.Delay(800 * (attempt + 1), cancellationToken).ConfigureAwait(false);
            }
        }
        return null;
    }

    private static string? Parse(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0) return null;
            var segments = root[0];
            if (segments.ValueKind != JsonValueKind.Array) return null;
            var builder = new StringBuilder();
            foreach (var segment in segments.EnumerateArray())
            {
                if (segment.ValueKind == JsonValueKind.Array && segment.GetArrayLength() > 0)
                {
                    var piece = segment[0];
                    if (piece.ValueKind == JsonValueKind.String) builder.Append(piece.GetString());
                }
            }
            return builder.ToString();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
