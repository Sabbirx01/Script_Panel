using System.Text.Json.Serialization;

namespace AISuitePanel.Models;

/// <summary>Where a tool account's email address came from (Screen 3 methods).</summary>
public enum MailSource
{
    TempMail,
    DottedGmail,
    CustomDomain,
    /// <summary>BAT [1.4] - one fixed Gmail + app password, used for one batch.</summary>
    CustomMail,
    /// <summary>BAT [1.5] - same Gmail rotation but the driver pauses for a manual OTP.</summary>
    SemiAuto,
    /// <summary>BAT [1.6] - same Gmail rotation but tagged as direct connection (no proxy).</summary>
    NoProxy,

    /// <summary>
    /// Outlook token pool - rows of <c>email|password|refresh_token|client_id</c>.
    /// The refresh token is exchanged for an IMAP/Graph access token and the
    /// verification code is read straight out of the mailbox, so no app
    /// password is involved anywhere.
    /// </summary>
    OutlookPool
}

/// <summary>Lifecycle of one tool account.</summary>
public enum AccountStatus
{
    Pending,
    Active,
    Failed,
    Expired
}

/// <summary>State of the stored browser session for an account.</summary>
public enum CookieState
{
    None,
    Saved,
    Expired,
    LoginFailed
}

/// <summary>One account created inside a tool panel.</summary>
public sealed class AccountRecord : ObservableObject
{
    private int _id;
    private string _emailUsed = "";
    private AccountStatus _status = AccountStatus.Pending;
    private CookieState _cookieState = CookieState.None;
    private DateTime? _lastLoginAt;
    private string _note = "";
    private bool _selected;

    /// <summary>Serial inside this tool (1, 2, 3 ...). Rendered as "01".</summary>
    public int Id
    {
        get => _id;
        set { if (Set(ref _id, value)) Raise(nameof(IdLabel)); }
    }

    /// <summary>The address the account was registered with (dotted alias / temp / custom).</summary>
    public string EmailUsed
    {
        get => _emailUsed;
        set => Set(ref _emailUsed, value);
    }

    public MailSource MailSource { get; set; }

    /// <summary>Which Gmail / temp service / domain actually produced the address.</summary>
    public string SourceRef { get; set; } = "";

    public AccountStatus Status
    {
        get => _status;
        set { if (Set(ref _status, value)) { Raise(nameof(StatusLabel)); Raise(nameof(StatusColor)); } }
    }

    public CookieState CookieState
    {
        get => _cookieState;
        set { if (Set(ref _cookieState, value)) { Raise(nameof(CookieLabel)); Raise(nameof(HasCookie)); } }
    }

    public DateTime CreatedAt { get; set; }

    public DateTime? LastLoginAt
    {
        get => _lastLoginAt;
        set => Set(ref _lastLoginAt, value);
    }

    /// <summary>Last error / remark shown when a row fails.</summary>
    public string Note
    {
        get => _note;
        set => Set(ref _note, value);
    }

    /// <summary>
    /// Tick in the manager table. Not persisted - it is a run-time selection used
    /// to narrow section 2 down to specific rows.
    /// </summary>
    [JsonIgnore]
    public bool Selected
    {
        get => _selected;
        set => Set(ref _selected, value);
    }

    /// <summary>
    /// Name of the cookie file the tool recorded for this account (accio fills
    /// this from its <c>accounts.json</c> "cookies_file" field). Not persisted -
    /// the tool's own file is the source of truth, and for every other tool this
    /// stays empty.
    /// </summary>
    [JsonIgnore]
    public string CookieFile { get; set; } = "";

    // ------------------------------------------------------------- display only

    [JsonIgnore] public string IdLabel => Id.ToString("00");

    [JsonIgnore] public string StatusLabel => Status.ToString();

    [JsonIgnore] public string CookieLabel => CookieState switch
    {
        CookieState.Saved => "Saved",
        CookieState.LoginFailed => "Login failed",
        CookieState.Expired => "Expired",
        _ => "None"
    };

    [JsonIgnore] public bool HasCookie => CookieState == CookieState.Saved;

    [JsonIgnore] public string StatusColor => Status switch
    {
        AccountStatus.Active => "#FF34D399",
        AccountStatus.Failed => "#FFF87171",
        AccountStatus.Expired => "#FFFBBF24",
        _ => "#FF93A3C2"
    };
}
