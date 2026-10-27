using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AISuitePanel.Models;

namespace AISuitePanel;

/// <summary>An address handed out by the router, plus what produced it.</summary>
public sealed record RoutedAddress(string Address, string SourceRef);

/// <summary>
/// Turns the global mail settings into concrete addresses (Screen 3 -> Screen 4).
/// </summary>
public static class MailRouter
{
    /// <summary>
    /// Names shown in the Method 1 dropdown.
    ///
    /// <para>DERIVED from <see cref="TempMailProviderCatalog.All"/> on purpose.
    /// These were two hand-written parallel arrays, which is exactly the kind of
    /// thing that drifts: appending a service to the catalog without a matching
    /// label shifts every option below it by one and silently repoints every
    /// saved <c>ServiceIndex</c>. Now they cannot disagree.</para>
    /// </summary>
    public static readonly string[] ServiceNames =
        Array.ConvertAll(TempMailProviderCatalog.All, p => p.Display);

    /// <summary>
    /// Never used for a real mailbox.
    ///
    /// <para>This list existed to back a "demoN.invalid" placeholder that the
    /// panel issued when a temp-mail service could not be reached. That path is
    /// gone — a placeholder address made the tool sign up with an address no
    /// mailbox existed behind, then wait out the whole OTP timeout for mail that
    /// could never arrive. Kept only so an older build's settings still parse;
    /// nothing routes to it.</para>
    /// </summary>
    private static readonly string[] ServiceDomains =
    {
        "demo1.invalid", "demo2.invalid", "demo3.invalid", "demo4.invalid", "demo5.invalid"
    };

    /// <summary>Addresses handed out so far - shown nowhere, handy when debugging.</summary>
    private static int _issued;

    public static int IssuedCount => _issued;

    /// <summary>
    /// Next address for a tool. Returns null when the selected source is not
    /// configured well enough to produce one.
    /// </summary>
    public static async Task<RoutedAddress?> NextAsync(string toolId, MailSource source, int serial)
    {
        RoutedAddress? routed = source switch
        {
            MailSource.TempMail     => await NextTempMailAsync().ConfigureAwait(false),
            MailSource.DottedGmail  => NextDottedGmail(toolId, serial),
            MailSource.CustomDomain => NextCustomDomain(),
            MailSource.CustomMail   => NextCustomMail(),
            MailSource.SemiAuto      => NextSemiAuto(toolId, serial),
            MailSource.NoProxy      => NextNoProxy(toolId, serial),
            MailSource.OutlookPool  => NextOutlookPool(toolId, serial),
            _ => null
        };

        if (routed is not null) Interlocked.Increment(ref _issued);
        return routed;
    }

    /// <summary>
    /// Sync shim for callers that haven't migrated to <c>NextAsync</c> yet.
    /// Blocks on the temp-mail service (which is fast - ~200 ms typical).
    /// </summary>
    public static RoutedAddress? Next(string toolId, MailSource source, int serial) =>
        NextAsync(toolId, source, serial).GetAwaiter().GetResult();

    // --------------------------------------------------------------- method 1

    /// <summary>
    /// Generate a REAL temp mailbox via the configured provider (Guerrilla
    /// Mail by default; 1secmail / mail.tm / Temp-Mail.io also supported).
    /// The hard-coded <c>.invalid</c> fall-back is kept as a last-ditch
    /// option for offline / API-failure scenarios so the panel pipeline
    /// still completes a dry run.
    /// </summary>
    private static async Task<RoutedAddress> NextTempMailAsync()
    {
        var cfg = SettingsStore.Current.TempMail;
        var provider = PickProvider(cfg);

        // Every service mints its mailbox through the one service object -
        // Guerrilla used to have its own inline block here, which meant the
        // other services had no place to hand back the token they issue.
        try
        {
            var svc = new TempMailService(provider, cfg.CustomApiKey);
            var fresh = await svc.GenerateMailboxAsync().ConfigureAwait(false);
            if (fresh is not null)
            {
                // Persist the session token (Guerrilla's sid_token, mail.tm's
                // JWT, tempmail.plus's epin, …) into CustomApiKey, which the
                // driver already hands to Python as TEMP_MAIL_API_KEY. Without
                // it the poller cannot read the inbox it was just given, so the
                // OTP never arrives.
                var token = svc.LastToken;
                if (!string.IsNullOrWhiteSpace(token) && token != cfg.CustomApiKey)
                {
                    cfg.CustomApiKey = token;
                    SettingsStore.Save(SettingsStore.Current);
                }
                var (local, domain) = fresh.Value;
                return new RoutedAddress(
                    $"{local}@{domain}",
                    $"{provider.Name} ({local}@{domain})");
            }
        }
        catch (Exception ex)
        {
            Log.Warn("tempmail",
                $"{provider.Name} generate failed ({ex.GetType().Name}: {ex.Message}).");
        }

        // No placeholder address. The panel used to hand out a
        // "box…@demoN.invalid" address here so a dry run could still finish, and
        // it was never clearly a placeholder: a real signup started, filled the
        // address into the site, and then sat waiting 150 s for an OTP that could
        // never arrive because the mailbox does not exist. A failed service is now
        // a skipped account with the reason on screen, which is both honest and
        // faster. Callers already treat a null address that way.
        Log.Warn("tempmail",
            $"{provider.Name} could not issue a mailbox — account skipped. " +
            "Try a different service in Global Settings → Method 1.");
        return null;
    }

