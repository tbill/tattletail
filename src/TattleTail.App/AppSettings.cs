using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using TattleTail.Core;

namespace TattleTail.App;

/// <summary>User preferences, saved to %LOCALAPPDATA%\TattleTail\settings.json.</summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public AddressDisplay AddressDisplay { get; set; } = AddressDisplay.Names;
    public bool ReverseDnsLookups { get; set; } = true;
    public bool ShowExitedPrograms { get; set; } = true;
    public bool GroupServersByDomain { get; set; } = true;
    public HistoryRange HistoryRange { get; set; } = HistoryRange.TenMinutes;
    public int HistoryRetentionDays { get; set; } = 30;

    /// <summary>Closing the window hides it in the system tray instead of quitting.</summary>
    public bool KeepRunningInTray { get; set; } = true;

    /// <summary>Pop up a notification when a program uses the network for the first time.</summary>
    public bool AlertOnNewPrograms { get; set; } = true;

    /// <summary>Whether the one-time "TattleTail is still running" hint has been shown.</summary>
    public bool TrayHintShown { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TattleTail", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new AppSettings();
            }
        }
        catch
        {
            // A damaged settings file just means defaults.
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            // Not being able to save preferences shouldn't crash the app.
        }
    }
}
