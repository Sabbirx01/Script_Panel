using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AISuitePanel.Automation;
using AISuitePanel.Models;
using Microsoft.Playwright;
using Microsoft.Win32;

namespace AISuitePanel.Views;

/// <summary>
/// Screen 4 - the Accio AI tool, laid out the way <c>Accio.exe</c>'s GUI dashboard
/// is: four stat cards across the top, then tabs for <b>Dashboard</b>, <b>Create</b>,
/// <b>Login</b>, and <b>Settings</b>. Everything that mutates state actually shells
/// out to <c>Accio.exe</c>, so the panel reads/writes the same files Accio does.
/// </summary>
public partial class AccioPanelView : UserControl
{
    private readonly ToolInfo _tool;
    private readonly ObservableCollection<AccountRecord> _accounts;
    private readonly ObservableCollection<LogEntry> _log = new();
    private readonly ObservableCollection<PoolEntry> _pool = new();
    private readonly ObservableCollection<DomainEntry> _domains = new();
    private readonly ObservableCollection<LoginRow> _loginRows = new();
    private readonly ObservableCollection<MailBanRow> _mailRows = new();

    private CancellationTokenSource? _cts;
    private bool _running;
    private string _viewerText = "";
    private string _viewerName = "";
    // Guards the two PROXY switches (Create run + Login row) while their saved
    // values are written onto them from code when the panel opens.
    private bool _syncingProxy;

    private FileSystemWatcher? _watcherAlice;
    private FileSystemWatcher? _watcherAccts;
    private FileSystemWatcher? _watcherCookies;
    private DateTime _lastDiskRefresh = DateTime.MinValue;

    public AccioPanelView(ToolInfo tool)
    {
        _tool = tool;
        InitializeComponent();

        _accounts = AccountStore.For(tool.Id);

        // Re-read from disk once, before the UI subscribes: Refresh is what
        // assigns the 1..N row numbers, and without it every row opened as #00
        // (which is exactly what the "deleted #00" log line showed).
        AccountStore.Refresh(tool.Id);

        TitleText.Text = $"DASHBOARD > {tool.PanelTitle}";
        SubTitle.Text = $"{tool.Type} - {tool.Tagline}";

        AcctList.ItemsSource  = _accounts;
        PoolList.ItemsSource  = _pool;
        DomainList.ItemsSource = _domains;
        LogList.ItemsSource    = _log;
        ConnTestList.ItemsSource = _connRows;
        LoginList.ItemsSource  = _loginRows;
        if (MailList is not null) MailList.ItemsSource = _mailRows;

        _accounts.CollectionChanged += (_, _) =>
        {
            RefreshStats();
            ReloadLoginRows();
        };

        CookieVault.Changed += OnVaultChanged;
        Log.Appended += OnLogAppended;
        Unloaded += OnUnloaded;

        LoadSettingsFromAccio();
        LoadProxySetupFromEnv();
        ShowTab("dashboard");
        RefreshStats();
        ReloadLoginRows();

        // Set the toggle default AFTER siblings are wired - the XAML
        // Initialized event for IsChecked=True would fire mid-load and NRE.
        //
        // Both switches used to be forced here on every open, so the operator's
        // choice was lost each time the panel was reopened. The saved per-tool
        // values are reflected instead, so ON stays ON and OFF stays OFF.
        SyncProxyToggle();
        SyncLoginProxyToggle();

        // Accio-only extra: the x-trick is create.py's "<address>x" probe step.
        // Default ON (the script's own default) and never offered on any other
        // tool's panel.
        if (XTrickCheck is not null)
        {
            XTrickCheck.IsChecked = true;
            OnXTrickChanged(XTrickCheck, new RoutedEventArgs());
        }

        // Mail always comes from Global Settings - render the summary + default
        // the per-run radios from it, then the per-tool ban list on Settings.
        RefreshMailSource();
        RefreshMailBanList();

        // The SIGNUP URL the create run will be driven to (per-tool, persisted).
        LoadCreateUrlFromSettings();
        LoadTimingSettings();

        StartFileWatchers();
    }

    /// <summary>
    /// Rebuild the Login tab rows from the current account list. Only rows
    /// that have cookies (cookie_count > 0 + cookies_file != null) appear,
    /// so the user only sees accounts they can actually log in with.
    /// </summary>
    private void ReloadLoginRows()
    {
        _loginRows.Clear();

        foreach (var acc in _accounts.Where(a => a.CookieState == CookieState.Saved))
        {
            // The cookie file accio recorded for this row wins; when a row has no
            // recorded name the address pattern is used. Both lookups resolve
            // inside accio's OWN folder (accounts\accio\accounts\cookies) - the
            // folder the panel points accio at - not the tool's source tree.
            var cookieFile = AccountStore.ResolveCookieFile(acc.CookieFile, acc.EmailUsed);
            var label = cookieFile is null
                ? "(in panel vault)"
                : Path.GetFileName(cookieFile);

            _loginRows.Add(new LoginRow
            {
                Account          = acc,
                Id               = acc.Id,
                IdLabel          = acc.IdLabel,
                EmailUsed        = acc.EmailUsed,
                CookieLabel      = acc.CookieLabel,
                CookieFileLabel  = label,
                CookieFilePath   = cookieFile,
                Selected         = false
            });
        }

        UpdateLoginCounts();
    }

    /// <summary>
    /// Accio's cookie file for an address when no recorded name is available.
    /// Shared with the account store so the name pattern lives in exactly one
    /// place.
    /// </summary>
    private static string? LookupCookieFile(string email) =>
        AccountStore.FindCookieFileForEmail(email);

    private void UpdateLoginCounts()
    {
        if (LoginCookieCount is null) return;

        var totalAccounts = _accounts.Count;
        var withCookies = _loginRows.Count;
        LoginCookieCount.Text = $"Accounts with cookies: {withCookies}  /  Total: {totalAccounts}";
    }

    private void OnLoginSelectAll(object sender, RoutedEventArgs e)
    {
        foreach (var r in _loginRows) r.Selected = true;
    }

    private void OnLoginSelectNone(object sender, RoutedEventArgs e)
    {
        foreach (var r in _loginRows) r.Selected = false;
    }

