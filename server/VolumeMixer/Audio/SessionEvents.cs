using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace VolumeMixer.Audio;

/// <summary>
/// Per-session CoreAudio callback adapter. Callbacks arrive on COM threads and are
/// immediately handed to the worker queue; no state is touched here.
/// </summary>
internal sealed class SessionEvents(AudioWorker worker, string sessionId) : IAudioSessionEventsHandler
{
    public void OnVolumeChanged(float volume, bool isMuted) => worker.PostSessionChanged(sessionId);

    public void OnDisplayNameChanged(string displayName) { }

    public void OnIconPathChanged(string iconPath) { }

    public void OnChannelVolumeChanged(uint channelCount, nint newVolumes, uint channelIndex) { }

    public void OnGroupingParamChanged(ref Guid groupingId) { }

    public void OnStateChanged(AudioSessionState state)
    {
        if (state == AudioSessionState.AudioSessionStateExpired)
            worker.PostSessionExpired(sessionId);
        else
            worker.PostSessionChanged(sessionId);
    }

    public void OnSessionDisconnected(AudioSessionDisconnectReason disconnectReason)
    {
        worker.PostSessionExpired(sessionId);
        // the device went away (Bluetooth asleep or out of range, unplugged, a new format)
        // or the audio service restarted: what comes back is a new device, even under the
        // same ID, and its sessions only show through a fresh binding
        if (disconnectReason is AudioSessionDisconnectReason.DisconnectReasonDeviceRemoval
            or AudioSessionDisconnectReason.DisconnectReasonServerShutdown
            or AudioSessionDisconnectReason.DisconnectReasonFormatChanged)
            worker.ScheduleReinit();
    }
}
