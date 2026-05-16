using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using SkiaSharp;
using VideoEmpty.Core.Api;
using VideoEmpty.Core.Model;
using VideoEmpty.Rendering.FFmpeg;
using VideoEmpty.Rendering.Skia;

namespace VideoEmpty.Rendering.Export;

/// <summary>
/// Streaming exporter:
///   ffmpeg (decode + scale + cfr) → BGRA pipe → Skia compositor → BGRA pipe → ffmpeg (encode + mux audio).
///
/// Replaces the previous per-instance pre-rendered PNG sequence + 80-deep
/// overlay filtergraph approach, which was O(instances × frames) on disk and
/// stacked one full-frame alpha blend per instance per frame.
///
/// Key wins for a 20 min × 80 instance project:
///   • No temp PNG sequences (millions of files removed).
///   • Single composite pass per frame instead of N chained overlays.
///   • Hardware H.264 encoder auto-detected (NVENC / QSV / AMF) with libx264 fallback.
///   • Fast preset and constant frame-rate pipeline keep encoder fed.
/// </summary>
public sealed class FFmpegVideoExporter : IVideoExporter
{
    private readonly FFmpegBinaries _bin;
    private readonly SkiaTemplateRenderer _renderer;
    private readonly ConcurrentDictionary<string, JobEntry> _jobs = new();
    private string? _cachedHardwareEncoder;
    private bool _hardwareEncoderProbed;
    private readonly object _hwLock = new();

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
        job.Status.State = JobState.Running;
        Process? decoder = null;
        Process? encoder = null;
        try
        {
            _bin.EnsureFFmpeg();

            double fps = project.VideoFps > 0 ? project.VideoFps : 30.0;
            int vw = project.VideoResolution.Width > 0 ? project.VideoResolution.Width : 1920;
            int vh = project.VideoResolution.Height > 0 ? project.VideoResolution.Height : 1080;
            // Many encoders require even dimensions for yuv420p.
            vw &= ~1; vh &= ~1;
            int frameBytes = checked(vw * vh * 4);

            // Pre-render each instance's overlay bitmap once (it doesn't change frame-to-frame;
            // only its position does). Disposed after the loop completes.
            var overlays = new List<(TemplateInstance Inst, Template Tpl, SKBitmap Bmp)>(project.Instances.Count);
            foreach (var inst in project.Instances)
            {
                var template = project.Templates.FirstOrDefault(t => t.Id == inst.TemplateId)
                    ?? throw new InvalidOperationException($"Template '{inst.TemplateId}' missing.");
                overlays.Add((inst, template, _renderer.RenderBitmap(template, inst.TextValues)));
            }

            try
            {
                // ---- Decoder: source video → raw BGRA frames at exact target fps/resolution.
                var decArgs = new List<string>
                {
                    "-hide_banner", "-loglevel", "error",
                    "-i", project.VideoPath!,
                    "-vf", $"fps={fps.ToString("0.######", CultureInfo.InvariantCulture)},scale={vw}:{vh}:flags=fast_bilinear,format=bgra",
                    "-f", "rawvideo",
                    "-pix_fmt", "bgra",
                    "-"
                };
                decoder = StartFFmpeg(decArgs, captureStdout: true, captureStderr: true, captureStdin: false);

                // Drain decoder stderr in the background so its pipe doesn't fill.
                _ = Task.Run(async () =>
                {
                    try { await decoder.StandardError.ReadToEndAsync().ConfigureAwait(false); }
                    catch { /* ignore */ }
                });

                // ---- Encoder: raw BGRA from stdin + audio from source → final mp4.
                var (videoCodec, presetArgs) = ResolveVideoCodec(options);
                job.Status.Message = $"Encoding ({videoCodec})";

                var encArgs = new List<string>
                {
                    "-hide_banner", "-loglevel", "error",
                    "-y",
                    // Input 0: raw video from this process via stdin.
                    "-f", "rawvideo",
                    "-pix_fmt", "bgra",
                    "-s", $"{vw}x{vh}",
                    "-r", fps.ToString("0.######", CultureInfo.InvariantCulture),
                    "-i", "-",
                    // Input 1: source video again, used only for audio.
                    "-i", project.VideoPath!,
                    "-map", "0:v:0",
                    "-map", "1:a?",
                    "-c:v", videoCodec,
                };
                encArgs.AddRange(presetArgs);
                if (options.VideoBitrateKbps is { } br) { encArgs.Add("-b:v"); encArgs.Add($"{br}k"); }
                else if (options.Crf is { } crf && IsCrfCodec(videoCodec)) { encArgs.Add("-crf"); encArgs.Add(crf.ToString(CultureInfo.InvariantCulture)); }
                encArgs.Add("-pix_fmt"); encArgs.Add("yuv420p");
                encArgs.Add("-c:a"); encArgs.Add(options.AudioCodec);
                encArgs.Add("-shortest");
                encArgs.Add("-fps_mode"); encArgs.Add("cfr");
                encArgs.Add("-r"); encArgs.Add(fps.ToString("0.######", CultureInfo.InvariantCulture));
                encArgs.Add("-progress"); encArgs.Add("pipe:2");
                encArgs.Add("-nostats");
                encArgs.Add(options.OutputPath);

                VideoEmpty.Core.Diagnostics.Log.Info("ffmpeg-export", $"encode: {_bin.FFmpegPath} {string.Join(" ", encArgs)}");
                encoder = StartFFmpeg(encArgs, captureStdout: false, captureStderr: true, captureStdin: true);

                var encStderr = new StringBuilder();
                var encStderrTask = Task.Run(async () =>
                {
                    while (true)
                    {
                        var line = await encoder.StandardError.ReadLineAsync().ConfigureAwait(false);
                        if (line is null) break;
                        encStderr.AppendLine(line);
                        UpdateProgress(line, project.VideoDurationMs, job.Status);
                    }
                });

                // ---- Composite loop: read N frames from decoder, draw overlays, write to encoder.
                var info = new SKImageInfo(vw, vh, SKColorType.Bgra8888, SKAlphaType.Premul);
                var buf = new byte[frameBytes];
                var handle = GCHandle.Alloc(buf, GCHandleType.Pinned);
                try
                {
                    using var dstBmp = new SKBitmap();
                    dstBmp.InstallPixels(info, handle.AddrOfPinnedObject(), vw * 4);

                    var decStdout = decoder.StandardOutput.BaseStream;
                    var encStdin = encoder.StandardInput.BaseStream;
                    long frameIdx = 0;
                    long totalFrames = Math.Max(1, (long)Math.Round(project.VideoDurationMs / 1000.0 * fps));

                    using var skPaint = new SKPaint { IsAntialias = true };

                    while (!job.Cts.IsCancellationRequested)
                    {
                        int read = 0;
                        while (read < frameBytes)
                        {
                            int n = await decStdout.ReadAsync(buf.AsMemory(read, frameBytes - read), job.Cts.Token).ConfigureAwait(false);
                            if (n == 0) break;
                            read += n;
                        }
                        if (read < frameBytes) break; // end of stream

                        int timeMs = (int)Math.Round(frameIdx * 1000.0 / fps);
                        // dstBmp pixels alias buf -> the current source frame is already there.
                        // Draw all active overlays on top.
                        using (var canvas = new SKCanvas(dstBmp))
                        {
                            double frameDurMs = 1000.0 / fps;
                            foreach (var (inst, tpl, obmp) in overlays)
                            {
                                // Include one extra frame so the final exit-anim frame is rendered.
                                if (timeMs < inst.StartMs) continue;
                                if (timeMs > inst.StartMs + inst.DurationMs + frameDurMs) continue;
                                var (x, y) = PreviewCompositor.ComputePosition(tpl, inst, timeMs, vw, vh);
                                canvas.DrawBitmap(obmp, x, y, skPaint);
                            }
                        }

                        await encStdin.WriteAsync(buf.AsMemory(0, frameBytes), job.Cts.Token).ConfigureAwait(false);

                        frameIdx++;
                        // Coarse progress fallback (encoder stderr also drives it).
                        if ((frameIdx & 0x3F) == 0)
                        {
                            var p = Math.Clamp((double)frameIdx / totalFrames, 0.0, 0.99);
                            if (p > job.Status.Progress) job.Status.Progress = p;
                        }
                    }
                }
                finally
                {
                    handle.Free();
                    try { encoder.StandardInput.Close(); } catch { /* ignore */ }
                }

                using var decReg = job.Cts.Token.Register(() => { try { if (!decoder.HasExited) decoder.Kill(true); } catch { } });
                using var encReg = job.Cts.Token.Register(() => { try { if (!encoder.HasExited) encoder.Kill(true); } catch { } });

                await encoder.WaitForExitAsync(job.Cts.Token).ConfigureAwait(false);
                await encStderrTask.ConfigureAwait(false);
                try { if (!decoder.HasExited) decoder.WaitForExit(2000); } catch { /* ignore */ }

                if (encoder.ExitCode != 0)
                {
                    var msg = encStderr.ToString();
                    VideoEmpty.Core.Diagnostics.Log.Error("ffmpeg-export", $"encoder exit={encoder.ExitCode} stderr={msg}");
                    job.Status.State = JobState.Failed;
                    job.Status.Error = msg;
                    return;
                }

                job.Status.Progress = 1.0;
                job.Status.OutputPath = options.OutputPath;
                job.Status.State = JobState.Completed;
            }
            finally
            {
                foreach (var (_, _, b) in overlays) b.Dispose();
            }
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
        finally
        {
            try { if (decoder is { HasExited: false }) decoder.Kill(true); } catch { }
            try { if (encoder is { HasExited: false }) encoder.Kill(true); } catch { }
        }
    }

