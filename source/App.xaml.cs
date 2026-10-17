using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using AISuitePanel.Automation;

namespace AISuitePanel;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            WriteCrashLog(args.ExceptionObject as Exception, "AppDomain");

        // Load persisted state before the shell window appears. Order matters:
        // the product registry decides which slug every key claims, the tool
        // registry decides which boxes exist, and the catalogue needs the cookie
        // vault plus the tool licences to report "Saved Cookies: N" and the
        // per-tool licence status.
        // Before anything can open a browser: make Playwright look at the build
        // that shipped inside this bundle instead of demanding one be downloaded
        // into %LOCALAPPDATA%\ms-playwright. Set on the process so the panel's
        // own Playwright (the Login tab) and every tool process inherit it.
        foreach (var kv in AppPaths.RuntimeEnvironment())
        {
            Environment.SetEnvironmentVariable(kv.Key, kv.Value);
        }

        AppPaths.Ensure();
        ProductCatalog.Load();
        ToolCatalog.Load();
        LicenseService.Load();
        SettingsStore.Load();
        ToolSettingsStore.Load();
        AliasGuard.Load();
        CookieVault.LoadAll();
        AccountStore.LoadAll();
        ToolLicenseService.Load();
        ToolCatalog.RefreshCookieCounts();

        RegisterDrivers();

        // Do not trust a local snapshot: online signed validation is required.
        _ = RefreshLicencesBeforeWindowAsync();
    }

    /// <summary>
    /// Wires the real drivers from the tool registry instead of a hard-coded
    /// list, so a box added to <c>tools.json</c> gets a working driver with no
    /// code change. Ids with no registration fall back to
    /// <see cref="SimulatedToolDriver"/> (placeholder).
    ///   * <c>accio</c>   - shells out to Accio.exe with documented CLI flags.
    ///   * <c>python</c>  - shells out to the user-configured Python script
    ///                      (Z AI, Devin AI, MiniMax AI and any future box).
    ///   * <c>simulated</c> - keeps the placeholder driver.
    /// </summary>
    private static void RegisterDrivers()
    {
        foreach (var tool in ToolCatalog.Tools)
        {
            switch (tool.Driver)
            {
                case "accio":
                    ToolDrivers.Register(new AccioToolDriver());
                    break;

                case "simulated":
                    break;

                default:
                    ToolDrivers.Register(new PythonToolDriver(tool.Id));
                    break;
            }
        }
    }

    private static async Task RefreshLicencesBeforeWindowAsync()
    {
        await LicenseService.RefreshStoredAsync();
        await ToolLicenseService.RefreshStoredAsync();

        if (Current is null)
        {
            Current = new MainWindow();
            Current.Show();
        }

        // Silent, click-free updates: download, verify and swap in the new build
        // in the background; it takes effect on the next launch. The manual
        // CHECK NOW / INSTALL UPDATE buttons stay as the fallback.
        _ = UpdateService.AutoInstallAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Never leave a tool behind. python does NOT die with its parent, so a
        // closed panel used to leave create/login/claim scripts running - one
        // real case was claim_advanced.py --open still alive with a dead parent
        // and its Chrome on screen, and no STOP button to end it.
        try { ProcessGuard.KillAll(); } catch { /* exiting anyway */ }

        base.OnExit(e);
    }

    private static MainWindow? Current { get; set; }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashLog(e.Exception, "Dispatcher");
        e.Handled = true;
        MessageBox.Show(
            Branding.AppName + " hit an unexpected error.\n\n" + e.Exception.Message +
            "\n\nDetails were written to:\n" + CrashLogPath,
            Branding.AppName, MessageBoxButton.OK, MessageBoxImage.Error);
        Shutdown(1);
    }

    public static string CrashLogPath => AppPaths.CrashLog;

    private static void WriteCrashLog(Exception? ex, string source)
    {
        try
        {
            var dir = Path.GetDirectoryName(CrashLogPath)!;
            Directory.CreateDirectory(dir);

            var sb = new StringBuilder();
            sb.AppendLine("==================================================");
            sb.AppendLine($"time   : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"source : {source}");
            sb.AppendLine($"version: {typeof(App).Assembly.GetName().Version}");
            sb.AppendLine("--------------------------------------------------");
            sb.AppendLine(ex?.ToString() ?? "(no exception object)");
            sb.AppendLine();

            File.AppendAllText(CrashLogPath, sb.ToString());
        }
        catch
        {
            // nothing else we can do
        }
    }
}
