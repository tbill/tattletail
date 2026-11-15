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
    public HistoryRange HistoryRange { get; set; } = HistoryRange.TenMinutes;
    public int HistoryRetentionDays { get; set; } = 30;

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
