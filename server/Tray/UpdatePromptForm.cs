using System.Text.RegularExpressions;
using PadNook.Core;

namespace PadNook.Tray;

/// <summary>
/// Asks whether to install an update and shows the release notes. The notes are Markdown (GitHub release
/// body): headings, bullets, bold and links are rendered instead of showing the raw symbols.
/// </summary>
internal sealed partial class UpdatePromptForm : Form
{
    public UpdatePromptForm(ReleaseInfo info)
    {
        Text = L.T("PadNook – Aggiornamento");
        Icon = AppIcon.Get();
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9.75f);
        BackColor = Color.White;
        ClientSize = new Size(580, 580);
        TopMost = true;

        // ---- Header: icon, title, versions ----
        var header = new Panel { Dock = DockStyle.Top, Height = 84, Padding = new Padding(20, 16, 20, 8) };
        var logo = new PictureBox
        {
            Image = AppIcon.Get().ToBitmap(),
            SizeMode = PictureBoxSizeMode.Zoom,
            Size = new Size(44, 44),
            Location = new Point(20, 18),
        };
        var title = new Label
        {
            Text = L.F("È disponibile PadNook {0}", info.Version),
            Font = new Font("Segoe UI Semibold", 14f),
            ForeColor = Color.FromArgb(0, 51, 153),
            AutoSize = true,
            Location = new Point(76, 14),
        };
        var sub = new Label
        {
            Text = L.F("Hai la versione {0} · download di {1:0.#} MB", Program.Version, info.Size / 1048576.0),
            ForeColor = Color.FromArgb(96, 96, 96),
            AutoSize = true,
            Location = new Point(78, 46),
        };
        header.Controls.AddRange([logo, title, sub]);

