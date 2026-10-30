using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;

namespace LaunchPanel;

/// <summary>
/// DeveloperXPanel-Setup.exe — the single file a customer keeps.
///
/// It carries the whole panel (panel exe, tools\ , runtime\ , .playwright\)
/// inside itself as one zip resource. On the first run it unpacks that zip into
/// %LOCALAPPDATA%\DeveloperXPanel and then starts the panel from there; on later
/// runs it sees the version already matches and starts the panel straight away.
///
/// Why %LOCALAPPDATA% and not beside the exe: a customer may launch the setup
/// from a read-only place (a download folder, a USB stick, a chat client's temp
/// dir), so the payload has to land somewhere the user can always write to.
///
/// The customer's own account data lives in that same install folder as
/// data\, and is carried across a re-install instead of being wiped.
/// </summary>
internal static class Program
{
    private const string PanelExeName      = "DeveloperXBulkAccPanel.exe";
    private const string AppFolderName     = "DeveloperXPanel";
    // The panel travels AES-256 encrypted. The version marker is a separate,
    // tiny resource on purpose: reading it must not cost a 549 MB decrypt, or
    // every launch after the first would stall on it.
    private const string PayloadResource   = "payload.enc";
    private const string VersionResource   = "bundle-version.txt";
    private const string VersionEntry      = "bundle-version.txt";
    private const string ShortcutName      = "Developer X Bulk Acc Panel.lnk";

    // "DXPL" + format byte + 16 byte IV + 32 byte SHA-256 of the plaintext
    private const int PayloadHeaderLength = 4 + 1 + 16 + 32;

    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);

        string target = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppFolderName);

        try
        {
            string wanted = ReadPayloadVersion();
            string have = ReadInstalledVersion(target);
            string panel = Path.Combine(target, PanelExeName);

            bool upToDate = string.Equals(wanted, have, StringComparison.OrdinalIgnoreCase)
                            && File.Exists(panel);

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

                CreateShortcut();
            }

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
    /// </summary>
    private static void CreateShortcut()
    {
        try
        {
            string self = Environment.ProcessPath ?? "";
            if (self.Length == 0) return;

            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string link = Path.Combine(desktop, ShortcutName);

            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return;

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
                        return;
                    }
                }
                catch
                {
                    return;
                }
            }

            dynamic shortcut = shell.CreateShortcut(link);
            shortcut.TargetPath = self;
            shortcut.WorkingDirectory = Path.GetDirectoryName(self)!;
            shortcut.Description = "Developer X Bulk Acc Panel";
            shortcut.Save();
        }
        catch
        {
            // A missing shortcut is cosmetic - never fail the install over it.
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
