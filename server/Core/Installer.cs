using System.Diagnostics;
using PadNook.Platform;
using Microsoft.Win32;

namespace PadNook.Core;

/// <summary>
/// Per-user installation (no admin rights): %LOCALAPPDATA%\Programs\PadNook\PadNook.exe,
/// Start menu shortcut and an entry in Settings → Apps. The same exe installs, runs and uninstalls.
/// </summary>
internal static class Installer
{
    /// <summary>PADNOOK_INSTALL_DIR (testing only) installs elsewhere and leaves autostart alone.</summary>
    private static readonly string? TestDir = Environment.GetEnvironmentVariable("PADNOOK_INSTALL_DIR");

    public static bool TestMode => !string.IsNullOrWhiteSpace(TestDir);

    private static string Suffix => TestMode ? ".Test" : "";

    public static string InstallDir => TestMode
        ? TestDir!
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "PadNook");

    public static string InstalledExe => Path.Combine(InstallDir, "PadNook.exe");

    private static string UninstallKey => $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\PadNook{Suffix}";
    private const string SettingsKey = @"Software\PadNook";

    private static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), TestMode ? "PadNook (test).lnk" : "PadNook.lnk");

    public static bool IsRunningInstalled =>
        string.Equals(Path.GetFullPath(Environment.ProcessPath!), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase);

    public static bool IsInstalled => File.Exists(InstalledExe);

    // ---- Install ------------------------------------------------------------------------------

    public enum PromptResult { Installed, RunPortable, Cancel }

    /// <summary>Shown when the exe is started from outside the install folder (e.g. Downloads).</summary>
    public static PromptResult PromptInstall()
    {
        var self = Environment.ProcessPath!;
        if (string.Equals(RegistryValue("SkipInstallPrompt"), self, StringComparison.OrdinalIgnoreCase))
            return PromptResult.RunPortable;

        var update = IsInstalled;
        var install = new TaskDialogButton(update ? L.T("Aggiorna la versione installata") : L.T("Installa PadNook"));
        var portable = new TaskDialogButton(L.T("Avvia senza installare"));
        var cancel = TaskDialogButton.Cancel;
        var page = new TaskDialogPage
        {
            Caption = "PadNook",
            Heading = update ? L.F("PadNook è già installato: vuoi sostituirlo con questa versione ({0})?", Program.Version) : L.T("Installare PadNook su questo PC?"),
            Text = L.F("PadNook verrà copiato in:\n{0}\n\n", InstallDir) +
                   L.T("Verranno creati il collegamento nel menu Start e la voce in Impostazioni → App (per disinstallarlo), ") +
                   L.T("e si avvierà con Windows. Non servono permessi di amministratore.\n\n") +
                   L.T("Gli aggiornamenti verranno cercati automaticamente su GitHub.\n") +
                   L.T("Tasti, impostazioni e dispositivi associati restano quelli che hai già."),
            Icon = TaskDialogIcon.Information,
            Buttons = { install, portable, cancel },
            DefaultButton = install,
            Verification = new TaskDialogVerificationCheckBox(L.T("Non chiedere più per questa copia")),
            AllowCancel = true,
        };
        var result = TaskDialog.ShowDialog(page);
        if (result == install)
        {
            return Install() ? PromptResult.Installed : PromptResult.Cancel;
        }
        if (result == portable)
        {
            if (page.Verification.Checked) SetRegistryValue("SkipInstallPrompt", self);
            return PromptResult.RunPortable;
        }
        return PromptResult.Cancel;
    }

    /// <summary>Copies this exe into the install folder, registers it and starts the installed copy.</summary>
    public static bool Install()
    {
        try
        {
            StopOtherInstances();
            Directory.CreateDirectory(InstallDir);
            CopyWithRetry(Environment.ProcessPath!, InstalledExe);
            Register();
            if (!TestMode)
            {
                AutoStart.SetEnabledFor(InstalledExe, true);
            }
            Log.Info($"Installed to {InstallDir}");
            Process.Start(new ProcessStartInfo(InstalledExe) { UseShellExecute = false, WorkingDirectory = InstallDir })?.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Install failed", ex);
            MessageBox.Show(L.F("Installazione non riuscita:\n{0}", ex.Message), "PadNook", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private static void CopyWithRetry(string from, string to)
    {
        for (var i = 0; ; i++)
        {
            try
            {
                File.Copy(from, to, true);
                return;
            }
            catch (IOException) when (i < 20) { Thread.Sleep(250); } // the old exe may still be closing
        }
    }

    /// <summary>Start menu shortcut + Settings → Apps entry (refreshed at every start of the installed copy).</summary>
    public static void Register()
    {
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey, true))
            {
                key.SetValue("DisplayName", TestMode ? "PadNook (test)" : "PadNook");
                key.SetValue("DisplayVersion", Program.Version);
                key.SetValue("Publisher", "AgaresFX");
                key.SetValue("DisplayIcon", $"{InstalledExe},0");
                key.SetValue("InstallLocation", InstallDir);
                key.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
                key.SetValue("URLInfoAbout", "https://github.com/AgaresFX/PadNook");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                var kb = File.Exists(InstalledExe) ? (int)(new FileInfo(InstalledExe).Length / 1024) : 0;
                key.SetValue("EstimatedSize", kb, RegistryValueKind.DWord);
            }
            CreateShortcut(ShortcutPath, InstalledExe);
        }
        catch (Exception ex) { Log.Error("Unable to register the installation", ex); }
    }

    private static void CreateShortcut(string lnk, string target)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException(L.T("WScript.Shell non disponibile"));
        dynamic shell = Activator.CreateInstance(type)!;
        try
        {
            dynamic sc = shell.CreateShortcut(lnk);
            sc.TargetPath = target;
            sc.WorkingDirectory = Path.GetDirectoryName(target);
            sc.IconLocation = $"{target},0";
            sc.Description = L.T("PadNook – pannello di tasti per il PC");
            sc.Save();
        }
        finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
    }

    // ---- Uninstall ----------------------------------------------------------------------------

    /// <param name="quiet">No questions: keeps the data, skips the firewall (needs admin) and the final message.</param>
    public static int Uninstall(bool quiet = false)
    {
        var page = new TaskDialogPage
        {
            Caption = "PadNook",
            Heading = L.T("Disinstallare PadNook?"),
            Text = L.T("Vengono rimossi il programma, il collegamento nel menu Start, l'avvio automatico e la regola del firewall ") +
                   L.T("(Windows chiederà l'autorizzazione di amministratore)."),
            Icon = TaskDialogIcon.Warning,
            Buttons = { TaskDialogButton.Yes, TaskDialogButton.No },
            DefaultButton = TaskDialogButton.No,
            Verification = new TaskDialogVerificationCheckBox(L.T("Elimina anche tasti, impostazioni, immagini e plugin")),
        };
        if (!quiet && TaskDialog.ShowDialog(page) != TaskDialogButton.Yes) return 1;
        var deleteData = !quiet && page.Verification.Checked;

        StopOtherInstances();
        if (!TestMode) AutoStart.SetEnabledFor(InstalledExe, false);
        try { File.Delete(ShortcutPath); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(SettingsKey + Suffix, false); } catch { }
        if (!TestMode && !quiet) Firewall.RequestElevatedRemoval();

        if (deleteData)
        {
            try { Directory.Delete(AppPaths.DefaultRoot, true); }
            catch (Exception ex) { MessageBox.Show(L.F("Alcuni dati non sono stati eliminati:\n{0}", ex.Message), "PadNook"); }
        }

        // A running exe cannot delete itself: a hidden cmd removes the folder once this process has exited.
        var dir = InstallDir;
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"{dir}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath(),
        })?.Dispose();

        if (!quiet) MessageBox.Show(L.T("PadNook è stato disinstallato."), "PadNook", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return 0;
    }

    // ---- Helpers ------------------------------------------------------------------------------

    /// <summary>Asks every other running PadNook to exit, then waits for them.</summary>
    public static void StopOtherInstances()
    {
        foreach (var name in new[] { "PadNook", "PadNook.Dev" })
        {
            try
            {
                using var ev = EventWaitHandle.OpenExisting($@"Local\{name}.Exit");
                ev.Set();
            }
            catch (WaitHandleCannotBeOpenedException) { }
        }
        var me = Environment.ProcessId;
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (true)
        {
            var others = Process.GetProcessesByName("PadNook")
                .Where(p => p.Id != me).ToList();
            if (others.Count == 0) return;
            if (DateTime.UtcNow > deadline)
            {
                // Versions before 1.2 do not listen for the exit request.
                foreach (var p in others)
                {
                    try { p.Kill(); p.WaitForExit(3000); } catch { }
                    p.Dispose();
                }
                return;
            }
            foreach (var p in others) p.Dispose();
            Thread.Sleep(200);
        }
    }

    /// <summary>Removes PadNook.exe.old left by the previous update.</summary>
    public static void CleanupAfterUpdate()
    {
        var old = Environment.ProcessPath + ".old";
        for (var i = 0; i < 10 && File.Exists(old); i++)
        {
            try { File.Delete(old); }
            catch { Thread.Sleep(500); }
        }
        try { File.Delete(Environment.ProcessPath + ".new"); } catch { }
    }

    private static string? RegistryValue(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(SettingsKey + Suffix);
        return key?.GetValue(name) as string;
    }

    private static void SetRegistryValue(string name, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(SettingsKey + Suffix, true);
        key.SetValue(name, value);
    }
}
