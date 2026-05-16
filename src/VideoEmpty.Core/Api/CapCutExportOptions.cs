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
    string? CloneSuffix = null);

public sealed record CapCutExportResult(
    string ProjectFolder,
    string DraftContentPath,
    int TextMaterialsAdded,
    int ShapeMaterialsAdded,
    int SegmentsAdded,
    string? BackupPath);