    private Process StartFFmpeg(List<string> args, bool captureStdout, bool captureStderr, bool captureStdin)
    {
        var psi = new ProcessStartInfo(_bin.FFmpegPath)
        {
            RedirectStandardOutput = captureStdout,
            RedirectStandardError = captureStderr,
            RedirectStandardInput = captureStdin,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var p = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffmpeg.");
        return p;
    }

    private (string codec, List<string> presetArgs) ResolveVideoCodec(ExportOptions options)
    {
        // If the caller explicitly requested a non-default codec, honour it.
        bool isDefault = string.Equals(options.VideoCodec, "libx264", StringComparison.OrdinalIgnoreCase);
        string codec = options.VideoCodec;
        if (isDefault && options.UseHardwareAcceleration)
        {
            var hw = DetectHardwareEncoder();
            if (hw is not null) codec = hw;
        }
        var presetArgs = BuildPresetArgs(codec, options.Preset);
        return (codec, presetArgs);
    }

    private static List<string> BuildPresetArgs(string codec, string? userPreset)
    {
        var a = new List<string>();
        switch (codec)
        {
            case "h264_nvenc":
            case "hevc_nvenc":
                a.Add("-preset"); a.Add(userPreset ?? "p4");
                a.Add("-tune");   a.Add("hq");
                a.Add("-rc");     a.Add("vbr");
                a.Add("-cq");     a.Add("23");
                break;
            case "h264_qsv":
            case "hevc_qsv":
                a.Add("-preset"); a.Add(userPreset ?? "faster");
                a.Add("-global_quality"); a.Add("23");
                break;
            case "h264_amf":
            case "hevc_amf":
                a.Add("-quality"); a.Add(userPreset ?? "speed");
                a.Add("-rc"); a.Add("cqp");
                a.Add("-qp_i"); a.Add("23");
                a.Add("-qp_p"); a.Add("23");
                break;
            default: // libx264 / libx265 / etc.
                a.Add("-preset"); a.Add(userPreset ?? "veryfast");
                break;
        }
        return a;
    }

    private static bool IsCrfCodec(string codec) =>
        codec.StartsWith("libx", StringComparison.OrdinalIgnoreCase);

    private string? DetectHardwareEncoder()
    {
        lock (_hwLock)
        {
            if (_hardwareEncoderProbed) return _cachedHardwareEncoder;
            _hardwareEncoderProbed = true;
            try
            {
                var psi = new ProcessStartInfo(_bin.FFmpegPath, "-hide_banner -encoders")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p is null) return null;
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);

                // Prefer NVENC > QSV > AMF (rough perf ordering on consumer hardware).
                foreach (var candidate in new[] { "h264_nvenc", "h264_qsv", "h264_amf" })
                {
                    if (output.Contains(candidate, StringComparison.Ordinal))
                    {
                        _cachedHardwareEncoder = candidate;
                        VideoEmpty.Core.Diagnostics.Log.Info("ffmpeg-export", $"Hardware encoder available: {candidate}");
                        return candidate;
                    }
                }
                VideoEmpty.Core.Diagnostics.Log.Info("ffmpeg-export", "No hardware H.264 encoder available; using libx264.");
            }
            catch (Exception ex)
            {
                VideoEmpty.Core.Diagnostics.Log.Error("ffmpeg-export", "Hardware encoder probe failed", ex);
            }
            return _cachedHardwareEncoder;
        }
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
