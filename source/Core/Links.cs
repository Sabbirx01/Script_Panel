using System.Diagnostics;

namespace AISuitePanel;

/// <summary>
/// Opens the panel's own brand / support links in the user's default browser.
///
/// Deliberately separate from <see cref="ManualBrowserService"/>: that one spins
/// up an isolated Playwright profile for a *tool's* official site, while these
/// links are plain "open in whatever browser the user already has" calls.
/// </summary>
public static class Links
{
    public static void OpenExternal(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
            Log.Info("app", $"Opened brand link: {url}");
        }
        catch (Exception ex)
        {
            Log.Error("app", $"Could not open {url} - {ex.Message}");
        }
    }

    /// <summary>automationservice.online</summary>
    public static void OpenWebsite() => OpenExternal(Branding.WebsiteUrl);

    /// <summary>facebook.com/developerxx1</summary>
    public static void OpenFacebook() => OpenExternal(Branding.FacebookUrl);

    /// <summary>Writes the support line + both links into the activity log.</summary>
    public static void LogBrand()
    {
        Log.Info("app", $"{Branding.OwnerUpper} - {Branding.PageUpper}");
        Log.Info("app", $"Page    : {Branding.FacebookLabel}");
        Log.Info("app", $"Website : {Branding.WebsiteLabel}");
    }
}
