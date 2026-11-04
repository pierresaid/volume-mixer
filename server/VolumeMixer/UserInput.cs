using System.Runtime.InteropServices;

namespace VolumeMixer;

/// <summary>
/// When someone last used this PC's keyboard or mouse. Clients use it as presence:
/// the desk monitor keeps its screen on while the PC is in use, even in silence.
/// </summary>
public sealed class UserInput
{
    private static readonly TimeSpan ResendAfter = TimeSpan.FromSeconds(30);

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time; // GetTickCount() of the last input
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    /// <summary>Raised with the seconds since the last input: at once on the first input
    /// after a quiet spell, then at most every 30 s while the PC is in use.</summary>
    public event Action<int>? Used;

    /// <summary>Seconds since the last keyboard or mouse input, or null if Windows can't tell.</summary>
    public static int? IdleSeconds() => LastInput() is { } at ? (int)(unchecked((uint)Environment.TickCount - at) / 1000) : null;

    private static uint? LastInput()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info) ? info.Time : null;
    }

    public async Task RunAsync()
    {
        var seen = LastInput();
        var sentAt = DateTime.MinValue;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync())
        {
            var at = LastInput();
            if (at is null || at == seen) continue;
            seen = at;
            if (DateTime.UtcNow - sentAt < ResendAfter) continue;
            sentAt = DateTime.UtcNow;
            Used?.Invoke(IdleSeconds() ?? 0);
        }
    }
}
