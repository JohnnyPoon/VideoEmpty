namespace VideoEmpty.Rendering.FFmpeg;

/// <summary>Locates ffmpeg/ffprobe binaries.</summary>
public class FFmpegBinaries
{
    public virtual string FFmpegPath { get; }
    public virtual string FFprobePath { get; }
    public virtual bool FFmpegFound { get; }
    public virtual bool FFprobeFound { get; }

    public FFmpegBinaries(string ffmpegPath, string ffprobePath, bool ffmpegFound, bool ffprobeFound)
    {
        FFmpegPath = ffmpegPath;
        FFprobePath = ffprobePath;
        FFmpegFound = ffmpegFound;
        FFprobeFound = ffprobeFound;
    }

    /// <summary>Discover ffmpeg/ffprobe on PATH or via VIDEOEMPTY_FFMPEG / VIDEOEMPTY_FFPROBE.</summary>
    public static FFmpegBinaries Discover()
    {
        var ffmpegEnv  = Environment.GetEnvironmentVariable("VIDEOEMPTY_FFMPEG");
        var ffprobeEnv = Environment.GetEnvironmentVariable("VIDEOEMPTY_FFPROBE");
        var ffmpegWhich = Which("ffmpeg");
        var ffprobeWhich = Which("ffprobe");
        var ffmpegWinget = OperatingSystem.IsWindows() ? FindWindowsWingetBinary("ffmpeg.exe") : null;
        var ffprobeWinget = OperatingSystem.IsWindows() ? FindWindowsWingetBinary("ffprobe.exe") : null;

        var ffmpegPath  = ffmpegEnv  ?? ffmpegWhich  ?? ffmpegWinget  ?? "ffmpeg";
        var ffprobePath = ffprobeEnv ?? ffprobeWhich ?? ffprobeWinget ?? "ffprobe";
        bool ffmpegFound  = ffmpegEnv  is not null ? File.Exists(ffmpegEnv)  : ffmpegWhich  is not null || ffmpegWinget  is not null;
        bool ffprobeFound = ffprobeEnv is not null ? File.Exists(ffprobeEnv) : ffprobeWhich is not null || ffprobeWinget is not null;
        VideoEmpty.Core.Diagnostics.Log.Info("FFmpegBinaries",
            $"ffmpeg='{ffmpegPath}' (found={ffmpegFound}); ffprobe='{ffprobePath}' (found={ffprobeFound})");
        return new FFmpegBinaries(ffmpegPath, ffprobePath, ffmpegFound, ffprobeFound);
    }

    public void EnsureFFprobe()
    {
        if (!FFprobeFound)
            throw new FileNotFoundException(BuildMissingMessage("ffprobe", FFprobePath));
    }

    public virtual void EnsureFFmpeg()
    {
        if (!FFmpegFound)
            throw new FileNotFoundException(BuildMissingMessage("ffmpeg", FFmpegPath));
    }

    public static string BuildMissingMessage(string tool, string attemptedPath) =>
        $"'{tool}' was not found (tried '{attemptedPath}'). " +
        $"Install FFmpeg and ensure '{tool}' is on PATH, or set the " +
        $"VIDEOEMPTY_{tool.ToUpperInvariant()} environment variable to its full path. " +
        "On Windows: 'winget install Gyan.FFmpeg'. On macOS: 'brew install ffmpeg'. " +
        "VideoEmpty can install this for you from the toolbar.";

    private static string? Which(string exe)
    {
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var candidates = OperatingSystem.IsWindows()
            ? new[] { exe + ".exe", exe + ".cmd", exe }
            : new[] { exe };
        foreach (var p in paths)
        foreach (var c in candidates)
        {
            try
            {
                var full = Path.Combine(p, c);
                if (File.Exists(full)) return full;
            }
            catch { }
        }
        return null;
    }

    private static string? FindWindowsWingetBinary(string exeName)
    {
        if (!OperatingSystem.IsWindows()) return null;

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData)) return null;

        var winGetPackages = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages");
        if (!Directory.Exists(winGetPackages)) return null;

        try
        {
            foreach (var packageDir in Directory.EnumerateDirectories(winGetPackages, "Gyan.FFmpeg*"))
            {
                var found = Directory.EnumerateFiles(packageDir, exeName, SearchOption.AllDirectories).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(found)) return found;
            }
        }
        catch
        {
            // Ignore search failures and continue with standard PATH detection.
        }

        return null;
    }
}
