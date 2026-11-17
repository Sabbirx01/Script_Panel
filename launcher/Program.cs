using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace LaunchPanel;

/// <summary>
/// DeveloperXPanel-Setup.exe — the single file a customer keeps.
///
/// It carries the whole panel (panel exe, tools\ , runtime\ , .playwright\)
/// inside itself as one zip resource. On the first run it asks which folder to
/// install into - %LOCALAPPDATA%\DeveloperXPanel by default - unpacks the zip
/// there, and starts the panel from it. On later runs it sees the version
/// already matches and starts the panel straight away.
///
/// Why a folder in the user's own area by default, and not beside the exe: a
/// customer may launch the setup from a read-only place (a download folder, a USB
/// stick, a chat client's temp dir), so the payload has to land somewhere the
/// user can always write to and that needs no administrator rights. A customer
/// who wants it on another drive picks that folder once, in the chooser, and
/// every later run - the desktop shortcut, the panel's own updater - returns to
/// it without asking again.
///
/// The customer's own account data lives in that same install folder as
/// data\, and is carried across a re-install instead of being wiped.
///
/// The same file is the uninstaller: Windows' "Apps &amp; features" entry
/// registered at install time runs it again with /uninstall.
/// </summary>
internal static class Program
{
    private const string PanelExeName      = "DeveloperXBulkAccPanel.exe";
    private const string AppFolderName     = "DeveloperXPanel";
    private const string DisplayName       = "Developer X Bulk Acc Panel";
    // The panel travels AES-256 encrypted. The version marker is a separate,
    // tiny resource on purpose: reading it must not cost a 549 MB decrypt, or
    // every launch after the first would stall on it.
    private const string PayloadResource   = "payload.enc";
    private const string VersionResource   = "bundle-version.txt";
    private const string VersionEntry      = "bundle-version.txt";
    private const string ShortcutName      = "Developer X Bulk Acc Panel.lnk";

    // Where Windows' "Apps & features" looks for the Uninstall button.
    private const string UninstallKey =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\DeveloperXPanel";

    // Where the chosen install folder is remembered, so a custom location is
    // picked exactly once.
    private const string AppKey = @"Software\DeveloperXPanel";
    private const string InstallPathValue = "InstallPath";

    // "DXPL" + format byte + 16 byte IV + 32 byte SHA-256 of the plaintext
    private const int PayloadHeaderLength = 4 + 1 + 16 + 32;

