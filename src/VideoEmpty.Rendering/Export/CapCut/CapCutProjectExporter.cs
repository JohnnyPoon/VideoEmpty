using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using VideoEmpty.Core.Api;
using VideoEmpty.Core.Model;

namespace VideoEmpty.Rendering.Export.CapCut;

/// <summary>
/// Exports our template instances into an existing CapCut PC project by
/// appending text/shape materials and corresponding track segments to
/// <c>draft_content.json</c>.
///
/// Safety: by default the entire project folder is cloned to a sibling folder
/// and the clone is edited, so the original project is never touched.
/// Optional <see cref="CapCutExportMode.EditInPlace"/> writes a timestamped
/// <c>.bak</c> next to the original first.
///
/// Animations are intentionally NOT translated to CapCut animations in v1;
/// each instance simply appears for its full duration.
/// </summary>
public static class CapCutProjectExporter
{
    private const string DraftContentName = "draft_content.json";

    public static CapCutExportResult Export(Project project, CapCutExportOptions options)
    {
        if (project is null) throw new ArgumentNullException(nameof(project));
        if (options is null) throw new ArgumentNullException(nameof(options));
        if (!Directory.Exists(options.ProjectFolder))
            throw new DirectoryNotFoundException($"CapCut project folder not found: {options.ProjectFolder}");

        var originalDraft = Path.Combine(options.ProjectFolder, DraftContentName);
        if (!File.Exists(originalDraft))
            throw new FileNotFoundException(
                $"'{DraftContentName}' not found in '{options.ProjectFolder}'. " +
                "Is this really a CapCut project folder?", originalDraft);

        string workingFolder;
        string? backupPath = null;
        switch (options.Mode)
        {
            case CapCutExportMode.CloneProject:
            {
                var parent = Path.GetDirectoryName(options.ProjectFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                             ?? throw new InvalidOperationException("Cannot determine parent of project folder.");
                var leaf = Path.GetFileName(options.ProjectFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                var suffix = options.CloneSuffix ?? $"VideoEmpty {DateTime.Now:yyyy-MM-dd HH-mm-ss}";
                workingFolder = Path.Combine(parent, $"{leaf} ({suffix})");
                CopyDirectoryRecursive(options.ProjectFolder, workingFolder);
                break;
            }
            case CapCutExportMode.EditInPlace:
            {
                workingFolder = options.ProjectFolder;
                backupPath = Path.Combine(workingFolder,
                    $"{DraftContentName}.videoempty.{DateTime.Now:yyyyMMdd-HHmmss}.bak");
                File.Copy(originalDraft, backupPath, overwrite: false);
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(options.Mode));
        }

        var draftPath = Path.Combine(workingFolder, DraftContentName);
        var json = File.ReadAllText(draftPath);
        var root = JsonNode.Parse(json) as JsonObject
                   ?? throw new InvalidDataException("draft_content.json root is not an object.");

        var stats = PatchDraftContent(root, project);

        // Atomic write: temp file + replace.
        var tmp = draftPath + ".videoempty.tmp";
        var options2 = new JsonSerializerOptions { WriteIndented = false };
        File.WriteAllText(tmp, root.ToJsonString(options2));
        // File.Replace requires the destination to exist; it does.
        File.Replace(tmp, draftPath, destinationBackupFileName: null);

        return new CapCutExportResult(
            workingFolder, draftPath,
            stats.TextMaterials, stats.ShapeMaterials, stats.Segments,
            backupPath);
    }

    private record struct PatchStats(int TextMaterials, int ShapeMaterials, int Segments);

    private static PatchStats PatchDraftContent(JsonObject root, Project project)
    {
        // ----- Canvas -----
        var canvas = root["canvas_config"] as JsonObject;
        int canvasW = canvas?["width"]?.GetValue<int>() ?? project.VideoResolution.Width;
        int canvasH = canvas?["height"]?.GetValue<int>() ?? project.VideoResolution.Height;
        if (canvasW <= 0) canvasW = 1920;
        if (canvasH <= 0) canvasH = 1080;

        // ----- Materials section -----
        var materials = root["materials"] as JsonObject ?? throw new InvalidDataException("materials missing.");
        var texts = GetOrCreateArray(materials, "texts");
        var shapes = GetOrCreateArray(materials, "shapes");

        // ----- Tracks: pick (or create) one text track and one sticker track -----
        var tracks = root["tracks"] as JsonArray ?? throw new InvalidDataException("tracks missing.");
        var textTrack = FindOrCreateTrack(tracks, "text");
        var stickerTrack = FindOrCreateTrack(tracks, "sticker");

        int baseRenderIndex = ComputeMaxRenderIndex(tracks) + 100;
        int renderIdx = baseRenderIndex;

        var stats = new PatchStats(0, 0, 0);

        foreach (var instance in project.Instances)
        {
            var template = project.Templates.FirstOrDefault(t => t.Id == instance.TemplateId);
            if (template is null) continue;

            long startUs = (long)instance.StartMs * 1000L;
            long durationUs = (long)instance.DurationMs * 1000L;

            // Top-left of the template box, in canvas pixels.
            // Center is the (normalized) midpoint of the template box on the video.
            double tplPxW = ScaleSize(template.Width, project.VideoResolution.Width, canvasW);
            double tplPxH = ScaleSize(template.Height, project.VideoResolution.Height, canvasH);
            double tplTopLeftX = instance.Center.X * canvasW - tplPxW / 2.0;
            double tplTopLeftY = instance.Center.Y * canvasH - tplPxH / 2.0;

            foreach (var element in template.Elements)
            {
                double elemPxW = ScaleSize(element.Width, project.VideoResolution.Width, canvasW);
                double elemPxH = ScaleSize(element.Height, project.VideoResolution.Height, canvasH);
                double elemTopLeftX = tplTopLeftX + ScaleSize(element.OffsetX, project.VideoResolution.Width, canvasW);
                double elemTopLeftY = tplTopLeftY + ScaleSize(element.OffsetY, project.VideoResolution.Height, canvasH);

                double centerPxX = elemTopLeftX + elemPxW / 2.0;
                double centerPxY = elemTopLeftY + elemPxH / 2.0;

                // CapCut clip.transform is normalized [-1..1] of half-canvas with y inverted.
                double tx = centerPxX / canvasW * 2.0 - 1.0;
                double ty = 1.0 - centerPxY / canvasH * 2.0;

                renderIdx++;

                if (element is ShapeElement shape)
                {
                    var (materialId, _) = AppendShapeMaterial(shapes, shape, elemPxW, elemPxH);
                    var seg = BuildSegment(materialId, startUs, durationUs, tx, ty, renderIdx);
                    stickerTrack["segments"]!.AsArray().Add(seg);
                    stats = stats with { ShapeMaterials = stats.ShapeMaterials + 1, Segments = stats.Segments + 1 };
                }
                else if (element is TextElement textEl)
                {
                    var resolvedText = ResolveText(textEl, instance);
                    var (materialId, _) = AppendTextMaterial(texts, textEl, resolvedText, elemPxW, elemPxH);
                    var seg = BuildSegment(materialId, startUs, durationUs, tx, ty, renderIdx);
                    textTrack["segments"]!.AsArray().Add(seg);
                    stats = stats with { TextMaterials = stats.TextMaterials + 1, Segments = stats.Segments + 1 };
                }
            }
        }

        // Extend project duration if needed.
        long requiredUs = project.Instances.Count == 0
            ? 0
            : project.Instances.Max(i => (long)(i.StartMs + i.DurationMs)) * 1000L;
        if (root["duration"] is JsonValue dv && dv.TryGetValue(out long curUs) && requiredUs > curUs)
        {
            root["duration"] = requiredUs;
        }
        root["update_time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        return stats;
    }

    private static string ResolveText(TextElement element, TemplateInstance instance)
    {
        if (instance.TextValues != null && instance.TextValues.TryGetValue(element.Id, out var v) && v is not null)
            return v;
        return element.DefaultText ?? string.Empty;
    }

    private static double ScaleSize(int value, int fromBasis, int toBasis)
    {
        if (fromBasis <= 0 || toBasis <= 0) return value;
        return value * (double)toBasis / fromBasis;
    }

    // ---------- Material builders ----------

    private static (string id, JsonObject node) AppendShapeMaterial(JsonArray arr, ShapeElement shape, double pxW, double pxH)
    {
        var id = NewCapCutGuid();
        double halfW = pxW / 2.0, halfH = pxH / 2.0;
        var fillHex = ToHexRgb(shape.Fill);
        var borderHex = ToHexRgb(shape.BorderColor);

        var mat = new JsonObject
        {
            ["id"] = id,
            ["type"] = "shape",
            ["shape_type"] = shape.Shape == ShapeKind.Ellipse ? 1 : 4,
            ["shape_size"] = new JsonArray(JsonValue.Create(pxW), JsonValue.Create(pxH)),
            ["custom_points"] = new JsonArray(
                JsonValue.Create(-halfW), JsonValue.Create( halfH),
                JsonValue.Create( halfW), JsonValue.Create( halfH),
                JsonValue.Create( halfW), JsonValue.Create(-halfH),
                JsonValue.Create(-halfW), JsonValue.Create(-halfH)),
            ["global_alpha"] = 1.0,
            ["color"] = "",
            ["border_line_style"] = 0,
            ["border_width"] = (double)Math.Max(0, shape.BorderThickness),
            ["border_color"] = borderHex,
            ["border_alpha"] = 1.0,
            ["shadow_color"] = "#000000",
            ["shadow_alpha"] = 0.0,
            ["shadow_distance"] = 0.0,
            ["shadow_angle"] = 0.0,
            ["shadow_ambiguity"] = 0.0,
            ["check_flag"] = 49,
            ["name"] = "rect_item",
            ["combo_info"] = new JsonObject { ["text_templates"] = new JsonArray() },
            ["shape_scale"] = new JsonArray(),
            ["custom_points_in"] = new JsonArray(0.0,0.0,0.0,0.0,0.0,0.0,0.0,0.0),
            ["custom_points_out"] = new JsonArray(0.0,0.0,0.0,0.0,0.0,0.0,0.0,0.0),
            ["endpoint_left_style"] = 0,
            ["endpoint_right_style"] = 0,
            ["line_style"] = 0,
            ["fill_render_style"] = new JsonObject
            {
                ["color"] = new JsonObject
                {
                    ["solid"] = new JsonObject
                    {
                        ["color"] = fillHex,
                        ["alpha"] = shape.Fill.A / 255.0,
                    },
                    ["render_type"] = "solid",
                },
                ["alpha"] = 1.0,
            },
            ["constant_material_id"] = "",
        };
        arr.Add(mat);
        return (id, mat);
    }

    private static (string id, JsonObject node) AppendTextMaterial(
        JsonArray arr, TextElement text, string resolvedText, double pxW, double pxH)
    {
        var id = NewCapCutGuid();
        var colorHex = ToHexRgb(text.TextColor);
        var rgbArr = new JsonArray(
            JsonValue.Create(text.TextColor.R / 255.0),
            JsonValue.Create(text.TextColor.G / 255.0),
            JsonValue.Create(text.TextColor.B / 255.0));
        int alignment = text.HAlign switch
        {
            HorizontalAlign.Left => 0,
            HorizontalAlign.Center => 1,
            HorizontalAlign.Right => 2,
            _ => 0,
        };
        // CapCut "size" field in content.styles is roughly font_size / 5 (observed 30 -> 6).
        double styleSize = Math.Max(1.0, text.FontSize / 5.0);
        var charCount = string.IsNullOrEmpty(resolvedText) ? 0 : resolvedText.Length;

        var contentObj = new JsonObject
        {
            ["text"] = resolvedText,
            ["styles"] = new JsonArray(new JsonObject
            {
                ["fill"] = new JsonObject
                {
                    ["content"] = new JsonObject
                    {
                        ["render_type"] = "solid",
                        ["solid"] = new JsonObject
                        {
                            ["color"] = rgbArr.DeepClone(),
                        },
                    },
                },
                ["font"] = new JsonObject
                {
                    ["path"] = "",
                    ["id"] = "",
                },
                ["size"] = styleSize,
                ["useLetterColor"] = true,
                ["range"] = new JsonArray(JsonValue.Create(0), JsonValue.Create(charCount)),
            }),
        };
        // CapCut stores the styled content as a JSON-encoded string inside `content`.
        var contentString = contentObj.ToJsonString();

        var mat = new JsonObject
        {
            ["recognize_task_id"] = "",
            ["id"] = id,
            ["name"] = "",
            ["recognize_text"] = "",
            ["recognize_model"] = "",
            ["punc_model"] = "",
            ["type"] = "text",
            ["content"] = contentString,
            ["base_content"] = "",
            ["words"] = new JsonObject
            {
                ["start_time"] = new JsonArray(),
                ["end_time"] = new JsonArray(),
                ["text"] = new JsonArray(),
            },
            ["current_words"] = new JsonObject
            {
                ["start_time"] = new JsonArray(),
                ["end_time"] = new JsonArray(),
                ["text"] = new JsonArray(),
            },
            ["global_alpha"] = 1.0,
            ["combo_info"] = new JsonObject { ["text_templates"] = new JsonArray() },
            ["caption_template_info"] = new JsonObject
            {
                ["resource_id"] = "", ["third_resource_id"] = "", ["resource_name"] = "",
                ["category_id"] = "", ["category_name"] = "", ["effect_id"] = "",
                ["request_id"] = "", ["path"] = "", ["is_new"] = false, ["source_platform"] = 0,
            },
            ["layer_weight"] = 1,
            ["letter_spacing"] = 0.0,
            ["text_curve"] = null,
            ["text_loop_on_path"] = false,
            ["offset_on_path"] = 0.0,
            ["enable_path_typesetting"] = false,
            ["text_exceeds_path_process_type"] = 0,
            ["text_typesetting_paths"] = null,
            ["text_typesetting_paths_file"] = "",
            ["text_typesetting_path_index"] = 0,
            ["line_spacing"] = Math.Max(0, text.LineSpacing) / 100.0,
            ["has_shadow"] = false,
            ["shadow_color"] = "",
            ["shadow_alpha"] = 0.9,
            ["shadow_smoothing"] = 0.45,
            ["shadow_distance"] = 5.0,
            ["shadow_point"] = new JsonObject { ["x"] = 0.636, ["y"] = -0.636 },
            ["shadow_angle"] = -45.0,
            ["shadow_thickness_projection_enable"] = false,
            ["shadow_thickness_projection_angle"] = 0.0,
            ["shadow_thickness_projection_distance"] = 0.0,
            ["border_alpha"] = 1.0,
            ["border_color"] = "#000000",
            ["border_width"] = 0.08,
            ["border_mode"] = 0,
            ["style_name"] = "",
            ["text_color"] = colorHex,
            ["text_alpha"] = text.TextColor.A / 255.0,
            ["font_name"] = "",
            ["font_title"] = "none",
            ["font_size"] = (double)text.FontSize,
            ["font_path"] = "",
            ["font_id"] = "",
            ["font_resource_id"] = "",
            ["initial_scale"] = 1.0,
            ["font_url"] = "",
            ["typesetting"] = 0,
            ["alignment"] = alignment,
            ["line_feed"] = 1,
            ["use_effect_default_color"] = false,
            ["is_rich_text"] = false,
            ["shape_clip_x"] = false,
            ["shape_clip_y"] = false,
            ["ktv_color"] = "",
            ["text_to_audio_ids"] = new JsonArray(),
            ["bold_width"] = text.Bold ? 0.06 : 0.0,
            ["italic_degree"] = text.Italic ? 1 : 0,
            ["underline"] = false,
            ["underline_width"] = 0.05,
            ["underline_offset"] = 0.22,
            ["sub_type"] = 0,
            ["check_flag"] = 15,
            ["text_size"] = text.FontSize,
            ["font_category_name"] = "",
            ["font_source_platform"] = 0,
            ["font_third_resource_id"] = "",
            ["font_category_id"] = "",
            ["add_type"] = 0,
            ["operation_type"] = 0,
            ["recognize_type"] = 0,
            ["fonts"] = new JsonArray(),
            ["background_color"] = "#000000",
            ["background_alpha"] = 0.0,
            ["background_style"] = 0,
            ["background_round_radius"] = 0.0,
            ["background_width"] = 0.14,
            ["background_height"] = 0.14,
            ["background_vertical_offset"] = 0.0,
            ["background_horizontal_offset"] = 0.0,
            ["background_fill"] = "",
            ["single_char_bg_enable"] = false,
            ["single_char_bg_color"] = "",
            ["single_char_bg_alpha"] = 1.0,
            ["single_char_bg_round_radius"] = 0.3,
            ["single_char_bg_width"] = 0.0,
            ["single_char_bg_height"] = 0.0,
            ["single_char_bg_vertical_offset"] = 0.0,
            ["single_char_bg_horizontal_offset"] = 0.0,
            ["font_team_id"] = "",
            ["tts_auto_update"] = false,
            ["text_preset_resource_id"] = "",
            ["group_id"] = "",
            ["preset_id"] = "",
            ["preset_name"] = "",
            ["preset_category"] = "",
            ["preset_category_id"] = "",
            ["preset_index"] = 0,
            ["preset_has_set_alignment"] = false,
            ["force_apply_line_max_width"] = false,
            ["language"] = "",
            ["relevance_segment"] = new JsonArray(),
            ["original_size"] = new JsonArray(),
            ["fixed_width"] = pxW,
            ["fixed_height"] = -1.0,
            ["line_max_width"] = 0.82,
            ["oneline_cutoff"] = false,
            ["cutoff_postfix"] = "",
            ["subtitle_template_original_fontsize"] = 0.0,
            ["subtitle_keywords"] = null,
            ["inner_padding"] = -1.0,
            ["multi_language_current"] = "none",
            ["source_from"] = "",
            ["is_lyric_effect"] = false,
            ["lyric_group_id"] = "",
            ["lyrics_template"] = new JsonObject
            {
                ["resource_id"] = "", ["resource_name"] = "", ["panel"] = "",
                ["effect_id"] = "", ["path"] = "",
                ["category_id"] = "", ["category_name"] = "", ["request_id"] = "",
            },
            ["is_batch_replace"] = false,
            ["is_words_linear"] = false,
            ["ssml_content"] = "",
            ["subtitle_keywords_config"] = null,
            ["sub_template_id"] = -1,
            ["translate_original_text"] = "",
        };
        arr.Add(mat);
        return (id, mat);
    }

    // ---------- Segment builder ----------

    private static JsonObject BuildSegment(string materialId, long startUs, long durationUs, double tx, double ty, int renderIndex)
    {
        return new JsonObject
        {
            ["id"] = NewCapCutGuid(),
            ["source_timerange"] = null,
            ["target_timerange"] = new JsonObject
            {
                ["start"] = startUs,
                ["duration"] = Math.Max(1L, durationUs),
            },
            ["render_timerange"] = new JsonObject { ["start"] = 0L, ["duration"] = 0L },
            ["desc"] = "",
            ["state"] = 0,
            ["speed"] = 1.0,
            ["is_loop"] = false,
            ["is_tone_modify"] = false,
            ["reverse"] = false,
            ["intensifies_audio"] = false,
            ["cartoon"] = false,
            ["volume"] = 1.0,
            ["last_nonzero_volume"] = 1.0,
            ["clip"] = new JsonObject
            {
                ["scale"] = new JsonObject { ["x"] = 1.0, ["y"] = 1.0 },
                ["rotation"] = 0.0,
                ["transform"] = new JsonObject { ["x"] = tx, ["y"] = ty },
                ["flip"] = new JsonObject { ["vertical"] = false, ["horizontal"] = false },
                ["alpha"] = 1.0,
            },
            ["uniform_scale"] = new JsonObject { ["on"] = true, ["value"] = 1.0 },
            ["material_id"] = materialId,
            ["extra_material_refs"] = new JsonArray(),
            ["render_index"] = renderIndex,
            ["keyframe_refs"] = new JsonArray(),
            ["enable_lut"] = false,
            ["enable_adjust"] = false,
            ["enable_hsl"] = false,
            ["visible"] = true,
            ["group_id"] = "",
            ["enable_color_curves"] = true,
            ["enable_hsl_curves"] = true,
            ["track_render_index"] = 1,
            ["hdr_settings"] = null,
            ["enable_color_wheels"] = true,
            ["track_attribute"] = 0,
            ["is_placeholder"] = false,
            ["template_id"] = "",
            ["enable_smart_color_adjust"] = false,
            ["template_scene"] = "default",
            ["common_keyframes"] = new JsonArray(),
            ["caption_info"] = null,
            ["responsive_layout"] = new JsonObject
            {
                ["enable"] = false,
                ["target_follow"] = "",
                ["size_layout"] = 0,
                ["horizontal_pos_layout"] = 0,
                ["vertical_pos_layout"] = 0,
            },
            ["enable_color_match_adjust"] = false,
            ["enable_color_correct_adjust"] = false,
            ["enable_adjust_mask"] = false,
            ["raw_segment_id"] = "",
            ["lyric_keyframes"] = null,
            ["enable_video_mask"] = true,
            ["digital_human_template_group_id"] = "",
            ["color_correct_alg_result"] = "",
            ["source"] = "segmentsourcenormal",
            ["enable_mask_stroke"] = false,
            ["enable_mask_shadow"] = false,
            ["enable_color_adjust_pro"] = false,
        };
    }

    // ---------- Helpers ----------

    private static JsonArray GetOrCreateArray(JsonObject obj, string key)
    {
        if (obj[key] is JsonArray a) return a;
        var arr = new JsonArray();
        obj[key] = arr;
        return arr;
    }

    private static JsonObject FindOrCreateTrack(JsonArray tracks, string type)
    {
        foreach (var node in tracks)
        {
            if (node is JsonObject t && string.Equals(t["type"]?.GetValue<string>(), type, StringComparison.Ordinal))
                return t;
        }
        var track = new JsonObject
        {
            ["id"] = NewCapCutGuid(),
            ["type"] = type,
            ["attribute"] = 0,
            ["flag"] = 0,
            ["is_default_name"] = true,
            ["name"] = "",
            ["segments"] = new JsonArray(),
        };
        tracks.Add(track);
        return track;
    }

    private static int ComputeMaxRenderIndex(JsonArray tracks)
    {
        int max = 0;
        foreach (var t in tracks)
        {
            if (t is not JsonObject obj) continue;
            if (obj["segments"] is not JsonArray segs) continue;
            foreach (var s in segs)
            {
                if (s is JsonObject so && so["render_index"] is JsonValue rv && rv.TryGetValue(out int ri))
                    if (ri > max) max = ri;
            }
        }
        return max;
    }

    private static string ToHexRgb(Color c) =>
        $"#{c.R:X2}{c.G:X2}{c.B:X2}".ToLowerInvariant();

    private static string NewCapCutGuid() =>
        Guid.NewGuid().ToString("D").ToUpperInvariant();

    private static void CopyDirectoryRecursive(string source, string dest)
    {
        if (Directory.Exists(dest))
            throw new IOException($"Destination already exists: {dest}");
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, dir);
            Directory.CreateDirectory(Path.Combine(dest, rel));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, file);
            File.Copy(file, Path.Combine(dest, rel), overwrite: false);
        }
    }
}
