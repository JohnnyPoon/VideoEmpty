using System.Text.Json;
using System.Text.Json.Nodes;
using VideoEmpty.Core.Api;
using VideoEmpty.Core.Model;
using VideoEmpty.Rendering.Export.CapCut;
using Xunit;

namespace VideoEmpty.Rendering.Tests;

public class CapCutProjectExporterTests
{
    private static string CreateMinimalProject(int w = 1920, int h = 1080)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ve-cc-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var root = new JsonObject
        {
            ["id"] = Guid.NewGuid().ToString("D").ToUpperInvariant(),
            ["version"] = 1,
            ["duration"] = 0,
            ["fps"] = 30,
            ["canvas_config"] = new JsonObject { ["width"] = w, ["height"] = h, ["ratio"] = "16:9" },
            ["materials"] = new JsonObject
            {
                ["texts"] = new JsonArray(),
                ["shapes"] = new JsonArray(),
            },
            ["tracks"] = new JsonArray(),
        };
        File.WriteAllText(Path.Combine(dir, "draft_content.json"), root.ToJsonString());
        return dir;
    }

    private static (Project project, Template template) BuildSampleProject()
    {
        var tpl = new Template
        {
            Id = "tpl1",
            Name = "Step",
            Width = 400,
            Height = 120,
            Elements =
            {
                new ShapeElement
                {
                    OffsetX = 0, OffsetY = 0, Width = 400, Height = 120,
                    Shape = ShapeKind.Rectangle,
                    Fill = new Color(255, 255, 255), BorderColor = new Color(0, 0, 0), BorderThickness = 2,
                },
                new TextElement
                {
                    OffsetX = 10, OffsetY = 10, Width = 380, Height = 100,
                    DefaultText = "Hello CapCut",
                    FontSize = 30,
                    TextColor = new Color(0, 0, 0),
                },
            },
        };
        var proj = new Project
        {
            VideoResolution = new Size(1920, 1080),
            VideoDurationMs = 10_000,
            Templates = { tpl },
            Instances =
            {
                // Center of canvas -> CapCut transform (0, 0)
                new TemplateInstance { TemplateId = "tpl1", Center = new NormalizedPoint(0.5, 0.5), StartMs = 1000, DurationMs = 2000 },
                // Top-left corner -> CapCut transform (-1, +1)
                new TemplateInstance { TemplateId = "tpl1", Center = new NormalizedPoint(0.0, 0.0), StartMs = 4000, DurationMs = 1500 },
            },
        };
        return (proj, tpl);
    }

    [Fact]
    public void CloneMode_CreatesSiblingFolder_AndAppendsMaterialsAndSegments()
    {
        var src = CreateMinimalProject();
        try
        {
            var (proj, _) = BuildSampleProject();
            var result = CapCutProjectExporter.Export(proj, new CapCutExportOptions(src, CapCutExportMode.CloneProject));

            Assert.NotEqual(src, result.ProjectFolder);
            Assert.True(Directory.Exists(result.ProjectFolder));
            Assert.True(File.Exists(result.DraftContentPath));
            // Source untouched
            var origRoot = JsonNode.Parse(File.ReadAllText(Path.Combine(src, "draft_content.json")))!.AsObject();
            Assert.Empty(origRoot["materials"]!["texts"]!.AsArray());
            Assert.Empty(origRoot["materials"]!["shapes"]!.AsArray());

            // Clone has the new content
            var newRoot = JsonNode.Parse(File.ReadAllText(result.DraftContentPath))!.AsObject();
            Assert.Equal(2, result.TextMaterialsAdded);
            Assert.Equal(2, result.ShapeMaterialsAdded);
            Assert.Equal(4, result.SegmentsAdded);
            Assert.Equal(2, newRoot["materials"]!["texts"]!.AsArray().Count);
            Assert.Equal(2, newRoot["materials"]!["shapes"]!.AsArray().Count);

            // duration extended to last instance end (4000+1500=5500ms => 5_500_000 µs)
            Assert.True(newRoot["duration"]!.GetValue<long>() >= 5_500_000);
        }
        finally
        {
            TryDelete(src);
        }
    }

    [Fact]
    public void EditInPlace_WritesBackup_AndModifiesOriginal()
    {
        var src = CreateMinimalProject();
        try
        {
            var (proj, _) = BuildSampleProject();
            var result = CapCutProjectExporter.Export(proj, new CapCutExportOptions(src, CapCutExportMode.EditInPlace));

            Assert.Equal(src, result.ProjectFolder);
            Assert.NotNull(result.BackupPath);
            Assert.True(File.Exists(result.BackupPath!));

            // Backup is the original (empty) content
            var bak = JsonNode.Parse(File.ReadAllText(result.BackupPath!))!.AsObject();
            Assert.Empty(bak["materials"]!["texts"]!.AsArray());

            // Original now has new content
            var nowRoot = JsonNode.Parse(File.ReadAllText(Path.Combine(src, "draft_content.json")))!.AsObject();
            Assert.Equal(2, nowRoot["materials"]!["texts"]!.AsArray().Count);
        }
        finally
        {
            TryDelete(src);
        }
    }

    [Fact]
    public void Segments_HaveMicrosecondTimingAndNormalizedTransform()
    {
        var src = CreateMinimalProject();
        try
        {
            var (proj, _) = BuildSampleProject();
            var result = CapCutProjectExporter.Export(proj, new CapCutExportOptions(src, CapCutExportMode.CloneProject));
            var root = JsonNode.Parse(File.ReadAllText(result.DraftContentPath))!.AsObject();

            var segs = new List<JsonObject>();
            foreach (var t in root["tracks"]!.AsArray())
            foreach (var s in t!.AsObject()["segments"]!.AsArray())
                segs.Add(s!.AsObject());

            // first instance starts at 1000ms => 1_000_000 µs, duration 2_000_000 µs
            Assert.Contains(segs, s =>
                s["target_timerange"]!["start"]!.GetValue<long>() == 1_000_000 &&
                s["target_timerange"]!["duration"]!.GetValue<long>() == 2_000_000);

            // All transforms must be within [-1, 1]
            foreach (var s in segs)
            {
                var tx = s["clip"]!["transform"]!["x"]!.GetValue<double>();
                var ty = s["clip"]!["transform"]!["y"]!.GetValue<double>();
                Assert.InRange(tx, -1.0, 1.0);
                Assert.InRange(ty, -1.0, 1.0);
            }
        }
        finally
        {
            TryDelete(src);
        }
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            // also any sibling clone created
            var parent = Path.GetDirectoryName(dir);
            var name = Path.GetFileName(dir);
            if (parent != null)
                foreach (var d in Directory.GetDirectories(parent, name + "*"))
                    try { Directory.Delete(d, true); } catch { }
        }
        catch { }
    }
}