    /// <summary>
    /// Per-row Delete: removes the account from Accio's accounts.json and the
    /// matching cookie file. With confirm dialog.
    /// </summary>
    private void OnDeleteAccount(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LoginRow row }) return;
        if (row.Account is null) return;

        var ans = MessageBox.Show(
            $"Delete account #{row.IdLabel} ({row.EmailUsed})?\n\n" +
            "This will remove the row from Accio's accounts/accounts.json and " +
            "delete the matching cookie file. " +
            "This cannot be undone.",
            "Delete account", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (ans != MessageBoxResult.OK) return;

        // Delete reports whether the row really left accounts.json. The log line
        // used to be printed unconditionally, so a delete that had silently hit
        // the wrong path still read as "deleted" - and the row reappeared on the
        // refresh right below this.
        var deleted = AccountStore.Delete(_tool.Id, row.Account);

        AccountStore.Refresh(_tool.Id);
        ReloadLoginRows();
        RefreshStats();

        if (deleted)
        {
            Log.Success(_tool.Id,
                $"#{row.IdLabel} ({row.EmailUsed}) deleted from accounts.json (+ its cookie file).");
        }
        else
        {
            Log.Error(_tool.Id,
                $"#{row.IdLabel} ({row.EmailUsed}) was NOT deleted - no matching row in " +
                $"{AccountStore.AccioAccountsJsonPath()}. Nothing was changed.");
        }
    }

    /// <summary>
    /// Per-row Login: tries Playwright (Chromium + cookies) first so the user
    /// sees a real browser with the session restored. If the Playwright driver
    /// isn't available, falls back to launching Accio.exe's actual GUI with
    /// --keep-open + --email-override.
    /// </summary>
    private async void OnLoginOne(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LoginRow row }) return;

        var cookies = await LoadCookiesForRowAsync(row);
        if (cookies.Count == 0)
        {
            Log.Warn(_tool.Id,
                $"No cookies found for #{row.IdLabel} ({row.EmailUsed}) - looked for " +
                $"\"{Path.GetFileName(row.Account?.CookieFile ?? "")}\" in " +
                $"{AccountStore.AccioCookiesDir()} and in the panel vault.");
            return;
        }

        var useProxy = LoginProxyToggle.IsChecked == true;
        var proxyUrl = useProxy ? await PickFirstAliveProxyAsync() : null;

        AppendLog(
            $"🔑 Login #{row.IdLabel} ({row.EmailUsed}) - launching browser with {cookies.Count} cookies" +
            (proxyUrl is null ? " (direct)" : $" via {proxyUrl}") +
            "...");

        try
        {
            await LaunchBrowserWithCookiesAsync(row.EmailUsed, cookies, proxyUrl).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn(_tool.Id,
                $"Playwright launch failed ({ex.GetType().Name}: {ex.Message}). " +
                "Falling back to Accio.exe GUI.");
            try
            {
                LaunchAccioGui(row.EmailUsed);
            }
            catch (Exception ex2)
            {
                Log.Error(_tool.Id, "Accio.exe GUI launch also failed: " + ex2.Message);
            }
        }
    }

    private void OnLoginProxyToggleChanged(object sender, RoutedEventArgs e)
    {
        if (LoginProxyToggle is null) return;
        var on = LoginProxyToggle.IsChecked == true;
        UpdateLoginProxyToggleText(on);

        // Restore-on-open path - see OnProxyToggleChanged.
        if (_syncingProxy) return;

        ToolSettingsStore.SetLoginProxyOn(_tool.Id, on);
    }

    /// <summary>Keeps the Login-row proxy switch's label and hint in step with the state.</summary>
    private void UpdateLoginProxyToggleText(bool on)
    {
        if (LoginProxyToggle is not null) LoginProxyToggle.Content = on ? "ON" : "OFF";
        if (LoginProxyHint is not null)
            LoginProxyHint.Text = on
                ? "ON → use first alive proxy from .env / _fallback_proxies.txt."
                : "OFF → direct connection (current network).";
    }

    /// <summary>Reflects the persisted per-tool Login-row PROXY flag on the switch.</summary>
    private void SyncLoginProxyToggle()
    {
        if (LoginProxyToggle is null) return;
        _syncingProxy = true;
        try
        {
            var on = ToolSettingsStore.IsLoginProxyOn(_tool.Id);
            LoginProxyToggle.IsChecked = on;
            UpdateLoginProxyToggleText(on);
        }
        finally
        {
            _syncingProxy = false;
        }
    }

    /// <summary>
    /// Pick the first proxy that answers a HEAD probe. Reads from
    /// <c>_fallback_proxies.txt</c> first, then <c>ACCIO_PROXY_API</c> / <c>ACCIO_PROXY_SOURCES</c>
    /// from <c>.env</c> (each as a candidate URL).
    /// </summary>
    private async Task<string?> PickFirstAliveProxyAsync()
    {
        var candidates = new List<string>();

        // 1. Plain list file
        var plain = Path.Combine(AccioToolDriver.AccioRoot, "_fallback_proxies.txt");
        if (File.Exists(plain))
        {
            foreach (var line in File.ReadAllLines(plain))
            {
                var s = line.Trim();
                if (!string.IsNullOrWhiteSpace(s) && !s.StartsWith("#"))
                    candidates.Add(s);
            }
        }

        // 2. .env URLs (just informational - we'd have to fetch them).
        // We don't fetch live here, only direct URLs are usable.

        if (candidates.Count == 0)
        {
            Log.Warn(_tool.Id,
                "Proxy toggle is ON but no proxies found in _fallback_proxies.txt. " +
                "Add lines (http://ip:port) or set ACCIO_PROXY_API in .env via Settings → Proxy Setup.");
            return null;
        }

        // Probe each candidate with a 5s timeout
        foreach (var candidate in candidates)
        {
            if (await ProbeProxyAliveAsync(candidate)) return candidate;
        }

        Log.Warn(_tool.Id, $"Proxy toggle ON but no candidate answered ({candidates.Count} tried). Falling back to direct.");
        return null;
    }

    private static async Task<bool> ProbeProxyAliveAsync(string proxy)
    {
        try
        {
            var handler = new HttpClientHandler
            {
                Proxy = new System.Net.WebProxy(proxy),
                UseProxy = true,
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
            using var req = new HttpRequestMessage(HttpMethod.Head, "https://www.accio.com/login");
            using var resp = await http.SendAsync(req);
            return resp.StatusCode != System.Net.HttpStatusCode.RequestTimeout;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Open Chromium, load every cookie, navigate to the user-specified URL.
    /// On timeout/error we surface a clear message instead of leaving an empty
    /// browser window open — and auto-fall-back to Accio.exe's GUI when direct
    /// navigation doesn't return a page at all.
    /// </summary>
    private async Task LaunchBrowserWithCookiesAsync(string email, List<BrowserCookie> cookies, string? proxyUrl = null)
    {
        var targetUrl = (LoginTargetUrlBox?.Text ?? "https://www.accio.com/").Trim();
        if (string.IsNullOrWhiteSpace(targetUrl)) targetUrl = "https://www.accio.com/";

        // Convert panel cookies to Playwright's Cookie shape.
        var pwCookies = cookies.Select(c => new Microsoft.Playwright.Cookie
        {
            Name     = c.Name,
            Value    = c.Value,
            Domain   = NormalisePwDomain(c.Domain),
            Path     = string.IsNullOrEmpty(c.Path) ? "/" : c.Path,
            Expires  = (float)c.Expires,
            HttpOnly = c.HttpOnly,
            Secure   = c.Secure,
            SameSite = ParseSameSite(c.SameSite)
        }).ToArray();

        IPlaywright? playwright = null;
        IBrowser? browser = null;
        IBrowserContext? context = null;
        IPage? page = null;
        var browserReady = false;

        try
        {
            playwright = await Playwright.CreateAsync();
            var launchOpts = new BrowserTypeLaunchOptions
            {
                Headless = false,
                Args     = new[] { "--disable-blink-features=AutomationControlled", "--no-sandbox" }
            };

            if (!string.IsNullOrWhiteSpace(proxyUrl))
            {
                launchOpts.Proxy = new Proxy { Server = proxyUrl };
            }

            browser = await playwright.Chromium.LaunchAsync(launchOpts);
            browserReady = true;

            context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize { Width = 1280, Height = 800 },
                UserAgent    = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36"
            });

            await context.AddCookiesAsync(pwCookies);

            page = await context.NewPageAsync();

            // 10s is enough when proxy is healthy; direct (proxy-off) fails faster.
            var response = await page.GotoAsync(targetUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout   = 10_000
            });

            if (response is null || !response.Ok)
            {
                AppendLog($"⚠ {targetUrl} → HTTP {response?.Status ?? 0}. Page may not be loaded. Browser stays open so you can navigate manually.");
            }
            else
            {
                AppendLog($"✓ Loaded {targetUrl}  ({response.Status}). Browser ready - cookies active.");
            }
        }
        catch (Exception ex)
        {
            Log.Warn(_tool.Id,
                $"Playwright could not load {targetUrl} ({ex.GetType().Name}: {ex.Message}).");

            // Close whatever Playwright opened before reporting.
            try { if (browser is not null) await browser.CloseAsync(); } catch { }
            try { if (playwright is not null) playwright.Dispose(); } catch { }
            playwright = null;
            browser    = null;
            context    = null;
            page       = null;

            // Auto-fallback to Accio.exe GUI when direct fails entirely.
            try
            {
                Log.Info(_tool.Id, "Falling back to Accio.exe GUI launch with this account.");
                LaunchAccioGui(email);
            }
            catch (Exception ex2)
            {
                Log.Error(_tool.Id, "Accio.exe GUI launch also failed: " + ex2.Message);
            }
            return;
        }

        // Park a forever-loop that does nothing, so the context doesn't get
        // GC'd while the user is using the browser. The process exits when the
        // browser window closes.
        _ = Task.Run(async () =>
        {
            while (browserReady && page is { IsClosed: false })
                await Task.Delay(1000).ConfigureAwait(false);

            try { if (context is not null) await context.DisposeAsync(); } catch { }
            try { if (browser is not null) await browser.DisposeAsync(); } catch { }
        });
    }

    private static void LaunchAccioGui(string email)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName         = AccioToolDriver.AccioExePath,
            WorkingDirectory = AccioToolDriver.AccioRoot,
            UseShellExecute  = false,
            CreateNoWindow   = false
        };
        startInfo.ArgumentList.Add("--keep-open");
        startInfo.ArgumentList.Add("--email-override");
        startInfo.ArgumentList.Add(email);

        Process.Start(startInfo);
    }

    private static string NormalisePwDomain(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return ".accio.com";
        return domain.StartsWith('.') ? domain : "." + domain;
    }

    private static SameSiteAttribute ParseSameSite(string? value) => value?.ToLowerInvariant() switch
    {
        "strict" => SameSiteAttribute.Strict,
        "none"   => SameSiteAttribute.None,
        _        => SameSiteAttribute.Lax
    };

    private async void OnLoginVerifySelected(object sender, RoutedEventArgs e)
    {
        var selected = _loginRows.Where(r => r.Selected).ToList();
        if (selected.Count == 0)
        {
            Log.Warn(_tool.Id, "No accounts selected. Tick the boxes first.");
            return;
        }

        LoginVerifyBtn.IsEnabled = false;
        AppendLog($"🔑 VERIFY LOGIN - testing {selected.Count} cookie set(s) against accio.com...");

        // The post-loop UI updates (button enable, summary log) need the UI
        // thread, so DON'T use ConfigureAwait(false) here - let the continuation
        // resume on the captured SynchronizationContext.
        foreach (var row in selected)
        {
            var (ok, info) = await ProbeCookieAsync(row);
            row.LastVerify = ok ? "✓ valid" : "✗ expired";
            row.LastVerifyColor = ok
                ? new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99))
                : new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
            AppendLog($"  {row.EmailUsed}  →  {(ok ? "✓" : "✗")} {info}");
        }

        AppendLog("🔑 VERIFY LOGIN done.  Rows above show ✓ valid / ✗ expired.");
        LoginVerifyBtn.IsEnabled = true;
    }

    /// <summary>
    /// Make a HEAD request to accio.com/dashboard with the account's cookies.
    /// 200 OK = session is still valid.  302 → /login = cookies expired.
    /// </summary>
    private static async Task<(bool, string)> ProbeCookieAsync(LoginRow row)
    {
        // Prefer the Accio cookie file - it has the full set; fall back to panel vault.
        var cookies = await LoadCookiesForRowAsync(row).ConfigureAwait(false);
        if (cookies.Count == 0)
            return (false, "no cookies on disk");

        try
        {
            var handler = new HttpClientHandler
            {
                UseCookies = false,   // we set Cookie header manually
                AllowAutoRedirect = false,
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };

            var cookieHeader = string.Join("; ",
                cookies.Select(c => $"{c.Name}={c.Value}"));

            var req = new HttpRequestMessage(HttpMethod.Get, "https://www.accio.com/");
            req.Headers.Add("Cookie", cookieHeader);
            req.Headers.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");

            using var resp = await http.SendAsync(req).ConfigureAwait(false);
            var status = (int)resp.StatusCode;
            var location = resp.Headers.Location?.OriginalString ?? "";

            if (status >= 200 && status < 400 &&
                !location.Contains("/login", StringComparison.OrdinalIgnoreCase))
                return (true, $"HTTP {status} (session valid)");

            if (status is 301 or 302 or 303 or 307 or 308 && location.Contains("/login", StringComparison.OrdinalIgnoreCase))
                return (false, $"HTTP {status} → /login (expired)");

            return (false, $"HTTP {status} (uncertain)");
        }
        catch (Exception ex)
        {
            return (false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static async Task<List<BrowserCookie>> LoadCookiesForRowAsync(LoginRow row)
    {
        // First preference: the Accio cookie file
        if (!string.IsNullOrEmpty(row.CookieFilePath) && File.Exists(row.CookieFilePath))
        {
            try
            {
                var raw = await File.ReadAllTextAsync(row.CookieFilePath).ConfigureAwait(false);
                var cookies = JsonSerializer.Deserialize<List<BrowserCookie>>(raw, JsonOptions);
                return cookies ?? new List<BrowserCookie>();
            }
            catch { /* try panel vault next */ }
        }

        // Fallback: panel vault
        var rec = CookieVault.Get(row.Account?.EmailUsed ?? "", row.Account?.Id ?? 0);
        if (rec is null)
        {
            // CookieVault is keyed by toolId - try that
            var toolRec = CookieVault.Get("accio", row.Id);
            return toolRec?.Cookies.ToList() ?? new List<BrowserCookie>();
        }
        return rec.Cookies.ToList();
    }

    private void StartFileWatchers()
    {
        // Watch the folders accio ACTUALLY writes: the accounts file inside the
        // tool's state folder (accounts\accio\accounts\accounts.json) and the
        // sibling cookies\ folder. Watching the tool's source folder - where
        // neither lives - meant a run that finished produced no auto-refresh at
        // all, so new rows and their cookies only appeared after a manual click.
        try
        {
            var accountsDir = AccountStore.AccioAccountsDir();
            var cookiesDir  = AccountStore.AccioCookiesDir();

            if (!string.IsNullOrWhiteSpace(accountsDir) && Directory.Exists(accountsDir))
            {
                _watcherAccts = new FileSystemWatcher(accountsDir, "accounts.json")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = true
                };
                _watcherAccts.Changed += (_, _) => ScheduleDiskRefresh("accounts.json");
                _watcherAccts.Created += (_, _) => ScheduleDiskRefresh("accounts.json");
            }

            if (!string.IsNullOrWhiteSpace(cookiesDir) && Directory.Exists(cookiesDir))
            {
                _watcherCookies = new FileSystemWatcher(cookiesDir, "*.json")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                    EnableRaisingEvents = true
                };
                _watcherCookies.Created += (_, _) => ScheduleDiskRefresh("cookie file");
                _watcherCookies.Changed += (_, _) => ScheduleDiskRefresh("cookie file");
            }

            // alice.json is accio's own mail pool and lives beside its scripts -
            // watched only when that folder exists.
            var accioRoot = AccioToolDriver.AccioRoot;
            if (!string.IsNullOrWhiteSpace(accioRoot) && Directory.Exists(accioRoot))
            {
                _watcherAlice = new FileSystemWatcher(accioRoot, "alice.json")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = true
                };
                _watcherAlice.Changed += (_, _) => ScheduleDiskRefresh("alice.json");
                _watcherAlice.Created += (_, _) => ScheduleDiskRefresh("alice.json");
            }
        }
        catch (Exception ex)
        {
            Log.Warn(_tool.Id, "Could not start file watchers: " + ex.Message);
        }
    }

    /// <summary>
    /// Debounced refresh - Accio.exe may write alice.json + accounts.json in
    /// quick succession; we coalesce them into one UI refresh per 750 ms.
    /// </summary>
    private void ScheduleDiskRefresh(string source)
    {
        var now = DateTime.UtcNow;
        var sinceLast = (now - _lastDiskRefresh).TotalMilliseconds;
        if (sinceLast < 750) return;

        _lastDiskRefresh = now;
        Dispatcher.Invoke(() =>
        {
            LoadSettingsFromAccio();
            AccountStore.Refresh(_tool.Id);
            RefreshStats();
            ReloadLoginRows();
            Log.Info(_tool.Id, $"🔄 auto-refresh ({source} changed on disk)");
        });
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        CookieVault.Changed -= OnVaultChanged;
        Log.Appended -= OnLogAppended;

        _watcherAlice?.Dispose();
        _watcherAccts?.Dispose();
        _watcherCookies?.Dispose();
        _watcherAlice = null;
        _watcherAccts = null;
        _watcherCookies = null;

        if (_running)
        {
            _cts?.Cancel();
            Log.Warn(_tool.Id, "Panel closed while a run was active - the run was cancelled.");
        }
    }

    // -------------------------------------------------------------- tab switch

    private void ShowTab(string which)
    {
        TabDashboard.Visibility = which == "dashboard" ? Visibility.Visible : Visibility.Collapsed;
        TabCreate.Visibility    = which == "create"    ? Visibility.Visible : Visibility.Collapsed;
        TabLogin.Visibility     = which == "login"     ? Visibility.Visible : Visibility.Collapsed;
        TabSettings.Visibility  = which == "settings"  ? Visibility.Visible : Visibility.Collapsed;

        // Highlight selected tab button (subtle)
        TabDashBtn.Opacity   = which == "dashboard" ? 1.0 : 0.55;
        TabCreateBtn.Opacity = which == "create"    ? 1.0 : 0.55;
        TabLoginBtn.Opacity  = which == "login"     ? 1.0 : 0.55;
        TabSetBtn.Opacity    = which == "settings"  ? 1.0 : 0.55;
    }

    private void OnTabDashboard(object sender, RoutedEventArgs e) => ShowTab("dashboard");
    private void OnTabCreate   (object sender, RoutedEventArgs e) => ShowTab("create");
    private void OnTabLogin    (object sender, RoutedEventArgs e) => ShowTab("login");
    private void OnTabSettings(object sender, RoutedEventArgs e)
    {
        // Ban list is derived from Global Settings + this tool's exclusions, so
        // rebuild it on every visit - the user may have edited Global Settings
        // in the meantime.
        RefreshMailBanList();
        ShowTab("settings");
    }

    // -------------------------------------------------------------- stats row

    private void RefreshStats()
    {
        var accounts = _accounts.ToList();
        var today = DateTime.Today.ToString("yyyy-MM-dd");

        StatTotal.Text        = accounts.Count.ToString();
        StatActiveToday.Text  = accounts.Count(a =>
            (a.CreatedAt.ToString("yyyy-MM-dd")) == today &&
            (a.Status == AccountStatus.Active || a.CookieState == CookieState.Saved)
        ).ToString();

        var lastCreated = accounts.LastOrDefault();
        StatLastRun.Text = lastCreated is null
            ? "—"
            : lastCreated.CreatedAt.ToString("HH:mm:ss  •  ") + (lastCreated.CreatedAt.ToString("yyyy-MM-dd"));

        StatPool.Text = _pool.Count.ToString();

        AcctEmpty.Visibility = accounts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnVaultChanged(string toolId)
    {
        if (!string.Equals(toolId, _tool.Id, StringComparison.OrdinalIgnoreCase)) return;
        Dispatcher.Invoke(() =>
        {
            var n = CookieVault.CountFor(_tool.Id);
            LoginCookieCount.Text = $"Total Available Cookies: {n}";
        });
    }

    // -------------------------------------------------------------- create

    /// <summary>
    /// The mail source used when the user has not overridden it - derived from
    /// <b>Global Settings</b>, exactly like <c>PythonToolPanelView</c> and
    /// <c>ToolPanelView</c> do. Whichever method Global Settings has fully
    /// configured wins, in the same priority order everywhere, so "where does
    /// the mail come from?" has one answer across the whole panel.
    /// </summary>
    private MailSource AutoSource()
    {
        // GMAIL FIRST - deliberately different from the other panels.
        //
        // The shared panel order puts Custom mail and Custom domain first, which
        // on an older launch handed Accio a "@automationservice.online" address
        // before the driver passed --allow-non-gmail, and the run died on the
        // guard ("FATAL: Accio create is Gmail-only").  Gmail therefore stays the
        // default so a fresh, unattended launch behaves the way it always has;
        // Custom Domain is only auto-picked when no Gmail method is configured.
        if (SettingsStore.DottedGmailUsable) return MailSource.DottedGmail;
        if (SettingsStore.CustomMailUsable)  return MailSource.CustomMail;
        // Custom Domain is a real Accio method now - the OTP comes out of the
        // Method 3 forward target over IMAP - so it is preferred over a Gmail
        // method that is not actually configured.
        if (SettingsStore.CustomDomainUsable) return MailSource.CustomDomain;
        return MailSource.DottedGmail;
    }

    /// <summary>
    /// What the user actually picked on this screen. Test is handled separately
    /// in <see cref="OnRun"/>, so this never returns "test".
    /// </summary>
    private MailSource SelectedMode() =>
        ModeTest.IsChecked   == true ? AutoSource()
      : ModePool.IsChecked   == true ? MailSource.DottedGmail
      : ModeTemp.IsChecked   == true ? MailSource.TempMail
      : ModeDomain.IsChecked == true ? MailSource.CustomDomain
      : ModeCustom.IsChecked == true ? MailSource.CustomMail
      : ModeSemi.IsChecked   == true ? MailSource.SemiAuto
      : ModeNoPx.IsChecked   == true ? MailSource.NoProxy
      : ModeOutlook.IsChecked == true ? MailSource.OutlookPool
      : AutoSource();

    /// <summary>
    /// Renders the Global Settings summary cards on the Create tab and defaults
    /// the per-run radios to <see cref="AutoSource"/>. Read-only: Global
    /// Settings is the only place these values are edited.
    /// </summary>
    private void RefreshMailSource()
    {
        if (GmailPoolSummary is null) return;

        var s = SettingsStore.Current;

        var pool = s.DottedGmail?.Accounts?
            .Where(a => SettingsStore.IsGmail(a.Email) && !string.IsNullOrWhiteSpace(a.AppPassword))
            .ToList() ?? new List<GmailEntry>();
        GmailPoolSummary.Text = pool.Count == 0
            ? "(empty — add one in Global Settings → Method 2)"
            : $"{pool.Count} Gmail{(pool.Count == 1 ? "" : "s")}  →  " +
              $"{string.Join(", ", pool.Take(3).Select(g => g.Email))}{(pool.Count > 3 ? ", ..." : "")}";

        var d = s.CustomDomain;
        DomainSummary.Text = string.IsNullOrWhiteSpace(d.Domain)
            ? "(none configured)"
            : $"{d.Domain}  →  {(string.IsNullOrWhiteSpace(d.ForwardTarget) ? "(no forward target)" : d.ForwardTarget)}";

        var cm = s.CustomMail;
        CustomMailSummary.Text = string.IsNullOrWhiteSpace(cm.Email)
            ? "(none configured)"
            : $"{cm.Email}  (app password: {(string.IsNullOrWhiteSpace(cm.AppPassword) ? "MISSING" : "set")})";

        var auto = AutoSource();
        ModePool.IsChecked   = auto == MailSource.DottedGmail;
        ModeTemp.IsChecked   = auto == MailSource.TempMail;
        ModeDomain.IsChecked = auto == MailSource.CustomDomain;
        ModeCustom.IsChecked = auto == MailSource.CustomMail;
        ModeSemi.IsChecked   = auto == MailSource.SemiAuto;
        ModeNoPx.IsChecked   = auto == MailSource.NoProxy;
        ModeOutlook.IsChecked = auto == MailSource.OutlookPool;
        ModeTest.IsChecked   = false;

        // Every mode create.py can actually run is offered. Nothing here is
        // disabled for being "Gmail-only" any more - that was true of an older
        // build, and leaving the radios greyed out made working methods look
        // broken:
        //
        //   * Temp mail: the driver passes --temp-mail, create.py switches to
        //     PanelTempInbox and polls the panel's own Guerrilla / 1secmail
        //     address for the code. No IMAP involved, so the Gmail guard does
        //     not apply.
        //   * Custom Domain: the address is forced with --email-override,
        //     --allow-non-gmail bypasses the guard, and the forwarded copy is
        //     read from the Method 3 forward target over IMAP.
        //   * Custom Mail: one Gmail + app password, used once. Needs a single
        //     account with an app password, which Method 4 supplies directly -
        //     and the driver already falls back to a pool Gmail or the Method 3
        //     forward target otherwise, so the radio stays live.
        ModeTemp.IsEnabled   = SettingsStore.Usable(MailSource.TempMail);
        ModeDomain.IsEnabled = SettingsStore.Usable(MailSource.CustomDomain);
        ModePool.IsEnabled   = SettingsStore.Usable(MailSource.DottedGmail);
        ModeCustom.IsEnabled = SettingsStore.Usable(MailSource.CustomMail)
                               || SettingsStore.DottedGmailUsable
                               || SettingsStore.CustomDomainUsable;
        ModeSemi.IsEnabled   = SettingsStore.Usable(MailSource.SemiAuto);
        ModeNoPx.IsEnabled   = SettingsStore.Usable(MailSource.NoProxy);

        // Method 5 - create.py reads the code from the token mailbox itself
        // (outlook_pool.wait_for_otp), so the radio is live as soon as the pool
        // holds a row and is switched on.
        ModeOutlook.IsEnabled = SettingsStore.Usable(MailSource.OutlookPool);

        // Tell the operator exactly what each method will do, and what it will
        // use, rather than only which one is auto-picked.
        var customAccount = SettingsStore.CustomMailAccount();
        MailModeHint.Text =
            $"Auto-pick: {CreateAccountRunner.SourceLabel(auto)}.  "
            + "Temp mail (2) gets a disposable inbox from the panel and polls it for the code. "
            + (customAccount.Email.Length > 0
                ? $"Custom mail (4) will sign up with {customAccount.Email} "
                  + (SettingsStore.CustomMailUsable ? "(Method 4)." : "(your pool account — Method 4 is empty).")
                : "Custom mail (4) needs a Gmail plus its app password in Method 4.")
            + (SettingsStore.CustomDomainUsable
                ? "  Custom domain (3) reads the code from the Method 3 forward target over IMAP."
                : "  Custom domain (3) needs Global Settings → Method 3 filled in.");
    }

    // ------------------------------------------------------- signup URL (create)

    /// <summary>
    /// Renders the SIGNUP URL the create run will be driven to - the persisted
    /// per-tool value or the built-in default - so the box is never blank and the
    /// operator can see exactly what is handed to the tool as <c>--url</c>.
    /// </summary>
    private void LoadCreateUrlFromSettings()
    {
        if (CreateUrlBox is null) return;

        var url = ToolSettingsStore.CreateUrl(_tool.Id);
        CreateUrlBox.Text = url;

        if (CreateUrlHint is not null)
        {
            CreateUrlHint.Text = string.IsNullOrWhiteSpace(url)
                ? "Empty - the tool uses its own built-in signup page."
                : $"Create run is driven to:  {url}   (passed as  --url)" +
                  (ToolSettingsStore.For(_tool.Id).CreateUrl is null ? "   [default]" : "   [saved for this tool]");
        }
    }

    /// <summary>
    /// Persists the SIGNUP URL when the operator leaves the box, so the next run -
    /// and the next launch of the panel - uses the same page. Empty input snaps
    /// back to the per-tool default instead of leaving the box blank.
    /// </summary>
    private void OnCreateUrlChanged(object sender, RoutedEventArgs e)
    {
        if (CreateUrlBox is null) return;

        var before = ToolSettingsStore.CreateUrl(_tool.Id);
        ToolSettingsStore.SetCreateUrl(_tool.Id, CreateUrlBox.Text);
        var after = ToolSettingsStore.CreateUrl(_tool.Id);

        LoadCreateUrlFromSettings();

        if (!string.Equals(before, after, StringComparison.OrdinalIgnoreCase))
            Log.Info(_tool.Id, $"Signup URL for create runs → {after}");
    }

    /// <summary>
    /// The URL this run should sign up on: whatever is in the box right now (so
    /// typing a link and hitting ▶ RUN works with no extra step), normalised and
    /// persisted, falling back to the saved/default value. Never empty.
    /// </summary>
    private string ResolveSignupUrl()
    {
        var fallback = ToolSettingsStore.DefaultCreateUrl(_tool.Id);

        if (CreateUrlBox is not null && !string.IsNullOrWhiteSpace(CreateUrlBox.Text))
        {
            var typed = ToolSettingsStore.NormaliseLoginUrl(CreateUrlBox.Text, fallback);
            ToolSettingsStore.SetCreateUrl(_tool.Id, typed);
            LoadCreateUrlFromSettings();
            return typed;
        }

        return ToolSettingsStore.CreateUrl(_tool.Id);
    }

    private void LoadTimingSettings()
    {
        var s = ToolSettingsStore.For(_tool.Id);
        CaptchaWaitBox.Text = Math.Max(0, s.CaptchaWaitSeconds).ToString("0.###", CultureInfo.InvariantCulture);
        XTrickWaitBox.Text = Math.Max(0, s.XTrickWaitSeconds).ToString("0.###", CultureInfo.InvariantCulture);
        ActionDelayBox.Text = Math.Max(0, s.ActionDelayMs).ToString(CultureInfo.InvariantCulture);
    }

    private void SaveTimingSettings()
    {
        var s = ToolSettingsStore.For(_tool.Id);
        if (double.TryParse(CaptchaWaitBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var captcha))
            s.CaptchaWaitSeconds = Math.Clamp(captcha, 0, 120);
        if (double.TryParse(XTrickWaitBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var xwait))
            s.XTrickWaitSeconds = Math.Clamp(xwait, 0, 120);
        if (int.TryParse(ActionDelayBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var action))
            s.ActionDelayMs = Math.Clamp(action, 0, 120000);
        ToolSettingsStore.Save(_tool.Id, s);
    }

    private void OnDecimalOnly(object sender, TextCompositionEventArgs e)
    {
        e.Handled = e.Text.Any(c => !char.IsDigit(c) && c != '.');
    }

    private void OnXTrickChanged(object sender, RoutedEventArgs e)
    {
        if (XTrickCheck is null) return;
        var on = XTrickCheck.IsChecked == true;
        XTrickCheck.Content = on
            ? "🧩  x-trick: ON  (type \"<address>x\", walk the Change step, drop the x)"
            : "🧩  x-trick: OFF  (--no-x-trick: type the address straight away)";
        if (XTrickHint is not null)
            XTrickHint.Text = on
                ? "Accio-only step. Turn it off only if the probe page starts failing."
                : "Panel will append --no-x-trick to create.py.";
    }

    private bool _countUnlimited;

    private void OnCountUnlimited(object sender, RoutedEventArgs e)
    {
        _countUnlimited = !_countUnlimited;
        CountBox.IsEnabled = !_countUnlimited;
        CountBox.Text = _countUnlimited ? "∞" : "1";
        CountBox.ToolTip = _countUnlimited
            ? "Unlimited - runs until ⏹ STOP or daily cap reached."
            : "Numbers only.";
        UpdateBatchHint();
    }

    private void UpdateBatchHint()
    {
        var maxRun = int.TryParse(MaxPerRunBox.Text, out var r) ? r : 50;
        var maxDay = int.TryParse(MaxPerDayBox.Text, out var d) ? d : 9999;

        BatchHint.Text = _countUnlimited
            ? $"∞ Unlimited: hit ⏹ STOP to break.  Cap this run at {maxRun}, this day at {maxDay}."
            : $"Run at most {maxRun} accounts / batch, {maxDay} / day.  Click ∞ for no cap.";
    }

    private async void OnRun(object sender, RoutedEventArgs e)
    {
        if (_running) return;

        // Parse count: support "∞" symbol for unlimited.
        int qty;
        var raw = CountBox.Text.Trim();
        if (_countUnlimited || raw == "∞")
        {
            var maxRun = int.TryParse(MaxPerRunBox.Text.Trim(), out var r) ? r : 50;
            qty = maxRun;
        }
        else if (!int.TryParse(raw, out qty) || qty < 1)
        {
            Log.Warn(_tool.Id, "Count must be a positive integer, or click ∞ for unlimited.");
            return;
        }

        // Test mode — simulated run, no real signup, no Accio.exe invocation.
        if (ModeTest.IsChecked == true)
        {
            await RunTestSimulationAsync(qty);
            return;
        }

        var mode = SelectedMode();
        var xTrick = XTrickCheck is null || XTrickCheck.IsChecked == true;
        var signupUrl = ResolveSignupUrl();
        SaveTimingSettings();
        AppendLog($"▶ RUN — mode={CreateAccountRunner.SourceLabel(mode)}  count={(_countUnlimited ? "∞" : qty.ToString())}  x-trick={(xTrick ? "ON" : "OFF")}");
        AppendLog($"   signup url: {signupUrl}");

        ProgressText.Text = $"00 / {(_countUnlimited ? "∞" : $"{qty:00}")}";
        Progress.Value = 0;
        ProgressPanel.Visibility = Visibility.Visible;
        RunBtn.Visibility = Visibility.Collapsed;
        StopBtn.Visibility = Visibility.Visible;
        PreflightBtn.IsEnabled = false;
        _running = true;
        _cts = new CancellationTokenSource();

        var summary = await AccioRunLoop.RunAsync(
            _tool, mode, qty, Dispatcher,
            (done, total) =>
            {
                ProgressText.Text = total == int.MaxValue
                    ? $"{done:00} / ∞"
                    : $"{done:00} / {total:00}";
                Progress.Value = total == 0 ? 0 : done * 100.0 / Math.Max(1, total);
            },
            AppendLog,
            _cts.Token,
            forceNoProxy: ProxyToggle.IsChecked != true,
            maxPerRun:    int.TryParse(MaxPerRunBox.Text, out var mr) ? mr : 50,
            maxPerDay:    int.TryParse(MaxPerDayBox.Text, out var md) ? md : 9999,
            unlimited:    _countUnlimited,
            xTrick:       xTrick,
            createUrl:    signupUrl);

        AppendLog($"Run finished — created: {summary.Succeeded}  failed: {summary.Failed}  total: {summary.Total}");

        ProgressPanel.Visibility = Visibility.Collapsed;
        RunBtn.Visibility = Visibility.Visible;
        StopBtn.Visibility = Visibility.Collapsed;
        PreflightBtn.IsEnabled = true;
        _running = false;
        _cts?.Dispose();
        _cts = null;

        AccountStore.Refresh(_tool.Id);
        RefreshStats();
    }

    /// <summary>
    /// Safe demo mode: streams a believable Accio.exe-shaped log line by line
    /// and pushes synthetic account rows into the panel's account store so the
    /// user can see the full UI cycle (stat cards update, table grows, log
    /// scrolls) without any real network call to accio.com.
    /// </summary>
    private async Task RunTestSimulationAsync(int qty)
    {
        AppendLog("▶ TEST RUN — no real signup, no Accio.exe invoked, no network call");
        AppendLog("─────────────────────────────────────────────────────");

        ProgressText.Text = $"00 / {qty:00}";
        Progress.Value = 0;
        ProgressPanel.Visibility = Visibility.Visible;
        RunBtn.Visibility = Visibility.Collapsed;
        StopBtn.Visibility = Visibility.Visible;
        _running = true;
        _cts = new CancellationTokenSource();

        var ok = 0;
        var bad = 0;

        for (var i = 0; i < qty; i++)
        {
            if (_cts.IsCancellationRequested) break;

            var done = i + 1;
            AppendLog($"--- Test account {done}/{qty} ---");
            AppendLog($"[sim] Loading alice.json pool...");
            await Task.Delay(250, _cts.Token);
            // Neutral placeholder on purpose: this used to spell out the
            // operator's own Gmail address, which meant every customer running
            // the demo saw it in the log -- and it was baked into the shipped
            // exe.  A simulator must not carry anyone's real address.
            AppendLog("[sim] Using alice: alice1@example.com (slot 1/6)");
            await Task.Delay(250, _cts.Token);

            // generate a fake dotted variant
            var local = "ex.am.ple";
            var email = $"{local}+test{done}@gmail.com";
            AppendLog($"[sim] Account: {email}");
            await Task.Delay(300, _cts.Token);
            AppendLog($"[sim] Navigating to https://www.accio.com/login...");
            await Task.Delay(400, _cts.Token);
            AppendLog($"[sim] Email field filled (CloakBrowser, headful)");
            await Task.Delay(300, _cts.Token);
            AppendLog($"[sim] Submit clicked");
            await Task.Delay(500, _cts.Token);
            AppendLog($"[sim] Waiting for OTP (IMAP poll, 12s budget)...");
            await Task.Delay(900, _cts.Token);
            var otp = Random.Shared.Next(100000, 999999);
            AppendLog($"[sim] OTP received: {otp}");
            await Task.Delay(300, _cts.Token);
            AppendLog($"[sim] OTP submitted");
            await Task.Delay(400, _cts.Token);
            AppendLog($"[sim] Dashboard reached: OK");
            await Task.Delay(200, _cts.Token);
            AppendLog($"[sim] Cookies saved: 14 (locale=en, session=***)");

            // add a synthetic row to the live account table (so the user can
            // see the Dashboard tab populate without touching the real file)
            Dispatcher.Invoke(() =>
            {
                var fakeId = (int)(DateTime.UtcNow.Ticks % 100000);
                var rec = new AccountRecord
                {
                    Id          = AccountStore.NextId(_tool.Id),
                    EmailUsed   = email,
                    MailSource  = MailSource.DottedGmail,
                    SourceRef   = "test-simulation",
                    Status      = AccountStatus.Active,
                    CookieState = CookieState.Saved,
                    CreatedAt   = DateTime.Now,
                    Note        = "TEST MODE - synthetic row, not a real account"
                };
                AccountStore.Add(_tool.Id, rec);

                // also stash a fake cookie set so View/Export don't error out
                CookieVault.Save(_tool.Id, new CookieRecord
                {
                    AccountId = rec.Id,
                    Email     = email,
                    SavedAt   = DateTime.Now,
                    Cookies   = new List<BrowserCookie>
                    {
                        new() { Name = "session", Value = "fake_" + Guid.NewGuid().ToString("N")[..24], Domain = ".accio.com", Path = "/", HttpOnly = true, Secure = true, SameSite = "Lax" },
                        new() { Name = "uid",     Value = Guid.NewGuid().ToString("N")[..12],             Domain = ".accio.com", Path = "/", HttpOnly = false, Secure = true, SameSite = "Lax" },
                        new() { Name = "locale",  Value = "en",                                          Domain = ".accio.com", Path = "/" }
                    }
                });
            });

            AppendLog($"[sim] account OK (test)");
            ok++;

            ProgressText.Text = $"{done:00} / {qty:00}";
            Progress.Value = done * 100.0 / qty;
            RefreshStats();

            if (i + 1 < qty)
            {
                AppendLog("[sim] pausing 1s before next account...");
                try { await Task.Delay(1000, _cts.Token); } catch { break; }
            }
        }

        AppendLog($"─────────────────────────────────────────────────────");
        AppendLog($"TEST RUN finished — ok: {ok}  failed: {bad}  (none of these are real accounts)");

        ProgressPanel.Visibility = Visibility.Collapsed;
        RunBtn.Visibility = Visibility.Visible;
        StopBtn.Visibility = Visibility.Collapsed;
        _running = false;
        _cts?.Dispose();
        _cts = null;
    }

    private void OnStop(object sender, RoutedEventArgs e)
    {
        if (_cts is null) return;
        _cts.Cancel();
        AppendLog("⏹ STOP requested - finishing current step.");
    }

    /// <summary>
    /// Proxy override switch. When OFF the runner appends <c>--no-proxy</c> to
    /// Accio.exe regardless of the picked mail mode — useful when .env still
    /// has ACCIO_PROXY_API but the user wants to run direct.
    /// </summary>
    private void OnProxyToggleChanged(object sender, RoutedEventArgs e)
    {
        // XAML can fire this during InitializeComponent before sibling elements
        // (ProxyHint, CountUnlimitedBtn, …) are wired - bail if any of them is null.
        if (ProxyToggle is null) return;

        var on = ProxyToggle.IsChecked == true;
        UpdateProxyToggleText(on);

        // Restore-on-open path: the value is already what the user chose, so
        // writing it back is a no-op and the log line would be noise.
        if (_syncingProxy) return;

        ToolSettingsStore.SetProxyOn(_tool.Id, on);
        AppendLog(on
            ? "🔌 Proxy toggle ON  (configured proxies will be used)"
            : "🔌 Proxy toggle OFF  (forcing --no-proxy)");
    }

    /// <summary>Keeps the switch's own label and its hint line in step with the state.</summary>
    private void UpdateProxyToggleText(bool on)
    {
        if (ProxyToggle is not null) ProxyToggle.Content = on ? "ON" : "OFF";
        if (ProxyHint is not null)
            ProxyHint.Text = on
                ? "ON → use configured proxies (.env or _fallback_proxies.txt)."
                : "OFF → force --no-proxy regardless of mode.";
    }

    /// <summary>
    /// Reflects the persisted per-tool PROXY flag on the Create-run switch.
    /// Runs after the siblings are wired, for the XAML-load reason described on
    /// <see cref="OnProxyToggleChanged"/>.
    /// </summary>
    private void SyncProxyToggle()
    {
        if (ProxyToggle is null) return;
        _syncingProxy = true;
        try
        {
            var on = ToolSettingsStore.IsProxyOn(_tool.Id);
            ProxyToggle.IsChecked = on;
            UpdateProxyToggleText(on);
        }
        finally
        {
            _syncingProxy = false;
        }
    }

    /// <summary>
    /// Quick reachability probe: tries each fallback proxy + a direct HEAD to
    /// accio.com. If everything is dead, warns the user before they burn 30 s
    /// on a guaranteed-fail run. Returns true if any path reaches accio.com.
    /// </summary>
    private async void OnPreflight(object sender, RoutedEventArgs e)
    {
        PreflightBtn.IsEnabled = false;
        AppendLog("🔍 PRE-FLIGHT: probing proxy + direct reachability...");

        var alive = false;
        var proxyFile = Path.Combine(AccioToolDriver.AccioRoot, "_fallback_proxies.txt");
        if (File.Exists(proxyFile))
        {
            var lines = File.ReadAllLines(proxyFile)
                             .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("#"))
                             .Take(2)  // sample first 2 to keep it fast
                             .ToArray();

            foreach (var line in lines)
            {
                var proxy = line.Trim();
                var (ok, info) = await ProbeProxyAsync(proxy).ConfigureAwait(false);
                AppendLog($"  proxy  {proxy}  →  {info}");
                if (ok) alive = true;
            }
        }

        var (directOk, directInfo) = await ProbeDirectAsync().ConfigureAwait(false);
        AppendLog($"  direct → accio.com  →  {directInfo}");
        if (directOk) alive = true;

        if (alive)
            AppendLog("✓ PRE-FLIGHT OK — at least one path reaches accio.com.");
        else
            AppendLog("⚠ PRE-FLIGHT FAILED — every proxy is dead AND direct is unreachable. " +
                      "Add ACCIO_PROXY_API to .env (Settings tab → Proxy Setup).");

        PreflightBtn.IsEnabled = true;
    }

    private void AppendLog(string line)
    {
        Dispatcher.Invoke(() =>
        {
            var current = LogBox.Text;
            LogBox.Text = string.IsNullOrEmpty(current)
                ? line
                : current + Environment.NewLine + line;

            // Keep the tail, not the head: a long run would otherwise hit the cap
            // and stop growing exactly when the interesting part starts.
            var lines = LogBox.Text.Split('\n');
            if (lines.Length > 1500)
                LogBox.Text = string.Join("\n", lines.Skip(lines.Length - 1500));

            // Follow the output, otherwise the box stayed on line 1 and the
            // stream looked frozen while lines were arriving.
            LogScroll?.ScrollToEnd();
        });
    }

    // ------------------------------------------------------------ log resizing

    /// <summary>Drag the grip under the live log to resize it.</summary>
    private void OnLogResize(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        // Dragging UP gives a negative VerticalChange and must grow the box.
        ResizeLog(-e.VerticalChange);
    }

    private void OnLogBigger(object sender, RoutedEventArgs e) => ResizeLog(140);

    private void OnLogSmaller(object sender, RoutedEventArgs e) => ResizeLog(-140);

    private void ResizeLog(double delta)
    {
        if (LogHost is null) return;
        const double min = 90;
        const double max = 900;   // sanity cap - the tab itself scrolls
        var wanted = LogHost.Height + delta;
        if (double.IsNaN(wanted)) wanted = 240;
        LogHost.Height = Math.Min(max, Math.Max(min, wanted));
        LogScroll?.ScrollToEnd();
    }

    // -------------------------------------------------------------- login

    private void OnLoginClick(object sender, RoutedEventArgs e)
    {
        // The Login tab UI is wired directly via OnLoginVerifySelected; this
        // legacy handler stays as a no-op for older XAML bindings.
    }

    // -------------------------------------------------------------- dashboard rows

    private void OnViewCookie(object sender, RoutedEventArgs e)
    {
        var (row, email) = ExtractRow(sender);
        if (row is null) return;

        var record = CookieVault.Get(_tool.Id, row.Id);
        if (record is null || record.Cookies.Count == 0)
        {
            record = LoadAccioCookie(row);
            if (record is null || record.Cookies.Count == 0)
            {
                Log.Warn(_tool.Id, $"#{row.IdLabel} has no stored cookie.");
                return;
            }
        }

        _viewerName = $"{_tool.Id}-{row.IdLabel}-cookies.json";
        _viewerText = JsonSerializer.Serialize(record, JsonOptions);

        MessageBox.Show(_viewerText, $"Cookie — {email}  (#{row.IdLabel})",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnExportCookie(object sender, RoutedEventArgs e)
    {
        var (row, email) = ExtractRow(sender);
        if (row is null) return;

        var record = CookieVault.Get(_tool.Id, row.Id) ?? LoadAccioCookie(row);
        if (record is null || record.Cookies.Count == 0)
        {
            Log.Warn(_tool.Id, $"#{row.IdLabel} has no stored cookie.");
            return;
        }

        var dialog = new SaveFileDialog
        {
            FileName = $"{_tool.Id}-{row.IdLabel}-cookies.json",
            Filter   = "JSON file (*.json)|*.json|All files (*.*)|*.*",
            Title    = "Export cookie"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var text = JsonSerializer.Serialize(record, JsonOptions);
            File.WriteAllText(dialog.FileName, text, new UTF8Encoding(false));
            Log.Success(_tool.Id, $"#{row.IdLabel} cookie exported to {dialog.FileName}");
        }
        catch (Exception ex)
        {
            Log.Error(_tool.Id, "Export failed - " + ex.Message);
        }
    }

    /// <summary>
    /// Resolve the DataContext of a clicked View/Export button — handles both
    /// the dashboard's AccountRecord rows and the Login tab's LoginRow rows.
    /// </summary>
    private (AccountRecord? row, string email) ExtractRow(object sender)
    {
        if (sender is not FrameworkElement fe) return (null, "");

        // Login tab rows wrap an AccountRecord on the .Account property.
        if (fe.DataContext is LoginRow lr) return (lr.Account, lr.EmailUsed);

        // Dashboard rows are AccountRecord directly.
        if (fe.DataContext is AccountRecord ar) return (ar, ar.EmailUsed);

        return (null, "");
    }

    private CookieRecord? LoadAccioCookie(AccountRecord row)
    {
        // Resolved through the account store, so View / Export open the exact
        // file the account was registered with - the one accounts.json names, in
        // accio's own accounts\cookies folder. Rows written before that field
        // existed fall back to the folder name pattern.
        try
        {
            var path = AccountStore.ResolveCookieFile(row.CookieFile, row.EmailUsed);
            if (path is null || !File.Exists(path)) return null;

            var raw = File.ReadAllText(path);
            var cookies = JsonSerializer.Deserialize<List<BrowserCookie>>(raw, JsonOptions)
                       ?? new List<BrowserCookie>();

            return new CookieRecord
            {
                AccountId = row.Id,
                Email     = row.EmailUsed,
                SavedAt   = File.GetLastWriteTime(path),
                Cookies   = cookies
            };
        }
        catch
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented              = true,
        ReadCommentHandling        = JsonCommentHandling.Skip,
        AllowTrailingCommas        = true
    };

    // -------------------------------------------------------------- settings

    private void OnRefreshSettings(object sender, RoutedEventArgs e)
    {
        LoadSettingsFromAccio();
        LoadProxySetupFromEnv();
        RefreshMailSource();
        RefreshMailBanList();
        LoadCreateUrlFromSettings();
        RefreshStats();
        Log.Info(_tool.Id, "Refreshed mail settings (Global Settings) + proxy setup + Accio state.");
    }

    // ------------------------------------------------- MAIL ban list (this tool only)

    /// <summary>
    /// Rebuilds the Settings-tab MAIL list from Global Settings + this tool's ban
    /// list. Every configured pool Gmail is shown once, flagged ACTIVE or BANNED
    /// for THIS tool only. Same control (and the same shared
    /// <see cref="MailBanRow"/>) the Python tool panels use.
    /// </summary>
    private void RefreshMailBanList()
    {
        if (MailList is null) return;

        _mailRows.Clear();

        var pool = SettingsStore.Current.DottedGmail?.Accounts?
            .Where(a => SettingsStore.IsGmail(a.Email))
            .ToList() ?? new List<GmailEntry>();

        foreach (var g in pool)
            _mailRows.Add(new MailBanRow
            {
                Email    = g.Email,
                IsBanned = ToolSettingsStore.IsExcluded(_tool.Id, g.Email)
            });

        if (MailBanEmpty is not null)
            MailBanEmpty.Visibility = pool.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (MailBanAllBanned is not null)
            MailBanAllBanned.Visibility = pool.Count > 0 && _mailRows.All(r => r.IsBanned)
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    /// <summary>
    /// One-click ban / add-back for a single Gmail, scoped to THIS tool. Writes
    /// straight to tool-settings.json; Global Settings is never touched.
    ///
    /// <para>
    /// This is the switch that makes the Accio run skip an address: the panel
    /// allocates every address through <c>MailRouter</c>, which reads exactly
    /// this list and moves on to the next Gmail in the rotation.
    /// </para>
    /// </summary>
    private void OnMailToggle(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MailBanRow row }) return;
        if (string.IsNullOrWhiteSpace(row.Email)) return;

        var excluded = ToolSettingsStore.Excluded(_tool.Id).ToList();
        var willBan  = !ToolSettingsStore.IsExcluded(_tool.Id, row.Email);

        if (willBan)
        {
            if (!excluded.Any(x => string.Equals((x ?? "").Trim(), row.Email.Trim(), StringComparison.OrdinalIgnoreCase)))
                excluded.Add(row.Email.Trim());
        }
        else
        {
            excluded.RemoveAll(x => string.Equals((x ?? "").Trim(), row.Email.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        ToolSettingsStore.SetExcluded(_tool.Id, excluded);
        RefreshMailBanList();

        AppendLog(willBan
            ? $"🚫 BANNED {row.Email} for {_tool.Id} only — Global Settings untouched. Other tools still use it."
            : $"➕ Add back {row.Email} for {_tool.Id} (banned list updated).");
        Log.Write(willBan ? LogKind.Warn : LogKind.Info, _tool.Id,
            willBan ? $"banned {row.Email} for this tool" : $"un-banned {row.Email} for this tool");

        if (willBan && MailRouter.AllGmailsBanned(_tool.Id))
            AppendLog($"⚠ Every Gmail is now BANNED for {_tool.Id} — add one back in Settings → MAIL to keep creating accounts.");
    }

    // ----------------------------------------------------- proxy setup (.env writer)

    private const string EnvPath = "";   // populated below via AccioRoot

    private string ActualEnvPath() => Path.Combine(AccioToolDriver.AccioRoot, ".env");

    private void LoadProxySetupFromEnv()
    {
        try
        {
            var path = ActualEnvPath();
            if (!File.Exists(path))
            {
                ProxyApiBox.Text = "";
                ProxySourcesBox.Text = "";
                ProxySetupStatus.Text = ".env not found at " + path;
                return;
            }

            var lines = File.ReadAllLines(path);
            ProxyApiBox.Text     = ExtractValue(lines, "ACCIO_PROXY_API")     ?? "";
            ProxySourcesBox.Text = ExtractValue(lines, "ACCIO_PROXY_SOURCES") ?? "";

            ProxySetupStatus.Text =
                $"loaded from .env  ·  ACCIO_PROXY_API='{ProxyApiBox.Text}'  ·  " +
                $"ACCIO_PROXY_SOURCES='{ProxySourcesBox.Text}'";
        }
        catch (Exception ex)
        {
            ProxySetupStatus.Text = "could not read .env: " + ex.Message;
        }
    }

    /// <summary>Reads a KEY=value pair from .env, ignoring comments and quoted values.</summary>
    private static string? ExtractValue(string[] lines, string key)
    {
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith("#") || string.IsNullOrWhiteSpace(line)) continue;
            if (!line.StartsWith(key, StringComparison.Ordinal)) continue;
            var eq = line.IndexOf('=');
            if (eq < 0) continue;
            var val = line[(eq + 1)..].Trim();
            // strip surrounding quotes if any
            if (val.Length >= 2 &&
                ((val.StartsWith('"') && val.EndsWith('"')) ||
                 (val.StartsWith('\'') && val.EndsWith('\''))))
                val = val[1..^1];
            return val;
        }
        return null;
    }

    private void OnSaveProxyApi(object sender, RoutedEventArgs e)
    {
        UpdateEnvKey("ACCIO_PROXY_API", ProxyApiBox.Text.Trim());
        ProxySetupStatus.Text = $"✓ ACCIO_PROXY_API saved → '{ProxyApiBox.Text.Trim()}'";
        Log.Info(_tool.Id, "ACCIO_PROXY_API updated in .env");
    }

    private void OnSaveProxySources(object sender, RoutedEventArgs e)
    {
        UpdateEnvKey("ACCIO_PROXY_SOURCES", ProxySourcesBox.Text.Trim());
        ProxySetupStatus.Text = $"✓ ACCIO_PROXY_SOURCES saved → '{ProxySourcesBox.Text.Trim()}'";
        Log.Info(_tool.Id, "ACCIO_PROXY_SOURCES updated in .env");
    }

    /// <summary>Replaces KEY=... in .env or appends a new line. Creates the file if missing.</summary>
    private void UpdateEnvKey(string key, string value)
    {
        try
        {
            var path = ActualEnvPath();
            var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
            var found = false;

            for (var i = 0; i < lines.Count; i++)
            {
                var t = lines[i].Trim();
                if (t.StartsWith("#") || string.IsNullOrWhiteSpace(t)) continue;
                if (!t.StartsWith(key, StringComparison.Ordinal)) continue;

                var eq = t.IndexOf('=');
                if (eq < 0) continue;

                if (string.IsNullOrEmpty(value))
                    lines[i] = $"#{key}=  (cleared {DateTime.Now:yyyy-MM-dd HH:mm})";
                else
                    lines[i] = $"{key}={value}";
                found = true;
                break;
            }

            if (!found)
            {
                if (!string.IsNullOrEmpty(value))
                    lines.Add($"{key}={value}");
            }

            File.WriteAllLines(path, lines, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            ProxySetupStatus.Text = $"could not save .env: {ex.Message}";
        }
    }

    // ----------------------------------------------------- connection test (diagnostic only)

    private readonly ObservableCollection<ConnTestRow> _connRows = new();
    private static readonly HttpClient ProbeClient = new()
    {
        // Tight timeout so a single dead proxy doesn't hold the test for minutes.
        Timeout = TimeSpan.FromSeconds(6)
    };

    /// <summary>
    /// Passive diagnostic. Does <b>not</b> invoke <c>Accio.exe</c>, does not
    /// trigger any signup form, and does not run browser automation. Only does
    /// file-existence + lightweight HEAD requests so the user can see exactly
    /// where the chain breaks before they spend real time running Accio.
    /// </summary>
    private async void OnConnectionTest(object sender, RoutedEventArgs e)
    {
        ConnTestBtn.IsEnabled = false;
        ConnTestPanel.Visibility = Visibility.Visible;
        _connRows.Clear();

        void AddRow(string status, Brush color, string message)
        {
            _connRows.Add(new ConnTestRow
            {
                Status = status,
                StatusColor = color,
                Message = message
            });
        }

        // ---- 0) Where the panel thinks accio's data lives ---------------
        // Printed first on purpose: every "deleted nothing" / "no cookies"
        // report so far came down to the panel resolving a DIFFERENT accounts
        // folder than the one the tool writes, so the operator can see it.
        var accountsJson = AccountStore.AccioAccountsJsonPath();
        if (File.Exists(accountsJson))
        {
            var rows = 0;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(accountsJson));
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    rows = doc.RootElement.GetArrayLength();
            }
            catch { /* count stays 0 */ }

            AddRow("PASS", PassBrush,
                $"Accounts file: {accountsJson}  ({rows} account{(rows == 1 ? "" : "s")}, " +
                $"{AppPaths.AppFolderRoot})");
        }
        else
        {
            AddRow("FAIL", FailBrush,
                $"accounts.json NOT found at {accountsJson} — the panel is reading its own " +
                "private store instead. Payload root resolved to " + AppPaths.AppFolderRoot);
        }

        // ---- 1) Accio.exe reachable? ----------------------------------
        var exePath = AccioToolDriver.AccioExePath;
        if (File.Exists(exePath))
            AddRow("PASS", PassBrush, $"Accio.exe found at {exePath}");
        else
            AddRow("FAIL", FailBrush, $"Accio.exe NOT found — set AccioRoot in AccioToolDriver.cs");

        // ---- 2) Mail source = Global Settings ---------------------------
        // alice.json is deliberately NOT part of this any more: it is the tool's
        // own mailbox and it used to override whatever Global Settings said, so
        // the check now reports the source every run actually uses.
        var s = SettingsStore.Current;
        var poolCount = s.DottedGmail?.Accounts?
            .Count(a => SettingsStore.IsGmail(a.Email) && !string.IsNullOrWhiteSpace(a.AppPassword)) ?? 0;
        var domainReady = !string.IsNullOrWhiteSpace(s.CustomDomain.Domain) &&
                          !string.IsNullOrWhiteSpace(s.CustomDomain.ForwardTarget) &&
                          !string.IsNullOrWhiteSpace(s.CustomDomain.TargetAppPassword);
        var customReady = !string.IsNullOrWhiteSpace(s.CustomMail.Email) &&
                          !string.IsNullOrWhiteSpace(s.CustomMail.AppPassword);

        AddRow(poolCount > 0 ? "PASS" : "WARN", poolCount > 0 ? PassBrush : WarnBrush,
            $"Global Settings mail — {poolCount} dotted-Gmail pool entr{(poolCount == 1 ? "y" : "ies")}, " +
            $"custom domain {(domainReady ? "ready" : "not configured")}, " +
            $"custom mail {(customReady ? "ready" : "not configured")}");

        if (poolCount == 0 && !domainReady && !customReady && !SettingsStore.TempMailUsable)
            AddRow("FAIL", FailBrush,
                "No usable mail source. Add one in Global Settings (Method 1-4) before running Create.");

        var env = Path.Combine(AccioToolDriver.AccioRoot, ".env");
        if (File.Exists(env))
            AddRow("PASS", PassBrush, ".env found");
        else
            AddRow("WARN", WarnBrush, ".env missing — OTP fetch and proxy provider URLs come from there");

        // ---- 3) Fallback proxy list alive? -----------------------------
        var proxyFile = Path.Combine(AccioToolDriver.AccioRoot, "_fallback_proxies.txt");
        if (File.Exists(proxyFile))
        {
            var lines = File.ReadAllLines(proxyFile)
                             .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("#"))
                             .ToArray();
            AddRow("INFO", InfoBrush,
                $"{lines.Length} fallback proxies in _fallback_proxies.txt — probing each...");

            if (lines.Length == 0)
            {
                AddRow("FAIL", FailBrush,
                    "Proxy file empty — Accio will go DIRECT, which fails (accio.com unreachable from your IP)");
            }
            else
            {
                foreach (var line in lines)
                {
                    var proxy = line.Trim();
                    var (ok, info) = await ProbeProxyAsync(proxy).ConfigureAwait(false);
                    Dispatcher.Invoke(() => AddRow(
                        ok ? "PASS" : "FAIL",
                        ok ? PassBrush : FailBrush,
                        $"{proxy}  →  {info}"));
                }
            }
        }
        else
        {
            AddRow("WARN", WarnBrush,
                "_fallback_proxies.txt missing — Accio will need ACCIO_PROXY_API / ACCIO_PROXY_SOURCES in .env");
        }

        // ---- 4) Direct reachability to accio.com ------------------------
        var (directOk, directInfo) = await ProbeDirectAsync().ConfigureAwait(false);
        Dispatcher.Invoke(() => AddRow(
            directOk ? "PASS" : "FAIL",
            directOk ? PassBrush : FailBrush,
            $"Direct → accio.com  →  {directInfo}"));

        // ---- 5) Verdict ------------------------------------------------
        string verdict;
        Brush verdictColor;
        if (_connRows.Any(r => r.Status == "FAIL"))
        {
            verdict =
                "⚠ Browser will open, fail to navigate to accio.com, and close.\n" +
                "Fix the FAIL items above (mainly: working proxy) before running for real.";
            verdictColor = FailBrush;
        }
        else
        {
            verdict = "✓ All checks passed — a real run should reach accio.com.";
            verdictColor = PassBrush;
        }

        Dispatcher.Invoke(() =>
        {
            ConnTestVerdict.Text = verdict;
            ConnTestVerdict.Foreground = verdictColor;
            ConnTestBtn.IsEnabled = true;
        });
    }

    /// <summary>HEAD probe through one proxy. Returns (alive, info-string).</summary>
    private static async Task<(bool, string)> ProbeProxyAsync(string proxy)
    {
        try
        {
            var handler = new HttpClientHandler
            {
                Proxy = new WebProxy(proxy),
                UseProxy = true,
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };

            var sw = Stopwatch.StartNew();
            using var req = new HttpRequestMessage(HttpMethod.Head,
                "https://www.accio.com/login");
            using var resp = await http.SendAsync(req).ConfigureAwait(false);
            sw.Stop();

            return (true, $"HTTP {resp.StatusCode} in {sw.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            return (false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>HEAD probe with no proxy — checks if direct reach to accio.com works.</summary>
    private static async Task<(bool, string)> ProbeDirectAsync()
    {
        try
        {
            var sw = Stopwatch.StartNew();
            using var req = new HttpRequestMessage(HttpMethod.Head,
                "https://www.accio.com/login");
            using var resp = await ProbeClient.SendAsync(req).ConfigureAwait(false);
            sw.Stop();
            return (true, $"HTTP {resp.StatusCode} in {sw.ElapsedMilliseconds} ms (DIRECT)");
        }
        catch (Exception ex)
        {
            return (false, $"{ex.GetType().Name}: {ex.Message} (DIRECT)");
        }
    }

    private static readonly Brush PassBrush = new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99));
    private static readonly Brush FailBrush = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
    private static readonly Brush WarnBrush = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24));
    private static readonly Brush InfoBrush = new SolidColorBrush(Color.FromRgb(0x93, 0xA3, 0xC2));

    public class ConnTestRow
    {
        public string Status { get; set; } = "";
        public Brush StatusColor { get; set; } = Brushes.Gray;
        public string Message { get; set; } = "";
    }

    private void LoadSettingsFromAccio()
    {
        _pool.Clear();
        _domains.Clear();
        // proxy setup fields populated separately by LoadProxySetupFromEnv()

        // Mail is read from GLOBAL SETTINGS, never from Accio's own alice.json.
        //
        // alice.json is the tool carrying its own mailbox: when it has entries
        // create.py overwrites the credentials the panel handed over, so the
        // address the user configured in Global Settings is silently replaced by
        // whatever the tool happens to have baked in. Every other panel reads
        // mail from Global Settings, and so does this one now.
        var s = SettingsStore.Current;

        try
        {
            var pool = s.DottedGmail?.Accounts?
                .Where(a => !string.IsNullOrWhiteSpace(a.Email))
                .ToList() ?? new List<GmailEntry>();

            foreach (var g in pool)
            {
                var usable = SettingsStore.IsGmail(g.Email) && !string.IsNullOrWhiteSpace(g.AppPassword);
                _pool.Add(new PoolEntry
                {
                    Email       = g.Email,
                    UsedLabel   = usable ? "app password: set" : "app password: MISSING",
                    StatusLabel = usable ? "ok" : "needs setup",
                    StatusColor = usable
                        ? new SolidColorBrush(Color.FromRgb(0x9C, 0xF0, 0xD0))
                        : new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71))
                });
            }

            var d = s.CustomDomain;
            if (!string.IsNullOrWhiteSpace(d.Domain))
            {
                var ready = !string.IsNullOrWhiteSpace(d.ForwardTarget) &&
                            !string.IsNullOrWhiteSpace(d.TargetAppPassword);
                _domains.Add(new DomainEntry
                {
                    Domain      = d.Domain,
                    UsageLabel  = string.IsNullOrWhiteSpace(d.ForwardTarget)
                        ? "no forward target"
                        : $"→ {d.ForwardTarget}",
                    ActiveLabel = ready ? "★ ready" : "incomplete",
                    ActiveColor = ready
                        ? new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24))
                        : new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71))
                });
            }

            InboxLine.Text =
                $"source      : Global Settings → Method 3 (Custom Domain)\n" +
                $"domain      : {(string.IsNullOrWhiteSpace(d.Domain) ? "(none configured)" : d.Domain)}\n" +
                $"forward to  : {(string.IsNullOrWhiteSpace(d.ForwardTarget) ? "(none configured)" : d.ForwardTarget)}\n" +
                $"has_password: {(string.IsNullOrWhiteSpace(d.TargetAppPassword) ? "no" : "yes")}\n" +
                $"note        : Accio's own alice.json pool is ignored — mail always comes from Global Settings.";
        }
        catch (Exception ex)
        {
            InboxLine.Text = "Failed to read Global Settings: " + ex.Message;
        }
    }

    public class PoolEntry
    {
        public string Email { get; set; } = "";
        public string UsedLabel { get; set; } = "";
        public string StatusLabel { get; set; } = "";
        public Brush StatusColor { get; set; } = Brushes.Gray;
    }

    public class DomainEntry
    {
        public string Domain { get; set; } = "";
        public string UsageLabel { get; set; } = "";
        public string ActiveLabel { get; set; } = "";
        public Brush ActiveColor { get; set; } = Brushes.Gray;
    }

    // LoginRow moved to Views/LoginRow.cs (shared with PythonToolPanelView).

    // -------------------------------------------------------------- nav

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (_running)
        {
            var ans = MessageBox.Show(
                "A run is still in progress. Cancel and go back?",
                _tool.Name, MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (ans != MessageBoxResult.OK) return;
            _cts?.Cancel();
        }
        Nav.GoDashboard();
    }

    // -------------------------------------------------------------- log

    private void OnLogAppended(LogEntry entry)
    {
        if (!string.IsNullOrEmpty(entry.ToolId) &&
            !string.Equals(entry.ToolId, _tool.Id, StringComparison.OrdinalIgnoreCase)) return;

        Dispatcher.Invoke(() =>
        {
            _log.Insert(0, entry);
            while (_log.Count > 200) _log.RemoveAt(_log.Count - 1);
        });
    }

    private void OnClearLog(object sender, RoutedEventArgs e) => _log.Clear();

    /// <summary>Wipe the Create tab's LIVE LOG box (the streamed stdout).</summary>
    private void OnClearLiveLog(object sender, RoutedEventArgs e)
    {
        if (LogBox is null) return;
        LogBox.Text = "(live log cleared)";
    }

    /// <summary>Copy the LIVE LOG box - falls back to the activity log when empty.</summary>
    private void OnCopyLiveLog(object sender, RoutedEventArgs e)
    {
        try
        {
            var live = LogBox?.Text ?? "";
            var text = string.IsNullOrWhiteSpace(live) ? Log.Text(_tool.Id) : live;
            if (string.IsNullOrWhiteSpace(text))
            {
                Log.Warn(_tool.Id, "Nothing to copy - the live log is empty.");
                return;
            }
            Clipboard.SetText(text);
            Log.Info(_tool.Id, $"Live log copied to clipboard ({text.Length} chars).");
        }
        catch (Exception ex)
        {
            Log.Error(_tool.Id, "Could not copy the live log - " + ex.Message);
        }
    }



    private void OnCopyLog(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(Log.Text(_tool.Id));
            Log.Info(_tool.Id, "Activity log copied to clipboard.");
        }
        catch (Exception ex)
        {
            Log.Error(_tool.Id, "Could not copy log - " + ex.Message);
        }
    }

    // -------------------------------------------------------------- helpers

    private void OnDigitsOnly(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !e.Text.All(char.IsDigit);
    }
}

