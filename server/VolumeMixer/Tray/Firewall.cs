using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Windows.Forms;

namespace VolumeMixer.Tray;

/// <summary>
/// Repairs the state left behind when the user clicked "Cancel" on the Windows Firewall
/// prompt (which silently creates a BLOCK rule for the exe): removes every rule bound to
/// this exe path and adds a clean inbound allow rule. Elevates only for this one action.
/// </summary>
public static class Firewall
{
    public enum Status { Open, Blocked, Unknown }

    /// <summary>
    /// Whether Windows Firewall lets phones reach this exe on the network the PC is on right now,
    /// read from the firewall policy (no admin needed, ~150 ms: call it off the UI thread).
    /// Blocked means a block rule, "block all incoming", or no allow rule under a default-block
    /// policy (e.g. the first-run prompt was dismissed). Unknown when the policy can't be read.
    /// </summary>
    public static Status Check(int port)
    {
        if (Environment.ProcessPath is not { } exePath) return Status.Unknown;
        try
        {
            var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (type is null) return Status.Unknown;
            var policy = Activator.CreateInstance(type)!;
            object Get(string property, int profile) =>
                type.InvokeMember(property, BindingFlags.GetProperty, null, policy, [profile])!;

            // profiles (domain 1, private 2, public 4) the PC is on where the firewall is on
            var current = (int)type.InvokeMember("CurrentProfileTypes", BindingFlags.GetProperty, null, policy, null)!;
            var enforced = new[] { 1, 2, 4 }.Where(p => (current & p) != 0 && (bool)Get("FirewallEnabled", p)).ToList();
            if (enforced.Count == 0) return Status.Open;

            int allowed = 0, blocked = 0;
            foreach (dynamic rule in (dynamic)type.InvokeMember("Rules", BindingFlags.GetProperty, null, policy, null)!)
            {
                if ((int)rule.Direction != 1 || !(bool)rule.Enabled) continue; // inbound only
                if ((int)rule.Protocol is not (6 or 256)) continue;             // TCP or any
                string? app = rule.ApplicationName;
                if (!string.IsNullOrEmpty(app)
                    && !string.Equals(Environment.ExpandEnvironmentVariables(app), exePath, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!PortMatches((string?)rule.LocalPorts, port)) continue;
                if ((int)rule.Action == 1) allowed |= (int)rule.Profiles;
                else blocked |= (int)rule.Profiles;
            }

            foreach (var profile in enforced)
            {
                if ((bool)Get("BlockAllInboundTraffic", profile) || (blocked & profile) != 0) return Status.Blocked;
                var defaultAllows = (int)Get("DefaultInboundAction", profile) == 1;
                if (!defaultAllows && (allowed & profile) == 0) return Status.Blocked;
            }
            return Status.Open;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Firewall] check failed: {ex.Message}");
            return Status.Unknown;
        }
    }

    /// <summary>"*", "6002", "6000-6010", or a comma-separated mix of those.</summary>
    private static bool PortMatches(string? ports, int port)
    {
        if (string.IsNullOrWhiteSpace(ports) || ports.Trim() == "*") return true;
        foreach (var part in ports.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var dash = part.IndexOf('-');
            if (dash < 0 ? int.TryParse(part, out var single) && single == port
                         : int.TryParse(part[..dash], out var low) && int.TryParse(part[(dash + 1)..], out var high)
                           && port >= low && port <= high)
                return true;
        }
        return false;
    }

    public static void Repair()
    {
        if (Environment.ProcessPath is not { } exePath) return;

        var script =
            $"Get-NetFirewallApplicationFilter -Program '{exePath.Replace("'", "''")}' -ErrorAction SilentlyContinue | " +
            "Get-NetFirewallRule | Remove-NetFirewallRule -ErrorAction SilentlyContinue; " +
            $"New-NetFirewallRule -DisplayName 'Volume Mixer' -Direction Inbound -Action Allow -Program '{exePath.Replace("'", "''")}' -Profile Any | Out-Null";

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"",
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch (Win32Exception)
        {
            // user declined the UAC prompt, nothing to do
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not repair the firewall rule:\n{ex.Message}", "Volume Mixer",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
