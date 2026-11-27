namespace PadNook.Tray;

/// <summary>Small dialog to choose a new port.</summary>
internal sealed class PortForm : Form
{
    private readonly NumericUpDown _port = new() { Minimum = 1024, Maximum = 65535, Width = 120 };

    public int Port => (int)_port.Value;

    public PortForm(int current)
    {
        Text = L.T("PadNook – Porta");
        Icon = AppIcon.Get();
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 10f);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);
        _port.Value = current;

        var root = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        root.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(380, 0),
            Text = L.T("Porta su cui PadNook accetta le connessioni (1024–65535).\n\n") +
                   L.T("Dopo il cambio PadNook si riavvia e chiede di aggiornare la regola del firewall. ") +
                   L.T("Sui dispositivi dovrai aprire il nuovo indirizzo (inquadra di nuovo il QR code): ") +
                   L.T("l'associazione resta valida."),
        });
        root.Controls.Add(_port);

        var buttons = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 12, 0, 0) };
        var ok = new Button { Text = L.T("Salva e riavvia"), AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = L.T("Annulla"), AutoSize = true, DialogResult = DialogResult.Cancel };
        buttons.Controls.AddRange([ok, cancel]);
        root.Controls.Add(buttons);
        Controls.Add(root);
        AcceptButton = ok;
        CancelButton = cancel;
    }
}
