using VideoEmpty.Core.Api;
using VideoEmpty.Rendering.Export;
using VideoEmpty.Rendering.Export.CapCut;
using VideoEmpty.Rendering.FFmpeg;
using VideoEmpty.Rendering.Skia;

namespace VideoEmpty.Rendering;

/// <summary>
/// One-stop factory: builds a fully wired <see cref="IVideoEmptyApi"/> using
/// SkiaSharp + FFmpeg. Use this from UI, HTTP, or MCP entry points.
/// </summary>
public static class VideoEmptyServices
{
    public static IVideoEmptyApi CreateApi(FFmpegBinaries? binaries = null)
    {
        var deps = new FFmpegDependencyManager(() => binaries ?? FFmpegBinaries.Discover());
        var renderer = new SkiaTemplateRenderer();
        FFmpegBinaries Bin() => deps.Binaries;
        var probe = new FFprobeVideoProbe(new LazyFFmpegBinaries(Bin));
        var preview = new FFmpegFramePreview(new LazyFFmpegBinaries(Bin));
        var exporter = new FFmpegVideoExporter(new LazyFFmpegBinaries(Bin), renderer);
        var compositor = new PreviewCompositor(renderer);
        var capCut = new CapCutExporter();
        return new VideoEmptyApi(renderer, probe, preview, exporter, deps, compositor.Compose, capCut);
    }
}

/// <summary>
/// Forwarding wrapper so probe/preview/exporter always see the *current* binaries
/// after the user installs FFmpeg from inside the app.
/// </summary>
internal sealed class LazyFFmpegBinaries : FFmpegBinaries
{
    private readonly Func<FFmpegBinaries> _get;
    public LazyFFmpegBinaries(Func<FFmpegBinaries> get) : base("", "", false, false) => _get = get;
    public override string FFmpegPath  => _get().FFmpegPath;
    public override string FFprobePath => _get().FFprobePath;
    public override bool FFmpegFound   => _get().FFmpegFound;
    public override bool FFprobeFound  => _get().FFprobeFound;
}
