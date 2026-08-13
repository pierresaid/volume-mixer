using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace VolumeMixer.Audio;

/// <summary>
/// Extracts one PNG icon per executable and serves it from memory via /icons/{key}.
/// Keys are stable hashes of the exe path, so browsers cache icons across sessions.
/// </summary>
public sealed class IconCache
{
    private readonly ConcurrentDictionary<string, byte[]> _pngByKey = new();
    private readonly ConcurrentDictionary<string, string?> _keyByPath = new(StringComparer.OrdinalIgnoreCase);

    public string? EnsureIcon(string exePath) => _keyByPath.GetOrAdd(exePath, path =>
    {
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon is null) return null;
            using var bitmap = icon.ToBitmap();
            using var stream = new MemoryStream();
            bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);

            var key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant())))[..12].ToLowerInvariant();
            _pngByKey[key] = stream.ToArray();
            return key;
        }
        catch
        {
            // elevated/protected process, missing file, UWP stub: client falls back to a letter avatar
            return null;
        }
    });

    public byte[]? Get(string key) => _pngByKey.TryGetValue(key, out var png) ? png : null;
}
