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
    bool ReplacePreviousExport = true);

public sealed record CapCutExportResult(
    string ProjectFolder,
    string DraftContentPath,
    int TextMaterialsAdded,
    int ShapeMaterialsAdded,
    int SegmentsAdded,
    string? BackupPath,
    int PreviousSegmentsRemoved = 0);