    /// <summary>
    /// Resolve the user's <c>ServiceIndex</c> setting (1..catalog length) to the
    /// matching <see cref="TempMailProvider"/>. Out-of-range values clamp to the
    /// catalog rather than throwing, so a settings file written by a NEWER build
    /// still opens on an older one instead of crashing the Create tab.
    /// </summary>
    private static TempMailProvider PickProvider(Models.TempMailSettings cfg)
    {
        var i = Math.Clamp(cfg.ServiceIndex, 1, TempMailProviderCatalog.All.Length);
        return TempMailProviderCatalog.All[i - 1];
    }

    // --------------------------------------------------------------- method 2

    private static RoutedAddress? NextDottedGmail(string toolId, int serial)
    {
        var cfg = SettingsStore.Current.DottedGmail;

        // Feature 1 - skip any pool Gmail the user banned for THIS tool. The
        // ban is per-tool, so a mailbox banned on, say, Z AI is still handed out
        // normally to every other tool.
        var pool = cfg.Accounts
            .Where(a => SettingsStore.IsGmail(a.Email))
            .Where(a => !ToolSettingsStore.IsExcluded(toolId, a.Email))
            .ToList();

        if (pool.Count == 0) return null;

        // Round-robin across the pool. The cursor is persisted, so a restart
        // carries on with the next Gmail instead of starting over at #1.
        var cursor = cfg.Cursor % pool.Count;
        if (cursor < 0) cursor += pool.Count;

        var picked = pool[cursor];
        cfg.Cursor = (cursor + 1) % pool.Count;
        SettingsStore.PersistCursor();

        var at = picked.Email.IndexOf('@');
        var localPart = picked.Email[..at];
        var domain = picked.Email[(at + 1)..];

        var capacity = Capacity(localPart);
        var start = (int)(StableHash(toolId + ":" + serial) % (uint)capacity);

        // Linear probe: walk the dot variations until one is free for this tool.
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var variant = DotVariant(localPart, start + attempt);
            var alias = variant + "@" + domain;

            if (cfg.AntiDuplicate && AliasGuard.IsUsed(toolId, variant))
                continue;

            // Marked either way so a single batch can never issue the same
            // address twice, even with anti-duplicate switched off.
            AliasGuard.Mark(toolId, variant);
            return new RoutedAddress(alias, picked.Email);
        }

