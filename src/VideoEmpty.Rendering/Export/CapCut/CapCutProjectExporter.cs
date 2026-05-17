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
/// Animations: by default every emitted segment gets a CapCut "Left Slide-In"
/// entry animation (the one the user's reference project uses on every Step /
/// Comment). Disable via <see cref="CapCutExportOptions.IncludeSlideInAnimation"/>.
///
/// Idempotent re-export: every material / segment we emit is tagged with the
/// well-known marker <see cref="OriginMarker"/>. When the user re-exports onto
/// the same project, the prior VideoEmpty content is removed first
/// (controlled by <see cref="CapCutExportOptions.ReplacePreviousExport"/>),
/// so re-running does not duplicate items.
/// </summary>
public static class CapCutProjectExporter
{
    private const string DraftContentName = "draft_content.json";

    /// <summary>Marker string written into <c>group_id</c> on every segment we emit
    /// and into the custom <c>videoempty_origin</c> field on every material we emit,
    /// so a later export can recognise and replace prior content.</summary>
    public const string OriginMarker = "videoempty";

    // Resource metadata for CapCut's "Left Slide-In" entry animation (sticker_animation),
    // observed in the user's reference project. Works for both text and sticker segments.
    private const string SlideInResourceId = "7592161167426997505";
    private const string SlideInName = "Left Slide-In";
    private const string SlideInCategoryId = "ruchang";
    private const string SlideInCategoryName = "In";
    private const long SlideInDefaultUs = 500_000;   // 500 ms — matches reference (was 300 ms, too quick to notice)

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
                backupPath = MakeUniquePath(Path.Combine(workingFolder,
                    $"{DraftContentName}.videoempty.{DateTime.Now:yyyyMMdd-HHmmss-fff}.bak"));
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

        var stats = PatchDraftContent(root, project, options);

        // Atomic write: temp file + replace.
        var tmp = draftPath + ".videoempty.tmp";
        var options2 = new JsonSerializerOptions { WriteIndented = false };
        File.WriteAllText(tmp, root.ToJsonString(options2));
        // File.Replace requires the destination to exist; it does.
        File.Replace(tmp, draftPath, destinationBackupFileName: null);

