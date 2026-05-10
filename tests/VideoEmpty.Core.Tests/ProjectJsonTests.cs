using VideoEmpty.Core.Api;
using VideoEmpty.Core.Model;
using VideoEmpty.Core.Serialization;
using VideoEmpty.Core.Templates;
using Xunit;

namespace VideoEmpty.Core.Tests;

public class ProjectJsonTests
{
    [Fact]
    public void RoundTrip_NewProjectWithBuiltinTemplates()
    {
        var p = new Project { Name = "Test", VideoPath = "vid.mp4", VideoDurationMs = 12345 };
        foreach (var t in BuiltInTemplates.All()) p.Templates.Add(t);
        p.Instances.Add(new TemplateInstance
        {
            TemplateId = BuiltInTemplates.StepTemplateId,
            Center = new NormalizedPoint(0.25, 0.75),
            StartMs = 1000,
            DurationMs = 3000,
            TextValues = new Dictionary<string, string>
            {
                [BuiltInTemplates.StepNumberElementId] = "1.",
                [BuiltInTemplates.StepTitleElementId] = "Setup"
            }
        });

        var json = ProjectJson.Serialize(p);
        var p2 = ProjectJson.Deserialize(json);

        Assert.Equal(p.Name, p2.Name);
        Assert.Equal(p.VideoPath, p2.VideoPath);
        Assert.Equal(2, p2.Templates.Count);
        Assert.Equal(BuiltInTemplates.StepTemplateId, p2.Templates[0].Id);
        Assert.IsType<ShapeElement>(p2.Templates[0].Elements[0]);
        Assert.IsType<TextElement>(p2.Templates[0].Elements[1]);
        Assert.Single(p2.Instances);
        Assert.Equal(0.25, p2.Instances[0].Center.X);
        Assert.Equal("1.", p2.Instances[0].TextValues[BuiltInTemplates.StepNumberElementId]);
    }

    [Fact]
    public void Color_HexRoundtrip()
    {
        var c = new Color(0x12, 0x34, 0x56, 0x78);
        var c2 = Color.FromHex(c.ToHex());
        Assert.Equal(c, c2);
    }
}
