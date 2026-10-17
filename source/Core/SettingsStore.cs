using AISuitePanel.Models;

namespace AISuitePanel;

/// <summary>
/// Screen 3 state - the three mail methods. Readiness helpers below are what the
/// tool panel uses to decide whether a mail source can actually be offered.
/// </summary>
public static class SettingsStore
{
    private static GlobalSettings _current = new();

    public static event Action? Changed;

    public static GlobalSettings Current => _current;

    public static void Load() => _current = Json.Load(AppPaths.Settings, () => new GlobalSettings());

    /// <summary>Last write failure, or "" when the settings reached disk.</summary>
    public static string LastSaveError => Json.LastError;

    /// <summary>
    /// Persists the settings. Returns false when the file could not be written, so
    /// the Save button can say so instead of reporting success for a write that
    /// never happened.
    /// </summary>
    public static bool Save(GlobalSettings settings)
    {
        settings.UpdatedAt = DateTime.Now;
        _current = settings;
        var ok = Json.Save(AppPaths.Settings, settings);
        Changed?.Invoke();
        return ok;
    }

    /// <summary>
    /// The round-robin cursor is advanced on every address handed out, so it is
    /// written back on its own (without firing <see cref="Changed"/>, which would
    /// just churn the dashboard).
    /// </summary>
    public static void PersistCursor()
    {
        Json.Save(AppPaths.Settings, _current);
    }

    // ------------------------------------------------------------- readiness

    public static bool TempMailUsable => _current.TempMail.ServiceIndex is >= 1 and <= 5;

    public static bool DottedGmailUsable =>
        _current.DottedGmail.Accounts.Any(a => IsGmail(a.Email));

    public static bool CustomDomainUsable =>
        IsDomain(_current.CustomDomain.Domain) && IsEmail(_current.CustomDomain.ForwardTarget);

        /// <summary>Method 4 - one fixed Gmail + app password, used for a single batch.</summary>
        public static bool CustomMailUsable =>
            IsGmail(_current.CustomMail.Email) &&
            !string.IsNullOrWhiteSpace(_current.CustomMail.AppPassword);

        /// <summary>
        /// The single Gmail + app password a "Custom Mail" run will sign up with.
        ///
        /// Prefers Method 4 exactly as configured, then falls back to an account
        /// the operator has already set up - the first pool Gmail that has an app
        /// password, else the Method 3 forward target.  "One Gmail + its app
        /// password, used once" describes all three, so an enabled Custom Mail
        /// radio always has credentials behind it instead of dying at run time.
        /// The driver resolves through this same method, which is what makes the
        /// panel's hint truthful about which address will be used.
        /// </summary>
        public static (string Email, string AppPassword) CustomMailAccount()
        {
            var settings = _current;

            if (IsGmail(settings.CustomMail.Email) &&
                !string.IsNullOrWhiteSpace(settings.CustomMail.AppPassword))
                return (settings.CustomMail.Email.Trim(), settings.CustomMail.AppPassword.Trim());

            var pool = settings.DottedGmail.Accounts.FirstOrDefault(
                a => IsGmail(a.Email) && !string.IsNullOrWhiteSpace(a.AppPassword));
            if (pool is not null) return (pool.Email.Trim(), pool.AppPassword.Trim());

            if (IsEmail(settings.CustomDomain.ForwardTarget) &&
                !string.IsNullOrWhiteSpace(settings.CustomDomain.TargetAppPassword))
                return (settings.CustomDomain.ForwardTarget.Trim(),
                        settings.CustomDomain.TargetAppPassword.Trim());

            return ("", "");
        }

    /// <summary>BAT [1.5] - same Gmail rotation, but the driver waits for a manual OTP.</summary>
    public static bool SemiAutoUsable => DottedGmailUsable;

    /// <summary>BAT [1.6] - direct connection. The panel has no proxy yet, so this is
    /// always available - the flag is just a label so future logs can tell what the
    /// user picked.</summary>
    public static bool NoProxyUsable => true;

    public static bool Usable(MailSource source) => source switch
    {
        MailSource.TempMail => TempMailUsable,
        MailSource.DottedGmail => DottedGmailUsable,
        MailSource.CustomDomain => CustomDomainUsable,
        MailSource.CustomMail => CustomMailUsable,
        MailSource.SemiAuto => SemiAutoUsable,
        MailSource.NoProxy => NoProxyUsable,
        _ => false
    };

