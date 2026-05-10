using VideoEmpty.Core.Model;

namespace VideoEmpty.Core.Templates;

public static class BuiltInTemplates
{
    public const string StepTemplateId      = "builtin.step";
    public const string CommentTemplateId   = "builtin.comment";
    public const string StepNumberElementId    = "step.number";
    public const string StepTitleElementId     = "step.title";
    public const string CommentLine1ElementId  = "comment.line1";
    public const string CommentLine2ElementId  = "comment.line2";

    public static Template Step() => new()
    {
        Id = StepTemplateId,
        Name = "Step",
        Width = 1280,
        Height = 300,
        DefaultDurationMs = 3000,
        Animation = new Animation { Enter = AnimationStyle.SlideLeft, Exit = AnimationStyle.SlideLeft, EnterMs = 350, ExitMs = 350 },
        Elements =
        {
            new ShapeElement { Id = "step.bg", OffsetX = 0, OffsetY = 0, Width = 1280, Height = 300,
                Shape = ShapeKind.Rectangle, Fill = Color.White, BorderColor = Color.Black, BorderThickness = 6 },
            new TextElement { Id = StepNumberElementId, OffsetX = 56, OffsetY = 36, Width = 1168, Height = 92,
                FontFamily = "Segoe UI", FontSize = 70, Bold = true, TextColor = Color.Black,
                HAlign = HorizontalAlign.Left, VAlign = VerticalAlign.Top, DefaultText = "2." },
            new TextElement { Id = StepTitleElementId, OffsetX = 56, OffsetY = 132, Width = 1168, Height = 136,
                FontFamily = "Segoe UI", FontSize = 66, TextColor = Color.Black,
                HAlign = HorizontalAlign.Left, VAlign = VerticalAlign.Top, DefaultText = "Setup GitHub Repository" }
        }
    };

    public static Template Comment() => new()
    {
        Id = CommentTemplateId,
        Name = "Comment",
        Width = 960,
        Height = 150,
        DefaultDurationMs = 3000,
        Animation = new Animation { Enter = AnimationStyle.SlideLeft, Exit = AnimationStyle.SlideLeft, EnterMs = 350, ExitMs = 350 },
        Elements =
        {
            new ShapeElement { Id = "comment.bg", OffsetX = 0, OffsetY = 0, Width = 960, Height = 150,
                Shape = ShapeKind.Rectangle, Fill = Color.Black, BorderColor = Color.White, BorderThickness = 6 },
            new TextElement { Id = CommentLine1ElementId, OffsetX = 22, OffsetY = 22, Width = 916, Height = 52,
                FontFamily = "Segoe UI", FontSize = 52, TextColor = Color.White,
                HAlign = HorizontalAlign.Left, VAlign = VerticalAlign.Top, DefaultText = "Go to GitHub" },
            new TextElement { Id = CommentLine2ElementId, OffsetX = 22, OffsetY = 78, Width = 916, Height = 52,
                FontFamily = "Segoe UI", FontSize = 52, TextColor = Color.White,
                HAlign = HorizontalAlign.Left, VAlign = VerticalAlign.Top, DefaultText = "Register or login" }
        }
    };

    public static IEnumerable<Template> All() => new[] { Step(), Comment() };
}
