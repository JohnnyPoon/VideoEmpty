using VideoEmpty.Core.Model;

namespace VideoEmpty.Core.Api;

/// <summary>Renders a single composed template image (RGBA PNG) for previews.</summary>
public interface ITemplateRenderer
{
    byte[] RenderTemplatePng(Template template, IReadOnlyDictionary<string, string>? textValues = null);
}

/// <summary>Probes a video file for resolution, fps, duration.</summary>
public interface IVideoProbe
{
    Task<VideoInfo> ProbeAsync(string path, CancellationToken ct = default);
}

/// <summary>Extracts a single frame at <paramref name="timeMs"/> as PNG bytes.</summary>
public interface IFramePreview
{
    Task<byte[]> ExtractFrameAsync(string videoPath, int timeMs, CancellationToken ct = default);

    /// <summary>
    /// Streams decoded frames (JPEG bytes) from <paramref name="startMs"/> at <paramref name="fps"/>
    /// frames-per-second. Frames are produced as fast as the decoder allows; the consumer is
    /// responsible for pacing playback. Frame timestamps are derived from frame index and fps.
    /// </summary>
    IAsyncEnumerable<FrameStreamItem> StreamFramesAsync(
        string videoPath, int startMs, double fps, int maxWidth, CancellationToken ct = default);
}

/// <summary>A single frame produced by <see cref="IFramePreview.StreamFramesAsync"/>.</summary>
/// <param name="TimeMs">Logical playback time of this frame in milliseconds.</param>
/// <param name="Jpeg">JPEG-encoded frame bytes.</param>
public readonly record struct FrameStreamItem(int TimeMs, byte[] Jpeg);

/// <summary>Composes the project export. Returns when the job is started.</summary>
public interface IVideoExporter
{
    string Start(Project project, ExportOptions options);
    JobStatus GetStatus(string jobId);
    void Cancel(string jobId);
}
