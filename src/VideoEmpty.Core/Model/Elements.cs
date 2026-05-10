namespace VideoEmpty.Core.Model;

public enum ShapeKind
{
    Rectangle,
    RoundedRectangle,
    Ellipse
}

public enum HorizontalAlign { Left, Center, Right }
public enum VerticalAlign { Top, Center, Bottom }

public abstract class Element
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public int OffsetX { get; set; }
    public int OffsetY { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

public sealed class ShapeElement : Element
{
    public ShapeKind Shape { get; set; } = ShapeKind.Rectangle;
    public Color Fill { get; set; } = Color.White;
    public Color BorderColor { get; set; } = Color.Black;
    public int BorderThickness { get; set; }
    public int CornerRadius { get; set; }
}

public sealed class TextElement : Element
{
    public string FontFamily { get; set; } = "Segoe UI";
    public int FontSize { get; set; } = 24;
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public Color TextColor { get; set; } = Color.Black;
    public HorizontalAlign HAlign { get; set; } = HorizontalAlign.Center;
    public VerticalAlign VAlign { get; set; } = VerticalAlign.Center;
    public int LineSpacing { get; set; } = 4;
    public string DefaultText { get; set; } = "";
}