        return null;
    }

    /// <summary>
    /// Feature 1 - true when the user has banned <b>every</b> pool Gmail for this
    /// tool, i.e. banning left nothing usable. False when the pool is empty
    /// (nothing configured is a different problem), so callers can show a
    /// different message for "no Gmails at all".
    /// </summary>
    public static bool AllGmailsBanned(string toolId)
    {
        var all = SettingsStore.Current.DottedGmail?.Accounts?
            .Where(a => SettingsStore.IsGmail(a.Email))
            .ToList() ?? new List<GmailEntry>();

        return all.Count > 0 && all.All(a => ToolSettingsStore.IsExcluded(toolId, a.Email));
    }

    /// <summary>How many dot variations a local part can produce (capped at 2^20).</summary>
    public static int Capacity(string localPart) =>
        1 << Math.Min(Math.Max(localPart.Length - 1, 1), 20);

    /// <summary>
    /// Inserts dots after the characters selected by <paramref name="index"/>.
    /// Gmail ignores dots, so every variant lands in the same inbox.
    /// </summary>
    public static string DotVariant(string localPart, int index)
    {
        if (localPart.Length < 2) return localPart;

        var slots = Math.Min(localPart.Length - 1, 20);
        var mask = (uint)index % (uint)(1 << slots);

        var sb = new System.Text.StringBuilder(localPart.Length + slots);
        for (var i = 0; i < localPart.Length; i++)
        {
            sb.Append(localPart[i]);
            if (i < slots && (mask & (1u << i)) != 0) sb.Append('.');
        }

        return CollapseDots(sb.ToString());
    }

    /// <summary>
    /// Folds ".." into "." so a variant never carries a double dot. Two indexes
    /// can collapse to the same alias - the guard treats the second as taken, so
    /// the probe just moves on.
    /// </summary>
    private static string CollapseDots(string value)
    {
        if (!value.Contains("..")) return value;

        var sb = new System.Text.StringBuilder(value.Length);
        var previousWasDot = false;

        foreach (var c in value)
        {
            if (c == '.')
            {
                if (previousWasDot) continue;
                previousWasDot = true;
            }
            else
            {
                previousWasDot = false;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    // --------------------------------------------------------------- method 3

    private static RoutedAddress? NextCustomDomain()
    {
        var cfg = SettingsStore.Current.CustomDomain;
        if (!SettingsStore.IsDomain(cfg.Domain)) return null;

        var local = "u" + Guid.NewGuid().ToString("N")[..10];
        return new RoutedAddress($"{local}@{cfg.Domain}", cfg.ForwardTarget);
    }

    // --------------------------------------------------------------- method 4

    /// <summary>One-shot guard so a multi-quantity Custom Mail batch fails fast after the first.</summary>
    private static int _customMailIssued;
    private static readonly object _customMailGate = new();

    /// <summary>
    /// One fixed Gmail + app password, used once. Differs from <see cref="NextDottedGmail"/>
    /// in that the address is returned verbatim (no rotation, no dots). After the
    /// first successful issue the Custom Mail config is cleared (if ClearAfterUse is
    /// on) and subsequent calls return null until the user reconfigures.
    /// </summary>
    private static RoutedAddress? NextCustomMail()
    {
        lock (_customMailGate)
        {
            if (_customMailIssued > 0) return null;

            var cfg = SettingsStore.Current.CustomMail;
            if (!SettingsStore.IsGmail(cfg.Email)) return null;
            if (string.IsNullOrWhiteSpace(cfg.AppPassword)) return null;

            var address = cfg.Email.Trim().ToLowerInvariant();
            Interlocked.Increment(ref _customMailIssued);

            if (cfg.ClearAfterUse)
            {
                cfg.Email = "";
                cfg.AppPassword = "";
                SettingsStore.Save(SettingsStore.Current);
            }

            return new RoutedAddress(address, "Custom Mail");
        }
    }

    // ----------------------------------------------------------------- modes

    /// <summary>
    /// Same dotted-Gmail rotation, but the driver is told to pause for a manual OTP.
    /// SourceRef carries the marker so downstream code (and the activity log) can tell.
    /// </summary>
    private static RoutedAddress? NextSemiAuto(string toolId, int serial) =>
        WithSourceRef(NextDottedGmail(toolId, serial), "Semi (manual OTP)");

    /// <summary>
    /// Same dotted-Gmail rotation, no proxy flag. The panel has no proxy plumbing yet
    /// (Phase 1) so this is functionally identical to <see cref="NextDottedGmail"/>;
    /// the tag exists so future proxy-aware code can short-circuit.
    /// </summary>
    private static RoutedAddress? NextNoProxy(string toolId, int serial) =>
        WithSourceRef(NextDottedGmail(toolId, serial), "No Proxy");

    /// <summary>
    /// Method 5 - hand out a <b>dotted alias</b> of the next pool mailbox.
    ///
    /// <para>Outlook ignores dots in the local part, so
    /// <c>Kadijah.Jopek4666@outlook.fr</c> is still delivered into the
    /// <c>KadijahJopek4666@outlook.fr</c> inbox. That is the same trick the Gmail
    /// pool uses: one bought mailbox yields many signups, every one of them
    /// still readable through the row's refresh token (the tool reads the code
    /// from the BASE address).</para>
    /// </summary>
    private static RoutedAddress? NextOutlookPool(string toolId, int serial)
    {
        var acct = OutlookPoolService.Next(toolId);
        if (acct is null) return null;

        // Dots are opt-in. A mailbox that does not treat them as an alias silently
        // swallows the verification mail, so the safe default is the plain address.
        if (SettingsStore.Current.OutlookPool?.DotAlias != true)
            return new RoutedAddress(acct.Email, acct.Email);

        var at = acct.Email.IndexOf('@');
        if (at <= 0) return new RoutedAddress(acct.Email, acct.Email);

        var localPart = acct.Email[..at];
        var domain = acct.Email[(at + 1)..];

        var capacity = Capacity(localPart);
        var start = (int)(StableHash(toolId + ":outlook:" + serial) % (uint)capacity);

        // Linear probe until a dot variation this tool has not used yet is free.
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var variant = DotVariant(localPart, start + attempt);
            var alias = variant + "@" + domain;

            if (AliasGuard.IsUsed(toolId, variant)) continue;

            AliasGuard.Mark(toolId, variant);
            Log.Info("mail", $"    outlook alias {alias}  ->  {acct.Email}");
            return new RoutedAddress(alias, acct.Email);
        }

        // Every variation for this mailbox is taken - fall back to the plain
        // address rather than failing the run.
        return new RoutedAddress(acct.Email, acct.Email);
    }

    private static RoutedAddress? WithSourceRef(RoutedAddress? inner, string marker) =>
        inner is null ? null : inner with { SourceRef = marker };

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// FNV-1a. <c>string.GetHashCode()</c> is randomised per process, which would
    /// make alias assignment differ between runs - this one is stable.
    /// </summary>
    private static uint StableHash(string value)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var c in value)
            {
                hash ^= c;
                hash *= 16777619u;
            }
            return hash;
        }
    }
}
