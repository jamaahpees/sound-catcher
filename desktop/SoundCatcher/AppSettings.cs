using System.IO;
using System.Text.Json;
using SoundCatcher.Audio;

namespace SoundCatcher;

public sealed class AppSettings
{
    public string Format { get; set; } = "mp3";
    public int Bitrate { get; set; } = 192;
    public string? DeviceId { get; set; }
    public bool OpenFolderAfterStop { get; set; } = true;
    public bool AutoTrim { get; set; } = true;
    public string OutputFolder { get; set; } = DefaultFolder();

    public static string DefaultFolder()
    {
        var music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        if (string.IsNullOrEmpty(music)) music = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(music, "Sound Catcher");
    }

    private static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SoundCatcher");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, Options);
                if (loaded is not null)
                {
                    if (string.IsNullOrWhiteSpace(loaded.OutputFolder)) loaded.OutputFolder = DefaultFolder();
                    return loaded;
                }
            }
        }
        catch
        {
            // pengaturan rusak, pakai bawaan
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch
        {
            // gagal menyimpan pengaturan bukan alasan untuk berhenti
        }
    }

    public AudioFormat ToFormat() => string.Equals(Format, "wav", StringComparison.OrdinalIgnoreCase)
        ? AudioFormat.Wav
        : AudioFormat.Mp3;
}
