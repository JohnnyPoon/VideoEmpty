using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using VideoEmpty.Core.Api;
using VideoEmpty.Core.Model;
using VideoEmpty.Rendering.FFmpeg;
using VideoEmpty.Rendering.Skia;

namespace VideoEmpty.Rendering.Export;

/// <summary>
/// Exports the project by pre-rendering every animation frame as a full-resolution
/// transparent PNG (position baked in via SkiaSharp), then compositing the sequences
/// onto the source video with FFmpeg. PTS alignment is done via <c>setpts</c> inside
/// the filter_complex rather than <c>-itsoffset</c>, which is unreliable with image2
/// inputs on some FFmpeg builds. <c>-shortest</c> is intentionally omitted so the
/// source video, not an image sequence, determines the output duration.
/// </summary>
public sealed class FFmpegVideoExporter : IVideoExporter
{
    private readonly FFmpegBinaries _bin;
    private readonly SkiaTemplateRenderer _renderer;
    private readonly ConcurrentDictionary<string, JobEntry> _jobs = new();

    public FFmpegVideoExporter(FFmpegBinaries bin, SkiaTemplateRenderer renderer)
    {
        _bin = bin;
        _renderer = renderer;
    }

    private sealed class JobEntry
    {
        public JobStatus Status { get; } = new();
        public CancellationTokenSource Cts { get; } = new();
        public Task? Task { get; set; }
        public string? WorkDir { get; set; }
    }

    public string Start(Project project, ExportOptions options)
    {
        if (string.IsNullOrEmpty(project.VideoPath))
            throw new InvalidOperationException("Project has no video.");

        var job = new JobEntry();
        job.Status.JobId = Guid.NewGuid().ToString("n");
        job.Status.State = JobState.Pending;
        _jobs[job.Status.JobId] = job;
        job.Task = Task.Run(() => RunJob(job, project, options));
        return job.Status.JobId;
    }

    public JobStatus GetStatus(string jobId) =>
        _jobs.TryGetValue(jobId, out var j)
            ? j.Status
            : throw new KeyNotFoundException($"Job '{jobId}' not found.");

    public void Cancel(string jobId)
    {
        if (_jobs.TryGetValue(jobId, out var j))
        {
            j.Cts.Cancel();
            j.Status.State = JobState.Cancelled;
        }
    }

