using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using VideoEmpty.Core.Api;

namespace VideoEmpty.Rendering.FFmpeg;

public sealed class FFprobeVideoProbe : IVideoProbe
{
    private readonly FFmpegBinaries _bin;
    public FFprobeVideoProbe(FFmpegBinaries bin) => _bin = bin;

    public async Task<VideoInfo> ProbeAsync(string path, CancellationToken ct = default)
    {
        _bin.EnsureFFprobe();
        if (!File.Exists(path))
            throw new FileNotFoundException($"Video file not found: '{path}'.", path);
        var args = $"-v error -print_format json -show_streams -show_format \"{path}\"";
        VideoEmpty.Core.Diagnostics.Log.Info("ffprobe", $"{_bin.FFprobePath} {args}");
        var (stdout, stderr, code) = await RunAsync(_bin.FFprobePath, args, ct).ConfigureAwait(false);
        if (code != 0)
        {
            VideoEmpty.Core.Diagnostics.Log.Error("ffprobe", $"exit={code} stderr={stderr}");
            throw new InvalidOperationException($"ffprobe failed (exit {code}): {stderr.Trim()}");
        }

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        var streams = root.GetProperty("streams");
        var v = default(JsonElement);
        bool found = false;
        foreach (var s in streams.EnumerateArray())
        {
            if (s.TryGetProperty("codec_type", out var ct1) && ct1.GetString() == "video")
            { v = s; found = true; break; }
        }
        if (!found) throw new InvalidOperationException("No video stream found.");

        int width  = v.GetProperty("width").GetInt32();
        int height = v.GetProperty("height").GetInt32();
        double fps = ParseFps(v.GetProperty("avg_frame_rate").GetString() ?? "30/1");
        double durSec = 0;
        if (root.TryGetProperty("format", out var f) &&
            f.TryGetProperty("duration", out var d) &&
            double.TryParse(d.GetString(), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var ds))
            durSec = ds;
        return new VideoInfo(width, height, fps, (int)Math.Round(durSec * 1000));
    }

    private static double ParseFps(string s)
    {
        var parts = s.Split('/');
        if (parts.Length == 2 &&
            double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) &&
            double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) &&
            d != 0)
            return n / d;
        return 30.0;
    }

    internal static async Task<(string stdout, string stderr, int code)> RunAsync(
        string exe, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        Process? p;
        try
        {
            p = Process.Start(psi);
        }
        catch (Exception ex)
        {
            VideoEmpty.Core.Diagnostics.Log.Error("Process", $"Failed to start '{exe}' {args}", ex);
            throw new InvalidOperationException(
                $"Failed to start '{exe}'. {ex.Message}", ex);
        }
        if (p is null)
            throw new InvalidOperationException($"Failed to start '{exe}'.");
        using var _ = p;
        var so = p.StandardOutput.ReadToEndAsync();
        var se = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        return (await so, await se, p.ExitCode);
    }
}

public sealed class FFmpegFramePreview : IFramePreview
{
    private readonly FFmpegBinaries _bin;
    public FFmpegFramePreview(FFmpegBinaries bin) => _bin = bin;

