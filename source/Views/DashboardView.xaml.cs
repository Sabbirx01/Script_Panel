using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AISuitePanel.Models;

namespace AISuitePanel.Views;

/// <summary>
/// Screen 2 - the four tool boxes. "Saved Cookies: N" is read from the cookie
/// vault, so it is always the real count and never a hard-coded number.
/// </summary>
public partial class DashboardView : UserControl
{
    public DashboardView()
    {
        InitializeComponent();

        ToolList.ItemsSource = ToolCatalog.Tools;
        ToolCatalog.RefreshCookieCounts();

        SubLine.Text = $"{ToolCatalog.Tools.Count} tools ready - open a panel to create accounts " +
                       "or log in with stored cookies.";
        KeyChip.Text = "PANEL LICENCE  " + LicenseService.MaskedKey;

        // Raised from worker threads while a batch runs - marshal before touching the boxes.
        CookieVault.Changed += OnVaultChanged;
        Unloaded += (_, _) => CookieVault.Changed -= OnVaultChanged;

        // A tool's own licence chip has to update the moment it is entered.
        ToolLicenseService.Changed += OnToolLicenceChanged;
        Unloaded += (_, _) => ToolLicenseService.Changed -= OnToolLicenceChanged;
        UpdateService.StartupChecked += OnStartupUpdateChecked;
        Unloaded += (_, _) => UpdateService.StartupChecked -= OnStartupUpdateChecked;
    }

    private void OnVaultChanged(string toolId) =>
        Dispatcher.Invoke((Action)ToolCatalog.RefreshCookieCounts);

    private void OnToolLicenceChanged() =>
        Dispatcher.Invoke((Action)ToolCatalog.RefreshCookieCounts);

    private void OnStartupUpdateChecked(UpdateService.CheckResult result)
    {
        Dispatcher.Invoke(() =>
        {
            if (!result.Ok || result.UpToDate || result.Changed.Count == 0) return;
            UpdateBanner.Visibility = Visibility.Visible;
            UpdateBannerText.Text = $"Version {result.AvailableVersion} is ready — {result.ChangedBytes / 1024.0 / 1024.0:F1} MB download";
            UpdateBannerNote.Text = result.Notes.Length > 0 ? result.Notes : "Open Settings to review and install the signed update.";
        });
    }

    private void OnOpenUpdateSettings(object sender, RoutedEventArgs e) => Nav.GoSettings();

    // ---------------------------------------------------------------- handlers

    private void OnOpenTool(object sender, RoutedEventArgs e)
    {
        e.Handled = true;

        if (sender is FrameworkElement { DataContext: ToolInfo tool })
            Nav.GoTool(tool);
    }

    /// <summary>Clicking anywhere on a card opens it too, not just the button.</summary>
    private void OnCardClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ToolInfo tool })
            Nav.GoTool(tool);
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e) => Nav.GoSettings();

    private void OnResetLicense(object sender, RoutedEventArgs e)
    {
        LicenseService.Reset();
        Nav.GoActivation();
    }
}
