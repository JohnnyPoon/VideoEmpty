using VideoEmpty.Core.Templates;
using VideoEmpty.Rendering.Skia;
using Xunit;

namespace VideoEmpty.Rendering.Tests;

public class SkiaTemplateRendererTests
{
    [Fact]
    public void RendersBuiltInStep_ToPng()
    {
        var r = new SkiaTemplateRenderer();
        var bytes = r.RenderTemplatePng(BuiltInTemplates.Step(),
            new Dictionary<string,string>
            {
                [BuiltInTemplates.StepNumberElementId] = "2.",
                [BuiltInTemplates.StepTitleElementId] = "Setup GitHub"
            });
        Assert.NotEmpty(bytes);
        // PNG signature
        Assert.Equal(0x89, bytes[0]);
        Assert.Equal((byte)'P', bytes[1]);
        Assert.Equal((byte)'N', bytes[2]);
        Assert.Equal((byte)'G', bytes[3]);
    }

    [Fact]
    public void RendersBuiltInComment_ToPng()
    {
        var r = new SkiaTemplateRenderer();
        var bytes = r.RenderTemplatePng(BuiltInTemplates.Comment());
        Assert.NotEmpty(bytes);
        Assert.Equal(0x89, bytes[0]);
    }
}
