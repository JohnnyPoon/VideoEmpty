namespace VideoEmpty.Core.Api;

public enum CapCutExportMode
{
    /// <summary>Copy the entire CapCut project folder to a sibling folder, edit the clone.</summary>
    CloneProject,
    /// <summary>Edit draft_content.json in place after saving a timestamped backup.</summary>
    EditInPlace,
}

public sealed record CapCutExportOptions(
    string ProjectFolder,
    CapCutExportMode Mode = CapCutExportMode.CloneProject,
    string? CloneSuffix = null,
    /// <summary>Add a CapCut "Left Slide-In" animation to every emitted segment so they animate on entry (matches the user's reference project).</summary>
    bool IncludeSlideInAnimation = true,
    /// <summary>If the target project already contains materials/segments emitted by a previous VideoEmpty export, remove them first so re-exports are idempotent rather than duplicating content.</summary>
    bool ReplacePreviousExport = true,
    /// <summary>
    /// Multiplier applied to shape_size, fixed_width and border_width at CapCut emit time.
    /// Our template pixel geometry tends to render ~50% bigger inside CapCut than the user
    /// wants on the canvas (verified by comparing an export against the same project
    /// after the user manually resized the items). 0.65 is the empirical midpoint between
    /// Step shapes (≈0.56) and Comment shapes (≈0.75) the user kept. Set 1.0 to disable.
    /// </summary>
    double ShapeScale = 0.65,
    /// <summary>
    /// Multiplier applied to font_size / style.size at CapCut emit time. Same calibration
    /// source as <see cref="ShapeScale"/>: Step text was kept at ≈0.85 of our default,
    /// Comment text at ≈0.96. 0.85 is the conservative midpoint. Set 1.0 to disable.
    /// </summary>
    double FontScale = 0.85);

public sealed record CapCutExportResult(
    string ProjectFolder,
    string DraftContentPath,
    int TextMaterialsAdded,
    int ShapeMaterialsAdded,
    int SegmentsAdded,
    string? BackupPath,
    int PreviousSegmentsRemoved = 0);
