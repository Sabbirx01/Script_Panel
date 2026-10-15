using AISuitePanel.Models;

namespace AISuitePanel.Automation;

/// <summary>Outcome of one driver call.</summary>
public sealed record DriverResult(bool Ok, string Message, IReadOnlyList<BrowserCookie>? Cookies = null)
{
    public static DriverResult Fail(string message) => new(false, message);
    public static DriverResult Success(string message, IReadOnlyList<BrowserCookie>? cookies = null) =>
        new(true, message, cookies);
}

/// <summary>
/// Everything a tool panel needs from the outside world. One implementation per
/// tool: the panel, the vault and the runners are all written against this, so
/// swapping in the real thing never touches the UI.
///
/// The shipped build uses <see cref="SimulatedToolDriver"/> - no network calls,
/// no real accounts. See BLUEPRINT.md §14 for what a real driver still needs.
/// </summary>
public interface IToolDriver
{
    string ToolId { get; }

    /// <summary>
    /// Registers a fresh account on the tool and returns its first cookie set.
    /// <paramref name="source"/> is the mail source the user picked (so a driver
    /// that delegates to a CLI can pass the matching --flag bundle).
    /// </summary>
    Task<DriverResult> CreateAccountAsync(string email, MailSource source, CancellationToken ct);

    /// <summary>Replays a stored cookie set and reports whether the session is still valid.</summary>
    Task<DriverResult> LoginWithCookiesAsync(string email, IReadOnlyList<BrowserCookie> cookies, CancellationToken ct);
}

/// <summary>Hands out the driver for a tool id.</summary>
public static class ToolDrivers
{
    private static readonly Dictionary<string, IToolDriver> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly object Gate = new();

    public static IToolDriver For(string toolId)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(toolId, out var existing)) return existing;

            IToolDriver driver = new SimulatedToolDriver(toolId);
            Cache[toolId] = driver;
            return driver;
        }
    }

    /// <summary>Replaces the driver for a tool (used when a real one is wired up).</summary>
    public static void Register(IToolDriver driver)
    {
        lock (Gate) Cache[driver.ToolId] = driver;
    }
}
