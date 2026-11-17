using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using Microsoft.Win32;

namespace AISuitePanel;

/// <summary>
/// Lists the panel in Windows' "Apps &amp; features" and removes it again when
/// that Uninstall button is used.
///
/// <para><b>Why the panel owns the entry, not only the setup.</b> The setup file
/// the customer downloaded is not part of the install - it stays wherever they
/// saved it, and the auto-updater never replaces it (it only swaps files inside
/// the install folder). So a machine installed before this existed had no entry
/// at all, and one whose setup file has since been deleted or moved had an
/// Uninstall button that pointed at nothing. The panel IS part of the install
/// and IS what the updater keeps current, so it registers and repairs the entry
/// on every start.</para>
///
/// <para><b>Uninstalling.</b> Windows runs this exe again with
/// <c>/uninstall</c>. That closes the panel and deletes its own folder from a
/// short-lived shell, because a running program cannot remove the folder it was
/// started from.</para>
/// </summary>
internal static class UninstallService
{
    private const string DisplayName  = "Developer X Bulk Acc Panel";
    private const string ShortcutName = "Developer X Bulk Acc Panel.lnk";
    private const string PanelExeName = "DeveloperXBulkAccPanel.exe";
    private const string SetupExeName = "DeveloperXPanel-Setup.exe";

    /// <summary>Where Windows' "Apps &amp; features" looks for the Uninstall button.</summary>
    private const string UninstallKey =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\DeveloperXPanel";

    /// <summary>Where the setup file remembers the folder the customer chose.</summary>
    private const string AppKey = @"Software\DeveloperXPanel";