        return new CapCutExportResult(
            workingFolder, draftPath,
            stats.TextMaterials, stats.ShapeMaterials, stats.Segments,
            backupPath,
            stats.PreviousSegmentsRemoved);
    }

    private record struct PatchStats(int TextMaterials, int ShapeMaterials, int Segments, int PreviousSegmentsRemoved);

    // Per-(template, element) bucket built in pass 1; tracks/segments emitted in passes 2-3.
    private sealed class SlotGroup
    {
        public string TemplateId { get; }
        public string ElementId { get; }
        public bool IsShape { get; }
        public string TemplateName { get; }
        public List<ElementSpec> Specs { get; } = new();
        public List<List<ElementSpec>> RowAssignments { get; } = new();
        public SlotGroup(string templateId, string elementId, bool isShape, string templateName)
        { TemplateId = templateId; ElementId = elementId; IsShape = isShape; TemplateName = templateName; }
    }

    private sealed record ElementSpec(
        Element Element, string? ResolvedText,
        double PxW, double PxH, double Tx, double Ty,
        long StartUs, long DurationUs);

    private static PatchStats PatchDraftContent(JsonObject root, Project project, CapCutExportOptions options)
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
        var materialAnimations = GetOrCreateArray(materials, "material_animations");

        // ----- Tracks: pick (or create) one text track and one sticker track -----
        var tracks = root["tracks"] as JsonArray ?? throw new InvalidDataException("tracks missing.");

        int removedSegments = 0;
        if (options.ReplacePreviousExport)
        {
            removedSegments = RemovePriorVideoEmptyContent(tracks, texts, shapes, materialAnimations);
        }

        // ----- Pass 1: build per-slot segment specs -----
        // A "slot" is uniquely identified by (templateId, elementId). All instances of the
        // same template using the same element slot share a CapCut row; we spill to
        // "(slot) spare N" rows when segments overlap in time.
        var slots = new Dictionary<(string templateId, string elementId), SlotGroup>();
        // Preserve a deterministic emission order matching how the user authored templates,
        // so that e.g. all "Step row 1" tracks appear above "Step row 2" tracks in CapCut.
        var slotOrder = new List<(string templateId, string elementId)>();

        // IMPORTANT: We only emit CapCut content for placed template *instances*, never
        // for template definitions. Built-in/predefined templates added to project.Templates
        // by `CreateProject` are reference data used only to look up element layout — they
        // never produce CapCut materials or segments on their own.
        foreach (var instance in project.Instances)
        {
            var template = project.Templates.FirstOrDefault(t => t.Id == instance.TemplateId);
            if (template is null) continue;

            long startUs = (long)instance.StartMs * 1000L;
            long durationUs = (long)instance.DurationMs * 1000L;

            // Apply ShapeScale uniformly to the template's geometry (template width/height,
            // element offsets, element widths/heights). This keeps the template centred on
            // the instance click point while shrinking the whole layout proportionally — so
            // shapes and text move together instead of each shrinking around its own centre
            // and producing an internally inconsistent x-position.
            double tplPxW = ScaleSize(template.Width, project.VideoResolution.Width, canvasW) * options.ShapeScale;
            double tplPxH = ScaleSize(template.Height, project.VideoResolution.Height, canvasH) * options.ShapeScale;
            double tplTopLeftX = instance.Center.X * canvasW - tplPxW / 2.0;
            double tplTopLeftY = instance.Center.Y * canvasH - tplPxH / 2.0;

            foreach (var element in template.Elements)
            {
                double elemPxW = ScaleSize(element.Width, project.VideoResolution.Width, canvasW) * options.ShapeScale;
                double elemPxH = ScaleSize(element.Height, project.VideoResolution.Height, canvasH) * options.ShapeScale;
                double elemTopLeftX = tplTopLeftX + ScaleSize(element.OffsetX, project.VideoResolution.Width, canvasW) * options.ShapeScale;
                double elemTopLeftY = tplTopLeftY + ScaleSize(element.OffsetY, project.VideoResolution.Height, canvasH) * options.ShapeScale;
                double centerPxX = elemTopLeftX + elemPxW / 2.0;
                double centerPxY = elemTopLeftY + elemPxH / 2.0;

                double tx = centerPxX / canvasW * 2.0 - 1.0;
                double ty = 1.0 - centerPxY / canvasH * 2.0;

                bool isShape = element is ShapeElement;
                string? resolvedText = (element is TextElement te) ? ResolveText(te, instance) : null;
                // Skip text elements with no user-supplied content — DefaultText is a
                // design-time placeholder for the in-app preview only; we don't bucket or
                // emit it into CapCut, otherwise an "uncustomised" instance leaks the
                // template's stand-in copy.
                if (!isShape && string.IsNullOrEmpty(resolvedText)) continue;
                var key = (template.Id, element.Id);
                if (!slots.TryGetValue(key, out var grp))
                {
                    grp = new SlotGroup(template.Id, element.Id, isShape, template.Name ?? template.Id);
                    slots[key] = grp;
                    slotOrder.Add(key);
                }
                grp.Specs.Add(new ElementSpec(element, resolvedText, elemPxW, elemPxH, tx, ty, startUs, durationUs));
            }
        }

        // ----- Pass 2: allocate tracks (shapes first, then texts so text z-sits above) -----
        // Always create FRESH tracks at the END of the tracks array (tagged with OriginMarker).
        // CapCut z-orders by tracks-array position: later = on top.
        var shapeSlotKeys = slotOrder.Where(k => slots[k].IsShape).ToList();
        var textSlotKeys  = slotOrder.Where(k => !slots[k].IsShape).ToList();

        // For each slot, greedy-assign segments to its primary row; spill to "spare" rows on overlap.
        var slotTracks = new Dictionary<(string, string), List<JsonObject>>();
        void AllocateSlot((string, string) key, string trackType)
        {
            var grp = slots[key];
            var rows = new List<List<ElementSpec>>();              // each row's specs (sorted, non-overlapping)
            var rowEndsUs = new List<long>();                       // latest end-time placed in each row

            foreach (var spec in grp.Specs.OrderBy(s => s.StartUs))
            {
                long endUs = spec.StartUs + spec.DurationUs;
                int chosen = -1;
                for (int r = 0; r < rowEndsUs.Count; r++)
                {
                    if (rowEndsUs[r] <= spec.StartUs) { chosen = r; break; }
                }
                if (chosen < 0)
                {
                    chosen = rowEndsUs.Count;
                    rows.Add(new List<ElementSpec>());
                    rowEndsUs.Add(0L);
                }
                rows[chosen].Add(spec);
                rowEndsUs[chosen] = endUs;
            }

            var emittedTracks = new List<JsonObject>();
            for (int r = 0; r < rows.Count; r++)
            {
                string name = r == 0
                    ? $"{grp.TemplateName} - {grp.ElementId}"
                    : $"{grp.TemplateName} - {grp.ElementId} spare {r}";
                var track = CreateTaggedTrack(tracks, trackType, name);
                emittedTracks.Add(track);
            }
            slotTracks[key] = emittedTracks;

            // Defer segment emission to after both shape and text tracks are created so that
            // track indices (for track_render_index) are stable.
            for (int r = 0; r < rows.Count; r++)
                slots[key].RowAssignments.Add(rows[r]);
        }

        foreach (var k in shapeSlotKeys) AllocateSlot(k, "sticker");
        foreach (var k in textSlotKeys)  AllocateSlot(k, "text");

        int baseRenderIndex = ComputeMaxRenderIndex(tracks) + 100;
        // Disjoint render_index ranges so text always wins z-order over shapes.
        int shapeRenderIdx = baseRenderIndex;
        int textRenderIdx = baseRenderIndex + 100_000;

        var stats = new PatchStats(0, 0, 0, removedSegments);

        // ----- Pass 3: emit materials + segments into their assigned tracks -----
        void EmitSlot((string, string) key)
        {
            var grp = slots[key];
            var trackList = slotTracks[key];
            for (int r = 0; r < grp.RowAssignments.Count; r++)
            {
                var track = trackList[r];
                int trackIdx = tracks.IndexOf(track);
                foreach (var spec in grp.RowAssignments[r])
                {
                    string? animId = options.IncludeSlideInAnimation
                        ? AppendSlideInAnimation(materialAnimations, spec.DurationUs)
                        : null;
                    string materialId;
                    int ri;
                    if (spec.Element is ShapeElement shape)
                    {
                        (materialId, _) = AppendShapeMaterial(shapes, shape, spec.PxW, spec.PxH, options.ShapeScale);
                        ri = shapeRenderIdx++;
                        stats = stats with { ShapeMaterials = stats.ShapeMaterials + 1 };
                    }
                    else
                    {
                        var textEl = (TextElement)spec.Element;
                        // Skip empty resolved text — keeps the CapCut timeline clean and prevents
                        // template DefaultText from leaking when an instance has no custom text.
                        if (string.IsNullOrEmpty(spec.ResolvedText))
                            continue;
                        (materialId, _) = AppendTextMaterial(texts, textEl, spec.ResolvedText, spec.PxW, spec.PxH, options.ShapeScale, options.FontScale);
                        ri = textRenderIdx++;
                        stats = stats with { TextMaterials = stats.TextMaterials + 1 };
                    }
                    var seg = BuildSegment(materialId, spec.StartUs, spec.DurationUs, spec.Tx, spec.Ty, ri, trackIdx, animId);
                    track["segments"]!.AsArray().Add(seg);
                    stats = stats with { Segments = stats.Segments + 1 };
                }
            }
        }

        foreach (var k in shapeSlotKeys) EmitSlot(k);
        foreach (var k in textSlotKeys)  EmitSlot(k);

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
        // IMPORTANT: do NOT fall back to element.DefaultText here. DefaultText is a
        // design-time placeholder used for the in-app preview only; emitting it into
        // CapCut would leak the template's stand-in copy (e.g. "Setup GitHub Repository")
        // as if it were real content for any instance the user hasn't customised.
        if (instance.TextValues != null && instance.TextValues.TryGetValue(element.Id, out var v) && v is not null)
            return v;
        return string.Empty;
    }

    private static double ScaleSize(int value, int fromBasis, int toBasis)
    {
        if (fromBasis <= 0 || toBasis <= 0) return value;
        return value * (double)toBasis / fromBasis;
    }

    // ---------- Material builders ----------

    private static (string id, JsonObject node) AppendShapeMaterial(JsonArray arr, ShapeElement shape, double pxW, double pxH, double shapeScale)
    {
        var id = NewCapCutGuid();
        // NOTE: pxW/pxH are already scaled by ShapeScale in Pass 1 (so shape geometry stays
        // consistent with element offsets). We only need to apply shapeScale here to the
        // border stroke width, which is a thickness independent of the shape's footprint.
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
            ["border_width"] = (double)Math.Max(0, shape.BorderThickness) * shapeScale,
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
            ["videoempty_origin"] = OriginMarker,
        };
        arr.Add(mat);
        return (id, mat);
    }

    private static (string id, JsonObject node) AppendTextMaterial(
        JsonArray arr, TextElement text, string resolvedText, double pxW, double pxH, double shapeScale, double fontScale)
    {
        var id = NewCapCutGuid();
        // NOTE: pxW/pxH are already scaled by ShapeScale in Pass 1 — they reflect the
        // shrunken text wrap-box footprint. shapeScale is kept in the signature only for
        // symmetry; the font_size / style.size are independently scaled by fontScale.
        _ = shapeScale;
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
        // CapCut sizing model (observed across reference projects):
        //   * `text_size` is a fixed canvas-unit value (the reference uses 30 for every text).
        //   * Actual glyph height is driven almost entirely by `font_size` and `style.size`.
        //   * Empirically, a `font_size` of ~6-8 produces a normal video title; values above
        //     ~10 quickly render very large. We pick FontSize / 10 so a design-time FontSize of
        //     ~70 px maps to a CapCut font_size of ~7, matching reference titles in scale.
        double fontSize = Math.Max(1.0, text.FontSize / 10.0) * fontScale;
        double styleSize = fontSize;
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
            ["font_size"] = fontSize,
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
            ["text_size"] = 30,
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
            // CapCut treats fixed_width as a layout/wrap box, not a literal glyph scale,
            // AS LONG AS we pass a positive value. With fixed_width=-1, CapCut silently
            // forces center alignment (verified across reference projects: every text with
            // fixed_width=-1 has alignment=1, and every alignment=0 text has fixed_width>0).
            // So we emit the element's pixel text-box width — glyph size is still driven by
            // font_size (FontSize/10) and the box only governs wrap + horizontal anchor.
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
            ["videoempty_origin"] = OriginMarker,
        };
        arr.Add(mat);
        return (id, mat);
    }

    // ---------- Animation builder ----------

    /// <summary>Appends a CapCut "Left Slide-In" sticker_animation material and returns its id.
    /// Works for both text and sticker segments (as in the reference project).</summary>
    private static string AppendSlideInAnimation(JsonArray arr, long segmentDurationUs)
    {
        var id = NewCapCutGuid();
        // Cap the animation to the segment length so short segments still play sensibly.
        long animUs = segmentDurationUs > 0 ? Math.Min(SlideInDefaultUs, segmentDurationUs) : SlideInDefaultUs;
        var entry = new JsonObject
        {
            ["id"] = SlideInResourceId,
            ["type"] = "in",
            ["start"] = 0L,
            ["duration"] = animUs,
            ["path"] = "",
            ["platform"] = "all",
            ["resource_id"] = SlideInResourceId,
            ["third_resource_id"] = "0",
            ["source_platform"] = 1,
            ["name"] = SlideInName,
            ["category_id"] = SlideInCategoryId,
            ["category_name"] = SlideInCategoryName,
            ["panel"] = "",
            ["material_type"] = "sticker",
            ["anim_adjust_params"] = null,
            ["request_id"] = "",
        };
        var mat = new JsonObject
        {
            ["id"] = id,
            ["type"] = "sticker_animation",
            ["animations"] = new JsonArray(entry),
            ["multi_language_current"] = "none",
            ["videoempty_origin"] = OriginMarker,
        };
        arr.Add(mat);
        return id;
    }

    // ---------- Re-export cleanup ----------

    /// <summary>Removes every segment whose <c>group_id</c> equals <see cref="OriginMarker"/>,
    /// every material whose <c>videoempty_origin</c> equals <see cref="OriginMarker"/>, and
    /// every track tagged with <c>videoempty_origin = OriginMarker</c>.
    /// Returns the number of segments removed.</summary>
    private static int RemovePriorVideoEmptyContent(
        JsonArray tracks, JsonArray texts, JsonArray shapes, JsonArray materialAnimations)
    {
        int segmentsRemoved = 0;

        // First pass: drop entire tracks we previously created (carrying our marker).
        for (int t = tracks.Count - 1; t >= 0; t--)
        {
            if (tracks[t] is JsonObject track &&
                track["videoempty_origin"]?.GetValue<string>() == OriginMarker)
            {
                if (track["segments"] is JsonArray segs) segmentsRemoved += segs.Count;
                tracks.RemoveAt(t);
            }
        }

        // Second pass: drop individual tagged segments from any user-owned tracks
        // (e.g. content emitted by an older exporter that didn't yet tag tracks).
        foreach (var trackNode in tracks)
        {
            if (trackNode is not JsonObject track) continue;
            if (track["segments"] is not JsonArray segs) continue;

            for (int i = segs.Count - 1; i >= 0; i--)
            {
                if (segs[i] is JsonObject s &&
                    s["group_id"]?.GetValue<string>() == OriginMarker)
                {
                    segs.RemoveAt(i);
                    segmentsRemoved++;
                }
            }
        }

        RemoveTaggedMaterials(texts);
        RemoveTaggedMaterials(shapes);
        RemoveTaggedMaterials(materialAnimations);

        return segmentsRemoved;
    }

    private static void RemoveTaggedMaterials(JsonArray arr)
    {
        for (int i = arr.Count - 1; i >= 0; i--)
        {
            if (arr[i] is JsonObject m &&
                m["videoempty_origin"]?.GetValue<string>() == OriginMarker)
            {
                arr.RemoveAt(i);
            }
        }
    }

    // ---------- Segment builder ----------

    private static JsonObject BuildSegment(string materialId, long startUs, long durationUs, double tx, double ty, int renderIndex, int trackRenderIndex, string? animationId = null)
    {
        var extraRefs = new JsonArray();
        if (!string.IsNullOrEmpty(animationId))
            extraRefs.Add(JsonValue.Create(animationId));

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
            ["extra_material_refs"] = extraRefs,
            ["render_index"] = renderIndex,
            ["keyframe_refs"] = new JsonArray(),
            ["enable_lut"] = false,
            ["enable_adjust"] = false,
            ["enable_hsl"] = false,
            ["visible"] = true,
            ["group_id"] = OriginMarker,
            ["enable_color_curves"] = true,
            ["enable_hsl_curves"] = true,
            ["track_render_index"] = trackRenderIndex,
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

    private static string MakeUniquePath(string desired)
    {
        if (!File.Exists(desired)) return desired;
        var dir = Path.GetDirectoryName(desired)!;
        var name = Path.GetFileNameWithoutExtension(desired);
        var ext = Path.GetExtension(desired);
        for (int i = 1; i < 1000; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(dir, $"{name}-{Guid.NewGuid():n}{ext}");
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

    /// <summary>Always appends a fresh track of <paramref name="type"/> at the end of
    /// <paramref name="tracks"/>. Tagged with <see cref="OriginMarker"/> so it can be
    /// removed on re-export. Appending at the end keeps our content visually on top
    /// in CapCut (tracks later in the array stack above earlier ones).</summary>
    private static JsonObject CreateTaggedTrack(JsonArray tracks, string type, string? name = null)
    {
        var track = new JsonObject
        {
            ["id"] = NewCapCutGuid(),
            ["type"] = type,
            ["attribute"] = 0,
            ["flag"] = 0,
            ["is_default_name"] = string.IsNullOrEmpty(name),
            ["name"] = name ?? "",
            ["segments"] = new JsonArray(),
            ["videoempty_origin"] = OriginMarker,
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
