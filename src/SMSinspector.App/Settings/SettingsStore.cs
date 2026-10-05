using System.Text.Json;

namespace SMSinspector.App.Settings;

public sealed record AppSettings
{
    /// <summary>Root of the user's doldecomp/sms clone.</summary>
    public string? DecompPath { get; init; }
}

/// <summary>
/// Local settings, stored in the user data folder (%APPDATA%\SMSinspector on Windows),
/// never next to the program or in the repository.
/// </summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string DataFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SMSinspector");

    private static string FilePath => Path.Combine(DataFolder, "settings.json");

    /// <summary>Loads the settings, or defaults when the file is missing or unreadable.</summary>
    public static AppSettings Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }
    }

    /// <summary>Saves the settings. Returns false when the file could not be written.</summary>
    public static bool Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(DataFolder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
