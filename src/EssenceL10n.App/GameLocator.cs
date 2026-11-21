namespace EssenceL10n.App;

/// <summary>在 Steam 库与常见目录里找含 EssenceEditor.exe 的那一层。</summary>
public static class GameLocator
{
    public const string Marker = "EssenceEditor.exe";
    private const string GameFolder = "Age of Empires IV";

    public static string? Detect()
    {
        foreach (var candidate in Candidates())
        {
            try
            {
                if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, Marker))) return candidate;
            }
            catch { /* 权限/非法路径忽略 */ }
        }
        return null;
    }

    private static IEnumerable<string> Candidates()
    {
        // 1) 程序自身所在目录及其上层（用户把工具丢进游戏目录时）
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 3 && dir is not null; i++, dir = dir.Parent) yield return dir.FullName;

        // 2) 常见 Steam 库根
        var steamRoots = new List<string>
        {
            @"C:\Program Files (x86)\Steam",
            @"C:\Program Files\Steam",
            @"D:\Steam",
            @"D:\SteamLibrary",
            @"E:\SteamLibrary",
            @"F:\SteamLibrary",
        };

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
            steamRoots.Add(Path.Combine(drive.Name, "SteamLibrary"));
            steamRoots.Add(Path.Combine(drive.Name, "Steam"));
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in steamRoots.Where(Directory.Exists))
        {
            var direct = Path.Combine(root, "steamapps", "common", GameFolder);
            if (seen.Add(direct)) yield return direct;

            var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            foreach (var line in SafeReadLines(vdf))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("\"path\"", StringComparison.OrdinalIgnoreCase)) continue;
                var parts = trimmed.Split('"', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;
                var library = parts[^1].Replace(@"\\", @"\");
                var game = Path.Combine(library, "steamapps", "common", GameFolder);
                if (seen.Add(game)) yield return game;
            }
        }
    }

    private static IEnumerable<string> SafeReadLines(string path)
    {
        try { return File.ReadAllLines(path); }
        catch { return Array.Empty<string>(); }
    }
}
