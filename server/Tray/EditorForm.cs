using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using PadNook.Core;
using PadNook.Web;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace PadNook.Tray;

/// <summary>
/// The editor as a Windows window: the webapp editor hosted in WebView2 (Edge), signed in with a per-process
/// key (no pairing, no PIN) and with PC-only helpers: Windows file/folder dialogs and the paths of dropped files.
/// </summary>
internal sealed class EditorForm : Form
{
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(14, 15, 19) };
    private readonly int _port;
    private readonly string _userDataDir;
    private readonly Action _fallbackToBrowser;

    public EditorForm(int port, string userDataDir, Action fallbackToBrowser)
    {
        _port = port;
        _userDataDir = userDataDir;
        _fallbackToBrowser = fallbackToBrowser;

        Text = "PadNook – Editor";
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { /* default icon */ }
        BackColor = Color.FromArgb(14, 15, 19);
        StartPosition = FormStartPosition.CenterScreen;
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Size = new Size(Math.Min(1480, area.Width * 9 / 10), Math.Min(940, area.Height * 9 / 10));
        MinimumSize = new Size(900, 600);
        Controls.Add(_web);
        Load += async (_, _) => await InitAsync();
    }

    private string Origin => $"http://localhost:{_port}";

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Dark title bar, to match the editor.
        var dark = 1;
        DwmSetWindowAttribute(Handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int));
    }

    private async Task InitAsync()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, _userDataDir);
            await _web.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or COMException or InvalidOperationException)
        {
            Log.Error("WebView2 not available", ex);
            MessageBox.Show(
                L.T("Per la finestra dell'editor serve «Microsoft Edge WebView2 Runtime», che non risulta installato.\n\n") +
                L.T("L'editor verrà aperto nel browser. Puoi installare WebView2 dal sito Microsoft."),
                "PadNook – Editor", MessageBoxButtons.OK, MessageBoxIcon.Information);
            _fallbackToBrowser();
            Close();
            return;
        }

        var core = _web.CoreWebView2;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
#if !DEBUG
        core.Settings.AreDevToolsEnabled = false;
#endif
        // The key only goes to PadNook's own page.
        await core.AddScriptToExecuteOnDocumentCreatedAsync(
            $"if (location.origin === '{Origin}') window.__padnookDesktop = {{ key: '{DesktopEditorAuth.Key}' }};");

        core.WebMessageReceived += OnMessage;
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            OpenExternal(e.Uri);
        };
        core.NavigationStarting += (_, e) =>
        {
            if (e.Uri.StartsWith(Origin + "/", StringComparison.OrdinalIgnoreCase) || e.Uri == Origin) return;
            e.Cancel = true;
            OpenExternal(e.Uri);
        };
        core.DocumentTitleChanged += (_, _) => Text = "PadNook – Editor";
        _web.Source = new Uri(Origin + "/?desktop=1");
    }

    private static void OpenExternal(string uri)
    {
        if (!uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose(); }
        catch { /* no browser */ }
    }

    // ---- Bridge: { id, cmd, ... } -> { id, result } -------------------------------------------

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!e.Source.StartsWith(Origin, StringComparison.OrdinalIgnoreCase)) return;
        JsonObject? msg;
        try { msg = JsonNode.Parse(e.WebMessageAsJson) as JsonObject; }
        catch (JsonException) { return; }
        if (msg is null || J.Int(msg["id"]) is not { } id) return;

        JsonNode? result = null;
        switch (J.Str(msg["cmd"]))
        {
            case "browse":
                result = Browse(J.Str(msg["kind"]) ?? "file", J.Str(msg["current"]) ?? "");
                break;
            case "dropPaths":
                var paths = new JsonArray();
                foreach (var o in e.AdditionalObjects ?? [])
                    if (o is CoreWebView2File f && !string.IsNullOrEmpty(f.Path)) paths.Add(f.Path);
                result = paths;
                break;
        }
        _web.CoreWebView2.PostWebMessageAsJson(new JsonObject { ["id"] = id, ["result"] = result }.ToJsonString());
    }

    private string? Browse(string kind, string current)
    {
        current = Environment.ExpandEnvironmentVariables(current.Trim().Trim('"'));
        if (kind == "folder")
        {
            using var fd = new FolderBrowserDialog
            {
                Description = L.T("Scegli una cartella"),
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true,
            };
            if (Directory.Exists(current)) fd.InitialDirectory = current;
            return fd.ShowDialog(this) == DialogResult.OK ? fd.SelectedPath : null;
        }

        using var od = new OpenFileDialog
        {
            Title = kind switch { "program" => L.T("Scegli un programma o un file"), "sound" => L.T("Scegli un suono"), _ => L.T("Scegli un file") },
            Filter = kind switch
            {
                "program" => L.T("Programmi e collegamenti (*.exe;*.lnk;*.bat;*.cmd;*.ps1;*.url)|*.exe;*.lnk;*.bat;*.cmd;*.ps1;*.url|Tutti i file (*.*)|*.*"),
                "sound" => L.T("Audio (*.wav;*.mp3;*.ogg;*.flac;*.m4a;*.wma)|*.wav;*.mp3;*.ogg;*.flac;*.m4a;*.wma|Tutti i file (*.*)|*.*"),
                _ => L.T("Tutti i file (*.*)|*.*"),
            },
            // A shortcut stays a shortcut (Store apps have no exe to point to).
            DereferenceLinks = kind != "program",
            CheckFileExists = true,
        };
        if (File.Exists(current))
        {
            od.InitialDirectory = Path.GetDirectoryName(current);
            od.FileName = Path.GetFileName(current);
        }
        else if (Directory.Exists(current)) od.InitialDirectory = current;
        else if (kind == "program") od.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return od.ShowDialog(this) == DialogResult.OK ? od.FileName : null;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
