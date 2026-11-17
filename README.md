# Developer X Bulk Acc Panel

A Windows desktop automation panel, shipped to customers as **one file**:
`DeveloperXPanel-Setup.exe`.

Everything the panel needs — the panel itself, its `tools\` scripts, a bundled
Python runtime and Chromium (Playwright) — travels inside that single file. The
customer downloads it once, runs it once, and never has to download or install
anything else.

## For customers

1. Get the setup file from the official download page: <https://sabbirx01.github.io/Script_Panel/>
2. Run it and pick where it should install. The default is
   `C:\Users\<you>\AppData\Local\DeveloperXPanel`; press **Browse…** to choose
   another folder or drive.
3. Done. The panel opens. Every later update arrives on its own — the panel
   checks the release feed and updates itself.

### Uninstalling

Windows **Settings → Apps → Installed apps → Developer X Bulk Acc Panel →
Uninstall**. The desktop shortcut disappears with it.

## Latest update — v2.3.0

### Pick your own install folder
The setup now asks where the panel should live instead of deciding for you. The
default stays on `C:` (`%LOCALAPPDATA%\DeveloperXPanel`), but you can point it at
another drive — `D:\DeveloperXPanel`, an external disk, anywhere you can write.
The folder you choose is remembered: the desktop shortcut, the panel's own
updater and the uninstaller all return to the same place, so you are asked only
once.

The setup refuses folders it cannot use safely — a drive root, `Windows`,
`Program Files`, a folder that already holds other files, a folder with less
than 2 GB free, or one it cannot write to.

### A real uninstaller
The panel now registers itself in **Apps & features** and handles `/uninstall`
itself, so "uninstall" actually removes everything: the whole install folder, the
desktop shortcut and both registry entries. A panel that is still open is closed
first. If the setup file was deleted from a machine, the panel repairs its own
uninstall entry on the next start, so the Uninstall button never goes stale.

### Still one file, still nothing else to download
The bundled Python runtime and Chromium are installed with the panel, so no
"install Python first" or browser download step exists — on a fresh PC, on a PC
with no internet, or behind a proxy.

### Your data survives a re-install
Re-running the setup over an existing install carries `data\` across - licence,
settings and workspaces are kept, only the program files are replaced.

## For maintainers

| Task | Command |
| --- | --- |
| Build the panel | `source\build.ps1` |
| Build the one-file setup | `temp\build-launcher.ps1 -Version 2.3.0` |
| Release + verify the feed | `temp\publish-release.ps1` |
| Check a release can reach customers | `temp\verify-update.ps1` |

`temp\build-launcher.ps1` packs the bundle into an AES-256 encrypted payload,
embeds it in the setup launcher and writes `Build\bundle-info.json` (the tools
fingerprint the update feed publishes). A tag push starts
`.github\workflows\release.yml`, which builds the update ZIP, creates the GitHub
release, refreshes `docs\update.json` and redeploys the download page.

The AES key (`launcher\payload.key`) and the generated `PayloadKey.g.cs` are never
committed - anyone holding them can unpack a customer's setup.
