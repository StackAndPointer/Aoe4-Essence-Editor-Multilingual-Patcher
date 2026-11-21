using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Velopack;
using Velopack.Sources;

namespace EssenceL10n.App;

public sealed class FileRow
{
    public string FileName { get; init; } = "";
    public string Units { get; init; } = "";
    public string Translated { get; init; } = "";
    public string Kept { get; init; } = "";
    public string Missing { get; init; } = "";
    public string Coverage { get; init; } = "";
    public double CoverageValue { get; init; }
}

public partial class MainWindow : Window
{
    private readonly ObservableCollection<FileRow> _rows = new();
    private I18n _i18n = I18n.ForCulture(I18n.DetectSystemCulture());
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();

        try { Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://EssenceL10n/Assets/app.ico"))); } catch { }
        try { AppIcon.Source = new Bitmap(AssetLoader.Open(new Uri("avares://EssenceL10n/Assets/icon-256.png"))); } catch { }

        FileList.ItemsSource = _rows;

        foreach (var (code, native) in I18n.Supported)
            UiLangBox.Items.Add(new ComboBoxItem { Content = native, Tag = code });
        UiLangBox.SelectionChanged += (_, _) =>
        {
            if (UiLangBox.SelectedItem is ComboBoxItem { Tag: string code })
            {
                _i18n = I18n.ForCulture(code);
                ApplyLanguage();
            }
        };

        BrowseButton.Click += OnBrowse;
        DetectButton.Click += (_, _) => DetectGameFolder();
        ScanButton.Click += async (_, _) => await RunAsync(ScanAsync);
        ApplyButton.Click += async (_, _) => await RunAsync(ApplyAsync);
        VerifyButton.Click += async (_, _) => await RunAsync(VerifyAsync);
        UpdateButton.Click += async (_, _) => await CheckUpdatesAsync();

        SelectUiLanguage(I18n.DetectSystemCulture());
        ApplyLanguage();
        ReloadPatchLanguages();
        DetectGameFolder(silent: true);
    }

    private void SelectUiLanguage(string code)
    {
        for (int i = 0; i < UiLangBox.ItemCount; i++)
        {
            if (UiLangBox.Items[i] is ComboBoxItem { Tag: string tag } && tag == code)
            {
                UiLangBox.SelectedIndex = i;
                return;
            }
        }
        UiLangBox.SelectedIndex = 0;
    }

    private void ApplyLanguage()
    {
        Title = _i18n["app.title"];
        TitleText.Text = _i18n["app.title"];
        SubtitleText.Text = _i18n["app.subtitle"];
        GameDirLabel.Text = _i18n["field.gameDir"];
        DirectoryBox.Watermark = _i18n["field.gameDir.placeholder"];
        UiLangLabel.Text = _i18n["label.uiLang"];
        PatchLangLabel.Text = _i18n["label.patchLang"];
        BrowseButton.Content = _i18n["btn.browse"];
        DetectButton.Content = _i18n["btn.detect"];
        ScanButton.Content = _i18n["btn.scan"];
        ApplyButton.Content = _i18n["btn.apply"];
        VerifyButton.Content = _i18n["btn.verify"];
        UpdateButton.Content = _i18n["btn.update"];
        BackupBox.Content = _i18n["chk.backup"];
        ColFile.Text = _i18n["col.file"];
        ColUnits.Text = _i18n["col.units"];
        ColTranslated.Text = _i18n["col.translated"];
        ColKept.Text = _i18n["col.kept"];
        ColMissing.Text = _i18n["col.missing"];
        ColCoverage.Text = _i18n["col.coverage"];
        LogTitle.Text = _i18n["log.title"];
        FooterText.Text = "essencel10n.exe · " + (_cliPath ?? "missing");
    }

