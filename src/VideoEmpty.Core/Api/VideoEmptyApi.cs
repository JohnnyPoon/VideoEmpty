using VideoEmpty.Core.Model;
using VideoEmpty.Core.Serialization;
using VideoEmpty.Core.Templates;

namespace VideoEmpty.Core.Api;

/// <summary>
/// Default in-process implementation of <see cref="IVideoEmptyApi"/>.
/// Pluggable via the rendering / ffmpeg abstractions so Core stays dependency-free.
/// </summary>
public sealed class VideoEmptyApi : IVideoEmptyApi
{
    private readonly ITemplateRenderer _renderer;
    private readonly IVideoProbe _probe;
    private readonly IFramePreview _framePreview;
    private readonly IVideoExporter _exporter;
    private readonly IDependencyManager _deps;

    public VideoEmptyApi(
        ITemplateRenderer renderer,
        IVideoProbe probe,
        IFramePreview framePreview,
        IVideoExporter exporter,
        IDependencyManager deps,
        Func<byte[], Project, int, byte[]>? compositor = null)
    {
        _renderer = renderer;
        _probe = probe;
        _framePreview = framePreview;
        _exporter = exporter;
        _deps = deps;
        _compositor = compositor;
    }

    public IDependencyManager Dependencies => _deps;

    public Project CreateProject(string name)
    {
        var p = new Project { Name = name };
        foreach (var t in BuiltInTemplates.All())
            p.Templates.Add(t);
        return p;
    }

    public Project OpenProject(string path) => ProjectJson.Load(path);

    public void SaveProject(Project project, string path) => ProjectJson.Save(project, path);

    public async Task<Project> SetVideoAsync(Project project, string videoPath, CancellationToken ct = default)
    {
        var info = await _probe.ProbeAsync(videoPath, ct).ConfigureAwait(false);
        project.VideoPath = videoPath;
        project.VideoResolution = new Size(info.Width, info.Height);
        project.VideoFps = info.Fps;
        project.VideoDurationMs = info.DurationMs;
        return project;
    }

    public IReadOnlyList<Template> ListTemplates(Project project) => project.Templates;

    public Template GetTemplate(Project project, string templateId) =>
        project.Templates.FirstOrDefault(t => t.Id == templateId)
        ?? throw new KeyNotFoundException($"Template '{templateId}' not found.");

    public Template CreateTemplate(Project project, Template template)
    {
        if (project.Templates.Any(t => t.Id == template.Id))
            throw new InvalidOperationException($"Template id '{template.Id}' already exists.");
        project.Templates.Add(template);
        return template;
    }

    public Template UpdateTemplate(Project project, Template template)
    {
        var idx = project.Templates.FindIndex(t => t.Id == template.Id);
        if (idx < 0) throw new KeyNotFoundException($"Template '{template.Id}' not found.");
        project.Templates[idx] = template;
        return template;
    }

    public void DeleteTemplate(Project project, string templateId)
    {
        var t = GetTemplate(project, templateId);
        if (project.Instances.Any(i => i.TemplateId == templateId))
            throw new InvalidOperationException($"Cannot delete template '{templateId}': it is in use.");
        project.Templates.Remove(t);
    }

    public Template DuplicateTemplate(Project project, string templateId, string? newName = null)
    {
        var src = GetTemplate(project, templateId);
        var json = ProjectJson.Options;
        var clone = System.Text.Json.JsonSerializer.Deserialize<Template>(
            System.Text.Json.JsonSerializer.Serialize(src, json), json)!;
        clone.Id = Guid.NewGuid().ToString("n");
        clone.Name = newName ?? src.Name + " copy";
        project.Templates.Add(clone);
        return clone;
    }

    public TemplateInstance AddInstance(Project project, AddInstanceRequest request)
    {
        var template = GetTemplate(project, request.TemplateId);
        var inst = new TemplateInstance
        {
            TemplateId = request.TemplateId,
            Center = NormalizedPoint.Clamp(request.CenterX, request.CenterY),
            AnimationOverride = CloneAnimation(request.AnimationOverride),
            StartMs = Math.Max(0, request.StartMs),
            DurationMs = request.DurationMs ?? template.DefaultDurationMs,
            TextValues = request.TextValues is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string>(request.TextValues)
        };
        project.Instances.Add(inst);
        return inst;
    }

    public TemplateInstance UpdateInstance(Project project, UpdateInstanceRequest request)
    {
        var inst = project.Instances.FirstOrDefault(i => i.Id == request.InstanceId)
            ?? throw new KeyNotFoundException($"Instance '{request.InstanceId}' not found.");
        if (request.CenterX is not null || request.CenterY is not null)
            inst.Center = NormalizedPoint.Clamp(
                request.CenterX ?? inst.Center.X,
                request.CenterY ?? inst.Center.Y);
        if (request.StartMs is not null) inst.StartMs = Math.Max(0, request.StartMs.Value);
        if (request.DurationMs is not null) inst.DurationMs = Math.Max(1, request.DurationMs.Value);
        if (request.TextValues is not null)
            foreach (var kv in request.TextValues)
                inst.TextValues[kv.Key] = kv.Value;
        if (request.AnimationOverride is not null)
            inst.AnimationOverride = CloneAnimation(request.AnimationOverride);
        return inst;
    }

