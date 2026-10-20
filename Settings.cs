using System.Text.Json;

namespace OwkCopy;

public enum ConflictPolicy { IfNewer, Overwrite, Skip, Rename }

public sealed class Settings
{
    public int Threads { get; set; } = 8;
    public ConflictPolicy Conflict { get; set; } = ConflictPolicy.IfNewer;
    public bool SkipLinks { get; set; } = true;
    public bool Verify { get; set; }
    public string Excludes { get; set; } = "";
    public string LastDestination { get; set; } = "";

    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OwkCopy", "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
