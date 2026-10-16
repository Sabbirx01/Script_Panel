using System.Drawing;

namespace LaunchPanel;

/// <summary>
/// The little window the customer sees while the bundled panel unpacks.
/// No designer file on purpose - this project is built from a script, so a
/// hand-written form keeps the build to one compiler run with no XAML step.
/// </summary>
internal sealed class ProgressForm : Form
{
    private readonly ProgressBar _bar = new();
    private readonly Label _status = new();

    public bool Succeeded { get; private set; }

    public ProgressForm(string version)
    {
        Text = "Developer X Panel";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(452, 132);
        BackColor = Color.FromArgb(11, 14, 32);
        ShowInTaskbar = true;

        var border = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(11, 14, 32),
            Padding = new Padding(1),
        };

        var title = new Label
        {
            Text = "Setting up your panel",
            Font = new Font("Segoe UI", 12F, FontStyle.Bold),
            ForeColor = Color.White,
            AutoSize = true,
            Location = new Point(26, 24),
        };

        var subtitle = new Label
        {
            Text = "version " + version + "  ·  this only happens once",
            Font = new Font("Segoe UI", 9F),
            ForeColor = Color.FromArgb(140, 150, 185),
            AutoSize = true,
            Location = new Point(26, 49),
        };

        _status.Text = "Starting";
        _status.Font = new Font("Segoe UI", 8.5F);
        _status.ForeColor = Color.FromArgb(176, 184, 214);
        _status.AutoEllipsis = true;
        _status.AutoSize = false;
        _status.Size = new Size(400, 18);
        _status.Location = new Point(26, 72);

        _bar.Minimum = 0;
        _bar.Maximum = 1000;
        _bar.Style = ProgressBarStyle.Continuous;
        _bar.Size = new Size(400, 10);
        _bar.Location = new Point(26, 96);

        border.Controls.Add(title);
        border.Controls.Add(subtitle);
        border.Controls.Add(_status);
        border.Controls.Add(_bar);
        Controls.Add(border);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
    }

    /// <summary>Safe to call from the worker thread.</summary>
    public void Report(string what, long written, long total)
    {
        OnUi(() =>
        {
            _status.Text = what;
            _bar.Value = total > 0
                ? (int)Math.Clamp(written * 1000 / total, 0, 1000)
                : 0;
        });
    }

    public void Finish()
    {
        OnUi(() =>
        {
            Succeeded = true;
            _bar.Value = 1000;
            _status.Text = "Starting the panel…";
        });

        // Let the last frame paint before the window disappears.
        Thread.Sleep(400);
        OnUi(Close);
    }

    public void Fail(Exception ex)
    {
        OnUi(() =>
        {
            Succeeded = false;

            string log = WriteLog(ex);

            MessageBox.Show(
                "The panel could not be unpacked.\n\n" + ex.Message +
                (log.Length > 0 ? "\n\nDetails: " + log : ""),
                "Developer X Panel",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);

            Close();
        });
    }

    private static string WriteLog(Exception ex)
    {
        try
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DeveloperXPanel-setup-error.txt");

            File.WriteAllText(path,
                DateTime.Now.ToString("u") + Environment.NewLine +
                ex + Environment.NewLine);

            return path;
        }
        catch
        {
            return "";
        }
    }

    private void OnUi(Action action)
    {
        try
        {
            if (!IsHandleCreated || IsDisposed) return;

            if (InvokeRequired) BeginInvoke(action);
            else action();
        }
        catch (ObjectDisposedException)
        {
            // Window already gone - nothing to report.
        }
    }
}
