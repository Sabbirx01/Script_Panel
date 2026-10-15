using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AISuitePanel.Models;

namespace AISuitePanel.Automation;

/// <summary>
/// Real driver for the Accio AI tool. Phase 3 decision: the panel no longer
/// implements its own browser automation - instead it shells out to the
/// pre-existing <c>Accio.exe</c> (a self-contained Python+Playwright bundle)
/// with the CLI flags that match the user's choice of mail source. Every flag
/// the panel knows about maps to one of Accio.exe's documented modes, so the
/// same Accio project files (<c>alice.json</c>, <c>accounts/accounts.json</c>,
/// <c>profiles/</c>, <c>_temp/</c>) stay the single source of truth.
///
/// Why subprocess instead of a fresh native Playwright reimplementation:
///   * Accio.exe already implements the whole OTP / IMAP / i18n flow correctly
///     against accio.com (verified by 3 successful accounts in the user's data).
///   * Re-doing the 150 KB Python in C# is the wrong short-term trade-off.
///   * The "Accio AI" tool still appears to do real work from the panel's UI -
///     the user drives the same project, the panel just becomes the cockpit.
/// </summary>
public sealed class AccioToolDriver : IToolDriver
{
    /// <summary>Tool id this driver serves - must match <c>ToolCatalog</c>.</summary>
    public const string ToolKey = "accio";

    /// <summary>
    /// Last-resort root: where the kit lives on the developer's own machine.
    /// Only reached when nothing else resolves, so an existing dev setup keeps
    /// working exactly as before.
    /// </summary>
    private const string DevRoot =
        @"D:\Bulk Automation Script\Accio\Accio_Complete\Test Folder\accio\Accio_Portable_v1.8";

    /// <summary>
    /// Where accio lives - resolved at run time, not a constant.
    ///
    /// <para>This used to be one absolute path on the developer's own PC, which
    /// meant the shipped bundle could never run accio at all: on a customer
    /// machine that folder does not exist. The order is now the path configured
    /// for this tool in Global Settings → Tool Paths first, then the folder the
    /// bundle ships, then that project's <c>source</c> sub-folder - and only
    /// then the old development path.</para>
    /// </summary>
    public static string AccioRoot
    {
        get
        {
            var configured = ToolSettingsStore.For(ToolKey).ToolWorkingDirectory;
            var resolved = AppPaths.Resolve(configured, ToolKey);
            if (!string.IsNullOrWhiteSpace(resolved) && Entry(resolved).Length > 0)
            {
                return resolved;
            }

            foreach (var candidate in new[]
                     {
                         Path.Combine(AppPaths.ToolsRoot, "accio", "source"),
                         Path.Combine(AppPaths.ToolsRoot, "accio"),
                         DevRoot,
                     })
            {
                try
                {
                    if (Entry(candidate).Length > 0)
                    {
                        return candidate;
                    }
                }
                catch
                {
                    // an unreadable candidate is simply not the one
                }
            }

            return DevRoot;
        }
    }

    /// <summary>
    /// What to launch inside a candidate root: a prebuilt <c>Accio.exe</c> when
    /// the operator keeps one, otherwise the Python source the bundle ships
    /// (<c>create.py</c>). Empty when neither is there.
    /// </summary>
    private static string Entry(string rootDir)
    {
        try
        {
            var exe = Path.Combine(rootDir, "Accio.exe");
            if (File.Exists(exe)) return exe;
            var script = Path.Combine(rootDir, "create.py");
            if (File.Exists(script)) return script;
        }
        catch
        {
            // a missing / unreadable folder is not an entry point
        }
        return "";
    }

    /// <summary>Full path to whichever entry point accio resolves to.</summary>
    public static string AccioExePath => Entry(AccioRoot);

    private static string AccountsJson(string rootDir) =>
        Path.Combine(rootDir, "accounts", "accounts.json");

    /// <summary>What "done" looks like in Accio.exe's stdout (BAT [1] happy path).</summary>
    private static readonly string[] SuccessMarkers =
    {
        "dashboard: OK",
        "account OK",
        "[SUCCESS]",
        "account created"
    };

