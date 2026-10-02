using Newtonsoft.Json;

namespace MusicLyricsWidget;

public class AppSettings
{
    public string AcrCloudHost { get; set; } = string.Empty;
    public string AcrCloudAccessKey { get; set; } = string.Empty;
    public string AcrCloudAccessSecret { get; set; } = string.Empty;
}

public static class SettingsManager
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MusicLyricsWidget",
        "settings.json");

    public static AppSettings Load()
    {
        if (!File.Exists(SettingsPath))
            return new AppSettings();

        var json = File.ReadAllText(SettingsPath);
        return JsonConvert.DeserializeObject<AppSettings>(json) ?? new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        var dir = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(dir);

        var json = JsonConvert.SerializeObject(settings, Formatting.Indented);
        File.WriteAllText(SettingsPath, json);
    }
}