        // ---- Release notes ----
        var notesBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = Color.FromArgb(247, 248, 250),
            ScrollBars = RichTextBoxScrollBars.Vertical,
            DetectUrls = false,
            TabStop = false,
            Cursor = Cursors.Default,
        };
        var notesFrame = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 4, 20, 12) };
        var notesInner = new Panel { Dock = DockStyle.Fill, BackColor = notesBox.BackColor, Padding = new Padding(14, 10, 8, 10) };
        notesInner.Controls.Add(notesBox);
        notesFrame.Controls.Add(notesInner);

        // ---- Buttons and footnote ----
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 96, BackColor = Color.FromArgb(240, 240, 240) };
        var install = new Button { Text = L.T("Installa e riavvia"), DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(150, 32) };
        var later = new Button { Text = L.T("Più tardi"), DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(100, 32) };
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Top,
            Height = 50,
            Padding = new Padding(12, 10, 12, 0),
        };
        buttons.Controls.AddRange([later, install]);
        var foot = new Label
        {
            Text = L.T("PadNook si chiude per qualche secondo e si riapre da solo: tasti e dispositivi associati restano."),
            ForeColor = Color.FromArgb(96, 96, 96),
            Dock = DockStyle.Fill,
            Padding = new Padding(20, 0, 20, 0),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        bottom.Controls.Add(foot);
        bottom.Controls.Add(buttons);

        Controls.Add(notesFrame);
        Controls.Add(bottom);
        Controls.Add(header);
        AcceptButton = install;
        CancelButton = later;

        RenderMarkdown(notesBox, info.Notes);
        Shown += (_, _) => install.Focus();
    }

    // ---- Minimal Markdown renderer (what GitHub release notes use) -----------------------------

    private static void RenderMarkdown(RichTextBox box, string markdown)
    {
        var baseFont = new Font("Segoe UI", 10f);
        var text = Color.FromArgb(32, 32, 32);
        box.Font = baseFont;
        var lines = markdown.Replace("\r\n", "\n").Trim().Split('\n');
        var first = true;
        var lastBlank = false;

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.Trim().Length == 0)
            {
                lastBlank = true;
                continue;
            }

            var heading = HeadingRx().Match(line);
            var bullet = BulletRx().Match(line);
            var numbered = NumberedRx().Match(line);

            if (heading.Success)
            {
                var level = heading.Groups[1].Value.Length;
                if (!first) Append(box, "\n", baseFont, text);
                if (!first) Append(box, "\n", new Font("Segoe UI", level <= 2 ? 7f : 4f), text);
                var size = level switch { 1 => 15f, 2 => 13f, _ => 11f };
                var color = level <= 2 ? Color.FromArgb(0, 51, 153) : Color.FromArgb(32, 32, 32);
                Para(box, 0, 0);
                AppendInline(box, heading.Groups[2].Value, new Font("Segoe UI Semibold", size), color);
            }
            else if (bullet.Success || numbered.Success)
            {
                var indent = (bullet.Success ? bullet.Groups[1].Value.Length : numbered.Groups[1].Value.Length) / 2;
                var marker = bullet.Success ? "•" : numbered.Groups[2].Value + ".";
                var body = bullet.Success ? bullet.Groups[2].Value : numbered.Groups[3].Value;
                if (!first) Append(box, "\n", baseFont, text);
                Para(box, 8 + indent * 18, 16);
                Append(box, marker + "\t", baseFont, Color.FromArgb(0, 102, 204));
                AppendInline(box, body, baseFont, text);
            }
            else
            {
                // Plain paragraph; consecutive lines join like on GitHub.
                if (!first) Append(box, lastBlank ? "\n" : " ", baseFont, text);
                if (lastBlank || first) Para(box, 0, 0);
                AppendInline(box, line.Trim(), baseFont, text);
            }
            first = false;
            lastBlank = false;
        }
        box.SelectionStart = 0;
        box.SelectionLength = 0;
    }

    /// <summary>Appends text handling **bold**, *italic*, `code` and [links](url).</summary>
    private static void AppendInline(RichTextBox box, string s, Font font, Color color)
    {
        s = LinkRx().Replace(s, "$1");
        var pos = 0;
        foreach (Match m in InlineRx().Matches(s))
        {
            if (m.Index > pos) Append(box, s[pos..m.Index], font, color);
            if (m.Groups["bold"].Success) Append(box, m.Groups["bold"].Value, new Font(font, FontStyle.Bold), color);
            else if (m.Groups["italic"].Success) Append(box, m.Groups["italic"].Value, new Font(font, FontStyle.Italic), color);
            else Append(box, m.Groups["code"].Value, new Font("Consolas", font.Size - 0.5f), Color.FromArgb(160, 40, 40));
            pos = m.Index + m.Length;
        }
        if (pos < s.Length) Append(box, s[pos..], font, color);
    }

    /// <summary>Paragraph indents for the paragraph being written at the end of the box.</summary>
    private static void Para(RichTextBox box, int indent, int hanging)
    {
        box.SelectionStart = box.TextLength;
        box.SelectionLength = 0;
        box.SelectionBullet = false;
        box.SelectionIndent = indent;
        box.SelectionHangingIndent = hanging;
    }

    private static void Append(RichTextBox box, string s, Font font, Color color)
    {
        box.SelectionStart = box.TextLength;
        box.SelectionLength = 0;
        box.SelectionFont = font;
        box.SelectionColor = color;
        box.AppendText(s);
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")]
    private static partial Regex HeadingRx();

    [GeneratedRegex(@"^(\s*)[-*+]\s+(.*)$")]
    private static partial Regex BulletRx();

    [GeneratedRegex(@"^(\s*)(\d+)[.)]\s+(.*)$")]
    private static partial Regex NumberedRx();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]+\)")]
    private static partial Regex LinkRx();

    [GeneratedRegex(@"\*\*(?<bold>.+?)\*\*|__(?<bold>.+?)__|(?<![\w*])\*(?<italic>[^*\s][^*]*?)\*(?![\w*])|`(?<code>[^`]+)`")]
    private static partial Regex InlineRx();
}
