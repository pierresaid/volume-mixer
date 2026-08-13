using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using Windows.Media.Control;

namespace VolumeMixer.Media;

public record MediaInfo(
    string Title,
    string Artist,
    bool Playing,
    string? ArtKey,
    bool CanSeek,
    long PositionMs,
    long DurationMs,
    double Rate);

/// <summary>
/// Now-playing info and transport controls via the Windows media-session API (SMTC).
/// Positions are sent as ANCHORS (position at send time + playback rate): clients
/// extrapolate locally, so no periodic position traffic is needed. Broadcasts are
/// deduplicated against the extrapolated position, keeping timeline chatter quiet.
/// </summary>
public sealed class MediaService
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private readonly ConcurrentDictionary<string, byte[]> _art = new();
    private readonly SemaphoreSlim _bindGate = new(1, 1);
    private DateTimeOffset _currentAt = DateTimeOffset.UtcNow;

    public MediaInfo? Current { get; private set; }

    /// <summary>Current info with the position advanced to "now", for snapshots.</summary>
    public MediaInfo? CurrentWithFreshPosition
    {
        get
        {
            var current = Current;
            if (current is null || !current.Playing || current.DurationMs <= 0) return current;
            var elapsed = (long)((DateTimeOffset.UtcNow - _currentAt).TotalMilliseconds * current.Rate);
            return current with { PositionMs = Math.Clamp(current.PositionMs + elapsed, 0, current.DurationMs) };
        }
    }

    public event Action<MediaInfo?>? Changed;

    public async Task StartAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += (_, _) => _ = RebindAsync();
            await RebindAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Media] init failed: {ex.Message}");
        }
    }

    private async Task RebindAsync()
    {
        await _bindGate.WaitAsync();
        try
        {
            var session = _manager?.GetCurrentSession();
            if (!ReferenceEquals(session, _session))
            {
                if (_session is not null)
                {
                    _session.MediaPropertiesChanged -= OnPropertiesChanged;
                    _session.PlaybackInfoChanged -= OnPlaybackChanged;
                    _session.TimelinePropertiesChanged -= OnTimelineChanged;
                }
                _session = session;
                if (_session is not null)
                {
                    _session.MediaPropertiesChanged += OnPropertiesChanged;
                    _session.PlaybackInfoChanged += OnPlaybackChanged;
                    _session.TimelinePropertiesChanged += OnTimelineChanged;
                }
            }
        }
        finally
        {
            _bindGate.Release();
        }
        await RefreshAsync();
    }

    private void OnPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) =>
        _ = RefreshAsync();

    private void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) =>
        _ = RefreshAsync();

    private void OnTimelineChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args) =>
        _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        MediaInfo? info = null;
        try
        {
            var session = _session;
            if (session is not null)
            {
                var props = await session.TryGetMediaPropertiesAsync();
                var playback = session.GetPlaybackInfo();

                string? artKey = null;
                if (props.Thumbnail is not null)
                {
                    try
                    {
                        using var reference = await props.Thumbnail.OpenReadAsync();
                        using var stream = reference.AsStreamForRead();
                        using var buffer = new MemoryStream();
                        await stream.CopyToAsync(buffer);
                        var bytes = buffer.ToArray();
                        if (bytes.Length > 0)
                        {
                            artKey = Convert.ToHexString(SHA1.HashData(bytes))[..12].ToLowerInvariant();
                            _art[artKey] = bytes;
                            PruneArt(keep: artKey);
                        }
                    }
                    catch { }
                }

                if (!string.IsNullOrWhiteSpace(props.Title))
                {
                    var playing = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                    var rate = playback.PlaybackRate is > 0 ? playback.PlaybackRate.Value : 1.0;

                    long positionMs = 0, durationMs = 0;
                    var canSeek = false;
                    try
                    {
                        var timeline = session.GetTimelineProperties();
                        durationMs = (long)(timeline.EndTime - timeline.StartTime).TotalMilliseconds;
                        var position = timeline.Position - timeline.StartTime;
                        if (playing)
                            position += (DateTimeOffset.UtcNow - timeline.LastUpdatedTime) * rate; // catch the anchor up to now
                        positionMs = Math.Clamp((long)position.TotalMilliseconds, 0, Math.Max(durationMs, 0));
                        canSeek = playback.Controls.IsPlaybackPositionEnabled && durationMs > 0;
                    }
                    catch { }

                    info = new MediaInfo(props.Title, props.Artist ?? "", playing, artKey, canSeek, positionMs, durationMs, rate);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Media] refresh failed: {ex.Message}");
        }

        var broadcast = ShouldBroadcast(info);
        Current = info;
        _currentAt = DateTimeOffset.UtcNow;
        if (broadcast) Changed?.Invoke(info);
    }

    /// <summary>Skip broadcasts whose only news is a position matching what clients already extrapolate.</summary>
    private bool ShouldBroadcast(MediaInfo? next)
    {
        var previous = Current;
        if (next is null || previous is null) return !Equals(next, previous);
        if (previous.Title != next.Title
            || previous.Artist != next.Artist
            || previous.Playing != next.Playing
            || previous.ArtKey != next.ArtKey
            || previous.CanSeek != next.CanSeek
            || Math.Abs(previous.Rate - next.Rate) > 0.01
            || Math.Abs(previous.DurationMs - next.DurationMs) > 500)
        {
            return true;
        }

        var expected = previous.Playing
            ? previous.PositionMs + (long)((DateTimeOffset.UtcNow - _currentAt).TotalMilliseconds * previous.Rate)
            : previous.PositionMs;
        return Math.Abs(next.PositionMs - expected) > 1500; // a real seek/discontinuity
    }

    private void PruneArt(string keep)
    {
        if (_art.Count <= 24) return;
        foreach (var key in _art.Keys.Where(k => k != keep).Take(_art.Count - 8).ToList())
            _art.TryRemove(key, out _);
    }

    public byte[]? GetArt(string key) => _art.TryGetValue(key, out var bytes) ? bytes : null;

    public async Task PlayPauseAsync()
    {
        try
        {
            var session = _session;
            if (session is null) return;
            if (session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                await session.TryPauseAsync();
            else
                await session.TryPlayAsync();
        }
        catch { }
    }

    public async Task NextAsync()
    {
        try
        {
            if (_session is { } session) await session.TrySkipNextAsync();
        }
        catch { }
    }

    public async Task PreviousAsync()
    {
        try
        {
            if (_session is { } session) await session.TrySkipPreviousAsync();
        }
        catch { }
    }

    public async Task SeekAsync(long positionMs)
    {
        try
        {
            var session = _session;
            if (session is null) return;
            var timeline = session.GetTimelineProperties();
            var target = timeline.StartTime + TimeSpan.FromMilliseconds(positionMs);
            if (timeline.EndTime > timeline.StartTime && target > timeline.EndTime) target = timeline.EndTime;
            if (target < timeline.StartTime) target = timeline.StartTime;
            await session.TryChangePlaybackPositionAsync(target.Ticks);
        }
        catch { }
    }
}
