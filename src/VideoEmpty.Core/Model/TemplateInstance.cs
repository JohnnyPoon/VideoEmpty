namespace VideoEmpty.Core.Model;

/// <summary>
/// A placement of a <see cref="Template"/>. Position is normalized (0..1) of the
/// source video resolution and refers to the CENTER of the template.
/// </summary>
public sealed class TemplateInstance
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string TemplateId { get; set; } = "";
    public NormalizedPoint Center { get; set; } = new(0.5, 0.5);
    public Animation? AnimationOverride { get; set; }
    public int StartMs { get; set; }
    public int DurationMs { get; set; } = 3000;
    public Dictionary<string, string> TextValues { get; set; } = new();
}
