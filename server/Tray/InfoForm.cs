using System.Runtime.InteropServices;
using PadNook.Platform;
using QRCoder;

namespace PadNook.Tray;

/// <summary>Window with address, QR code and pairing code (dark, editor-styled).</summary>
internal sealed class InfoForm : Form
{
    // Palette mirrored from webapp/src/styles.css
    private static readonly Color Bg = ColorTranslator.FromHtml("#0f1012");
    private static readonly Color Surface = ColorTranslator.FromHtml("#16171a");
    private static readonly Color Surface2 = ColorTranslator.FromHtml("#1e1f23");
    private static readonly Color Surface3 = ColorTranslator.FromHtml("#27282d");
    private static readonly Color Border = ColorTranslator.FromHtml("#2a2b30");
    private static readonly Color TextC = ColorTranslator.FromHtml("#ecedf0");
    private static readonly Color Text2 = ColorTranslator.FromHtml("#a9abb2");
    private static readonly Color Muted = ColorTranslator.FromHtml("#7d7f87");
    private static readonly Color Accent = ColorTranslator.FromHtml("#4a7dff");
    private static readonly Color Ok = ColorTranslator.FromHtml("#3fbf72");

    private const int ContentWidth = 340;

    private readonly AppServices _svc;
    private readonly ComboBox _ipBox = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        DrawMode = DrawMode.OwnerDrawFixed,
        FlatStyle = FlatStyle.Standard, // themed by DarkMode_CFD in OnHandleCreated
        Width = ContentWidth,
        ItemHeight = 24,
        BackColor = Surface2,
        ForeColor = TextC,
        Margin = new Padding(0, 4, 0, 0),
    };
    private readonly LinkLabel _url = new()
    {
        AutoSize = true,
        LinkColor = Accent,
        ActiveLinkColor = Accent,
        VisitedLinkColor = Accent,
        LinkBehavior = LinkBehavior.HoverUnderline,
        Font = new Font("Cascadia Mono", 10f),
        Margin = new Padding(0, 4, 0, 0),
    };
    private readonly PictureBox _qr = new() { Width = 264, Height = 264, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.White };
    private readonly Label _code = new()
    {
        AutoSize = true,
        Font = new Font("Cascadia Mono", 30f, FontStyle.Bold),
        ForeColor = TextC,
        Margin = new Padding(0, 2, 0, 0),
    };
    private readonly Label _expiry = new() { AutoSize = true, ForeColor = Muted, Margin = new Padding(2, 0, 0, 0) };
    private readonly Label _status = new() { AutoSize = true, ForeColor = Ok, MaximumSize = new Size(ContentWidth, 0) };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly Action _pairedHandler;
    private DateTimeOffset _expires;
    private string _currentCode = "";

    public InfoForm(AppServices svc, Action openEditor)
    {
        _svc = svc;
        Text = L.T("PadNook – Collega un dispositivo");
        Icon = AppIcon.Get();
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 10f);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        ShowInTaskbar = true;
        BackColor = Bg;
        ForeColor = TextC;

        var root = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            BackColor = Bg,
            Padding = new Padding(20, 16, 20, 20),
            Margin = new Padding(0),
        };

        root.Controls.Add(new Label
        {
            Text = L.T("Collega un dispositivo"),
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 14f),
            ForeColor = TextC,
            Margin = new Padding(0, 0, 0, 2),
        });
        root.Controls.Add(new Label
        {
            Text = L.T("Inquadra il QR code con la fotocamera di iPhone o iPad. Il dispositivo deve essere sulla stessa rete Wi-Fi del PC."),
            AutoSize = true,
            MaximumSize = new Size(ContentWidth, 0),
            ForeColor = Text2,
            Margin = new Padding(0, 0, 0, 14),
        });

        // QR on a white card (the quiet zone must stay light for scanning)
        var qrCard = new Panel
        {
            Width = ContentWidth,
            Height = _qr.Height + 28,
            BackColor = Color.White,
            Margin = new Padding(0),
        };
        _qr.Location = new Point((qrCard.Width - _qr.Width) / 2, 14);
        qrCard.Controls.Add(_qr);
        qrCard.Resize += (_, _) => Round(qrCard, 10);
        Round(qrCard, 10);
        root.Controls.Add(qrCard);

        root.Controls.Add(Caption("RETE", 16));
        root.Controls.Add(_ipBox);
        _ipBox.DrawItem += DrawIpItem;

        root.Controls.Add(Caption(L.T("INDIRIZZO"), 12));
        root.Controls.Add(_url);
        _url.LinkClicked += (_, _) => CopyAddress();

        root.Controls.Add(Caption(L.T("CODICE DI ACCOPPIAMENTO"), 12));
        root.Controls.Add(new Label
        {
            Text = L.T("Per l'app aggiunta alla schermata Home."),
            AutoSize = true,
            ForeColor = Muted,
            Margin = new Padding(0, 2, 0, 0),
        });
        root.Controls.Add(_code);
        root.Controls.Add(_expiry);
        root.Controls.Add(_status);

        var buttons = new TableLayoutPanel
        {
            ColumnCount = 3,
            RowCount = 1,
            Width = ContentWidth,
            Height = 34,
            Margin = new Padding(0, 18, 0, 0),
            BackColor = Bg,
        };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 31));
        var newCode = MakeButton(L.T("Nuovo codice"), primary: false);
        newCode.Click += (_, _) => RefreshCode(forceNew: true);
        var copy = MakeButton(L.T("Copia indirizzo"), primary: false);
        copy.Click += (_, _) => CopyAddress();
        var editor = MakeButton(L.T("Apri editor"), primary: true);
        editor.Margin = new Padding(0);
        editor.Click += (_, _) => openEditor();
        buttons.Controls.Add(newCode, 0, 0);
        buttons.Controls.Add(copy, 1, 0);
        buttons.Controls.Add(editor, 2, 0);
        root.Controls.Add(buttons);

        Controls.Add(root);

        foreach (var a in NetInfo.GetLanAddresses())
            _ipBox.Items.Add(new IpItem(a));
        if (_ipBox.Items.Count == 0)
            _ipBox.Items.Add(L.T("Nessuna rete locale trovata"));
        _ipBox.SelectedIndex = 0;
        _ipBox.SelectedIndexChanged += (_, _) => Render();

        _pairedHandler = () => BeginInvokeSafe(() =>
        {
            _status.Text = L.T("✓ Dispositivo associato. Nuovo codice generato.");
            RefreshCode(forceNew: true);
        });
        _svc.Pairing.TrayCodeConsumed += _pairedHandler;

        _timer.Tick += (_, _) => UpdateExpiry();
        _timer.Start();
        RefreshCode(forceNew: false);
    }

    private static Label Caption(string text, int top) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font("Segoe UI Semibold", 8.5f),
        ForeColor = Muted,
        Margin = new Padding(0, top, 0, 0),
    };

    private static Button MakeButton(string text, bool primary)
    {
        var b = new Button
        {
            Text = text,
            Dock = DockStyle.Fill,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5f),
            BackColor = primary ? Accent : Surface2,
            ForeColor = primary ? Color.White : TextC,
            Padding = new Padding(0),
            Margin = new Padding(0, 0, 6, 0),
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
        };
        b.FlatAppearance.BorderColor = primary ? Accent : Border;
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.MouseOverBackColor = primary ? ControlPaint.Light(Accent, 0.15f) : Surface3;
        b.FlatAppearance.MouseDownBackColor = primary ? ControlPaint.Dark(Accent, 0.05f) : Surface;
        return b;
    }

    private void DrawIpItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        var selected = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
        using (var bg = new SolidBrush(selected ? Surface3 : Surface2))
            e.Graphics.FillRectangle(bg, e.Bounds);
        var text = _ipBox.Items[e.Index]?.ToString() ?? "";
        var rect = new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 6, e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, text, _ipBox.Font, rect, TextC,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
    }

    private static void Round(Control c, int radius)
    {
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        var d = radius * 2;
        var r = new Rectangle(0, 0, c.Width, c.Height);
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        c.Region?.Dispose();
        c.Region = new Region(path);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? appName, string? idList);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            SetWindowTheme(_ipBox.Handle, "DarkMode_CFD", null); // dark drop-down arrow + list

            var on = 1;
            DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
            var caption = ColorTranslator.ToWin32(Bg);
            DwmSetWindowAttribute(Handle, 35, ref caption, sizeof(int)); // DWMWA_CAPTION_COLOR (Win11)
        }
        catch { }
    }

    private sealed record IpItem(LanAddress Address)
    {
        public override string ToString() => $"{Address.Address}  ({Address.InterfaceName})";
    }

    private string? SelectedIp => (_ipBox.SelectedItem as IpItem)?.Address.Address.ToString();

    private string BaseUrl => SelectedIp is { } ip ? $"http://{ip}:{_svc.Port}" : $"http://localhost:{_svc.Port}";

    private void RefreshCode(bool forceNew)
    {
        var (code, expires) = _svc.Pairing.GetTrayCode(forceNew);
        _currentCode = code;
        _expires = expires;
        Render();
    }

    private void Render()
    {
        _url.Text = BaseUrl;
        _code.Text = $"{_currentCode[..3]} {_currentCode[3..]}";
        UpdateExpiry();

        var qrText = $"{BaseUrl}/#pair={_currentCode}";
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(qrText, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(10);
        using var ms = new MemoryStream(png);
        using var img = Image.FromStream(ms);
        var old = _qr.Image;
        _qr.Image = new Bitmap(img);
        old?.Dispose();
    }

    private void UpdateExpiry()
    {
        var left = _expires - DateTimeOffset.Now;
        if (left <= TimeSpan.Zero)
        {
            RefreshCode(forceNew: true);
            return;
        }
        _expiry.Text = L.F("Valido ancora {0}:{1:D2} · si usa una sola volta", left.Minutes, left.Seconds);
    }

    private void CopyAddress()
    {
        try
        {
            Clipboard.SetText(BaseUrl);
            _status.Text = L.T("Indirizzo copiato negli appunti.");
        }
        catch { }
    }

    private void BeginInvokeSafe(Action a)
    {
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke(a);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _svc.Pairing.TrayCodeConsumed -= _pairedHandler;
        _timer.Dispose();
        _qr.Image?.Dispose();
        base.OnFormClosed(e);
    }
}
