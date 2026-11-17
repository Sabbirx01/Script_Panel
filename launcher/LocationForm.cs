using System.Drawing;
using System.IO;

namespace LaunchPanel;

/// <summary>
/// Asks where the panel should be installed, before a single byte is unpacked.
///
/// <para>Shown once. The folder is remembered, so the desktop shortcut and every
/// later run of this setup go straight back to the same place instead of asking
/// again - and the panel's own updater, which runs this setup from inside the
/// install folder, needs no answer at all.</para>
///
/// <para>Default is the customer's own folder
/// (<c>%LOCALAPPDATA%\DeveloperXPanel</c>) because it always exists and never
/// needs administrator rights, which is what makes the install click-free. A
/// customer who wants it elsewhere picks a folder here - typically another
/// drive.</para>
///
/// <para>Hand-written rather than designer-generated on purpose: this project is
/// built by a script, so the build stays one compiler run with no XAML step.</para>
/// </summary>
internal sealed class LocationForm : Form
{
    private const string PanelExeName = "DeveloperXBulkAccPanel.exe";
    private const string DisplayName = "Developer X Bulk Acc Panel";

    private readonly TextBox _path = new();
    private readonly Label _problem = new();

    /// <summary>Where the customer chose to install. Only valid with OK.</summary>
    public string SelectedPath { get; private set; } = "";

    public LocationForm(string suggested)
    {
        Text = "Developer X Panel - Setup";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        ClientSize = new Size(540, 268);
        BackColor = Color.FromArgb(11, 14, 32);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 9F);

        var title = new Label
        {
            Text = "Where should " + DisplayName + " be installed?",
            Font = new Font("Segoe UI", 12F, FontStyle.Bold),
            ForeColor = Color.White,
            AutoSize = true,
            Location = new Point(26, 22),
        };

        var subtitle = new Label
        {
            Text = "The folder below is used by default. Pick another one - for example on drive D: - if you prefer.",
            Font = new Font("Segoe UI", 9F),
            ForeColor = Color.FromArgb(140, 150, 185),
            AutoSize = false,
            Size = new Size(490, 34),
            Location = new Point(26, 50),
        };

        _path.Text = suggested;
        _path.Font = new Font("Consolas", 9F);
        _path.BackColor = Color.FromArgb(20, 24, 48);
        _path.ForeColor = Color.White;
        _path.BorderStyle = BorderStyle.FixedSingle;
        _path.Size = new Size(396, 24);
        _path.Location = new Point(26, 92);

        var browse = new Button
        {
            Text = "Browse…",
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(40, 44, 72),
            ForeColor = Color.White,
            Size = new Size(88, 25),
            Location = new Point(430, 91),
            TabStop = false,
        };
        browse.FlatAppearance.BorderColor = Color.FromArgb(70, 78, 120);
        browse.Click += OnBrowse;

        _problem.AutoSize = false;
        _problem.Size = new Size(492, 32);
        _problem.Location = new Point(26, 122);
        _problem.ForeColor = Color.FromArgb(255, 130, 130);
        _problem.Font = new Font("Segoe UI", 8.5F);
        _problem.Text = "";

        var install = new Button
        {
            Text = "Install",
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(32, 86, 190),
            ForeColor = Color.White,
            Size = new Size(120, 32),
            Location = new Point(26, 170),
        };
        install.FlatAppearance.BorderSize = 0;
        install.Click += OnInstall;

        var cancel = new Button
        {
            Text = "Cancel",
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(40, 44, 72),
            ForeColor = Color.White,
            Size = new Size(100, 32),
            Location = new Point(156, 170),
        };
        cancel.FlatAppearance.BorderColor = Color.FromArgb(70, 78, 120);
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        var note = new Label
        {
            Text = "Everything the panel needs is inside this one file - nothing else to download.",
            Font = new Font("Segoe UI", 8.5F),
            ForeColor = Color.FromArgb(120, 130, 165),
            AutoSize = false,
            Size = new Size(492, 20),
            Location = new Point(26, 220),
        };

        Controls.Add(title);
        Controls.Add(subtitle);
        Controls.Add(_path);
        Controls.Add(browse);
        Controls.Add(_problem);
        Controls.Add(install);
        Controls.Add(cancel);
        Controls.Add(note);

