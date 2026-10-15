namespace AISuitePanel;

/// <summary>
/// Single source of truth for every user-visible brand string in the panel.
///
/// Change the product name, the page name or a channel link here and the whole
/// UI follows - no need to hunt through the XAML. Screens bind to these values
/// with <c>{x:Static core:Branding.X}</c>.
/// </summary>
public static class Branding
{
    // ================================================================ identity

    /// <summary>Who makes and supports the panel.</summary>
    public const string Owner = "Developer X";
    public const string OwnerUpper = "DEVELOPER X";

    /// <summary>The product name shown everywhere in the UI and taskbar.</summary>
    public const string AppName = "Developer X Bulk Acc Panel";
    public const string AppNameUpper = "DEVELOPER X BULK ACC PANEL";

    /// <summary>The service/fan page the panel is sold under.</summary>
    public const string PageName = "Automation Service";
    public const string PageUpper = "AUTOMATION SERVICE";

    /// <summary>Shown in the little pill next to the brand in the title bar.</summary>
    public const string Version = "V1.1";

    /// <summary>Window title (= what UI Automation and the taskbar show).</summary>
    public const string WindowTitle = "Developer X Bulk Acc Panel";

    /// <summary>One-line description used on the activation screen and About areas.</summary>
    public const string Tagline =
        "Bulk account and cookie tooling for AI panels - built, licensed and " +
        "supported by Developer X.";

    // ================================================================ channels

    public const string FacebookUrl = "https://www.facebook.com/developerxx1";
    public const string FacebookLabel = "facebook.com/developerxx1";

    public const string WebsiteUrl = "https://automationservice.online";
    public const string WebsiteLabel = "automationservice.online";

    /// <summary>Where the licence endpoint lives - shown when activation fails.</summary>
    public const string LicenceHost = "license.automationservice.online";

    public const string SupportLine =
        "Support & key delivery: message the Automation Service page on Facebook.";

    // ============================================================ screen titles

    public const string ActivationTitle = AppNameUpper + " - ACTIVATION";

    public const string DashboardTitle = AppNameUpper + " - DASHBOARD";

    /// <summary>Line under the activation field.</summary>
    public const string ActivationSubtitle =
        "Paste your licence key to unlock the panel.";

    /// <summary>Explains why activation needs the network.</summary>
    public const string ActivationHint =
        "Online RSA verification is required. The server public key is embedded; " +
        "no private key is stored in this app.";

    /// <summary>Footer strip that is visible on every screen.</summary>
    public const string FooterOwner = "DEVELOPER X";
    public const string FooterPage = "AUTOMATION SERVICE";
}
