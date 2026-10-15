# Developer X Bulk Acc Panel — Source

Windows desktop panel built with **.NET 10 / WPF**. Four screens:

1. **Activation** — one field for the licence key + `🚀 ACTIVATE SOFTWARE`.
2. **Dashboard** — the tool boxes (Z AI, Accio AI, Devin AI, MiniMax AI by default), each showing
   a live `Saved Cookies: N` counter and an `OPEN TOOL` button, plus `⚙️ SETTING`. The box list is
   read from `%APPDATA%\AISuitePanel\tools.json`, so it is not hard-coded in the source.
3. **Global Mail Settings** — the three mail methods every tool panel draws from.
4. **Tool Panel** — create accounts, log in with stored cookies, manage accounts and cookies,
   and tune the tool's run settings.

The matching design document is [`../BLUEPRINT.md`](../BLUEPRINT.md).

## Build

```powershell
# from this folder
.\build.ps1
```

The finished executable is written to `..\output\DeveloperXBulkAccPanel.exe`
(single file, self-contained — runs on any 64-bit Windows without .NET installed).

```powershell
.\build.ps1 -FrameworkDependent   # ~1 MB exe, needs the .NET desktop runtime
```

A running panel is closed automatically before the build overwrites the exe.

## Test

```powershell
powershell -ExecutionPolicy Bypass -File "..\accio\tools\uitest.ps1"
```

Drives the real exe through UI Automation, walks all four screens (activate → dashboard →
settings → Z AI panel → create 3 accounts → cookie login → viewer → dashboard → Devin AI panel
→ restart), saves a screenshot per step into `..\accio\screenshots` and asserts both the UI
text and the files written to `%APPDATA%\AISuitePanel`. It wipes that folder first, so it
always starts from the activation screen, and it always closes the panel afterwards.

## Layout

```
source/
├── AISuitePanel.csproj      project + publish settings
├── NuGet.config             pin to nuget.org (this machine had no sources)
├── build.ps1                publish script
├── App.xaml(.cs)            application entry, store loading, crash logging
├── MainWindow.xaml(.cs)     custom title bar + screen host
├── Themes/Dark.xaml         palette, brushes and every control style
├── Core/
│   ├── AppPaths.cs          every file the panel owns
│   ├── Json.cs              atomic load/save (temp file + move)
│   ├── Log.cs               activity log + file log, masks secrets
│   ├── Branding.cs          every user-visible brand string, one place
│   ├── ProductCatalog.cs    the one licence rule: a key's product is its own prefix
│   ├── LicenseService.cs    online RSA response verification + machine-bound main licence
│   ├── ToolLicenseService.cs per-tool online RSA licence state
│   ├── SettingsStore.cs     Screen 3 state + validation + readiness checks
│   ├── ToolSettingsStore.cs per-tool delay / threads
│   ├── AccountStore.cs      one observable account list per tool
│   ├── CookieVault.cs       cookie storage, counts and export
│   ├── AliasGuard.cs        which dotted aliases a tool has already used
│   ├── MailRouter.cs        settings → concrete addresses (3 methods)
│   ├── ToolCatalog.cs       the dashboard boxes, loaded from tools.json
│   ├── Nav.cs               screen switching
│   └── Automation/
│       ├── IToolDriver.cs          the seam a real tool driver plugs into
│       ├── SimulatedToolDriver.cs  the driver this build ships with
│       ├── CreateAccountRunner.cs  section 1 engine
│       └── CookieLoginRunner.cs    section 2 engine
├── Models/                  ToolInfo, AccountRecord, CookieRecord, GlobalSettings, LogEntry
├── Views/
│   ├── ActivationView.xaml      screen 1
│   ├── DashboardView.xaml       screen 2
│   ├── GlobalSettingsView.xaml  screen 3
│   └── ToolPanelView.xaml       screen 4
└── Assets/app.ico           application icon
```

## Common edits