    public void DeleteInstance(Project project, string instanceId)
    {
        var inst = project.Instances.FirstOrDefault(i => i.Id == instanceId)
            ?? throw new KeyNotFoundException($"Instance '{instanceId}' not found.");
        project.Instances.Remove(inst);
    }

    public IReadOnlyList<TemplateInstance> ListInstances(Project project) => project.Instances;

    public Task<byte[]> RenderFrameAsync(Project project, int timeMs, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(project.VideoPath))
            throw new InvalidOperationException("Project has no video.");
        return RenderFrameInternalAsync(project, timeMs, ct);
    }

    private async Task<byte[]> RenderFrameInternalAsync(Project project, int timeMs, CancellationToken ct)
    {
        var raw = await _framePreview.ExtractFrameAsync(project.VideoPath!, timeMs, ct).ConfigureAwait(false);
        return _compositor is null ? raw : _compositor(raw, project, timeMs);
    }

    /// <summary>Optional overlay compositor; when set, RenderFrameAsync returns frames with overlays drawn.</summary>
    private readonly Func<byte[], Project, int, byte[]>? _compositor;

    public byte[] RenderTemplatePreview(Template template, IReadOnlyDictionary<string, string>? textValues = null)
        => _renderer.RenderTemplatePng(template, textValues);

    public string StartExport(Project project, ExportOptions options) =>
        _exporter.Start(project, options);

    public async Task ExportSubtitlesAsync(Project project, ExportSubtitlesOptions options, CancellationToken ct = default)
    {
        var entries = new List<SubtitleEntry>();
        var index = 1;

        foreach (var inst in project.Instances.OrderBy(i => i.StartMs))
        {
            var template = project.Templates.FirstOrDefault(t => t.Id == inst.TemplateId);
            if (template is null) continue;

            // Filter by template type/name if requested
            if (!string.IsNullOrEmpty(options.TemplateTypeFilter) &&
                !template.Name.Contains(options.TemplateTypeFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            // Filter by time range
            var endMs = inst.StartMs + inst.DurationMs;
            if (options.StartTimeMs is not null && endMs < options.StartTimeMs)
                continue;
            if (options.EndTimeMs is not null && inst.StartMs > options.EndTimeMs)
                continue;

            // Combine all text values
            var text = string.Join("\n",
                inst.TextValues.Values
                    .Where(v => !string.IsNullOrWhiteSpace(v)));

            if (!string.IsNullOrEmpty(text))
            {
                entries.Add(new SubtitleEntry(
                    IndexOrId: index++,
                    StartMs: inst.StartMs,
                    EndMs: endMs,
                    Text: text,
                    TemplateName: template.Name,
                    CenterX: inst.Center.X,
                    CenterY: inst.Center.Y));
            }
        }

        var content = options.Format.ToLower() switch
        {
            "srt" => FormatAsSrt(entries),
            "vtt" => FormatAsVtt(entries),
            "json" => FormatAsJson(entries),
            _ => throw new ArgumentException($"Unknown subtitle format: {options.Format}")
        };

        await System.IO.File.WriteAllTextAsync(options.OutputPath, content, ct).ConfigureAwait(false);
    }

    private static string FormatAsSrt(List<SubtitleEntry> entries)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var e in entries)
        {
            sb.AppendLine(e.IndexOrId.ToString());
            sb.AppendLine($"{FormatTimestampSrt(e.StartMs)} --> {FormatTimestampSrt(e.EndMs)}");
            sb.AppendLine(e.Text);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string FormatTimestampSrt(int ms)
    {
        var ts = TimeSpan.FromMilliseconds(ms);
        return $"{ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2},{ts.Milliseconds:D3}";
    }

    private static string FormatAsVtt(List<SubtitleEntry> entries)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("WEBVTT");
        sb.AppendLine();
        foreach (var e in entries)
        {
            sb.AppendLine($"{FormatTimestampVtt(e.StartMs)} --> {FormatTimestampVtt(e.EndMs)}");
            sb.AppendLine(e.Text);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string FormatTimestampVtt(int ms)
    {
        var ts = TimeSpan.FromMilliseconds(ms);
        return $"{ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds:D3}";
    }

    private static string FormatAsJson(List<SubtitleEntry> entries)
    {
        var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
        return System.Text.Json.JsonSerializer.Serialize(entries, options);
    }

    public JobStatus GetJobStatus(string jobId) => _exporter.GetStatus(jobId);

    public void CancelJob(string jobId) => _exporter.Cancel(jobId);

    private static Animation? CloneAnimation(Animation? source)
    {
        if (source is null) return null;
        return new Animation
        {
            Enter = source.Enter,
            Exit = source.Exit,
            EnterMs = source.EnterMs,
            ExitMs = source.ExitMs
        };
    }
}
