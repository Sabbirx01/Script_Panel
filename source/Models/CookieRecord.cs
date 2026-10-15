namespace AISuitePanel.Models;

/// <summary>One browser cookie, shaped so it can be handed straight to a session.</summary>
public sealed class BrowserCookie
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
    public string Domain { get; set; } = "";
    public string Path { get; set; } = "/";

    /// <summary>
    /// Unix seconds (Accio cookies.json writes this with sub-second precision,
    /// e.g. 1805878552.556792).  Keep as <c>double</c> so JSON round-trips cleanly.
    /// 0 / -1 = session cookie.
    /// </summary>
    public double Expires { get; set; }

    public bool HttpOnly { get; set; }
    public bool Secure { get; set; }
    public string SameSite { get; set; } = "Lax";
}

/// <summary>The stored session of one account inside one tool.</summary>
public sealed class CookieRecord
{
    public int AccountId { get; set; }
    public string Email { get; set; } = "";
    public DateTime SavedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public List<BrowserCookie> Cookies { get; set; } = new();
}
