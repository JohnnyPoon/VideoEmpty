using VideoEmpty.Core.Api;
using VideoEmpty.Core.Model;

namespace VideoEmpty.Rendering.Export.CapCut;

/// <summary>Adapter that exposes the static <see cref="CapCutProjectExporter"/> as an <see cref="ICapCutExporter"/>.</summary>
public sealed class CapCutExporter : ICapCutExporter
{
    public CapCutExportResult Export(Project project, CapCutExportOptions options)
        => CapCutProjectExporter.Export(project, options);
}
