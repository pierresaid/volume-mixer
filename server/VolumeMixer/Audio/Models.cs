namespace VolumeMixer.Audio;

public record MasterState(int Volume, bool Muted);

public record AppState(string Key, string Name, string? IconKey, int Volume, bool Muted, bool IsSystem);

public record DeviceInfo(string Id, string Name, bool IsDefault);

public record MixerSnapshot(
    string MachineName,
    MasterState Master,
    bool? MicMuted,
    string? MicName,
    IReadOnlyList<DeviceInfo> Devices,
    IReadOnlyList<AppState> Apps);

public record PeakFrame(float Master, float? Mic, Dictionary<string, float> Apps);
