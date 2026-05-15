using System.Diagnostics;
using VideoEmpty.Core.Api;
using VideoEmpty.Core.Diagnostics;

namespace VideoEmpty.Rendering.FFmpeg;

/// <summary>
/// Detects ffmpeg/ffprobe and installs them via the platform package manager
/// (winget on Windows, Homebrew on macOS). Linux is supported as best-effort
/// (apt-get on Debian/Ubuntu, dnf on Fedora) but otherwise reports an actionable error.
/// </summary>
public sealed class FFmpegDependencyManager : IDependencyManager
{
    private readonly Func<FFmpegBinaries> _rediscover;
    private FFmpegBinaries _bin;

    public FFmpegDependencyManager(Func<FFmpegBinaries>? rediscover = null)
    {
        _rediscover = rediscover ?? FFmpegBinaries.Discover;
        _bin = _rediscover();
    }

    /// <summary>Currently-known binary locations (refreshed on Check/Install).</summary>
    public FFmpegBinaries Binaries => _bin;

    public bool HasMissing => !_bin.FFmpegFound || !_bin.FFprobeFound;

    public async Task<IReadOnlyList<DependencyStatus>> CheckAsync(CancellationToken ct = default)
    {
        _bin = _rediscover();
        var list = new List<DependencyStatus>();
        list.Add(await BuildStatusAsync("ffmpeg",  _bin.FFmpegPath,  _bin.FFmpegFound,  ct));
        list.Add(await BuildStatusAsync("ffprobe", _bin.FFprobePath, _bin.FFprobeFound, ct));
        return list;
    }

    private static async Task<DependencyStatus> BuildStatusAsync(string name, string path, bool found, CancellationToken ct)
    {
        var s = new DependencyStatus { Name = name, Path = path, State = found ? DependencyState.Installed : DependencyState.Missing };
        if (found)
        {
            try
            {
                var (stdout, _, code) = await ProcessHelper.RunAsync(path, "-version", ct);
                if (code == 0)
                {
                    var firstLine = stdout.Split('\n').FirstOrDefault()?.Trim();
                    s.Version = firstLine;
                }
            }
            catch (Exception ex)
            {
                s.Message = ex.Message;
            }
        }
        else
        {
            s.Message = $"'{name}' not found on PATH.";
        }
        return s;
    }

    public async Task InstallMissingAsync(IProgress<DependencyInstallProgress>? progress = null, CancellationToken ct = default)
    {
        await CheckAsync(ct);
        if (!HasMissing) return;

        if (OperatingSystem.IsWindows())
            await InstallWindowsAsync(progress, ct);
        else if (OperatingSystem.IsMacOS())
            await InstallMacAsync(progress, ct);
        else if (OperatingSystem.IsLinux())
            await InstallLinuxAsync(progress, ct);
        else
            throw new PlatformNotSupportedException("Automatic install is not supported on this OS.");

        // Re-discover after install so subsequent calls see the new binaries.
        _bin = _rediscover();
        if (HasMissing)
            throw new InvalidOperationException(
                "FFmpeg installation completed but the binaries were still not detected. " +
                "You may need to restart the app so a new PATH is picked up.");
    }

