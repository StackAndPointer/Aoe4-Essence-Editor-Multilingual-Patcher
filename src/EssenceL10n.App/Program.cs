using System.Globalization;
using Avalonia;
using Velopack;

namespace EssenceL10n.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run();

        // --lang <culture> 可强制界面语言（调试/多语言截图用），否则按系统 UI 语言自动选择
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (!string.Equals(args[i], "--lang", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var culture = new CultureInfo(args[i + 1]);
                CultureInfo.DefaultThreadCurrentUICulture = culture;
                CultureInfo.CurrentUICulture = culture;
            }
            catch (CultureNotFoundException) { /* 忽略非法 culture */ }
            break;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