        AcceptButton = install;
        CancelButton = cancel;
        ActiveControl = _path;
        _path.SelectAll();
    }

    private void OnBrowse(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose the folder to install " + DisplayName + " into",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            SelectedPath = Existing(_path.Text),
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _path.Text = dialog.SelectedPath;
            _problem.Text = "";
        }
    }

    private void OnInstall(object? sender, EventArgs e)
    {
        if (!Validate(_path.Text, out string problem))
        {
            _problem.Text = problem;
            _path.Focus();
            return;
        }

        SelectedPath = Path.GetFullPath(_path.Text.Trim().Trim('"'));
        DialogResult = DialogResult.OK;
        Close();
    }

    /// <summary>
    /// Refuses the folders the install would damage or that Windows would not
    /// allow. The install REPLACES the whole target folder (it unpacks beside it
    /// and swaps it in), so anything already living there that is not a previous
    /// install would be thrown away - which is why a non-empty folder is
    /// rejected rather than merged.
    /// </summary>
    private static bool Validate(string text, out string problem)
    {
        problem = "";

        string typed = text.Trim().Trim('"');
        if (typed.Length == 0)
        {
            problem = "Type a folder, or choose one with Browse…";
            return false;
        }

        string full;
        try
        {
            full = Path.GetFullPath(typed);
        }
        catch
        {
            problem = "That is not a valid folder path.";
            return false;
        }

        // A whole drive is never a folder to install into - and the swap would
        // try to move it.
        string? root = Path.GetPathRoot(full);
        if (root is null || full.TrimEnd('\\', '/').Equals(
                root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
        {
            problem = "Choose a folder on the drive, not the drive itself.";
            return false;
        }

        if (IsProtected(full))
        {
            problem = "Windows keeps that area for itself. Choose a folder in your own user area, " +
                      "or on another drive (for example D:\\DeveloperXPanel).";
            return false;
        }

        if (Directory.Exists(full) && !IsEmpty(full) && !LooksLikeOurInstall(full))
        {
            problem = "That folder already holds other files. Choose an empty folder, or make a new one.";
            return false;
        }

        // About 1.3 GB is unpacked. Refusing a drive that cannot hold it beats
        // failing half way through the unpack.
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(full) ?? "");
            if (drive.IsReady && drive.AvailableFreeSpace < 2L * 1024 * 1024 * 1024)
            {
                long freeGb = drive.AvailableFreeSpace / (1024L * 1024 * 1024);
                problem = "That drive has only " + freeGb + " GB free. The panel needs about 1.3 GB.";
                return false;
            }
        }
        catch
        {
            // A drive that cannot be queried is not a reason to refuse.
        }

        // Prove it is writable before unpacking 1 GB into it.
        try
        {
            Directory.CreateDirectory(full);
            string probe = Path.Combine(full, ".dx-write-test");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            problem = "Windows will not let anything be written there: " + ex.Message;
            return false;
        }

        return true;
    }

    private static bool IsProtected(string folder)
    {
        string[] roots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        ];

        string withSlash = folder.TrimEnd('\\', '/') + "\\";

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            if (withSlash.StartsWith(root.TrimEnd('\\', '/') + "\\", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool LooksLikeOurInstall(string folder)
    {
        try
        {
            return Directory.Exists(folder) &&
                   (File.Exists(Path.Combine(folder, PanelExeName)) ||
                    File.Exists(Path.Combine(folder, "portable.txt")));
        }
        catch
        {
            return false;
        }
    }

    private static bool IsEmpty(string folder)
    {
        try
        {
            return !Directory.EnumerateFileSystemEntries(folder).Any();
        }
        catch
        {
            return false;   // unreadable: treat as not empty and refuse
        }
    }

    /// <summary>The nearest folder that exists, for the browse dialog to open at.</summary>
    private static string Existing(string path)
    {
        try
        {
            string full = Path.GetFullPath(path.Trim().Trim('"'));
            while (full.Length > 0 && !Directory.Exists(full))
            {
                string? parent = Path.GetDirectoryName(full);
                if (parent is null || parent == full) break;
                full = parent;
            }

            return Directory.Exists(full)
                ? full
                : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }
        catch
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }
    }
}
