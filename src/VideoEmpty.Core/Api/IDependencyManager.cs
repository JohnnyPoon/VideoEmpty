namespace VideoEmpty.Core.Api;

public enum DependencyState { Installed, Missing, Installing, Unknown, FailedToInstall }

public sealed class DependencyStatus
{
    public string Name { get; set; } = "";
    public DependencyState State { get; set; }
    public string? Path { get; set; }
    public string? Version { get; set; }
    public string? Message { get; set; }
}

public sealed class DependencyInstallProgress
{
    public string Name { get; set; } = "";
    public string Stage { get; set; } = "";
    public string? Detail { get; set; }
}

/// <summary>
/// Detects required external tools (ffmpeg/ffprobe) and installs them per platform:
/// Windows -> winget (Gyan.FFmpeg), macOS -> Homebrew (brew install ffmpeg).
/// </summary>
public interface IDependencyManager
{
    /// <summary>Re-check installed tools and update internal state.</summary>
    Task<IReadOnlyList<DependencyStatus>> CheckAsync(CancellationToken ct = default);

    /// <summary>True if any required dependency is missing.</summary>
    bool HasMissing { get; }

    /// <summary>
    /// Install all missing dependencies. Throws <see cref="PlatformNotSupportedException"/>
    /// or <see cref="InvalidOperationException"/> with actionable instructions if the package
    /// manager itself is missing.
    /// </summary>
    Task InstallMissingAsync(IProgress<DependencyInstallProgress>? progress = null, CancellationToken ct = default);
}
