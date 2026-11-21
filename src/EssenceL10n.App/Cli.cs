using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace EssenceL10n.App;

public sealed class CliResult
{
    public bool Ok { get; init; }
    public string StdOut { get; init; } = "";
    public string StdErr { get; init; } = "";
    public int ExitCode { get; init; }
    public JsonDocument? Json { get; init; }
    public string? Error { get; init; }
}

/// <summary>GUI 只做前端：所有改写操作都通过子进程调用 essencel10n.exe（--json）。</summary>
public static class Cli
{
    public const string FileName = "EssenceL10n.Cli.exe";

    public static string? Locate()
    {
        var local = Path.Combine(AppContext.BaseDirectory, FileName);
        if (File.Exists(local)) return local;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 4 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir.FullName, FileName);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var folder in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(folder.Trim('"'), FileName);
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* 忽略非法 PATH 项 */ }
        }
        return null;
    }

    public static async Task<CliResult> RunAsync(IEnumerable<string> args, CancellationToken cancellationToken = default)
    {
        var exe = Locate();
        if (exe is null)
            return new CliResult { Ok = false, Error = "cli-missing" };

        var info = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = info };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        process.WaitForExit();

        var output = stdout.ToString().Trim();
        JsonDocument? json = null;
        if (output.StartsWith('{'))
        {
            try { json = JsonDocument.Parse(output); }
            catch { json = null; }
        }

        return new CliResult
        {
            Ok = process.ExitCode == 0 && json is not null,
            StdOut = output,
            StdErr = stderr.ToString().Trim(),
            ExitCode = process.ExitCode,
            Json = json,
        };
    }
}