    /// <summary>Markers that always mean the run failed (whichever mode).</summary>
    private static readonly string[] FailureMarkers =
    {
        "account FAILED",
        "[ERROR]",
        "Traceback (most recent call last)",
        // A bare "Playwright" used to be listed here, and that was a real bug:
        // accio logs "StealthBot fail (...), using Playwright fallback" on every
        // single run (CloakBrowser is not installed), so every run - including
        // one that created the account perfectly - matched a failure marker and
        // was reported as failed. Match the actual launch errors instead.
        "browserType.launch:",
        "Executable doesn't exist",          // missing Chromium
        "Driver not found",                  // missing Node.js driver
        "Target page, context or browser has been closed",
        "Captcha required",                  // captcha wall (treat as failure)
        "OTP timeout",
        "Login failed",
        "Aborted"
    };

    /// <summary>More specific failure classes — used to give the user a one-line fix hint.</summary>
    private enum FailureKind
    {
        Unknown,
        TimeoutNetwork,    // ERR_TIMED_OUT / can't reach accio.com
        NoProxy,           // proxy list empty + no .env provider
        Captcha,
        OtpTimeout,
        BrowserLaunch,
        OtpReadFailure,
        ImapAuth,
        AlreadyExists
    }

    private static FailureKind Classify(string joined)
    {
        if (joined.Contains("ERR_TIMED_OUT",          StringComparison.OrdinalIgnoreCase)) return FailureKind.TimeoutNetwork;
        if (joined.Contains("ERR_CONNECTION_RESET",   StringComparison.OrdinalIgnoreCase)) return FailureKind.TimeoutNetwork;
        if (joined.Contains("ERR_PROXY_CONNECTION",   StringComparison.OrdinalIgnoreCase)) return FailureKind.NoProxy;
        if (joined.Contains("ERR_TUNNEL_CONNECTION",   StringComparison.OrdinalIgnoreCase)) return FailureKind.NoProxy;
        if (joined.Contains("proxy list is dead",      StringComparison.OrdinalIgnoreCase)) return FailureKind.NoProxy;
        if (joined.Contains("no working proxy",        StringComparison.OrdinalIgnoreCase)) return FailureKind.NoProxy;
        if (joined.Contains("Captcha required",        StringComparison.OrdinalIgnoreCase)) return FailureKind.Captcha;
        if (joined.Contains("OTP timeout",             StringComparison.OrdinalIgnoreCase)) return FailureKind.OtpTimeout;
        if (joined.Contains("Executable doesn't exist",StringComparison.OrdinalIgnoreCase)) return FailureKind.BrowserLaunch;
        if (joined.Contains("Driver not found",        StringComparison.OrdinalIgnoreCase)) return FailureKind.BrowserLaunch;
        if (joined.Contains("IMAP",                    StringComparison.OrdinalIgnoreCase) &&
            joined.Contains("auth",                    StringComparison.OrdinalIgnoreCase)) return FailureKind.ImapAuth;
        if (joined.Contains("already exists",          StringComparison.OrdinalIgnoreCase)) return FailureKind.AlreadyExists;
        return FailureKind.Unknown;
    }

    private static string FixHint(FailureKind k) => k switch
    {
        FailureKind.TimeoutNetwork  => "network unreachable. Add ACCIO_PROXY_API to .env or check VPN.",
        FailureKind.NoProxy         => "no working proxy. Add ACCIO_PROXY_API (or ACCIO_PROXY_SOURCES) to .env.",
        FailureKind.Captcha         => "captcha wall. Pause the run, solve in the browser, retry.",
        FailureKind.OtpTimeout      => "OTP did not arrive in time. Check the inbox Gmail + app password.",
        FailureKind.BrowserLaunch   => "Playwright/Chromium missing. Run: pip install --upgrade cloakbrowser.",
        FailureKind.ImapAuth        => "IMAP auth failed. Re-check Gmail app password in .env.",
        FailureKind.AlreadyExists   => "address already used. Clear used-aliases or pick another source.",
        _                            => "see Accio.exe stdout above for the exact error."
    };

