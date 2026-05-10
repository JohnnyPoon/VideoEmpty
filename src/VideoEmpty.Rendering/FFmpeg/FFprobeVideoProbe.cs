using System.Diagnostics;
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
}
