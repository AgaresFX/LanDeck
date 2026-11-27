using PadNook.Actions;
using PadNook.Config;
using PadNook.Core;
using PadNook.Live;
using PadNook.Platform;
using PadNook.Plugins;
using PadNook.Security;
using PadNook.Tray;
using PadNook.Web;

namespace PadNook;

internal static class Program
{
    public static string Version =>
        typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    [STAThread]
    private static int Main(string[] args)
    {
        // Elevated helper mode: only creates the firewall rule and exits.
        if (args.Length >= 2 && args[0] == "--setup-firewall" && int.TryParse(args[1], out var fwPort))
            return Firewall.ApplyRule(fwPort, Environment.ProcessPath!);

        if (args.Contains("--remove-firewall")) return Firewall.RemoveRule();

        ApplicationConfiguration.Initialize();
        if (args.Contains("--uninstall")) return Installer.Uninstall(quiet: args.Contains("--quiet"));
        if (args.Contains("--install")) return Installer.Install() ? 0 : 1; // silent install (scripts)

        // Development only: shows the update dialog with the release notes of a Markdown file.
        if (Environment.GetEnvironmentVariable("PADNOOK_DEV") == "1" && args.Length >= 2 && args[0] == "--preview-update")
        {
            var info = new ReleaseInfo("9.9.9", File.ReadAllText(args[1]), "", "", 62_000_000, "");
            using var form = new UpdatePromptForm(info);
            return (int)form.ShowDialog();
        }

        var dev = Environment.GetEnvironmentVariable("PADNOOK_DEV") == "1";

        // Started from outside the install folder (e.g. Downloads): offer to install.
        var installerActive = !dev || Installer.TestMode;
        if (installerActive && !Installer.IsRunningInstalled && !args.Contains("--restart") && !args.Contains("--autostart"))
        {
            if (Installer.PromptInstall() != Installer.PromptResult.RunPortable) return 0;
        }

        // Development instances (PADNOOK_DEV=1) do not collide with the installed one.
        var instance = dev ? "PadNook.Dev" : "PadNook";
        using var mutex = new Mutex(true, $@"Local\{instance}.SingleInstance", out var isFirstInstance);
        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{instance}.ShowInfo");
        using var exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{instance}.Exit");
        if (!isFirstInstance && args.Contains("--restart"))
        {
            // Restart (e.g. after a port change): wait for the previous instance to exit.
            try { isFirstInstance = mutex.WaitOne(TimeSpan.FromSeconds(15)); }
            catch (AbandonedMutexException) { isFirstInstance = true; }
        }
        if (!isFirstInstance)
        {
            // Already running: ask the running instance to show its window.
            showEvent.Set();
            return 0;
        }

        var paths = new AppPaths();
        Log.Init(paths.LogsDir);
        Log.Info($"PadNook {Version} starting, data in {paths.Root}");
        Application.ThreadException += (_, e) => Log.Error("UI exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled exception", e.ExceptionObject as Exception);

        using var config = new ConfigStore(paths);
        config.Load();
        L.Init(config.SettingsSnapshot()["language"]?.ToString());
        using var security = new SecurityStore(paths);
        security.Load();
        var pairing = new PairingService();
        WsHub? hubRef = null;
        using var live = new LiveService(paths, config, () => hubRef?.OnlineCount > 0);
        var secrets = new SecretStore(config);
        using var obs = new ObsClient(config, live, secrets);
        var templating = new Templating(live.State, secrets, config);
        var http = new HttpEngine(templating);
        using var twitch = new TwitchService(config, secrets, live);
        using var slobs = new SlobsClient(config, secrets, live);
        using var streamlabs = new StreamlabsSocket(config, secrets, live);
        using var lightroom = new LightroomClient(live);
        using var discord = new DiscordClient(config, secrets, live);
        using var httpPoller = new HttpPoller(config, live, http);
        Platform.SoundBoard.Init(config);
        using var plugins = new PluginHost(paths, live);
        var executor = new ActionExecutor(ActionCatalog.CreateBuiltins(live.State, obs, plugins, http, httpPoller, twitch, templating, slobs, streamlabs, lightroom, discord));
        using var toggles = new ToggleStore(paths);
        var hub = hubRef = new WsHub(config, security, pairing, executor, toggles);
        hub.AttachServices(live, obs, plugins, secrets, httpPoller, twitch, slobs, streamlabs, lightroom, discord);
        var eventRules = new EventRules(executor, hub.FlashAsync);
        twitch.EventSub.Event += ev => eventRules.Handle(ev, twitch.Rules);
        streamlabs.Event += ev => eventRules.Handle(ev, streamlabs.Rules);
        live.Start();
        obs.Start();
        httpPoller.Start();
        twitch.Start();
        slobs.Start();
        streamlabs.Start();
        lightroom.Start();
        discord.Start();
        var port = config.Port;
        if (config.IsFirstRun && !IsPortFree(port))
        {
            // First run only: devices already paired keep using the saved port afterwards.
            var free = Enumerable.Range(port + 1, 50).FirstOrDefault(IsPortFree);
            if (free != 0)
            {
                Log.Info($"Port {port} is busy, using {free}");
                port = free;
                config.UpdateSettings(s => s["port"] = free);
                config.Flush();
            }
        }
        var devMode = Environment.GetEnvironmentVariable("PADNOOK_DEV") == "1";

        if (config.IsFirstRun && !devMode) AutoStart.SetEnabled(true);
        if (!devMode) FirewallSetup.Ensure(config, port);

        var server = new WebServer(hub, security, new ImageStore(paths), port);
        string? startError = null;
        try
        {
            server.StartAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            startError = ex.InnerException?.Message ?? ex.Message;
            Log.Error("Server start failed", ex);
        }

        using var updater = installerActive && Installer.IsRunningInstalled ? new Updater() : null;
        if (Installer.IsRunningInstalled)
        {
            Installer.CleanupAfterUpdate();
            Installer.Register(); // refresh the version shown in Settings → Apps
        }
        var updatedTo = Array.IndexOf(args, "--updated") is var ui and >= 0 && ui + 1 < args.Length ? args[ui + 1] : null;

        var services = new AppServices(paths, config, security, pairing, hub, port, plugins, updater);
        var launchedAtLogin = args.Contains("--autostart");
        using var tray = new TrayContext(services, startError, showInfoOnStart: config.IsFirstRun && !launchedAtLogin, updatedTo);

        live.State.TimerFinished += (name, pc, _phone) =>
        {
            if (!pc) return;
            var title = LiveStateStore.TimerTitle(config.Snapshot().Profiles, name);
            tray.RunOnUi(() => tray.TimerDone(title));
        };

        var waiter = new Thread(() =>
        {
            while (showEvent.WaitOne()) tray.RunOnUi(tray.ShowInfo);
        })
        { IsBackground = true, Name = "ShowInfoListener" };
        waiter.Start();
        var exitWaiter = new Thread(() =>
        {
            // Another copy (installer, uninstaller) asks this one to close.
            if (exitEvent.WaitOne()) tray.RunOnUi(tray.ExitThread);
        })
        { IsBackground = true, Name = "ExitListener" };
        exitWaiter.Start();

        Application.Run(tray);
        Platform.SoundBoard.Shutdown();

        server.StopAsync().GetAwaiter().GetResult();
        Log.Info("PadNook stopped");
        return 0;
    }

    public static bool IsPortFree(int port)
    {
        try
        {
            var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.IPv6Any, port);
            l.Server.DualMode = true;
            l.Start();
            l.Stop();
            return true;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return false;
        }
    }
}
