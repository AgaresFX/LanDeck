namespace PadNook.Core;

/// <summary>Minimal thread-safe file logger.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _file;

    public static void Init(string dir)
    {
        _file = Path.Combine(dir, "padnook.log");
        try
        {
            var fi = new FileInfo(_file);
            if (fi.Exists && fi.Length > 1_000_000) File.Move(_file, _file + ".old", true);
        }
        catch { /* ignore */ }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        if (_file is null) return;
        lock (Gate)
        {
            try
            {
                File.AppendAllText(_file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
            catch { /* logging must never crash the app */ }
        }
    }
}
