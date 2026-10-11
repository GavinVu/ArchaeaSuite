using System.Text.Json;

namespace ArchaeaPhoto;

/// <summary>
/// Preferences, stored in %APPDATA%\ArchaeaSuite\ArchaeaPhoto — outside the
/// install folder, so launcher updates and uninstalls never touch them.
/// </summary>
public sealed class AppSettings
{
    public string Theme { get; set; } = "dark";
    public List<string> RecentFiles { get; set; } = new();
    public string? LastFolder { get; set; }
    public double WindowWidth { get; set; } = 1360;
    public double WindowHeight { get; set; } = 860;
    public bool Maximized { get; set; }
    public double RightPanelWidth { get; set; } = 300;

    /// <summary>ARCHAEAPHOTO_DATA_DIR overrides the location (used by the tests).</summary>
    public static string Folder =>
        Environment.GetEnvironmentVariable("ARCHAEAPHOTO_DATA_DIR") is { Length: > 0 } custom ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ArchaeaSuite", "ArchaeaPhoto");

    static string FilePath => Path.Combine(Folder, "settings.json");

    /// <summary>Where new projects are saved by default.</summary>
    public static string ProjectsFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ArchaeaPhoto");

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new();
        }
        catch
        {
            // A damaged settings file falls back to defaults.
        }
        return new AppSettings { Theme = LauncherTheme() ?? "dark" };
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch
        {
            // Preferences are not worth interrupting the user for.
        }
    }

    public void AddRecent(string path)
    {
        RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, path);
        if (RecentFiles.Count > 10) RecentFiles.RemoveRange(10, RecentFiles.Count - 10);
        Save();
    }

    /// <summary>On first run, start in the theme the launcher is set to.</summary>
    static string? LauncherTheme()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "org.archaeasuite.launcher", "settings.json");
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("theme", out var t) && t.GetString() == "light" ? "light" : "dark";
        }
        catch
        {
            return null;
        }
    }
}
