using AISuitePanel.Models;

namespace AISuitePanel.Automation;

/// <summary>
/// Stand-in driver used by the shipped build. It runs the full flow - registration,
/// the wait for a verification mail, capturing the session - but talks to nothing
/// outside the process, so the panel is safe to run and test end to end.
///
/// Cookie domains are issued under the reserved <c>.invalid</c> TLD on purpose:
/// they cannot resolve, so a simulated session can never leak into a real site.
///
/// Replace this class (see <see cref="ToolDrivers.Register"/>) with a driver that
/// drives the real tool once the endpoints and credentials are available.
/// </summary>
public sealed class SimulatedToolDriver : IToolDriver
{
    public SimulatedToolDriver(string toolId) => ToolId = toolId;

    public string ToolId { get; }

    private string Domain => $"{ToolId}.simulated.invalid";

    public async Task<DriverResult> CreateAccountAsync(string email, MailSource source, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(email))
            return DriverResult.Fail("No address was supplied by the mail router.");

        // Deterministic failure hook so the failure path can be demonstrated
        // without waiting for a real tool to break.
        if (email.Contains("fail", StringComparison.OrdinalIgnoreCase))
            return DriverResult.Fail("Registration rejected by the tool (simulated).");

        await Task.Delay(TimeSpan.FromMilliseconds(650), ct);          // open sign-up page
        await Task.Delay(TimeSpan.FromMilliseconds(500), ct);          // submit the form
        await Task.Delay(TimeSpan.FromMilliseconds(600), ct);          // wait for the verify mail

        return DriverResult.Success(
            "Account created (simulated).",
            NewCookies(seed: email));
    }

    public async Task<DriverResult> LoginWithCookiesAsync(string email, IReadOnlyList<BrowserCookie> cookies, CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(700), ct);          // open the tool
        await Task.Delay(TimeSpan.FromMilliseconds(600), ct);          // replay the session

        var session = cookies.FirstOrDefault(c => string.Equals(c.Name, "session", StringComparison.OrdinalIgnoreCase));

        if (session is null || string.IsNullOrWhiteSpace(session.Value))
            return DriverResult.Fail("Stored cookie set has no session cookie - it has expired.");

        return DriverResult.Success("Session restored (simulated).", NewCookies(seed: email));
    }

    /// <summary>A small, obviously-fake cookie set shaped like a real one.</summary>
    private IReadOnlyList<BrowserCookie> NewCookies(string seed)
    {
        var token = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(seed + DateTime.UtcNow.Ticks)))[..32];

        var expires = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds();

        return new List<BrowserCookie>
        {
            new() { Name = "session",   Value = token,               Domain = "." + Domain, Path = "/", Expires = expires, HttpOnly = true,  Secure = true, SameSite = "Lax" },
            new() { Name = "uid",       Value = token[..12],         Domain = "." + Domain, Path = "/", Expires = expires, HttpOnly = false, Secure = true, SameSite = "Lax" },
            new() { Name = "csrf",      Value = token[12..24],       Domain = "." + Domain, Path = "/", Expires = 0,       HttpOnly = false, Secure = true, SameSite = "Strict" }
        };
    }
}
