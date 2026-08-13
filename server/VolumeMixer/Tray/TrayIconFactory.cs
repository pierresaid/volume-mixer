using System.Drawing;
using System.Drawing.Drawing2D;

namespace VolumeMixer.Tray;

public static class TrayIconFactory
{
    private static readonly Dictionary<int, Icon> Cache = new();

    /// <summary>The app mark from the embedded .ico, at the requested size.</summary>
    public static Icon Create(int size = 32)
    {
        if (Cache.TryGetValue(size, out var cached)) return cached;

        Icon icon;
        try
        {
            using var stream = typeof(TrayIconFactory).Assembly
                .GetManifestResourceStream("VolumeMixer.Resources.app.ico");
            icon = stream is not null ? new Icon(stream, size, size) : DrawFallback();
        }
        catch
        {
            icon = DrawFallback();
        }

        Cache[size] = icon;
        return icon;
    }

    private static Icon DrawFallback()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var body = new SolidBrush(Color.White);
            g.FillRectangle(body, 4, 12, 6, 8);
            g.FillPolygon(body, new[] { new Point(8, 12), new Point(16, 4), new Point(16, 28), new Point(8, 20) });

            using var pen = new Pen(Color.White, 2.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(pen, 14, 8, 10, 16, -55, 110);
            g.DrawArc(pen, 17, 4, 14, 24, -50, 100);
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }
}