    public string ToolId => ToolKey;

    // ---------------------------------------------------------------- create

    /// <summary>
    /// Static entry point used by <c>AccioPanelView</c>'s own runner: lets the
    /// caller pipe every stdout line into a per-tool Live Log box in addition to
    /// the static activity log.
    /// </summary>
    public static Task<DriverResult> CreateDirectAsync(
        string email, MailSource source, Action<string>? liveLog, CancellationToken ct,
        bool forceNoProxy = false, bool xTrick = true, string? createUrl = null)
    {
        var driver = new AccioToolDriver();
        return driver.CreateAccountAsyncInternal(email, source, liveLog, ct, forceNoProxy, xTrick, createUrl);
    }

    // IToolDriver entry point - the panel uses CreateDirectAsync instead of this.
    public Task<DriverResult> CreateAccountAsync(
        string email, MailSource source, CancellationToken ct) =>
        CreateAccountAsyncInternal(email, source, liveLog: null, ct,
            forceNoProxy: false, xTrick: true, createUrl: null);

    private async Task<DriverResult> CreateAccountAsyncInternal(
        string email, MailSource source, Action<string>? liveLog,
        CancellationToken ct, bool forceNoProxy, bool xTrick, string? createUrl = null)
    {
        if (string.IsNullOrWhiteSpace(email))
            return DriverResult.Fail("No address was supplied by the mail router.");

        var root = AccioRoot;
        var entry = Entry(root);
        if (entry.Length == 0)
        {
            return DriverResult.Fail(
                $"Accio was not found under: {root}  -  set this tool's working " +
                "directory in Global Settings → Tool Paths.");
        }

        // Hand accio the SAME mail the panel just allocated. TrySyncEnv writes
        // the panel's address plus the IMAP credentials of the chosen method
        // into accio's .env and returns them, so the identical values go into
        // this child process. That is what makes Global Mail Settings the single
        // source of truth: accio used to read its own baked-in secret.py, which
        // also had priority over .env and silently overrode whatever the panel
        // had configured.
        var env = PythonToolDriver.TrySyncEnv(
            ToolKey,
            new ToolSettings
            {
                ToolWorkingDirectory = root,
                CreateScriptPath = entry.EndsWith(".py", StringComparison.OrdinalIgnoreCase)
                    ? entry
                    : Path.Combine(root, "create.py"),
            },
            source, email);

        // The same shared list the other tools read, plus this run's ON/OFF state.
        // accio keeps its own proxy pipeline (pre-test + StealthBot rotation) but
        // takes the panel's list as its candidate source, so the Global Settings
        // box is the one place proxies are managed.
        env["PANEL_PROXY_FILE"] = ProxyPool.File;
        env["PANEL_PROXY"] = forceNoProxy ? "off" : "on";
        var timing = ToolSettingsStore.For(ToolKey);
        env["ACCIO_CAPTCHA_WAIT_S"] = Math.Clamp(timing.CaptchaWaitSeconds, 0, 120)
            .ToString(CultureInfo.InvariantCulture);
        env["ACCIO_XTRICK_WAIT_S"] = Math.Clamp(timing.XTrickWaitSeconds, 0, 120)
            .ToString(CultureInfo.InvariantCulture);
        env["ACCIO_ACTION_DELAY_MS"] = Math.Clamp(timing.ActionDelayMs, 0, 120000)
            .ToString(CultureInfo.InvariantCulture);

        var args = BuildArgs(email, source, forceNoProxy, xTrick, createUrl);
        var timeoutForOne = TimeSpan.FromMinutes(8);

        Log.Info(ToolKey, $"{Path.GetFileName(entry)} -- {CreateAccountRunner.SourceLabel(source)}: {email}" +
                          (forceNoProxy ? "  (force --no-proxy)" : "") +
                          (xTrick ? "" : "  (x-trick OFF)"));
        Log.Info(ToolKey, $"  cd \"{root}\"");
        liveLog?.Invoke($"{Path.GetFileName(entry)} -- {CreateAccountRunner.SourceLabel(source)}" +
                        (forceNoProxy ? "  (force --no-proxy)" : ""));

        return await RunAccioAsync(entry, root, email, args, env, timeoutForOne, liveLog, ct)
            .ConfigureAwait(false);
    }

