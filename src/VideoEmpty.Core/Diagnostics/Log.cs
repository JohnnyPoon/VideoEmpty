using System.Globalization;

namespace VideoEmpty.Core.Diagnostics;

public static class Log
{
    private static readonly object _gate = new();
    private static string? _path;
    public static string LogPath => _path ??= ResolvePath();

    public enum Level { Debug, Info, Warn, Error }

    private static string ResolvePath()
    {
        string baseDir;
        if (OperatingSystem.IsWindows())
            baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        else
            baseDir = Environment.GetEnvironmentVariable("XDG_STATE_HOME")
                      ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state");
        var dir = Path.Combine(baseDir, "VideoEmpty", "logs");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "videoempty.log");
    }

    public static void Debug(string source, string message) => Write(Level.Debug, source, message, null);
    public static void Info(string source, string message)  => Write(Level.Info,  source, message, null);
    public static void Warn(string source, string message)  => Write(Level.Warn,  source, message, null);
    public static void Error(string source, string message, Exception? ex = null) => Write(Level.Error, source, message, ex);

    public static void Write(Level level, string source, string message, Exception? ex)
    {
        var ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        var line = $"[{ts}] [{level,-5}] [{source}] {message}";
        if (ex is not null) line += Environment.NewLine + ex;
        try
        {
            lock (_gate)
            {
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
        }
        catch { }
        try { Console.Error.WriteLine(line); } catch { }
    }
}
