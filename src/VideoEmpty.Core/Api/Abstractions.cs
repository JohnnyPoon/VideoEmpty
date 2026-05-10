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
}

/// <summary>Composes the project export. Returns when the job is started.</summary>
public interface IVideoExporter
{
    string Start(Project project, ExportOptions options);
    JobStatus GetStatus(string jobId);
    void Cancel(string jobId);
}
