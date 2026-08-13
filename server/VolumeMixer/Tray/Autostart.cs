using Microsoft.Win32;

namespace VolumeMixer.Tray;

/// <summary>HKCU Run key: per-user autostart, no admin rights involved.</summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "VolumeMixer";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is not null;
            }
            catch { return false; }
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled && Environment.ProcessPath is { } exePath)
                key.SetValue(ValueName, $"\"{exePath}\"");
            else
                key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch { }
    }
}