    private async Task RunJob(JobEntry job, Project project, ExportOptions options)
    {
        var work = Path.Combine(Path.GetTempPath(), "videoempty", job.Status.JobId);
        Directory.CreateDirectory(work);
        job.WorkDir = work;
        job.Status.State = JobState.Running;
        try
        {
            _bin.EnsureFFmpeg();

            double fps = project.VideoFps > 0 ? project.VideoFps : 30.0;
            int vw = project.VideoResolution.Width > 0 ? project.VideoResolution.Width : 1920;
            int vh = project.VideoResolution.Height > 0 ? project.VideoResolution.Height : 1080;

            // 1. Pre-render every animation frame for each instance.
            //    Each frame is a full-resolution transparent PNG with the overlay
            //    pixel-positioned via the same math as the live preview compositor.
            //    This produces butter-smooth animation without FFmpeg expression evaluation.
            var seqInfos = new List<(string dir, int frameCount, TemplateInstance inst)>();
            for (int i = 0; i < project.Instances.Count; i++)
            {
                var inst = project.Instances[i];
                job.Status.Message = $"Pre-rendering animation {i + 1}/{project.Instances.Count}";
                var template = project.Templates.FirstOrDefault(t => t.Id == inst.TemplateId)
                    ?? throw new InvalidOperationException($"Template '{inst.TemplateId}' missing.");

                using var overlayBmp = _renderer.RenderBitmap(template, inst.TextValues);
                var seqDir = Path.Combine(work, $"seq_{i}");
                Directory.CreateDirectory(seqDir);

                double frameDurMs = 1000.0 / fps;
                int frameCount = 0;
                for (double tMs = inst.StartMs; tMs <= inst.StartMs + inst.DurationMs + frameDurMs; tMs += frameDurMs)
                {
                    var (x, y) = PreviewCompositor.ComputePosition(template, inst, (int)tMs, vw, vh);

                    // Render a full-resolution transparent canvas with the overlay drawn at
                    // the computed pixel position for this frame's time.
                    using var frame = new SkiaSharp.SKBitmap(vw, vh, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Premul);
                    using (var canvas = new SkiaSharp.SKCanvas(frame))
                    {
                        canvas.Clear(SkiaSharp.SKColors.Transparent);
                        using var paint = new SkiaSharp.SKPaint { IsAntialias = true };
                        canvas.DrawBitmap(overlayBmp, x, y, paint);
                    }
                    using var img = SkiaSharp.SKImage.FromBitmap(frame);
                    using var encoded = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90);
                    var framePath = Path.Combine(seqDir, $"frame_{frameCount:D6}.png");
                    await File.WriteAllBytesAsync(framePath, encoded.ToArray(), job.Cts.Token).ConfigureAwait(false);
                    frameCount++;
                }

                seqInfos.Add((seqDir, frameCount, inst));
            }

            // 2. Build ffmpeg argv using the pre-rendered sequences.
            var args = BuildExportArgsFromSequences(project, options, seqInfos, fps);
            VideoEmpty.Core.Diagnostics.Log.Info("ffmpeg-export", $"{_bin.FFmpegPath} {string.Join(" ", args)}");
            job.Status.Progress = 0.1;
            job.Status.Message = "Encoding video";

            var psi = new ProcessStartInfo(_bin.FFmpegPath)
            {
                RedirectStandardOutput = false,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            Process? p;
            try { p = Process.Start(psi); }
            catch (Exception ex)
            {
                VideoEmpty.Core.Diagnostics.Log.Error("ffmpeg-export", "Failed to start ffmpeg", ex);
                throw new InvalidOperationException($"Failed to start ffmpeg ('{_bin.FFmpegPath}'): {ex.Message}", ex);
            }
            if (p is null) throw new InvalidOperationException("Failed to start ffmpeg.");
            using var _ = p;
            var stderr = new StringBuilder();
            var stderrTask = Task.Run(async () =>
            {
                while (true)
                {
                    var line = await p.StandardError.ReadLineAsync().ConfigureAwait(false);
                    if (line is null) break;
                    stderr.AppendLine(line);
                    UpdateProgress(line, project.VideoDurationMs, job.Status);
                }
            });
            using var reg = job.Cts.Token.Register(() => { try { p.Kill(true); } catch { } });

            await p.WaitForExitAsync(job.Cts.Token).ConfigureAwait(false);
            await stderrTask.ConfigureAwait(false);
            if (p.ExitCode != 0)
            {
                var stderrText = stderr.ToString();
                VideoEmpty.Core.Diagnostics.Log.Error("ffmpeg-export", $"exit={p.ExitCode} stderr={stderrText}");
                job.Status.State = JobState.Failed;
                job.Status.Error = stderrText;
                return;
            }

            job.Status.Progress = 1.0;
            job.Status.OutputPath = options.OutputPath;
            job.Status.State = JobState.Completed;
        }
        catch (OperationCanceledException)
        {
            job.Status.State = JobState.Cancelled;
        }
        catch (Exception ex)
        {
            VideoEmpty.Core.Diagnostics.Log.Error("ffmpeg-export", "Export job failed", ex);
            job.Status.State = JobState.Failed;
            job.Status.Error = ex.Message;
        }
    }

