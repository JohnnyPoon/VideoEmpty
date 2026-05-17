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

    [Fact]
    public void EveryEmittedSegment_GetsSlideInAnimationReference()
    {
        var src = CreateMinimalProject();
        try
        {
            var (proj, _) = BuildSampleProject();
            var result = CapCutProjectExporter.Export(proj, new CapCutExportOptions(src, CapCutExportMode.CloneProject));
            var root = JsonNode.Parse(File.ReadAllText(result.DraftContentPath))!.AsObject();

            var anims = root["materials"]!["material_animations"]!.AsArray();
            // One animation per segment.
            Assert.Equal(result.SegmentsAdded, anims.Count);
            // All are sticker_animation "in" Left Slide-In
            foreach (var a in anims)
            {
                Assert.Equal("sticker_animation", a!["type"]!.GetValue<string>());
                var inner = a["animations"]!.AsArray()[0]!;
                Assert.Equal("in", inner["type"]!.GetValue<string>());
                Assert.Equal("Left Slide-In", inner["name"]!.GetValue<string>());
            }

            // Each segment's extra_material_refs points to an existing animation id.
            var animIds = new HashSet<string>(anims.Select(a => a!["id"]!.GetValue<string>()));
            foreach (var t in root["tracks"]!.AsArray())
            foreach (var s in t!.AsObject()["segments"]!.AsArray())
            {
                var refs = s!["extra_material_refs"]!.AsArray();
                Assert.NotEmpty(refs);
                Assert.Contains(refs[0]!.GetValue<string>(), animIds);
            }
        }
        finally
        {
            TryDelete(src);
        }
    }

    [Fact]
    public void IncludeSlideInAnimation_False_EmitsNoAnimations()
    {
        var src = CreateMinimalProject();
        try
        {
            var (proj, _) = BuildSampleProject();
            var result = CapCutProjectExporter.Export(proj,
                new CapCutExportOptions(src, CapCutExportMode.CloneProject, IncludeSlideInAnimation: false));
            var root = JsonNode.Parse(File.ReadAllText(result.DraftContentPath))!.AsObject();
            Assert.Empty(root["materials"]!["material_animations"]!.AsArray());
            foreach (var t in root["tracks"]!.AsArray())
            foreach (var s in t!.AsObject()["segments"]!.AsArray())
                Assert.Empty(s!["extra_material_refs"]!.AsArray());
        }
        finally
        {
            TryDelete(src);
        }
    }

    [Fact]
    public void ReExport_ReplacesPriorVideoEmptyContent_NoDuplicates()
    {
        var src = CreateMinimalProject();
        try
        {
            var (proj, _) = BuildSampleProject();

            // 1st export: edit-in-place so the same folder is touched twice.
            var first = CapCutProjectExporter.Export(proj, new CapCutExportOptions(src, CapCutExportMode.EditInPlace));
            Assert.Equal(0, first.PreviousSegmentsRemoved);

            // 2nd export: same project, same folder.
            var second = CapCutProjectExporter.Export(proj, new CapCutExportOptions(src, CapCutExportMode.EditInPlace));
            Assert.Equal(first.SegmentsAdded, second.PreviousSegmentsRemoved);

            var root = JsonNode.Parse(File.ReadAllText(second.DraftContentPath))!.AsObject();
            // After the 2nd export the totals match a single export, not double.
            Assert.Equal(second.TextMaterialsAdded, root["materials"]!["texts"]!.AsArray().Count);
            Assert.Equal(second.ShapeMaterialsAdded, root["materials"]!["shapes"]!.AsArray().Count);
            int totalSegs = 0;
            foreach (var t in root["tracks"]!.AsArray())
                totalSegs += t!.AsObject()["segments"]!.AsArray().Count;
            Assert.Equal(second.SegmentsAdded, totalSegs);
        }
        finally
        {
            TryDelete(src);
        }
    }

    [Fact]
    public void ReExport_PreservesUserAddedContent()
    {
        var src = CreateMinimalProject();
        try
        {
            // Manually add a non-VideoEmpty segment+material that must survive a re-export.
            var draftPath = Path.Combine(src, "draft_content.json");
            var root = JsonNode.Parse(File.ReadAllText(draftPath))!.AsObject();
            var userMaterialId = Guid.NewGuid().ToString("D").ToUpperInvariant();
            root["materials"]!["texts"]!.AsArray().Add(new JsonObject
            {
                ["id"] = userMaterialId,
                ["type"] = "text",
                ["content"] = "user added",
            });
            root["tracks"]!.AsArray().Add(new JsonObject
            {
                ["type"] = "text",
                ["segments"] = new JsonArray(new JsonObject
                {
                    ["id"] = Guid.NewGuid().ToString("D").ToUpperInvariant(),
                    ["material_id"] = userMaterialId,
                    ["group_id"] = "",
                    ["target_timerange"] = new JsonObject { ["start"] = 0L, ["duration"] = 1000L },
                }),
            });
            File.WriteAllText(draftPath, root.ToJsonString());

            var (proj, _) = BuildSampleProject();
            CapCutProjectExporter.Export(proj, new CapCutExportOptions(src, CapCutExportMode.EditInPlace));
            CapCutProjectExporter.Export(proj, new CapCutExportOptions(src, CapCutExportMode.EditInPlace));

            var after = JsonNode.Parse(File.ReadAllText(draftPath))!.AsObject();
            // User-added material is still present.
            Assert.Contains(after["materials"]!["texts"]!.AsArray(),
                m => m!["id"]!.GetValue<string>() == userMaterialId);
        }
        finally
        {
            TryDelete(src);
        }
    }

    [Fact]
    public void TextTrack_AppendedAfterStickerTrack_SoTextRendersAboveShapes()
    {
        var src = CreateMinimalProject();
        try
        {
            var (proj, _) = BuildSampleProject();
            var result = CapCutProjectExporter.Export(proj, new CapCutExportOptions(src, CapCutExportMode.CloneProject));
            var root = JsonNode.Parse(File.ReadAllText(result.DraftContentPath))!.AsObject();

            var tracks = root["tracks"]!.AsArray();
            int stickerIdx = -1, textIdx = -1;
            for (int i = 0; i < tracks.Count; i++)
            {
                var t = tracks[i]!.AsObject();
                if (t["videoempty_origin"]?.GetValue<string>() != "videoempty") continue;
                var type = t["type"]!.GetValue<string>();
                if (type == "sticker") stickerIdx = i;
                else if (type == "text") textIdx = i;
            }
            Assert.True(stickerIdx >= 0 && textIdx >= 0, "Both tagged tracks should be present");
            Assert.True(textIdx > stickerIdx,
                "Text track must come AFTER sticker track so text renders above shapes in CapCut");
        }
        finally { TryDelete(src); }
    }

    [Fact]
    public void TextSegments_HaveHigherRenderIndexThanShapeSegments()
    {
        var src = CreateMinimalProject();
        try
        {
            var (proj, _) = BuildSampleProject();
            var result = CapCutProjectExporter.Export(proj, new CapCutExportOptions(src, CapCutExportMode.CloneProject));
            var root = JsonNode.Parse(File.ReadAllText(result.DraftContentPath))!.AsObject();

            int minTextRi = int.MaxValue, maxShapeRi = int.MinValue;
            foreach (var t in root["tracks"]!.AsArray())
            {
                var track = t!.AsObject();
                var type = track["type"]!.GetValue<string>();
                foreach (var s in track["segments"]!.AsArray())
                {
                    var ri = s!["render_index"]!.GetValue<int>();
                    if (type == "text") minTextRi = Math.Min(minTextRi, ri);
                    else if (type == "sticker") maxShapeRi = Math.Max(maxShapeRi, ri);
                }
            }
            Assert.True(minTextRi > maxShapeRi,
                $"Text render_index ({minTextRi}) must exceed shape render_index ({maxShapeRi})");
        }
        finally { TryDelete(src); }
    }

    [Fact]
    public void TrackRenderIndex_MatchesTrackArrayIndex_AndAllShapesShareOneTrack()
    {
        var src = CreateMinimalProject();
        try
        {
            var (proj, _) = BuildSampleProject();
            var result = CapCutProjectExporter.Export(proj, new CapCutExportOptions(src, CapCutExportMode.CloneProject));
            var root = JsonNode.Parse(File.ReadAllText(result.DraftContentPath))!.AsObject();

            var tracks = root["tracks"]!.AsArray();
            for (int i = 0; i < tracks.Count; i++)
            {
                var t = tracks[i]!.AsObject();
                if (t["videoempty_origin"]?.GetValue<string>() != "videoempty") continue;
                foreach (var s in t["segments"]!.AsArray())
                    Assert.Equal(i, s!["track_render_index"]!.GetValue<int>());
            }

            // All shapes (from 2 instances) end up on a single sticker track row.
            var stickerTrack = tracks.Single(t =>
                t!.AsObject()["videoempty_origin"]?.GetValue<string>() == "videoempty" &&
                t["type"]!.GetValue<string>() == "sticker");
            Assert.Equal(2, stickerTrack!["segments"]!.AsArray().Count);
        }
        finally { TryDelete(src); }
    }

    [Fact]
    public void TextMaterial_FontSize_IsStyleSize_NotRawFontSize()
    {
        var src = CreateMinimalProject();
        try
        {
            var (proj, _) = BuildSampleProject();
            // FontSize on the TextElement is 30 in BuildSampleProject.
            var result = CapCutProjectExporter.Export(proj, new CapCutExportOptions(src, CapCutExportMode.CloneProject));
            var root = JsonNode.Parse(File.ReadAllText(result.DraftContentPath))!.AsObject();

            var firstText = root["materials"]!["texts"]!.AsArray()[0]!.AsObject();
            // CapCut convention from reference: font_size = text_size / 5
            Assert.Equal(30.0 / 5.0, firstText["font_size"]!.GetValue<double>(), precision: 4);
            Assert.Equal(30, firstText["text_size"]!.GetValue<int>());
        }
        finally { TryDelete(src); }
    }

    [Fact]
    public void Slot_NonOverlappingInstances_AllShareOneTrackPerElement()
    {
        var src = CreateMinimalProject();
        try
        {
            // BuildSampleProject has 1 shape element + 1 text element; 2 non-overlapping instances
            // (1000-3000 and 4000-5500). Expect 1 sticker track (2 segs) and 1 text track (2 segs).
            var (proj, _) = BuildSampleProject();
            var result = CapCutProjectExporter.Export(proj, new CapCutExportOptions(src, CapCutExportMode.CloneProject));
            var root = JsonNode.Parse(File.ReadAllText(result.DraftContentPath))!.AsObject();

            var oursStickers = root["tracks"]!.AsArray()
                .Where(t => t!.AsObject()["videoempty_origin"]?.GetValue<string>() == "videoempty"
                            && t["type"]!.GetValue<string>() == "sticker").ToList();
            var oursTexts = root["tracks"]!.AsArray()
                .Where(t => t!.AsObject()["videoempty_origin"]?.GetValue<string>() == "videoempty"
                            && t["type"]!.GetValue<string>() == "text").ToList();
            Assert.Single(oursStickers);
            Assert.Single(oursTexts);
            Assert.Equal(2, oursStickers[0]!["segments"]!.AsArray().Count);
            Assert.Equal(2, oursTexts[0]!["segments"]!.AsArray().Count);
        }
        finally { TryDelete(src); }
    }

    [Fact]
    public void Slot_OverlappingInstances_SpillToSpareTracks()
    {
        var src = CreateMinimalProject();
        try
        {
            // Create 3 overlapping instances so the text element needs 3 rows: primary + 2 spares.
            var (proj, tpl) = BuildSampleProject();
            proj.Instances.Clear();
            proj.Instances.Add(new TemplateInstance { TemplateId = tpl.Id, Center = new NormalizedPoint(0.5, 0.5), StartMs = 0,    DurationMs = 5000 });
            proj.Instances.Add(new TemplateInstance { TemplateId = tpl.Id, Center = new NormalizedPoint(0.5, 0.5), StartMs = 1000, DurationMs = 5000 });
            proj.Instances.Add(new TemplateInstance { TemplateId = tpl.Id, Center = new NormalizedPoint(0.5, 0.5), StartMs = 2000, DurationMs = 5000 });

            var result = CapCutProjectExporter.Export(proj, new CapCutExportOptions(src, CapCutExportMode.CloneProject));
            var root = JsonNode.Parse(File.ReadAllText(result.DraftContentPath))!.AsObject();

            var ourTextTracks = root["tracks"]!.AsArray()
                .Where(t => t!.AsObject()["videoempty_origin"]?.GetValue<string>() == "videoempty"
                            && t["type"]!.GetValue<string>() == "text").ToList();
            Assert.Equal(3, ourTextTracks.Count);

            // Naming: primary then "spare 1", "spare 2".
            var names = ourTextTracks.Select(t => t!["name"]!.GetValue<string>()).ToList();
            Assert.Single(names, n => !n.Contains("spare"));
            Assert.Single(names, n => n.EndsWith("spare 1"));
            Assert.Single(names, n => n.EndsWith("spare 2"));

            // Each spare track holds exactly one of the overlapping segments.
            Assert.All(ourTextTracks, t => Assert.Single(t!["segments"]!.AsArray()));
        }
        finally { TryDelete(src); }
    }

    [Fact]
    public void TextMaterial_FixedWidth_IsNegativeOne_NotElementPxWidth()
    {
        // CapCut auto-fits text to fixed_width; passing a large element pixel width
        // (e.g. 1280px Step element) inflates the rendered glyph size. We emit -1
        // so the text size is driven by font_size/text_size only.
        var src = CreateMinimalProject();
        try
        {
            var (proj, _) = BuildSampleProject();
            var result = CapCutProjectExporter.Export(proj, new CapCutExportOptions(src, CapCutExportMode.CloneProject));
            var root = JsonNode.Parse(File.ReadAllText(result.DraftContentPath))!.AsObject();
            foreach (var m in root["materials"]!["texts"]!.AsArray())
                Assert.Equal(-1.0, m!["fixed_width"]!.GetValue<double>());
        }
        finally { TryDelete(src); }
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
