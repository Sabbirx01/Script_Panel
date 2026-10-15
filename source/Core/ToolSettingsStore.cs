using System.Collections.Generic;
using System.Linq;
using AISuitePanel.Models;

namespace AISuitePanel;

/// <summary>Screen 4, section 4 - delay and thread count, kept per tool.</summary>
public static class ToolSettingsStore
{
    private static Dictionary<string, ToolSettings> _map = new(StringComparer.OrdinalIgnoreCase);

    public static void Load()
    {
        _map = Json.Load(AppPaths.ToolSettings, () => new Dictionary<string, ToolSettings>());
        foreach (var pair in _map)
        {
            ResolveToolPaths(pair.Key, pair.Value);
        }
    }

    /// <summary>
    /// Points a tool's configured paths at files that really exist.
    ///
    /// One function, called once per tool, is the whole portable story: a path
    /// that is present is left exactly as configured (so a development machine
    /// never shifts under the operator), and a path that is missing is looked for
    /// beside the .exe - as written, then under <c>tools\&lt;toolId&gt;\</c>. A bundle
    /// can therefore ship <c>tool-settings.json</c> with relative paths like
    /// <c>tools\zai\Zcreatev2.py</c> and work from any drive letter, while the
    /// machine that built it carries on using its own absolute paths.
    /// </summary>
    private static void ResolveToolPaths(string toolId, ToolSettings s)
    {
        if (s is null)
        {
            return;
        }
        s.PythonExePath = AppPaths.ResolvePython(s.PythonExePath);
        s.CreateScriptPath = AppPaths.Resolve(s.CreateScriptPath, toolId);
        s.LoginScriptPath = AppPaths.Resolve(s.LoginScriptPath, toolId);
        s.ToolWorkingDirectory = AppPaths.Resolve(s.ToolWorkingDirectory, toolId);
    }

    /// <summary>Always returns a usable object - never null.</summary>
    public static ToolSettings For(string toolId)
    {
        if (_map.TryGetValue(toolId, out var s))
        {
            return s;
        }

        var fresh = new ToolSettings();
        ResolveToolPaths(toolId, fresh);
        return fresh;
    }

    public static void Save(string toolId, ToolSettings settings)
    {
        _map[toolId] = settings;
        Json.Save(AppPaths.ToolSettings, _map);
    }

    /// <summary>
    /// Per-tool "hide the browser" flag. <c>false</c> (the default) keeps the
    /// tool's own visible-browser behaviour.
    /// </summary>
    public static bool IsHeadless(string toolId) => For(toolId).Headless;

    /// <summary>
    /// Persists the per-tool headless toggle. No-op when the value is unchanged,
    /// so flipping the switch never rewrites the file needlessly.
    /// </summary>
    public static void SetHeadless(string toolId, bool headless)
    {
        var settings = For(toolId);
        if (settings.Headless == headless) return;
        settings.Headless = headless;
        Save(toolId, settings);
    }

    // ------------------------------------------------------ per-tool login URL

    /// <summary>
    /// The per-tool default landing page, mirroring the mapping the panel used to
    /// keep inline. Used whenever the user has not typed a URL yet.
    /// </summary>
    public static string DefaultLoginUrl(string toolId) =>
        (toolId ?? "").ToLowerInvariant() switch
        {
            "zai"   => "https://chat.z.ai/",
            "devin" => "https://app.devin.ai/",
            "tool4" => "https://www.minimax.io/",
            _       => "https://www.google.com/"
        };

    /// <summary>
    /// Light normalisation for the Login URL box:
    ///   * empty / whitespace  -> the tool's <see cref="DefaultLoginUrl"/>;
    ///   * missing scheme      -> <c>https://</c> is prepended;
    ///   * surrounding quotes  -> stripped.
    /// The result is always a usable, non-empty absolute URL.
    /// </summary>
    public static string NormaliseLoginUrl(string? raw, string fallback)
    {
        var value = (raw ?? "").Trim().Trim('"').Trim('\'');
        if (value.Length == 0) return fallback;
        if (value.IndexOf("://", StringComparison.Ordinal) < 0)
            value = "https://" + value;
        return value;
    }

