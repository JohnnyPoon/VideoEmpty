using SkiaSharp;
using VideoEmpty.Core.Api;
using VideoEmpty.Core.Model;

namespace VideoEmpty.Rendering.Skia;

public sealed class SkiaTemplateRenderer : ITemplateRenderer
{
    /// <summary>Renders the static (no animation) frame of a template as a PNG.</summary>
    public byte[] RenderTemplatePng(Template template, IReadOnlyDictionary<string, string>? textValues = null)
    {
        using var bmp = RenderBitmap(template, textValues);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public SKBitmap RenderBitmap(Template template, IReadOnlyDictionary<string, string>? textValues)
    {
        var info = new SKImageInfo(template.Width, template.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var bmp = new SKBitmap(info);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Transparent);
        foreach (var el in template.Elements)
        {
            switch (el)
            {
                case ShapeElement s: DrawShape(canvas, s); break;
                case TextElement t:  DrawText(canvas, t, textValues); break;
            }
        }
        return bmp;
    }

    private static void DrawShape(SKCanvas canvas, ShapeElement s)
    {
        var rect = SKRect.Create(s.OffsetX, s.OffsetY, s.Width, s.Height);
        using var fill = new SKPaint { Color = ToSk(s.Fill), Style = SKPaintStyle.Fill, IsAntialias = true };
        switch (s.Shape)
        {
            case ShapeKind.Rectangle: canvas.DrawRect(rect, fill); break;
            case ShapeKind.RoundedRectangle: canvas.DrawRoundRect(rect, s.CornerRadius, s.CornerRadius, fill); break;
            case ShapeKind.Ellipse: canvas.DrawOval(rect, fill); break;
        }
        if (s.BorderThickness > 0)
        {
            using var border = new SKPaint
            {
                Color = ToSk(s.BorderColor),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = s.BorderThickness,
                IsAntialias = true
            };
            // Inset by half stroke so border lies inside the bounds.
            var inset = rect;
            inset.Inflate(-s.BorderThickness / 2f, -s.BorderThickness / 2f);
            switch (s.Shape)
            {
                case ShapeKind.Rectangle: canvas.DrawRect(inset, border); break;
                case ShapeKind.RoundedRectangle: canvas.DrawRoundRect(inset, s.CornerRadius, s.CornerRadius, border); break;
                case ShapeKind.Ellipse: canvas.DrawOval(inset, border); break;
            }
        }
    }

    private static void DrawText(SKCanvas canvas, TextElement t, IReadOnlyDictionary<string, string>? values)
    {
        var text = values is not null && values.TryGetValue(t.Id, out var v) ? v : t.DefaultText;
        if (string.IsNullOrEmpty(text)) return;
        var lines = text.Replace("\r\n", "\n").Split('\n');

        var style = (t.Bold, t.Italic) switch
        {
            (true, true)   => SKFontStyle.BoldItalic,
            (true, false)  => SKFontStyle.Bold,
            (false, true)  => SKFontStyle.Italic,
            _              => SKFontStyle.Normal
        };
        using var typeface = SKTypeface.FromFamilyName(t.FontFamily, style)
                              ?? SKTypeface.FromFamilyName(null, style)
                              ?? SKTypeface.Default;
        using var font = new SKFont(typeface, t.FontSize);
        using var paint = new SKPaint
        {
            Color = ToSk(t.TextColor),
            IsAntialias = true
        };

        var metrics = font.Metrics;
        float lineHeight = metrics.Descent - metrics.Ascent + t.LineSpacing;
        float blockHeight = lineHeight * lines.Length - t.LineSpacing;

        float blockTop = t.VAlign switch
        {
            VerticalAlign.Top    => t.OffsetY,
            VerticalAlign.Bottom => t.OffsetY + t.Height - blockHeight,
            _                    => t.OffsetY + (t.Height - blockHeight) / 2f
        };

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            float lineWidth = font.MeasureText(line);
            float x = t.HAlign switch
            {
                HorizontalAlign.Left   => t.OffsetX,
                HorizontalAlign.Right  => t.OffsetX + t.Width - lineWidth,
                _                      => t.OffsetX + (t.Width - lineWidth) / 2f
            };
            float baseline = blockTop + i * lineHeight - metrics.Ascent;
            canvas.DrawText(line, x, baseline, SKTextAlign.Left, font, paint);
        }
    }

    private static SKColor ToSk(Color c) => new(c.R, c.G, c.B, c.A);
}
