using System.Collections.Generic;
using VideoEmpty.Core.Model;

namespace VideoEmpty.Core.Api;

public sealed record AddInstanceRequest(
    string TemplateId,
    double CenterX,
    double CenterY,
    int StartMs,
    int? DurationMs = null,
    Dictionary<string, string>? TextValues = null,
    Animation? AnimationOverride = null);

public sealed record UpdateInstanceRequest(
    string InstanceId,
    double? CenterX = null,
    double? CenterY = null,
    int? StartMs = null,
    int? DurationMs = null,
    Dictionary<string, string>? TextValues = null,
    Animation? AnimationOverride = null);

public sealed record VideoInfo(int Width, int Height, double Fps, int DurationMs);

public sealed record ExportOptions(
    string OutputPath,
    string VideoCodec = "libx264",
    string AudioCodec = "aac",
    int? VideoBitrateKbps = null,
    int? Crf = 18,
    bool UseHardwareAcceleration = true,
    string? Preset = null);

public sealed record ExportSubtitlesOptions(
    string OutputPath,
    string Format = "srt", // "srt", "vtt", "json"
    string? TemplateTypeFilter = null, // legacy: single template name substring (null = all)
    int? StartTimeMs = null,
    int? EndTimeMs = null,
    IReadOnlyList<string>? TemplateNameFilters = null); // null/empty = all; otherwise instance template name must match one of these (exact match)

public sealed record SubtitleEntry(
    int IndexOrId,
    int StartMs,
    int EndMs,
    string Text,
    string TemplateName,
    double? CenterX,
    double? CenterY);

public enum JobState { Pending, Running, Completed, Failed, Cancelled }

public sealed class JobStatus
{
    public string JobId { get; set; } = "";
    public JobState State { get; set; }
    public double Progress { get; set; } // 0..1
    public string? Message { get; set; }
    public string? OutputPath { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Unified API surface for VideoEmpty. Implemented in-process; UI calls it directly,
/// HTTP server and MCP server are thin adapters over this interface.
/// </summary>
public interface IVideoEmptyApi
{
    // Project
    Project CreateProject(string name);
    Project OpenProject(string path);
    void SaveProject(Project project, string path);
    Task<Project> SetVideoAsync(Project project, string videoPath, CancellationToken ct = default);

    // Templates
    IReadOnlyList<Template> ListTemplates(Project project);
    Template GetTemplate(Project project, string templateId);
    Template CreateTemplate(Project project, Template template);
    Template UpdateTemplate(Project project, Template template);
    void DeleteTemplate(Project project, string templateId);
    Template DuplicateTemplate(Project project, string templateId, string? newName = null);

    // Instances
    TemplateInstance AddInstance(Project project, AddInstanceRequest request);
    TemplateInstance UpdateInstance(Project project, UpdateInstanceRequest request);
    void DeleteInstance(Project project, string instanceId);
    IReadOnlyList<TemplateInstance> ListInstances(Project project);

    // Preview
    Task<byte[]> RenderFrameAsync(Project project, int timeMs, CancellationToken ct = default);
    byte[] RenderTemplatePreview(Template template, IReadOnlyDictionary<string, string>? textValues = null);

    /// <summary>
    /// Streams composed preview frames (overlays drawn) starting at <paramref name="startMs"/>
    /// at the requested <paramref name="fps"/>. The producer pushes frames as fast as they decode;
    /// the consumer is responsible for pacing/discarding to match wall-clock playback.
    /// Each yielded frame is a JPEG byte array tagged with its logical playback timestamp.
    /// </summary>
    IAsyncEnumerable<FrameStreamItem> StreamPreviewFramesAsync(
        Project project, int startMs, double fps, int maxWidth, CancellationToken ct = default);

    // Export
    string StartExport(Project project, ExportOptions options);
    Task ExportSubtitlesAsync(Project project, ExportSubtitlesOptions options, CancellationToken ct = default);
    /// <summary>Append our template instances into an existing CapCut PC project folder.</summary>
    CapCutExportResult ExportToCapCut(Project project, CapCutExportOptions options);
    JobStatus GetJobStatus(string jobId);
    void CancelJob(string jobId);

    // Dependencies
    IDependencyManager Dependencies { get; }
}
