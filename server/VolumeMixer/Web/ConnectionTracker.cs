namespace VolumeMixer.Web;

/// <summary>Counts connected phones and labels newcomers for the tray balloon.</summary>
public sealed class ConnectionTracker
{
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public event Action<int>? CountChanged;
    public event Action<string, int>? Joined;

    public void OnJoined(string label)
    {
        var count = Interlocked.Increment(ref _count);
        Joined?.Invoke(label, count);
        CountChanged?.Invoke(count);
    }

    public void OnLeft()
    {
        var count = Interlocked.Decrement(ref _count);
        if (count < 0)
        {
            Interlocked.Exchange(ref _count, 0);
            count = 0;
        }
        CountChanged?.Invoke(count);
    }

    public static string LabelFor(string? userAgent, bool isLoopback)
    {
        if (isLoopback) return "This PC";
        var ua = userAgent ?? "";
        if (ua.Contains("iPhone")) return "An iPhone";
        if (ua.Contains("iPad")) return "An iPad";
        if (ua.Contains("Android")) return "An Android phone";
        return "A device";
    }
}
