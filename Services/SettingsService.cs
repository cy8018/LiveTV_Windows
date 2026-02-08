using System.IO;
using System.Text.Json;

namespace IPTVPlayer.Services;

/// <summary>
/// Manages application settings persistence
/// </summary>
public class SettingsService
{
    private static readonly string SettingsFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IPTVPlayer");
    
    private static readonly string SettingsFile = Path.Combine(SettingsFolder, "settings.json");

    public AppSettings Settings { get; private set; } = new();

    public SettingsService()
    {
        Load();
    }

    public void Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                var json = File.ReadAllText(SettingsFile);
                Settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch
        {
            Settings = new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            if (!Directory.Exists(SettingsFolder))
            {
                Directory.CreateDirectory(SettingsFolder);
            }

            var json = JsonSerializer.Serialize(Settings, new JsonSerializerOptions 
            { 
                WriteIndented = true 
            });
            File.WriteAllText(SettingsFile, json);
        }
        catch
        {
            // Ignore save errors
        }
    }
}

/// <summary>
/// Application settings
/// </summary>
public class AppSettings
{
    public string? LastPlaylistPath { get; set; }
    public int Volume { get; set; } = 75;
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 720;
    
    /// <summary>
    /// Whether the fullscreen channel list is on the right side (false = left, true = right)
    /// </summary>
    public bool ChannelListOnRight { get; set; } = false;
    
    /// <summary>
    /// Custom channel settings (keyed by channel name + URL hash)
    /// </summary>
    public Dictionary<string, ChannelCustomization> ChannelCustomizations { get; set; } = new();
}

/// <summary>
/// Per-channel customization settings
/// </summary>
public class ChannelCustomization
{
    public bool IsHidden { get; set; }
    public int SortOrder { get; set; }
    public int LastSourceIndex { get; set; }
}
