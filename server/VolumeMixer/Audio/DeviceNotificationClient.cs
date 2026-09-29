using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace VolumeMixer.Audio;

/// <summary>
/// Reacts to endpoint topology changes: a default output/mic change triggers a full
/// re-bind, plug/unplug refreshes the device list pushed to phones.
/// </summary>
internal sealed class DeviceNotificationClient(AudioWorker worker) : IMMNotificationClient
{
    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => worker.ScheduleDeviceListRefresh();

    public void OnDeviceAdded(string pwstrDeviceId) => worker.ScheduleDeviceListRefresh();

    public void OnDeviceRemoved(string deviceId) => worker.ScheduleDeviceListRefresh();

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
