using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
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
using AISuitePanel.Automation;
using AISuitePanel.Models;
using Microsoft.Playwright;
using Microsoft.Win32;

namespace AISuitePanel.Views;

/// <summary>
/// Generic 4-tab panel for tools whose real automation is a separate Python
/// + Playwright script.  Used by Z AI and Devin AI; the tool id parameter
/// decides which <c>ToolSettings</c> the panel reads / writes.
///
/// The view deliberately mirrors <see cref="AccioPanelView"/>'s shape (same
/// stat cards, same tab order, same Live Log box) so the user feels equally
/// at home on every tool page.
/// </summary>
public partial class PythonToolPanelView : UserControl
{
    private readonly ToolInfo _tool;
    private readonly string _toolId;
    private readonly ObservableCollection<AccountRecord> _accounts;
    private readonly ObservableCollection<LoginRow> _loginRows = new();
    private readonly ObservableCollection<MailBanRow> _mailRows = new();
    private readonly PythonToolDriver _driver;

    private CancellationTokenSource? _cts;
    private bool _running;
    // Guards the two headless switches (Create tab + Extra tab) against firing
    // each other's handlers while they are being kept in step.
    private bool _syncingHeadless;
    // Same guard for the per-tool PROXY switch: SyncProxyToggle() writes the
    // saved value onto the toggle from code, which fires OnProxyToggleChanged
    // again - without this, every open would also re-log the switch line.
    private bool _syncingProxy;
    // The Extra tab runs at most one script at a time; these are what STOP uses.
    private CancellationTokenSource? _extraCts;
    private System.Diagnostics.Process? _extraProc;

    public PythonToolPanelView(ToolInfo tool)
    {
        if (tool is null) throw new ArgumentNullException(nameof(tool));
        _tool = tool;
        _toolId = tool.Id;
        InitializeComponent();

        _driver = new PythonToolDriver(_toolId);
        _accounts = AccountStore.For(_toolId);

        TitleText.Text = $"DASHBOARD > {tool.PanelTitle}";
        SubTitle.Text = $"{tool.Type} - {tool.Tagline}";
        DashboardHeader.Text = $"{tool.Name.ToUpperInvariant()} ACCOUNT TABLE";
        SettingsHeader.Text = $"{tool.Name.ToUpperInvariant()} - TOOL PATHS & ARG TEMPLATES";
        TabCreateBtn.Content = $"▶ Create ({tool.Name})";
        TabLoginBtn.Content  = $"🔓 Login ({tool.Name})";

        AcctList.ItemsSource  = _accounts;
        LoginList.ItemsSource  = _loginRows;
        if (MailList is not null) MailList.ItemsSource = _mailRows;

        _accounts.CollectionChanged += (_, _) =>
        {
            RefreshStats();
            ReloadLoginRows();
        };

        Unloaded += OnUnloaded;
        ShowTab("dashboard");
        LoadSettings();
        RefreshMailSource();
        RefreshStats();
        ReloadLoginRows();
        RefreshMailBanList();
        ApplyExtraTabToolShape();

        // Set proxy toggle default AFTER siblings are wired - same XAML-load
        // gotcha that bit AccioPanelView first time around.
        //
        // It used to be forced ON here on every single open, so an operator who
        // had switched the proxy OFF found it back on next time and had to flip
        // it again before every run. The saved per-tool value is reflected
        // instead, so ON stays ON and OFF stays OFF.
        SyncProxyToggle();

        // Same for the per-tool HEADLESS switch: reflect whatever this tool's
        // saved settings say (default OFF = visible browser).
        SyncHeadlessToggle();
    }

    // ----------------------------------------------------------------- tab switch

    private void ShowTab(string which)
    {
        TabDashboard.Visibility = which == "dashboard" ? Visibility.Visible : Visibility.Collapsed;
        TabCreate.Visibility    = which == "create"    ? Visibility.Visible : Visibility.Collapsed;
        TabLogin.Visibility     = which == "login"     ? Visibility.Visible : Visibility.Collapsed;
        TabExtra.Visibility     = which == "extra"     ? Visibility.Visible : Visibility.Collapsed;
        TabSettings.Visibility  = which == "settings"  ? Visibility.Visible : Visibility.Collapsed;

        TabDashBtn.Opacity   = which == "dashboard" ? 1.0 : 0.55;
        TabCreateBtn.Opacity = which == "create"    ? 1.0 : 0.55;
        TabLoginBtn.Opacity  = which == "login"     ? 1.0 : 0.55;
        TabExtraBtn.Opacity  = which == "extra"     ? 1.0 : 0.55;
        TabSetBtn.Opacity    = which == "settings"  ? 1.0 : 0.55;
    }

    private void OnTabDashboard(object sender, RoutedEventArgs e) => ShowTab("dashboard");
    private void OnTabCreate   (object sender, RoutedEventArgs e) { RefreshMailSource(); ShowTab("create"); }
    private void OnTabLogin    (object sender, RoutedEventArgs e) => ShowTab("login");
    private void OnTabSettings (object sender, RoutedEventArgs e) { RefreshMailBanList(); ShowTab("settings"); }
    private void OnTabExtra    (object sender, RoutedEventArgs e) { ApplyExtraTabToolShape(); ShowTab("extra"); }

    private void OnBack(object sender, RoutedEventArgs e) => Nav.GoDashboard();

    // ----------------------------------------------------------------- stats

    private void RefreshStats()
    {
        var accounts = _accounts.ToList();
        var today = DateTime.Today.ToString("yyyy-MM-dd");

        StatTotal.Text       = accounts.Count.ToString();
        StatActiveToday.Text = accounts.Count(a =>
            (a.CreatedAt.ToString("yyyy-MM-dd")) == today).ToString();

        var last = accounts.LastOrDefault();
        StatLastRun.Text = last is null
            ? "—"
            : last.CreatedAt.ToString("HH:mm:ss  •  ") + (last.CreatedAt.ToString("yyyy-MM-dd"));

        var s = ToolSettingsStore.For(_toolId);
        var paths = new List<string>();
        if (!string.IsNullOrWhiteSpace(s.CreateScriptPath) && File.Exists(s.CreateScriptPath)) paths.Add("create");
        if (!string.IsNullOrWhiteSpace(s.LoginScriptPath)  && File.Exists(s.LoginScriptPath))  paths.Add("login");
        StatScripts.Text = paths.Count == 0
            ? "⚠ no scripts"
            : (paths.Count == 2 ? "✓ create + login" : $"✓ {paths[0]} only");

        AcctEmpty.Visibility = accounts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ----------------------------------------------------------------- dashboard rows

    /// <summary>
    /// Where to look for cookies.txt. The script writes to its own folder
    /// (e.g. <c>python/cookies.txt</c>), but existing kits keep data at the
    /// project root (e.g. <c>Z Code V2/cookies.txt</c>) — try both.
    /// </summary>
    private string? ResolveCookiesTxt()
    {
        var s = ToolSettingsStore.For(_toolId);
        var candidates = new List<string>();

        // ONE place now: beside the exe, in accounts\<tool>\, which is where the
        // scripts are told to write (PANEL_TOOL_STATE_DIR). Checked FIRST, or the
        // cookies stop resolving now that the scripts no longer write them into
        // the application folder. The older locations below stay as a fallback
        // for an install that has not been migrated yet.
        try
        {
            candidates.Add(Path.Combine(AppPaths.ToolAccountsDir(_toolId), "cookies.txt"));
        }
        catch
        {
            // no base folder to resolve against
        }

        if (!string.IsNullOrWhiteSpace(s.CreateScriptPath))
            candidates.Add(Path.Combine(Path.GetDirectoryName(s.CreateScriptPath)!, "cookies.txt"));
        if (!string.IsNullOrWhiteSpace(s.ToolWorkingDirectory))
            candidates.Add(Path.Combine(s.ToolWorkingDirectory!, "cookies.txt"));
        return candidates.FirstOrDefault(File.Exists);
    }

    private void OnViewCookie(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: AccountRecord row }) return;

