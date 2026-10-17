using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AISuitePanel.Models;

namespace AISuitePanel.Views;

/// <summary>
/// Screen 3 - the three mail methods every tool panel draws from.
/// </summary>
public partial class GlobalSettingsView : UserControl
{
    /// <summary>One editable "gmail : app password" row.</summary>
    public sealed class GmailRow : ObservableObject
    {
        private string _email = "";
        private string _appPassword = "";
        private bool _reveal;

        public string Email
        {
            get => _email;
            set => Set(ref _email, value);
        }

        public string AppPassword
        {
            get => _appPassword;
            set => Set(ref _appPassword, value);
        }

        /// <summary>False keeps the app password masked behind the bullet overlay.</summary>
        public bool Reveal
        {
            get => _reveal;
            set => Set(ref _reveal, value);
        }
    }

    private readonly ObservableCollection<GmailRow> _rows = new();
    private readonly DispatcherTimer _toastTimer;

    private bool _dirty;
    private bool _targetRevealed;
    private bool _customMailRevealed;
    private bool _accountPassRevealed;

    public GlobalSettingsView()
    {
        InitializeComponent();

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.6) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            Toast.Visibility = Visibility.Collapsed;
        };

        SubLine.Text = "One place for every address the tool panels are allowed to use. " +
                       "Nothing is saved until you press Save.";

        LoadFromSettings();
        AttachDirtyTracking();
    }

    // ------------------------------------------------------------------- load

    private void LoadFromSettings()
    {
        var s = SettingsStore.Current;

        ServiceCombo.ItemsSource = MailRouter.ServiceNames;
        ServiceCombo.SelectedIndex = Math.Clamp(s.TempMail.ServiceIndex - 1, 0, MailRouter.ServiceNames.Length - 1);
        ApiKeyBox.Text = s.TempMail.CustomApiKey;

        _rows.Clear();
        foreach (var g in s.DottedGmail.Accounts)
            _rows.Add(NewRow(g.Email, g.AppPassword));
        GmailList.ItemsSource = _rows;

        AntiDuplicateCheck.IsChecked = s.DottedGmail.AntiDuplicate;
        RoundRobinRadio.IsChecked = true;

        DomainBox.Text = s.CustomDomain.Domain;
        ForwardBox.Text = s.CustomDomain.ForwardTarget;
        TargetPassBox.Text = s.CustomDomain.TargetAppPassword;

        // Updates
        UpdateUrlBox.Text = s.Update.BaseUrl;

        // Method 4 - Custom Mail
        CustomMailEmailBox.Text = s.CustomMail.Email;
        CustomMailPassBox.Text = s.CustomMail.AppPassword;
        CustomMailClearCheck.IsChecked = s.CustomMail.ClearAfterUse;

        // Account password (shared by every tool)
        AccountPassBox.Text = s.AccountPassword.Value;

        ApplyTargetMask();
        ApplyCustomMailMask();
        ApplyAccountPassMask();
        LoadProxyPool();
        RefreshHints();

        // Tell the operator straight away whether the server has something newer,
        // rather than making them press CHECK NOW to find out. Silent unless there
        // is news, and it can never block the window opening.
        if (UpdateService.Configured) OnCheckForUpdates(this, new RoutedEventArgs());

        _dirty = false;
    }

    private GmailRow NewRow(string email = "", string appPassword = "")
    {
        var row = new GmailRow { Email = email, AppPassword = appPassword };
        row.PropertyChanged += (_, _) => _dirty = true;
        return row;
    }

    private void AttachDirtyTracking()
    {
        ApiKeyBox.TextChanged += (_, _) => _dirty = true;
        DomainBox.TextChanged += (_, _) => _dirty = true;
        ForwardBox.TextChanged += (_, _) => _dirty = true;
        TargetPassBox.TextChanged += (_, _) => _dirty = true;
        AntiDuplicateCheck.Checked += (_, _) => _dirty = true;
        AntiDuplicateCheck.Unchecked += (_, _) => _dirty = true;

        CustomMailEmailBox.TextChanged += (_, _) => _dirty = true;
        CustomMailPassBox.TextChanged += (_, _) => _dirty = true;
        CustomMailClearCheck.Checked += (_, _) => _dirty = true;
        CustomMailClearCheck.Unchecked += (_, _) => _dirty = true;

        AccountPassBox.TextChanged += (_, _) => _dirty = true;
    }

    private void RefreshHints()
    {
        GmailEmpty.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var perTool = ToolCatalog.Tools.Select(t => $"{t.Name}: {AliasGuard.Count(t.Id)}");
        AliasUsage.Text = "Dotted aliases blocked so far — " + string.Join("  ·  ", perTool);
    }

    // --------------------------------------------------------------- handlers

    private void OnAddGmail(object sender, RoutedEventArgs e)
    {
        _rows.Add(NewRow());
        _dirty = true;
        RefreshHints();
    }

    private void OnRemoveGmail(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: GmailRow row }) return;

        _rows.Remove(row);
        _dirty = true;
        RefreshHints();
    }

    private void OnToggleRowReveal(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: GmailRow row }) return;

        var wasDirty = _dirty;
        row.Reveal = !row.Reveal;
        _dirty = wasDirty;   // showing a password is not an edit
    }

    private void OnToggleTargetPass(object sender, RoutedEventArgs e)
    {
        var wasDirty = _dirty;
        _targetRevealed = !_targetRevealed;
        ApplyTargetMask();
        _dirty = wasDirty;
    }

    private void ApplyTargetMask()
    {
        TargetPassMask.Visibility = _targetRevealed ? Visibility.Collapsed : Visibility.Visible;
        TargetPassBox.IsHitTestVisible = _targetRevealed;
        TargetPassEye.Content = _targetRevealed ? "🙈" : "👁";
    }

    private void OnToggleCustomMailPass(object sender, RoutedEventArgs e)
    {
        var wasDirty = _dirty;
        _customMailRevealed = !_customMailRevealed;
        ApplyCustomMailMask();
        _dirty = wasDirty;
    }

    private void ApplyCustomMailMask()
    {
        CustomMailPassMask.Visibility = _customMailRevealed ? Visibility.Collapsed : Visibility.Visible;
        CustomMailPassBox.IsHitTestVisible = _customMailRevealed;
        CustomMailPassEye.Content = _customMailRevealed ? "🙈" : "👁";
    }

    private void OnToggleAccountPass(object sender, RoutedEventArgs e)
    {
        var wasDirty = _dirty;
        _accountPassRevealed = !_accountPassRevealed;
        ApplyAccountPassMask();
        _dirty = wasDirty;
    }

    private void ApplyAccountPassMask()
    {
        // The overlay doubles as the hint for the empty case ("random per
        // account"), so it stays in place until the user reveals the box.
        AccountPassMask.Visibility = _accountPassRevealed ? Visibility.Collapsed : Visibility.Visible;
        AccountPassBox.IsHitTestVisible = _accountPassRevealed;
        AccountPassEye.Content = _accountPassRevealed ? "🙈" : "👁";
    }

    // ----------------------------------------------------------- mailbox test

    private CancellationTokenSource? _probeCts;

    /// <summary>
    /// Logs in to the forward target with the app password that is on screen and
    /// reports what the panel can actually read.
    ///
    /// This exists because a rejected app password is indistinguishable from
    /// "no code has arrived yet": Gmail shows the password as four groups of four
    /// letters, IMAP wants the 16 bare characters, and when the login failed the
    /// tool simply sat there polling an inbox it was locked out of until the run
    /// timed out.  Testing first turns that into one clear sentence.
    /// </summary>
    private async void OnTestDomainInbox(object sender, RoutedEventArgs e)
    {
        var address = ForwardBox.Text.Trim();
        var password = TargetPassBox.Text;

        _probeCts?.Cancel();
        _probeCts = new CancellationTokenSource();
        var ct = _probeCts.Token;

        TestDomainInboxButton.IsEnabled = false;
        var muted = TryFindResource("B.TextMuted") as Brush;
        if (muted is not null) DomainInboxStatus.Foreground = muted;
        DomainInboxStatus.Text = address.Length == 0
            ? "Fill in the forward target first."
            : $"Testing {MailboxProbe.ImapHostFor(address)} ...";

        try
        {
            var result = await MailboxProbe.TestAsync(address, password, ct);
            if (ct.IsCancellationRequested) return;

            var colour = TryFindResource(result.Ok ? "B.Ok" : "B.Err") as Brush;
            if (colour is not null) DomainInboxStatus.Foreground = colour;
            DomainInboxStatus.Text = result.Ok && result.Newest.Length > 0
                ? result.Summary + "\nNewest: " + result.Newest
                : result.Summary;

            Log.Info("", "Mailbox test (Method 3) "
                         + (result.Ok ? "OK - " : "FAILED - ") + result.Summary);
        }
        catch (Exception ex)
        {
            var colour = TryFindResource("B.Err") as Brush;
            if (colour is not null) DomainInboxStatus.Foreground = colour;
            DomainInboxStatus.Text = ex.Message;
        }
        finally
        {
            TestDomainInboxButton.IsEnabled = true;
        }
    }

    // ----------------------------------------------------------------- updates

    private IReadOnlyList<UpdateService.ManifestFile> _pendingUpdate =
        Array.Empty<UpdateService.ManifestFile>();
    private string _pendingVersion = "";

    private void SetUpdateStatus(string text, bool? ok)
    {
        UpdateStatus.Text = text;
        var brush = TryFindResource(ok == true ? "B.Ok" : ok == false ? "B.Err" : "B.TextMuted") as Brush;
        if (brush is not null) UpdateStatus.Foreground = brush;
    }

    /// <summary>
    /// Asks the configured server what has changed. Nothing is installed here —
    /// the operator sees the list and size first, then decides.
    /// </summary>
    private async void OnCheckForUpdates(object sender, RoutedEventArgs e)
    {
        UpdateCheckButton.IsEnabled = false;
        UpdateInstallButton.IsEnabled = false;
        UpdateNoteBox.Visibility = Visibility.Collapsed;
        UpdateDetail.Text = "";

        // The box is the source of truth, so a URL typed but not yet Saved still
        // works for a check - saving first would be a surprise extra step.
        var typed = UpdateUrlBox.Text.Trim();
        var saved = SettingsStore.Current.Update.BaseUrl;
        try
        {
            if (!string.Equals(typed, saved, StringComparison.Ordinal))
            {
                var current = SettingsStore.Current;
                current.Update = new UpdateSettings { BaseUrl = typed };
                SettingsStore.Save(current);
            }

            SetUpdateStatus("Checking " + (typed.Length == 0 ? "(no server set)" : typed) + " ...", null);
            var result = await UpdateService.CheckAsync();

            if (!result.Ok)
            {
                SetUpdateStatus("Check failed: " + result.Error, false);
                return;
            }

            _pendingUpdate = result.Changed;
            _pendingVersion = result.AvailableVersion;
            UpdateInstallButton.IsEnabled = result.Changed.Count > 0;

            if (result.UpToDate)
            {
                SetUpdateStatus($"Up to date — version {result.CurrentVersion}.", true);
                return;
            }

            var names = result.Changed.Select(f => Path.GetFileName(f.Path)).Distinct().Take(8);
            SetUpdateStatus(
                $"Update available: {result.AvailableVersion} "
                + $"({result.Changed.Count} file(s), {result.ChangedBytes / 1024.0 / 1024.0:F1} MB)",
                null);
            UpdateDetail.Text = string.Join(", ", names)
                                + (result.Changed.Count > 8 ? ", ..." : "");
            if (result.Notes.Length > 0)
            {
                UpdateNotes.Text = result.Notes;
                UpdateNoteBox.Visibility = Visibility.Visible;
            }
            Log.Info("", $"Panel update {result.AvailableVersion} available "
                         + $"({result.Changed.Count} file(s)).");
        }
        catch (Exception ex)
        {
            SetUpdateStatus(ex.Message, false);
        }
        finally
        {
            UpdateCheckButton.IsEnabled = true;
        }
    }

    private async void OnInstallUpdate(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate.Count == 0) return;

        UpdateCheckButton.IsEnabled = false;
        UpdateInstallButton.IsEnabled = false;
        var progress = new Progress<(int Done, int Total, string Name)>(p =>
            SetUpdateStatus($"Installing ... {p.Done}/{p.Total} — {p.Name}", null));

        try
        {
            var result = await UpdateService.InstallAsync(_pendingUpdate, _pendingVersion, progress);
            if (!result.Ok)
            {
                SetUpdateStatus(result.Error, false);
                return;
            }

            SetUpdateStatus($"Installed {result.FilesWritten} file(s).", true);
            Log.Success("", $"Panel update installed ({result.FilesWritten} file(s)).");

            if (result.ExeReplaced &&
                MessageBox.Show(
                    "The panel itself was updated. Restart now to load it?",
                    "Update installed", MessageBoxButton.YesNo, MessageBoxImage.Question)
                == MessageBoxResult.Yes)
            {
                if (UpdateService.Restart()) Application.Current.Shutdown();
                else SetUpdateStatus("Installed. Restart when convenient.", true);
            }
            else
            {
                SetUpdateStatus("Installed. Restart the panel to load the new code.", true);
            }
        }
        catch (Exception ex)
        {
            SetUpdateStatus(ex.Message, false);
        }
        finally
        {
            UpdateCheckButton.IsEnabled = true;
            UpdateInstallButton.IsEnabled = _pendingUpdate.Count > 0;
        }
    }

    /// <summary>Copies back whatever the last update replaced.</summary>
    private void OnRestorePrevious(object sender, RoutedEventArgs e)
    {
        var backup = UpdateService.LastBackup();
        if (backup is null)
        {
            SetUpdateStatus("There is no backup yet — nothing has been installed from a server.", false);
            return;
        }

        if (MessageBox.Show(
                $"Copy the files in\n{backup}\nback over the install? Restart the panel afterwards.",
                "Restore previous version", MessageBoxButton.YesNo, MessageBoxImage.Warning)
            != MessageBoxResult.Yes)
            return;

        var restored = UpdateService.RestoreLastBackup(out var error);
        SetUpdateStatus(restored > 0
            ? $"Restored {restored} file(s). Restart the panel to load them."
            : "Restore failed: " + error,
            restored > 0);
    }

    // -------------------------------------------------------------- proxy pool

    /// <summary>The box as a clean proxy list (normalised, blanks dropped).</summary>
    private List<string> SplitProxyBox() =>
        (ProxyPoolBox.Text ?? "")
            .Split('\n')
            .Select(ProxyPool.Normalize)
            .Where(line => line.Length > 0)
            .ToList();

    private void LoadProxyPool()
    {
        if (ProxyPoolBox is null) return;

        var list = ProxyPool.Load();
        ProxyPoolBox.Text = string.Join(Environment.NewLine, list);
        if (ProxyPoolPath is not null) ProxyPoolPath.Text = "file: " + ProxyPool.File;
        SetProxyStatus(list.Count == 0
            ? "No proxies yet — press GITHUB PROXIES for a fresh batch."
            : $"{list.Count} proxy(ies) loaded.", ok: null);
    }

    private void OnReloadProxyPool(object sender, RoutedEventArgs e) => LoadProxyPool();

    /// <summary>Pull a fresh batch of public proxies into the box.</summary>
    private async void OnFetchGitHubProxies(object sender, RoutedEventArgs e)
    {
        ProxyGitHubButton.IsEnabled = false;
        ProxyTestButton.IsEnabled = false;
        SetProxyStatus("Downloading the public GitHub lists ...", ok: null);
        try
        {
            var list = await ProxyPool.FetchFromGitHubAsync();
            if (list.Count == 0)
            {
                SetProxyStatus("Nothing came back — check the connection and try again.", ok: false);
                return;
            }

            ProxyPoolBox.Text = string.Join(Environment.NewLine, list);
            ProxyPool.Save(list);
            SetProxyStatus($"{list.Count} proxy(ies) downloaded. Now press "
                           + "TEST & REMOVE DEAD to keep only the live ones.", ok: true);
        }
        catch (Exception ex)
        {
            SetProxyStatus(ex.Message, ok: false);
        }
        finally
        {
            ProxyGitHubButton.IsEnabled = true;
            ProxyTestButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Test every proxy in the box, delete the ones that do not answer, and save
    /// the survivors to disk.  A free proxy that is dead today would otherwise
    /// make a run fail with ERR_PROXY_CONNECTION and look like a panel bug.
    /// </summary>
    private async void OnTestProxies(object sender, RoutedEventArgs e)
    {
        var all = SplitProxyBox();
        if (all.Count == 0)
        {
            SetProxyStatus("The box is empty.", ok: false);
            return;
        }

        ProxyGitHubButton.IsEnabled = false;
        ProxyTestButton.IsEnabled = false;

        // Thousands of entries at 6s each would take hours; the first batch is
        // plenty and the rest stay in the box for the next pass.
        var batch = all.Take(300).ToList();
        SetProxyStatus($"Testing {batch.Count} of {all.Count} ...", ok: null);

        try
        {
            var progress = new Progress<(int Done, int Total, int Alive)>(p =>
                SetProxyStatus($"Testing ... {p.Done}/{p.Total} done, {p.Alive} alive", ok: null));

            var alive = await ProxyPool.TestAsync(batch, progress);
            ProxyPoolBox.Text = string.Join(Environment.NewLine, alive);
            ProxyPool.Save(alive);

            SetProxyStatus(alive.Count == 0
                ? $"None of the {batch.Count} tested proxies answered — press GITHUB PROXIES for a fresh batch."
                : $"{alive.Count} of {batch.Count} alive and kept; the dead ones were removed.",
                ok: alive.Count > 0);
        }
        catch (Exception ex)
        {
            SetProxyStatus(ex.Message, ok: false);
        }
        finally
        {
            ProxyGitHubButton.IsEnabled = true;
            ProxyTestButton.IsEnabled = true;
        }
    }

    private void SetProxyStatus(string text, bool? ok)
    {
        if (ProxyPoolStatus is null) return;
        ProxyPoolStatus.Text = text;
        var brush = TryFindResource(ok == true ? "B.Ok" : ok == false ? "B.Err" : "B.TextMuted") as Brush;
        if (brush is not null) ProxyPoolStatus.Foreground = brush;
    }

    // ------------------------------------------------------------------- save

    private void OnSave(object sender, RoutedEventArgs e)
    {
        // The proxy list lives in its own plain-text file (the tools read it
        // directly), so it is written on Save as well as by TEST & REMOVE DEAD.
        ProxyPool.Save(SplitProxyBox());

        var settings = new GlobalSettings
        {
            TempMail = new TempMailSettings
            {
                ServiceIndex = ServiceCombo.SelectedIndex + 1,
                CustomApiKey = ApiKeyBox.Text.Trim()
            },
            DottedGmail = new DottedGmailSettings
            {
                Accounts = _rows
                    .Where(r => !string.IsNullOrWhiteSpace(r.Email) || !string.IsNullOrWhiteSpace(r.AppPassword))
                    .Select(r => new GmailEntry
                    {
                        Email = r.Email.Trim(),
                        AppPassword = r.AppPassword.Trim()
                    })
                    .ToList(),

                RoutingMode = "RoundRobin",
                AntiDuplicate = AntiDuplicateCheck.IsChecked == true,

                // Never reset the rotation cursor while saving.
                Cursor = SettingsStore.Current.DottedGmail.Cursor
            },
            CustomDomain = new CustomDomainSettings
            {
                Domain = SettingsStore.NormalizeDomain(DomainBox.Text),
                ForwardTarget = ForwardBox.Text.Trim(),
                TargetAppPassword = TargetPassBox.Text.Trim()
            },
            CustomMail = new CustomMailSettings
            {
                Email = CustomMailEmailBox.Text.Trim(),
                AppPassword = CustomMailPassBox.Text.Trim(),
                ClearAfterUse = CustomMailClearCheck.IsChecked == true
            },
            AccountPassword = new AccountPasswordSettings
            {
                Value = AccountPassBox.Text.Trim()
            },
            Update = new UpdateSettings
            {
                BaseUrl = UpdateUrlBox.Text.Trim()
            }
        };

        var issues = SettingsStore.Validate(settings);

        // Problems are shown as warnings, not used as a veto. Refusing the whole
        // save because one method was half-filled made Save look broken: nothing
        // was written, no error was shown, and Back still said "unsaved changes".
        IssueList.ItemsSource = issues;
        IssuePanel.Visibility = issues.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var saved = SettingsStore.Save(settings);

        if (!saved)
        {
            // Nothing reached disk, so the edits really are still unsaved.
            _dirty = true;
            var why = SettingsStore.LastSaveError.Length > 0
                ? SettingsStore.LastSaveError
                : "the settings file could not be written.";
            ShowToast("NOT SAVED — " + why);
            Log.Error("", $"Global mail settings could not be written to {AppPaths.Settings}: {why}");
            return;
        }

        // Show the tidied domain back to the user. This assignment raises
        // TextChanged, so the dirty flag is cleared AFTER it - otherwise a
        // successful save still left Back saying "unsaved changes".
        DomainBox.Text = settings.CustomDomain.Domain;

        _dirty = false;
        RefreshHints();

        if (issues.Count > 0)
        {
            ShowToast($"Saved with {issues.Count} warning(s) — see the list above.");
            Log.Warn("", $"Global mail settings saved with {issues.Count} warning(s).");
        }
        else
        {
            ShowToast("Saved. Tool panels can use these methods right away.");
            Log.Success("", "Global mail settings saved.");
        }
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (_dirty)
        {
            var answer = MessageBox.Show(
                "You have unsaved changes.\n\nLeave without saving?",
                "Global Mail Settings",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);

            if (answer != MessageBoxResult.OK) return;
        }

        Nav.GoDashboard();
    }

    // ------------------------------------------------------------------ toast

    private void ShowToast(string message)
    {
        ToastText.Text = message;
        Toast.Visibility = Visibility.Visible;

        _toastTimer.Stop();
        _toastTimer.Start();
    }
}
