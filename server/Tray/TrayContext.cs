using System.Diagnostics;
using System.Text.Json.Nodes;
using PadNook.Config;
using PadNook.Core;
using PadNook.Platform;
using PadNook.Security;
using PadNook.Web;

namespace PadNook.Tray;

internal static class AppIcon
{
    private static Icon? _icon;

    public static Icon Get()
    {
        if (_icon is not null) return _icon;
        try { _icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }
        return _icon ??= SystemIcons.Application;
    }
}

/// <summary>Shared services used by the tray UI.</summary>
internal sealed record AppServices(
    AppPaths Paths, ConfigStore Config, SecurityStore Security, PairingService Pairing, WsHub Hub, int Port,
    Plugins.PluginHost Plugins, Updater? Updater);

/// <summary>Notification-area icon and its menu. Owns the WinForms message loop.</summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly AppServices _svc;
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly Control _invoker;
    private readonly string? _startError;
    private InfoForm? _info;

    private Action? _balloonClick;

    public TrayContext(AppServices svc, string? startError, bool showInfoOnStart, string? updatedTo = null)
    {
        _svc = svc;
        _startError = startError;

        _invoker = new Control();
        _invoker.CreateControl();
        _ = _invoker.Handle; // force handle creation on the UI thread

        _menu = new ContextMenuStrip();
        _menu.Opening += (_, _) => BuildMenu();
        BuildMenu();

        _icon = new NotifyIcon
        {
            Icon = AppIcon.Get(),
            Text = "PadNook",
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) OpenEditor(); };
        _icon.BalloonTipClicked += (_, _) => { var a = _balloonClick; _balloonClick = null; a?.Invoke(); };
        _icon.BalloonTipClosed += (_, _) => _balloonClick = null;
        if (svc.Updater is not null) svc.Updater.UpdateAvailable += info => RunOnUi(() => NotifyUpdate(info));

        _svc.Hub.ConnectionsChanged += () => RunOnUi(UpdateTooltip);
        UpdateTooltip();

        // Development: open the editor window right away.
        if (Environment.GetEnvironmentVariable("PADNOOK_DEV") == "1" && Environment.GetCommandLineArgs().Contains("--open-editor"))
            _invoker.BeginInvoke(OpenEditor); // once the message loop runs
        if (Environment.GetEnvironmentVariable("PADNOOK_DEV") == "1" && Environment.GetCommandLineArgs().Contains("--open-info"))
            _invoker.BeginInvoke(ShowInfo);

        if (startError is not null)
        {
            _icon.ShowBalloonTip(10000, L.T("PadNook non è attivo"),
                L.F("Impossibile avviare il server sulla porta {0}: {1}", svc.Port, startError), ToolTipIcon.Error);
        }
        else if (showInfoOnStart)
        {
            ShowInfo();
        }
        else if (updatedTo is not null)
        {
            Balloon(L.T("PadNook aggiornato"), L.F("Ora stai usando la versione {0}.", Program.Version), ToolTipIcon.Info);
        }
    }

    public void RunOnUi(Action action)
    {
        if (_invoker.IsDisposed) return;
        if (_invoker.InvokeRequired) _invoker.BeginInvoke(action);
        else action();
    }

    private void UpdateTooltip()
    {
        var n = _svc.Hub.OnlineCount;
        var text = _startError is not null ? L.T("PadNook – server non attivo")
            : n == 0 ? L.T("PadNook – nessun dispositivo connesso")
            : n == 1 ? L.T("PadNook – 1 dispositivo connesso")
            : L.F("PadNook – {0} dispositivi connessi", n);
        _icon.Text = text.Length > 63 ? text[..63] : text;
    }

    private void BuildMenu()
    {
        _menu.Items.Clear();

        var show = new ToolStripMenuItem(L.T("Mostra indirizzo e QR code…"), null, (_, _) => ShowInfo())
        {
            Font = new Font(_menu.Font, FontStyle.Bold),
        };
        _menu.Items.Add(show);
        _menu.Items.Add(new ToolStripMenuItem(L.T("Apri editor…"), null, (_, _) => OpenEditor()));

        var devices = new ToolStripMenuItem(L.T("Dispositivi associati"));
        var list = _svc.Security.Devices();
        if (list.Count == 0)
        {
            devices.DropDownItems.Add(new ToolStripMenuItem(L.T("Nessun dispositivo")) { Enabled = false });
        }
        foreach (var d in list.OrderBy(d => d.Name))
        {
            var label = _svc.Hub.IsOnline(d.Id) ? L.F("{0}  (connesso)", d.Name) : d.Name;
            var item = new ToolStripMenuItem(label);
            item.DropDownItems.Add(new ToolStripMenuItem(
                L.F("Associato il {0:d}, ultimo accesso {1:g}", d.PairedAt.LocalDateTime, d.LastSeen.LocalDateTime))
            { Enabled = false });
            var id = d.Id;
            var name = d.Name;
            item.DropDownItems.Add(new ToolStripMenuItem(L.T("Revoca accesso…"), null, (_, _) => Revoke(id, name)));
            devices.DropDownItems.Add(item);
        }
        _menu.Items.Add(devices);
        _menu.Items.Add(new ToolStripSeparator());

        var autostart = new ToolStripMenuItem(L.T("Avvia con Windows")) { Checked = AutoStart.IsEnabled };
        autostart.Click += (_, _) => AutoStart.SetEnabled(!AutoStart.IsEnabled);
        _menu.Items.Add(autostart);
        _menu.Items.Add(new ToolStripMenuItem(L.T("Configura firewall…"), null, (_, _) => FirewallSetup.Ensure(_svc.Config, _svc.Port, force: true)));
        _menu.Items.Add(new ToolStripMenuItem(L.F("Cambia porta ({0})…", _svc.Port), null, (_, _) => ChangePort()));
        _menu.Items.Add(new ToolStripMenuItem(L.T("Apri cartella dei dati"), null, (_, _) =>
            Process.Start(new ProcessStartInfo(_svc.Paths.Root) { UseShellExecute = true })));
        var plugins = new ToolStripMenuItem("Plugin");
        plugins.DropDownItems.Add(new ToolStripMenuItem(L.T("Apri cartella dei plugin"), null, (_, _) =>
            Process.Start(new ProcessStartInfo(_svc.Plugins.Folder) { UseShellExecute = true })));
        plugins.DropDownItems.Add(new ToolStripMenuItem(L.T("Ricarica plugin"), null, (_, _) =>
        {
            _svc.Plugins.Reload();
            var d = _svc.Plugins.Describe();
            var n = (d["plugins"] as JsonArray)?.Count ?? 0;
            var errors = (d["errors"] as JsonArray)?.Select(e => e?.ToString()).ToList() ?? [];
            _icon.ShowBalloonTip(5000, "PadNook – Plugin",
                errors.Count == 0 ? L.F("Plugin caricati: {0}", n) : L.F("Plugin caricati: {0}\nErrori:\n{1}", n, string.Join("\n", errors)),
                errors.Count == 0 ? ToolTipIcon.Info : ToolTipIcon.Warning);
        }));
        _menu.Items.Add(plugins);
        _menu.Items.Add(new ToolStripSeparator());
        if (_svc.Updater?.Available is { } upd)
        {
            _menu.Items.Add(new ToolStripMenuItem(L.F("Installa aggiornamento {0}…", upd.Version), null, (_, _) => PromptUpdate(upd))
            {
                Font = new Font(_menu.Font, FontStyle.Bold),
            });
        }
        _menu.Items.Add(new ToolStripMenuItem(L.F("Cerca aggiornamenti (versione {0})", Program.Version), null, (_, _) => _ = CheckUpdatesAsync()));
        if (Installer.IsRunningInstalled)
        {
            _menu.Items.Add(new ToolStripMenuItem(L.T("Disinstalla PadNook…"), null, (_, _) =>
                Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--uninstall") { UseShellExecute = false })?.Dispose()));
        }
        else
        {
            _menu.Items.Add(new ToolStripMenuItem(L.T("Installa PadNook su questo PC…"), null, (_, _) =>
            {
                if (Installer.Install()) ExitThread();
            }));
        }
        var language = new ToolStripMenuItem("Lingua · Language");
        foreach (var (code, label) in new[] { ("auto", L.English ? "Automatic (Windows)" : "Automatica (Windows)"), ("it", "Italiano"), ("en", "English") })
        {
            language.DropDownItems.Add(new ToolStripMenuItem(label, null, (_, _) => SetLanguage(code)) { Checked = L.Setting == code });
        }
        _menu.Items.Add(language);
        _menu.Items.Add(new ToolStripMenuItem(L.T("Licenza e crediti…"), null, (_, _) => ShowLegal()));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem(L.T("Esci"), null, (_, _) => ExitThread()));
    }

    /// <summary>PC-side language (tray, windows, messages to the devices). Each device picks its own in its menu.</summary>
    private void SetLanguage(string code)
    {
        _svc.Config.UpdateSettings(s => s["language"] = code);
        L.Init(code);
        UpdateTooltip();
    }

    /// <summary>A countdown ended: Windows notification (with the system notification sound).</summary>
    public void TimerDone(string title) =>
        Balloon(L.T("⏰ Tempo scaduto"), title == "Timer" ? L.T("Il timer è finito.") : L.F("{0}: il timer è finito.", title), ToolTipIcon.Info);

    private void Balloon(string title, string text, ToolTipIcon icon, Action? onClick = null)
    {
        _balloonClick = onClick;
        _icon.ShowBalloonTip(10000, title, text, icon);
    }

    // ---- Updates ------------------------------------------------------------------------------

    private void NotifyUpdate(ReleaseInfo info)
    {
        // Automated tests of a test installation only: install without asking.
        if (Installer.TestMode && Environment.GetEnvironmentVariable("PADNOOK_UPDATE_AUTOTEST") == "1")
        {
            PromptUpdate(info, ask: false);
            return;
        }
        Balloon(L.T("Aggiornamento di PadNook disponibile"),
            L.F("È disponibile la versione {0} (hai la {1}). Clicca qui per installarla.", info.Version, Program.Version),
            ToolTipIcon.Info, () => PromptUpdate(info));
    }

    private async Task CheckUpdatesAsync()
    {
        if (_svc.Updater is null)
        {
            MessageBox.Show(L.T("Gli aggiornamenti automatici funzionano solo con PadNook installato.\n\n") +
                            L.T("Usa «Installa PadNook su questo PC…» nel menu dell'icona."), L.T("PadNook – Aggiornamenti"),
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try
        {
            var info = await _svc.Updater.CheckAsync(manual: true);
            if (info is null)
                MessageBox.Show(L.F("Hai già l'ultima versione ({0}).", Program.Version), L.T("PadNook – Aggiornamenti"),
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            else PromptUpdate(info);
        }
        catch (Exception ex)
        {
            MessageBox.Show(L.F("Impossibile controllare gli aggiornamenti:\n{0}", ex.Message), L.T("PadNook – Aggiornamenti"),
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private bool _updating;

    private void PromptUpdate(ReleaseInfo info, bool ask = true)
    {
        if (_updating) return;
        if (ask)
        {
            using var prompt = new UpdatePromptForm(info);
            if (prompt.ShowDialog() != DialogResult.OK) return;
        }
        _updating = true;
        var form = new UpdateForm(info);
        form.FormClosed += (_, _) =>
        {
            _updating = false;
            if (form.NewExe is null) return;
            try
            {
                Updater.Restart(form.NewExe, info.Version);
                ExitThread();
            }
            catch (Exception ex)
            {
                Log.Error("Unable to start the updated exe", ex);
                Updater.RollBack();
                MessageBox.Show(L.F("Impossibile avviare la nuova versione: {0}", ex.Message), "PadNook", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };
        form.Show();
    }

    public void ShowInfo()
    {
        if (_info is null || _info.IsDisposed)
        {
            _info = new InfoForm(_svc, OpenEditor);
            _info.FormClosed += (_, _) => _info = null;
            _info.Show();
        }
        _info.WindowState = FormWindowState.Normal;
        _info.Activate();
        _info.BringToFront();
    }

    private EditorForm? _editor;

    /// <summary>The editor window (WebView2); falls back to the browser if WebView2 is missing.</summary>
    /// <summary>Writes the license and the third-party notices (embedded) to the data folder and opens them.</summary>
    private void ShowLegal()
    {
        try
        {
            var asm = typeof(TrayContext).Assembly;
            var text = new System.Text.StringBuilder();
            foreach (var name in new[] { "LICENSE.md", "THIRD-PARTY-NOTICES.md" })
            {
                var res = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(".Legal." + name, StringComparison.Ordinal));
                if (res is null) continue;
                using var reader = new StreamReader(asm.GetManifestResourceStream(res)!);
                text.AppendLine(reader.ReadToEnd()).AppendLine().AppendLine(new string('-', 72)).AppendLine();
            }
            var file = Path.Combine(_svc.Paths.Root, L.T("Licenza e crediti.txt"));
            File.WriteAllText(file, text.ToString(), new System.Text.UTF8Encoding(true));
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) { Log.Error("Unable to show the license", ex); }
    }

    private void OpenEditor()
    {
        if (_editor is null || _editor.IsDisposed)
        {
            _editor = new EditorForm(_svc.Port, Path.Combine(_svc.Paths.Root, "editor-webview"), OpenEditorInBrowser);
            _editor.FormClosed += (_, _) => _editor = null;
            _editor.Show();
        }
        if (_editor.WindowState == FormWindowState.Minimized) _editor.WindowState = FormWindowState.Normal;
        _editor.Activate();
        _editor.BringToFront();
    }

    private void OpenEditorInBrowser()
    {
        // A dedicated short-lived code, so the code shown in the QR window stays valid.
        var (code, _) = _svc.Pairing.CreateCode(TimeSpan.FromMinutes(2));
        var url = $"http://localhost:{_svc.Port}/#pair={code}&name=PC&edit=1";
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(L.F("Impossibile aprire il browser: {0}", ex.Message), "PadNook"); }
    }

    private void Revoke(string id, string name)
    {
        var res = MessageBox.Show(
            L.F("Revocare l'accesso a \"{0}\"?\n\nIl dispositivo verrà disconnesso e dovrà essere associato di nuovo.", name),
            L.T("PadNook – Revoca dispositivo"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (res != DialogResult.Yes) return;
        _ = _svc.Hub.KickDeviceAsync(id);
        _svc.Security.Revoke(id);
    }

    private void ChangePort()
    {
        using var form = new PortForm(_svc.Port);
        if (form.ShowDialog() != DialogResult.OK || form.Port == _svc.Port) return;
        if (!Program.IsPortFree(form.Port))
        {
            MessageBox.Show(L.F("La porta {0} è già usata da un altro programma. Scegline un'altra.", form.Port),
                L.T("PadNook – Porta"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var port = form.Port;
        _svc.Config.UpdateSettings(s => s["port"] = port);
        _svc.Config.Flush();
        Log.Info($"Port changed to {port}, restarting");
        // The new instance waits until this one has exited (single-instance mutex).
        Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--restart") { UseShellExecute = false });
        ExitThread();
    }

    protected override void ExitThreadCore()
    {
        _info?.Close();
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _invoker.Dispose();
        base.ExitThreadCore();
    }
}

/// <summary>Creates the firewall rule on first run (or when the port/exe path changes).</summary>
internal static class FirewallSetup
{
    public static bool Ensure(ConfigStore config, int port, bool force = false)
    {
        var program = Environment.ProcessPath!;
        var fw = config.SettingsSnapshot()["firewall"];
        if (!force && J.Int(fw?["port"]) == port
                   && string.Equals(J.Str(fw?["program"]), program, StringComparison.OrdinalIgnoreCase))
            return true;

        var answer = MessageBox.Show(
            L.T("PadNook deve aggiungere una regola a Windows Firewall per accettare le connessioni ") +
            L.F("dai dispositivi della rete locale sulla porta {0}.\n\n", port) +
            L.T("Windows chiederà l'autorizzazione di amministratore."),
            "PadNook – Firewall", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
        if (answer != DialogResult.OK) return false;

        if (Firewall.RequestElevatedSetup(port))
        {
            config.UpdateSettings(s => s["firewall"] = new JsonObject { ["port"] = port, ["program"] = program });
            config.Flush();
            Log.Info($"Firewall rule created for port {port}");
            return true;
        }

        MessageBox.Show(
            L.T("La regola del firewall non è stata creata. iPhone e iPad potrebbero non riuscire a collegarsi.\n\n") +
            L.T("Puoi riprovare dal menu dell'icona: «Configura firewall…»."),
            "PadNook – Firewall", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return false;
    }
}