        var path = ResolveCookiesTxt();
        if (path is null)
        {
            MessageBox.Show("No cookies.txt in the script folder or working folder.",
                "No cookies", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var email = row.EmailUsed;
        var match = File.ReadAllLines(path)
            .FirstOrDefault(l => l.StartsWith(email, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            MessageBox.Show($"No entry for {email} in {Path.GetFileName(path)}.", "No cookies",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        MessageBox.Show(match, $"cookies.txt — {email}",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnExportCookie(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: AccountRecord row }) return;
        var dlg = new SaveFileDialog
        {
            FileName = $"{_toolId}-{Sanitise(row.EmailUsed)}-cookies.txt",
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        var path = ResolveCookiesTxt();
        if (path is null)
        {
            MessageBox.Show("No cookies.txt found.", "Nothing to export",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var match = File.ReadAllLines(path)
            .FirstOrDefault(l => l.StartsWith(row.EmailUsed, StringComparison.OrdinalIgnoreCase));
        if (match is null) { MessageBox.Show("No entry for this email.", "Nothing to export",
            MessageBoxButton.OK, MessageBoxImage.Warning); return; }

        File.WriteAllText(dlg.FileName, match, new UTF8Encoding(false));
        Log.Success(_toolId, $"Exported cookies for {row.EmailUsed} → {dlg.FileName}");
    }

    // ----------------------------------------------------------------- login rows

    private void ReloadLoginRows()
    {
        _loginRows.Clear();
        foreach (var acc in _accounts.Where(a => a.CookieState == CookieState.Saved))
        {
            _loginRows.Add(new LoginRow
            {
                Account          = acc,
                Id               = acc.Id,
                IdLabel          = acc.IdLabel,
                EmailUsed        = acc.EmailUsed,
                CookieLabel      = acc.CookieLabel,
                Selected         = false
            });
        }
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
    /// Re-reads accounts.txt + cookies.txt from the script's working folder so
    /// the Login tab reflects whatever the Python script just wrote. Cheap
    /// (single File.ReadAllLines each), so we don't bother with FileSystemWatcher.
    /// </summary>
    private void OnRefreshLogin(object sender, RoutedEventArgs e)
    {
        AccountStore.Refresh(_toolId);
        ReloadLoginRows();
        RefreshStats();
        AppendLog($"🔄 Refreshed from disk  ({_loginRows.Count} accounts with cookies).");
    }

    /// <summary>
    /// Probe every ticked cookie set against the tool's main URL with a HEAD
    /// request. 200 OK = session still valid; 302 → /login = expired. Marks
    /// each row with ✓ / ✗ in the Verify column, matching Accio's UX.
    /// </summary>
    private async void OnLoginVerifySelected(object sender, RoutedEventArgs e)
    {
        var selected = _loginRows.Where(r => r.Selected).ToList();
        if (selected.Count == 0)
        {
            Log.Warn(_toolId, "No accounts selected. Tick the boxes first.");
            return;
        }

        if (LoginVerifyBtn is not null) LoginVerifyBtn.IsEnabled = false;

        // MiniMax (tool4): a plain HEAD/GET probe cannot tell a MiniMax session
        // apart - www.minimax.io answers 200 for guests too, so the VERIFY
        // column used to stay empty.  Run the tool's own login script instead:
        // it replays each cookie set in a real browser and reports per account.
        if (string.Equals(_toolId, "tool4", StringComparison.OrdinalIgnoreCase))
        {
            await VerifyWithLoginScriptAsync(selected);
            if (LoginVerifyBtn is not null) LoginVerifyBtn.IsEnabled = true;
            return;
        }

        AppendLog($"🔑 VERIFY LOGIN — testing {selected.Count} cookie set(s) against {VerifyUrl()}...");

        // UI updates need the dispatcher thread - DON'T use ConfigureAwait(false)
        // on async void handlers that touch XAML properties after the await.
        foreach (var row in selected)
        {
            var (ok, info) = await ProbeCookieAsync(row).ConfigureAwait(true);
            row.LastVerify = ok ? "✓ valid" : "✗ expired";
            row.LastVerifyColor = ok
                ? new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99))
                : new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
            AppendLog($"  {row.EmailUsed}  →  {(ok ? "✓" : "✗")} {info}");
        }

        AppendLog("🔑 VERIFY LOGIN done.  Rows above show ✓ valid / ✗ expired.");
        if (LoginVerifyBtn is not null) LoginVerifyBtn.IsEnabled = true;
    }

    // ------------------------------------------------------------------ EXTRA tab
    // Everything that is not Create/Login: Auto, Check, Force all and the three
    // claim actions.  Each button just runs the tool's own script in the tool's
    // own folder, so behaviour matches the standalone CLI exactly.

    private void AppendExtra(string line)
    {
        if (ExtraLog is null) return;
        ExtraLog.Inlines.Add(new System.Windows.Documents.Run(line + System.Environment.NewLine));
        while (ExtraLog.Inlines.Count > 500 && ExtraLog.Inlines.FirstInline is not null)
            ExtraLog.Inlines.Remove(ExtraLog.Inlines.FirstInline);
    }

    private string LoginScriptPath() =>
        AISuitePanel.ToolSettingsStore.For(_toolId)?.LoginScriptPath ?? "";

    private string ClaimScriptPath()
    {
        var login = LoginScriptPath();
        var dir = string.IsNullOrWhiteSpace(login) ? "" : System.IO.Path.GetDirectoryName(login) ?? "";
        return System.IO.Path.Combine(dir, "claim_advanced.py");
    }

    /// <summary>
    /// The script behind the Extra tab's ACCOUNT buttons (AUTO / CHECK / FORCE
    /// ALL).
    ///
    /// <para>
    /// These run the tool's BATCH login flow, which lives in
    /// <c>login_advanced.py</c> - NOT in the single-account script the row LOGIN
    /// buttons use (<c>minimax_login.py</c>, a cookie-verify adapter).
    /// </para>
    ///
    /// <para>
    /// Pointing them at the adapter was why every MiniMax Extra button looked
    /// dead: it does not know <c>--auto-unused</c>, <c>--check</c> or
    /// <c>--auto</c> at all (it only reads --open/--stay/--url/--limit), so it
    /// silently ignored the mode flag and ran its own VERIFY sweep instead -
    /// which then exited 1 straight away whenever the .env SIGNUP_EMAIL had no
    /// matching row in cookies.txt. Nothing happened, and nothing said why.
    /// </para>
    ///
    /// <para>
    /// login_advanced.py is used only when it really sits next to the configured
    /// login script, so a tool without an advanced flow keeps working as before.
    /// </para>
    /// </summary>
    private string ExtraLoginScriptPath()
    {
        var login = LoginScriptPath();
        var dir = string.IsNullOrWhiteSpace(login)
            ? ""
            : System.IO.Path.GetDirectoryName(login) ?? "";

        if (dir.Length > 0)
        {
            var advanced = System.IO.Path.Combine(dir, "login_advanced.py");
            if (System.IO.File.Exists(advanced)) return advanced;
        }
        return login;
    }

    /// <summary>
    /// Feature 2 - reshape the Extra tab to match the tool actually open.
    /// The CLAIM block is MiniMax-specific: it only makes sense when the tool
    /// really ships a <c>claim_advanced.py</c> next to its login script (which
    /// <see cref="ClaimScriptPath"/> already points at). Z AI / Devin do not, so
    /// there the whole block is collapsed and the blurb drops every MiniMax
    /// mention.
    /// </summary>
    private void ApplyExtraTabToolShape()
    {
        var hasClaim = false;
        try
        {
            var claim = ClaimScriptPath();
            hasClaim = !string.IsNullOrWhiteSpace(claim) && File.Exists(claim);
        }
        catch
        {
            hasClaim = false;
        }

        if (ClaimSection is not null)
            ClaimSection.Visibility = hasClaim ? Visibility.Visible : Visibility.Collapsed;

        if (ExtraHeading is not null)
            ExtraHeading.Text = hasClaim
                ? "🎁 EXTRA TOOLS  -  Auto · Check · Force all · Claim"
                : "🎁 EXTRA TOOLS  -  Auto · Check · Force all";

        if (ExtraBlurb is not null)
            ExtraBlurb.Text = hasClaim
                ? "AUTO / CHECK / FORCE run the tool's login script on the accounts that need it. " +
                  "CLAIM runs the MiniMax Daily Check-in - the MiniMax desktop app is never touched. Every button streams its output below."
                : "AUTO / CHECK / FORCE run the tool's login script on the accounts that need it. " +
                  "Every button streams its output below.";

        // Browser mode: mirror the saved per-tool headless switch onto the Extra
        // tab's switch and its hint, so both views always show the same mode.
        SetHeadlessMode(ToolSettingsStore.IsHeadless(_toolId));
    }

    /// <summary>
    /// Runs a script of this tool (login_advanced.py / claim_advanced.py) with the
    /// tool's python + working folder and streams every line into the Extra log.
    /// </summary>
    /// <summary>
    /// Runs ONE Extra-tab script at a time and lets you stop it.
    ///
    /// Two things this adds over the first version:
    /// <list type="bullet">
    ///   <item><description>
    ///   a STOP button. There was none, so a CLAIM ALL / AUTO run could only be
    ///   ended by killing the panel - which left the python process (and its
    ///   Chrome) running, because nothing tied the child to the parent.
    ///   </description></item>
    ///   <item><description>
    ///   a one-run-at-a-time guard. Clicking AUTO then CLAIM ALL used to start
    ///   a second python while the first was still going, which is how you end
    ///   up with several accounts/browsers open at once.
    ///   </description></item>
    /// </list>
    /// </summary>
    private async Task RunExtraScriptAsync(string script, string arguments, string label)
    {
        if (_extraCts is not null)
        {
            AppendExtra("✗ a script is already running - press ⏹ STOP first.");
            return;
        }

        var settings = AISuitePanel.ToolSettingsStore.For(_toolId);
        var exe = settings?.PythonExePath ?? "";
        var work = settings?.ToolWorkingDirectory ?? "";

        if (string.IsNullOrWhiteSpace(script) || !System.IO.File.Exists(script))
        {
            AppendExtra($"✗ script not found: {script}");
            return;
        }
        if (string.IsNullOrWhiteSpace(exe) || !System.IO.File.Exists(exe))
        {
            AppendExtra("✗ the python.exe from the tool settings was not found.");
            return;
        }
        if (string.IsNullOrWhiteSpace(work) || !System.IO.Directory.Exists(work))
            work = System.IO.Path.GetDirectoryName(script) ?? "";

        AppendExtra($"▶ {label}");
        AppendExtra($"  $ {System.IO.Path.GetFileName(script)} {arguments}".TrimEnd());

        // Claim the single Extra slot + arm STOP before anything can start.
        var cts = new CancellationTokenSource();
        _extraCts = cts;
        SetExtraRunning(true);

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                // -u: redirected stdout is block-buffered by Python, so without
                // it the Extra/Claim log stayed empty until the script exited.
                Arguments = $"-u \"{script}\" {arguments}",
                WorkingDirectory = work,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };

            // Extra/Claim used to be the one launcher that forgot to tell the
            // script where the panel stores this tool's accounts.  claim_advanced.py
            // then fell back to its script folder (tools\minimax) and saw the
            // wrong/missing cookies.txt, account.txt and claims.txt - so every
            // MiniMax CLAIM button appeared dead even though the same data was
            // present in accounts\minimax.  Keep the working directory for code,
            // but route all account state through the canonical AppPaths folder,
            // exactly like PythonToolDriver and the row-login launcher.
            AISuitePanel.AppPaths.EnsureToolAccountsDir(_toolId);
            psi.Environment["PANEL_TOOL_STATE_DIR"] =
                AISuitePanel.AppPaths.ToolAccountsDir(_toolId);
            foreach (var kv in AISuitePanel.AppPaths.RuntimeEnvironment())
                psi.Environment[kv.Key] = kv.Value;

            using var proc = new System.Diagnostics.Process { StartInfo = psi };
            proc.OutputDataReceived += (_, e2) =>
            {
                if (e2.Data is not null) Dispatcher.Invoke(() => AppendExtra("  " + e2.Data));
            };
            proc.ErrorDataReceived += (_, e2) =>
            {
                if (e2.Data is not null) Dispatcher.Invoke(() => AppendExtra("  " + e2.Data));
            };
            proc.Start();
            _extraProc = proc;
            // Tie it to the panel: closing the panel must take this run and the
            // browser it opened with it. STOP kills the same tree on demand.
            ProcessGuard.Adopt(proc);
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            try
            {
                await proc.WaitForExitAsync(cts.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                ProcessGuard.KillTree(proc);
                AppendExtra($"⏹ {label} - stopped (script and its browser killed).");
                return;
            }

            ProcessGuard.Forget(proc);
            AppendExtra(proc.ExitCode == 0
                ? $"✓ {label} - finished (exit 0)"
                : $"✗ {label} - exit code {proc.ExitCode}");
        }
        catch (Exception ex)
        {
            AppendExtra($"✗ {label}: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _extraProc = null;
            _extraCts = null;
            SetExtraRunning(false);
            cts.Dispose();
        }
    }

    /// <summary>
    /// Locks the Extra-tab buttons while a script runs and un-hides STOP. One
    /// script at a time is deliberate: two at once is how several accounts and
    /// browsers end up open together.
    /// </summary>
    private void SetExtraRunning(bool running)
    {
        foreach (var b in new[] { AutoBtn, CheckBtn, ForceBtn,
                                  ClaimAllBtn, ClaimListBtn, ClaimSlotBtn })
        {
            if (b is not null) b.IsEnabled = !running;
        }
        if (ClaimSlotsBox is not null) ClaimSlotsBox.IsEnabled = !running;
        if (ExtraStopBtn is not null)
            ExtraStopBtn.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// STOP - end the running Extra script, plus its whole process tree and the
    /// browser it opened.
    /// </summary>
    private void OnExtraStop(object sender, RoutedEventArgs e)
    {
        var cts = _extraCts;
        if (cts is null)
        {
            AppendExtra("⏹ nothing is running.");
            return;
        }

        AppendExtra("⏹ STOP requested - killing the script and everything it opened…");
        try { cts.Cancel(); } catch { /* already gone */ }

        var proc = _extraProc;
        if (proc is not null) ProcessGuard.KillTree(proc);
    }

    /// <summary>
    /// Browser flag for the Extra-tab scripts, driven by the same per-tool
    /// HEADLESS switch as the Create tab: ON → <c>--headless</c>; OFF →
    /// <c>--open</c> (the visible-browser flag these scripts document — the
    /// claim script defaults to hidden, so passing nothing would leave it
    /// hidden).
    /// </summary>
    private string ExtraBrowserFlag() =>
        ToolSettingsStore.For(_toolId).Headless ? "--headless" : "--open";

    /// <summary>
    /// Extra tab's browser-mode switch. It is the SAME setting as the Create
    /// tab's HEADLESS toggle, so this only routes the click into the shared
    /// apply and mirrors the other switch; no second value is ever created.
    /// </summary>
    private void OnExtraHeadlessChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingHeadless) return;
        SetHeadlessMode(ExtraHeadlessToggle?.IsChecked == true);
    }

    /// <summary>
    /// Writes the per-tool headless switch and keeps BOTH switches in step.
    ///
    /// The guard matters: setting one toggle from code fires its Checked/Unchecked
    /// handler, which would otherwise write again and fight the other switch.
    /// </summary>
    private void SetHeadlessMode(bool on)
    {
        _syncingHeadless = true;
        try
        {
            ToolSettingsStore.SetHeadless(_toolId, on);

            if (HeadlessToggle is not null) HeadlessToggle.IsChecked = on;
            UpdateHeadlessToggleText(on);

            if (ExtraHeadlessToggle is not null)
            {
                ExtraHeadlessToggle.IsChecked = on;
                // The label has to follow the state, otherwise the switch reads
                // "HEADLESS: OFF" while it is actually ON - which is exactly how
                // it looked in the first UI test.
                ExtraHeadlessToggle.Content = on ? "HEADLESS: ON" : "HEADLESS: OFF";
            }
            if (ExtraHeadlessHint is not null)
                ExtraHeadlessHint.Text = on
                    ? "ON now - Chrome stays hidden for AUTO / CHECK / FORCE / CLAIM."
                    : "OFF now - Chrome opens for AUTO / CHECK / FORCE / CLAIM.";
        }
        finally
        {
            _syncingHeadless = false;
        }
    }

    /// <summary>
    /// AUTO - ONE account per click.
    ///
    /// <c>--limit 1</c> is deliberate: the tool's own menu option [4] walks
    /// every ready account, which opens account after account and leaves the
    /// last MiniMax session on screen. Here one click = one account: it logs in
    /// with the saved cookie, opens the MiniMax Agent page and clicks the
    /// launch button, so MiniMax comes up on exactly that account.
    ///
    /// The 24h rule is unchanged and still prints BOTH lists: every account
    /// ("all of them shown") plus why the used ones are skipped - and a skipped
    /// account becomes eligible again once it is older than 24h.
    /// </summary>
    private async void OnExtraAuto(object sender, RoutedEventArgs e) =>
        await RunExtraScriptAsync(ExtraLoginScriptPath(),
                                  $"--auto-unused --limit 1 --yes {ExtraBrowserFlag()}",
                                  "AUTO - ONE account unused in 24h");

    private async void OnExtraCheck(object sender, RoutedEventArgs e) =>
        await RunExtraScriptAsync(ExtraLoginScriptPath(), $"--check {ExtraBrowserFlag()}",
                                  "CHECK - cookie health");

    private async void OnExtraForce(object sender, RoutedEventArgs e) =>
        await RunExtraScriptAsync(ExtraLoginScriptPath(), $"--auto {ExtraBrowserFlag()} --yes",
                                  "FORCE ALL - login every account");

    private async void OnExtraClaimAll(object sender, RoutedEventArgs e) =>
        await RunExtraScriptAsync(ClaimScriptPath(), ExtraBrowserFlag(),
                                  "CLAIM ALL - accounts untouched in 24h");

    private async void OnExtraClaimList(object sender, RoutedEventArgs e) =>
        await RunExtraScriptAsync(ClaimScriptPath(), $"--list {ExtraBrowserFlag()}",
                                  "CLAIM LIST - who is ready");

    private async void OnExtraClaimSlots(object sender, RoutedEventArgs e)
    {
        var slots = (ClaimSlotsBox?.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(slots))
        {
            AppendExtra("✗ type the account number(s) first, for example  1,6,7");
            return;
        }
        await RunExtraScriptAsync(ClaimScriptPath(), $"--slots \"{slots}\" {ExtraBrowserFlag()}",
                                  $"CLAIM #{slots}");
    }

    /// <summary>
    /// Runs the configured login script per selected account and marks each row
    /// from the real result.  The driver
    /// <see cref="AISuitePanel.Automation.PythonToolDriver"/> puts the address in
    /// the script's .env, so the script verifies exactly that one cookie set.
    /// </summary>
    private async Task VerifyWithLoginScriptAsync(List<LoginRow> rows)
    {
        var driver = AISuitePanel.Automation.ToolDrivers.For(_toolId);
        if (driver is null)
        {
            AppendLog("⚠ No driver registered for this tool - cannot verify.");
            return;
        }

        AppendLog($"🔑 VERIFY LOGIN — running the login script for {rows.Count} cookie set(s)...");
        foreach (var row in rows)
        {
            var cookies = await LoadCookiesForAccountAsync(row.EmailUsed).ConfigureAwait(true);
            if (cookies.Count == 0)
            {
                row.LastVerify = "✗ no cookies";
                row.LastVerifyColor = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
                AppendLog($"  {row.EmailUsed}  →  ✗ no cookies in cookies.txt");
                continue;
            }

            AISuitePanel.Automation.DriverResult result;
            try
            {
                result = await driver.LoginWithCookiesAsync(
                    row.EmailUsed, cookies, CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                result = new AISuitePanel.Automation.DriverResult(false, $"{ex.GetType().Name}: {ex.Message}");
            }

            row.LastVerify = result.Ok ? "✓ live" : "✗ expired";
            row.LastVerifyColor = result.Ok
                ? new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99))
                : new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
            AppendLog($"  {row.EmailUsed}  →  {(result.Ok ? "✓" : "✗")} {result.Message}");
        }
        AppendLog("🔑 VERIFY LOGIN done.  Rows above show ✓ live / ✗ expired.");
    }

    /// <summary>
    /// Tool-specific probe URL — chat.z.ai for Z AI, devin.ai for Devin AI.
    /// </summary>
    private string VerifyUrl() => _toolId.ToLowerInvariant() switch
    {
        "zai"    => "https://chat.z.ai/",
        "devin"  => "https://app.devin.ai/",
        "tool4"  => "https://www.minimax.io/",
        _        => "https://example.com/"
    };

    /// <summary>
    /// Parse the script's <c>cookies.txt</c> (format: <c>email | [json]</c>) for
    /// this account, then HEAD the verify URL with the cookies stitched into a
    /// manual Cookie header.
    /// </summary>
    /// <summary>
    /// True when two addresses are the SAME mailbox.
    ///
    /// <para>Gmail ignores dots in the local part (and everything from a "+"
    /// onwards), so the cookie row saved as
    /// <c>sab.b.irmol.la2071@gmail.com</c> and a grid row showing
    /// <c>s.a.b.b.i.r.molla2071@gmail.com</c> are one single account. The old
    /// check was a plain <c>StartsWith</c>, which sees two different strings and
    /// reports "no cookies" for accounts whose cookies are sitting right there in
    /// the file - which is exactly the wall of "No cookies found" warnings in the
    /// activity log.
    /// </summary>
    private static bool SameMailbox(string a, string b)
    {
        var na = NormaliseMailbox(a);
        return na.Length > 0 && string.Equals(na, NormaliseMailbox(b),
                                              StringComparison.OrdinalIgnoreCase);
    }

    private static string NormaliseMailbox(string address)
    {
        var addr = (address ?? string.Empty).Trim().Trim('"');
        var at = addr.LastIndexOf('@');
        if (at <= 0) return addr.ToLowerInvariant();

        var local = addr[..at];
        var domain = addr[(at + 1)..].ToLowerInvariant();

        var plus = local.IndexOf('+');
        if (plus >= 0) local = local[..plus];

        if (domain is "gmail.com" or "googlemail.com")
        {
            local = local.Replace(".", string.Empty);
            domain = "gmail.com";          // the two spellings are one mailbox
        }

        return local.ToLowerInvariant() + "@" + domain;
    }

    private async Task<(bool, string)> ProbeCookieAsync(LoginRow row)
    {
        var cookiesPath = ResolveCookiesTxt();
        if (cookiesPath is null)
            return (false, "no cookies.txt in script or working folder");

        string? match = null;
        foreach (var line in File.ReadAllLines(cookiesPath, Encoding.UTF8))
        {
            var bar = line.IndexOf('|');
            if (bar <= 0) continue;
            if (SameMailbox(line[..bar], row.EmailUsed))
            {
                match = line;
                break;
            }
        }
        if (match is null) return (false, "no cookies.txt row for this email");

        // "email | [json]" → the part after the pipe is the JSON cookie array.
        var pipe = match.IndexOf('|');
        var cookieJson = match[(pipe + 1)..].Trim();
        List<BrowserCookie>? cookies;
        try
        {
            cookies = JsonSerializer.Deserialize<List<BrowserCookie>>(cookieJson, JsonOptions);
        }
        catch (Exception ex)
        {
            return (false, $"cookies.txt json parse: {ex.Message}");
        }
        if (cookies is null || cookies.Count == 0)
            return (false, "no cookies in entry");

        try
        {
            var handler = new HttpClientHandler
            {
                UseCookies = false,
                AllowAutoRedirect = false,
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
            var cookieHeader = string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}"));

            var req = new HttpRequestMessage(HttpMethod.Get, VerifyUrl());
            req.Headers.Add("Cookie", cookieHeader);
            req.Headers.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");

            using var resp = await http.SendAsync(req).ConfigureAwait(false);
            var status = (int)resp.StatusCode;
            var location = resp.Headers.Location?.OriginalString ?? "";

            if (status >= 200 && status < 400 &&
                !location.Contains("/login", StringComparison.OrdinalIgnoreCase))
                return (true, $"HTTP {status} (session valid)");

            if (status is 301 or 302 or 303 or 307 or 308 &&
                (location.Contains("/login", StringComparison.OrdinalIgnoreCase) ||
                 location.Contains("/auth",  StringComparison.OrdinalIgnoreCase)))
                return (false, $"HTTP {status} → /login (expired)");

            return (false, $"HTTP {status} (uncertain)");
        }
        catch (Exception ex)
        {
            return (false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private void OnLoginOne(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LoginRow row }) return;

        // Prefer the tool's OWN login script whenever one is configured and on
        // disk.  A cookies-only replay is not enough for every product: Devin,
        // for instance, keeps its session in localStorage (auth1_session) as
        // well as the webapp_logged_in cookie, so a cookies-only browser lands
        // on the login form again.  devin_login.py / minimax_login.py replay
        // BOTH and verify the session - so the row button runs that script for
        // this ONE account (VISIBLE, --open) and streams its output below.
        var settings = ToolSettingsStore.For(_toolId);
        var loginScript = settings.LoginScriptPath ?? "";
        if (!string.IsNullOrWhiteSpace(loginScript) && File.Exists(loginScript))
        {
            _ = RunLoginScriptForRowAsync(row, sender as Button);
            return;
        }

        // Fallback (no login script configured, or the file is missing): the
        // original cookie-only replay in a fresh visible browser.
        AppendLog($"🔑 Login #{row.IdLabel} ({row.EmailUsed}) — opening browser with cookies...");
        _ = Task.Run(async () =>
        {
            try
            {
                var cookies = await LoadCookiesForAccountAsync(row.EmailUsed).ConfigureAwait(false);
                if (cookies.Count == 0)
                {
                    Dispatcher.Invoke(() => AppendLog($"⚠ No cookies found in cookies.txt for {row.EmailUsed}."));
                    return;
                }

                var targetUrl = LoginTargetUrl();
                AppendLog($"  → {cookies.Count} cookies  →  {targetUrl}");
                await LaunchBrowserWithCookiesAsync(row.EmailUsed, cookies, targetUrl)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error(_toolId, $"Login threw: {ex.GetType().Name}: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Row "🔑 Login" through the tool's OWN login script (only called when one
    /// is configured and present).
    ///
    /// <para>
    /// 1. writes this row's address into the tool's <c>.env</c> as
    ///    <c>SIGNUP_EMAIL</c> + <c>PANEL_EMAIL</c> (via
    ///    <see cref="AISuitePanel.Automation.PythonToolDriver.SyncLoginTarget"/>,
    ///    which touches only those two keys) so a single-account login script
    ///    verifies exactly that cookie set;
    /// 2. runs <c>&lt;PythonExePath&gt; "&lt;LoginScriptPath&gt;" --open</c> with
    ///    the tool's working folder as cwd (script folder as fallback), i.e.
    ///    always a VISIBLE browser - never <c>--headless</c>;
    /// 3. streams stdout/stderr into the panel's Live Log and marks the row.
    /// </para>
    ///
    /// The per-row button is disabled for the duration so a double click can't
    /// launch two browsers at once.
    /// </summary>
    private async Task RunLoginScriptForRowAsync(LoginRow row, Button? button)
    {
        var settings = ToolSettingsStore.For(_toolId);
        var script = settings.LoginScriptPath ?? "";
        if (string.IsNullOrWhiteSpace(script) || !File.Exists(script))
        {
            AppendLog($"⚠ Login script not found ({script}) — nothing to run.");
            return;
        }

        var exe = settings.PythonExePath ?? "";
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            exe = _driver.ResolvedPythonPath();

        var work = settings.ToolWorkingDirectory ?? "";
        if (string.IsNullOrWhiteSpace(work) || !Directory.Exists(work))
            work = Path.GetDirectoryName(script) ?? "";

        // Point the login script at just this row. SyncLoginTarget keeps every
        // other .env key intact (mail settings etc. are never clobbered).
        _driver.SyncLoginTarget(settings, row.EmailUsed);

        // Where the session should open: the per-tool URL the user can edit on
        // this very Login tab. Prefer whatever is in the box RIGHT NOW (so typing
        // a referral link and clicking 🔑 Login works without a trip to Settings),
        // normalise it, and persist it so it survives a restart. Falls back to the
        // saved/default value when the box is empty; never empty in the end.
        var loginUrl = LoginUrlBox is not null && !string.IsNullOrWhiteSpace(LoginUrlBox.Text)
            ? ToolSettingsStore.NormaliseLoginUrl(LoginUrlBox.Text, ToolSettingsStore.DefaultLoginUrl(_toolId))
            : ToolSettingsStore.LoginUrl(_toolId);
        ToolSettingsStore.SetLoginUrl(_toolId, loginUrl);
        if (LoginUrlBox is not null) LoginUrlBox.Text = loginUrl;

        if (button is not null) button.IsEnabled = false;
        if (LoginVerifyBtn is not null) LoginVerifyBtn.IsEnabled = false;

        AppendLog($"🔑 Login #{row.IdLabel} ({row.EmailUsed}) — running {Path.GetFileName(script)} (visible browser)...");
        AppendLog($"  → wrote SIGNUP_EMAIL={row.EmailUsed} + PANEL_EMAIL to .env");
        AppendLog($"  → target URL: {loginUrl}");
        AppendLog($"  → --stay: the browser stays open after login — close the window when you are done.");
        AppendLog($"  $ {Path.GetFileName(script)} --open --stay --url \"{loginUrl}\"");
        // Mirror into the per-tool log file (workspaces\<tool>\logs\<date>.log)
        // so the exact command + arguments are auditable after the run.
        Log.Info(_toolId,
            $"  $ python {Path.GetFileName(script)} --open --stay --url \"{loginUrl}\"  " +
            $"(row login for {row.EmailUsed})");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = exe,
                WorkingDirectory       = work,
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                RedirectStandardInput  = true,
                CreateNoWindow         = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding  = Encoding.UTF8
            };
            // -u first: redirected stdout is block-buffered, so without it the
            // live log showed nothing until the login script exited.
            psi.ArgumentList.Add("-u");
            // Script path MUST be the first positional argument so python.exe
            // doesn't try to interpret --open as one of its own options.
            psi.ArgumentList.Add(script);
            psi.ArgumentList.Add("--open");
            // A visible browser that STAYS OPEN until the user closes it (the
            // script enforces a generous cap, e.g. 30 min, as a safety net).
            psi.ArgumentList.Add("--stay");
            // Land on the per-tool URL instead of the script's hard-coded page.
            psi.ArgumentList.Add("--url");
            psi.ArgumentList.Add(loginUrl);
            // Belt & braces: panel_mail.load_env() lets os.environ win over the
            // file, so the script sees the target even if a stale .env lingers.
            psi.Environment["SIGNUP_EMAIL"]   = row.EmailUsed;
            psi.Environment["PANEL_EMAIL"]    = row.EmailUsed;
            // Second way to hand the URL over (the script also reads --url).
            psi.Environment["PANEL_LOGIN_URL"] = loginUrl;

            // Where the tool keeps its accounts / cookies / .env.
            //
            // This was the bug behind "cookie login never works". Every tool
            // resolves cookies.txt through tools/_shared/tool_state.py, which
            // returns PANEL_TOOL_STATE_DIR when it is set and otherwise falls
            // back to the folder holding the script. PythonToolDriver and
            // AccioToolDriver both export this for the runs they start; this
            // hand-rolled launcher - the per-row "🔑 Login" button - did not.
            //
            // So Zloginv2 looked for cookies.txt in tools\zai\ (where the panel
            // has never written one), found nothing, printed "[X] No cookies!",
            // hit the EOF on input() and exited 1 - even though 11 cookie sets
            // were sitting in accounts\zai\cookies.txt. The button was dead for
            // Z AI, Devin and MiniMax alike.
            AppPaths.EnsureToolAccountsDir(_toolId);
            psi.Environment["PANEL_TOOL_STATE_DIR"] = AppPaths.ToolAccountsDir(_toolId);

            // Point Playwright at the browser the bundle ships, exactly as the
            // driver does. Without it a portable install (which has no
            // %LOCALAPPDATA%\ms-playwright) dies with "Executable doesn't exist"
            // the moment the login browser is launched.
            foreach (var kv in AppPaths.RuntimeEnvironment())
            {
                psi.Environment[kv.Key] = kv.Value;
            }

            using var proc = new Process { StartInfo = psi };
            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                Log.Info(_toolId, "  | " + e.Data);
                Dispatcher.Invoke(() => AppendLog("  " + e.Data));
            };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                Log.Warn(_toolId, "  ! " + e.Data);
                Dispatcher.Invoke(() => AppendLog("  [err] " + e.Data));
            };
            proc.Start();
            // Row-login browsers must not outlive the panel either. Adopted
            // AFTER Start() on purpose - the job needs a live process handle.
            ProcessGuard.Adopt(proc);
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            // Close stdin: a one-shot login script ignores it, an interactive
            // one (Zloginv2) must not sit forever waiting on input().
            try { proc.StandardInput.Close(); } catch { /* already gone */ }

            await proc.WaitForExitAsync().ConfigureAwait(true);

            var ok = proc.ExitCode == 0;
            row.LastVerify = ok ? "✓ live" : "✗ not live";
            row.LastVerifyColor = ok
                ? new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99))
                : new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
            AppendLog(ok
                ? $"✓ {row.EmailUsed} — login script finished (exit 0), session live."
                : $"✗ {row.EmailUsed} — login script exit code {proc.ExitCode} (session not live).");
            Log.Info(_toolId, $"login script {Path.GetFileName(script)} for {row.EmailUsed} -> exit {proc.ExitCode}");
        }
        catch (Exception ex)
        {
            AppendLog($"✗ Login script failed: {ex.GetType().Name}: {ex.Message}");
            Log.Error(_toolId, $"login script {Path.GetFileName(script)}: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (button is not null) button.IsEnabled = true;
            if (LoginVerifyBtn is not null) LoginVerifyBtn.IsEnabled = true;
        }
    }

    /// <summary>
    /// Where the browser should land after cookie injection - now the per-tool
    /// URL the user can edit on the Login tab (mirrors Accio's URL field), so a
    /// referral link works for the cookies-only fallback too. Falls back to the
    /// tool's built-in default when the box is empty.
    /// </summary>
    private string LoginTargetUrl() => ToolSettingsStore.LoginUrl(_toolId);

    /// <summary>
    /// Read the script's <c>cookies.txt</c> (format: <c>email | [json]</c>) and
    /// return the cookie list for this email. Returns an empty list when the
    /// entry is missing or unparseable.
    /// </summary>
    private async Task<List<BrowserCookie>> LoadCookiesForAccountAsync(string email)
    {
        var cookiesPath = ResolveCookiesTxt();
        if (cookiesPath is null) return new List<BrowserCookie>();

        string? match = null;
        foreach (var line in await File.ReadAllLinesAsync(cookiesPath, Encoding.UTF8).ConfigureAwait(false))
        {
            var bar = line.IndexOf('|');
            if (bar <= 0) continue;

            // SameMailbox, not StartsWith. Gmail ignores dots in the local part
            // and everything from a "+", so the address a grid row shows and the
            // key the script saved can differ while naming ONE account. The old
            // StartsWith test called those different strings and reported
            // "no cookies" for rows whose cookies were sitting in the file.
            if (SameMailbox(line[..bar], email))
            {
                match = line;
                break;
            }
        }
        if (match is null) return new List<BrowserCookie>();

        var pipe = match.IndexOf('|');
        var json = match[(pipe + 1)..].Trim();
        try
        {
            return JsonSerializer.Deserialize<List<BrowserCookie>>(json, JsonOptions)
                   ?? new List<BrowserCookie>();
        }
        catch
        {
            return new List<BrowserCookie>();
        }
    }

    /// <summary>
    /// Open Chromium with a FRESH (non-persistent) context, inject the cookies,
    /// then navigate to the dashboard URL so the user lands signed in.
    ///
    /// Why fresh instead of the script's <c>launch_persistent_context</c>:
    /// the script's persistent profile keeps every account's cookies across
    /// runs, so when you login to Account B the browser still has Account
    /// A's session cookies sitting in the Cookie jar and chat.z.ai picks the
    /// wrong one. A fresh context = only the cookies we just injected.
    /// </summary>
    private async Task LaunchBrowserWithCookiesAsync(string email, List<BrowserCookie> cookies, string targetUrl)
    {
        IPlaywright? playwright = null;
        IBrowser? browser = null;
        IBrowserContext? context = null;
        IPage? page = null;
        var browserReady = false;

        try
        {
            playwright = await Playwright.CreateAsync().ConfigureAwait(false);
            browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = false,
                Args = new[] {
                    "--disable-blink-features=AutomationControlled",
                    "--no-sandbox",
                    "--disable-third-party-cookies",
                    "--window-size=1280,800"
                }
            }).ConfigureAwait(false);
            browserReady = true;

            context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize { Width = 1280, Height = 800 },
                UserAgent    = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36"
            }).ConfigureAwait(false);

            // Inject FIRST, then navigate. Navigating before injection would
            // hit chat.z.ai without the token cookie, which redirects to
            // /auth and the token never reaches the server.
            var pwCookies = cookies.Select(c => new Microsoft.Playwright.Cookie
            {
                Name     = c.Name,
                Value    = c.Value,
                Domain   = NormalisePwDomain(c.Domain),
                Path     = string.IsNullOrEmpty(c.Path) ? "/" : c.Path,
                Expires  = c.Expires < 0 ? -1f : (float)c.Expires,
                HttpOnly = c.HttpOnly,
                Secure   = c.Secure,
                SameSite = ParseSameSite(c.SameSite)
            }).ToArray();
            await context.AddCookiesAsync(pwCookies).ConfigureAwait(false);

            page = await context.NewPageAsync().ConfigureAwait(false);
            var resp = await page.GotoAsync(targetUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout   = 15_000
            }).ConfigureAwait(false);

            if (resp is null || !resp.Ok)
            {
                Dispatcher.Invoke(() => AppendLog($"⚠ {targetUrl}  →  HTTP {resp?.Status ?? 0}"));
            }
            else
            {
                Dispatcher.Invoke(() => AppendLog($"✓ Loaded {targetUrl}  ({resp.Status})  — should be signed in."));
            }

            // Verify: if the page redirected to /auth, the token cookie
            // didn't take — surface a clear hint to the user.
            var curUrl = page.Url;
            if (curUrl.Contains("/auth", StringComparison.OrdinalIgnoreCase) ||
                curUrl.Contains("/login", StringComparison.OrdinalIgnoreCase))
            {
                Dispatcher.Invoke(() => AppendLog(
                    "⚠ Page redirected to login — cookies may be expired. Try VERIFY LOGIN to confirm."));
            }
        }
        catch (Exception ex)
        {
            Log.Warn(_toolId, $"Browser launch failed: {ex.GetType().Name}: {ex.Message}");
        }

        // Park a watchdog so the browser + context don't get GC'd while
        // the user is interacting. Exit when the browser window closes.
        _ = Task.Run(async () =>
        {
            while (browserReady && page is { IsClosed: false })
                await Task.Delay(1000).ConfigureAwait(false);
            try { if (context is not null) await context.DisposeAsync().ConfigureAwait(false); } catch { }
            try { if (browser  is not null) await browser.DisposeAsync().ConfigureAwait(false);  } catch { }
        });
    }

