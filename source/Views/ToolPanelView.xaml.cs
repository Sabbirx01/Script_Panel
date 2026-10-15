using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AISuitePanel.Automation;
using AISuitePanel.Models;
using Microsoft.Win32;
using Microsoft.Playwright;

namespace AISuitePanel.Views;

/// <summary>
/// Screen 4 - one tool's panel: create accounts, log in with stored cookies,
/// manage accounts and cookies, and tune this tool's run settings.
/// </summary>
public partial class ToolPanelView : UserControl
{
    private readonly ToolInfo _tool;
    private readonly ObservableCollection<AccountRecord> _accounts;
    private readonly ObservableCollection<LogEntry> _log = new();

    private CancellationTokenSource? _cts;
    private bool _running;

    /// <summary>Once the user types a login quantity, the auto default stops overriding it.</summary>
    private bool _loginQtyEdited;
    private bool _settingLoginQty;

    private string _viewerText = "";
    private string _viewerName = "";

    public ToolPanelView(ToolInfo tool)
    {
        _tool = tool;
        InitializeComponent();

        _accounts = AccountStore.For(tool.Id);

        TitleText.Text = $"DASHBOARD > {tool.PanelTitle}";
        SubTitle.Text = $"{tool.Type} - {tool.Tagline}";
        OpenOfficialButton.Visibility = string.IsNullOrWhiteSpace(tool.OfficialUrl)
            ? Visibility.Collapsed : Visibility.Visible;
        WorkspaceHint.Text = "Isolated workspace: " + tool.WorkspacePath;

        AccountList.ItemsSource = _accounts;
        LogList.ItemsSource = _log;

        LoginQtyBox.TextChanged += (_, _) =>
        {
            if (!_settingLoginQty) _loginQtyEdited = true;
        };

        LoadToolSettings();
        SetDefaultSource();
        RefreshAvailability();
        UpdateCounters();
        RefreshEmptyState();

        _accounts.CollectionChanged += (_, _) =>
        {
            RefreshEmptyState();
            UpdateCounters();
        };

        // Both are raised from worker threads during a run.
        CookieVault.Changed += OnVaultChanged;
        Log.Appended += OnLogAppended;
        Unloaded += OnUnloaded;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        CookieVault.Changed -= OnVaultChanged;
        Log.Appended -= OnLogAppended;

        if (!_running) return;

        _cts?.Cancel();
        Log.Warn(_tool.Id, "Panel closed while a run was active - the run was cancelled.");
    }

    // ------------------------------------------------------------- event glue

    private void OnVaultChanged(string toolId)
    {
        if (!string.Equals(toolId, _tool.Id, StringComparison.OrdinalIgnoreCase)) return;

        Dispatcher.Invoke((Action)UpdateCounters);
    }

    private void OnLogAppended(LogEntry entry)
    {
        if (!string.IsNullOrEmpty(entry.ToolId) &&
            !string.Equals(entry.ToolId, _tool.Id, StringComparison.OrdinalIgnoreCase)) return;

        Dispatcher.Invoke((Action)(() =>
        {
            _log.Insert(0, entry);                                   // newest first
            while (_log.Count > 200) _log.RemoveAt(_log.Count - 1);
        }));
    }