    // -------------------- Windows (winget) --------------------
    private async Task InstallWindowsAsync(IProgress<DependencyInstallProgress>? progress, CancellationToken ct)
    {
        var winget = LocateExecutable("winget");
        if (winget is null)
            throw new InvalidOperationException(
                "winget (Windows Package Manager) was not found. Install 'App Installer' from the Microsoft Store " +
                "(https://aka.ms/getwinget), then retry. As an alternative, install FFmpeg manually from https://www.gyan.dev/ffmpeg/builds/ " +
                "and add the bin folder to PATH, or set VIDEOEMPTY_FFMPEG / VIDEOEMPTY_FFPROBE.");

        progress?.Report(new DependencyInstallProgress { Name = "ffmpeg", Stage = "winget install Gyan.FFmpeg" });
        Log.Info("DepInstall", "Running winget install Gyan.FFmpeg");
        var args = "install --id=Gyan.FFmpeg -e --silent --accept-source-agreements --accept-package-agreements";
        var (stdout, stderr, code) = await ProcessHelper.RunAsync(winget, args, ct,
            line => progress?.Report(new DependencyInstallProgress { Name = "ffmpeg", Stage = "installing", Detail = line }));
        Log.Info("winget", $"exit={code}\nstdout:\n{stdout}\nstderr:\n{stderr}");
        if (code != 0 && IsInstallationInterrupted(stdout, stderr))
            throw new OperationCanceledException("Installation was interrupted. You can retry.");
        if (code != 0 && !IsAlreadyInstalledNoUpgrade(stdout, stderr))
            throw new InvalidOperationException($"winget failed (exit {code}): {(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr)}");
        if (code != 0)
            progress?.Report(new DependencyInstallProgress { Name = "ffmpeg", Stage = "already installed", Detail = "No upgrade available." });

        // winget alters PATH for new processes, but the current process won't see it.
        // Try to locate the freshly installed binaries and update PATH for this process.
        TryRefreshProcessPathFromRegistry();
        AddWindowsFfmpegHintPaths();
        var refreshed = FFmpegBinaries.Discover();
        if (!refreshed.FFmpegFound || !refreshed.FFprobeFound)
        {
            // Search common winget link locations.
            var hints = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps")
            };
            AddPaths(hints);
            AddWindowsFfmpegHintPaths();
        }
    }

    private static bool IsAlreadyInstalledNoUpgrade(string stdout, string stderr)
    {
        var text = (stdout + "\n" + stderr).ToLowerInvariant();
        bool alreadyInstalled = text.Contains("found an existing package already installed");
        bool noUpgrade =
            text.Contains("no available upgrade found") ||
            text.Contains("no newer package versions are available");
        return alreadyInstalled && noUpgrade;
    }

    private static bool IsInstallationInterrupted(string stdout, string stderr)
    {
        var text = (stdout + "\n" + stderr).ToLowerInvariant();
        return text.Contains("cancelled") ||
               text.Contains("canceled") ||
               text.Contains("terminated") ||
               text.Contains("operation canceled") ||
               text.Contains("operation cancelled");
    }

    private static void AddWindowsFfmpegHintPaths()
    {
        if (!OperatingSystem.IsWindows()) return;

        var hints = new List<string>();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var winGetPackages = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(winGetPackages))
        {
            foreach (var packageDir in Directory.EnumerateDirectories(winGetPackages, "Gyan.FFmpeg*"))
            {
                foreach (var ffmpegExe in Directory.EnumerateFiles(packageDir, "ffmpeg.exe", SearchOption.AllDirectories))
                {
                    var binDir = Path.GetDirectoryName(ffmpegExe);
                    if (!string.IsNullOrWhiteSpace(binDir)) hints.Add(binDir);
                }
            }
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var commonBin = Path.Combine(programFiles, "ffmpeg", "bin");
        if (Directory.Exists(commonBin)) hints.Add(commonBin);

        AddPaths(hints);
    }

    private static void TryRefreshProcessPathFromRegistry()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var machine = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? "";
            var user    = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
            var combined = string.Join(Path.PathSeparator, new[] { user, machine, Environment.GetEnvironmentVariable("PATH") ?? "" }
                .Where(s => !string.IsNullOrWhiteSpace(s)));
            Environment.SetEnvironmentVariable("PATH", combined);
            Log.Info("DepInstall", "Refreshed process PATH from registry.");
        }
        catch (Exception ex) { Log.Warn("DepInstall", "Refresh PATH failed: " + ex.Message); }
    }

    private static void AddPaths(IEnumerable<string> dirs)
    {
        var current = Environment.GetEnvironmentVariable("PATH") ?? "";
        var parts = current.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();
        foreach (var d in dirs)
            if (!string.IsNullOrWhiteSpace(d) && !parts.Contains(d, StringComparer.OrdinalIgnoreCase))
                parts.Add(d);
        Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator, parts));
    }

    // -------------------- macOS (Homebrew) --------------------
    private async Task InstallMacAsync(IProgress<DependencyInstallProgress>? progress, CancellationToken ct)
    {
        var brew = LocateExecutable("brew") ?? "/opt/homebrew/bin/brew";
        if (!File.Exists(brew))
            brew = "/usr/local/bin/brew";
        if (!File.Exists(brew))
            throw new InvalidOperationException(
                "Homebrew was not found. Install it from https://brew.sh (one line in Terminal), then retry. " +
                "Alternatively, install FFmpeg manually and ensure 'ffmpeg' / 'ffprobe' are on PATH, or set " +
                "VIDEOEMPTY_FFMPEG / VIDEOEMPTY_FFPROBE.");

        progress?.Report(new DependencyInstallProgress { Name = "ffmpeg", Stage = "brew install ffmpeg" });
        Log.Info("DepInstall", $"Running {brew} install ffmpeg");
        var (stdout, stderr, code) = await ProcessHelper.RunAsync(brew, "install ffmpeg", ct,
            line => progress?.Report(new DependencyInstallProgress { Name = "ffmpeg", Stage = "installing", Detail = line }));
        Log.Info("brew", $"exit={code}\nstdout:\n{stdout}\nstderr:\n{stderr}");
        if (code != 0)
            throw new InvalidOperationException($"brew failed (exit {code}): {(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr)}");

        // Add Homebrew bin dirs to process PATH so this run can locate ffmpeg.
        AddPaths(new[] { "/opt/homebrew/bin", "/usr/local/bin" });
    }

    // -------------------- Linux (best-effort) --------------------
    private async Task InstallLinuxAsync(IProgress<DependencyInstallProgress>? progress, CancellationToken ct)
    {
        // Try apt-get, then dnf, then pacman.
        var attempts = new (string exe, string args)[]
        {
            ("/usr/bin/apt-get", "install -y ffmpeg"),
            ("/usr/bin/dnf",     "install -y ffmpeg"),
            ("/usr/bin/pacman",  "-S --noconfirm ffmpeg")
        };
        foreach (var (exe, args) in attempts)
        {
            if (!File.Exists(exe)) continue;
            progress?.Report(new DependencyInstallProgress { Name = "ffmpeg", Stage = $"{Path.GetFileName(exe)} {args}" });
            var (so, se, code) = await ProcessHelper.RunAsync("sudo", $"{exe} {args}", ct,
                line => progress?.Report(new DependencyInstallProgress { Name = "ffmpeg", Stage = "installing", Detail = line }));
            Log.Info("apt/dnf/pacman", $"exit={code}\nstdout:\n{so}\nstderr:\n{se}");
            if (code == 0) return;
        }
        throw new InvalidOperationException(
            "No supported Linux package manager succeeded. Install FFmpeg manually (sudo apt install ffmpeg / sudo dnf install ffmpeg).");
    }

    private static string? LocateExecutable(string name)
    {
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var candidates = OperatingSystem.IsWindows()
            ? new[] { name + ".exe", name + ".cmd", name }
            : new[] { name };
        foreach (var p in paths)
        foreach (var c in candidates)
        {
            try { var f = Path.Combine(p, c); if (File.Exists(f)) return f; }
            catch { }
        }
        return null;
    }
}

internal static class ProcessHelper
{
    public static async Task<(string stdout, string stderr, int code)> RunAsync(
        string exe, string args, CancellationToken ct, Action<string>? onLine = null)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        Process? p;
        try { p = Process.Start(psi); }
        catch (Exception ex)
        {
            Log.Error("Process", $"Failed to start '{exe} {args}'", ex);
            throw new InvalidOperationException($"Failed to start '{exe}': {ex.Message}", ex);
        }
        if (p is null) throw new InvalidOperationException($"Failed to start '{exe}'.");
        using var _ = p;

        var soBuf = new System.Text.StringBuilder();
        var seBuf = new System.Text.StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is { } l) { soBuf.AppendLine(l); onLine?.Invoke(l); } };
        p.ErrorDataReceived  += (_, e) => { if (e.Data is { } l) { seBuf.AppendLine(l); onLine?.Invoke(l); } };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        return (soBuf.ToString(), seBuf.ToString(), p.ExitCode);
    }
}