    // ----------------------------------------------------------------- login

    public async Task<DriverResult> LoginWithCookiesAsync(
        string email, IReadOnlyList<BrowserCookie> cookies, CancellationToken ct)
    {
        if (cookies is null || cookies.Count == 0)
            return DriverResult.Fail("No stored cookies to replay.");

        var root = AccioRoot;
        var entry = Entry(root);
        if (entry.Length == 0)
            return DriverResult.Fail($"Accio was not found under: {root}");

        // Bake the cookie set into a file accio recognises (the same cookie file
        // format it writes next to an account). It lives in accio's "accounts"
        // folder - now BESIDE THE EXE (accounts\accio\accounts\), because
        // create.py/login.py read their accounts from PANEL_TOOL_STATE_DIR. The
        // old in-tree location is only used while that folder does not exist.
        var accioAccounts = Path.Combine(AppPaths.ToolAccountsDir(ToolKey), "accounts");
        if (!Directory.Exists(accioAccounts))
            accioAccounts = Path.GetDirectoryName(AccountsJson(root))!;
        try
        {
            Directory.CreateDirectory(accioAccounts);
        }
        catch
        {
            // Falls through to whatever folder could be resolved.
        }
        var cookieFile = Path.Combine(
            accioAccounts,
            $"_login_only_{SanitiseForFileName(email)}.json");

        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(cookies);
            File.WriteAllText(cookieFile, json, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            return DriverResult.Fail("Could not stage cookies to disk: " + ex.Message);
        }

        try
        {
            // Accio itself does not expose a "login-only-with-cookies" flag, so
            // we run --no-wait-url against the login URL with --keep-open: this
            // asks Accio.exe to open the browser, set the cookies we just baked,
            // and let the user / a future Playwright pass do the actual replay.
            var args = new List<string>
            {
                "--no-wait-url",
                "--keep-open",
                "--count", "1",
                "--url", "https://www.accio.com/login",
                "--email-override", email
            };

            return await RunAccioAsync(entry, root, email, args,
                env: null, timeout: TimeSpan.FromMinutes(2),
                liveLog: null, ct: ct).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(cookieFile); } catch { /* ignore */ }
        }
    }

    // --------------------------------------------------------------- internals

    /// <summary>
    /// Maps a panel mail source to the Accio.exe CLI flags. Exactly one path per
    /// source - keep them in sync with the values documented in
    /// <c>Accio_Portable_v1.8/start.bat</c>.
    ///
    /// <para>
    /// ORDER MATTERS. <c>--email-override</c> takes a value, so it must stay
    /// glued to the address that follows it. It used to be inserted at index 0
    /// while the mode / proxy flags were inserted at index 1 - which landed
    /// BETWEEN the option and its value and produced
    /// <c>--email-override --no-proxy worker-0</c>. argparse then aborted with
    /// "argument --email-override: expected one argument", exit code 2, and not
    /// one account was ever created. Every flag is therefore appended now and
    /// the override pair is written LAST, where nothing can split it.
    /// </para>
    /// </summary>
    private static List<string> BuildArgs(
        string email, MailSource source, bool forceNoProxy = false, bool xTrick = true,
        string? createUrl = null)
    {
        // Which page this run signs up on. The Create tab's SIGNUP URL box hands
        // us the operator's own link (their landing page, a referral/campaign
        // URL, another locale); anything that does not pass one - a direct API
        // call, an older caller - gets the tool's configured/default signup
        // page. The URL used to be hard-coded here, so every customer created
        // accounts on accio.com/login and nothing else.
        var signupUrl = ToolSettingsStore.NormaliseLoginUrl(createUrl, ToolSettingsStore.CreateUrl(ToolKey));
        if (string.IsNullOrWhiteSpace(signupUrl))
            signupUrl = "https://www.accio.com/login";

        var args = new List<string>
        {
            "--no-wait-url",
            "--url", signupUrl,
            "--count", "1"
        };

        // The panel owns the mail in every mode: the address it allocated for
        // this run is forced with --email-override, and the IMAP credentials of
        // the chosen method travel in the child environment via TrySyncEnv.
        //
        // --gmail-pool is deliberately NOT used any more. It switches on accio's
        // OWN alice.json pool, and when that pool has entries accio overwrites the
        // mail credentials with its own alice - exactly the "the tool carries its
        // own mail instead of taking it from Global Settings" behaviour this is
        // meant to remove. Leaving the flag off keeps the panel's keys in charge.
        // Nothing is lost: the panel's MailRouter builds the dotted alias from
        // Global Settings and it is that alias which gets forced, so each run
        // still gets a fresh address.
        switch (source)
        {
            case MailSource.DottedGmail:
                break;                          // pool address, forced below

            case MailSource.TempMail:
                // accio takes the address from the panel's own mailbox instead of
                // minting a second inbox - see PanelTempInbox in create.py.
                args.Add("--temp-mail");
                break;

            case MailSource.CustomDomain:
                // accio has no custom-domain mode of its own (it collects the flag
                // into argparse's "unknown" list and silently ignores it), so the
                // catch-all address is forced with --email-override and the
                // forwarded copy is read from CUSTOM_DOMAIN_INBOX over IMAP.
                //
                // --allow-non-gmail is REQUIRED here. create.py refuses a
                // non-Gmail signup address before the browser opens (its
                // Gmail-only guard) - correct for a hand-run, wrong when the
                // operator deliberately picked Method 3: the OTP is still read
                // over IMAP, just from the forward target instead of the pool.
                args.Add("--allow-non-gmail");
                break;

            case MailSource.CustomMail:
                // One fixed Gmail for the next batch - no rotation.
                break;

            case MailSource.SemiAuto:
                // Same pool address, but the user types the OTP themselves.
                args.Add("--manual-otp");
                break;

            case MailSource.NoProxy:
                // Same pool address, but force a direct connection (skip proxy).
                args.Add("--no-proxy");
                break;

            default:
                throw new InvalidOperationException(
                    $"AccioToolDriver does not know how to handle MailSource.{source}");
        }

        // Panel-level override: kill the proxy no matter which mode was picked.
        // Toggle in Create tab calls into this so the user can run direct even
        // when .env still has ACCIO_PROXY_API configured.
        if (forceNoProxy && !args.Contains("--no-proxy"))
        {
            args.Add("--no-proxy");
            Log.Warn(ToolKey, "Proxy toggle is OFF - forcing --no-proxy.");
        }

        // Accio-only switch: create.py's "x-trick" types "<address>x" in the
        // email box, walks through the Change step and removes the trailing x.
        // No other tool has it, so the check box lives on the Accio panel alone.
        if (!xTrick && !args.Contains("--no-x-trick"))
        {
            args.Add("--no-x-trick");
            Log.Info(ToolKey, "x-trick is OFF - appending --no-x-trick.");
        }

        // LAST on purpose - see the order note above.
        if (!string.IsNullOrWhiteSpace(email))
        {
            args.Add("--email-override");
            args.Add(email.Trim());
        }

        return args;
    }

    /// <summary>
    /// Launches accio with the given flags, streams stdout through the activity
    /// log, and reports success/failure based on exit code + parsed markers.
    ///
    /// <para><paramref name="entry"/> is either a prebuilt <c>Accio.exe</c> or the
    /// <c>create.py</c> the bundle ships; a script is run with the panel's Python,
    /// which <see cref="AppPaths.ResolvePython"/> already knows how to find.</para>
    /// </summary>
    private static async Task<DriverResult> RunAccioAsync(
        string entry, string root, string email, List<string> args,
        IReadOnlyDictionary<string, string>? env,
        TimeSpan timeout, Action<string>? liveLog, CancellationToken ct)
    {
        var isScript = entry.EndsWith(".py", StringComparison.OrdinalIgnoreCase);
        var psi = new ProcessStartInfo
        {
            FileName               = isScript ? PythonToolDriver.ResolvePythonFor(ToolKey) : entry,
            WorkingDirectory       = root,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding  = Encoding.UTF8
        };
        // The script has to be the first positional argument, otherwise python
        // reads our flags as its own options ("unknown option --count").
        if (isScript) psi.ArgumentList.Add(entry);
        foreach (var a in args) psi.ArgumentList.Add(a);

        // accio's accounts and cookies go beside the exe in accounts\accio\, not
        // into the folder holding its source - same rule as every other tool, so
        // the application folder can be copied or replaced without carrying
        // accounts with it. create.py/login.py fall back to their own folder
        // when this is absent.
        AppPaths.EnsureToolAccountsDir(ToolKey);
        psi.Environment["PANEL_TOOL_STATE_DIR"] = AppPaths.ToolAccountsDir(ToolKey);

        // The panel's mail keys for this run, then the shared runtime settings
        // (the bundled browser).  panel_mail-style readers let os.environ win
        // over the .env, which is what keeps two overlapping runs apart.
        if (env is not null)
        {
            foreach (var kv in env)
            {
                if (!string.IsNullOrWhiteSpace(kv.Key)) psi.Environment[kv.Key] = kv.Value ?? "";
            }
        }
        foreach (var kv in AppPaths.RuntimeEnvironment())
        {
            psi.Environment[kv.Key] = kv.Value;
        }

        var argLine = string.Join(' ', args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
        var launchLine = (isScript ? "python " + Path.GetFileName(entry) : Path.GetFileName(entry))
                         + " " + argLine;
        Log.Info(ToolKey, $"  $ {launchLine}");
        liveLog?.Invoke($"$ {launchLine}");

        // Wipe in-memory accio accounts so the panel doesn't show a "Pending"
        // row for an address Accio itself might or might not have created.
        AccountStore.Refresh(ToolKey);

        Process proc;
        try
        {
            proc = Process.Start(psi)!;
            // Keep it tied to the panel so a closed panel never leaves this run
            // (or its browser) running in the background.
            ProcessGuard.Adopt(proc);
        }
        catch (Exception ex)
        {
            return DriverResult.Fail($"Could not launch accio: {ex.Message}");
        }

        var stdoutLines = new List<string>(64);
        var stderrLines = new List<string>(16);

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stdoutLines.Add(e.Data);
            Log.Info(ToolKey, "  | " + e.Data);
            liveLog?.Invoke(e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stderrLines.Add(e.Data);
            Log.Warn(ToolKey, "  ! " + e.Data);
            liveLog?.Invoke("[err] " + e.Data);
        };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

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
                    ? $"Accio.exe timed out after {timeout.TotalMinutes:0} min for {email}."
                    : $"Accio.exe cancelled for {email}.");
        }

        var allLines = stdoutLines.Concat(stderrLines).ToArray();
        var joined = string.Join('\n', allLines);

        bool success = proc.ExitCode == 0 &&
                       SuccessMarkers.Any(m => joined.Contains(m, StringComparison.OrdinalIgnoreCase));

        bool hardFail = FailureMarkers.Any(m => joined.Contains(m, StringComparison.OrdinalIgnoreCase));

        if (success && !hardFail)
        {
            Log.Success(ToolKey, $"Accio finished OK for {email} (exit {proc.ExitCode}).");
            // Pull whatever Accio.exe actually wrote to accounts.json so the
            // panel grid reflects the canonical row.
            AccountStore.Refresh(ToolKey);
            return DriverResult.Success(
                $"Accio.exe finished OK for {email}.  Refresh the panel to see the new row.");
        }

        // Diagnose why
        var kind = Classify(joined);
        var tail = allLines
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .TakeLast(8)
            .Select(l => "  · " + l.Trim());

        var msg = $"Accio.exe returned {proc.ExitCode} for {email}  ({kind}).  Fix: {FixHint(kind)}";
        if (tail.Any())
            msg += Environment.NewLine + string.Join(Environment.NewLine, tail);

        Log.Error(ToolKey, msg);
        return DriverResult.Fail(msg);
    }

    private static string SanitiseForFileName(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        return sb.ToString();
    }
}