    /// <summary>
    /// The effective Login URL for <paramref name="toolId"/>: the persisted value
    /// (normalised) or the built-in default. Never empty - so the panel always has
    /// something to hand the login script as <c>--url</c>.
    /// </summary>
    public static string LoginUrl(string toolId) =>
        NormaliseLoginUrl(For(toolId).LoginUrl, DefaultLoginUrl(toolId));

    /// <summary>
    /// Persists the per-tool Login URL. Stores <c>null</c> when the value equals the
    /// tool's default, so the key disappears from the JSON again and the file keeps
    /// its original shape until the user actually customises the URL.
    /// </summary>
    public static void SetLoginUrl(string toolId, string? url)
    {
        var settings = For(toolId);
        var normalised = NormaliseLoginUrl(url, DefaultLoginUrl(toolId));
        var stored = string.Equals(normalised, DefaultLoginUrl(toolId), StringComparison.OrdinalIgnoreCase)
            ? null
            : normalised;

        if (string.Equals(settings.LoginUrl, stored, StringComparison.Ordinal)) return;
        settings.LoginUrl = stored;
        Save(toolId, settings);
    }

    // ------------------------------------------------- per-tool SIGNUP URL (create)

    /// <summary>
    /// The page a create run is driven to when the user has not typed one. accio
    /// previously had this hard-coded in the driver, which meant every operator
    /// created accounts on accio.com's own login page and nothing else.
    /// </summary>
    public static string DefaultCreateUrl(string toolId) =>
        (toolId ?? "").ToLowerInvariant() switch
        {
            "accio" => "https://www.accio.com/login",
            _       => ""
        };

    /// <summary>
    /// The effective SIGNUP URL for <paramref name="toolId"/>: the persisted
    /// value (normalised) or the built-in default. Empty only for tools that do
    /// not take a URL on the create path.
    /// </summary>
    public static string CreateUrl(string toolId) =>
        NormaliseLoginUrl(For(toolId).CreateUrl, DefaultCreateUrl(toolId));

    /// <summary>
    /// Persists the per-tool SIGNUP URL. Like <see cref="SetLoginUrl"/>, the key
    /// disappears from <c>tool-settings.json</c> again once the value is back to
    /// the tool's default.
    /// </summary>
    public static void SetCreateUrl(string toolId, string? url)
    {
        var settings = For(toolId);
        var normalised = NormaliseLoginUrl(url, DefaultCreateUrl(toolId));
        var stored = string.Equals(normalised, DefaultCreateUrl(toolId), StringComparison.OrdinalIgnoreCase)
            ? null
            : normalised;

        if (string.Equals(settings.CreateUrl, stored, StringComparison.Ordinal)) return;
        settings.CreateUrl = stored;
        Save(toolId, settings);
    }

    // -------------------------------------------------------- per-tool mail bans
    // Feature 1. A Gmail can be banned for ONE tool without touching Global
    // Settings, so the same mailbox keeps working everywhere else.

    /// <summary>
    /// The Gmails banned for <paramref name="toolId"/>. Never null; an unset list
    /// reads back as empty.
    /// </summary>
    public static IReadOnlyList<string> Excluded(string toolId) =>
        For(toolId).ExcludedGmails ?? new List<string>();

    /// <summary>
    /// True when <paramref name="email"/> is banned for <paramref name="toolId"/>.
    /// Case-insensitive and whitespace-tolerant.
    /// </summary>
    public static bool IsExcluded(string toolId, string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return false;
        var needle = email.Trim();
        foreach (var raw in Excluded(toolId))
            if (string.Equals((raw ?? "").Trim(), needle, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>
    /// Replaces the ban list for one tool and persists it immediately. An empty
    /// list is stored as <c>null</c> so the key disappears from the JSON again
    /// (the file returns to exactly its pre-ban shape).
    /// </summary>
    public static void SetExcluded(string toolId, IEnumerable<string>? emails)
    {
        var settings = For(toolId);
        var cleaned = (emails ?? Enumerable.Empty<string>())
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => e.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        settings.ExcludedGmails = cleaned.Count == 0 ? null : cleaned;
        Save(toolId, settings);
    }
}