    /// <summary>True when this run was asked to remove the panel.</summary>
    public static bool IsUninstall(string[] args) =>
        args.Any(a => a.Equals("/uninstall", StringComparison.OrdinalIgnoreCase)
                      || a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase));

    private static bool HasFlag(string[] args, string flag) =>
        args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase)
                      || a.Equals("/" + flag, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Makes sure "Apps &amp; features" shows the panel with a working Uninstall
    /// button, and repairs the entry when it is missing or names a file that is
    /// gone. A working entry is left exactly as it is, so a fresh install keeps
    /// the setup-based command its launcher registered.
    /// </summary>
    public static void EnsureEntry()
    {
        // Only an installed copy belongs in "Apps & features": the bundle writes
        // portable.txt beside the exe, a development copy has none.
        if (!AppPaths.Portable) return;

        try
        {
            // The folder the panel runs from IS the install folder - that is
            // where the setup unpacked it. Deliberately not the resolved payload
            // root: that one walks up to whichever folder actually holds a
            // tools\, so a panel started from anywhere else would list - and
            // later try to delete - a folder that is not its own.
            string target = AppPaths.AppRoot;

            using (var existing = Registry.CurrentUser.OpenSubKey(UninstallKey))
            {
                if (existing is not null &&
                    CommandExists(existing.GetValue("UninstallString") as string ?? ""))
                {
                    return;
                }
            }

            string self = Environment.ProcessPath ?? Path.Combine(target, PanelExeName);

            using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
            if (key is null) return;

            key.SetValue("DisplayName", DisplayName);
            key.SetValue("DisplayVersion", UpdateService.CurrentVersion);
            key.SetValue("Publisher", "Developer X");
            key.SetValue("InstallLocation", target);
            key.SetValue("DisplayIcon", self + ",0");
            key.SetValue("EstimatedSize", SizeInKb(target), RegistryValueKind.DWord);
            // Nothing here is repairable or changeable the way an MSI would be.
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.SetValue("UninstallString", "\"" + self + "\" /uninstall");
            key.SetValue("QuietUninstallString", "\"" + self + "\" /uninstall quiet");
        }
        catch
        {
            // A profile that refuses the write must not affect the panel.
        }
    }

    /// <summary>
    /// Removes the panel: anything it is running, the desktop shortcut and the
    /// whole install folder, which is everything it put on the PC - it installs
    /// no service and writes nothing outside its own folder. Returns the process
    /// exit code.
    /// </summary>
    public static int Run(string[] args)
    {
        bool quiet = HasFlag(args, "quiet");

        // Only an installed copy is a real installation: the bundle writes
        // portable.txt beside the exe. A development copy - the build dropped in
        // output\ - is not, and running /uninstall on it must do nothing.
        if (!AppPaths.Portable)
        {
            if (!quiet)
            {
                MessageBox.Show(
                    "This copy of " + DisplayName + " is not installed, so there is nothing to remove.",
                    DisplayName, MessageBoxButton.OK, MessageBoxImage.Information);
            }
            return 1;
        }

        string target = AppPaths.AppRoot;

        try
        {
            if (!quiet && MessageBox.Show(
                    "Remove " + DisplayName + " from this PC?\n\n" +
                    "Its accounts, saved sessions and licence file are removed with it.",
                    DisplayName,
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                return 0;   // the customer changed their mind
            }

            // Any panel still running from this folder holds the exe open, so the
            // folder could not be deleted under it. Closing it also takes its
            // python runs and their browsers with it.
            CloseRunningPanel(target);

            RemoveShortcut();
            RemoveEntry();
            ScheduleDelete(target);

            if (!quiet)
            {
                MessageBox.Show(DisplayName + " has been removed.", DisplayName,
                                MessageBoxButton.OK, MessageBoxImage.Information);
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
                    DisplayName, MessageBoxButton.OK, MessageBoxImage.Error);
            }

            return 1;
        }
    }

    /// <summary>
    /// Closes every panel running from <paramref name="target"/> - never this
    /// process, which still has work to do - so the folder can be deleted. A
    /// panel started from somewhere else is not ours to close.
    /// </summary>
    private static void CloseRunningPanel(string target)
    {
        int self = Environment.ProcessId;
        string prefix = Path.GetFullPath(target).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;

        foreach (var process in Process.GetProcessesByName(
                     Path.GetFileNameWithoutExtension(PanelExeName)))
        {
            try
            {
                if (process.Id == self) continue;

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

    /// <summary>Deletes the "Apps &amp; features" entry and the remembered folder.</summary>
    private static void RemoveEntry()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false); }
        catch { /* a leftover entry is cosmetic */ }

        // The setup file wrote this so a custom install folder is chosen once.
        // Left behind, it would outlive the folder it names.
        try { Registry.CurrentUser.DeleteSubKeyTree(AppKey, throwOnMissingSubKey: false); }
        catch { /* a leftover value is cosmetic */ }
    }

    /// <summary>
    /// Deletes the desktop shortcut. Prefers the one the launcher recorded, and
    /// otherwise falls back to the well-known name - removing it only when it
    /// really is this panel's launcher, never a file that merely shares the name.
    /// </summary>
    private static void RemoveShortcut()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
            if (key?.GetValue("Shortcut") is string recorded &&
                Path.GetFileName(recorded).Equals(ShortcutName, StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(recorded)) File.Delete(recorded);
                return;
            }
        }
        catch
        {
            // fall through to the well-known location
        }

        try
        {
            string link = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ShortcutName);
            if (!File.Exists(link)) return;

            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return;

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(link);
            string target = (string)shortcut.TargetPath ?? "";

            // A shortcut of the same name that points somewhere else is not ours.
            string leaf = Path.GetFileName(target);
            if (leaf.Equals(PanelExeName, StringComparison.OrdinalIgnoreCase) ||
                leaf.Equals(SetupExeName, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(link);
            }
        }
        catch
        {
            // A leftover shortcut is cosmetic.
        }
    }

    /// <summary>
    /// Deletes <paramref name="target"/> once this process is gone: a running
    /// program cannot remove the folder it was started from.
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

    /// <summary>True when a registered uninstall command still names a real file.</summary>
    private static bool CommandExists(string command)
    {
        var text = command.Trim();
        if (text.Length == 0) return false;

        // The path is quoted whenever it can contain spaces, which is the only
        // form this project writes; a bare path is taken up to the first space.
        string path;
        if (text[0] == '"')
        {
            int end = text.IndexOf('"', 1);
            path = end > 1 ? text[1..end] : "";
        }
        else
        {
            int space = text.IndexOf(' ');
            path = space > 0 ? text[..space] : text;
        }

        try { return path.Length > 0 && File.Exists(path); }
        catch { return false; }
    }
}