    private void ReloadPatchLanguages()
    {
        PatchLangBox.Items.Clear();
        var catalogsDir = Path.Combine(AppContext.BaseDirectory, "catalogs");
        var codes = new List<string>();
        if (Directory.Exists(catalogsDir))
            codes.AddRange(Directory.GetDirectories(catalogsDir)
                .Select(Path.GetFileName)
                .Where(name => name is not null && File.Exists(Path.Combine(catalogsDir, name!, name + ".jsonl")))
                .Select(name => name!)
                .OrderBy(name => name, StringComparer.Ordinal));

        foreach (var code in codes) PatchLangBox.Items.Add(new ComboBoxItem { Content = code, Tag = code });
        if (PatchLangBox.ItemCount > 0)
        {
            var system = I18n.DetectSystemCulture();
            var preferred = codes.FirstOrDefault(c => string.Equals(c, system, StringComparison.OrdinalIgnoreCase))
                            ?? codes.FirstOrDefault(c => c.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
                            ?? codes[0];
            PatchLangBox.SelectedIndex = codes.IndexOf(preferred);
        }
    }

    private string? _cliPath => Cli.Locate();

    private void DetectGameFolder(bool silent = false)
    {
        var detected = GameLocator.Detect();
        if (detected is not null)
        {
            DirectoryBox.Text = detected;
            if (!silent) Log("[detect] " + detected);
        }
        else if (!silent)
        {
            Status(_i18n["msg.invalidDir"], "#C9791A");
        }
    }

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = _i18n["field.gameDir"],
            AllowMultiple = false,
        });
        if (folders.Count > 0) DirectoryBox.Text = folders[0].Path.LocalPath;
    }

    private void Status(string text, string color = "#1E9E62")
    {
        StatusText.Text = text;
        StatusText.Foreground = Brush.Parse(color);
    }

    private void Log(string message)
    {
        LogBox.Text = string.IsNullOrEmpty(LogBox.Text) ? message : LogBox.Text + Environment.NewLine + message;
        LogBox.CaretIndex = LogBox.Text?.Length ?? 0;
    }

    private string? CurrentCatalog()
    {
        if (PatchLangBox.SelectedItem is not ComboBoxItem { Tag: string code }) return null;
        var path = Path.Combine(AppContext.BaseDirectory, "catalogs", code, code + ".jsonl");
        return File.Exists(path) ? path : null;
    }

    private string? CurrentDirectory()
    {
        var text = DirectoryBox.Text?.Trim();
        if (string.IsNullOrEmpty(text) || !Directory.Exists(text)) return null;
        return text;
    }

    private async Task RunAsync(Func<string, string, Task> action)
    {
        if (_busy) return;
        var directory = CurrentDirectory();
        if (directory is null) { Status(_i18n["msg.invalidDir"], "#C9791A"); return; }
        var catalog = CurrentCatalog();
        if (catalog is null) { Status(_i18n["msg.noCatalog"], "#C9791A"); return; }
        if (_cliPath is null) { Status(_i18n["msg.cliMissing"], "#C9791A"); return; }

        _busy = true;
        try { await action(directory, catalog); }
        catch (Exception ex)
        {
            Status(_i18n["status.error"] + ": " + ex.Message, "#C9791A");
            Log("ERROR " + ex.GetType().Name + ": " + ex.Message);
        }
        finally { _busy = false; }
    }

    private async Task ScanAsync(string directory, string catalog)
    {
        Status(_i18n["status.scanning"], "#6B7688");
        Log("[scan] " + directory);
        var result = await Cli.RunAsync(new[] { "report", directory, "--catalog", catalog, "--json" });
        if (!result.Ok || result.Json is null)
        {
            Status(_i18n["status.error"], "#C9791A");
            Log(result.StdErr.Length > 0 ? result.StdErr : result.StdOut);
            return;
        }

        _rows.Clear();
        var root = result.Json.RootElement;
        foreach (var item in root.GetProperty("assemblies").EnumerateArray())
        {
            _rows.Add(new FileRow
            {
                FileName = item.GetProperty("file").GetString() ?? "",
                Units = item.GetProperty("total").GetInt32().ToString(CultureInfo.CurrentCulture),
                Translated = item.GetProperty("translated").GetInt32().ToString(CultureInfo.CurrentCulture),
                Kept = item.GetProperty("kept").GetInt32().ToString(CultureInfo.CurrentCulture),
                Missing = item.GetProperty("missing").GetInt32().ToString(CultureInfo.CurrentCulture),
                Coverage = item.GetProperty("coverage").GetDouble().ToString("0.0", CultureInfo.CurrentCulture) + "%",
                CoverageValue = item.GetProperty("coverage").GetDouble(),
            });
        }

        var total = root.GetProperty("total");
        var units = total.GetProperty("units").GetInt32();
        var translated = total.GetProperty("translated").GetInt32();
        var coverage = total.GetProperty("coverage").GetDouble();
        Status(_i18n.F("summary.coverage", units, translated, coverage.ToString("0.0", CultureInfo.CurrentCulture)));
        Log($"[scan] units={units} translated={translated} coverage={coverage:F1}%");
    }

    private async Task ApplyAsync(string directory, string catalog)
    {
        Status(_i18n["status.applying"], "#6B7688");
        var args = new List<string> { "apply", directory, "--catalog", catalog, "--json" };
        if (BackupBox.IsChecked != true) args.Add("--no-backup");
        var result = await Cli.RunAsync(args);
        if (!result.Ok || result.Json is null)
        {
            Status(_i18n["status.error"], "#C9791A");
            Log(result.StdErr.Length > 0 ? result.StdErr : result.StdOut);
            return;
        }

        var total = result.Json.RootElement.GetProperty("total");
        var patched = total.GetProperty("patched").GetInt32();
        var units = total.GetProperty("units").GetInt32();
        foreach (var line in result.StdErr.Split('\n', StringSplitOptions.RemoveEmptyEntries)) Log(line);
        Status(_i18n.F("summary.applied", patched, units));
        Log($"[apply] patched={patched}/{units}");
    }

    private async Task VerifyAsync(string directory, string catalog)
    {
        Status(_i18n["status.verifying"], "#6B7688");
        var result = await Cli.RunAsync(new[] { "verify", directory, "--catalog", catalog, "--json" });
        if (!result.Ok || result.Json is null) { Status(_i18n["status.error"], "#C9791A"); return; }
        var root = result.Json.RootElement;
        Status($"{root.GetProperty("coverage").GetDouble():F1}%");
        Log($"[verify] fromCatalog={root.GetProperty("fromCatalog").GetInt32()} original={root.GetProperty("original").GetInt32()}");
    }

    private async Task CheckUpdatesAsync()
    {
        var configPath = Path.Combine(AppContext.BaseDirectory, "update.json");
        string? repo = null;
        try
        {
            if (File.Exists(configPath))
                repo = JsonDocument.Parse(File.ReadAllText(configPath)).RootElement.TryGetProperty("githubRepo", out var value)
                    ? value.GetString()
                    : null;
        }
        catch { }

        if (string.IsNullOrWhiteSpace(repo))
        {
            Status(_i18n["update.notConfigured"], "#C9791A");
            return;
        }

        try
        {
            var manager = new UpdateManager(new GithubSource("https://github.com/" + repo.Trim(), null, false));
            var update = await manager.CheckForUpdatesAsync();
            if (update is null)
            {
                Status(_i18n["update.none"]);
                return;
            }
            Status(_i18n.F("update.available", update.TargetFullRelease.Version));
            await manager.DownloadUpdatesAsync(update);
            manager.ApplyUpdatesAndRestart(update);
        }
        catch (Exception ex)
        {
            Status(_i18n["status.error"] + ": " + ex.Message, "#C9791A");
        }
    }
}