    /// <summary>Why a source is greyed out - shown under the radio buttons.</summary>
    public static string WhyNot(MailSource source) => source switch
    {
        MailSource.DottedGmail => "Needs at least one Gmail in Global Settings.",
        MailSource.CustomDomain => "Needs a domain and a forward target in Global Settings.",
        MailSource.TempMail => "Select a built-in service in Global Settings.",
        MailSource.CustomMail => "Needs a Gmail + 16-char app password in Method 4.",
        MailSource.SemiAuto => "Needs at least one Gmail in Global Settings.",
        MailSource.NoProxy => "Always available.",
        _ => ""
    };

    // ------------------------------------------------------------ validation

    /// <summary>Returns a list of human readable problems; empty means good to save.</summary>
    public static List<string> Validate(GlobalSettings s)
    {
        var issues = new List<string>();

        if (s.TempMail.ServiceIndex is < 1 or > 5)
            issues.Add("Method 1: pick a built-in temp mail service.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in s.DottedGmail.Accounts)
        {
            if (string.IsNullOrWhiteSpace(g.Email) && string.IsNullOrWhiteSpace(g.AppPassword))
                continue; // a blank row the user has not filled yet

            if (!IsGmail(g.Email))
                issues.Add($"Method 2: '{g.Email}' is not a Gmail address.");
            else if (!seen.Add(g.Email))
                issues.Add($"Method 2: '{g.Email}' is listed twice.");
        }

        var domain = s.CustomDomain.Domain;
        var target = s.CustomDomain.ForwardTarget;

        if (!string.IsNullOrWhiteSpace(domain) && !IsDomain(domain))
            issues.Add("Method 3: domain must look like 'example.com'.");

        if (!string.IsNullOrWhiteSpace(target) && !IsEmail(target))
            issues.Add("Method 3: forward target must be a valid email address.");

        // Method 4 - Custom Mail. Both fields blank = not configured (skip silently).
        var cm = s.CustomMail;
        var cmTouched = !string.IsNullOrWhiteSpace(cm.Email) || !string.IsNullOrWhiteSpace(cm.AppPassword);
        if (cmTouched)
        {
            if (!IsGmail(cm.Email))
                issues.Add("Method 4: a valid Gmail address is required.");
            if (string.IsNullOrWhiteSpace(cm.AppPassword))
                issues.Add("Method 4: app password is required when the Gmail is set.");
            else if (cm.AppPassword.Replace(" ", "").Replace("-", "").Length != 16)
                issues.Add("Method 4: app password must be 16 characters (XXXX-XXXX-XXXX-XXXX).");
        }

        return issues;
    }

    // ---------------------------------------------------------------- helpers

    public static bool IsEmail(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Contains('@')
        && value.IndexOf('@') > 0
        && value.IndexOf('.', value.IndexOf('@')) > value.IndexOf('@') + 1;

    public static bool IsGmail(string value) =>
        IsEmail(value) && value.Trim().EndsWith("@gmail.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gmail displays an app password as four groups of four letters -
    /// <c>abcd efgh ijkl mnop</c> - and that is how it gets typed in. IMAP wants
    /// the 16 characters with no separators, so every app password is normalised
    /// on the way out to a tool. Without this the tools opened the mailbox with a
    /// 19-character string and every IMAP login failed, which looked like "no
    /// verification code arrived" rather than a credential problem.
    ///
    /// The stored value is left exactly as the operator typed it.
    /// </summary>
    public static string NormalizeAppPassword(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? ""
            : value.Replace(" ", "").Replace("-", "").Trim();

    public static bool IsDomain(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;

        var d = value.Trim().ToLowerInvariant()
            .Replace("https://", "").Replace("http://", "").TrimEnd('/');

        if (d.StartsWith("www.")) d = d[4..];

        return d.Contains('.')
            && !d.StartsWith('.')
            && !d.EndsWith('.')
            && !d.Contains(' ')
            && !d.Contains('/');
    }

    /// <summary>Normalises user input for storage: lowercase, no scheme, no www.</summary>
    public static string NormalizeDomain(string value)
    {
        var d = (value ?? "").Trim().ToLowerInvariant()
            .Replace("https://", "").Replace("http://", "").TrimEnd('/');

        if (d.StartsWith("www.")) d = d[4..];
        return d;
    }
}