    private static string NormalisePwDomain(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return ".z.ai";
        return domain.StartsWith('.') ? domain : "." + domain;
    }

    private static SameSiteAttribute ParseSameSite(string? value) => value?.ToLowerInvariant() switch
    {
        "strict" => SameSiteAttribute.Strict,
        "none"   => SameSiteAttribute.None,
        _        => SameSiteAttribute.Lax
    };

    private void OnDeleteAccount(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LoginRow row }) return;
        if (row.Account is null) return;

        var ans = MessageBox.Show(
            $"Delete account #{row.IdLabel} ({row.EmailUsed})?\n\n" +
            "This removes the entry from the script's accounts.txt (and the " +
            "matching cookies.txt row). Cannot be undone.",
            "Delete account", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (ans != MessageBoxResult.OK) return;

        AccountStore.Delete(_toolId, row.Account);
        Log.Warn(_toolId, $"#{row.IdLabel} ({row.EmailUsed}) deleted from accounts.txt");
        ReloadLoginRows();
        AccountStore.Refresh(_toolId);
        RefreshStats();
    }

    // ----------------------------------------------------------------- create tab

    /// <summary>
    /// Pick the mail source the script will use when the user hasn't manually
    /// picked one — derived from Global Settings. Same priority AccioPanelView
    /// uses; priority drives the default radio selection.
    /// </summary>
    private MailSource AutoSource()
    {
        // Gmail Pool first, by request: it is the method that must be
        // pre-selected every time, on every tool.  The other methods stay one
        // click away, and are still auto-picked when the pool is not configured
        // at all (otherwise the radio would default to a method that cannot run).
        if (SettingsStore.DottedGmailUsable)  return MailSource.DottedGmail;
        if (SettingsStore.CustomMailUsable)   return MailSource.CustomMail;
        if (SettingsStore.CustomDomainUsable) return MailSource.CustomDomain;
        if (SettingsStore.TempMailUsable)     return MailSource.TempMail;
        return MailSource.DottedGmail;
    }

    /// <summary>What the user actually picked on this screen — test is handled
    /// separately in <see cref="OnRun"/>, so this never returns a "test" mode.</summary>
    private MailSource SelectedMode() =>
        ModeTest.IsChecked   == true ? AutoSource()  // test runs below override; default to auto for display
      : ModePool.IsChecked   == true ? MailSource.DottedGmail
      : ModeTemp.IsChecked   == true ? MailSource.TempMail
      : ModeDomain.IsChecked == true ? MailSource.CustomDomain
      : ModeCustom.IsChecked == true ? MailSource.CustomMail
      : ModeSemi.IsChecked   == true ? MailSource.SemiAuto
      : ModeNoPx.IsChecked   == true ? MailSource.NoProxy
      : AutoSource();

    /// <summary>
    /// Render the Global-Settings summary cards so the user can see at a
    /// glance what each method has configured.  Pure read — never writes;
    /// <c>Global Settings</c> is the single source of truth.
    /// Also defaults the per-run radio buttons to <see cref="AutoSource"/>.
    /// </summary>
    private void RefreshMailSource()
    {
        var s = SettingsStore.Current;

        var pool = s.DottedGmail?.Accounts?
            .Where(a => SettingsStore.IsGmail(a.Email) && !string.IsNullOrWhiteSpace(a.AppPassword))
            .ToList() ?? new List<GmailEntry>();
        GmailPoolSummary.Text = pool.Count == 0
            ? "(empty — add one in Global Settings → Method 2)"
            : $"{pool.Count} Gmail{(pool.Count == 1 ? "" : "s")}  →  {string.Join(", ", pool.Take(3).Select(g => g.Email))}{(pool.Count > 3 ? ", ..." : "")}";

        var d = s.CustomDomain;
        DomainSummary.Text = string.IsNullOrWhiteSpace(d.Domain)
            ? "(none configured)"
            : $"{d.Domain}  →  {(string.IsNullOrWhiteSpace(d.ForwardTarget) ? "(no forward target)" : d.ForwardTarget)}";

        var cm = s.CustomMail;
        CustomMailSummary.Text = string.IsNullOrWhiteSpace(cm.Email)
            ? "(none configured)"
            : $"{cm.Email}  (app password: {(string.IsNullOrWhiteSpace(cm.AppPassword) ? "MISSING" : "set")})";

        // Default the per-run radio to whatever AutoSource() picks — user can
        // still override per-run by clicking another radio before RUN.
        var auto = AutoSource();
        ModePool.IsChecked   = auto == MailSource.DottedGmail;
        ModeTemp.IsChecked   = auto == MailSource.TempMail;
        ModeDomain.IsChecked = auto == MailSource.CustomDomain;
        ModeCustom.IsChecked = auto == MailSource.CustomMail;
        ModeSemi.IsChecked   = auto == MailSource.SemiAuto;
        ModeNoPx.IsChecked   = auto == MailSource.NoProxy;
        ModeTest.IsChecked   = false;
    }

    private void OnProxyToggleChanged(object sender, RoutedEventArgs e)
    {
        if (ProxyToggle is null) return;
        var on = ProxyToggle.IsChecked == true;
        UpdateProxyToggleText(on);

        // Restore-on-open path: the value is already what the user chose, so
        // writing it back is a no-op and the log line would be noise.
        if (_syncingProxy) return;

        ToolSettingsStore.SetProxyOn(_toolId, on);
        AppendLog(on
            ? "🔌 Proxy ON  — remembered for this tool; scripts use their own proxy / .env settings."
            : "🔌 Proxy OFF (--no-proxy) — remembered for this tool.");
    }

    /// <summary>
    /// Reflects the persisted per-tool PROXY flag on the switch. Runs after the
    /// siblings are wired, for the same XAML-load reason the forced-ON default
    /// was moved there in the first place.
    /// </summary>
    private void SyncProxyToggle()
    {
        if (ProxyToggle is null) return;
        _syncingProxy = true;
        try
        {
            var on = ToolSettingsStore.IsProxyOn(_toolId);
            ProxyToggle.IsChecked = on;
            UpdateProxyToggleText(on);
        }
        finally
        {
            _syncingProxy = false;
        }
    }

    /// <summary>Keeps the switch's own label and its hint line in step with the state.</summary>
    private void UpdateProxyToggleText(bool on)
    {
        if (ProxyToggle is not null) ProxyToggle.Content = on ? "ON" : "OFF";
        if (ProxyHint is not null)
            ProxyHint.Text = on
                ? "ON → use the script's own proxy / .env settings."
                : "OFF → panel will append --no-proxy to the script invocation.";
    }

    // ------------------------------------------------------- headless switch

    /// <summary>Reflects the persisted per-tool HEADLESS flag on the switch.</summary>
    private void SyncHeadlessToggle()
    {
        if (HeadlessToggle is null) return;
        var on = ToolSettingsStore.For(_toolId).Headless;
        HeadlessToggle.IsChecked = on;
        UpdateHeadlessToggleText(on);
    }

    private void UpdateHeadlessToggleText(bool on)
    {
        if (HeadlessToggle is not null) HeadlessToggle.Content = on ? "HEADLESS: ON" : "HEADLESS: OFF";
        if (HeadlessHint is not null)
            HeadlessHint.Text = on
                ? "ON → scripts run with --headless (no browser window)."
                : "OFF → scripts run with a visible browser.";
    }

    /// <summary>
    /// Per-tool HEADLESS switch. Persisted the moment it is flipped (so it
    /// survives a restart even if the user never presses SAVE SETTINGS); every
    /// create run and every Extra-tab script run reads it back from
    /// <see cref="ToolSettingsStore"/>.
    /// </summary>
    private void OnHeadlessToggleChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingHeadless) return;
        if (HeadlessToggle is null) return;
        var on = HeadlessToggle.IsChecked == true;
        // Writes the value AND mirrors the Extra-tab switch - the two are views
        // of one setting, never two settings.
        SetHeadlessMode(on);
        AppendLog(on
            ? "🕶 HEADLESS: ON — scripts will run with --headless (no browser window)."
            : "🕶 HEADLESS: OFF — scripts will run with a visible browser.");
    }

    private void OnDigitsOnly(object sender, TextCompositionEventArgs e)
    {
        // Allow digits only - same shape as AccioPanelView's preview filter.
        foreach (var c in e.Text) if (!char.IsDigit(c)) { e.Handled = true; return; }
    }

    private async void OnRun(object sender, RoutedEventArgs e)
    {
        if (_running) return;

        // Total  = how many accounts this run should attempt overall.
        // AtOnce = how many of them may be in flight at the same time (waves).
        if (!int.TryParse(TotalBox.Text.Trim(), out var qty) || qty < 1)
        {
            Log.Warn(_toolId, "Total must be a positive integer.");
            return;
        }

        if (!int.TryParse(AtOnceBox.Text.Trim(), out var atOnce) || atOnce < 1)
            atOnce = 1;
        atOnce = Math.Clamp(atOnce, 1, qty);

        // Test mode first — simulated run, no real signup, no python.exe invoked.
        if (ModeTest.IsChecked == true)
        {
            await RunTestSimulationAsync(qty);
            return;
        }

        var mode = SelectedMode();
        if (int.TryParse(DelaySecondsBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var delay))
        {
            var settings = ToolSettingsStore.For(_toolId);
            settings.DelaySeconds = Math.Clamp(delay, 0, 120);
            ToolSettingsStore.Save(_toolId, settings);
        }
        var headless = ToolSettingsStore.For(_toolId).Headless;
        AppendLog($"▶ RUN — mode={CreateAccountRunner.SourceLabel(mode)}  total={qty}  " +
                  $"atOnce={atOnce}  headless={(headless ? "ON" : "OFF")}  tool={_toolId}");

        // Bail out fast if the user hasn't configured any mail. Script can't do
        // anything useful without at least one Gmail or a custom domain.
        if (!SettingsStore.Usable(mode))
        {
            AppendLog("✗ Selected mail source is not configured. Pick another or open Global Settings.");
            return;
        }

        // Feature 1 - don't start a run that can never get a mailbox: if the
        // user banned every pool Gmail for this tool, say so clearly instead of
        // letting the router silently return nothing.
        if (mode is MailSource.DottedGmail or MailSource.SemiAuto or MailSource.NoProxy &&
            MailRouter.AllGmailsBanned(_toolId))
        {
            AppendLog($"✗ Every Gmail configured in Global Settings is BANNED for {_toolId}. " +
                      "Open Settings → MAIL and add one back (this affects only this tool).");
            return;
        }

        ProgressText.Text = $"00 / {qty:00}";
        Progress.Value = 0;
        ProgressPanel.Visibility = Visibility.Visible;
        RunBtn.Visibility = Visibility.Collapsed;
        StopBtn.Visibility = Visibility.Visible;
        PreflightBtn.IsEnabled = false;
        _running = true;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        // Hand the address off to the driver via MailRouter — same path Accio
        // uses. The script does its own routing internally (reads GMAIL_USER
        // from .env which TrySyncEnv wrote), so the address is mostly for the
        // log; but MailRouter.Next also marks the alias so the next run skips
        // duplicates if the user does pick-and-mix.
        var forceNoProxy = ProxyToggle.IsChecked != true;

        var done = 0;
        var failed = 0;
        var completed = 0;

        // WAVES: `atOnce` accounts run in parallel; the moment one finishes the
        // next serial is handed to the freed slot until `qty` have been
        // attempted (or the STOP button cancels us).
        var gap = TimeSpan.FromSeconds(Math.Clamp(ToolSettingsStore.For(_toolId).DelaySeconds, 0, 120));
        using var gate = new SemaphoreSlim(atOnce, atOnce);
        var workers = new List<Task>(qty);

        try
        {
            for (var i = 0; i < qty; i++)
            {
                if (ct.IsCancellationRequested) break;
                workers.Add(Worker(i + 1));

                // Keep the existing delaySeconds spacing between spawns (same
                // gap the shared CreateAccountRunner applies).
                if (gap > TimeSpan.Zero && i < qty - 1)
                {
                    try { await Task.Delay(gap, ct).ConfigureAwait(true); }
                    catch (OperationCanceledException) { break; }
                }
            }

            await Task.WhenAll(workers).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // STOP pressed while workers were still waiting for a free slot.
        }
        catch (Exception ex)
        {
            Log.Error(_toolId, $"Run loop crashed: {ex.GetType().Name}: {ex.Message}");
        }

        AppendLog($"Run finished — created: {done}  failed: {failed}  total: {qty}");

        ProgressPanel.Visibility = Visibility.Collapsed;
        RunBtn.Visibility = Visibility.Visible;
        StopBtn.Visibility = Visibility.Collapsed;
        PreflightBtn.IsEnabled = true;
        _running = false;
        _cts?.Dispose();
        _cts = null;

        AccountStore.Refresh(_toolId);
        RefreshStats();

        // ------------------------------------------------------------- worker
        async Task Worker(int serial)
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (ct.IsCancellationRequested) return;

                var addr = await MailRouter.NextAsync(_toolId, mode, serial).ConfigureAwait(false);
                if (addr is null)
                {
                    Interlocked.Increment(ref failed);
                    AppendLog(MailRouter.AllGmailsBanned(_toolId)
                        ? $"✗ Every Gmail is BANNED for {_toolId} — nothing to pick. Add one back in Settings → MAIL."
                        : "✗ Mail router returned no address. Configure Global Settings → Method 2 (DottedGmail) or pick another source.");
                    return;
                }

                AppendLog($"--- {serial}/{qty}  ({addr.Address}  ref:{addr.SourceRef}) ---");
                var result = await _driver.CreateAccountAsync(
                    addr.Address, mode, AppendLog, ct,
                    forceNoProxy, 1).ConfigureAwait(false);

                if (result.Ok) Interlocked.Increment(ref done);
                else Interlocked.Increment(ref failed);
            }
            catch (OperationCanceledException)
            {
                // STOP pressed - the driver already logged its own cancellation.
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failed);
                Log.Error(_toolId, $"#{serial:00} stopped - {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                gate.Release();

                var finished = Interlocked.Increment(ref completed);
                Dispatcher.Invoke(() =>
                {
                    ProgressText.Text = $"{finished:00} / {qty:00}";
                    Progress.Value = finished * 100.0 / Math.Max(1, qty);
                });
            }
        }
    }

    private void OnStop(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        AppendLog("⏹ STOP requested - finishing current step.");
    }

    /// <summary>
    /// Same "no real signup, just a believable log stream" simulation that
    /// AccioPanelView uses, so the user can see the full UI cycle without
    /// burning through a real account.
    /// </summary>
    private async Task RunTestSimulationAsync(int qty)
    {
        AppendLog("▶ TEST RUN — no real signup, no python invocation, no network call");
        AppendLog("─────────────────────────────────────────────────────");

        ProgressText.Text = $"00 / {qty:00}";
        Progress.Value = 0;
        ProgressPanel.Visibility = Visibility.Visible;
        RunBtn.Visibility = Visibility.Collapsed;
        StopBtn.Visibility = Visibility.Visible;
        _running = true;
        _cts = new CancellationTokenSource();

        for (var i = 0; i < qty; i++)
        {
            if (_cts.IsCancellationRequested) break;
            var done = i + 1;

            AppendLog($"[sim] python Zcreatev2.py -- count=1 (session {done})");
            await Task.Delay(200, _cts.Token);
            // Placeholder, not the operator's address: these demo lines used to
            // spell out a real Gmail and it shipped inside the exe.
            AppendLog("[sim] Loading .env: GMAIL_USER=you@example.com");
            await Task.Delay(150, _cts.Token);
            var dotted = $"ex.am.ple+test{done}@gmail.com";
            AppendLog($"[sim] dot variation: {dotted}");
            await Task.Delay(200, _cts.Token);
            AppendLog("[sim] Navigating to https://chat.z.ai/auth...");
            await Task.Delay(300, _cts.Token);
            AppendLog("[sim] Continue with Email → Sign up clicked");
            await Task.Delay(200, _cts.Token);
            AppendLog("[sim] Captcha: solve manually...");
            await Task.Delay(500, _cts.Token);
            AppendLog("[sim] Captcha solved");
            await Task.Delay(200, _cts.Token);
            AppendLog("[sim] Create Account clicked");
            await Task.Delay(300, _cts.Token);
            AppendLog("[sim] Verify Your Email screen — IMAP polling...");
            await Task.Delay(700, _cts.Token);
            AppendLog("[sim] Verify URL: https://chat.z.ai/set-password?token=...");
            await Task.Delay(300, _cts.Token);
            AppendLog("[sim] Password set — DONE!");

            Dispatcher.Invoke(() =>
            {
                var rec = new AccountRecord
                {
                    Id          = AccountStore.NextId(_toolId),
                    EmailUsed   = dotted,
                    MailSource  = MailSource.DottedGmail,
                    SourceRef   = "test-simulation",
                    Status      = AccountStatus.Active,
                    CookieState = CookieState.Saved,
                    CreatedAt   = DateTime.Now,
                    Note        = "TEST MODE - synthetic row, not a real account"
                };
                AccountStore.Add(_toolId, rec);
                CookieVault.Save(_toolId, new CookieRecord
                {
                    AccountId = rec.Id,
                    Email     = dotted,
                    SavedAt   = DateTime.Now,
                    Cookies   = new List<BrowserCookie>
                    {
                        new() { Name = "token", Value = "fake_" + Guid.NewGuid().ToString("N")[..24], Domain = ".z.ai", Path = "/", HttpOnly = true, Secure = true, SameSite = "Lax" }
                    }
                });
            });

            ProgressText.Text = $"{done:00} / {qty:00}";
            Progress.Value = done * 100.0 / qty;
            RefreshStats();
        }

        AppendLog("─────────────────────────────────────────────────────");
        AppendLog($"TEST RUN finished — {qty} synthetic rows added.");

        ProgressPanel.Visibility = Visibility.Collapsed;
        RunBtn.Visibility = Visibility.Visible;
        StopBtn.Visibility = Visibility.Collapsed;
        _running = false;
        _cts?.Dispose();
        _cts = null;
    }

    /// <summary>
    /// Cheap pre-flight: confirms python.exe resolves, scripts exist on disk,
    /// working directory has an .env (or we have a Gmail pool to write one),
    /// and that the script's accounts.txt is parseable.
    /// </summary>
    private async void OnPreflight(object sender, RoutedEventArgs e)
    {
        PreflightBtn.IsEnabled = false;
        AppendLog("🔍 PRE-FLIGHT: checking python + scripts + .env + accounts.txt...");

        var s = ToolSettingsStore.For(_toolId);

        // python
        var py = _driver.ResolvedPythonPath();
        AppendLog($"  python       → {py}");
        // File.Exists doesn't handle quoted paths with spaces — strip any
        // surrounding quotes and check the literal path.
        var pyExe = py.Trim().Trim('"');
        AppendLog(File.Exists(pyExe) ? "  ✓ python.exe found"
                                     : "  ⚠ python.exe not found — add to PATH or set PythonExePath");

        // create script
        AppendLog($"  create.py    → {s.CreateScriptPath ?? "(empty)"}");
        AppendLog(!string.IsNullOrWhiteSpace(s.CreateScriptPath) && File.Exists(s.CreateScriptPath)
            ? "  ✓ create script found" : "  ⚠ create script missing or empty");

        // login script
        AppendLog($"  login.py     → {s.LoginScriptPath ?? "(empty)"}");
        AppendLog(!string.IsNullOrWhiteSpace(s.LoginScriptPath) && File.Exists(s.LoginScriptPath)
            ? "  ✓ login script found" : "  ⚠ login script missing or empty");

        // working dir
        var workOk = !string.IsNullOrWhiteSpace(s.ToolWorkingDirectory) && Directory.Exists(s.ToolWorkingDirectory);
        AppendLog($"  working dir  → {s.ToolWorkingDirectory ?? "(empty)"}");
        AppendLog(workOk ? "  ✓ working dir exists" : "  ⚠ working dir missing");

        // .env (or Gmail pool)
        if (workOk)
        {
            var envPath = Path.Combine(s.ToolWorkingDirectory!, ".env");
            if (File.Exists(envPath))
            {
                AppendLog($"  .env         → {envPath}");
                AppendLog("  ✓ .env present — script will use it");
            }
            else
            {
                AppendLog($"  .env         → (not present at {envPath})");
                var pool = SettingsStore.Current.DottedGmail?.Accounts?
                    .Where(a => SettingsStore.IsGmail(a.Email) && !string.IsNullOrWhiteSpace(a.AppPassword))
                    .ToList();
                AppendLog((pool?.Count ?? 0) > 0
                    ? $"  ✓ Gmail pool has {pool!.Count} entry — panel will write .env at run-time"
                    : "  ⚠ no .env AND Gmail pool empty — set Global Settings → Method 2");
            }

            // Same rule as the account list: the scripts write beside the exe in
            // accounts\<tool>\, so that is the first place to look; the working
            // folder is only a fallback for a not-yet-migrated install.
            var acctPath = Path.Combine(AppPaths.ToolAccountsDir(_toolId), "accounts.txt");
            if (!File.Exists(acctPath))
                acctPath = Path.Combine(s.ToolWorkingDirectory!, "accounts.txt");
            if (File.Exists(acctPath))
                AppendLog($"  accounts.txt → {acctPath} ({File.ReadAllLines(acctPath).Length} lines)");
            else
                AppendLog("  accounts.txt → (will be created on first run)");
        }

        // gmail pool summary (shared global setting, same source Accio uses)
        var dg = SettingsStore.Current.DottedGmail;
        AppendLog(dg?.Accounts?.Count > 0
            ? $"  Gmail pool   → {dg.Accounts.Count} entries (used: round-robin)"
            : "  Gmail pool   → (empty - configure Global Settings → Method 2)");

        AppendLog("✓ PRE-FLIGHT done.");

        PreflightBtn.IsEnabled = true;
        await Task.CompletedTask;
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

            // Follow the output. Without this the box stayed on line 1 and the
            // stream looked frozen even though lines were arriving.
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
        const double max = 900;   // sanity cap - the log is inside a scrollable tab
        var wanted = LogHost.Height + delta;
        if (double.IsNaN(wanted)) wanted = 240;
        LogHost.Height = Math.Min(max, Math.Max(min, wanted));
        LogScroll?.ScrollToEnd();
    }

    /// <summary>Wipe the Create tab's LIVE LOG box (the streamed script stdout).</summary>
    private void OnClearLiveLog(object sender, RoutedEventArgs e)
    {
        if (LogBox is null) return;
        LogBox.Text = "(live log cleared)";
    }

    /// <summary>Copy the LIVE LOG box - falls back to the per-tool log file when empty.</summary>
    private void OnCopyLiveLog(object sender, RoutedEventArgs e)
    {
        try
        {
            var live = LogBox?.Text ?? "";
            var text = string.IsNullOrWhiteSpace(live) ? Log.Text(_toolId) : live;
            if (string.IsNullOrWhiteSpace(text))
            {
                AppendLog("⚠ Nothing to copy - the live log is empty.");
                return;
            }
            Clipboard.SetText(text);
            AppendLog($"🖹 Live log copied to clipboard ({text.Length} chars).");
        }
        catch (Exception ex)
        {
            AppendLog("⚠ Could not copy the live log - " + ex.Message);
        }
    }

    // ------------------------------------------------- MAIL ban list (Feature 1)

    /// <summary>
    /// Rebuilds the Settings-tab MAIL list from Global Settings + this tool's
    /// ban list. Every configured pool Gmail is shown once, flagged ACTIVE or
    /// BANNED for THIS tool only.
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
                IsBanned = ToolSettingsStore.IsExcluded(_toolId, g.Email)
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
    /// </summary>
    private void OnMailToggle(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MailBanRow row }) return;
        if (string.IsNullOrWhiteSpace(row.Email)) return;

        var excluded = ToolSettingsStore.Excluded(_toolId).ToList();
        var willBan  = !ToolSettingsStore.IsExcluded(_toolId, row.Email);

        if (willBan)
        {
            if (!excluded.Any(x => string.Equals((x ?? "").Trim(), row.Email.Trim(), StringComparison.OrdinalIgnoreCase)))
                excluded.Add(row.Email.Trim());
        }
        else
        {
            excluded.RemoveAll(x => string.Equals((x ?? "").Trim(), row.Email.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        ToolSettingsStore.SetExcluded(_toolId, excluded);
        RefreshMailBanList();

        AppendLog(willBan
            ? $"🚫 BANNED {row.Email} for {_toolId} only — Global Settings untouched. Other tools still use it."
            : $"➕ Add back {row.Email} for {_toolId} (banned list updated).");

        if (willBan && MailRouter.AllGmailsBanned(_toolId))
            AppendLog($"⚠ Every Gmail is now BANNED for {_toolId} — add one back in Settings → MAIL to keep creating accounts.");
    }

    // ----------------------------------------------------------------- settings tab

    private void LoadSettings()
    {
        var s = ToolSettingsStore.For(_toolId);
        PythonExeBox.Text    = s.PythonExePath ?? "";
        CreateScriptBox.Text = s.CreateScriptPath ?? "";
        LoginScriptBox.Text  = s.LoginScriptPath ?? "";
        WorkDirBox.Text      = s.ToolWorkingDirectory ?? "";
        CreateArgsBox.Text   = s.CreateArgTemplate ?? "--count {count}";
        LoginArgsBox.Text    = s.LoginArgTemplate ?? "";

        // Show the EFFECTIVE login URL (persisted value or the tool default), so
        // the box is never blank and the user always sees what will be passed.
        if (LoginUrlBox is not null) LoginUrlBox.Text = ToolSettingsStore.LoginUrl(_toolId);

        PythonHint.Text = string.IsNullOrWhiteSpace(s.PythonExePath)
            ? "Empty = 'python' / 'py -3' on PATH."
            : $"Will use: {s.PythonExePath}";

        DelaySecondsBox.Text = Math.Clamp(s.DelaySeconds, 0, 120).ToString(CultureInfo.InvariantCulture);

        // Keep the per-tool HEADLESS switch in step with what is on disk.
        SyncHeadlessToggle();
    }

    private void OnSaveSettings(object sender, RoutedEventArgs e)
    {
        var s = ToolSettingsStore.For(_toolId);
        s.PythonExePath        = PythonExeBox.Text.Trim();
        s.CreateScriptPath     = CreateScriptBox.Text.Trim();
        s.LoginScriptPath      = LoginScriptBox.Text.Trim();
        s.ToolWorkingDirectory = WorkDirBox.Text.Trim();
        s.CreateArgTemplate    = CreateArgsBox.Text.Trim();
        s.LoginArgTemplate     = LoginArgsBox.Text.Trim();
        if (int.TryParse(DelaySecondsBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var delay))
            s.DelaySeconds = Math.Clamp(delay, 0, 120);
        // Saving the paths must not silently drop the HEADLESS choice.
        s.Headless             = HeadlessToggle?.IsChecked == true;

        ToolSettingsStore.Save(_toolId, s);

        // Persist the per-tool Login URL (normalised; empty → tool default).
        if (LoginUrlBox is not null)
            ToolSettingsStore.SetLoginUrl(_toolId, LoginUrlBox.Text);
        var effectiveUrl = ToolSettingsStore.LoginUrl(_toolId);
        if (LoginUrlBox is not null) LoginUrlBox.Text = effectiveUrl;

        Log.Success(_toolId, "Tool settings saved.");
        AppendLog($"💾 Settings saved → tool-settings.json  (login URL: {effectiveUrl})");
        RefreshStats();
    }

    private void OnReloadSettings(object sender, RoutedEventArgs e)
    {
        LoadSettings();
        AppendLog("🔄 Settings reloaded from disk.");
    }

    private void OnBrowsePython(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select python.exe",
            Filter = "python.exe|python.exe;python3.exe|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog() == true) PythonExeBox.Text = dlg.FileName;
    }

    private void OnBrowseCreateScript(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select create account script (.py)",
            Filter = "Python files (*.py)|*.py|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog() == true) CreateScriptBox.Text = dlg.FileName;
    }

    private void OnBrowseLoginScript(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select login / cookie-replay script (.py)",
            Filter = "Python files (*.py)|*.py|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog() == true) LoginScriptBox.Text = dlg.FileName;
    }

    private void OnBrowseWorkDir(object sender, RoutedEventArgs e)
    {
        // OpenFileDialog has no folder mode — use SaveFileDialog trick: pick any
        // file inside the folder, then take its directory.
        var dlg = new OpenFileDialog
        {
            Title = "Select tool working folder (pick any file inside it)",
            Filter = "All files (*.*)|*.*"
        };
        if (dlg.ShowDialog() == true)
            WorkDirBox.Text = System.IO.Path.GetDirectoryName(dlg.FileName) ?? "";
    }

    private void OnDetectPython(object sender, RoutedEventArgs e)
    {
        AppendLog("🔍 Probing PATH for python / py...");
        foreach (var candidate in new[] { "python", "python3", "py" })
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = candidate,
                    Arguments = "--version",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var p = System.Diagnostics.Process.Start(psi);
                if (p is null) continue;
                if (!p.WaitForExit(3000)) { p.Kill(); continue; }
                if (p.ExitCode != 0) continue;
                AppendLog($"  ✓ {candidate} → exit 0 (found on PATH)");
                PythonExeBox.Text = candidate;
                return;
            }
            catch (Exception ex)
            {
                AppendLog($"  ✗ {candidate} → {ex.GetType().Name}: {ex.Message}");
            }
        }
        AppendLog("⚠ No python interpreter found on PATH. Set PythonExePath manually.");
    }

    // ----------------------------------------------------------------- lifecycle

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_running)
        {
            _cts?.Cancel();
            Log.Warn(_toolId, "Panel closed while a run was active - the run was cancelled.");
        }
    }

    private static string Sanitise(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        return sb.ToString();
    }
}

/// <summary>
/// One row in the Settings-tab MAIL list (Feature 1): a Global-Settings Gmail
/// plus whether it is banned for the tool whose panel is open. Rebuilt on every
/// change, so plain read-only properties are enough (no INotifyPropertyChanged).
/// </summary>
public sealed class MailBanRow
{
    public string Email { get; init; } = "";
    public bool IsBanned { get; init; }

    public string StatusLabel => IsBanned ? "BANNED" : "ACTIVE";

    public Brush StatusColor => IsBanned
        ? new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71))   // red  - banned
        : new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99));  // green - active

    public string ActionLabel => IsBanned ? "➕ Add back" : "🚫 Ban for this tool";
}
