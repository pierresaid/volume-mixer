using System.Security.Cryptography;
using System.Text.Json;

namespace VolumeMixer;

public class Settings
{
    public int Port { get; set; } = 6002;
    public string Token { get; set; } = "";
    public bool FirstRunCompleted { get; set; }

    public static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VolumeMixer");

    private static string FilePath => Path.Combine(Dir, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath));
                if (settings is not null && !string.IsNullOrWhiteSpace(settings.Token))
                    return settings;
            }
        }
        catch
        {
            // corrupted file: fall through and regenerate
        }

        var fresh = new Settings { Token = NewToken() };
        fresh.Save();
        return fresh;
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public void RotateToken()
    {
        Token = NewToken();
        Save();
    }

    private static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');
}