| Want to change | Edit |
|---|---|
| Tool names, colours, icons, add a 5th box | `%APPDATA%\AISuitePanel\tools.json` (defaults in `Core/ToolCatalog.cs`) |
| Which products may open the panel | `%APPDATA%\AISuitePanel\products.json` → `panelProducts` (defaults in `Core/ProductCatalog.cs`) |
| A tool's licence name | `%APPDATA%\AISuitePanel\tools.json` → `product` / `legacyProducts` (defaults in `Core/ToolCatalog.cs`) |
| Colours / fonts for the whole app | `Themes/Dark.xaml` |
| Product name, page name, support links | `Core/Branding.cs` |
| Main online licence verification | `Core/LicenseService.cs` |
| Per-tool online licences | `Core/ToolLicenseService.cs` |
| Connect a tool for real | `Core/Automation/` — implement `IToolDriver` and register it |
| Temp mail service endpoints | `Core/MailRouter.cs` (`ServiceDomains`) |
| Window title, min size, chrome buttons | `MainWindow.xaml` |

## Where data is stored

```
%APPDATA%\AISuitePanel\license.json        main licence snapshot (RSA-verified, machine-bound)
%APPDATA%\AISuitePanel\tool-licenses.json   one RSA-verified licence snapshot per tool
%APPDATA%\AISuitePanel\products.json        which products open the panel, plus the endpoint override
%APPDATA%\AISuitePanel\tools.json           which tool boxes the dashboard shows
%APPDATA%\AISuitePanel\settings.json        global mail settings
%APPDATA%\AISuitePanel\tool-settings.json   per-tool delay / threads
%APPDATA%\AISuitePanel\used-aliases.json    dotted aliases already spent, per tool
%APPDATA%\AISuitePanel\accounts\{tool}.json accounts, one file per tool
%APPDATA%\AISuitePanel\cookies\{tool}.json  cookie vault, one file per tool
%APPDATA%\AISuitePanel\logs\{date}.log      append-only activity log
%APPDATA%\AISuitePanel\crash.log            written only if the app hits an error
```

Deleting that folder resets the panel to a fresh, unactivated state.
**RESET LICENSE** on the dashboard does the same for the licence only.

## Licence behaviour (current build)

### One name per tool

> **A key's product is the prefix printed on the key.** `ZAI-XXXXX-…` is product `zai`,
> `PANEL-XXXXX-…` is product `panel`, `MINIMAX-XXXXX-…` is product `minimax`.

That single rule replaces the old "try a list of candidate products and remember the one
that worked" logic. The client no longer guesses, so there is nothing to keep in sync with
the server's product list, and one activation is one HTTP call instead of a fan-out.

- **The main panel** needs a key whose product is in `panelProducts` (default `panel`, `dola`).
  Same requirement as before: a fresh online RSA-2048 / PKCS#1 v1.5 / SHA-256 response,
  checked for nonce, product, device binding, expiry and `sig_v2`.
- **Each tool** needs its own key. A box's product is its own name by default
  (`tools.json` → `product`), and a 5th box needs no code change at all. `legacyProducts`
  lists older names the same tool was sold under, so the Z AI box accepts both `zai` and
  `dolazai` and a key sold before a rename keeps working.
- **A tool's product never opens the panel**, whatever `panelProducts` says — one licence per
  box is the point. And a tool key pasted on the main screen is refused *before* any call, so
  it is never bound to this PC on the way to being refused; the message names the right box
  instead ("This is a Z AI key. Open the Z AI box and enter it there.").
- **Retiring a name on the server does not touch a single key.** Rows keep the product they
  were created with, and validation compares that row to the product the client read off the
  key, so every key ever sold keeps validating.
- Server product codes contain only letters and digits, because the cPanel server normalises
  product names by removing punctuation.
- To add another tool: add it in the server panel (Plans & Tools → Add another tool, **one
  field** — the prefix is the name in capitals), issue its key, then add the box to
  `tools.json` with the same id. No code change and no rebuild.
- No server file, `config.php`, private signing key or shared signing secret is changed by this client patch.
- **Dotted Gmail aliases** are generated from the Gmail you configured: dots are inserted
  into the local part, already-used aliases are skipped per tool (`used-aliases.json`),
  and the round-robin cursor is persisted so a restart continues with the next Gmail.
- **Account creation and cookie login run against `SimulatedToolDriver`** — no network
  calls, no real accounts, and every session is issued in the reserved `.invalid` TLD so
  nothing can ever reach a real site. Point `ToolDrivers.Register(...)` at a real
  `IToolDriver` to connect an actual tool; nothing in the UI has to change.
- App passwords and cookie values are stored in clear (own-machine grade). Wrap
  `Json.Save` / `Json.Load` in `SettingsStore` and `CookieVault` with DPAPI before
  shipping — see BLUEPRINT.md §7.