    private async void OnOpenOfficialSite(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_tool.OfficialUrl)) return;
        await ManualBrowserService.OpenAsync(_tool.Id, _tool.OfficialUrl);
    }

    private void OnOpenWorkspace(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_tool.WorkspacePath);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _tool.WorkspacePath,
                UseShellExecute = true
            });
            Log.Info(_tool.Id, "Opened isolated tool workspace.");
        }
        catch (Exception ex)
        {
            Log.Error(_tool.Id, "Could not open workspace - " + ex.Message);
        }
    }

    // ----------------------------------------------------------------- section 1

    private void SetDefaultSource()
    {
        if (SettingsStore.Usable(MailSource.DottedGmail)) SrcDotted.IsChecked = true;
        else if (SettingsStore.Usable(MailSource.TempMail)) SrcTemp.IsChecked = true;
        else if (SettingsStore.Usable(MailSource.CustomDomain)) SrcDomain.IsChecked = true;
        else if (SettingsStore.Usable(MailSource.NoProxy)) SrcNoProxy.IsChecked = true;
        else if (SettingsStore.Usable(MailSource.SemiAuto)) SrcSemi.IsChecked = true;
        else if (SettingsStore.Usable(MailSource.CustomMail)) SrcCustomMail.IsChecked = true;
    }

    private MailSource SelectedSource() =>
        SrcTemp.IsChecked == true ? MailSource.TempMail
        : SrcDomain.IsChecked == true ? MailSource.CustomDomain
        : SrcCustomMail.IsChecked == true ? MailSource.CustomMail
        : SrcSemi.IsChecked == true ? MailSource.SemiAuto
        : SrcNoProxy.IsChecked == true ? MailSource.NoProxy
        : MailSource.DottedGmail;

    /// <summary>Greys out any method that is not configured in Global Settings.</summary>
    private void RefreshAvailability()
    {
        SrcTemp.IsEnabled      = SettingsStore.Usable(MailSource.TempMail);
        SrcDotted.IsEnabled    = SettingsStore.Usable(MailSource.DottedGmail);
        SrcDomain.IsEnabled    = SettingsStore.Usable(MailSource.CustomDomain);
        SrcCustomMail.IsEnabled = SettingsStore.Usable(MailSource.CustomMail);
        SrcSemi.IsEnabled      = SettingsStore.Usable(MailSource.SemiAuto);
        SrcNoProxy.IsEnabled   = SettingsStore.Usable(MailSource.NoProxy);

        // A disabled source must never stay selected.
        if (SrcTemp.IsChecked == true && !SrcTemp.IsEnabled) SrcTemp.IsChecked = false;
        if (SrcDotted.IsChecked == true && !SrcDotted.IsEnabled) SrcDotted.IsChecked = false;
        if (SrcDomain.IsChecked == true && !SrcDomain.IsEnabled) SrcDomain.IsChecked = false;
        if (SrcCustomMail.IsChecked == true && !SrcCustomMail.IsEnabled) SrcCustomMail.IsChecked = false;
        if (SrcSemi.IsChecked == true && !SrcSemi.IsEnabled) SrcSemi.IsChecked = false;
        if (SrcNoProxy.IsChecked == true && !SrcNoProxy.IsEnabled) SrcNoProxy.IsChecked = false;

        var blocked = new List<string>();

        if (!SrcTemp.IsEnabled)      blocked.Add("Temp Mail - "      + SettingsStore.WhyNot(MailSource.TempMail));
        if (!SrcDotted.IsEnabled)    blocked.Add("Dotted Gmail - "   + SettingsStore.WhyNot(MailSource.DottedGmail));
        if (!SrcDomain.IsEnabled)    blocked.Add("Custom Domain - "  + SettingsStore.WhyNot(MailSource.CustomDomain));
        if (!SrcCustomMail.IsEnabled) blocked.Add("Custom Mail - "   + SettingsStore.WhyNot(MailSource.CustomMail));
        if (!SrcSemi.IsEnabled)      blocked.Add("Semi Automation - " + SettingsStore.WhyNot(MailSource.SemiAuto));
        if (!SrcNoProxy.IsEnabled)   blocked.Add("No Proxy - "        + SettingsStore.WhyNot(MailSource.NoProxy));

        SrcHint.Text = blocked.Count switch
        {
            0 => "All six methods are configured.",
            6 => "No mail method is configured yet - open \u2699 SETTING on the dashboard first.",
            _ => "Not available:  " + string.Join("   |   ", blocked)
        };

        StartButton.IsEnabled = !_running && AnySourceAvailable();
    }

    private bool AnySourceAvailable() =>
        SrcTemp.IsEnabled || SrcDotted.IsEnabled || SrcDomain.IsEnabled ||
        SrcCustomMail.IsEnabled || SrcSemi.IsEnabled || SrcNoProxy.IsEnabled;

    private async void OnStartCreation(object sender, RoutedEventArgs e)
    {
        if (_running) return;

        var source = SelectedSource();

        if (!SettingsStore.Usable(source))
        {
            Log.Warn(_tool.Id,
                $"{CreateAccountRunner.SourceLabel(source)} is not configured - open the dashboard and press SETTING.");
            return;
        }

        if (!int.TryParse(QtyBox.Text.Trim(), out var qty) || qty < 1 || qty > 100)
        {
            Log.Warn(_tool.Id, "Account quantity must be between 1 and 100.");
            return;
        }

        var settings = CurrentToolSettings();
        ToolSettingsStore.Save(_tool.Id, settings);

        ResultText.Visibility = Visibility.Collapsed;
        ProgressText.Text = $"00 / {qty:00}";
        Progress.Value = 0;
        SetRunState(running: true, login: false);

        _cts = new CancellationTokenSource();

        try
        {
            var summary = await CreateAccountRunner.RunAsync(
                _tool, source, qty, settings, Dispatcher,
                (done, total) =>
                {
                    ProgressText.Text = $"{done:00} / {total:00}";
                    Progress.Value = total == 0 ? 0 : done * 100.0 / total;
                },
                _cts.Token);

            ResultText.Text = $"Batch finished - {summary.Succeeded} created, {summary.Failed} failed.";
            ResultText.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            Log.Error(_tool.Id, "Create run stopped - " + ex.Message);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            SetRunState(running: false, login: false);
        }
    }

    // ----------------------------------------------------------------- section 2

    private async void OnLoginCookies(object sender, RoutedEventArgs e)
    {
        if (_running) return;

        var rows = _accounts.ToList();
        var selected = rows.Where(r => r.Selected).ToList();

        List<AccountRecord> targets;

        if (selected.Count > 0)
        {
            targets = selected
                .Where(r => r.HasCookie)
                .OrderBy(r => r.LastLoginAt ?? DateTime.MinValue)
                .ToList();

            if (targets.Count == 0)
            {
                Log.Warn(_tool.Id, "The selected row(s) have no stored cookie.");
                return;
            }
        }
        else
        {
            // Least recently used first, so every cookie gets its turn.
            var pool = rows
                .Where(r => r.HasCookie)
                .OrderBy(r => r.LastLoginAt ?? DateTime.MinValue)
                .ToList();

            if (pool.Count == 0)
            {
                Log.Warn(_tool.Id, "No cookies available - create an account first.");
                return;
            }

            if (!int.TryParse(LoginQtyBox.Text.Trim(), out var qty) || qty < 1) qty = 1;

            if (qty > pool.Count)
            {
                Log.Warn(_tool.Id, $"Only {pool.Count} cookie(s) are available - logging in all of them.");
                qty = pool.Count;
            }

            targets = pool.Take(qty).ToList();
        }

        var settings = CurrentToolSettings();

        LoginResultText.Visibility = Visibility.Collapsed;
        LoginProgressText.Text = $"00 / {targets.Count:00}";
        LoginProgress.Value = 0;
        SetRunState(running: true, login: true);

        _cts = new CancellationTokenSource();

        try
        {
            var summary = await CookieLoginRunner.RunAsync(
                _tool, targets, settings, Dispatcher,
                (done, total) =>
                {
                    LoginProgressText.Text = $"{done:00} / {total:00}";
                    LoginProgress.Value = total == 0 ? 0 : done * 100.0 / total;
                },
                _cts.Token);

            LoginResultText.Text =
                $"Login finished - {summary.Succeeded} session(s) restored, {summary.Failed} failed.";
            LoginResultText.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            Log.Error(_tool.Id, "Login run stopped - " + ex.Message);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            SetRunState(running: false, login: true);
        }
    }

    // ------------------------------------------------------------------- runs

    private void OnStop(object sender, RoutedEventArgs e)
    {
        if (_cts is null) return;

        _cts.Cancel();
        Log.Warn(_tool.Id, "Stop requested - finishing the step that is already running.");
    }

    private void SetRunState(bool running, bool login)
    {
        _running = running;

        ProgressPanel.Visibility = running && !login ? Visibility.Visible : Visibility.Collapsed;
        LoginProgressPanel.Visibility = running && login ? Visibility.Visible : Visibility.Collapsed;

        QtyBox.IsEnabled = !running;
        DelayBox.IsEnabled = !running;
        ThreadsBox.IsEnabled = !running;

        if (running)
        {
            SrcTemp.IsEnabled = false;
            SrcDotted.IsEnabled = false;
            SrcDomain.IsEnabled = false;
            SrcCustomMail.IsEnabled = false;
            SrcSemi.IsEnabled = false;
            SrcNoProxy.IsEnabled = false;

            StartButton.IsEnabled = false;
            LoginButton.IsEnabled = false;
            LoginQtyBox.IsEnabled = false;
            return;
        }

        RefreshAvailability();
        UpdateCounters();
    }

    // ----------------------------------------------------------------- section 3

    private void RefreshEmptyState() =>
        AccountEmpty.Visibility = _accounts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void UpdateCounters()
    {
        var available = CookieVault.CountFor(_tool.Id);
        StatTotal.Text = _accounts.Count.ToString();
        StatActiveToday.Text = _accounts.Count(a => a.Status == AccountStatus.Active && a.CreatedAt.Date == DateTime.Today).ToString();
        StatLastRun.Text = _accounts.Count == 0
            ? "—"
            : _accounts.Max(a => a.CreatedAt).ToString("HH:mm:ss  ·  yyyy-MM-dd");
        StatCookies.Text = available.ToString();

        TotalCookiesText.Text = $"Total Available Cookies: {available}";

        LoginButton.IsEnabled = !_running && available > 0;
        LoginQtyBox.IsEnabled = !_running && available > 0;

        _settingLoginQty = true;

        try
        {
            // Until the user types something, the box tracks the vault.
            if (!_loginQtyEdited)
            {
                LoginQtyBox.Text = Math.Min(5, available).ToString();
            }
            else if (available == 0)
            {
                LoginQtyBox.Text = "0";
            }
            else if (!int.TryParse(LoginQtyBox.Text.Trim(), out var qty) || qty < 1)
            {
                LoginQtyBox.Text = Math.Min(5, available).ToString();
            }
            else if (qty > available)
            {
                LoginQtyBox.Text = available.ToString();
            }
        }
        finally
        {
            _settingLoginQty = false;
        }
    }

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        var on = SelectAllCheck.IsChecked == true;
        foreach (var row in _accounts) row.Selected = on;
    }

    private void OnViewCookie(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: AccountRecord row }) return;

        var record = CookieVault.Get(_tool.Id, row.Id);
        if (record is null || record.Cookies.Count == 0)
        {
            Log.Warn(_tool.Id, $"#{row.IdLabel} has no stored cookie.");
            return;
        }

        _viewerName = $"{_tool.Id}-{row.IdLabel}-cookies.json";
        _viewerText = System.Text.Json.JsonSerializer.Serialize(record, Json.Options);

        ViewerTitle.Text = $"COOKIE - {row.EmailUsed}   (#{row.IdLabel})";
        ViewerBody.Text = _viewerText;
        ViewerOverlay.Visibility = Visibility.Visible;
    }

    private void OnExportCookie(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: AccountRecord row }) return;

        var record = CookieVault.Get(_tool.Id, row.Id);
        if (record is null || record.Cookies.Count == 0)
        {
            Log.Warn(_tool.Id, $"#{row.IdLabel} has no stored cookie.");
            return;
        }

        var dialog = new SaveFileDialog
        {
            FileName = $"{_tool.Id}-{row.IdLabel}-cookies.json",
            Filter = "JSON file (*.json)|*.json|All files (*.*)|*.*",
            Title = "Export cookie"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            var text = System.Text.Json.JsonSerializer.Serialize(record, Json.Options);
            File.WriteAllText(dialog.FileName, text, new UTF8Encoding(false));
            Log.Success(_tool.Id, $"#{row.IdLabel} cookie exported to {dialog.FileName}");
        }
        catch (Exception ex)
        {
            Log.Error(_tool.Id, "Export failed - " + ex.Message);
        }
    }

    private void OnDeleteRow(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: AccountRecord row }) return;

        var answer = MessageBox.Show(
            $"Delete account #{row.IdLabel} ({row.EmailUsed}) and its stored cookie?",
            _tool.Name, MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (answer != MessageBoxResult.OK) return;

        AccountStore.Delete(_tool.Id, row);
        Log.Info(_tool.Id, $"#{row.IdLabel} deleted.");
    }

    private void OnExportAll(object sender, RoutedEventArgs e)
    {
        var count = CookieVault.CountFor(_tool.Id);

        if (count == 0)
        {
            Log.Warn(_tool.Id, "Nothing to export - the cookie vault is empty.");
            return;
        }

        ExportSummary.Text = $"{count} cookie set(s) for {_tool.Name} will be written to one file.";
        ExportOverlay.Visibility = Visibility.Visible;
    }

    private void OnExportAllJson(object sender, RoutedEventArgs e) => SaveExport(json: true);

    private void OnExportAllTxt(object sender, RoutedEventArgs e) => SaveExport(json: false);

    private void SaveExport(bool json)
    {
        var format = json ? "json" : "txt";

        var dialog = new SaveFileDialog
        {
            FileName = CookieVault.SuggestedExportName(_tool.Id, format),
            Filter = json
                ? "JSON file (*.json)|*.json|All files (*.*)|*.*"
                : "Text file (*.txt)|*.txt|All files (*.*)|*.*",
            Title = "Export all cookies"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            var text = json ? CookieVault.ExportJson(_tool.Id) : CookieVault.ExportTxt(_tool.Id);

            // A BOM helps Excel/openers read the .txt columns; JSON stays clean.
            File.WriteAllText(dialog.FileName, text, new UTF8Encoding(!json));

            Log.Success(_tool.Id, $"All cookies exported to {dialog.FileName}");
            HideOverlays();
        }
        catch (Exception ex)
        {
            Log.Error(_tool.Id, "Export failed - " + ex.Message);
        }
    }

    private void OnDeleteAll(object sender, RoutedEventArgs e)
    {
        DeleteSummary.Text =
            $"{_accounts.Count} account(s) and {CookieVault.CountFor(_tool.Id)} cookie set(s) for " +
            $"{_tool.Name} will be removed.";

        DeleteConfirmBox.Text = "";
        DeleteOverlay.Visibility = Visibility.Visible;
    }

    private void OnDeleteConfirmChanged(object sender, TextChangedEventArgs e) =>
        DeleteConfirmButton.IsEnabled =
            string.Equals(DeleteConfirmBox.Text.Trim(), "DELETE", StringComparison.Ordinal);

    private void OnConfirmDeleteAll(object sender, RoutedEventArgs e)
    {
        AccountStore.DeleteAll(_tool.Id);
        Log.Warn(_tool.Id, "All accounts and cookies deleted.");
        HideOverlays();
    }

    // ----------------------------------------------------------------- section 4

    private ToolSettings CurrentToolSettings()
    {
        var delay = int.TryParse(DelayBox.Text.Trim(), out var d) ? Math.Clamp(d, 0, 120) : 3;
        var threads = int.TryParse(ThreadsBox.Text.Trim(), out var t) ? Math.Clamp(t, 1, 10) : 2;

        return new ToolSettings { DelaySeconds = delay, Threads = threads };
    }

    private void LoadToolSettings()
    {
        var settings = ToolSettingsStore.For(_tool.Id);

        DelayBox.Text = settings.DelaySeconds.ToString();
        ThreadsBox.Text = settings.Threads.ToString();
        ThreadsHint.Text = settings.Threads > 4
            ? "High thread count can trigger rate limits."
            : "1-10 workers";
    }

    private void OnSaveToolSettings(object sender, RoutedEventArgs e)
    {
        var settings = CurrentToolSettings();
        ToolSettingsStore.Save(_tool.Id, settings);

        DelayBox.Text = settings.DelaySeconds.ToString();
        ThreadsBox.Text = settings.Threads.ToString();
        ThreadsHint.Text = settings.Threads > 4
            ? "High thread count can trigger rate limits."
            : "1-10 workers";

        Log.Success(_tool.Id,
            $"Tool settings saved - delay {settings.DelaySeconds}s, {settings.Threads} thread(s).");
    }

    // -------------------------------------------------------------------- log

    private void OnClearLog(object sender, RoutedEventArgs e) => _log.Clear();

    private void OnCopyLog(object sender, RoutedEventArgs e)
    {
        var text = Log.Text(_tool.Id);
        if (string.IsNullOrWhiteSpace(text)) return;

        try
        {
            Clipboard.SetText(text);
            Log.Info(_tool.Id, "Activity log copied to the clipboard.");
        }
        catch (Exception ex)
        {
            Log.Error(_tool.Id, "Could not copy the log - " + ex.Message);
        }
    }

    // --------------------------------------------------------------- overlays

    private void OnCopyViewer(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_viewerText)) return;

        try
        {
            Clipboard.SetText(_viewerText);
            Log.Info(_tool.Id, "Cookie copied to the clipboard.");
        }
        catch (Exception ex)
        {
            Log.Error(_tool.Id, "Could not copy the cookie - " + ex.Message);
        }
    }

    private void OnSaveViewer(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_viewerText)) return;

        var dialog = new SaveFileDialog
        {
            FileName = _viewerName,
            Filter = "JSON file (*.json)|*.json|All files (*.*)|*.*",
            Title = "Save cookie"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            File.WriteAllText(dialog.FileName, _viewerText, new UTF8Encoding(false));
            Log.Success(_tool.Id, $"Cookie saved to {dialog.FileName}");
        }
        catch (Exception ex)
        {
            Log.Error(_tool.Id, "Save failed - " + ex.Message);
        }
    }

    private void OnCloseOverlays(object sender, RoutedEventArgs e) => HideOverlays();

    private void HideOverlays()
    {
        ViewerOverlay.Visibility = Visibility.Collapsed;
        ExportOverlay.Visibility = Visibility.Collapsed;
        DeleteOverlay.Visibility = Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ input

    private void OnDigitsOnly(object sender, TextCompositionEventArgs e) =>
        e.Handled = !e.Text.All(char.IsDigit);

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (_running)
        {
            var answer = MessageBox.Show(
                "A run is still active.\n\nStop it and go back to the dashboard?",
                _tool.Name, MessageBoxButton.OKCancel, MessageBoxImage.Warning);

            if (answer != MessageBoxResult.OK) return;

            _cts?.Cancel();
        }

        Nav.GoDashboard();
    }
}
