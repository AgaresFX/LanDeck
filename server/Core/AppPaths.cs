namespace PadNook.Core;

/// <summary>Locations of the persisted data (%APPDATA%\PadNook by default).</summary>
public sealed class AppPaths
{
    public string Root { get; }
    public string ConfigFile => Path.Combine(Root, "config.json");
    public string SecurityFile => Path.Combine(Root, "security.json");
    public string ImagesDir => Path.Combine(Root, "images");
    public string LogsDir => Path.Combine(Root, "logs");

    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PadNook");

    public AppPaths()
    {
        // PADNOOK_DATA allows an isolated data folder (useful for development/testing).
        var overrideDir = Environment.GetEnvironmentVariable("PADNOOK_DATA");
        Root = !string.IsNullOrWhiteSpace(overrideDir) ? overrideDir : DefaultRoot;
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ImagesDir);
        Directory.CreateDirectory(LogsDir);
    }
}
