using System.Text.Json.Serialization;

namespace AISuitePanel.Models;

/// <summary>Everything configured on Screen 3 (Global Mail Settings).</summary>
public sealed class GlobalSettings
{
    public TempMailSettings TempMail { get; set; } = new();
    public DottedGmailSettings DottedGmail { get; set; } = new();
    public CustomDomainSettings CustomDomain { get; set; } = new();
    /// <summary>BAT [1.4] - one fixed Gmail + app password, used for a single batch.</summary>
    public CustomMailSettings CustomMail { get; set; } = new();

    /// <summary>
    /// The password every tool puts on the accounts it creates.  Kept here,
    /// beside the mail, so that no tool has to carry one in its own source:
    /// each tool used to ship the operator's own password as a literal
    /// (<c>Zcreatev2.py</c> "Developerx@999", <c>create_advanced.py</c>
    /// "Developerx444"), which meant every customer's accounts shared one
    /// guessable password and the operator's password travelled in the bundle.
    /// </summary>
    public AccountPasswordSettings AccountPassword { get; set; } = new();

    /// <summary>Where this panel looks for its own updates. See UpdateService.</summary>
    public UpdateSettings Update { get; set; } = new();

    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// The operator's update feed.
/// </summary>
public sealed class UpdateSettings
{
    /// <summary>
    /// The built-in GitHub release feed. Customers do not type a URL: the panel
    /// checks the latest public release and verifies the package hash before it
    /// touches the install. A custom HTTPS manifest URL remains supported for
    /// private/staging feeds; set the value to <c>off</c> to disable updates.
    /// </summary>
    public const string DefaultBaseUrl = "github://Sabbirx01/Script_Panel";

    /// <summary>GitHub owner/repository or a custom manifest URL.</summary>
    public string BaseUrl { get; set; } = DefaultBaseUrl;
}

/// <summary>
/// One password for every account a tool creates.
///
/// <para>Empty <see cref="Value"/> is a valid choice and NOT a mistake: the
/// tools then mint a random password per account and print it, which is
/// strictly safer than a shared literal.  The panel deliberately does not
/// write a key at all in that case, so the tool can tell "not configured"
/// apart from "configured as empty".</para>
/// </summary>
public sealed class AccountPasswordSettings
{
    public string Value { get; set; } = "";
}

/// <summary>Method 1 - one of the five built-in services, optional own API key.</summary>
public sealed class TempMailSettings
{
    public int ServiceIndex { get; set; } = 1;
    public string CustomApiKey { get; set; } = "";
}

/// <summary>Method 2 - a pool of Gmails rotated round-robin.</summary>
public sealed class DottedGmailSettings
{
    public List<GmailEntry> Accounts { get; set; } = new();

    /// <summary>"RoundRobin" is the only mode the UI offers today.</summary>
    public string RoutingMode { get; set; } = "RoundRobin";

    /// <summary>Block a dotted alias that this tool has already used.</summary>
    public bool AntiDuplicate { get; set; } = true;

    /// <summary>Persisted rotation cursor - survives a restart.</summary>
    public int Cursor { get; set; }
}

public sealed class GmailEntry
{
    public string Email { get; set; } = "";
    public string AppPassword { get; set; } = "";
}

/// <summary>Method 3 - a catch-all domain that forwards into one inbox.</summary>
public sealed class CustomDomainSettings
{
    public string Domain { get; set; } = "";
    public string ForwardTarget { get; set; } = "";
    public string TargetAppPassword { get; set; } = "";
}

/// <summary>
/// Method 4 (BAT [1.4]) - one Gmail + app password, used for the next batch only.
/// Differs from Method 2 (Dotted Gmail) in that the address is NOT rotated and
/// NOT dotted - the original Gmail is used as-is, once.
/// </summary>
public sealed class CustomMailSettings
{
    public string Email { get; set; } = "";
    public string AppPassword { get; set; } = "";

    /// <summary>Wipe the fields after a successful batch so the next run cannot reuse them.</summary>
    public bool ClearAfterUse { get; set; } = true;
}

/// <summary>Per-tool run settings (Screen 4, section 4).</summary>
public sealed class ToolSettings
{
    /// <summary>Pause between two discrete actions, in seconds.</summary>
    public int DelaySeconds { get; set; } = 3;

    /// <summary>How many workers may run at the same time.</summary>
    public int Threads { get; set; } = 2;

    /// <summary>Accio-only wait after the user clears CAPTCHA, in seconds.</summary>
    public double CaptchaWaitSeconds { get; set; } = 5;

