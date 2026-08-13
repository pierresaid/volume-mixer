using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace VolumeMixer.Audio;

/// <summary>
/// Owns every CoreAudio object on a single dedicated MTA thread.
/// Hub commands, CoreAudio callbacks, the reconciliation timer and the peak-meter
/// poll are all funneled through one queue, so mixer state has exactly one writer.
/// </summary>
public sealed class AudioWorker : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private readonly CancellationTokenSource _cts = new();

    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;
    private MMDevice? _mic;
    private DeviceNotificationClient? _deviceNotifications;
    private readonly object _reinitLock = new();
    private bool _reinitScheduled;
    private bool _deviceRefreshScheduled;
    private bool _suppressEvents;
    private volatile int _clientCount;
    private bool _lastFrameQuiet;

    private sealed class SessionEntry
    {
        public required AudioSessionControl Control { get; init; }
        public required SessionEvents Events { get; init; }
        public required string GroupKey { get; init; }
        public required string Name { get; init; }
        public required string? IconKey { get; init; }
        public required bool IsSystem { get; init; }
    }

    private readonly Dictionary<string, SessionEntry> _sessions = new(); // session instance id -> entry
    private readonly Dictionary<string, AppState> _apps = new();         // group key -> last published state
    private MasterState _master = new(0, false);
    private bool? _micMuted;
    private string? _micName;
    private List<DeviceInfo> _devices = [];

    public event Action<AppState>? AppUpserted;
    public event Action<string>? AppRemoved;
    public event Action<MasterState>? MasterChanged;
    public event Action<bool>? MicChanged;
    public event Action<IReadOnlyList<DeviceInfo>>? DevicesChanged;
    public event Action<PeakFrame>? PeaksReady;
    public event Action<MixerSnapshot>? SnapshotRefreshed;

    public IconCache Icons { get; } = new();

    public AudioWorker()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "AudioWorker" };
        _thread.SetApartmentState(ApartmentState.MTA);
    }

    public void Start()
    {
        _thread.Start();
        _ = ReconcileLoopAsync();
        _ = PeakLoopAsync();
    }

    // ---- public API: safe from any thread, work happens on the worker ----

    public void SetClientCount(int count) => _clientCount = count;

    public Task<MixerSnapshot> GetSnapshotAsync()
    {
        var tcs = new TaskCompletionSource<MixerSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() => tcs.TrySetResult(BuildSnapshot()));
        return tcs.Task;
    }

    public void SetAppVolume(string key, int volume) =>
        Post(() => ApplyToGroup(key, v => v.Volume = Math.Clamp(volume, 0, 100) / 100f));

    public void SetAppMuted(string key, bool muted) =>
        Post(() => ApplyToGroup(key, v => v.Mute = muted));

    public void SetMasterVolume(int volume) => Post(() =>
    {
        try
        {
            if (_device is not null)
                _device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(volume, 0, 100) / 100f;
        }
        catch (Exception ex) { OnComFailure(ex); }
    });

    public void SetMasterMuted(bool muted) => Post(() =>
    {
        try
        {
            if (_device is not null)
                _device.AudioEndpointVolume.Mute = muted;
        }
        catch (Exception ex) { OnComFailure(ex); }
    });

    public void SetMicMuted(bool muted) => Post(() =>
    {
        try
        {
            if (_mic is not null)
                _mic.AudioEndpointVolume.Mute = muted;
        }
        catch (Exception ex) { Debug.WriteLine($"[AudioWorker] mic mute failed: {ex.Message}"); }
    });

    public void SetDefaultDevice(string deviceId) => Post(() =>
    {
        try
        {
            PolicyConfig.SetDefaultRenderDevice(deviceId);
            // the OnDefaultDeviceChanged notification triggers the re-bind + snapshot push
        }
        catch (Exception ex) { Debug.WriteLine($"[AudioWorker] device switch failed: {ex.Message}"); }
    });

    // ---- entry points for CoreAudio callback adapters (arbitrary COM threads) ----

    internal void PostSessionChanged(string sessionId) => Post(() =>
    {
        if (_sessions.TryGetValue(sessionId, out var entry))
            PublishGroup(entry.GroupKey);
    });

    internal void PostSessionExpired(string sessionId) => Post(() => RemoveSession(sessionId));

    internal void PostMasterChanged(float volume, bool muted) => Post(() =>
    {
        var master = new MasterState((int)Math.Round(volume * 100), muted);
        if (master != _master)
        {
            _master = master;
            if (!_suppressEvents) MasterChanged?.Invoke(master);
        }
    });

    internal void ScheduleReinit()
    {
        lock (_reinitLock)
        {
            if (_reinitScheduled) return;
            _reinitScheduled = true;
        }
        // default-device switches fire several notifications in a burst, coalesce them
        _ = Task.Delay(500).ContinueWith(_ => Post(() =>
        {
            lock (_reinitLock) { _reinitScheduled = false; }
            InitializeDevice();
        }));
    }

    internal void ScheduleDeviceListRefresh()
    {
        lock (_reinitLock)
        {
            if (_deviceRefreshScheduled) return;
            _deviceRefreshScheduled = true;
        }
        _ = Task.Delay(1000).ContinueWith(_ => Post(() =>
        {
            lock (_reinitLock) { _deviceRefreshScheduled = false; }
            RefreshDeviceList(broadcast: true);
        }));
    }

    // ---- worker internals (only ever run on the worker thread) ----

    private void Post(Action action)
    {
        try { _queue.Add(action); }
        catch (InvalidOperationException) { /* shutting down */ }
    }

    private void Run()
    {
        Post(InitializeDevice);
        try
        {
            foreach (var action in _queue.GetConsumingEnumerable(_cts.Token))
            {
                try { action(); }
                catch (Exception ex) { Debug.WriteLine($"[AudioWorker] action failed: {ex}"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ReconcileLoopAsync()
    {
        // safety net: even if a CoreAudio event is missed, state converges within 10s
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
            while (await timer.WaitForNextTickAsync(_cts.Token))
                Post(Reconcile);
        }
        catch (OperationCanceledException) { }
    }

    private async Task PeakLoopAsync()
    {
        // live meters: 10 Hz, and only while at least one phone is connected
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
            while (await timer.WaitForNextTickAsync(_cts.Token))
            {
                if (_clientCount > 0)
                    Post(CollectPeaks);
            }
        }
        catch (OperationCanceledException) { }
    }

    private void CollectPeaks()
    {
        if (_device is null) return;

        float master = 0;
        float? mic = null;
        var apps = new Dictionary<string, float>();

        try { master = _device.AudioMeterInformation.MasterPeakValue; } catch { }
        if (_mic is not null)
        {
            try { mic = _mic.AudioMeterInformation.MasterPeakValue; } catch { mic = null; }
        }

        foreach (var entry in _sessions.Values)
        {
            float peak = 0;
            try { peak = entry.Control.AudioMeterInformation.MasterPeakValue; } catch { }
            if (apps.TryGetValue(entry.GroupKey, out var existing))
                apps[entry.GroupKey] = Math.Max(existing, peak);
            else
                apps[entry.GroupKey] = peak;
        }

        var quiet = master < 0.002f && (mic ?? 0) < 0.002f && apps.Values.All(p => p < 0.002f);
        if (quiet && _lastFrameQuiet) return; // one zero-frame is enough, then go silent
        _lastFrameQuiet = quiet;

        static float Round(float v) => MathF.Round(v, 3);
        PeaksReady?.Invoke(new PeakFrame(
            Round(master),
            mic is { } m ? Round(m) : null,
            apps.ToDictionary(kv => kv.Key, kv => Round(kv.Value))));
    }

    private void InitializeDevice()
    {
        _suppressEvents = true;
        var ok = false;
        try
        {
            TearDownDevice();
            _enumerator ??= new MMDeviceEnumerator();
            if (_deviceNotifications is null)
            {
                _deviceNotifications = new DeviceNotificationClient(this);
                _enumerator.RegisterEndpointNotificationCallback(_deviceNotifications);
            }
            _device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _device.AudioEndpointVolume.OnVolumeNotification += OnEndpointVolumeNotification;
            _device.AudioSessionManager.OnSessionCreated += OnSessionCreated;
            InitializeMic();
            RefreshDeviceList(broadcast: false);
            RefreshMaster();
            Reconcile();
            ok = true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AudioWorker] device init failed: {ex.Message}");
        }
        finally
        {
            _suppressEvents = false;
        }

        SnapshotRefreshed?.Invoke(BuildSnapshot());
        if (!ok)
        {
            // no render device right now (RDP, headless, driver reset), keep trying quietly
            _ = Task.Delay(5000).ContinueWith(_ => Post(InitializeDevice));
        }
    }

    private void InitializeMic()
    {
        try
        {
            if (_enumerator is null) return;
            try
            {
                _mic = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            }
            catch
            {
                _mic = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
            }
            _mic.AudioEndpointVolume.OnVolumeNotification += OnMicVolumeNotification;
            _micMuted = _mic.AudioEndpointVolume.Mute;
            try { _micName = _mic.FriendlyName; } catch { _micName = null; }
        }
        catch
        {
            _mic = null;
            _micMuted = null;
            _micName = null;
        }
    }

    private void TearDownDevice()
    {
        foreach (var id in _sessions.Keys.ToList())
            RemoveSession(id);
        _sessions.Clear();
        _apps.Clear();

        if (_device is not null)
        {
            try { _device.AudioEndpointVolume.OnVolumeNotification -= OnEndpointVolumeNotification; } catch { }
            try { _device.AudioSessionManager.OnSessionCreated -= OnSessionCreated; } catch { }
            try { _device.Dispose(); } catch { }
            _device = null;
        }

        if (_mic is not null)
        {
            try { _mic.AudioEndpointVolume.OnVolumeNotification -= OnMicVolumeNotification; } catch { }
            try { _mic.Dispose(); } catch { }
            _mic = null;
            _micName = null;
        }
    }

    private void OnEndpointVolumeNotification(AudioVolumeNotificationData data) =>
        PostMasterChanged(data.MasterVolume, data.Muted);

    private void OnMicVolumeNotification(AudioVolumeNotificationData data) => Post(() =>
    {
        if (_micMuted != data.Muted)
        {
            _micMuted = data.Muted;
            if (!_suppressEvents) MicChanged?.Invoke(data.Muted);
        }
    });

    private void OnSessionCreated(object sender, IAudioSessionControl newSession)
    {
        // fires on a COM thread: wrap the raw interface, then hand off to the worker
        AudioSessionControl control;
        try { control = new AudioSessionControl(newSession); }
        catch { return; }
        Post(() => TryAddSession(control));
    }

    private void RefreshDeviceList(bool broadcast)
    {
        if (_enumerator is null) return;
        try
        {
            var defaultId = _device?.ID;
            var devices = new List<DeviceInfo>();
            var collection = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            for (var i = 0; i < collection.Count; i++)
            {
                try
                {
                    using var device = collection[i];
                    devices.Add(new DeviceInfo(device.ID, device.FriendlyName, device.ID == defaultId));
                }
                catch { }
            }

            if (!devices.SequenceEqual(_devices))
            {
                _devices = devices;
                if (broadcast && !_suppressEvents) DevicesChanged?.Invoke(devices);
            }
        }
        catch (Exception ex) { Debug.WriteLine($"[AudioWorker] device list failed: {ex.Message}"); }
    }

    private void Reconcile()
    {
        if (_device is null) return;
        try
        {
            var manager = _device.AudioSessionManager;
            manager.RefreshSessions();
            var sessions = manager.Sessions;
            var seen = new HashSet<string>();

            for (var i = 0; i < sessions.Count; i++)
            {
                AudioSessionControl control;
                string id;
                try
                {
                    control = sessions[i];
                    id = control.GetSessionInstanceIdentifier;
                }
                catch { continue; }

                if (SafeState(control) == AudioSessionState.AudioSessionStateExpired) continue;
                seen.Add(id);
                if (!_sessions.ContainsKey(id))
                    TryAddSession(control);
            }

            foreach (var id in _sessions.Keys.Where(id => !seen.Contains(id)).ToList())
                RemoveSession(id);

            foreach (var group in _sessions.Values.Select(s => s.GroupKey).Distinct().ToList())
                PublishGroup(group);

            RefreshMaster();
        }
        catch (Exception ex) { OnComFailure(ex); }
    }

    private static AudioSessionState SafeState(AudioSessionControl control)
    {
        try { return control.State; }
        catch { return AudioSessionState.AudioSessionStateExpired; }
    }

    private void TryAddSession(AudioSessionControl control)
    {
        string id;
        try { id = control.GetSessionInstanceIdentifier; }
        catch { return; }
        if (string.IsNullOrEmpty(id) || _sessions.ContainsKey(id)) return;
        if (SafeState(control) == AudioSessionState.AudioSessionStateExpired) return;

        string groupKey;
        string name;
        string? iconKey = null;
        var isSystem = false;

        try
        {
            if (control.IsSystemSoundsSession)
            {
                groupKey = "system";
                name = "System sounds";
                isSystem = true;
            }
            else
            {
                var pid = (int)control.GetProcessID;
                if (pid <= 4) return; // idle/system pseudo-processes
                using var process = Process.GetProcessById(pid);
                string? exePath = null;
                try { exePath = process.MainModule?.FileName; } catch { /* elevated/protected */ }
                name = ResolveName(process, exePath);
                iconKey = exePath is not null ? Icons.EnsureIcon(exePath) : null;
                groupKey = process.ProcessName.ToLowerInvariant();
            }
        }
        catch
        {
            return; // process exited between enumeration and inspection
        }

        var events = new SessionEvents(this, id);
        try { control.RegisterEventClient(events); } catch { }

        _sessions[id] = new SessionEntry
        {
            Control = control,
            Events = events,
            GroupKey = groupKey,
            Name = name,
            IconKey = iconKey,
            IsSystem = isSystem,
        };
        PublishGroup(groupKey);
    }

    private static string ResolveName(Process process, string? exePath)
    {
        if (exePath is not null)
        {
            try
            {
                var description = FileVersionInfo.GetVersionInfo(exePath).FileDescription;
                if (!string.IsNullOrWhiteSpace(description)) return description.Trim();
            }
            catch { }
        }
        try
        {
            if (!string.IsNullOrWhiteSpace(process.MainWindowTitle)) return process.MainWindowTitle;
        }
        catch { }
        return process.ProcessName;
    }

    private void RemoveSession(string id)
    {
        if (!_sessions.Remove(id, out var entry)) return;
        try { entry.Control.UnRegisterEventClient(entry.Events); } catch { }
        try { entry.Control.Dispose(); } catch { }
        PublishGroup(entry.GroupKey);
    }

    private void PublishGroup(string groupKey)
    {
        var members = _sessions.Where(kv => kv.Value.GroupKey == groupKey).ToList();
        var dead = new List<string>();
        var volume = 0;
        var allMuted = true;
        SessionEntry? first = null;

        foreach (var (id, entry) in members)
        {
            try
            {
                var sessionVolume = entry.Control.SimpleAudioVolume;
                volume = Math.Max(volume, (int)Math.Round(sessionVolume.Volume * 100));
                allMuted &= sessionVolume.Mute;
                first ??= entry;
            }
            catch
            {
                dead.Add(id);
            }
        }

        foreach (var id in dead)
        {
            if (_sessions.Remove(id, out var entry))
            {
                try { entry.Control.Dispose(); } catch { }
            }
        }

        if (first is null)
        {
            if (_apps.Remove(groupKey) && !_suppressEvents)
                AppRemoved?.Invoke(groupKey);
            return;
        }

        var app = new AppState(groupKey, first.Name, first.IconKey, volume, allMuted, first.IsSystem);
        if (!_apps.TryGetValue(groupKey, out var previous) || previous != app)
        {
            _apps[groupKey] = app;
            if (!_suppressEvents) AppUpserted?.Invoke(app);
        }
    }

    private void ApplyToGroup(string key, Action<SimpleAudioVolume> apply)
    {
        var applied = false;
        foreach (var entry in _sessions.Values.Where(s => s.GroupKey == key).ToList())
        {
            try
            {
                apply(entry.Control.SimpleAudioVolume);
                applied = true;
            }
            catch { }
        }
        if (applied) PublishGroup(key);
    }

    private void RefreshMaster()
    {
        if (_device is null) return;
        try
        {
            var endpoint = _device.AudioEndpointVolume;
            var master = new MasterState((int)Math.Round(endpoint.MasterVolumeLevelScalar * 100), endpoint.Mute);
            if (master != _master)
            {
                _master = master;
                if (!_suppressEvents) MasterChanged?.Invoke(master);
            }
        }
        catch (Exception ex) { OnComFailure(ex); }
    }

    private MixerSnapshot BuildSnapshot() =>
        new(Environment.MachineName, _master, _micMuted, _micName, _devices,
            _apps.Values
                .OrderBy(a => a.IsSystem ? 1 : 0)
                .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .ToList());

    private void OnComFailure(Exception ex)
    {
        Debug.WriteLine($"[AudioWorker] COM failure: {ex.Message}");
        if (ex is COMException or InvalidCastException)
            ScheduleReinit();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _queue.CompleteAdding();
        if (_thread.IsAlive) _thread.Join(2000);
        try
        {
            TearDownDevice();
            if (_enumerator is not null && _deviceNotifications is not null)
                _enumerator.UnregisterEndpointNotificationCallback(_deviceNotifications);
            _enumerator?.Dispose();
        }
        catch { }
    }
}