/// <summary>
/// Accio-specific create runner. Wraps <see cref="AccioToolDriver"/> with the
/// loop, progress reporting, and live-log streaming that the Accio panel expects.
/// </summary>
public static class AccioRunLoop
{
    public static async Task<RunSummary> RunAsync(
        ToolInfo tool,
        MailSource mode,
        int quantity,
        Dispatcher ui,
        Action<int, int>? onProgress,
        Action<string> appendLog,
        CancellationToken ct,
        bool forceNoProxy = false,
        int maxPerRun = 50,
        int maxPerDay = 9999,
        bool unlimited = false,
        bool xTrick = true,
        string? createUrl = null)
    {
        var ok = 0;
        var bad = 0;

        // Use int.MaxValue as the sentinel for unlimited so the existing
        // progress-bar / progress-text code keeps working with no special cases.
        var hardCap = unlimited ? int.MaxValue : Math.Min(quantity, maxPerRun);
        appendLog(unlimited
            ? $"∞ mode - hard cap = {maxPerRun} / run, {maxPerDay} / day."
            : $"batch cap = {hardCap} (count={quantity}, maxRun={maxPerRun}).");

        for (var i = 0; i < hardCap; i++)
        {
            ct.ThrowIfCancellationRequested();

            // Panel-side daily cap — skipped entirely in ∞ mode, which exists to
            // have no cap at all, and skipped again when the box sits at its
            // no-cap value (>= 9999). With the daily limit gone from the tool
            // itself (license.py), this was the last thing capping a batch.
            if (!unlimited && ok + bad >= maxPerDay && maxPerDay < 9999)
            {
                appendLog($"Daily cap reached ({maxPerDay}). Stopping.");
                break;
            }

            var done = i + 1;
            appendLog($"--- Account {done}/{hardCap} ---");

            // The address comes from the panel's MailRouter, i.e. straight from
            // Global Settings - the single source of truth for every tool.
            //
            // This used to pass the literal string "worker-N". Nothing ever
            // allocated a real mailbox for an Accio run, so create.py was handed
            // --email-override worker-0: an address with no "@" in it, no pool
            // inbox behind it and no mailbox to read an OTP from. Combined with
            // the flag-ordering bug in AccioToolDriver it also made argparse
            // abort before the browser ever opened.
            var routed = await MailRouter.NextAsync(tool.Id, mode, done).ConfigureAwait(false);
            if (routed is null)
            {
                bad++;
                appendLog($"#{done:00} skipped - no address available for " +
                          $"{CreateAccountRunner.SourceLabel(mode)}. Check Global Settings " +
                          "→ Method 2 / 3 / 4.");
                var reachedNoAddr = unlimited ? hardCap : quantity;
                ui.Invoke(() => onProgress?.Invoke(done, reachedNoAddr));
                continue;
            }

            appendLog($"#{done:00}  {routed.Address}" +
                      (string.IsNullOrWhiteSpace(routed.SourceRef) ? "" : $"   [{routed.SourceRef}]"));

            var result = await AccioToolDriver
                .CreateDirectAsync(routed.Address, mode, appendLog, ct, forceNoProxy, xTrick, createUrl)
                .ConfigureAwait(false);

            if (result.Ok) ok++;
            else          bad++;

            AccountStore.Refresh(tool.Id);

            // For unlimited mode, report progress against the cap; for fixed, use quantity
            var total = unlimited ? hardCap : quantity;
            ui.Invoke(() => onProgress?.Invoke(done, total));

            if (i + 1 < hardCap)
            {
                appendLog($"Pausing 3s before next account...");
                try { await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }

        return new RunSummary(hardCap, ok, bad);
    }
}