    /// <summary>Accio-only wait after the verification-code page appears, before Change, in seconds.</summary>
    public double XTrickWaitSeconds { get; set; } = 5;

    /// <summary>Extra Accio-only delay after discrete UI actions, in milliseconds.</summary>
    public int ActionDelayMs { get; set; }

    // ─── Multi-tool Python runner (Z AI / Devin AI / future tools) ──────────────

    /// <summary>
    /// Absolute path to <c>python.exe</c>. Empty = the panel will try "python" on
    /// PATH, then "py -3" as a fallback.
    /// </summary>
    public string PythonExePath { get; set; } = "";

    /// <summary>Absolute path to the account-creation Python script (e.g. Zcreatev2.py).</summary>
    public string CreateScriptPath { get; set; } = "";

    /// <summary>Absolute path to the cookie-login Python script (e.g. Zloginv2.py).</summary>
    public string LoginScriptPath { get; set; } = "";

    /// <summary>
    /// Working directory for the Python invocation - usually the folder that
    /// contains <c>.env</c>, <c>accounts.txt</c>, <c>cookies.txt</c> for that tool.
    /// </summary>
    public string ToolWorkingDirectory { get; set; } = "";

    /// <summary>
    /// Shell-style argument template substituted with global settings at runtime.
    /// Placeholders:
    ///   <c>{count}</c>       - count from the Create tab,
    ///   <c>{gmail}</c>       - first usable Gmail from Method 2 (DottedGmail pool),
    ///   <c>{password}</c>    - that Gmail's app password,
    ///   <c>{proxy}</c>       - first alive proxy (or empty),
    ///   <c>{domain}</c>      - active custom domain from alice.json,
    ///   <c>{target}</c>      - user-supplied target URL.
    /// Example:  <c>--count {count} --gmail {gmail} --password {password} --no-proxy</c>
    /// </summary>
    public string CreateArgTemplate { get; set; } = "--count {count}";

    /// <summary>Login arg template, e.g. <c>--cookie-file cookies.txt</c> or empty.</summary>
    public string LoginArgTemplate { get; set; } = "";

    /// <summary>
    /// Run the tool's Python script with the browser hidden (<c>--headless</c>).
    /// Default <c>false</c> = visible browser, exactly as the scripts behave on
    /// their own. Toggled per tool from the Create tab; persisted here so it
    /// survives a restart.
    /// </summary>
    public bool Headless { get; set; } = false;

    /// <summary>
    /// Per-tool landing page for the Login tab's row "🔑 Login" run - mirrors the
    /// Accio panel's <c>LoginTargetUrlBox</c>. The user can paste a referral link
    /// (e.g. <c>https://app.devin.ai/org/&lt;account&gt;/...?ref=xyz</c>) and it is
    /// handed to the login script as <c>--url "&lt;value&gt;"</c> so the session
    /// opens exactly there instead of the script's hard-coded <c>SITE_URL</c>.
    ///
    /// <para>Default = <c>null</c>: the property is omitted from
    /// <c>tool-settings.json</c> until the user edits the box, so the file keeps
    /// its original shape and the per-tool default is applied by
    /// <see cref="AISuitePanel.ToolSettingsStore.LoginUrl"/>.</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LoginUrl { get; set; }

    /// <summary>
    /// Per-tool SIGNUP page the create run is driven to - the Create tab's URL
    /// box. Handed to the tool as <c>--url "&lt;value&gt;"</c>, so accounts are
    /// created on the page the operator wants (a customer's own landing /
    /// referral link, a different locale, a campaign URL) instead of the
    /// hard-coded default. Empty = <see cref="AISuitePanel.ToolSettingsStore.DefaultCreateUrl"/>,
    /// which for accio is <c>https://www.accio.com/login</c>.
    ///
    /// <para>Default = <c>null</c>: the property is omitted from
    /// <c>tool-settings.json</c> until the user edits the box.</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CreateUrl { get; set; }

    /// <summary>
    /// Feature 1 - PER-TOOL mail ban list. Any Gmail whose address appears here is
    /// skipped when the panel picks a pool Gmail for THIS tool only (the dotted
    /// rotation, the IMAP/OTP mailbox and the <c>OwnerOfAlias</c> lookup all
    /// honour it). Global Settings (Method 2) is never modified, so the same
    /// Gmail keeps working for every other tool.
    ///
    /// <para>Default = <c>null</c> (meaning "nothing banned"): the property is
    /// omitted from <c>tool-settings.json</c> entirely while the list is empty,
    /// so the file keeps its original shape until the user actually bans a
    /// Gmail - and snaps back to that shape after an "add back".</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? ExcludedGmails { get; set; }
}
