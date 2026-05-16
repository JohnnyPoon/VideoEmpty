using SkiaSharp;
using VideoEmpty.Core.Api;
using VideoEmpty.Core.Model;

namespace VideoEmpty.Rendering.Skia;

/// <summary>
/// Composites all template instances active at a given time onto an extracted video frame.
/// The slide-in/out math mirrors the per-frame ffmpeg overlay expressions in
/// <see cref="Export.FFmpegVideoExporter"/> so the preview matches the export output.
/// </summary>
public sealed class PreviewCompositor
{
    private readonly SkiaTemplateRenderer _renderer;
    public PreviewCompositor(SkiaTemplateRenderer renderer) => _renderer = renderer;

    /// <summary>
    /// Composite overlays for <paramref name="timeMs"/> onto the supplied PNG frame.
    /// Returns a new PNG.
    /// </summary>
    public byte[] Compose(byte[] framePng, Project project, int timeMs)
    {
        using var srcData = SKData.CreateCopy(framePng);
        using var src = SKBitmap.Decode(srcData) ?? throw new InvalidOperationException("Frame decode failed.");
        var info = new SKImageInfo(src.Width, src.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var dst = new SKBitmap(info);

        // The frame may be downscaled vs. the project's true video resolution (e.g. during
        // streaming preview). Template coordinates/sizes are authored in original video pixels,
        // so we draw overlays through a scaled canvas to keep their on-screen size proportional.
        int refW = project.VideoResolution.Width  > 0 ? project.VideoResolution.Width  : src.Width;
        int refH = project.VideoResolution.Height > 0 ? project.VideoResolution.Height : src.Height;
        float scaleX = refW > 0 ? (float)src.Width  / refW : 1f;
        float scaleY = refH > 0 ? (float)src.Height / refH : 1f;

        using (var canvas = new SKCanvas(dst))
        {
            canvas.Clear(SKColors.Black);
            canvas.DrawBitmap(src, 0, 0);
            foreach (var inst in project.Instances)
            {
                if (timeMs < inst.StartMs || timeMs > inst.StartMs + inst.DurationMs) continue;
                var template = project.Templates.FirstOrDefault(t => t.Id == inst.TemplateId);
                if (template is null) continue;

                using var overlay = _renderer.RenderBitmap(template, inst.TextValues);
                // Compute position in original-video coordinates, then scale to frame coords.
                var (x, y) = ComputePosition(template, inst, timeMs, refW, refH);
                using var paint = new SKPaint { IsAntialias = true };
                canvas.Save();
                canvas.Scale(scaleX, scaleY);
                canvas.DrawBitmap(overlay, x, y, paint);
                canvas.Restore();
            }
        }
        using var img = SKImage.FromBitmap(dst);
        // JPEG is dramatically faster to encode than PNG and ample quality for on-screen preview.
        // (Export path does not use this compositor — see FFmpegVideoExporter.)
        using var encoded = img.Encode(SKEncodedImageFormat.Jpeg, 85);
        return encoded.ToArray();
    }

    /// <summary>
    /// Top-left coordinate of the overlay at <paramref name="timeMs"/>, including slide animation.
    /// Mirrors FFmpegVideoExporter.BuildAxisExpression.
    /// </summary>
    public static (float x, float y) ComputePosition(Template template, TemplateInstance inst, int timeMs, int videoW, int videoH)
    {
        var anim = inst.AnimationOverride ?? template.Animation;
        double t = timeMs / 1000.0;
        double start = inst.StartMs / 1000.0;
        double end = (inst.StartMs + inst.DurationMs) / 1000.0;
        double enterDur = anim.EnterMs / 1000.0;
        double exitDur = anim.ExitMs / 1000.0;

        double cx = videoW * inst.Center.X;
        double cy = videoH * inst.Center.Y;
        double ow = template.Width;
        double oh = template.Height;
        double centerXRest = cx - ow / 2.0;
        double centerYRest = cy - oh / 2.0;

        bool horizontalPlacement = IsHorizontal(anim.Enter) || IsHorizontal(anim.Exit);
        double xRest = horizontalPlacement ? HorizontalRestX(anim, centerXRest, videoW, ow) : centerXRest;
        double yRest = horizontalPlacement
            ? Math.Clamp(centerYRest, 0.0, Math.Max(0.0, videoH - oh))
            : centerYRest;

        double Off(AnimationStyle dir, bool isX)
        {
            return dir switch
            {
                AnimationStyle.SlideLeft   => isX ? -ow                : yRest,
                AnimationStyle.SlideRight  => isX ? videoW             : yRest,
                AnimationStyle.SlideTop    => isX ? xRest              : -oh,
                AnimationStyle.SlideBottom => isX ? xRest              : videoH,
                _ => isX ? xRest : yRest
            };
        }

        double Axis(bool isX)
        {
            double rest = isX ? xRest : yRest;
            if (enterDur > 0 && t < start + enterDur)
            {
                double off = Off(anim.Enter, isX);
                double k = Math.Clamp((t - start) / enterDur, 0, 1);
                return off + (rest - off) * k;
            }
            if (exitDur > 0 && t > end - exitDur)
            {
                double off = Off(anim.Exit, isX);
                double k = Math.Clamp((t - (end - exitDur)) / exitDur, 0, 1);
                return rest + (off - rest) * k;
            }
            return rest;
        }

        return ((float)Axis(true), (float)Axis(false));
    }

    private static bool IsHorizontal(AnimationStyle style) =>
        style is AnimationStyle.SlideLeft or AnimationStyle.SlideRight;

    private static double HorizontalRestX(Animation anim, double fallbackRestX, int videoW, double overlayW)
    {
        if (anim.Enter == AnimationStyle.SlideLeft || anim.Exit == AnimationStyle.SlideLeft)
            return 0.0;
        if (anim.Enter == AnimationStyle.SlideRight || anim.Exit == AnimationStyle.SlideRight)
            return Math.Max(0.0, videoW - overlayW);
        return fallbackRestX;
    }
}
