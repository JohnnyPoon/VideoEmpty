namespace VideoEmpty.Core.Model;

public sealed class Template
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string Name { get; set; } = "Untitled";
    public int Width { get; set; } = 400;
    public int Height { get; set; } = 120;
    public List<Element> Elements { get; set; } = new();
    public Animation Animation { get; set; } = new();
    public SoundConfig Sound { get; set; } = new();
    public int DefaultDurationMs { get; set; } = 3000;
}
