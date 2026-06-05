using System.Text.Json;
using Foundry.Core.Ai;
using Foundry.Core.Firmware;
using Foundry.Core.Kb;
using Foundry.Core.Project;
using Foundry.Core.Validation;

namespace Foundry.Core.Generation;

// AI OpenSCAD enclosure generate/fix (split from ProjectGenerator.cs).
public sealed partial class ProjectGenerator
{
    /// <summary>Ask the AI to write parametric OpenSCAD for this enclosure (PRD v2 Phase B).</summary>
    public async Task<(bool Ok, string Scad, string Message)> GenerateEnclosureScadAsync(Project.Project project, CancellationToken ct = default)
    {
        if (!_ai.HasKey) return (false, "", "Add your Anthropic API key in Settings.");
        var e = project.Enclosure;
        var cutouts = string.Join("\n", e.Cutouts.Select(c =>
            $"  - face {c.Face} · {c.Shape}" + (c.Shape == "circle"
                ? $" d={c.D:0.##}"
                : $" {(c.Size?.Length > 0 ? c.Size[0].ToString("0.##") : "10")}×{(c.Size?.Length > 1 ? c.Size[1].ToString("0.##") : "6")}")
              + $" pos=[{string.Join(",", c.Pos.Select(p => p.ToString("0.##")))}]  label=\"{c.Label}\""));
        var vents = e.Vents.Count == 0 ? "(none)" : string.Join(", ", e.Vents.Select(v => $"{v.Count}×{v.Face}"));
        var parts = string.Join("\n", project.Components.Take(8).Select(c => $"  - {c.Alias} ({c.Name})"));

        // Surface face dimensions and an occupied/free map so the model can apply style
        // features only to clear faces and size the recessed panel to contain its cutouts.
        double il = e.Inner is { Length: > 0 } ? e.Inner[0] : 60, iw = e.Inner is { Length: > 1 } ? e.Inner[1] : 40, ih = e.Inner is { Length: > 2 } ? e.Inner[2] : 25;
        double w = e.Wall, outerL = il + 2 * w, outerW = iw + 2 * w, outerH = ih + w;
        var occupied = new HashSet<string>(e.Cutouts.Select(c => c.Face), StringComparer.OrdinalIgnoreCase);
        occupied.UnionWith(e.Vents.Select(v => v.Face));
        string FaceState(string face, double fw, double fh) =>
            $"  - {face,-6} {fw:0.##}×{fh:0.##} mm  {(occupied.Contains(face) ? "[OCCUPIED — cutouts/vents present]" : "[free — style here]")}";
        var faceMap = string.Join("\n", new[]
        {
            FaceState("front",  outerL, outerH),
            FaceState("back",   outerL, outerH),
            FaceState("left",   outerW, outerH),
            FaceState("right",  outerW, outerH),
            FaceState("top",    outerL, outerW),
            FaceState("bottom", outerL, outerW),
        });

        var user =
            $"Design a 3D-printable enclosure for this device. Output ONLY parametric OpenSCAD code (no prose, no fences).\n\n" +
            $"Inner cavity: [{il:0.##}, {iw:0.##}, {ih:0.##}] mm   outer: [{outerL:0.##}, {outerW:0.##}, {outerH:0.##}] mm   wall: {w:0.##} mm   lid: {e.Lid}   mount: {e.Mount}   standoffs: {e.Standoffs}\n" +
            $"Faces (outer dimensions + occupancy):\n{faceMap}\n\n" +
            $"Vents: {vents}\nCutouts:\n{(string.IsNullOrEmpty(cutouts) ? "  (none)" : cutouts)}\n\nComponents:\n{parts}\n\n" +
            "Reminder: cutout `pos = [x,y]` is the offset from the FACE's centre (x = horizontal axis of that face, y = vertical axis); " +
            "see the face-axes block in the system prompt. " +
            "Apply the recess/bezel to a face that has cutouts (size the recess to contain ALL of that face's cutouts + a 2 mm margin). " +
            "Place the accent ridge on a face marked [free] (prefer the lid top, then back); never run it across an OCCUPIED face. " +
            "Apply the 45° corner clip only at a corner whose two adjacent faces are [free] within 8 mm. " +
            "Subtract every cutout LAST and use a cut depth ≥ wall + bezel_inset + accent_ridge_w + 4 mm so it pierces through any style feature in front of it.";

        try
        {
            var raw = await _ai.CompleteAsync(EnclosureScadSystemPrompt, user, _model, ct);
            var scad = StripFences(raw).Trim();
            if (scad.Length < 50 || !LooksLikeScad(scad))
                return (false, "", "The model didn't return OpenSCAD code. Try again.");
            Diagnostics.AppLog.Info("scad", $"enclosure SCAD generated · {scad.Length} chars");
            return (true, scad, "Generated.");
        }
        catch (Exception ex)
        {
            Diagnostics.AppLog.Error("scad", $"enclosure SCAD failed: {ex.Message}");
            return (false, "", $"Failed: {ex.Message}");
        }
    }

    /// <summary>Ask the AI to repair OpenSCAD code given the compiler stderr (PRD v2 Phase D — SCAD fix loop).</summary>
    public async Task<(bool Ok, string Scad, string Message)> FixEnclosureScadAsync(Project.Project project, string currentScad, string compilerError, CancellationToken ct = default)
    {
        if (!_ai.HasKey || string.IsNullOrWhiteSpace(compilerError)) return (false, currentScad, "no error to fix");
        var user =
            $"This OpenSCAD script fails to render in OpenSCAD 2021.01. Fix it and return ONLY the corrected " +
            $"OpenSCAD code (no prose, no fences). Keep the parametric structure.\n\n" +
            $"COMPILER ERROR:\n{compilerError}\n\nCURRENT SCAD:\n{currentScad}";
        try
        {
            var raw = await _ai.CompleteAsync(EnclosureScadSystemPrompt, user, _model, ct);
            var scad = StripFences(raw).Trim();
            if (scad.Length < 50 || !LooksLikeScad(scad)) return (false, currentScad, "fix didn't return SCAD");
            Diagnostics.AppLog.Info("scad", $"SCAD fix applied · {scad.Length} chars");
            return (true, scad, "Fixed.");
        }
        catch (Exception ex) { return (false, currentScad, $"Fix failed: {ex.Message}"); }
    }

    private const string EnclosureScadSystemPrompt = """
You are a senior parametric CAD engineer designing **techno-futurist** 3D-printable enclosures.
Aim for the look of a high-end gadget — chamfered edges, recessed face panels, subtle accent ridges,
diagonal vent grilles — never a plain box. Write COMPLETE, valid OpenSCAD that satisfies the request.

Required parametric structure
- Start with a clearly-named block of top-level parameters: `wall_thickness`, `inner_l`, `inner_w`,
  `inner_h`, `lid_thickness`, `screw_diameter`, plus styling knobs (`chamfer`, `bezel_inset`,
  `accent_ridge_w`, `vent_angle`, `corner_clear`) so the user can dial the look in. Default
  `corner_clear = 6` (mm). Realistic defaults; integer for counts.
- Build geometry from named modules: `outer_shell`, `lid`, `cutouts`, `standoffs`, `mounts`, plus a
  `style_features` module for the futurist details. Use difference/union/hull/minkowski/rotate/
  translate cleanly. Keep `$fn = 32` for speed (use 24 for very small fillets).

Coordinate convention — STRICT
- The base origin is the OUTER box centre at z=0. The base sits with its bottom on z=0 and its
  open top at z = `inner_h + wall_thickness`. Outer footprint is `outer_l × outer_w` where
  `outer_l = inner_l + 2*wall_thickness` and `outer_w = inner_w + 2*wall_thickness`.
- Cutout `pos = [x, y]` is the offset from the FACE'S OWN CENTRE, in millimetres, where x is the
  horizontal axis of that face and y is the vertical axis of that face (z is "up" on the
  side/front/back faces; y is "up" on the top/bottom faces). `(0,0)` means the face's exact centre.
- Face axes (re-confirm before placing each cutout):
    * front  (−Y wall)  → x along +X (left→right), y along +Z (down→up)
    * back   (+Y wall)  → x along −X,              y along +Z
    * left   (−X wall)  → x along +Y (back→front), y along +Z
    * right  (+X wall)  → x along −Y,              y along +Z
    * top    (+Z lid)   → x along +X,              y along +Y
    * bottom (−Z floor) → x along +X,              y along −Y

Build order — MUST follow this sequence
1. Build the outer shell (chamfered + top-bevelled).
2. UNION style additions (recessed panels, raised bezels, accent ridges, light-pipe trim).
3. UNION standoffs and mount tabs/flanges.
4. DIFFERENCE the inner cavity.
5. DIFFERENCE every cutout — LAST. Each cutout's subtracting tool extends from the inside of the
   cavity all the way through any style additions on that face (cut depth ≥ `wall_thickness +
   bezel_inset + accent_ridge_w + 4`). Result: cutouts always read as clean openings, never
   partially blocked by a ridge, bezel border, or recess wall.
6. DIFFERENCE vent slots (also using a generous depth, same reasoning).

Cutout keepout — style features must yield to cutouts
- For every cutout, treat its bounding box plus a 2 mm margin as a KEEPOUT zone on its face.
- The accent ridge must skip keepout zones (split it into segments that go AROUND them, or place
  the ridge on a face that has NO cutouts — preferring the lid top, then the back face).
- The recessed-panel bezel border must STAY CLEAR of every cutout's keepout — either size the
  recess big enough to contain all the face's cutouts inside it (with the 2 mm margin), or shrink
  it to a region that contains no cutouts at all. Never let the raised bezel border cross a cutout.
- The 45° stealth corner clip is allowed ONLY on a corner whose two adjacent faces have NO cutouts
  within 8 mm of that corner. If no such corner exists, omit the corner clip rather than break a
  cutout.
- Light-pipe slits are extra cutouts — they obey the same keepout rule (never inside another
  cutout's keepout).

Style-feature face restrictions — STRICT (these are the common AI mistakes)
- **Accent ridge — allowed faces: LID TOP (default, preferred), BACK (+Y wall), or BASE BOTTOM
  underside. NEVER on the LEFT or RIGHT side faces.** Rationale: the side faces need a
  `rotate([0,±90,0])` to lay a 2D-extruded ridge on, and after that rotation the 2D x-axis
  becomes global Z — so a `ridge_len = 50 mm` ends up as a 50 mm-tall vertical PILLAR that
  pokes out the top and bottom of a 30 mm-tall wall. Lid-top and back-face placements use only
  flat translates (or a single `rotate([90,0,0])`) and are reliably geometrically safe.
- **Stealth corner clip — must be a SUBTRACTIVE 45° cube/wedge** removed from one vertical corner
  of the `outer_shell`, with the cut at least the full wall height (z = 0 to outer_h). It is NOT
  a decorative etched line, NOT a thin `linear_extrude(0.6)` of a hull — it is `difference() {
  outer_shell(); translate([±outer_l/2, ±outer_w/2, 0]) rotate([0,0,45]) cube([2*chamfer_clip,
  2*chamfer_clip, outer_h + 2], center=true); }` with `chamfer_clip ≈ 6 mm`. Omit it entirely
  rather than render an etched/scribed substitute.
- **Light-pipe slits** stay subtractive and on the front bezel face only.

Sanity-check — every style addition must fit inside its face
- Before unioning any style feature, mentally compute its bounding box in WORLD coordinates AFTER
  every rotate/translate, and verify `[x_min, x_max] × [y_min, y_max] × [z_min, z_max]` is fully
  inside the face it claims to sit on (or fully inside the outer_shell envelope for protruding
  features ≤ 1.5 mm proud).
- A feature on a vertical side face has `z ∈ [0, outer_h]` — never below 0, never above `outer_h`.
- A feature on the lid top has `z ∈ [lid_thickness, lid_thickness + accent_ridge_h]`.
- A feature on the bottom has `z ∈ [-accent_ridge_h, 0]`.
- If your math doesn't satisfy these bounds, your rotate is wrong — try a different face or
  use only translate (no rotate) on the lid top.

Corner clearance — NO openings at corners
- Every cutout's bounding box must stay at least `corner_clear` mm (default 6 mm) inside every
  edge of its face. That means for a face of width `fw` and height `fh`, the cutout centre's
  allowed range is `x ∈ [−fw/2 + corner_clear + size_x/2,  fw/2 − corner_clear − size_x/2]`
  and `y ∈ [−fh/2 + corner_clear + size_y/2,  fh/2 − corner_clear − size_y/2]` (for a circle of
  diameter `d`, use `size_x = size_y = d`).
- If a requested `pos` would put the cutout closer than `corner_clear` to any edge — and therefore
  near the corner where two edges meet — clamp `pos` to the nearest allowed value along that axis.
  Keep the cutout on its requested face; only the offset within the face may move.
- If the cutout itself is larger than the available room (`size > face_dim − 2*corner_clear`),
  centre it on the face and add a `// note: <label> too big for corner clearance — centred` comment.
- This protects the chamfered vertical edge, the top bevel, corner standoffs/screw bosses, and the
  stealth corner clip. No opening should ever break the silhouette of a corner.

Techno-futurist styling — REQUIRED, applied subject to the keepout rule above
- **Chamfered vertical edges** on the outer shell (`hull()` over offset round-rects, or a
  `minkowski` with a small chamfer cylinder). 2–3 mm radius reads "designed", not "printed-box".
- **Top edge chamfer/bevel** on the base wall tops and the lid — a ~1.5 mm 45° bevel.
- **Recessed face panel** on a face that has cutouts (front by default): inset `bezel_inset`
  (1–2 mm) with a slim raised border (the bezel) framing the cutouts. The recess must contain
  ALL of that face's cutouts plus their 2 mm margin.
- **Slim accent ridge** (~1 mm proud, `accent_ridge_w` ≈ 3 mm wide), parallel to the long axis,
  stopping short of corners. Place on the LID TOP, BACK face, or BASE BOTTOM only — NEVER on
  the left or right vertical side faces (see Style-feature face restrictions below). Segment
  around keepouts.
- **Diagonal/angled vent grille**: rotate vent slots by `vent_angle` (default 25°) or use a
  triangular/hex slot pattern. Keep the rotated slot bounding box entirely inside the face.
- **Stealth corner detail**: SUBTRACTIVE 45° wedge cut from one vertical corner of the
  outer_shell (full wall height) — NOT a decorative etch. See restrictions below.
- **Light-pipe slits** for indicator LEDs near the front bezel (thin 1.0×4 mm cutouts) when an
  LED appears in the components and a clear spot exists.
- Keep the design printable: no overhangs >45°, no bridges longer than the wall is thick, no
  fragile features <0.8 mm. Avoid `text()` (fonts aren't installed).

Functional rules (must still be met)
- Place every requested cutout on its named face at the given (x,y) offset, using the face
  axes above. Do NOT drop a cutout. You MAY clamp its (x,y) inward by up to a few millimetres
  to satisfy the corner-clearance rule above — that's preferred over breaking a corner.
- Vent slots respect the schema's face + count, but render as the angled/grille style above.
- Add corner standoffs with M2/M3 pilot holes when the schema asks for them; position them so
  they do not collide with any cutout's keepout.
- `lid == "screw"` → add corner screw bosses + matching clearance holes in the lid.
- `lid == "snap"` → add a locating lip on the lid that nests inside the base opening.
- `mount == "wall-tabs"` → flanged wall-mount tabs with screw holes on the long sides
  (avoid faces dense with cutouts).
- Render the base and the lid SIDE-BY-SIDE at z=0 (translate the lid +X past the base by
  `outer_l + 12`), oriented for printing so the .stl is ready to slice.

Output discipline
- OpenSCAD 2021.01 only. No external libraries, no `text()`, no `import()`.
- Output ONLY OpenSCAD code — no markdown fences, no prose, no explanation.
""";

    private static bool LooksLikeScad(string s) =>
        s.Contains("module ") || s.Contains("difference(") || s.Contains("union(") || s.Contains("cube(") || s.Contains("cylinder(");

    private static string StripFences(string s)
    {
        s = s.Trim();
        if (s.StartsWith("```")) { var nl = s.IndexOf('\n'); if (nl > 0) s = s[(nl + 1)..]; }
        if (s.EndsWith("```")) s = s[..^3];
        return s.Trim();
    }
}
