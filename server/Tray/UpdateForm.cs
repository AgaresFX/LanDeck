using PadNook.Core;

namespace PadNook.Tray;

/// <summary>Download progress of an update. NewExe is set when the exe was swapped successfully.</summary>
internal sealed class UpdateForm : Form
{
    private readonly ReleaseInfo _info;
    private readonly ProgressBar _bar = new() { Dock = DockStyle.Top, Height = 22, Maximum = 100 };
    private readonly Label _label = new() { Dock = DockStyle.Top, Height = 32, TextAlign = ContentAlignment.MiddleLeft };

    public string? NewExe { get; private set; }

    public UpdateForm(ReleaseInfo info)
    {
        _info = info;
        Text = L.T("PadNook – Aggiornamento");
        Icon = AppIcon.Get();
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(380, 76);
        Padding = new Padding(12);
        _label.Text = L.F("Download di PadNook {0}…", info.Version);
        Controls.Add(_bar);
        Controls.Add(_label);
        TopMost = true;
        Shown += async (_, _) => await RunAsync();
    }

    private async Task RunAsync()
    {
        try
        {
            var progress = new Progress<int>(p =>
            {
                _bar.Value = Math.Clamp(p, 0, 100);
                _label.Text = L.F("Download di PadNook {0}… {1}%", _info.Version, p);
            });
            NewExe = await Updater.DownloadAndSwapAsync(_info, progress);
            _label.Text = L.T("Riavvio…");
        }
        catch (Exception ex)
        {
            Log.Error("Update failed", ex);
            MessageBox.Show(this, L.F("Aggiornamento non riuscito:\n{0}", ex.Message), "PadNook", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        Close();
    }
}
