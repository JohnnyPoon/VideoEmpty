# Feature: Export template instances to CapCut project format

## Feasibility verdict: **Yes, very feasible.**

The CapCut PC project format is straightforward JSON. The reference project
`I Built & Deployed a Website Using AI ...` proves the round-trip: 342 text
materials + 179 shape materials are all stored as plain JSON in
`draft_content.json` and the references between tracks and materials are clean.

## What I learned from the references

### File layout of a CapCut project folder
```
<project>/
  draft_content.json        ← ~4.5 MB JSON, the actual timeline (we modify this)
  draft_content.json.bak    ← CapCut itself keeps a .bak — so the pattern is allowed
  draft_meta_info.json      ← small metadata, may need duration/update_time bumped
  draft_cover.jpg, etc.     ← cover, plus per-feature side folders we don't touch
```

### `draft_content.json` shape (relevant parts)
```jsonc
{
  "id": "<project guid>",
  "duration": <microseconds>,            // need to extend if our last instance
                                          // goes past the existing end
  "canvas_config": { "width":1920, "height":1080, "ratio":"16:9" },
  "fps": 30,
  "materials": {
    "texts":  [ <textMaterial>,  ... ],   // one per visible text run
    "shapes": [ <shapeMaterial>, ... ],   // one per visible shape
    "material_animations": [ ... ],       // we will mostly leave empty / copy
    ...50+ other arrays we leave untouched
  },
  "tracks": [
    { "type":"video", "segments":[ ... ] },  // existing video tracks — untouched
    { "type":"text",  "segments":[ <segment>, ... ] },
    { "type":"sticker","segments":[ <segment>, ... ] }   // shapes ride here
  ]
}
```

### Text material (representative fields, abbreviated)
```jsonc
{
  "id": "B0C1D002-…",          // GUID, referenced by a segment's material_id
  "type": "text",
  "content": "{\"text\":\"…\",\"styles\":[{\"fill\":{\"content\":{\"render_type\":\"solid\",\"solid\":{\"color\":[r,g,b]}}},\"font\":{\"path\":\"…\"},\"size\":6,\"range\":[0,N]}]}",
  "text_color": "#000000",
  "text_size": 30,
  "font_path": "C:/Users/…/CapCut/.../Font/SystemFont/en.ttf",
  "border_color":"#000000","border_width":0.08,"border_alpha":1.0,
  "background_color":"#000000","background_alpha":1.0,
  "alignment":0,                  // 0=left,1=center,2=right
  "letter_spacing":0,"line_spacing":0.02,
  "fixed_width": 141.05, "fixed_height": -1, "line_max_width": 0.82,
  // + ~80 other fields, all safely defaultable
}
```

### Shape material (rectangle)
```jsonc
{
  "id": "7D902B4D-…",
  "type": "shape",
  "shape_type": 4,                            // 4 = rectangle
  "shape_size": [539.86, 49.50],              // in canvas px
  "custom_points": [-w/2,h/2, w/2,h/2, w/2,-h/2, -w/2,-h/2],
  "fill_render_style": {
    "color": { "solid": { "color":"#fefdfe","alpha":1.0 }, … },
    "render_type":"solid"
  },
  "border_color":"#000000","border_width":2.0,"border_alpha":1.0,
  "shadow_color":"#000000","shadow_alpha":0.5,"shadow_distance":10,"shadow_angle":45,
  "name":"rect_item"
}
```

### Segment (lives in a `tracks[].segments[]`)
```jsonc
{
  "id":"<segment guid>",
  "material_id":"<id of text or shape material>",
  "extra_material_refs":["<animation material id or empty>"],
  "target_timerange":{"start": <µs>, "duration": <µs>},   // microseconds!
  "clip":{
    "scale":{"x":1,"y":1},
    "rotation":0,
    "transform":{"x": -1..1, "y": -1..1},     // normalized canvas coords, center=0
    "alpha":1
  },
  "render_index": <int>,                       // z-order, increases for newer
  "track_render_index": <int>,
  "visible":true
}
```

### Key unit conversions
| Our model                      | CapCut                                       |
|--------------------------------|----------------------------------------------|
| `StartMs` / `DurationMs` (ms)  | `target_timerange.{start,duration}` × 1000 (µs) |
| `Center` (0..1 normalized)     | `clip.transform.{x,y}` mapped from `[0..1]` to `[-1..1]` (`tx = 2*nx - 1`, `ty = 1 - 2*ny`; y is inverted) |
| `Template.{Width,Height}` (px) | shape `shape_size` in canvas pixels; text `fixed_width` similar |
| Element `OffsetX/Y` (template-local px) | folded into per-element `clip.transform` per material (CapCut has no grouping — see below) |
| RGB color                      | hex `#rrggbb` (text/shape) or `[r,g,b]` 0..1 (text content.styles.fill) |

