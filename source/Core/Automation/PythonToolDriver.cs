using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using AISuitePanel.Models;

namespace AISuitePanel.Automation;

/// <summary>
/// Generic driver for tools whose real automation lives in a separate
/// Python + Playwright script (Z AI = <c>Zcreatev2.py</c> / <c>Zloginv2.py</c>,
/// Devin AI = <c>devin_create.py</c> / <c>devin_login.py</c>).
///
/// Phase-3-style decision: instead of porting 200 KB of working Python into C#,
/// the panel shells out to the user's already-tested script with the CLI flags
/// (or stdin prompts) it expects, and reads the script's own <c>accounts.txt</c>
/// + <c>cookies.txt</c> back into the panel grid after the run finishes.
///
/// Every tool whose catalog entry uses this driver must set its
/// <c>ToolSettings.{PythonExePath, CreateScriptPath, LoginScriptPath,
/// ToolWorkingDirectory, CreateArgTemplate, LoginArgTemplate}</c> in the
/// "Tool Paths" section of Global Settings, or via the Settings tab on the
/// panel itself.
///
/// Argument template placeholders (substituted at run-time):
///   <c>{count}</c>      - count from the Create tab
///   <c>{gmail}</c>      - first usable Gmail from the DottedGmail pool
///   <c>{password}</c>   - that Gmail's app password
///   <c>{proxy}</c>      - first alive proxy URL (or empty)
///   <c>{domain}</c>     - active custom domain (alice.json / settings)
///   <c>{target}</c>     - user-supplied target URL
///
/// For interactive scripts that prompt via <c>input()</c> (Zcreatev2, Zloginv2)
/// the driver pipes the count / menu choice through stdin and closes it,
/// which is enough to drive a one-shot batch through.
/// </summary>
public sealed class PythonToolDriver : IToolDriver
{
    /// <summary>Stable tool id, mirrors the catalog entry (e.g. "zai", "devin").</summary>
    public string ToolId { get; }

    /// <summary>
    /// Per-tool settings. Always loaded fresh from <c>ToolSettingsStore</c> so
    /// changes the user made in Settings tab take effect on the next run.
    /// </summary>
    private ToolSettings Settings => ToolSettingsStore.For(ToolId);

    /// <summary>
    /// Marks in stdout that mean a happy ending for one batch. Kept generic
    /// on purpose — the scripts vary (Zcreatev2 prints "DONE!", Zloginv2
    /// prints "[OK] Cookie login!").
    /// </summary>
    private static readonly string[] SuccessMarkers =
    {
        "DONE!", "[OK]", "account OK", "[SUCCESS]", "Done:", "saved:"
    };

    /// <summary>
    /// Hard failures. Anything that says the run didn't complete takes this
    /// path; the <see cref="Classify"/> step then chooses a useful Fix hint.
    /// </summary>
    private static readonly string[] FailureMarkers =
    {
        "[ERROR]", "Traceback (most recent call last)",
        "No such file or directory", "ModuleNotFoundError",
        "IMAP err:", "Verify link not received", "Captcha",
        "OTP timeout", "Aborted", "Login failed", "FAILED cookies"
    };

    public PythonToolDriver(string toolId)
    {
        if (string.IsNullOrWhiteSpace(toolId))
            throw new ArgumentException("Tool id is required.", nameof(toolId));
        ToolId = toolId;
    }

    // ----------------------------------------------------------------- create

    public Task<DriverResult> CreateAccountAsync(
        string email, MailSource source, CancellationToken ct) =>
        CreateAccountAsyncInternal(email, source, liveLog: null, ct, forceNoProxy: false);

    /// <summary>
    /// Like <see cref="CreateAccountAsync"/> but lets the caller stream every
    /// stdout line into a per-tool Live Log box (used by the panel view).
    /// </summary>
    public Task<DriverResult> CreateAccountAsync(
        string email, MailSource source, Action<string>? liveLog, CancellationToken ct,
        bool forceNoProxy = false, int count = 1) =>
        CreateAccountAsyncInternal(email, source, liveLog, ct, forceNoProxy, count);

