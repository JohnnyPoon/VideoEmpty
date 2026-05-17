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
        Width = 540,
        Height = 110,
        DefaultDurationMs = 3000,
        Animation = new Animation { Enter = AnimationStyle.SlideLeft, Exit = AnimationStyle.SlideLeft, EnterMs = 350, ExitMs = 350 },
        Elements =
        {
            new ShapeElement { Id = "step.bg", OffsetX = 0, OffsetY = 0, Width = 540, Height = 110,
                Shape = ShapeKind.Rectangle, Fill = Color.White, BorderColor = Color.Black, BorderThickness = 2 },
            new TextElement { Id = StepNumberElementId, OffsetX = 22, OffsetY = 12, Width = 488, Height = 34,
                FontFamily = "Segoe UI", FontSize = 22, Bold = true, TextColor = Color.Black,
                HAlign = HorizontalAlign.Left, VAlign = VerticalAlign.Top, DefaultText = "2." },
            new TextElement { Id = StepTitleElementId, OffsetX = 22, OffsetY = 52, Width = 488, Height = 50,
                FontFamily = "Segoe UI", FontSize = 22, TextColor = Color.Black,
                HAlign = HorizontalAlign.Left, VAlign = VerticalAlign.Top, DefaultText = "Setup GitHub Repository" }
        }
    };

    public static Template Comment() => new()
    {
        Id = CommentTemplateId,
        Name = "Comment",
        Width = 420,
        Height = 70,
        DefaultDurationMs = 3000,
        Animation = new Animation { Enter = AnimationStyle.SlideLeft, Exit = AnimationStyle.SlideLeft, EnterMs = 350, ExitMs = 350 },
        Elements =
        {
            new ShapeElement { Id = "comment.bg", OffsetX = 0, OffsetY = 0, Width = 420, Height = 70,
                Shape = ShapeKind.Rectangle, Fill = Color.Black, BorderColor = Color.White, BorderThickness = 2 },
            new TextElement { Id = CommentLine1ElementId, OffsetX = 14, OffsetY = 12, Width = 392, Height = 26,
                FontFamily = "Segoe UI", FontSize = 18, TextColor = Color.White,
                HAlign = HorizontalAlign.Left, VAlign = VerticalAlign.Top, DefaultText = "Go to GitHub" },
            new TextElement { Id = CommentLine2ElementId, OffsetX = 14, OffsetY = 38, Width = 392, Height = 26,
                FontFamily = "Segoe UI", FontSize = 18, TextColor = Color.White,
                HAlign = HorizontalAlign.Left, VAlign = VerticalAlign.Top, DefaultText = "Register or login" }
        }
    };

    public static IEnumerable<Template> All() => new[] { Step(), Comment() };
}
