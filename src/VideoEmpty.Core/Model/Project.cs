namespace VideoEmpty.Core.Model;

public sealed class Project
{
    public string Version { get; set; } = "1";
    public string Name { get; set; } = "Untitled";
    public string? VideoPath { get; set; }
    public Size VideoResolution { get; set; } = new(1920, 1080);
    public double VideoFps { get; set; } = 30.0;
    public int VideoDurationMs { get; set; }
    public List<Template> Templates { get; set; } = new();
    public List<TemplateInstance> Instances { get; set; } = new();
}
