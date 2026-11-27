using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using PadNook.Core;
using PadNook.Web;
using Microsoft.Win32;

namespace PadNook.Platform;

/// <summary>Windows Firewall inbound rule (created through netsh, elevated once).</summary>
internal static class Firewall
{
    public const string RuleName = "PadNook";

    /// <summary>Runs in the elevated helper process (PadNook.exe --setup-firewall PORT).</summary>
    public static int ApplyRule(int port, string program)
    {
        RunNetsh($"advfirewall firewall delete rule name=\"{RuleName}\"");
        return RunNetsh(
            $"advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow " +
            $"program=\"{program}\" protocol=TCP localport={port} remoteip=localsubnet profile=any enable=yes");
    }

    /// <summary>Elevated helper mode (PadNook.exe --remove-firewall).</summary>
    public static int RemoveRule()
    {
        return RunNetsh($"advfirewall firewall delete rule name=\"{RuleName}\"");
    }

    public static void RequestElevatedRemoval()
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!, "--remove-firewall")
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            using var p = Process.Start(psi);
            p?.WaitForExit(30000);
        }
        catch (Win32Exception) { /* UAC cancelled: the rule stays, harmless */ }
    }

    private static int RunNetsh(string args)
    {
        var psi = new ProcessStartInfo("netsh", args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit(15000);
        return p.HasExited ? p.ExitCode : -1;
    }

    /// <summary>Starts an elevated copy of the exe that creates the rule. Returns false if cancelled or failed.</summary>
    public static bool RequestElevatedSetup(int port)
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!, $"--setup-firewall {port}")
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit(30000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            return false; // UAC prompt cancelled
        }
    }
}

/// <summary>Start with Windows through HKCU\...\Run.</summary>
internal static class AutoStart
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PadNook";

    private static string Command => CommandFor(Environment.ProcessPath!);
    private static string CommandFor(string exe) => $"\"{exe}\" --autostart";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue(ValueName) is string s
                   && s.Contains(Environment.ProcessPath!, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Installer: points autostart to the installed exe (or removes it if it points there).</summary>
    public static void SetEnabledFor(string exe, bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath, true);
            if (enabled) key.SetValue(ValueName, CommandFor(exe));
            else if (key.GetValue(ValueName) is string s && s.Contains(exe, StringComparison.OrdinalIgnoreCase)) key.DeleteValue(ValueName, false);
        }
        catch (Exception ex) { Log.Error("Unable to change autostart", ex); }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath, true);
            if (enabled) key.SetValue(ValueName, Command);
            else key.DeleteValue(ValueName, false);
        }
        catch (Exception ex) { Log.Error("Unable to change autostart", ex); }
    }
}

public sealed record LanAddress(IPAddress Address, string InterfaceName, bool Preferred);

internal static partial class NetInfo
{
    [GeneratedRegex("vEthernet|VirtualBox|VMware|Hyper-V|WSL|Bluetooth|TAP-|Tailscale|ZeroTier|Npcap|Loopback",
        RegexOptions.IgnoreCase)]
    private static partial Regex VirtualAdapter();

    /// <summary>LAN IPv4 addresses of this PC; the most likely Wi-Fi/Ethernet address comes first.</summary>
    public static IReadOnlyList<LanAddress> GetLanAddresses()
    {
        var list = new List<LanAddress>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

            var props = ni.GetIPProperties();
            var hasGateway = props.GatewayAddresses.Any(g =>
                g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
            var isVirtual = VirtualAdapter().IsMatch(ni.Name) || VirtualAdapter().IsMatch(ni.Description);

            foreach (var ua in props.UnicastAddresses)
            {
                var ip = ua.Address;
                if (ip.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ip)) continue;
                if (!LanFilter.IsLocal(ip)) continue;
                var linkLocal = ip.GetAddressBytes()[0] == 169;
                list.Add(new LanAddress(ip, ni.Name, hasGateway && !isVirtual && !linkLocal));
            }
        }
        return list
            .OrderByDescending(a => a.Preferred)
            .ThenBy(a => a.Address.GetAddressBytes()[0] == 169)
            .ToList();
    }
}
