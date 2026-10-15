using System.IO;
using System.Text;
using System.Text.Json;

namespace AISuitePanel;

/// <summary>
/// Every file the panel owns lives under one folder. Deleting
/// <c>%APPDATA%\AISuitePanel</c> resets the app to a fresh, unactivated state.
/// </summary>
public static class AppPaths
{
    /// <summary>
    /// The folder the running program sits in.
    ///
    /// Everything portable is resolved against this, so the same project runs
    /// from <c>D:\Script Panel</c>, from a USB stick, or from a customer's
    /// Desktop without a single path being rewritten. For a single-file build
    /// this is the folder holding the .exe.
    /// </summary>
    public static string AppRoot
    {
        get
        {
            try
            {
                var dir = AppContext.BaseDirectory;
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    return dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                }
            }
            catch
            {
                // fall through
            }
            return Directory.GetCurrentDirectory();
        }
    }

    /// <summary>
    /// The folder holding the panel's payload: the tools, the browser runtime
    /// and the accounts folder, all together.
    ///
    /// <para>Normally the folder holding the .exe. The panel's own build drops
    /// the .exe into <c>output\</c> though, and <c>output\</c> has no
    /// <c>tools\</c> of its own - the tools live one level up, in the project
    /// folder - so in that layout the payload root is taken from beside the
    /// <c>tools\</c> folder actually in use. Both layouts then resolve to ONE
    /// place, instead of the panel looking in <c>output\tools</c> while the
    /// tools sit in the project's <c>tools\</c> (which is exactly what made
    /// every tool fail to launch: the configured relative path resolved against
    /// the .exe's folder, where there is no tools\ folder at all).
    /// </summary>
    public static string AppFolderRoot
    {
        get
        {
            // The exe's own folder first (a normal install and output\ both land
            // here), then everything else that can identify the payload with
            // certainty - see PayloadRootCandidates. Returning the first folder
            // that actually holds a tools\ folder is what keeps the panel's
            // accounts folder, the tools and the runtime pointing at ONE place.
            try
            {
                foreach (var candidate in PayloadRootCandidates())
                {
                    if (!HasTools(candidate)) continue;
                    RememberPayloadRoot(candidate);
                    return candidate;
                }
            }
            catch
            {
                // Nothing resolvable: fall through to the exe's own folder.
            }

            return AppRoot;
        }
    }

    /// <summary>True when <paramref name="dir"/> actually holds a <c>tools\</c> folder.</summary>
    private static bool HasTools(string dir)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(dir) &&
                   Directory.Exists(Path.Combine(dir, "tools"));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Every folder that could be the payload root, best first.
    ///
    /// <para>The first two cover the layout the panel ships in: the .exe beside
    /// <c>tools\</c>, or one level under it (<c>output\</c>). The working
    /// directory covers a shortcut that sets one.</para>
    ///
    /// <para>The last two matter for a build that gets UNPACKED somewhere else
    /// before it runs - a single-file release started by a sandboxed launcher
    /// runs as a temp copy whose folder (and whose parent) hold no <c>tools\</c>
    /// at all. In that case <see cref="AppContext.BaseDirectory"/> is worthless,
    /// and the panel used to conclude "no payload" - i.e. it silently switched to
    /// its own private workspace store, showing old rows and deleting nothing.
    /// So the payload root is also recovered from the tools' own configured paths
    /// (absolute, and already proven to work, because the panel launches the
    /// tools with them) and from the last root a good run resolved.</para>
    /// </summary>
    private static IEnumerable<string> PayloadRootCandidates()
    {
        yield return AppRoot;
        yield return Path.GetDirectoryName(AppRoot) ?? "";
        yield return CurrentDirectory();

        var remembered = RememberedRoot();
        if (remembered.Length > 0) yield return remembered;

        foreach (var candidate in ConfiguredRoots()) yield return candidate;
    }

    private static string CurrentDirectory()
    {
        try { return Directory.GetCurrentDirectory(); }
        catch { return ""; }
    }

    /// <summary>
    /// The payload roots suggested by the tools' configured paths. Read straight
    /// from <c>tool-settings.json</c>: going through
    /// <see cref="ToolSettingsStore"/> would come back into this property
    /// (Resolve → ToolsRoot → AppFolderRoot) and recurse.
    /// </summary>
    private static IEnumerable<string> ConfiguredRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in ConfiguredToolDirectories())
        {
            var current = dir;
            for (var depth = 0; depth < 4 && !string.IsNullOrWhiteSpace(current); depth++)
            {
                if (seen.Add(current)) yield return current;
                current = Path.GetDirectoryName(current) ?? "";
            }
        }
    }

    private static IEnumerable<string> ConfiguredToolDirectories()
    {
        var results = new List<string>();

        try
        {
            var path = ToolSettings;
            if (!File.Exists(path)) return results;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return results;

            foreach (var tool in doc.RootElement.EnumerateObject())
            {
                if (tool.Value.ValueKind != JsonValueKind.Object) continue;

                foreach (var key in new[]
                         {
                             "toolWorkingDirectory", "createScriptPath",
                             "loginScriptPath", "pythonExePath"
                         })
                {
                    if (!tool.Value.TryGetProperty(key, out var value)) continue;
                    if (value.ValueKind != JsonValueKind.String) continue;

                    var raw = value.GetString();
                    if (string.IsNullOrWhiteSpace(raw)) continue;

                    var full = raw!;
                    try { full = Path.GetFullPath(raw!); } catch { /* keep it as written */ }

                    var dir = File.Exists(full)
                        ? Path.GetDirectoryName(full) ?? ""
                        : full;

                    if (dir.Length > 0) results.Add(dir);
                }
            }
        }
        catch
        {
            // A missing / unreadable settings file simply suggests no folders.
        }

        return results;
    }

    // ------------------------------------------------------- remembered root

    private static string? _rememberedRoot;

    private static string RememberedRootFile => Path.Combine(Root, "app-root.txt");

    /// <summary>The payload root the last good run resolved to ("" when none).</summary>
    private static string RememberedRoot()
    {
        if (_rememberedRoot is not null) return _rememberedRoot;

        try
        {
            var file = RememberedRootFile;
            _rememberedRoot = File.Exists(file) ? File.ReadAllText(file).Trim() : "";
        }
        catch
        {
            _rememberedRoot = "";
        }

        return _rememberedRoot;
    }

    /// <summary>
    /// Remembers a root that really held the payload, so a later launch from an
    /// unexpected place still finds the same accounts folder. Never throws - a
    /// read-only profile only means nothing is remembered.
    /// </summary>
    private static void RememberPayloadRoot(string root)
    {
        try
        {
            if (string.Equals(_rememberedRoot, root, StringComparison.OrdinalIgnoreCase)) return;

            Directory.CreateDirectory(Root);
            File.WriteAllText(RememberedRootFile, root, new UTF8Encoding(false));
            _rememberedRoot = root;
        }
        catch
        {
            // best effort only
        }
    }

    /// <summary>The bundled tools, one folder per tool id: <c>tools\zai\</c>.</summary>
    public static string ToolsRoot => Path.Combine(AppFolderRoot, "tools");

    /// <summary>Bundled runtime: <c>runtime\ms-playwright\</c>, <c>runtime\python\</c>.</summary>
    public static string RuntimeRoot => Path.Combine(AppFolderRoot, "runtime");

    /// <summary>
    /// The one folder account data lives in: beside the exe, in the same payload
    /// folder as <c>tools\</c> and <c>runtime\</c>. See
    /// <see cref="AppFolderRoot"/> for why that is not simply
    /// <c>AppRoot\accounts</c>.
    /// </summary>
    public static string AccountsRoot => Path.Combine(AppFolderRoot, "accounts");

    /// <summary>
    /// The accounts folder each tool uses, keyed by tool id.
    ///
    /// <para>Mostly the tool id, but NOT always: the MiniMax box is registered as
    /// <c>tool4</c>, so a folder named after the id would be <c>accounts\tool4</c>
    /// while everything - the migrated accounts, the runs made so far - lives in
    /// <c>accounts\minimax</c>. That mismatch hid MiniMax's accounts from the
    /// panel and, worse, would have split its data across two folders as soon as
    /// the panel ran the tool. The name here is the tool's own folder under
    /// <c>tools\</c>, which is what the accounts folder is meant to mirror.
    /// </summary>
    private static readonly Dictionary<string, string> AccountsFolderByTool =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["accio"] = "accio",
            ["zai"] = "zai",
            ["devin"] = "devin",
            ["tool4"] = "minimax",
        };

    /// <summary>
    /// Where one tool keeps its account-related files: <c>accounts\&lt;tool&gt;\</c>
    /// beside the executable.
    ///
    /// <para>This is the whole point of the layout. Accounts, session cookies,
    /// usage counters, claims and the mail <c>.env</c> used to be written into
    /// whichever folder happened to hold the tool's source - i.e. inside the
    /// application folder, next to the code. Copying or shipping that folder
    /// therefore carried somebody's accounts and live cookies with it, and
    /// replacing the folder to apply an update deleted them.
    ///
    /// <para>Now there is ONE place for account data, beside the exe instead of
    /// inside it, and the application folder holds nothing but the program. The
    /// path is handed to each tool in PANEL_TOOL_STATE_DIR.
    /// </summary>
    public static string ToolAccountsDir(string toolId)
    {
        var folder = AccountsFolderByTool.TryGetValue(toolId, out var mapped)
            ? mapped
            : Safe(toolId);
        return Path.Combine(AccountsRoot, folder);
    }

    /// <summary>Creates one tool's accounts folder. Safe to call repeatedly.</summary>
    public static void EnsureToolAccountsDir(string toolId)
    {
        try
        {
            Directory.CreateDirectory(ToolAccountsDir(toolId));
        }
        catch
        {
            // A tool that cannot create its folder still falls back to keeping
            // its files beside its own script, so this must never be fatal.
        }
    }

    /// <summary>
    /// The browser build that shipped inside the bundle, or "" when there is
    /// none.
    ///
    /// <para>Playwright looks for browsers in <c>PLAYWRIGHT_BROWSERS_PATH</c>
    /// and, failing that, in <c>%LOCALAPPDATA%\ms-playwright</c> — it never
    /// looks beside the application.  The portable bundle ships
    /// <c>runtime\ms-playwright</c>, but nothing ever pointed Playwright at it,
    /// so on a machine that had never run <c>playwright install</c> those 700 MB
    /// were invisible and every launch died with "Executable doesn't exist".
    /// Exporting this path to child processes is what makes the bundled browser
    /// the one that actually gets used.</para>
    /// </summary>
    public static string BundledBrowsersPath
    {
        get
        {
            try
            {
                var dir = Path.Combine(RuntimeRoot, "ms-playwright");
                if (Directory.Exists(dir))
                {
                    return dir;
                }
            }
            catch
            {
                // A locked or missing runtime just means "no bundled browser".
            }
            return "";
        }
    }

    /// <summary>
    /// Environment additions every tool process needs. Only non-empty values
    /// are returned, so a development copy with no bundle is left exactly as it
    /// was and keeps using whatever Playwright already had.
    /// </summary>
    public static IEnumerable<KeyValuePair<string, string>> RuntimeEnvironment()
    {
        // Deliberately tied to Portable: on the operator's own machine the
        // runtime folder may exist without being a complete browser set, and
        // there is no reason to override a working %LOCALAPPDATA% install.
        if (!Portable)
        {
            yield break;
        }

        var browsers = BundledBrowsersPath;
        if (browsers.Length > 0)
        {
            yield return new KeyValuePair<string, string>("PLAYWRIGHT_BROWSERS_PATH", browsers);
        }
    }

    /// <summary>
    /// True when the panel should keep its own files beside the .exe instead of
    /// in <c>%APPDATA%</c>.
    ///
    /// Switched on by dropping <c>portable.txt</c> next to the .exe - which is
    /// exactly what the shipping bundle does. A development copy, which has no
    /// such file, keeps using %APPDATA% and is not disturbed.
    /// </summary>
    public static bool Portable
    {
        get
        {
            try
            {
                return File.Exists(Path.Combine(AppRoot, "portable.txt"))
                       || Directory.Exists(Path.Combine(AppRoot, "data"));
            }
            catch
            {
                return false;
            }
        }
    }

    public static string Root => Portable
        ? Path.Combine(AppRoot, "data")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                       "AISuitePanel");

    /// <summary>
    /// Resolves a path from tool-settings.json.
    ///
    /// The configured path wins whenever it is actually there, so a development
    /// machine goes on using the folders it already has and nothing moves under
    /// it. The moment it is *not* there - which is what happens as soon as the
    /// project is copied to another drive, or handed to a customer with a
    /// different layout - the same file is looked for beside the .exe: as
    /// written, then under <c>tools\&lt;toolId&gt;\</c>. That is what lets one folder
    /// work from any drive letter without re-configuring every tool by hand.
    /// </summary>
    public static string Resolve(string? configured, string? toolId = null)
    {
        var value = (configured ?? "").Trim();
        if (value.Length == 0)
        {
            return "";
        }

        if (File.Exists(value) || Directory.Exists(value))
        {
            return value;                       // what the operator configured
        }

        var leaf = Path.GetFileName(value);
        var tries = new List<string>();
        if (!Path.IsPathRooted(value))
        {
            tries.Add(Path.Combine(AppRoot, value));
        }
        if (leaf.Length > 0)
        {
            if (toolId is { Length: > 0 })
            {
                tries.Add(Path.Combine(ToolsRoot, Tool(toolId), leaf));
            }
            tries.Add(Path.Combine(ToolsRoot, leaf));
        }

        foreach (var candidate in tries)
        {
            try
            {
                if (File.Exists(candidate) || Directory.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // keep looking
            }
        }

        return value;                           // nothing matched: leave it alone
    }

    /// <summary>
    /// The interpreter to run a tool's scripts with: the configured one when it
    /// exists, otherwise the Python bundled beside the .exe.
    /// </summary>
    public static string ResolvePython(string? configured)
    {
        var value = Resolve(configured);
        if (value.Length > 0 && File.Exists(value))
        {
            return value;
        }

        foreach (var candidate in new[]
                 {
                     Path.Combine(RuntimeRoot, "python", "python.exe"),
                     Path.Combine(ToolsRoot, "python", "python.exe"),
                 })
        {
            try
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // keep looking
            }
        }

        return value;
    }

    /// <summary>Tool folder names are lower-case alphanumerics, matching workspaces.</summary>
    private static string Tool(string id) => new string(id.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    public static string License => Path.Combine(Root, "license.json");
    public static string LegacyLicense => Path.Combine(Root, "license.dat");
    public static string ToolLicenses => Path.Combine(Root, "tool-licenses.json");

    /// <summary>
    /// Editable product registry (master product + per-tool product slugs).
    /// Written on first run so a product added on the licence server can be
    /// used without rebuilding the panel.
    /// </summary>
    public static string Products => Path.Combine(Root, "products.json");

    /// <summary>
    /// Editable dashboard tool list. Written on first run so a new tool box can
    /// be added without rebuilding the panel.
    /// </summary>
    public static string Tools => Path.Combine(Root, "tools.json");
    public static string Settings => Path.Combine(Root, "settings.json");
    public static string ToolSettings => Path.Combine(Root, "tool-settings.json");
    public static string UsedAliases => Path.Combine(Root, "used-aliases.json");
    public static string AccountsDir => Path.Combine(Root, "accounts");
    public static string CookiesDir => Path.Combine(Root, "cookies");
    public static string LogsDir => Path.Combine(Root, "logs");
    public static string WorkspacesDir => Path.Combine(Root, "workspaces");
    public static string CrashLog => Path.Combine(Root, "crash.log");

    /// <summary>Root of one tool's completely separate local workspace.</summary>
    public static string Workspace(string toolId) =>
        Path.Combine(WorkspacesDir, Safe(toolId));

    public static string WorkspaceAccountsDir(string toolId) =>
        Path.Combine(Workspace(toolId), "accounts");

    public static string WorkspaceCookiesDir(string toolId) =>
        Path.Combine(Workspace(toolId), "cookies");

    public static string WorkspaceProfilesDir(string toolId) =>
        Path.Combine(Workspace(toolId), "profiles");

    public static string WorkspaceLogsDir(string toolId) =>
        Path.Combine(Workspace(toolId), "logs");

    /// <summary>Per-tool account file inside its isolated workspace.</summary>
    public static string AccountsFile(string toolId) =>
        Path.Combine(WorkspaceAccountsDir(toolId), "accounts.json");

    /// <summary>Per-tool cookie vault file inside its isolated workspace.</summary>
    public static string CookiesFile(string toolId) =>
        Path.Combine(WorkspaceCookiesDir(toolId), "cookies.json");

    /// <summary>Legacy locations retained as read-only migration fallbacks.</summary>
    public static string LegacyAccountsFile(string toolId) =>
        Path.Combine(AccountsDir, Safe(toolId) + ".json");

    public static string LegacyCookiesFile(string toolId) =>
        Path.Combine(CookiesDir, Safe(toolId) + ".json");

    public static string TodayLog => Path.Combine(LogsDir, DateTime.Now.ToString("yyyy-MM-dd") + ".log");

    public static void Ensure()
    {
        try
        {
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(AccountsDir);
            Directory.CreateDirectory(CookiesDir);
            Directory.CreateDirectory(LogsDir);
            Directory.CreateDirectory(WorkspacesDir);
        }
        catch
        {
            // If the profile is not writable the panel still runs, it just forgets.
        }
    }

    /// <summary>
    /// Creates one tool's isolated workspace folders. Call this for every tool
    /// the catalogue knows about - the list is registry driven now, so it is not
    /// hard-coded here any more.
    /// </summary>
    public static void EnsureWorkspace(string toolId)
    {
        try
        {
            Directory.CreateDirectory(WorkspaceAccountsDir(toolId));
            Directory.CreateDirectory(WorkspaceCookiesDir(toolId));
            Directory.CreateDirectory(WorkspaceProfilesDir(toolId));
            Directory.CreateDirectory(WorkspaceLogsDir(toolId));
        }
        catch
        {
            // Same best-effort contract as Ensure().
        }
    }

    private static string Safe(string id) =>
        new string(id.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
}
