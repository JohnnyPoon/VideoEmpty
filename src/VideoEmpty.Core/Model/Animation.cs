namespace VideoEmpty.Core.Model;

public enum AnimationStyle
{
    None,
    SlideLeft,
    SlideRight,
    SlideTop,
    SlideBottom,
    Fade
}

public sealed class Animation
{
    public AnimationStyle Enter { get; set; } = AnimationStyle.SlideLeft;
    public AnimationStyle Exit { get; set; } = AnimationStyle.SlideLeft;
    public int EnterMs { get; set; } = 350;
    public int ExitMs { get; set; } = 350;
}

public sealed class SoundConfig
{
    public string? EnterFile { get; set; }
    public string? ExitFile { get; set; }
    public double Volume { get; set; } = 1.0;
}
