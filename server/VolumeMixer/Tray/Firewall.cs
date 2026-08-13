using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Forms;

namespace VolumeMixer.Tray;

/// <summary>
/// Repairs the state left behind when the user clicked "Cancel" on the Windows Firewall
/// prompt (which silently creates a BLOCK rule for the exe): removes every rule bound to
/// this exe path and adds a clean inbound allow rule. Elevates only for this one action.
/// </summary>
public static class Firewall
{
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