    [STAThread]
    private static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);

        if (IsUninstall(args))
        {
            Environment.ExitCode = Uninstall(UninstallFolder(), HasFlag(args, "quiet"));
            return;
        }

        try
        {
            string target = ChooseInstallFolder(args);
            if (target.Length == 0) return;   // the customer closed the folder chooser

            string wanted = ReadPayloadVersion();
            string have = ReadInstalledVersion(target);
            string panel = Path.Combine(target, PanelExeName);

            bool upToDate = string.Equals(wanted, have, StringComparison.OrdinalIgnoreCase)
                            && File.Exists(panel);

            string shortcut = "";

            if (!upToDate)
            {
                using var form = new ProgressForm(wanted);
                form.Shown += (_, _) => Task.Run(() =>
                {
                    try
                    {
                        Install(target, form);
                        form.Finish();
                    }
                    catch (Exception ex)
                    {
                        form.Fail(ex);
                    }
                });

                Application.Run(form);

                if (!form.Succeeded)
                {
                    return;   // Fail() has already told the user what went wrong
                }

                shortcut = CreateShortcut();
            }

            RegisterUninstall(target, wanted, shortcut);
            RememberInstallFolder(target);

            StartPanel(Path.Combine(target, PanelExeName));
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Setup could not finish.\n\n" + ex.Message,
                "Developer X Panel",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    // ------------------------------------------------------- where it installs

    private static string DefaultFolder() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppFolderName);

    /// <summary>
    /// The folder this run installs into. The customer is asked only when nothing
    /// is installed yet; returns "" when they closed the chooser.
    ///
    /// The order matters. An explicit <c>-target</c> wins so a scripted install
    /// never waits for a click. A setup running from inside an installed folder -
    /// which is what the panel's own updater and its self-repair produce, since
    /// both stage the setup under <c>data\</c> - installs over that folder,
    /// because nobody is there to answer a window in a background update.
    /// </summary>
    private static string ChooseInstallFolder(string[] args)
    {
        string asked = ArgumentValue(args, "target");
        if (asked.Length > 0) return Path.GetFullPath(asked);

        string here = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "";
        if (LooksInstalled(here)) return here;

        // <install>\data\pending-setup.exe: the install is one level up.
        string up = Path.GetDirectoryName(here.TrimEnd('\\', '/')) ?? "";
        if (LooksInstalled(up)) return up;

        string recorded = ReadRecordedFolder();
        if (recorded.Length > 0) return recorded;

        string fallback = DefaultFolder();
        if (LooksInstalled(fallback)) return fallback;

        // A silent run must never wait on a window nobody can see.
        if (HasFlag(args, "quiet")) return fallback;

        using var chooser = new LocationForm(fallback);
        return chooser.ShowDialog() == DialogResult.OK ? chooser.SelectedPath : "";
    }

    /// <summary>The installed copy's folder, or "" when there is not one.</summary>
    private static string ReadRecordedFolder()
    {
        string recorded = ReadRegistry(AppKey, InstallPathValue);
        if (LooksInstalled(recorded)) return recorded;

        // An install made before the folder was remembered still recorded where
        // it lives, for the Uninstall button.
        string legacy = ReadRegistry(UninstallKey, "InstallLocation");
        return LooksInstalled(legacy) ? legacy : "";
    }

    /// <summary>Where /uninstall points: the install that is recorded, else the default.</summary>
    private static string UninstallFolder()
    {
        string recorded = ReadRecordedFolder();
        return recorded.Length > 0 ? recorded : DefaultFolder();
    }

    private static void RememberInstallFolder(string target)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(AppKey);
            key?.SetValue(InstallPathValue, target);
        }
        catch
        {
            // The uninstall entry records the same folder; a refused write is
            // no reason to fail an install that otherwise worked.
        }
    }

    /// <summary>Called on uninstall, so a removed install leaves nothing behind.</summary>
    private static void ForgetInstallFolder()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(AppKey, throwOnMissingSubKey: false); }
        catch { /* a leftover value is cosmetic */ }
    }

    private static string ReadRegistry(string key, string name)
    {
        try
        {
            using var sub = Registry.CurrentUser.OpenSubKey(key);
            return sub?.GetValue(name) as string ?? "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>True when <paramref name="folder"/> actually holds the panel.</summary>
    private static bool LooksInstalled(string folder)
    {
        try
        {
            return folder.Length > 0 && File.Exists(Path.Combine(folder, PanelExeName));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Reads <c>-target v</c>, <c>-target=v</c> or <c>/target:v</c>; "" when the
    /// switch is absent.
    /// </summary>
    private static string ArgumentValue(string[] args, string name)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg.Length < 2 || (arg[0] != '-' && arg[0] != '/')) continue;

            string body = arg.TrimStart('-', '/');
            int split = body.IndexOfAny([':', '=']);
            string key = split >= 0 ? body[..split] : body;
            if (!key.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;

            if (split >= 0) return body[(split + 1)..].Trim('"');

            // "-target C:\My Folder" - the path may hold spaces, so the next
            // argument is taken whole.
            if (i + 1 < args.Length) return args[i + 1].Trim('"');
        }

        return "";
    }

    // ---------------------------------------------------------------- versions

    /// <summary>
    /// The version of the panel packed inside this exe. Read from its own tiny
    /// resource so the up-to-date path never has to decrypt the big payload.
    /// </summary>
    private static string ReadPayloadVersion()
    {
        using var stream = Assembly.GetExecutingAssembly()
                               .GetManifestResourceStream(VersionResource)
                           ?? throw new InvalidOperationException(
                               "This setup file is incomplete (" + VersionResource + " is missing).");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Trim();
    }

    private static string ReadInstalledVersion(string target)
    {
        try
        {
            string marker = Path.Combine(target, VersionEntry);
            return File.Exists(marker) ? File.ReadAllText(marker).Trim() : "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>The raw embedded payload stream. Caller owns it.</summary>
    private static Stream OpenPayloadStream()
    {
        return Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadResource)
               ?? throw new InvalidOperationException(
                   "This setup file is incomplete (no panel payload inside it).");
    }

    /// <summary>
    /// Decrypts the embedded payload to <paramref name="destination"/> and
    /// verifies it against the SHA-256 recorded at packing time.
    ///
    /// Honest note on strength: the AES key has to live inside this exe, because
    /// the panel must be unpacked on the customer's PC. That makes this
    /// deterrence - it stops "open the exe, pull the 549 MB payload out, hand it
    /// around" - not secrecy. The integrity check is what actually matters here:
    /// it makes a corrupted or spliced setup fail loudly instead of half
    /// installing.
    /// </summary>
    private static void DecryptPayload(string destination, ProgressForm form)
    {
        using var source = OpenPayloadStream();

        var header = new byte[PayloadHeaderLength];
        if (source.Read(header, 0, header.Length) != header.Length)
        {
            throw new InvalidOperationException("The panel payload is truncated.");
        }

        if (header[0] != (byte)'D' || header[1] != (byte)'X' ||
            header[2] != (byte)'P' || header[3] != (byte)'L')
        {
            throw new InvalidOperationException("The panel payload is not in the expected format.");
        }

        byte format = header[4];
        if (format != 1)
        {
            throw new InvalidOperationException(
                "This setup was packed with a newer format (" + format + ") than this reader knows.");
        }

        var iv = new byte[16];
        Buffer.BlockCopy(header, 5, iv, 0, iv.Length);

        var expected = new byte[32];
        Buffer.BlockCopy(header, 21, expected, 0, expected.Length);

        using var aes = Aes.Create();
        aes.KeySize = 256;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = PayloadKey.Get();
        aes.IV = iv;

        long total = Math.Max(1, source.Length - PayloadHeaderLength);
        long written = 0;

        using var decryptor = aes.CreateDecryptor();
        using var crypto = new CryptoStream(source, decryptor, CryptoStreamMode.Read);
        using var file = File.Create(destination);
        using var hash = SHA256.Create();

        var buffer = new byte[1024 * 1024];
        int read;
        long nextReport = 0;

        while ((read = crypto.Read(buffer, 0, buffer.Length)) > 0)
        {
            hash.TransformBlock(buffer, 0, read, null, 0);
            file.Write(buffer, 0, read);
            written += read;

            if (written >= nextReport)
            {
                form.Report("Decrypting the panel", written, total);
                nextReport = written + (16L * 1024 * 1024);
            }
        }

        hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);

        if (hash.Hash is null || !CryptographicOperations.FixedTimeEquals(hash.Hash, expected))
        {
            throw new InvalidOperationException(
                "The panel payload did not pass its integrity check - the setup file is damaged.");
        }
    }

    // ---------------------------------------------------------------- install

    private static void Install(string target, ProgressForm form)
    {
        string staging = target + ".new";
        string backup = target + ".old";

        form.Report("Preparing folder", 0, 1);

        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        Directory.CreateDirectory(staging);

        // ZipArchive needs a seekable stream, and the resource is not one.
        string tempZip = Path.Combine(Path.GetTempPath(),
                                      "dxx-payload-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            DecryptPayload(tempZip, form);
            ExtractTo(tempZip, staging, form);

            // A previous install's accounts / licence / settings must survive.
            //
            // data\ holds the licence, settings and the per-tool workspaces, and
            // accounts\ holds every created account (accounts.json, cookies.txt,
            // the tool's .env). Copying data\ alone - which is what this did -
            // meant an upgrade threw the customer's accounts away: the old folder
            // is deleted right after the swap, so there was nothing left to
            // recover them from.
            foreach (var keep in new[] { "data", "accounts" })
            {
                string old = Path.Combine(target, keep);
                if (!Directory.Exists(old)) continue;
                form.Report("Keeping your saved accounts", 0, 1);
                CopyDirectory(old, Path.Combine(staging, keep));
            }

            File.WriteAllText(Path.Combine(staging, VersionEntry), ReadPayloadVersion());

            // A panel started from this folder holds the folder open, so the
            // swap below fails with a sharing violation while it runs
            // ("The process cannot access the file because it is being used by
            // another process" - reproduced). Close it first.
            form.Report("Closing the running panel", 0, 1);
            CloseRunningPanel(target);

            form.Report("Finishing", 1, 1);

            SwapIn(staging, target, backup);
        }
        finally
        {
            TryDelete(tempZip);
            TryDeleteDirectory(staging);
        }
    }

    private static void ExtractTo(string zipPath, string destination, ProgressForm form)
    {
        string root = Path.GetFullPath(destination);

        using var zip = ZipFile.OpenRead(zipPath);

        long total = 0;
        foreach (var e in zip.Entries) total += Math.Max(0, e.Length);

        long written = 0;
        int count = 0;

        foreach (var entry in zip.Entries)
        {
            string full = Path.GetFullPath(Path.Combine(root, entry.FullName));

            // Zip-slip guard: never write outside the install folder.
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;

            if (entry.Name.Length == 0)
            {
                Directory.CreateDirectory(full);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            entry.ExtractToFile(full, overwrite: true);

            written += Math.Max(0, entry.Length);
            count++;

            if (count % 20 == 0)
            {
                form.Report("Unpacking " + Trim(entry.FullName), written, total);
            }
        }

        form.Report("Unpacked " + count + " files", total, total);
    }

    /// <summary>
    /// Replace the installed folder with the freshly unpacked one, with a few
    /// retries: an antivirus scanner or a just-closed panel can keep a handle
    /// on the folder for a moment after the process is gone.
    /// </summary>
    private static void SwapIn(string staging, string target, string backup)
    {
        Exception? last = null;

        for (int attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                if (Directory.Exists(backup)) Directory.Delete(backup, true);
                if (Directory.Exists(target)) Directory.Move(target, backup);
                Directory.Move(staging, target);
                if (Directory.Exists(backup)) Directory.Delete(backup, true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                last = ex;

                // Half-done swap: put the old install back before retrying.
                try
                {
                    if (!Directory.Exists(target) && Directory.Exists(backup))
                    {
                        Directory.Move(backup, target);
                    }
                }
                catch
                {
                    // Reported by the retry below if it keeps failing.
                }

                Thread.Sleep(1500 * attempt);
            }
        }

        throw new IOException(
            "The panel folder could not be replaced. Close the panel and try again." +
            (last is null ? "" : " (" + last.Message + ")"), last);
    }

    /// <summary>
    /// Closes any panel running from <paramref name="target"/> so its folder can
    /// be replaced. Panels started from anywhere else are left alone.
    /// </summary>
    private static void CloseRunningPanel(string target)
    {
        try
        {
            string prefix = Path.GetFullPath(target).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;

            foreach (var process in Process.GetProcessesByName(
                         Path.GetFileNameWithoutExtension(PanelExeName)))
            {
                try
                {
                    string path = process.MainModule?.FileName ?? "";

                    if (path.Length == 0 ||
                        !Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;   // a different install - not ours to close
                    }

                    process.CloseMainWindow();

                    if (!process.WaitForExit(8000))
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(5000);
                    }

                    // Give Windows a beat to release the file handles.
                    Thread.Sleep(600);
                }
                catch
                {
                    // The process died on its own, or cannot be inspected.
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch
        {
            // Never fail an install because of this.
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, dir.Substring(source.Length).TrimStart('\\')));
        }

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            string to = Path.Combine(destination, file.Substring(source.Length).TrimStart('\\'));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to, overwrite: true);
        }
    }

    private static string Trim(string path)
    {
        path = path.Replace('/', '\\');
        return path.Length <= 48 ? path : "…" + path.Substring(path.Length - 47);
    }

    // ---------------------------------------------------------------- uninstall

    /// <summary>
    /// True when this run was asked to remove the panel rather than install it.
    /// Windows' "Apps &amp; features" Uninstall button runs this same exe with the
    /// switch listed in the registry entry below.
    /// </summary>
    private static bool IsUninstall(string[] args)
    {
        return args.Any(a => a.Equals("/uninstall", StringComparison.OrdinalIgnoreCase)
                             || a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasFlag(string[] args, string flag)
    {
        return args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase)
                             || a.Equals("/" + flag, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Lists the panel in "Apps &amp; features" so a customer can remove it the
    /// normal way, instead of being told to hunt for a hidden folder.
    ///
    /// Runs on every start of this exe, not only on a fresh install: an install
    /// made before the entry existed, or one whose setup file has been moved,
    /// would otherwise never get a working entry.
    ///
    /// HKCU and not HKLM, deliberately - a machine-wide entry needs
    /// administrator rights, and installing never does. Windows shows a per-user
    /// entry to that user, which is exactly who installed it.
    /// </summary>
    private static void RegisterUninstall(string target, string version, string shortcut)
    {
        try
        {
            string self = Environment.ProcessPath ?? "";

            // Only a setup file kept outside the install folder is a safe
            // uninstall command. The copy the panel's own updater stages lives
            // inside data\ and is deleted once that update is in, which would
            // leave the entry pointing at a file that no longer exists.
            bool usable = self.Length > 0 && File.Exists(self) && !IsInside(target, self);

            using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
            if (key is null) return;

            key.SetValue("DisplayName", DisplayName);
            key.SetValue("DisplayVersion", version);
            key.SetValue("Publisher", "Developer X");
            key.SetValue("InstallLocation", target);
            key.SetValue("DisplayIcon", Path.Combine(target, PanelExeName) + ",0");
            key.SetValue("EstimatedSize", SizeInKb(target), RegistryValueKind.DWord);
            // Nothing here is repairable or changeable the way an MSI would be.
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);

            if (usable)
            {
                key.SetValue("UninstallString", "\"" + self + "\" /uninstall");
                key.SetValue("QuietUninstallString", "\"" + self + "\" /uninstall quiet");
            }

            // Remembered so uninstall removes the one shortcut this setup
            // created, and never a file of the same name that was already there.
            if (shortcut.Length > 0) key.SetValue("Shortcut", shortcut);
        }
        catch
        {
            // A locked-down profile that refuses the write must not fail an
            // install that otherwise worked.
        }
    }

    /// <summary>
    /// Removes the panel: the running panel, the desktop shortcut and the install
    /// folder - which is everything it put on the PC, since it writes no service,
    /// no Start Menu entry and nothing in Program Files. Returns the process exit
    /// code.
    /// </summary>
    private static int Uninstall(string target, bool quiet)
    {
        try
        {
            if (!quiet && MessageBox.Show(
                    "Remove " + DisplayName + " from this PC?\n\n" +
                    "Its accounts, saved sessions and licence file are removed with it.",
                    DisplayName,
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            {
                return 0;   // the customer changed their mind
            }

            CloseRunningPanel(target);
            RemoveShortcut();
            RemoveUninstallEntry();
            ForgetInstallFolder();

            if (Directory.Exists(target))
            {
                // Normally this exe sits in Downloads and the folder can go right
                // away. When it does not - the panel's updater runs the setup it
                // staged inside data\ - a process cannot delete the folder it was
                // started from, so hand that to a shell that waits for us.
                if (IsInside(target, Environment.ProcessPath ?? ""))
                {
                    ScheduleDelete(target);
                }
                else
                {
                    Directory.Delete(target, true);
                }
            }

            if (!quiet)
            {
                MessageBox.Show(
                    DisplayName + " has been removed.",
                    DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }

            return 0;
        }
        catch (Exception ex)
        {
            if (!quiet)
            {
                MessageBox.Show(
                    "The panel could not be removed completely.\n\n" + ex.Message +
                    "\n\nYou can delete this folder by hand:\n" + target,
                    DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            return 1;
        }
    }

    private static void RemoveUninstallEntry()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false); }
        catch { /* a leftover entry is cosmetic */ }
    }

    /// <summary>Deletes the desktop shortcut, but only the one we recorded.</summary>
    private static void RemoveShortcut()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
            if (key?.GetValue("Shortcut") is not string link) return;
            if (!Path.GetFileName(link).Equals(ShortcutName, StringComparison.OrdinalIgnoreCase)) return;
            if (File.Exists(link)) File.Delete(link);
        }
        catch
        {
            // A leftover shortcut is cosmetic.
        }
    }

    /// <summary>
    /// Deletes <paramref name="target"/> once this process is gone: a running exe
    /// cannot remove the folder it was started from.
    /// </summary>
    private static void ScheduleDelete(string target)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            // ping rather than timeout: timeout needs a console this process
            // does not have.
            Arguments = "/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"" + target + "\"",
            CreateNoWindow = true,
            UseShellExecute = false,
        });
    }

    /// <summary>Installed size in KB, for the "Size" column Windows shows.</summary>
    private static int SizeInKb(string folder)
    {
        try
        {
            long bytes = 0;
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                try { bytes += new FileInfo(file).Length; } catch { /* vanished mid-walk */ }
            }

            return (int)Math.Min(int.MaxValue, bytes / 1024);
        }
        catch
        {
            return 0;
        }
    }

    private static bool IsInside(string folder, string path)
    {
        if (path.Length == 0) return false;

        string prefix = Path.GetFullPath(folder).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- launching

    private static void StartPanel(string panelPath)
    {
        if (!File.Exists(panelPath))
        {
            throw new FileNotFoundException(
                "The panel did not unpack correctly. Please run this setup again.", panelPath);
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = panelPath,
            WorkingDirectory = Path.GetDirectoryName(panelPath)!,
            UseShellExecute = true,
        });
    }

    /// <summary>
    /// Desktop shortcut that points at THIS setup file, so handing the customer a
    /// newer setup later replaces their panel with no instructions needed.
    /// Returns the link it made, or "" when it made none - uninstall uses that to
    /// remove exactly the shortcut this setup created.
    /// </summary>
    private static string CreateShortcut()
    {
        try
        {
            string self = Environment.ProcessPath ?? "";
            if (self.Length == 0) return "";

            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string link = Path.Combine(desktop, ShortcutName);

            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return "";

            dynamic shell = Activator.CreateInstance(shellType)!;

            // Never touch a shortcut this setup did not create. A customer can
            // easily already own a file with a similar name - an unrelated
            // ".lnk" is not ours to overwrite.
            if (File.Exists(link))
            {
                try
                {
                    dynamic existing = shell.CreateShortcut(link);
                    string existingTarget = (string)existing.TargetPath;
                    if (!string.Equals(existingTarget, self, StringComparison.OrdinalIgnoreCase))
                    {
                        return "";
                    }
                }
                catch
                {
                    return "";
                }
            }

            dynamic shortcut = shell.CreateShortcut(link);
            shortcut.TargetPath = self;
            shortcut.WorkingDirectory = Path.GetDirectoryName(self)!;
            shortcut.Description = DisplayName;
            shortcut.Save();

            return link;
        }
        catch
        {
            // A missing shortcut is cosmetic - never fail the install over it.
            return "";
        }
    }

    // ---------------------------------------------------------------- helpers

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
    }
}