    private async Task<DriverResult> CreateAccountAsyncInternal(
        string email, MailSource source, Action<string>? liveLog,
        CancellationToken ct, bool forceNoProxy, int count = 1)
    {
        var settings = Settings;
        var validation = Validate(settings, wantLogin: false);
        if (validation is not null) return validation;

        var script = settings.CreateScriptPath!;
        var workDir = settings.ToolWorkingDirectory!;

        // 1. Sync the script's .env with the keys the active mode needs
        //    (GMAIL_USER for the pool-based modes, TEMP_MAIL_ADDRESS for
        //    Temp Mail, mode-specific extras). The `email` parameter here is
        //    the address the panel actually wants the script to use — for
        //    Temp Mail it's a freshly-issued Guerrilla Mail address; for
        //    Gmail Pool it's a dotted variant of the active pool Gmail.
        //
        //    The same keys are ALSO handed to the child process through its
        //    environment: panel_mail.load_env() overlays os.environ on top of
        //    the .env files, so two parallel create runs (the "At once" wave
        //    mode) each keep their OWN address even though they share one .env.
        var envKeys = TrySyncEnv(ToolId, settings, source, email);

        // The shared proxy list plus this run's ON/OFF state.  The tools read
        // PANEL_PROXY_FILE through _shared/proxy_pool.py (which panel_browser
        // consults on every launch) and PANEL_PROXY tells them whether the
        // operator's per-tool switch is off - the same thing --no-proxy says, but
        // this reaches tools that never had a proxy flag of their own.
        envKeys["PANEL_PROXY_FILE"] = ProxyPool.File;
        envKeys["PANEL_PROXY"] = forceNoProxy ? "off" : "on";

        // 2. Build the CLI argument list from the template (substituting
        //    placeholders) and any extra flags the panel always wants.
        //    --headless is appended here (never left to the user's template)
        //    whenever the per-tool toggle is ON.
        var args = BuildArgs(settings.CreateArgTemplate, source, count, forceNoProxy, settings.Headless);

        // 3. Pick a stdin payload if the script is interactive (Zcreatev2 asks
        //    for the count with input()). Devin / non-interactive scripts can
        //    ignore this and just read from stdin; either way it's harmless.
        var stdinLines = new[] { count.ToString() };

        var timeout = TimeSpan.FromMinutes(20);   // larger than Accio — Z AI can take 4 min / account

        Log.Info(ToolId,
            $"python {Path.GetFileName(script)} -- {CreateAccountRunner.SourceLabel(source)}: " +
            $"{count}x → {email}  ({(forceNoProxy ? "no-proxy" : "proxy-from-env")}, " +
            $"{(settings.Headless ? "headless" : "visible")})");
        liveLog?.Invoke($"▶ python {Path.GetFileName(script)} -- mode={source}  count={count}" +
                        (settings.Headless ? "  --headless" : ""));
        liveLog?.Invoke($"  → wrote SIGNUP_EMAIL={email} + mode keys to .env; script picks up the address.");
        if (source == MailSource.TempMail)
            liveLog?.Invoke($"  → temp-mail mode: skipping GMAIL_USER; script will poll Guerrilla API for OTP.");

        return await RunPythonAsync(script, args, stdinLines, workDir, timeout, liveLog, ct, envKeys)
            .ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ login

    public async Task<DriverResult> LoginWithCookiesAsync(
        string email, IReadOnlyList<BrowserCookie> cookies, CancellationToken ct)
    {
        var settings = Settings;
        var validation = Validate(settings, wantLogin: true);
        if (validation is not null) return validation;

        var script = settings.LoginScriptPath!;
        var workDir = settings.ToolWorkingDirectory!;

        // Hand the cookies to the script the only way it can receive them: in its
        // OWN cookie file, under the exact address the script is about to be told
        // to look for.
        //
        // This is what was broken. The cookies arrive here from the vault/panel
        // side, but nothing ever wrote them anywhere - the method took a
        // `cookies` parameter and never used it, relying instead on the tool's
        // own cookies.txt already holding a matching row. The two sides key by
        // ADDRESS and Gmail ignores dots, so the panel's "s.a.b.ir.molla2071@…"
        // never matched the file's "sa.b.ir.molla2071@…" and the script silently
        // opened a browser with no cookies at all - for z.ai, MiniMax and Devin
        // alike, since all three share this one code path.
        InjectCookiesForScript(email, cookies);

        // Login scripts already have their own cookies.txt handling — we
        // do NOT want to clobber it by syncing the Gmail env. So skip step 1.
        // The script does still need to know WHICH account this run is about
        // (tool4's minimax_login.py verifies exactly that one cookie set), so
        // refresh only that pair of keys.
        SyncLoginTarget(settings, email);

        var args = BuildArgs(settings.LoginArgTemplate, source: MailSource.DottedGmail,
                             count: 0, forceNoProxy: false, headless: settings.Headless);

        // Land the verification on the per-tool URL the user set on the Login tab
        // (same value the row "🔑 Login" run uses). NOT passed as --headless: the
        // verify path keeps its old behaviour and never gets --stay, so it still
        // exits on its own once the check is done.
        var loginUrl = ToolSettingsStore.LoginUrl(ToolId);
        if (!string.IsNullOrWhiteSpace(loginUrl) && !args.Contains("--url"))
        {
            args.Add("--url");
            args.Add(loginUrl);
        }

        // Login scripts typically expose a menu where 0 = login all stored
        // accounts. Piping "0\n" then closing stdin is enough to drive them
        // through the whole batch (the EOF on input() makes the script exit
        // after the last login).
        var stdinLines = new[] { "0" };

        var timeout = TimeSpan.FromMinutes(30);   // login + browser keep-open can run a while

        Log.Info(ToolId, $"python {Path.GetFileName(script)} -- login (cookies replayed)");
        return await RunPythonAsync(script, args, stdinLines, workDir, timeout, liveLog: null, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the cookies into the tool's OWN <c>accounts\&lt;tool&gt;\cookies.txt</c>,
    /// replacing the row for this address.
    ///
    /// <para>The login scripts read that file and nothing else, matching the row
    /// by address. So the row must be keyed by the very string the script is
    /// handed (<c>SyncLoginTarget</c> passes this same <paramref name="email"/>),
    /// otherwise the lookup misses and the browser opens with no cookies.
    ///
    /// <para>Written camelCase on purpose: Playwright's Python API expects
    /// <c>name</c>, <c>value</c>, <c>domain</c>, <c>path</c>, <c>expires</c>,
    /// <c>sameSite</c>. Only this tool's own file is touched, so one tool's
    /// cookies can never reach another's.
    /// </summary>
    private void InjectCookiesForScript(string email, IReadOnlyList<BrowserCookie> cookies)
    {
        if (cookies is null || cookies.Count == 0 || string.IsNullOrWhiteSpace(email)) return;

        var wanted = email.Trim();
        try
        {
            var file = Path.Combine(AppPaths.ToolAccountsDir(ToolId), "cookies.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);

            var options = new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            };
            var line = wanted + " | " + System.Text.Json.JsonSerializer.Serialize(cookies, options);

            var kept = new List<string>();
            var replaced = false;
            if (File.Exists(file))
            {
                foreach (var existing in File.ReadAllLines(file, Encoding.UTF8))
                {
                    if (existing.Trim().Length == 0) continue;
                    var bar = existing.IndexOf('|');
                    var key = bar > 0 ? existing[..bar].Trim() : existing.Trim();
                    if (string.Equals(key, wanted, StringComparison.OrdinalIgnoreCase))
                    {
                        kept.Add(line);
                        replaced = true;
                    }
                    else
                    {
                        kept.Add(existing);
                    }
                }
            }

            if (!replaced) kept.Add(line);
            File.WriteAllLines(file, kept, Encoding.UTF8);
            Log.Info(ToolId, $"  staged {cookies.Count} cookie(s) for {wanted} in {file}");
        }
        catch (Exception ex)
        {
            // A failure here is not fatal: the script may still find its own row.
            Log.Warn(ToolId, "  could not stage cookies for the login script: " + ex.Message);
        }
    }

    // --------------------------------------------------------------- internals

    private static DriverResult? Validate(ToolSettings s, bool wantLogin)
    {
        if (string.IsNullOrWhiteSpace(s.PythonExePath) && !PythonOnPath())
            return DriverResult.Fail(
                "Python interpreter not found. Set ToolSettings.PythonExePath in Global " +
                "Settings → Tool Paths, or add python.exe to PATH.");

        var script = wantLogin ? s.LoginScriptPath : s.CreateScriptPath;
        if (string.IsNullOrWhiteSpace(script) || !File.Exists(script))
            return DriverResult.Fail(
                $"{(wantLogin ? "Login" : "Create")} script not found at: {script ?? "(empty)"}  " +
                "Set it in Global Settings → Tool Paths.");

        if (string.IsNullOrWhiteSpace(s.ToolWorkingDirectory) || !Directory.Exists(s.ToolWorkingDirectory))
            return DriverResult.Fail(
                $"Tool working directory not found at: {s.ToolWorkingDirectory ?? "(empty)"}  " +
                "This is the folder that contains .env, accounts.txt, cookies.txt for the script.");

        return null;
    }

    /// <summary>
    /// Writes the keys the active <paramref name="mode"/> needs into the
    /// script's <c>.env</c>. The <paramref name="email"/> parameter is the
    /// address the panel wants the script to actually use on the signup
    /// form — for Temp Mail it's a freshly-issued Guerrilla Mail address,
    /// for Gmail Pool it's a dotted variant of the active pool Gmail.
    /// Other modes (CustomMail, CustomDomain, SemiAuto, NoProxy) still
    /// fall back to a usable Gmail from the global DottedGmail pool for IMAP.
    ///
    /// The pool is rotated via the persisted <c>cursor</c> on
    /// <see cref="SettingsStore.Current"/>.DottedGmail — same cursor that
    /// <see cref="MailRouter"/> uses for its own picks, so a session that
    /// mixes panel-driven and CLI-driven batches keeps rotating through
    /// every configured Gmail instead of hammering pool[0] forever.
    /// The cursor is advanced AFTER the keys are written and persisted so a
    /// crash mid-write doesn't double-issue the same Gmail next run.
    ///
    /// Writes to <b>two</b> locations when they differ:
    ///   1. <c>{ToolWorkingDirectory}\.env</c>          — where the user normally
    ///                                                     keeps their .env (e.g.
    ///                                                     <c>Z Code V2\.env</c>)
    ///   2. <c>{scriptDir}\.env</c>                     — where the script reads
    ///                                                     from at import time
    ///                                                     (e.g. <c>python\.env</c>)
    /// </summary>
    /// <summary>
    /// Writes just SIGNUP_EMAIL / PANEL_EMAIL into the script's .env so a login
    /// script knows which stored cookie set this call is about.  Nothing else in
    /// the .env is touched - the login path must not clobber the mail settings.
    ///
    /// <para>
    /// Visible to the panel views (<see cref="AISuitePanel.Views.PythonToolPanelView"/>)
    /// as well, because the per-row "🔑 Login" button runs the tool's own login
    /// script for ONE account and needs to point it at that exact cookie set.
    /// </para>
    /// </summary>
    internal void SyncLoginTarget(ToolSettings settings, string email)
    {
        if (string.IsNullOrWhiteSpace(email)) return;

        var dirs = new List<string>();
        if (!string.IsNullOrWhiteSpace(settings.LoginScriptPath))
        {
            var dir = System.IO.Path.GetDirectoryName(settings.LoginScriptPath);
            if (!string.IsNullOrWhiteSpace(dir)) dirs.Add(dir!);
        }
        if (!string.IsNullOrWhiteSpace(settings.ToolWorkingDirectory))
            dirs.Add(settings.ToolWorkingDirectory!);

        foreach (var dir in dirs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                System.IO.Directory.CreateDirectory(dir);
                var path = System.IO.Path.Combine(dir, ".env");
                var lines = System.IO.File.Exists(path)
                    ? System.IO.File.ReadAllLines(path).ToList()
                    : new List<string>();

                WriteKey("SIGNUP_EMAIL", email);
                WriteKey("PANEL_EMAIL", email);
                System.IO.File.WriteAllLines(path, lines);

                void WriteKey(string key, string value)
                {
                    var at = lines.FindIndex(l => l.TrimStart()
                        .StartsWith(key + "=", StringComparison.OrdinalIgnoreCase));
                    if (at >= 0) lines[at] = key + "=" + value;
                    else lines.Add(key + "=" + value);
                }
            }
            catch (Exception ex)
            {
                Log.Warn(ToolId, $"could not refresh the login target in {dir}\\.env: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Which pool Gmail a dotted alias belongs to.  Gmail ignores dots, so
    /// <c>b.s.n.s.s.m.e.t.a@gmail.com</c> is <c>bsnssmeta@gmail.com</c> - the
    /// mailbox whose IMAP we must poll for the OTP.
    /// </summary>
    private static GmailEntry? OwnerOfAlias(List<GmailEntry>? pool, string? alias)
    {
        if (pool is null || pool.Count == 0 || string.IsNullOrWhiteSpace(alias))
            return null;
        var at = alias.IndexOf('@');
        if (at <= 0) return null;
        var flat = alias[..at].Replace(".", "");
        foreach (var entry in pool)
        {
            var local = (entry.Email ?? "").Split('@')[0].Replace(".", "");
            if (local.Equals(flat, StringComparison.OrdinalIgnoreCase))
                return entry;
        }
        return null;
    }

    /// <summary>
    /// Writes the per-run keys to the script's <c>.env</c> and returns them so
    /// the caller can also inject them into the child process's environment
    /// (<c>panel_mail.load_env()</c> lets <c>os.environ</c> win over the file,
    /// which is what keeps parallel "At once" runs from reading each other's
    /// <c>SIGNUP_EMAIL</c>).
    /// </summary>
    /// <remarks>
    /// <paramref name="toolId"/> is a parameter rather than the instance's own
    /// <see cref="ToolId"/> because <see cref="AccioToolDriver"/> uses this too:
    /// the panel must pick ONE pool Gmail for a run, and if accio rotated the
    /// cursor a second time it would sign up with pool[n] while IMAP watched
    /// pool[n+1].  Sharing the method is what keeps the two drivers in step.
    /// </remarks>
    internal static Dictionary<string, string> TrySyncEnv(
        string toolId, ToolSettings s, MailSource mode, string email)
    {
        var written = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var settings = SettingsStore.Current;
            // Feature 1 - the IMAP/OTP mailbox and the rotation both come from
            // this pool, so a Gmail banned for THIS tool must never be picked
            // here either.
            var pool = settings.DottedGmail?.Accounts?
                .Where(a => SettingsStore.IsGmail(a.Email) &&
                            !string.IsNullOrWhiteSpace(a.AppPassword) &&
                            !ToolSettingsStore.IsExcluded(toolId, a.Email))
                .ToList();
            // Gmail Pool: MailRouter already picked the pool Gmail this dotted
            // alias belongs to, and THAT is where the OTP lands.  Rotating the
            // cursor a second time here (as this used to) made the alias come
            // from pool[n] while GMAIL_USER pointed at pool[n+1] - IMAP then
            // watched the wrong inbox and every OTP timed out.
            var aliasOwner = OwnerOfAlias(pool, email);
            string rotGmail, rotPassword;
            if (aliasOwner is not null)
            {
                rotGmail    = aliasOwner.Email ?? "";
                rotPassword = aliasOwner.AppPassword ?? "";
            }
            else
            {
                (rotGmail, rotPassword) = PickRotatedGmail(pool);
            }
            var firstPoolGmail    = rotGmail.Trim();
            var firstPoolPassword = rotPassword.Trim();

            // Custom Domain (Method 3) - the account is a catch-all address on the
            // user's OWN domain and every message is forwarded into the inbox they
            // configured beside it.  That inbox carries its own app password and is
            // not necessarily one of the pool Gmails, so it is resolved here and
            // takes precedence over the pool for every IMAP key written below.
            var customDomainInbox = (settings.CustomDomain?.ForwardTarget ?? "").Trim();
            var customDomainPass  = (settings.CustomDomain?.TargetAppPassword ?? "").Trim();
            var customDomainHasInbox =
                mode == MailSource.CustomDomain && customDomainInbox.Length > 0;

            if (string.IsNullOrEmpty(firstPoolGmail) && mode != MailSource.TempMail &&
                !customDomainHasInbox)
            {
                Log.Warn(toolId,
                    MailRouter.AllGmailsBanned(toolId)
                        ? $"Every Gmail is BANNED for {toolId} - no mailbox can be picked. " +
                          "Open the tool's Settings → MAIL section and add one back."
                        : "Global DottedGmail pool is empty - the script will fall back to its own .env " +
                          "(if any). Configure Gmail + app password in Global Settings → Method 2.");
            }

            // IMAP creds — which Gmail to use for OTP.  Every other mode uses the
            // rotated pool Gmail (the OTP can land there via a forwarder / API).
            //
            // Custom Mail is the exception, and deliberately so: it is "ONE Gmail
            // + app password, used once", so it must not rotate. It resolves
            // through SettingsStore.CustomMailAccount(), which prefers Method 4
            // and otherwise falls back to a fixed account the operator already
            // configured - that is what keeps the radio usable when Method 4 is
            // still empty, and it is the same account the panel's hint names.
            var imapGmail = firstPoolGmail;
            var imapPassword = firstPoolPassword;

            if (mode == MailSource.CustomMail)
            {
                var custom = SettingsStore.CustomMailAccount();
                if (custom.Email.Length > 0) imapGmail = custom.Email;
                if (custom.AppPassword.Length > 0) imapPassword = custom.AppPassword;
            }

            // Custom Domain overrides both: the forwarded copy of the signup mail
            // lands in the Method 3 forward target, so THAT is the mailbox the
            // tools have to open.  Using the pool Gmail instead made every
            // custom-domain run poll the wrong inbox and time out with
            // "no verification code" whenever the two were not the same address.
            // (Only when no forward target is configured do we keep the pool Gmail,
            // which is what the operator would have been relying on before.)
            if (customDomainHasInbox)
            {
                imapGmail = customDomainInbox;
                if (customDomainPass.Length > 0) imapPassword = customDomainPass;
            }

            // Build the candidate paths.
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(s.ToolWorkingDirectory))
                paths.Add(Path.Combine(s.ToolWorkingDirectory!, ".env"));
            if (!string.IsNullOrWhiteSpace(s.CreateScriptPath))
            {
                var scriptDir = Path.GetDirectoryName(s.CreateScriptPath);
                if (!string.IsNullOrWhiteSpace(scriptDir))
                    paths.Add(Path.Combine(scriptDir!, ".env"));

                    // The .env carries the mailbox password, so it is account
                    // data: it goes beside the exe too, not into the folder
                    // holding the tool's code. The tools read the state folder
                    // first (tools/_shared/tool_state.py -> env_file).
                    try
                    {
                        paths.Add(Path.Combine(AppPaths.ToolAccountsDir(toolId), ".env"));
                    }
                    catch
                    {
                        // Falls back to the two locations above.
                    }
            }

            // Keys we always write so the script can log what's active.
            var keys = new Dictionary<string, string>
            {
                ["PANEL_MAIL_MODE"] = mode.ToString(),
                ["PANEL_TOOL"]      = toolId,
                ["PANEL_EMAIL"]     = email ?? ""
            };

            // Email that the script should sign up with.  For Temp Mail this
            // is the fresh Guerrilla address; for Gmail Pool it's a dotted
            // variant of the pool Gmail.  Script reads SIGNUP_EMAIL first,
            // then PANEL_EMAIL, then falls back to GMAIL_USER.
            if (!string.IsNullOrWhiteSpace(email))
                keys["SIGNUP_EMAIL"] = email.Trim();

            // The password every tool must set on the account it creates.
            // Written ONLY when the operator configured one: with no key at
            // all the tool can tell "not configured" apart from "configured as
            // empty" and mints a random password per account instead of
            // falling back to a literal in its own source.
            var accountPassword = (SettingsStore.Current.AccountPassword?.Value ?? "").Trim();
            if (accountPassword.Length > 0)
            {
                keys["ACCOUNT_PASSWORD"] = accountPassword;
                keys["PANEL_ACCOUNT_PASSWORD"] = accountPassword;   // older alias
            }

            // For Temp Mail: don't write GMAIL_USER from the pool — that
            // would silently override the temp mailbox with a Gmail.  The
            // temp script reads SIGNUP_EMAIL for the address and
            // TEMP_MAIL_API_KEY for the session.
            if (mode == MailSource.TempMail)
            {
                keys["TEMP_MAIL_SERVICE"] = (settings.TempMail.ServiceIndex).ToString();
                if (!string.IsNullOrWhiteSpace(settings.TempMail.CustomApiKey))
                    keys["TEMP_MAIL_API_KEY"] = settings.TempMail.CustomApiKey.Trim();
                // IMAP still needed for the verify URL?  No — the temp
                // script reads OTP via API.  Skip GMAIL_USER entirely.
            }
            else
            {
                keys["GMAIL_USER"]         = imapGmail;
                // 16 bare characters: Gmail's display form ("abcd efgh ijkl mnop")
                // is not accepted by IMAP.
                keys["GMAIL_APP_PASSWORD"] = SettingsStore.NormalizeAppPassword(imapPassword);
            }

            // CustomDomain: also write the domain + forward target so the
            // script knows where OTP / signups go.
            if (mode == MailSource.CustomDomain &&
                !string.IsNullOrWhiteSpace(settings.CustomDomain.Domain))
            {
                keys["CUSTOM_DOMAIN"]            = settings.CustomDomain.Domain.Trim();
                keys["CUSTOM_DOMAIN_INBOX"]      = imapGmail;
                keys["CUSTOM_DOMAIN_INBOX_PASS"] = SettingsStore.NormalizeAppPassword(imapPassword);
            }

            foreach (var envPath in paths)
            {
                try
                {
                    var existing = new List<string>();
                    if (File.Exists(envPath))
                        existing.AddRange(File.ReadAllLines(envPath, Encoding.UTF8));

                    foreach (var kv in keys)
                        UpsertEnv(existing, kv.Key, kv.Value);

                    Directory.CreateDirectory(Path.GetDirectoryName(envPath)!);
                    File.WriteAllLines(envPath, existing, new UTF8Encoding(false));
                    Log.Info(toolId, $"  wrote {keys.Count} keys to {envPath} (email={email})");
                }
                catch (Exception innerEx)
                {
                    Log.Warn(toolId, $"  could not write {envPath} ({innerEx.Message})");
                }
            }

            // Hand the exact same key set back so the caller can put it in the
            // child process's environment too.
            foreach (var kv in keys) written[kv.Key] = kv.Value;
        }
        catch (Exception ex)
        {
            Log.Warn(toolId, "  could not sync .env (" + ex.Message + ") - script will use its own.");
        }

        return written;
    }

    private static void UpsertEnv(List<string> lines, string key, string value)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var raw = lines[i].TrimStart();
            if (raw.StartsWith("#")) continue;
            var eq = raw.IndexOf('=');
            if (eq <= 0) continue;
            if (string.Equals(raw[..eq].Trim(), key, StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = key + "=" + value;
                return;
            }
        }
        lines.Add(key + "=" + value);
    }

    /// <summary>
    /// Cursor-rotated pick from the Gmail pool. Shared with
    /// <see cref="MailRouter"/> via the persisted <c>DottedGmail.Cursor</c>
    /// field — so a panel-driven batch and a CLI-driven batch never fight
    /// over pool[0]. After picking, the cursor is advanced by one (mod pool
    /// size) and written back so the very next call — whether from the panel
    /// or from a fresh process — gets the next Gmail.
    ///
    /// Returns <c>("", "")</c> when the pool is empty so callers can fall
    /// back to the script's own <c>.env</c>.
    /// </summary>
    private static (string Email, string AppPassword) PickRotatedGmail(
        List<GmailEntry>? pool)
    {
        if (pool is null || pool.Count == 0)
            return ("", "");

        var cfg = SettingsStore.Current.DottedGmail;
        // Defensive: cursor might be stale relative to pool size (the user
        // removed entries after the last run). Normalise into [0, N).
        var raw = cfg?.Cursor ?? 0;
        var idx = ((raw % pool.Count) + pool.Count) % pool.Count;

        var picked = pool[idx];
        var newCursor = (idx + 1) % pool.Count;
        if (cfg != null && cfg.Cursor != newCursor)
        {
            cfg.Cursor = newCursor;
            SettingsStore.PersistCursor();
        }

        return (picked.Email ?? "", picked.AppPassword ?? "");
    }

    /// <summary>
    /// Splits the user's template on whitespace, expands every supported
    /// placeholder and appends panel-managed flags (force --no-proxy).
    /// </summary>
    private List<string> BuildArgs(string? template, MailSource source, int count,
                                   bool forceNoProxy, bool headless = false)
    {
        var args = new List<string>();
        if (!string.IsNullOrWhiteSpace(template))
        {
            foreach (var raw in Tokenise(template))
            {
                args.Add(Expand(raw, source, count));
            }
        }

        if (forceNoProxy && !args.Contains("--no-proxy"))
            args.Add("--no-proxy");

        // Panel-managed flag: appended here (never left to the user's template)
        // so the per-tool HEADLESS toggle always reaches the script.
        if (headless && !args.Contains("--headless"))
            args.Add("--headless");

        return args;
    }

    /// <summary>
    /// Tokenise on whitespace, but keep anything in single or double quotes as a
    /// single token. Lets the user write <c>--gmail "my.user@gmail.com"</c>.
    /// </summary>
    private static IEnumerable<string> Tokenise(string template)
    {
        var buf = new StringBuilder();
        char? quote = null;
        foreach (var ch in template)
        {
            if (quote is not null)
            {
                if (ch == quote) { quote = null; continue; }
                buf.Append(ch);
                continue;
            }
            if (ch is '"' or '\'') { quote = ch; continue; }
            if (char.IsWhiteSpace(ch))
            {
                if (buf.Length > 0) { yield return buf.ToString(); buf.Clear(); }
                continue;
            }
            buf.Append(ch);
        }
        if (buf.Length > 0) yield return buf.ToString();
    }

    private string Expand(string token, MailSource source, int count)
    {
        if (!token.Contains('{')) return token;

        var gmail = "";
        var password = "";
        // Feature 1 - {gmail} / {password} placeholders must honour this tool's
        // ban list too, otherwise a template could still inject a banned Gmail.
        var pool = SettingsStore.Current.DottedGmail?.Accounts?
            .Where(a => SettingsStore.IsGmail(a.Email) &&
                        !string.IsNullOrWhiteSpace(a.AppPassword) &&
                        !ToolSettingsStore.IsExcluded(ToolId, a.Email))
            .ToList();
        if (pool is { Count: > 0 })
        {
            // Use the cursor-rotated Gmail so {gmail} / {password} placeholders
            // pick the next pool entry rather than always pool[0]. The cursor is
            // shared with MailRouter, so a session that mixes CLI and panel runs
            // (and that reuses {gmail} in a 5-batch) still walks every Gmail in
            // turn instead of hammering the first one.
            var (rotGmail, rotPassword) = PickRotatedGmail(pool);
            gmail    = rotGmail;
            password = rotPassword;
        }

        var domain = SettingsStore.Current.CustomDomain?.Domain ?? "";
        var target = SettingsStore.Current.CustomDomain?.ForwardTarget ?? "";

        // Map MailSource to the script's --mode value.  The script ignores
        // any value it doesn't understand (default = gmail-pool).
        var modeStr = source switch
        {
            MailSource.CustomDomain  => "custom-domain",
            MailSource.CustomMail    => "custom-mail",
            MailSource.TempMail      => "temp-mail",
            MailSource.SemiAuto      => "semi-auto",
            MailSource.NoProxy       => "no-proxy",
            _ => "gmail-pool"
        };

        return token
            .Replace("{count}",    count.ToString())
            .Replace("{gmail}",    gmail)
            .Replace("{password}", password)
            .Replace("{domain}",   domain)
            .Replace("{target}",   target)
            .Replace("{mode}",     modeStr)
            // {proxy} intentionally left blank — the script reads .env for proxy.
            .Replace("{proxy}",    "");
    }

    /// <summary>
    /// The core spawn-and-stream routine. Mirrors <see cref="AccioToolDriver.RunAccioAsync"/>
    /// so the UX (Live Log, success/failure classification) is identical.
    /// </summary>
    private async Task<DriverResult> RunPythonAsync(
        string script, List<string> args, string[] stdinLines,
        string workDir, TimeSpan timeout,
        Action<string>? liveLog, CancellationToken ct,
        IReadOnlyDictionary<string, string>? envOverrides = null)
    {
        var python = ResolvePython(Settings);

        var psi = new ProcessStartInfo
        {
            FileName               = python,
            WorkingDirectory       = workDir,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            RedirectStandardInput  = true,
            CreateNoWindow         = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding  = Encoding.UTF8
        };

        // Per-run mail keys go into THIS process's environment as well as the
        // shared .env - that is what lets several create runs overlap without
        // reading each other's SIGNUP_EMAIL (panel_mail.load_env() lets
        // os.environ win over the file).
        if (envOverrides is not null)
        {
            foreach (var kv in envOverrides)
            {
                if (string.IsNullOrWhiteSpace(kv.Key)) continue;
                psi.Environment[kv.Key] = kv.Value ?? "";
            }
        }

        // Tell the tool where to keep its account-related files: beside the exe
        // in accounts\<tool>\, NOT in the folder holding the tool's source. That
        // is what lets the application folder be copied, updated or shipped
        // without carrying anybody's accounts, cookies or mailbox password with
        // it. The tool falls back to its own folder when this is absent, so a
        // manual run outside the panel is unaffected.
        AppPaths.EnsureToolAccountsDir(ToolId);
        psi.Environment["PANEL_TOOL_STATE_DIR"] = AppPaths.ToolAccountsDir(ToolId);

        // Point Playwright at the browser build that shipped with the bundle.
        // Nothing ever did this before, so the 700 MB under runtime\ was dead
        // weight: Playwright only looks at PLAYWRIGHT_BROWSERS_PATH and then
        // %LOCALAPPDATA%\ms-playwright, never beside the .exe.
        foreach (var kv in AppPaths.RuntimeEnvironment())
        {
            psi.Environment[kv.Key] = kv.Value;
        }

        // -u (unbuffered) MUST come before the script path.  Python
        // block-buffers stdout whenever it is not attached to a console, and our
        // pipe is exactly that: without -u the child wrote nothing until it
        // exited, so the LIVE LOG showed the "$ python ..." line and then sat
        // silent for the whole run - it looked like the script had hung.
        psi.ArgumentList.Add("-u");

        // Belt & braces for anything the script shells out to.
        psi.Environment["PYTHONUNBUFFERED"] = "1";

        // The script path MUST be the first positional argument, otherwise
        // python.exe interprets our template flags (e.g. --count) as its own
        // options and prints "unknown option --count" + the usage line.
        psi.ArgumentList.Add(script);
        foreach (var a in args) psi.ArgumentList.Add(a);

        var argLine = string.Join(' ', args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
        Log.Info(ToolId, $"  $ python {Path.GetFileName(script)} {argLine}");
        liveLog?.Invoke($"$ python {Path.GetFileName(script)} {argLine}");

        // Wipe the in-memory account cache so any new row is the script's, not
        // a stale one we held from before.
        AccountStore.Refresh(ToolId);

        Process proc;
        try
        {
            proc = Process.Start(psi)!;
            // Tie it to the panel: closing (or killing) the panel must take this
            // run and the browser it opens with it.
            ProcessGuard.Adopt(proc);
        }
        catch (Exception ex)
        {
            return DriverResult.Fail($"Could not launch python.exe: {ex.Message}");
        }

        // Pipe stdin (the script may read with input(); closing EOF lets it
        // exit its read loop after we've fed the values it needs).
        try
        {
            foreach (var line in stdinLines)
            {
                await proc.StandardInput.WriteLineAsync(line).ConfigureAwait(false);
            }
            await proc.StandardInput.FlushAsync().ConfigureAwait(false);
            proc.StandardInput.Close();
        }
        catch { /* the script may have already exited; ignore */ }

        var stdoutLines = new List<string>(128);
        var stderrLines = new List<string>(16);

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stdoutLines.Add(e.Data);
            Log.Info(ToolId, "  | " + e.Data);
            liveLog?.Invoke(e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stderrLines.Add(e.Data);
            Log.Warn(ToolId, "  ! " + e.Data);
            liveLog?.Invoke("[err] " + e.Data);
        };

        // Process.Start(psi) already started the process - just begin the
        // async stdout/stderr pumps.
        try
        {
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            return DriverResult.Fail($"python.exe stream pumps failed: {ex.Message}");
        }

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            await proc.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
            return DriverResult.Fail(
                timeoutCts.IsCancellationRequested
                    ? $"python timed out after {timeout.TotalMinutes:0} min."
                    : "python run cancelled.");
        }

        var allLines = stdoutLines.Concat(stderrLines).ToArray();
        var joined = string.Join('\n', allLines);

        bool success = proc.ExitCode == 0 &&
                       SuccessMarkers.Any(m => joined.Contains(m, StringComparison.OrdinalIgnoreCase));
        bool hardFail = FailureMarkers.Any(m => joined.Contains(m, StringComparison.OrdinalIgnoreCase));

        if (success && !hardFail)
        {
            Log.Success(ToolId, $"python finished OK (exit {proc.ExitCode}).");
            AccountStore.Refresh(ToolId);
            return DriverResult.Success(
                $"Python script finished OK (exit {proc.ExitCode}).  Refresh to see new accounts.");
        }

        var kind = Classify(joined);
        var tail = allLines
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .TakeLast(8)
            .Select(l => "  · " + l.Trim());

        var msg = $"python returned {proc.ExitCode}  ({kind}).  Fix: {FixHint(kind)}";
        if (tail.Any())
            msg += Environment.NewLine + string.Join(Environment.NewLine, tail);

        Log.Error(ToolId, msg);
        return DriverResult.Fail(msg);
    }

    /// <summary>Resolve the python interpreter: explicit path → PATH → "py -3".</summary>
    /// <summary>
    /// The interpreter to run a tool's scripts with, looked up by tool id.
    /// Shared with <see cref="AccioToolDriver"/>, which falls back to running
    /// accio's <c>create.py</c> when the operator has no prebuilt Accio.exe.
    /// </summary>
    internal static string ResolvePythonFor(string toolId) =>
        ResolvePython(ToolSettingsStore.For(toolId));

    private static string ResolvePython(ToolSettings s)
    {
        if (!string.IsNullOrWhiteSpace(s.PythonExePath) && File.Exists(s.PythonExePath))
            return s.PythonExePath;

        if (PythonOnPath("python"))    return "python";
        if (PythonOnPath("python3"))   return "python3";
        if (PythonOnPath("py"))        return "py";
        return "python";   // fall through; will surface a clear "not found" error
    }

    private static bool PythonOnPath(string exe = "python")
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "--version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            return p.WaitForExit(3000) && p.ExitCode == 0;
        }
        catch { return false; }
    }

    // ------------------------------ error classification ------------------------------

    private enum FailureKind
    {
        Unknown,
        TimeoutNetwork,
        NoPython,
        ScriptMissing,
        ImapAuth,
        Captcha,
        OtpTimeout,
        AlreadyExists
    }

    private static FailureKind Classify(string joined)
    {
        if (joined.Contains("'python' is not recognized",  StringComparison.OrdinalIgnoreCase) ||
            joined.Contains("No such file or directory",   StringComparison.OrdinalIgnoreCase) ||
            joined.Contains("python.exe",                  StringComparison.OrdinalIgnoreCase))
            return FailureKind.NoPython;
        if (joined.Contains("ScriptNotFound",               StringComparison.OrdinalIgnoreCase) ||
            joined.Contains("Create script not found",      StringComparison.OrdinalIgnoreCase))
            return FailureKind.ScriptMissing;
        if (joined.Contains("ERR_TIMED_OUT",                StringComparison.OrdinalIgnoreCase))
            return FailureKind.TimeoutNetwork;
        if (joined.Contains("IMAP",                         StringComparison.OrdinalIgnoreCase) &&
            joined.Contains("auth",                         StringComparison.OrdinalIgnoreCase))
            return FailureKind.ImapAuth;
        if (joined.Contains("Captcha",                      StringComparison.OrdinalIgnoreCase) ||
            joined.Contains("verification",                 StringComparison.OrdinalIgnoreCase))
            return FailureKind.Captcha;
        if (joined.Contains("Verify link not received",     StringComparison.OrdinalIgnoreCase) ||
            joined.Contains("OTP timeout",                  StringComparison.OrdinalIgnoreCase))
            return FailureKind.OtpTimeout;
        if (joined.Contains("already exists",               StringComparison.OrdinalIgnoreCase))
            return FailureKind.AlreadyExists;
        return FailureKind.Unknown;
    }

    private static string FixHint(FailureKind k) => k switch
    {
        FailureKind.NoPython    => "python.exe not on PATH. Set PythonExePath in Global Settings → Tool Paths.",
        FailureKind.ScriptMissing => "create/login script path is wrong or file is missing. Open Global Settings → Tool Paths.",
        FailureKind.TimeoutNetwork => "network unreachable. Check VPN / proxy.",
        FailureKind.ImapAuth    => "IMAP auth failed. Verify GMAIL_USER + GMAIL_APP_PASSWORD in the script's .env.",
        FailureKind.Captcha     => "captcha wall. Solve in the browser, retry.",
        FailureKind.OtpTimeout  => "OTP did not arrive in time. Check Gmail + app password.",
        FailureKind.AlreadyExists => "address already used. Wipe accounts.txt or pick another Gmail.",
        _ => "see the script's stdout above for the exact error."
    };

    /// <summary>
    /// Convenience overload used by the panel view: returns the resolved python
    /// path so the Settings tab can show "Python: C:\Python311\python.exe".
    /// </summary>
    public string ResolvedPythonPath() => ResolvePython(Settings);
}