    public async Task<byte[]> ExtractFrameAsync(string videoPath, int timeMs, CancellationToken ct = default)
    {
        _bin.EnsureFFmpeg();
        if (!File.Exists(videoPath))
            throw new FileNotFoundException($"Video file not found: '{videoPath}'.", videoPath);
        var ts = TimeSpan.FromMilliseconds(timeMs).ToString(@"hh\:mm\:ss\.fff");
        var args = $"-ss {ts} -i \"{videoPath}\" -frames:v 1 -f image2pipe -vcodec png -";
        VideoEmpty.Core.Diagnostics.Log.Info("ffmpeg", $"{_bin.FFmpegPath} {args}");
        var psi = new ProcessStartInfo(_bin.FFmpegPath, args)
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
            VideoEmpty.Core.Diagnostics.Log.Error("ffmpeg", "Failed to start ffmpeg", ex);
            throw new InvalidOperationException($"Failed to start ffmpeg ('{_bin.FFmpegPath}'): {ex.Message}", ex);
        }
        if (p is null) throw new InvalidOperationException("Failed to start ffmpeg.");
        using var _ = p;
        using var ms = new MemoryStream();
        var copy = p.StandardOutput.BaseStream.CopyToAsync(ms, ct);
        var err = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        await copy.ConfigureAwait(false);
        if (p.ExitCode != 0)
        {
            var stderr = await err;
            VideoEmpty.Core.Diagnostics.Log.Error("ffmpeg", $"frame extract exit={p.ExitCode} stderr={stderr}");
            throw new InvalidOperationException($"ffmpeg frame extract failed (exit {p.ExitCode}): {stderr.Trim()}");
        }
        return ms.ToArray();
    }

    public async IAsyncEnumerable<FrameStreamItem> StreamFramesAsync(
        string videoPath, int startMs, double fps, int maxWidth,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        _bin.EnsureFFmpeg();
        if (!File.Exists(videoPath))
            throw new FileNotFoundException($"Video file not found: '{videoPath}'.", videoPath);
        if (fps <= 0) fps = 15;
        if (maxWidth <= 0) maxWidth = 1280;

        var ts = TimeSpan.FromMilliseconds(Math.Max(0, startMs)).ToString(@"hh\:mm\:ss\.fff");
        // -ss before -i = fast seek (keyframe). -fflags +genpts for clean timing.
        // scale uses min() so we don't upscale; force-original-aspect ratio keeps shape.
        var vf = $"fps={fps.ToString(System.Globalization.CultureInfo.InvariantCulture)},scale='min({maxWidth},iw)':-2";
        var args = $"-loglevel error -ss {ts} -i \"{videoPath}\" -vf {vf} -f image2pipe -vcodec mjpeg -q:v 6 -";
        VideoEmpty.Core.Diagnostics.Log.Info("ffmpeg", $"stream: {_bin.FFmpegPath} {args}");

        var psi = new ProcessStartInfo(_bin.FFmpegPath, args)
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
            VideoEmpty.Core.Diagnostics.Log.Error("ffmpeg", "Failed to start ffmpeg stream", ex);
            throw new InvalidOperationException($"Failed to start ffmpeg ('{_bin.FFmpegPath}'): {ex.Message}", ex);
        }
        if (p is null) throw new InvalidOperationException("Failed to start ffmpeg.");

        using var proc = p;
        // Drain stderr in the background so the pipe doesn't fill.
        _ = Task.Run(async () =>
        {
            try { await proc.StandardError.ReadToEndAsync().ConfigureAwait(false); }
            catch { /* ignore */ }
        }, CancellationToken.None);

        using var reg = ct.Register(() =>
        {
            try { if (!proc.HasExited) proc.Kill(true); }
            catch { /* ignore */ }
        });

        var stream = proc.StandardOutput.BaseStream;
        var frameMs = 1000.0 / fps;
        int idx = 0;

        await foreach (var jpeg in ReadMjpegFramesAsync(stream, ct).ConfigureAwait(false))
        {
            int timeMs = startMs + (int)Math.Round(idx * frameMs);
            yield return new FrameStreamItem(timeMs, jpeg);
            idx++;
        }

        try { if (!proc.HasExited) proc.Kill(true); }
        catch { /* ignore */ }
    }

    // Parses a concatenated MJPEG stream into discrete JPEG byte arrays by detecting
    // SOI (0xFFD8) start markers. Each frame is [SOI..nextSOI). Final frame is flushed at EOF.
    private static async IAsyncEnumerable<byte[]> ReadMjpegFramesAsync(
        Stream input, [EnumeratorCancellation] CancellationToken ct)
    {
        var buf = new byte[64 * 1024];
        var current = new MemoryStream(256 * 1024);
        bool inFrame = false;
        byte prev = 0;

        while (!ct.IsCancellationRequested)
        {
            int read;
            try { read = await input.ReadAsync(buf.AsMemory(0, buf.Length), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { yield break; }
            catch (IOException) { break; }
            if (read <= 0) break;

            for (int i = 0; i < read; i++)
            {
                byte b = buf[i];
                bool isSoi = prev == 0xFF && b == 0xD8;
                if (isSoi)
                {
                    if (inFrame && current.Length > 2)
                    {
                        // Emit previous frame (drop the trailing 0xFF that started this new SOI).
                        current.SetLength(current.Length - 1);
                        var frame = current.ToArray();
                        current.SetLength(0);
                        yield return frame;
                    }
                    inFrame = true;
                    current.WriteByte(0xFF);
                    current.WriteByte(0xD8);
                }
                else if (inFrame)
                {
                    current.WriteByte(b);
                }
                prev = b;
            }
        }

        if (inFrame && current.Length >= 4)
        {
            yield return current.ToArray();
        }
    }
}
