using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace VolumeMixer.Audio;

/// <summary>
/// Reacts to endpoint topology changes: a default output/mic change, or a bound device
/// unplugged and back (Bluetooth waking up), triggers a full re-bind; other plug/unplug
/// events refresh the device list pushed to phones.
/// </summary>
internal sealed class DeviceNotificationClient(AudioWorker worker) : IMMNotificationClient
{
    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => worker.PostDeviceStateChanged(deviceId);

    public void OnDeviceAdded(string pwstrDeviceId) => worker.PostDeviceStateChanged(pwstrDeviceId);

    public void OnDeviceRemoved(string deviceId) => worker.PostDeviceStateChanged(deviceId);

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if ((flow == DataFlow.Render && role is Role.Multimedia or Role.Communications) ||
            (flow == DataFlow.Capture && role == Role.Communications))
        {
            worker.ScheduleReinit();
        }
    }

    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
    {
    }
}
