using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Windows;
using AISuitePanel.Automation;
using AISuitePanel.Models;

namespace AISuitePanel;

/// <summary>
/// Accounts for every tool, held in one live, observable collection per tool so
/// the grid keeps its state when you navigate away and back.
///
/// Storage: one JSON file per tool.
///   * Default tools: <c>%APPDATA%\AISuitePanel\accounts\{toolId}.json</c>
///   * <b>accio</b>: <c>{AccioRoot}/accounts/accounts.json</c> - the existing
///     Accio project's file, shared with <c>Accio.exe</c>. The panel reads it
///     after every run; it never writes the Pending row back (the run itself
///     produces the persisted row on Accio's side).
///
/// Threading: mutate the collection on the UI thread only (the runners do this
/// through the dispatcher). <see cref="Persist"/> is safe from anywhere.
/// </summary>
public static class AccountStore
{
    private static readonly Dictionary<string, ObservableCollection<AccountRecord>> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly object Gate = new();

    public static void LoadAll()
    {
        foreach (var tool in ToolCatalog.Tools) _ = For(tool.Id);
    }

    /// <summary>The live row set for one tool.</summary>
    public static ObservableCollection<AccountRecord> For(string toolId)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(toolId, out var existing)) return existing;

            var stored = Read(toolId);
            var collection = new ObservableCollection<AccountRecord>(stored);
            Cache[toolId] = collection;
            return collection;
        }
    }

    public static int CountFor(string toolId) => For(toolId).Count;

    /// <summary>Next free row number - always 1-based, never reused after a delete.</summary>
    public static int NextId(string toolId)
    {
        var rows = For(toolId);

        // For "accio" the numbering follows whatever Accio owns - we just count
        // the rows we have. This matches the table the user already sees.
        return Math.Max(0, rows.Count) + 1;
    }

    /// <summary>
    /// Adds a new row. For <b>accio</b>, the row is held <i>only</i> in memory
    /// until the next <see cref="Refresh"/>; nothing is written to disk because
    /// Accio.exe writes the canonical record to its own file when the run finishes.
    /// </summary>
    public static void Add(string toolId, AccountRecord record)
    {
        if (IsExternal(toolId))
        {
            For(toolId).Add(record);
            return;
        }

        For(toolId).Add(record);
        Persist(toolId);
    }

    /// <summary>
    /// Removes one account from whatever file it lives in, together with its
    /// cookies. Returns <c>true</c> only when a row was really removed from disk
    /// - the caller must not claim success otherwise (the Accio panel used to log
    /// "deleted from accounts.json" unconditionally, which is what made a failed
    /// delete look like a delete that came back on refresh).
    /// </summary>
    public static bool Delete(string toolId, AccountRecord record)
    {
        if (IsExternal(toolId))
        {
            // Each external tool has its own on-disk shape; pick the right writer.
            var removed = string.Equals(toolId, AccioToolDriver.ToolKey, StringComparison.OrdinalIgnoreCase)
                ? DeleteFromAccioJson(record)
                : DeleteFromAccountsTxt(toolId, record);

            CookieVault.Delete(toolId, record.Id);
            CookieVault.DeleteForEmail(toolId, record.EmailUsed);

            // Drop the in-memory row immediately.
            var collection = For(toolId);
            if (collection.Contains(record)) collection.Remove(record);
            return removed;
        }

        For(toolId).Remove(record);
        CookieVault.Delete(toolId, record.Id);
        CookieVault.DeleteForEmail(toolId, record.EmailUsed);
        Persist(toolId);
        return true;
    }

    /// <summary>
    /// Removes a single line from the Z AI / Devin <c>accounts.txt</c> and also
    /// drops the matching entry from <c>cookies.txt</c> (if present). Atomic
    /// write via tmp+move so a crash mid-rewrite doesn't corrupt the file.
    /// </summary>
    private static bool DeleteFromAccountsTxt(string toolId, AccountRecord record)
    {
        var removedFromAccounts = false;
        try
        {
            var path = ExternalPath(toolId);
            if (path is null || !File.Exists(path))
            {
                Log.Warn(toolId, $"No external accounts file for {toolId} - nothing to delete from.");
                return false;
            }

            var email = record.EmailUsed.Trim();
            var lines = File.ReadAllLines(path, Encoding.UTF8).ToList();
            var removed = lines.RemoveAll(l =>
                l.Trim().StartsWith(email, StringComparison.OrdinalIgnoreCase)) > 0;

            if (removed)
            {
                var tmp = path + ".tmp";
                File.WriteAllLines(tmp, lines, new UTF8Encoding(false));
                File.Move(tmp, path, overwrite: true);
                Log.Success(toolId, $"Removed {email} from {Path.GetFileName(path)}");
                removedFromAccounts = true;
            }

            // Also drop the matching cookies.txt entry, if any.
            var cookiesPath = Path.Combine(Path.GetDirectoryName(path)!, "cookies.txt");
            if (File.Exists(cookiesPath))
            {
                var cs = File.ReadAllLines(cookiesPath, Encoding.UTF8).ToList();
                var removedCookies = cs.RemoveAll(l =>
                    l.Trim().StartsWith(email, StringComparison.OrdinalIgnoreCase)) > 0;
                if (removedCookies)
                {
                    var tmpC = cookiesPath + ".tmp";
                    File.WriteAllLines(tmpC, cs, new UTF8Encoding(false));
                    File.Move(tmpC, cookiesPath, overwrite: true);
                    Log.Info(toolId, $"  removed cookies.txt entry for {email}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(toolId, "Delete from accounts.txt failed: " + ex.Message);
            return false;
        }

        return removedFromAccounts;
    }

    private static bool DeleteFromAccioJson(AccountRecord record)
    {
        try
        {
            // THE one resolver for accio's accounts file. This used to be built
            // from AccioRoot - the folder holding accio's own scripts - while the
            // list is read from accounts\accio\ beside the exe. The two are not
            // the same folder, so Delete edited a file that does not exist, the
            // row survived, and the immediate Refresh put it straight back.
            var path = AccioAccountsJsonPath();
            if (!File.Exists(path))
            {
                Log.Error(AccioToolDriver.ToolKey,
                    $"accounts.json not found at {path} - nothing was deleted.");
                return false;
            }

            if (string.IsNullOrWhiteSpace(record.EmailUsed))
            {
                Log.Warn(AccioToolDriver.ToolKey, "Refusing to delete a row that has no email.");
                return false;
            }

            // Edit the file as raw JSON nodes. Never round-trip the rows through
            // AccountRecord: Accio.exe owns this schema (lower-case keys,
            // "status" as a *string*, cookie_count / cookies_file) and
            // re-serialising the panel's own model rewrote every surviving row
            // into PascalCase keys with a numeric Status. After that the panel
            // could no longer read the file it had just written - the login tab
            // went empty with "Could not read Accio accounts.json ... $[0].Status".
            var array = JsonNode.Parse(File.ReadAllText(path)) as JsonArray;
            if (array is null)
            {
                Log.Error(AccioToolDriver.ToolKey,
                    $"{path} is not a JSON array - nothing was deleted.");
                return false;
            }

            JsonNode? doomed = null;
            foreach (var node in array)
            {
                if (node is null) continue;
                if (string.Equals(JsonField(node, "email", "EmailUsed"),
                                  record.EmailUsed, StringComparison.OrdinalIgnoreCase))
                {
                    doomed = node;
                    break;
                }
            }

            if (doomed is null)
            {
                Log.Warn(AccioToolDriver.ToolKey,
                    $"No row for {record.EmailUsed} in {path} - the list already matches.");
                return false;
            }

            array.Remove(doomed);

            // Atomic write: tmp then move
            var tmp = path + ".tmp";
            var json = array.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);

            Log.Success(AccioToolDriver.ToolKey,
                $"Deleted {record.EmailUsed} from {Path.GetFileName(path)}");

            // Also remove the matching cookie file(s) - the one accounts.json
            // names first, then anything else matching the address. Without this
            // the cookies outlived the account, so "Saved Cookies" kept counting
            // an account that no longer existed.
            var removedCookies = 0;
            foreach (var cookieFile in FindCookieFilesForEmail(record.EmailUsed, record.CookieFile))
            {
                try
                {
                    File.Delete(cookieFile);
                    removedCookies++;
                    Log.Info(AccioToolDriver.ToolKey,
                        $"  removed cookie file {Path.GetFileName(cookieFile)}");
                }
                catch (Exception exCookie)
                {
                    Log.Warn(AccioToolDriver.ToolKey,
                        $"  could not remove cookie file {Path.GetFileName(cookieFile)}: {exCookie.Message}");
                }
            }

            if (removedCookies == 0)
                Log.Info(AccioToolDriver.ToolKey, $"  no cookie file on disk for {record.EmailUsed}");

            return true;
        }
        catch (Exception ex)
        {
            Log.Error(AccioToolDriver.ToolKey, "Delete from accounts.json failed: " + ex.Message);
            return false;
        }
    }

    // ------------------------------------------- accio: where its files really are

    /// <summary>
    /// accio's <c>accounts.json</c> - the ONE path every accio reader/writer in
    /// the panel has to agree on (list, delete, cookie lookup, file watcher).
    ///
    /// <para>It lives in the tool's state folder beside the exe
    /// (<c>accounts\accio\accounts\accounts.json</c>), which is also the folder
    /// the panel hands accio as PANEL_TOOL_STATE_DIR and the folder
    /// <see cref="ToolStateCandidates"/> reads. The folder inside accio's own
    /// source tree is only consulted for an install that has not been migrated
    /// yet, and the canonical path is returned when nothing exists at all so the
    /// caller can create it or report a path that means something.</para>
    /// </summary>
    public static string AccioAccountsJsonPath()
    {
        foreach (var candidate in ToolStateCandidates(AccioToolDriver.ToolKey))
        {
            if (File.Exists(candidate)) return candidate;
        }

        try
        {
            var legacy = Path.Combine(AccioToolDriver.AccioRoot, "accounts", "accounts.json");
            if (File.Exists(legacy)) return legacy;
        }
        catch
        {
            // AccioRoot is resolved from tool settings and a broken value must
            // not stop us naming the canonical path below.
        }

        return Path.Combine(AppPaths.ToolAccountsDir(AccioToolDriver.ToolKey),
                            "accounts", "accounts.json");
    }

    /// <summary>Folder holding accio's <c>accounts.json</c>.</summary>
    public static string AccioAccountsDir() =>
        Path.GetDirectoryName(AccioAccountsJsonPath()) ?? "";

    /// <summary>
    /// Folder holding accio's per-account cookie files
    /// (<c>accounts\accio\accounts\cookies</c>). Falls back to the folder inside
    /// accio's source tree when that is the only one that exists.
    /// </summary>
    public static string AccioCookiesDir()
    {
        var canonical = Path.Combine(AppPaths.ToolAccountsDir(AccioToolDriver.ToolKey),
                                     "accounts", "cookies");
        if (Directory.Exists(canonical)) return canonical;

        try
        {
            var legacy = Path.Combine(AccioToolDriver.AccioRoot, "accounts", "cookies");
            if (Directory.Exists(legacy)) return legacy;
        }
        catch
        {
            // see AccioAccountsJsonPath
        }

        return canonical;
    }

    /// <summary>Every folder a cookie file for accio may sit in, canonical first.</summary>
    private static IEnumerable<string> AccioCookieDirs()
    {
        var canonical = Path.Combine(AppPaths.ToolAccountsDir(AccioToolDriver.ToolKey),
                                     "accounts", "cookies");
        yield return canonical;

        var legacy = "";
        try { legacy = Path.Combine(AccioToolDriver.AccioRoot, "accounts", "cookies"); }
        catch { /* unreadable AccioRoot: canonical only */ }

        if (legacy.Length > 0 && !string.Equals(legacy, canonical, StringComparison.OrdinalIgnoreCase))
            yield return legacy;
    }

    /// <summary>
    /// Every cookie file on disk that belongs to one address. accio names them
    /// <c>accio_{local, dots as underscores}_at_{domain, dots as underscores}_{stamp}.json</c>,
    /// so the address is enough to locate the file even when a row has lost its
    /// <c>cookies_file</c> field - and the name accounts.json recorded (when
    /// known) is accepted first, because that is authoritative.
    /// </summary>
    private static List<string> FindCookieFilesForEmail(string email, string? listedFile = null)
    {
        var found = new List<string>();
        var seen  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var dir in AccioCookieDirs())
            {
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;

                if (!string.IsNullOrWhiteSpace(listedFile))
                {
                    var direct = Path.Combine(dir, Path.GetFileName(listedFile));
                    if (File.Exists(direct) && seen.Add(direct)) found.Add(direct);
                }

                var atIdx = email.IndexOf('@');
                if (atIdx <= 0) continue;

                var local  = email[..atIdx].Replace('.', '_');
                var domain = email[(atIdx + 1)..];
                var stem1  = "accio_" + local + "_at_" + domain.Replace('.', '_') + "_";
                var stem2  = "accio_" + local + "_at_" + domain + "_";

                // Materialise the listing: callers delete what comes back.
                foreach (var file in Directory.GetFiles(dir, "*.json"))
                {
                    var name = Path.GetFileName(file);
                    if (!name.StartsWith(stem1, StringComparison.OrdinalIgnoreCase) &&
                        !name.StartsWith(stem2, StringComparison.OrdinalIgnoreCase)) continue;

                    if (seen.Add(file)) found.Add(file);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn(AccioToolDriver.ToolKey, "Cookie folder scan failed: " + ex.Message);
        }

        return found;
    }

    /// <summary>
    /// The cookie file the panel should use for one account. Prefers the name
    /// accounts.json recorded for that row, then the newest file matching the
    /// address. Null when the account has no cookies on disk.
    /// </summary>
    public static string? ResolveCookieFile(string? listedFile, string email)
    {
        var files = FindCookieFilesForEmail(email ?? "", listedFile);
        if (files.Count == 0) return null;

        if (!string.IsNullOrWhiteSpace(listedFile))
        {
            var wanted = Path.GetFileName(listedFile);
            var exact = files.FirstOrDefault(f =>
                string.Equals(Path.GetFileName(f), wanted, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;
        }

        return files
            .OrderByDescending(f =>
            {
                try { return File.GetLastWriteTime(f); }
                catch { return DateTime.MinValue; }
            })
            .First();
    }

    /// <summary>
    /// Finds Accio's stored cookie file for an address (no name known).
    /// Kept for callers that only have the email; new code should use
    /// <see cref="ResolveCookieFile"/> so a recorded file name wins.
    /// </summary>
    public static string? FindCookieFileForEmail(string email) => ResolveCookieFile(null, email);

    /// <summary>Removes every account of a tool together with its stored cookies.</summary>
    public static void DeleteAll(string toolId)
    {
        if (IsExternal(toolId))
        {
            Log.Warn(toolId,
                "Accio owns this accounts file - clear it by editing the JSON " +
                "or using Accio.exe directly.");
            return;
        }

        For(toolId).Clear();
        CookieVault.DeleteAll(toolId);
        Persist(toolId);
    }

    public static void Persist(string toolId)
    {
        if (IsExternal(toolId))
        {
            // Don't write - re-read so the panel reflects whatever Accio wrote.
            Refresh(toolId);
            return;
        }

        List<AccountRecord> snapshot;
        lock (Gate) snapshot = For(toolId).ToList();
        Json.Save(AppPaths.AccountsFile(toolId), snapshot);
    }

    /// <summary>
    /// Re-reads the storage file and replaces the live collection with the
    /// fresh contents. Used by the Accio driver after every invocation so the
    /// panel reflects Accio's actual state without racing on writes.
    /// </summary>
    public static void Refresh(string toolId)
    {
        if (!IsExternal(toolId)) return;

        // The tool may have written new cookie files since the vault last read
        // its folder; without this the account created a second ago would show
        // "Saved" but Login would answer "no cookies" for up to 5 seconds.
        CookieVault.InvalidateToolLayer(toolId);

        var fresh = Read(toolId);
        var assign = new Action(() =>
        {
            var collection = For(toolId);
            collection.Clear();
            int nextId = 1;
            foreach (var r in fresh)
            {
                r.Id = nextId++;
                collection.Add(r);
            }
        });

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) assign();
        else dispatcher.Invoke(assign);
    }

    // ---------------------------------------------------------------- storage

    /// <summary>
    /// Tools whose accounts live outside the panel's own vault. The panel reads
    /// these paths on every refresh and never writes them back — the script owns
    /// the file. Same idea as Accio, just with different on-disk shapes.
    /// </summary>
    private static bool IsExternal(string toolId)
    {
        if (string.Equals(toolId, AccioToolDriver.ToolKey, StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(toolId, "zai", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(toolId, "devin", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(toolId, "tool4", StringComparison.OrdinalIgnoreCase))
            return true;   // MiniMax AI - flat accounts.txt next to its scripts
        return false;
    }

    /// <summary>
    /// Where the script stores its accounts. Returns null when the path can't
    /// be resolved (e.g. ToolWorkingDirectory not configured yet) so the panel
    /// can fall back to its own vault instead of throwing.
    /// </summary>
    /// <summary>
    /// Every place one tool's accounts file may live, best first.
    ///
    /// <para>The first entry is the single place the tools write now: beside the
    /// executable, in <c>accounts\&lt;tool&gt;\</c>. The tools were moved there so
    /// the application folder - the one holding the .exe and the tool code -
    /// carries no accounts at all; the panel therefore has to read the same
    /// folder, or its list comes up empty even though the data is intact.
    /// </summary>
    private static IEnumerable<string> ToolStateCandidates(string toolId)
    {
        string stateDir;
        try
        {
            stateDir = AppPaths.ToolAccountsDir(toolId);
        }
        catch
        {
            yield break;   // no base folder to resolve against
        }

        if (string.Equals(toolId, AccioToolDriver.ToolKey, StringComparison.OrdinalIgnoreCase))
        {
            // accio keeps a whole accounts folder: one json index plus a cookie
            // file per account.
            yield return Path.Combine(stateDir, "accounts", "accounts.json");
            yield return Path.Combine(stateDir, "accounts.json");
        }
        else
        {
            yield return Path.Combine(stateDir, "accounts.txt");
        }
    }

    private static string? ExternalPath(string toolId)
    {
        // The single location the tools actually write to now.
        foreach (var candidate in ToolStateCandidates(toolId))
        {
            if (File.Exists(candidate))
                return candidate;
        }

        // accio: the same resolver the delete and the cookie lookup use, so the
        // panel can never read one file and edit another.
        if (string.Equals(toolId, AccioToolDriver.ToolKey, StringComparison.OrdinalIgnoreCase))
            return AccioAccountsJsonPath();

        // Z AI / Devin AI: their Python scripts live under the per-tool
        // working directory configured in Tool Settings. The script writes
        // accounts.txt next to itself; the panel just reads it.
        var toolSettings = ToolSettingsStore.For(toolId);
        if (string.IsNullOrWhiteSpace(toolSettings.ToolWorkingDirectory))
            return null;
        var workDir = toolSettings.ToolWorkingDirectory!;
        if (!Directory.Exists(workDir))
            return null;

        // Prefer the script-side path (where the script writes) when both
        // exist; otherwise fall back to the user-facing project root.
        // Read() handles either one and merges duplicates by email.
        if (string.Equals(toolId, "zai", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(toolId, "devin", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(toolId, "tool4", StringComparison.OrdinalIgnoreCase))
        {
            var scriptDir = !string.IsNullOrWhiteSpace(toolSettings.CreateScriptPath)
                ? Path.GetDirectoryName(toolSettings.CreateScriptPath) ?? ""
                : "";
            var scriptAccounts = !string.IsNullOrWhiteSpace(scriptDir)
                ? Path.Combine(scriptDir, "accounts.txt")
                : null;

            if (scriptAccounts is not null && File.Exists(scriptAccounts))
                return scriptAccounts;

            return Path.Combine(workDir, "accounts.txt");
        }

        return null;
    }

    private static List<AccountRecord> Read(string toolId)
    {
        var external = ExternalPath(toolId);
        if (external is not null && File.Exists(external))
        {
            // Z AI / Devin AI use a flat "email:password" text file. The shape
            // is small enough that we always read it as text first; if it parses
            // as JSON it's an Accio-style file, otherwise fall back to the
            // text-mode reader.
            if (IsJsonShape(external))
                return ReadAccioJson(external);

            if (string.Equals(toolId, "zai", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolId, "devin", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolId, "tool4", StringComparison.OrdinalIgnoreCase))
                return ReadAccountsTxt(external);

            return ReadAccioJson(external);
        }

        var workspace = AppPaths.AccountsFile(toolId);
        if (File.Exists(workspace))
            return Json.Load(workspace, () => new List<AccountRecord>());

        // Read old per-tool files once if they exist; new writes go only to the
        // isolated workspace so existing Accio/Z/Devin data never mix.
        var legacy = AppPaths.LegacyAccountsFile(toolId);
        return File.Exists(legacy)
            ? Json.Load(legacy, () => new List<AccountRecord>())
            : new List<AccountRecord>();
    }

    /// <summary>
    /// Cheap shape probe: a file is JSON if its first non-whitespace character
    /// is <c>[</c> or <c>{</c>. Accounts.txt starts with a literal email, so
    /// this test routes the right file to the right parser.
    /// </summary>
    private static bool IsJsonShape(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            for (var i = 0; i < 16; i++)
            {
                var b = fs.ReadByte();
                if (b < 0) return false;
                var c = (char)b;
                if (char.IsWhiteSpace(c)) continue;
                return c == '[' || c == '{';
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Parses Z AI / Devin's flat <c>email:password</c> text file. Lines
    /// starting with <c>#</c> or empty are ignored; entries without an
    /// <c>@</c> in the address are silently dropped (defensive).
    /// </summary>
    private static List<AccountRecord> ReadAccountsTxt(string path)
    {
        var list = new List<AccountRecord>();
        try
        {
            var fileTime = File.GetLastWriteTime(path);
            foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#")) continue;

                var colonIdx = trimmed.IndexOf(':');
                if (colonIdx <= 0) continue;

                var email = trimmed[..colonIdx].Trim();
                if (!email.Contains('@')) continue;

                list.Add(new AccountRecord
                {
                    EmailUsed   = email,
                    MailSource  = MailSource.DottedGmail,
                    SourceRef   = "python-script",
                    Status      = AccountStatus.Active,
                    CookieState = HasCookieFileForEmail(email, path) ? CookieState.Saved : CookieState.None,
                    CreatedAt   = fileTime,
                    Note        = ""
                });
            }
        }
        catch (Exception ex)
        {
            Log.Warn(Path.GetFileNameWithoutExtension(path),
                $"Could not read accounts.txt ({ex.Message}) - panel list is empty.");
        }
        return list;
    }

    /// <summary>
    /// For Z AI, the cookies.txt lives next to accounts.txt. If the same email
    /// shows up there too, mark the row as having saved cookies.
    /// </summary>
    private static bool HasCookieFileForEmail(string email, string accountsTxtPath)
    {
        try
        {
            var cookiesPath = Path.Combine(
                Path.GetDirectoryName(accountsTxtPath)!,
                "cookies.txt");
            if (!File.Exists(cookiesPath)) return false;
            foreach (var line in File.ReadAllLines(cookiesPath, Encoding.UTF8))
            {
                if (line.StartsWith(email, StringComparison.OrdinalIgnoreCase) &&
                    line.Contains('|'))
                    return true;
            }
        }
        catch { }
        return false;
    }

    // -------- Accio -> AccountRecord mapping -------------------------------

    /// <summary>
    /// Reads Accio.exe's <c>accounts.json</c> into panel rows.
    ///
    /// Accio owns the schema (<c>email</c>, <c>status</c> as a *string*,
    /// <c>cookie_count</c>, <c>cookies_file</c>), but older / misc written rows
    /// can carry the panel's own PascalCase shape instead. Every field is
    /// therefore read defensively - a single odd value used to abort the whole
    /// deserialization, which emptied the account list and with it the login
    /// tab. A row with no cookie count falls back to the cookie file on disk.
    /// </summary>
    private static List<AccountRecord> ReadAccioJson(string path)
    {
        var list = new List<AccountRecord>();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
            {
                CommentHandling     = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });

            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;

            foreach (var row in doc.RootElement.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object) continue;

                var email = JsonField(row, "email", "EmailUsed");
                if (string.IsNullOrWhiteSpace(email)) continue;   // unidentifiable row

                var mailMode    = JsonField(row, "mail_mode", "SourceRef");
                var cookieCount = JsonNumber(row, "cookie_count");
                var cookiesFile = JsonField(row, "cookies_file");

                // Rows written without a cookie_count (legacy shape) would show
                // as "no cookies" even though the file exists - so look it up.
                if (cookieCount is null or 0)
                {
                    var file = FindCookieFileForEmail(email!);
                    if (file is not null && File.Exists(file))
                    {
                        cookiesFile = Path.GetFileName(file);
                        cookieCount = CountCookiesIn(file);
                    }
                }

                var hasCookies = (cookieCount ?? 0) > 0 || !string.IsNullOrWhiteSpace(cookiesFile);

                list.Add(new AccountRecord
                {
                    EmailUsed   = email!,
                    MailSource  = ModeToSource(mailMode),
                    SourceRef   = mailMode ?? "",
                    Status      = StatusToPanel(JsonField(row, "status", "Status")),
                    CookieState = hasCookies ? CookieState.Saved : CookieState.None,
                    CreatedAt   = ParseAccioStamp(JsonField(row, "registered_at", "CreatedAt"))
                                  ?? DateTime.Now,
                    Note        = JsonField(row, "note", "Note") ?? "",
                    // The file accio itself recorded for this row. Carrying it on
                    // the row is what lets Login / View / Export open the exact
                    // cookie set of this account instead of guessing from the
                    // address (and, before, guessing in the wrong folder).
                    CookieFile  = cookiesFile ?? ""
                });
            }
        }
        catch (Exception ex)
        {
            Log.Warn(AccioToolDriver.ToolKey,
                $"Could not read Accio accounts.json ({ex.Message}) - panel list is empty.");
        }
        return list;
    }

    private static MailSource ModeToSource(string? mode) => (mode ?? "").ToLowerInvariant() switch
    {
        "gmail-dot"      => MailSource.DottedGmail,
        "gmail-pool"     => MailSource.DottedGmail,
        "temp-mail"      => MailSource.TempMail,
        "1secmail"       => MailSource.TempMail,
        "custom-domain"  => MailSource.CustomDomain,
        "custom-mail"    => MailSource.CustomMail,
        "semi-auto"      => MailSource.SemiAuto,
        "manual-otp"     => MailSource.SemiAuto,
        "no-proxy"       => MailSource.NoProxy,
        "outlook-pool"   => MailSource.OutlookPool,
        "outlook"        => MailSource.OutlookPool,
        "outlook-token"  => MailSource.OutlookPool,
        _                => MailSource.DottedGmail
    };

    private static AccountStatus StatusToPanel(string? s) => (s ?? "").ToLowerInvariant() switch
    {
        // Accio.exe spells the column out.
        "dashboard" or "ok" or "active" => AccountStatus.Active,
        "partial"                       => AccountStatus.Active,
        "pending"                       => AccountStatus.Pending,
        "failed" or "error"             => AccountStatus.Failed,
        "expired"                       => AccountStatus.Expired,

        // Legacy rows carry the panel enum as a number: Pending 0, Active 1, ...
        "0"                             => AccountStatus.Pending,
        "1"                             => AccountStatus.Active,
        "2"                             => AccountStatus.Failed,
        "3"                             => AccountStatus.Expired,

        _                               => AccountStatus.Active
    };

    private static DateTime? ParseAccioStamp(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (DateTime.TryParse(raw.Replace('/', '-'), out var dt)) return dt;
        return null;
    }

    // -------- tolerant JSON field readers (Accio snake_case + legacy PascalCase)

    /// <summary>
    /// Reads the first present name from a JSON object as text. Accepts numbers
    /// too, so a numeric <c>Status</c> in a legacy row no longer throws.
    /// </summary>
    private static string? JsonField(JsonElement row, params string[] names)
    {
        foreach (var name in names)
        {
            if (!row.TryGetProperty(name, out var value)) continue;

            switch (value.ValueKind)
            {
                case JsonValueKind.String:  return value.GetString();
                case JsonValueKind.Number:  return value.GetRawText();
                case JsonValueKind.True:    return "true";
                case JsonValueKind.False:   return "false";
            }
        }
        return null;
    }

    private static string? JsonField(JsonNode node, params string[] names)
    {
        var value = node as JsonObject;
        if (value is null) return null;

        foreach (var name in names)
        {
            if (!value.TryGetPropertyValue(name, out var child) || child is null) continue;
            if (child is JsonValue scalar) return scalar.ToString();
        }
        return null;
    }

    private static int? JsonNumber(JsonElement row, string name)
    {
        if (!row.TryGetProperty(name, out var value)) return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n)) return n;
        if (value.ValueKind == JsonValueKind.String &&
            int.TryParse(value.GetString(), out var parsed)) return parsed;

        return null;
    }

    /// <summary>Number of cookie objects in one of Accio's cookie files.</summary>
    private static int CountCookiesIn(string file)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            return doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.GetArrayLength()
                : 0;
        }
        catch
        {
            return 0;
        }
    }

}