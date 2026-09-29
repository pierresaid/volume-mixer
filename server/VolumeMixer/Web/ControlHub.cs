using System.Net;
using Microsoft.AspNetCore.SignalR;
using VolumeMixer.Audio;
using VolumeMixer.Media;

namespace VolumeMixer.Web;

public class ControlHub(AudioWorker worker, MediaService media, ConnectionTracker tracker) : Hub
{
    public override async Task OnConnectedAsync()
    {
        var http = Context.GetHttpContext();
        var isLoopback = http?.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip);
        tracker.OnJoined(ConnectionTracker.LabelFor(http?.Request.Headers.UserAgent.ToString(), isLoopback));

        // every (re)connect gets the full state: reconnect IS resync
        await Clients.Caller.SendAsync("snapshot", await ComposeSnapshotAsync());
        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        tracker.OnLeft();
        worker.SetMicMeterWanted(Context.ConnectionId, false);
        return base.OnDisconnectedAsync(exception);
    }

    public Task<object> GetSnapshot() => ComposeSnapshotAsync();

    public void SetAppVolume(string key, int volume) => worker.SetAppVolume(key, volume);

    public void SetAppMuted(string key, bool muted) => worker.SetAppMuted(key, muted);

    public void SetMasterVolume(int volume) => worker.SetMasterVolume(volume);

    public void SetMasterMuted(bool muted) => worker.SetMasterMuted(muted);

    public void SetMicMuted(bool muted) => worker.SetMicMuted(muted);

    public void SetMicMeter(bool wanted) => worker.SetMicMeterWanted(Context.ConnectionId, wanted);

    public void SetDefaultDevice(string deviceId) => worker.SetDefaultDevice(deviceId);

    public Task MediaPlayPause() => media.PlayPauseAsync();

    public Task MediaNext() => media.NextAsync();

    public Task MediaPrevious() => media.PreviousAsync();

    public Task MediaSeek(long positionMs) => media.SeekAsync(positionMs);

    private async Task<object> ComposeSnapshotAsync()
    {
        var s = await worker.GetSnapshotAsync();
        return new { s.MachineName, s.Master, s.MicMuted, s.MicName, s.MicUsers, s.Devices, s.Apps, Media = media.CurrentWithFreshPosition };
    }
}