    /// <summary>
    /// Builds FFmpeg args using pre-rendered per-frame PNG sequences.
    /// Position is baked into every PNG (x=0:y=0 in overlay).
    /// PTS is shifted inside the filter_complex via setpts so FFmpeg's image2
    /// demuxer (which always starts at PTS=0) is reliable across all platforms.
    /// NOTE: -shortest is intentionally omitted — the source video determines the
    /// output duration. -itsoffset is avoided because it mis-aligns image2 inputs
    /// in the filter graph on some FFmpeg builds.
    /// </summary>
    internal static List<string> BuildExportArgsFromSequences(
        Project project, ExportOptions options,
        List<(string dir, int frameCount, TemplateInstance inst)> seqInfos,
        double fps)
    {
        var inv = CultureInfo.InvariantCulture;
        var args = new List<string> { "-y", "-i", project.VideoPath! };

        // Add every image-sequence input at natural PTS (0-based).
        // PTS offsetting is handled by setpts inside the filter graph, which is
        // guaranteed to be applied after demuxing and is independent of -itsoffset quirks.
        foreach (var (dir, _, _) in seqInfos)
        {
            args.Add("-f"); args.Add("image2");
            args.Add("-framerate"); args.Add(fps.ToString("0.###", inv));
            args.Add("-i"); args.Add(Path.Combine(dir, "frame_%06d.png"));
        }

        if (seqInfos.Count > 0)
        {
            var sb = new StringBuilder();
            string lastLabel = "0:v";
            for (int i = 0; i < seqInfos.Count; i++)
            {
                var inst = seqInfos[i].inst;
                double startSec = inst.StartMs / 1000.0;
                double endSec   = (inst.StartMs + inst.DurationMs) / 1000.0;
                string startStr = startSec.ToString("0.######", inv);
                string endStr   = endSec.ToString("0.######", inv);

                // setpts=PTS+startSec/TB shifts the image sequence's PTS so frame 0
                // lands at startSec in the timeline.  TB for image2 at fps N is 1/N,
                // so PTS+startSec/TB = PTS + startSec*fps (integer PTS units).
                string ovLabel  = $"ov{i}";
                string outLabel = $"v{i + 1}";

                // Shift the sequence PTS to start at the instance's start time.
                sb.Append('[').Append(i + 1).Append($":v]setpts=PTS+{startStr}/TB[{ovLabel}];");

                // Overlay: position baked into PNG so x=0:y=0.
                // repeatlast=0 — after the last sequence frame stop showing it.
                // enable guards the overlay window so it is hidden outside the instance.
                sb.Append('[').Append(lastLabel).Append("][").Append(ovLabel).Append(']')
                  .Append("overlay=x=0:y=0:format=auto:repeatlast=0")
                  .Append(":enable='between(t,").Append(startStr).Append(',').Append(endStr).Append(")'")
                  .Append('[').Append(outLabel).Append("];");

                lastLabel = outLabel;
            }
            if (sb.Length > 0 && sb[^1] == ';') sb.Length -= 1;

            args.Add("-filter_complex"); args.Add(sb.ToString());
            args.Add("-map"); args.Add($"[{lastLabel}]");
            args.Add("-map"); args.Add("0:a?");
        }

        args.Add("-c:v"); args.Add(options.VideoCodec);
        args.Add("-progress"); args.Add("pipe:2");
        args.Add("-nostats");
        if (options.Crf is { } crf) { args.Add("-crf"); args.Add(crf.ToString(CultureInfo.InvariantCulture)); }
        if (options.VideoBitrateKbps is { } br) { args.Add("-b:v"); args.Add($"{br}k"); }
        args.Add("-c:a"); args.Add(options.AudioCodec);
        args.Add("-pix_fmt"); args.Add("yuv420p");
        // Do NOT add -shortest: it would end the video when the first image sequence ends,
        // cutting the output to only the duration of that sequence.
        args.Add(options.OutputPath);
        return args;
    }

    private static void UpdateProgress(string line, int durationMs, JobStatus status)
    {
        if (durationMs <= 0) return;
        if (line.StartsWith("out_time=", StringComparison.Ordinal))
        {
            var raw = line["out_time=".Length..].Trim();
            if (TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out var ts))
            {
                var p = Math.Clamp(ts.TotalMilliseconds / durationMs, 0.0, 0.99);
                status.Progress = Math.Max(status.Progress, p);
                status.Message = $"Encoding video ({ts:mm\\:ss}/{TimeSpan.FromMilliseconds(durationMs):mm\\:ss})";
            }
            return;
        }
        if (line.StartsWith("progress=", StringComparison.Ordinal) &&
            string.Equals(line["progress=".Length..].Trim(), "end", StringComparison.OrdinalIgnoreCase))
        {
            status.Progress = 1.0;
        }
    }
}
