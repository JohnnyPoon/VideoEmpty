using VideoEmpty.Core.Api;
using VideoEmpty.Core.Model;
using VideoEmpty.Core.Templates;
using Xunit;

namespace VideoEmpty.Core.Tests;

public class VideoEmptyApiTests
{
    private static IVideoEmptyApi NewApi() =>
        new VideoEmptyApi(new FakeRenderer(), new FakeProbe(), new FakePreview(), new FakeExporter(), new FakeDeps());

    [Fact]
    public void CreateProject_SeedsBuiltInTemplates()
    {
        var api = NewApi();
        var p = api.CreateProject("p1");
        Assert.Equal(2, p.Templates.Count);
        Assert.Contains(p.Templates, t => t.Id == BuiltInTemplates.StepTemplateId);
        Assert.Contains(p.Templates, t => t.Id == BuiltInTemplates.CommentTemplateId);
    }

    [Fact]
    public void AddInstance_UsesTemplateDefaultDuration_AndClampsCenter()
    {
        var api = NewApi();
        var p = api.CreateProject("p");
        var inst = api.AddInstance(p, new AddInstanceRequest(
            BuiltInTemplates.StepTemplateId, 1.5, -0.2, 500));
        Assert.Equal(3000, inst.DurationMs);
        Assert.Equal(1.0, inst.Center.X);
        Assert.Equal(0.0, inst.Center.Y);
        Assert.Single(p.Instances);
    }

    [Fact]
    public void UpdateInstance_PartialFields()
    {
        var api = NewApi();
        var p = api.CreateProject("p");
        var inst = api.AddInstance(p, new AddInstanceRequest(BuiltInTemplates.StepTemplateId, 0.5, 0.5, 0));
        api.UpdateInstance(p, new UpdateInstanceRequest(inst.Id, DurationMs: 5000,
            TextValues: new() { [BuiltInTemplates.StepNumberElementId] = "2." }));
        Assert.Equal(5000, p.Instances[0].DurationMs);
        Assert.Equal("2.", p.Instances[0].TextValues[BuiltInTemplates.StepNumberElementId]);
    }

    [Fact]
    public void DeleteTemplate_RejectedIfInUse()
    {
        var api = NewApi();
        var p = api.CreateProject("p");
        api.AddInstance(p, new AddInstanceRequest(BuiltInTemplates.StepTemplateId, 0.5, 0.5, 0));
        Assert.Throws<InvalidOperationException>(() => api.DeleteTemplate(p, BuiltInTemplates.StepTemplateId));
    }

    private sealed class FakeRenderer : ITemplateRenderer
    { public byte[] RenderTemplatePng(Template t, IReadOnlyDictionary<string,string>? v=null) => Array.Empty<byte>(); }
    private sealed class FakeProbe : IVideoProbe
    { public Task<VideoInfo> ProbeAsync(string p, CancellationToken ct=default) => Task.FromResult(new VideoInfo(1920,1080,30,10000)); }
    private sealed class FakePreview : IFramePreview
    { public Task<byte[]> ExtractFrameAsync(string p, int t, CancellationToken ct=default) => Task.FromResult(Array.Empty<byte>()); }
    private sealed class FakeExporter : IVideoExporter
    {
        public string Start(Project p, ExportOptions o) => "job1";
        public JobStatus GetStatus(string id) => new() { JobId = id, State = JobState.Completed };
        public void Cancel(string id) { }
    }
    private sealed class FakeDeps : IDependencyManager
    {
        public bool HasMissing => false;
        public Task<IReadOnlyList<DependencyStatus>> CheckAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DependencyStatus>>(Array.Empty<DependencyStatus>());
        public Task InstallMissingAsync(IProgress<DependencyInstallProgress>? p = null, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
