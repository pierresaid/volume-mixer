using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using VolumeMixer.Audio;
using VolumeMixer.Media;

namespace VolumeMixer.Web;

/// <summary>
/// Kestrel + SignalR + the embedded web client, hosted inside the tray process.
/// Plain HTTP on the LAN; the hub is gated by the pairing token from the QR code
/// (loopback connections are trusted: same PC means already in control).
/// </summary>
public sealed class ServerHost
{
    private WebApplication? _app;
    private Settings _settings = null!;

    public int Port { get; private set; }
    public string LanUrl { get; private set; } = "";
    public string LocalUrl => $"http://localhost:{Port}";
    public string PhoneUrl => $"{LanUrl}/#t={_settings.Token}";

    public ConnectionTracker Connections { get; } = new();
    public MediaService Media { get; } = new();
    public UserInput Input { get; } = new();

    public event Action? LanUrlChanged;

    public async Task StartAsync(Settings settings, AudioWorker worker)
    {
        _settings = settings;
        Port = ResolvePort(settings.Port);
        var ip = Net.GetPrimaryLocalAddress()?.ToString() ?? "127.0.0.1";
        LanUrl = $"http://{ip}:{Port}";

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.WebHost.UseUrls($"http://0.0.0.0:{Port}");
        builder.Services.AddSignalR(options =>
        {
            // detect dead peers fast in both directions
            options.KeepAliveInterval = TimeSpan.FromSeconds(5);
            options.ClientTimeoutInterval = TimeSpan.FromSeconds(15);
        });
        builder.Services.AddSingleton(worker);
        builder.Services.AddSingleton(Media);
        builder.Services.AddSingleton(Connections);

        var app = builder.Build();

        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/volumehub") && !IsAuthorized(context))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            await next();
        });

        var webRoot = TryGetEmbeddedWebRoot();
        if (webRoot is not null)
        {
            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = webRoot });
            app.UseStaticFiles(new StaticFileOptions { FileProvider = webRoot });
        }
        else
        {
            app.MapGet("/", () => Results.Text(
                "Volume Mixer server is running, but the web client was not embedded.\n" +
                "Build it first: cd client && npm install && npm run build, then rebuild the server.",
                "text/plain"));
        }

        app.MapGet("/icons/{key}", (string key, HttpContext context, AudioWorker w) =>
        {
            var png = w.Icons.Get(key);
            if (png is null) return Results.NotFound();
            context.Response.Headers.CacheControl = "public, max-age=604800, immutable";
            return Results.Bytes(png, "image/png");
        });

        app.MapGet("/media/art/{key}", (string key, HttpContext context, MediaService m) =>
        {
            var art = m.GetArt(key);
            if (art is null) return Results.NotFound();
            context.Response.Headers.CacheControl = "public, max-age=86400, immutable";
            return Results.Bytes(art, "image/jpeg");
        });

        app.MapHub<ControlHub>("/volumehub");

        await app.StartAsync();
        _app = app;

        // worker + media events -> broadcast to every connected phone
        var hub = app.Services.GetRequiredService<IHubContext<ControlHub>>();
        worker.AppUpserted += a => _ = hub.Clients.All.SendAsync("appUpserted", a);
        worker.AppRemoved += key => _ = hub.Clients.All.SendAsync("appRemoved", key);
        worker.MasterChanged += m => _ = hub.Clients.All.SendAsync("masterChanged", m);
        worker.MicChanged += muted => _ = hub.Clients.All.SendAsync("micChanged", muted);
        worker.MicUsersChanged += users => _ = hub.Clients.All.SendAsync("micUsers", users);
        worker.DevicesChanged += devices => _ = hub.Clients.All.SendAsync("devicesChanged", devices);
        worker.PeaksReady += frame => _ = hub.Clients.All.SendAsync("peaks", frame);
        worker.SnapshotRefreshed += s => _ = hub.Clients.All.SendAsync("snapshot", ControlHub.Payload(s, Media));
        Media.Changed += info => _ = hub.Clients.All.SendAsync("mediaChanged", info);
        Media.SessionsChanged += sessions => _ = hub.Clients.All.SendAsync("mediaSessions", sessions);
        Input.Used += idle => _ = hub.Clients.All.SendAsync("pcInput", idle);
        Connections.CountChanged += count => worker.SetClientCount(count);

        _ = Media.StartAsync();
        _ = Input.RunAsync();
    }

    /// <summary>Re-detects the LAN address after a network change; true if it moved.</summary>
    public bool RefreshLanUrl()
    {
        var ip = Net.GetPrimaryLocalAddress()?.ToString() ?? "127.0.0.1";
        var url = $"http://{ip}:{Port}";
        if (url == LanUrl) return false;
        LanUrl = url;
        LanUrlChanged?.Invoke();
        return true;
    }

    public async Task StopAsync()
    {
        if (_app is null) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await _app.StopAsync(timeout.Token); } catch { }
    }

    private bool IsAuthorized(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        if (remote is not null && IPAddress.IsLoopback(remote)) return true;

        if (context.Request.Query.TryGetValue("access_token", out var query) && TokenEquals(query.ToString()))
            return true;

        var authorization = context.Request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && TokenEquals(authorization[7..]))
            return true;

        return false;
    }

    private bool TokenEquals(string candidate)
    {
        var expected = Encoding.UTF8.GetBytes(_settings.Token);
        var actual = Encoding.UTF8.GetBytes(candidate);
        return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static IFileProvider? TryGetEmbeddedWebRoot()
    {
        try
        {
            var provider = new ManifestEmbeddedFileProvider(typeof(ServerHost).Assembly, "wwwroot");
            return provider.GetFileInfo("index.html").Exists ? provider : null;
        }
        catch
        {
            return null; // no embedded manifest (client was never built)
        }
    }

    private static int ResolvePort(int preferred)
    {
        for (var port = preferred; port < preferred + 20; port++)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Any, port);
                listener.Start();
                listener.Stop();
                return port;
            }
            catch (SocketException) { }
        }
        throw new InvalidOperationException($"No free TCP port found between {preferred} and {preferred + 19}.");
    }
}