### The grouping problem (and how we solve it)
CapCut has no element groups, which is exactly why this feature exists. Each
of our template-instance's elements (1 shape + 1..N text boxes) becomes its
own CapCut material + segment, all sharing the same `target_timerange`. We
position each element by combining the instance's center + the element's
template-local offset, then converting to CapCut's normalized
`clip.transform` system.

## Implementation plan

### Phase 1 — Safety (do first, can ship alone)
1. **Backup strategy** before *any* write:
   - Prompt user to pick the CapCut project folder.
   - Default mode: **clone** the project folder to a sibling folder named
     `<original> (VideoEmpty export <timestamp>)` and edit only the clone.
     CapCut will list it as a separate project, so the original is untouched.
   - Optional advanced mode (off by default): edit in place but first copy
     `draft_content.json` → `draft_content.json.videoempty.<timestamp>.bak`.
   - Refuse to write if CapCut is running with this project open (best-effort
     lock check on the file).

### Phase 2 — Emitter (the actual work)
2. New project `VideoEmpty.Rendering/Export/CapCut/` with:
   - `CapCutProject.cs` — light POCOs for the subset of fields we read/write
     (everything else is preserved verbatim via `JsonNode` so we don't lose
     CapCut-only data).
   - `CapCutColor.cs`, `CapCutGeometry.cs` — unit/colour conversions.
   - `CapCutTemplateEmitter.cs` — given a `TemplateInstance` + its `Template`
     + the canvas size, produce: a list of text materials, a list of shape
     materials, and a list of (track_kind, segment) tuples.
   - `CapCutProjectWriter.cs` — open existing `draft_content.json`, append our
     materials, append segments to the right text/sticker tracks (creating
     new tracks if none exist), bump `duration` if needed, save atomically
     (write to temp + rename).

3. **GUIDs**: use uppercase 8-4-4-4-12 to match CapCut's style, e.g.
   `Guid.NewGuid().ToString("D").ToUpperInvariant()`.

4. **Time precision**: store everything in `long` microseconds; never round-trip
   through `double` seconds.

5. **Render indices**: start at `max(existing render_index) + 100` so our
   overlays sit on top of existing CapCut content.

### Phase 3 — UI
6. New menu item **File → Export to CapCut Project…**:
   - Step 1 dialog: "Choose your CapCut project folder" (default to
     `%LOCALAPPDATA%\CapCut\User Data\Projects\com.lveditor.draft`).
   - Step 2 dialog: choose **Clone project** (recommended, default) or
     **Edit in place (creates `.bak`)**.
   - Confirmation listing N instances → N text materials, M shape materials.
   - Progress + success/error.

### Phase 4 — Validation
7. **Round-trip smoke test**: open the cloned project in CapCut and confirm
   each instance renders as expected. Document any CapCut version pinning.
8. **Unit tests** for: unit conversions, color encoding, segment construction,
   `draft_content.json` patch idempotency.

## Open questions before implementing (will ask user)
- Q1: Default mode — clone the project, or edit in place with `.bak`?
  (Clone is safer; edit-in-place is what the user might already expect.)
- Q2: Animations — our app supports slide-in/out etc. CapCut animations are
  separate material objects referenced from `extra_material_refs`. For v1 do
  we skip animations (instance just appears for its duration), or attempt a
  best-effort map to CapCut's built-in "Rise", "Fade in" etc?
- Q3: Should we also export the source video clip itself into the new CapCut
  project, or assume the user opens the existing CapCut project that already
  has the video on the main track?

## Risks / caveats
- CapCut may add or rename fields between versions. Reference project is
  CapCut 8.5.0.3590 (Windows). We use **add-only** edits and preserve
  unknown fields via `JsonNode` to minimise breakage.
- Fonts: text materials reference an absolute font path. We default to the
  bundled CapCut SystemFont path on Windows; on macOS we'd need a different
  default. Out of scope for v1 (Windows only).
- 4-point `custom_points` array on shapes must match `shape_size` exactly or
  CapCut shows a malformed rectangle.

## Todo list (tracked in SQL)
- `cc-safety`           Phase 1 — backup / clone mode + folder picker
- `cc-emitter-core`     Phase 2 — POCOs + emitter + writer
- `cc-emitter-tests`    unit tests for conversions + writer
- `cc-ui`               Phase 3 — menu item + dialogs
- `cc-roundtrip-test`   Phase 4 — manual validation in CapCut
