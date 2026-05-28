namespace Foundry.Core.Pcb;

/// <summary>
/// Deterministic, KiCad-free placement engine (Track B v2.3). Turns a <see cref="PlacementPlan"/>
/// (AI advice — functional groups + edge/near intent, NEVER coordinates) plus the project's components
/// and their <see cref="FootprintMap.CourtyardOf"/> sizes into collision-free mm coordinates and a
/// rectangular board outline. The AI is fenced to placement intent; this class owns every coordinate and
/// can never emit an overlapping or off-board layout. An empty plan degrades to a tidy grid identical in
/// spirit to v2.2. Pure and fully unit-testable — no KiCad, no AI. Same posture as the rest of Foundry:
/// AI proposes, deterministic geometry disposes.
/// </summary>
public static class PcbPlacer
{
    private const string Unassigned = "_unassigned";

    /// <summary>One component to place: its ref, footprint lib id, courtyard W×H, and a coarse rotation hint.</summary>
    public sealed record PlacedItem(string Ref, string LibId, (double WMm, double HMm) Courtyard, double RotHint = 0);

    /// <summary>A placed component's final position (snapped to 0.05 mm).</summary>
    public readonly record struct Placement(double XMm, double YMm, double Rot);

    /// <summary>The placer's output: ref → position lookup + the board outline, sized to fit.</summary>
    public sealed class PlaceResult
    {
        private readonly Dictionary<string, Placement> _byRef;
        public PlaceResult(Dictionary<string, Placement> byRef, IReadOnlyList<double[]> outline)
        {
            _byRef = byRef;
            OutlineSegmentsMm = outline;
        }
        public Placement this[string @ref] => _byRef[@ref];
        public bool TryGet(string @ref, out Placement p) => _byRef.TryGetValue(@ref, out p);
        public IReadOnlyDictionary<string, Placement> Positions => _byRef;
        public IReadOnlyList<double[]> OutlineSegmentsMm { get; }
    }

    // A box being packed (inflated by gap on every side).
    private sealed class Box
    {
        public string Ref = "";
        public double W, H;        // inflated footprint extent (courtyard + 2*gap)
        public double X, Y;        // local then absolute lower-left corner
        public double Rot;
        public double CourtW, CourtH;  // un-inflated courtyard (for adjacency math)
    }

    /// <summary>
    /// Place every item with guaranteed non-overlap. Coordinates are deterministic (everything is sorted
    /// by ref). <paramref name="marginMm"/> is the board border; <paramref name="gapMm"/> is the minimum
    /// clearance enforced between every pair of courtyards.
    /// </summary>
    public static PlaceResult Place(IReadOnlyList<PlacedItem> items, PlacementPlan plan,
        double marginMm = 5.0, double gapMm = 1.5)
    {
        plan ??= PlacementPlan.Empty;
        var ordered = items.OrderBy(i => i.Ref, StringComparer.OrdinalIgnoreCase).ToList();

        // 1. Build a box per item: resolve courtyard, swap W/H for 90/270, inflate by gap.
        var boxes = new Dictionary<string, Box>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in ordered)
        {
            double rot = SnapRot(RotHintFor(it, plan));
            var (cw, ch) = it.Courtyard;
            if (rot is 90 or 270) (cw, ch) = (ch, cw);
            boxes[it.Ref] = new Box
            {
                Ref = it.Ref, Rot = rot, CourtW = cw, CourtH = ch,
                W = cw + 2 * gapMm, H = ch + 2 * gapMm,
            };
        }

        // 2. Assign each item to a group (hint wins, else group membership, else _unassigned).
        var groupOf = AssignGroups(ordered, plan);

        // 3. Resolve per-item edge affinity (hint overrides its group's edge).
        var edgeOf = AssignEdges(ordered, plan, groupOf);

        // Edge items are pulled out of region flow; interior items are packed into group regions.
        var interiorRefs = ordered.Where(i => edgeOf[i.Ref] == EdgeAffinity.None).Select(i => i.Ref).ToList();
        var edgeRefs = ordered.Where(i => edgeOf[i.Ref] != EdgeAffinity.None).Select(i => i.Ref).ToList();

        // Empty-plan fast path: everything _unassigned, no edge/near hints → single tidy grid (v2.2).
        bool anyNear = ordered.Any(i => NearRefFor(i, plan) is not null);
        bool anyEdge = edgeRefs.Count > 0;
        bool anyGroup = groupOf.Values.Any(g => g != Unassigned);
        if (!anyNear && !anyEdge && !anyGroup)
            return Grid(ordered.Select(i => boxes[i.Ref]).ToList(), marginMm, gapMm);

        // 4. Pack each interior group into its own local bin.
        var nearOf = ordered.ToDictionary(i => i.Ref, i => NearRefFor(i, plan), StringComparer.OrdinalIgnoreCase);
        var interiorByGroup = interiorRefs
            .GroupBy(r => groupOf[r], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var groupBoxes = new Dictionary<string, (double W, double H, List<Box> Members)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (gid, members) in interiorByGroup)
        {
            var memberBoxes = members.OrderBy(r => r, StringComparer.OrdinalIgnoreCase).Select(r => boxes[r]).ToList();
            var (gw, gh) = PackGroup(memberBoxes, nearOf, gapMm);
            groupBoxes[gid] = (gw, gh, memberBoxes);
        }

        // 5. Order interior groups into regions (left→right), then lay them out.
        var regionGap = Math.Max(gapMm * 2, 2.0);
        var orderedGroups = OrderGroups(groupBoxes.Keys, plan);
        double regionsMaxH = groupBoxes.Count == 0 ? 0 : groupBoxes.Values.Max(g => g.H);

        double cursorX = marginMm;
        foreach (var gid in orderedGroups)
        {
            var (gw, gh, members) = groupBoxes[gid];
            double offY = marginMm + (regionsMaxH - gh) / 2.0;   // vertically center each region in the band
            foreach (var b in members) { b.X += cursorX; b.Y += offY; }
            cursorX += gw + regionGap;
        }

        double interiorRight = cursorX <= marginMm ? marginMm : cursorX - regionGap;
        double interiorTop = marginMm + regionsMaxH;

        // 6. Pin edge items to reserved bands flush with the named edge.
        var placedBoxes = boxes.Values.Where(b => edgeOf[b.Ref] == EdgeAffinity.None).ToList();
        PlaceEdges(edgeRefs, edgeOf, boxes, marginMm, gapMm, ref interiorRight, ref interiorTop, placedBoxes);

        // 7. Size the outline to contain every box + margin, snap to 0.05.
        var all = boxes.Values.ToList();
        double boardW = Math.Max(interiorRight, all.Count == 0 ? 0 : all.Max(b => b.X + b.W)) + marginMm;
        double boardH = Math.Max(interiorTop, all.Count == 0 ? 0 : all.Max(b => b.Y + b.H)) + marginMm;

        var result = new Dictionary<string, Placement>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in all)
            result[b.Ref] = new Placement(Snap(b.X + b.W / 2), Snap(b.Y + b.H / 2), b.Rot);

        return new PlaceResult(result, Outline(Snap(boardW), Snap(boardH)));
    }

    // ---- group/edge assignment ----

    private static Dictionary<string, string> AssignGroups(List<PlacedItem> items, PlacementPlan plan)
    {
        var groupOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var known = new HashSet<string>(items.Select(i => i.Ref), StringComparer.OrdinalIgnoreCase);

        // group members (first wins on duplicate membership)
        foreach (var g in plan.Groups)
            foreach (var m in g.Members)
                if (known.Contains(m) && !groupOf.ContainsKey(m)) groupOf[m] = g.Id;

        // explicit hint group overrides membership
        foreach (var h in plan.Hints)
            if (h.Group is { Length: > 0 } && known.Contains(h.Ref)) groupOf[h.Ref] = h.Group;

        // a NearRef cap follows its target into the target's group
        foreach (var i in items)
        {
            var near = NearRefFor(i, plan);
            if (near is not null && known.Contains(near) && groupOf.TryGetValue(near, out var tg))
                groupOf[i.Ref] = tg;
        }

        foreach (var i in items)
            if (!groupOf.ContainsKey(i.Ref)) groupOf[i.Ref] = Unassigned;

        return groupOf;
    }

    private static Dictionary<string, EdgeAffinity> AssignEdges(
        List<PlacedItem> items, PlacementPlan plan, Dictionary<string, string> groupOf)
    {
        var edgeOf = new Dictionary<string, EdgeAffinity>(StringComparer.OrdinalIgnoreCase);
        var groupEdge = plan.Groups.ToDictionary(g => g.Id, g => g.Edge, StringComparer.OrdinalIgnoreCase);
        var hintEdge = plan.Hints.Where(h => h.Edge != EdgeAffinity.None)
            .GroupBy(h => h.Ref, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Edge, StringComparer.OrdinalIgnoreCase);

        foreach (var i in items)
        {
            // per-item hint edge wins; else the item's group edge.
            if (hintEdge.TryGetValue(i.Ref, out var he)) { edgeOf[i.Ref] = he; continue; }
            edgeOf[i.Ref] = groupOf.TryGetValue(i.Ref, out var g) && groupEdge.TryGetValue(g, out var ge)
                ? ge : EdgeAffinity.None;
        }
        return edgeOf;
    }

    private static IReadOnlyList<string> OrderGroups(IEnumerable<string> groupIds, PlacementPlan plan)
    {
        var ids = new HashSet<string>(groupIds, StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var g in plan.RegionOrder)
            if (ids.Remove(g)) result.Add(g);
        bool hasUnassigned = ids.Remove(Unassigned);
        result.AddRange(ids.OrderBy(g => g, StringComparer.OrdinalIgnoreCase));
        if (hasUnassigned) result.Add(Unassigned);
        return result;
    }

    // ---- packing ----

    /// <summary>
    /// Shelf bin-pack one group's boxes into a square-ish region. "near" caps are pulled out and placed
    /// adjacent to their target's final box (right, then top/left/bottom), collision-checked. Returns the
    /// group's local bounding box; each box's X/Y is set to a local (lower-left) offset.
    /// </summary>
    private static (double W, double H) PackGroup(List<Box> members,
        IReadOnlyDictionary<string, string?> nearOf, double gap)
    {
        var nearTargets = new HashSet<string>(
            members.Select(b => b.Ref).Where(r => nearOf.TryGetValue(r, out var n) && n is not null),
            StringComparer.OrdinalIgnoreCase);

        // shelf-pack the non-near "anchor" boxes first
        var anchors = members.Where(b => !nearTargets.Contains(b.Ref)).ToList();
        var caps = members.Where(b => nearTargets.Contains(b.Ref)).ToList();

        double totalArea = anchors.Sum(b => b.W * b.H);
        double targetW = Math.Max(anchors.Count == 0 ? 0 : anchors.Max(b => b.W), Math.Sqrt(totalArea) * 1.3);

        var placed = new List<Box>();
        double shelfX = 0, shelfY = 0, shelfH = 0, maxW = 0;
        foreach (var b in anchors)
        {
            if (shelfX > 0 && shelfX + b.W > targetW)
            {
                shelfY += shelfH + gap;
                shelfX = 0; shelfH = 0;
            }
            b.X = shelfX; b.Y = shelfY;
            shelfX += b.W + gap;
            shelfH = Math.Max(shelfH, b.H);
            maxW = Math.Max(maxW, b.X + b.W);
            placed.Add(b);
        }
        double curW = Math.Max(maxW, targetW);
        double curH = shelfY + shelfH;

        // place each near-cap adjacent to its target box (right, top, left, bottom), else append on a new shelf
        foreach (var cap in caps.OrderBy(b => b.Ref, StringComparer.OrdinalIgnoreCase))
        {
            var targetRef = nearOf[cap.Ref];
            var target = placed.FirstOrDefault(b => b.Ref.Equals(targetRef, StringComparison.OrdinalIgnoreCase));
            bool seated = false;
            if (target is not null)
            {
                foreach (var (x, y) in new[]
                {
                    (target.X + target.W + gap, target.Y),                 // right
                    (target.X, target.Y + target.H + gap),                 // top
                    (target.X - cap.W - gap, target.Y),                    // left
                    (target.X, target.Y - cap.H - gap),                    // bottom
                })
                {
                    if (x < 0 || y < 0) continue;
                    cap.X = x; cap.Y = y;
                    if (!Overlaps(cap, placed)) { seated = true; break; }
                }
            }
            if (!seated)   // fall back to a fresh shelf below everything (never overlap)
            {
                cap.X = 0; cap.Y = curH + gap;
                curH = cap.Y + cap.H;
            }
            placed.Add(cap);
            curW = Math.Max(curW, cap.X + cap.W);
            curH = Math.Max(curH, cap.Y + cap.H);
        }

        // normalize so the group's local box starts at (0,0)
        double minX = placed.Count == 0 ? 0 : placed.Min(b => b.X);
        double minY = placed.Count == 0 ? 0 : placed.Min(b => b.Y);
        foreach (var b in placed) { b.X -= minX; b.Y -= minY; }
        return (curW - minX, curH - minY);
    }

    private static void PlaceEdges(List<string> edgeRefs, Dictionary<string, EdgeAffinity> edgeOf,
        Dictionary<string, Box> boxes, double margin, double gap,
        ref double interiorRight, ref double interiorTop, List<Box> placedBoxes)
    {
        // left/right → vertical strip; top/bottom → horizontal strip. Each reserves its own band so it
        // never overlaps the interior regions or another edge band.
        var byEdge = edgeRefs.GroupBy(r => edgeOf[r]);

        double boardRight = interiorRight;
        double boardTop = interiorTop;

        foreach (var grp in byEdge.OrderBy(g => g.Key))
        {
            var strip = grp.OrderBy(r => r, StringComparer.OrdinalIgnoreCase).Select(r => boxes[r]).ToList();
            switch (grp.Key)
            {
                case EdgeAffinity.Left:
                {
                    double x = margin, y = margin;
                    // push interior regions right of the left strip
                    double stripW = strip.Count == 0 ? 0 : strip.Max(b => b.W);
                    foreach (var b in placedBoxes) b.X += stripW + gap;
                    boardRight += stripW + gap;
                    foreach (var b in strip) { b.X = x; b.Y = y; y += b.H + gap; }
                    boardTop = Math.Max(boardTop, y);
                    break;
                }
                case EdgeAffinity.Right:
                {
                    double stripW = strip.Count == 0 ? 0 : strip.Max(b => b.W);
                    double x = boardRight + gap, y = margin;
                    foreach (var b in strip) { b.X = x; b.Y = y; y += b.H + gap; }
                    boardRight = x + stripW;
                    boardTop = Math.Max(boardTop, y);
                    break;
                }
                case EdgeAffinity.Top:
                {
                    double y = boardTop + gap, x = margin;
                    foreach (var b in strip) { b.X = x; b.Y = y; x += b.W + gap; }
                    boardTop = y + (strip.Count == 0 ? 0 : strip.Max(b => b.H));
                    boardRight = Math.Max(boardRight, x);
                    break;
                }
                case EdgeAffinity.Bottom:
                {
                    // reserve a bottom band by shifting every box placed so far up, then fill the band
                    double bandH = strip.Count == 0 ? 0 : strip.Max(b => b.H);
                    foreach (var b in boxes.Values.Where(b => edgeOf[b.Ref] != EdgeAffinity.Bottom)) b.Y += bandH + gap;
                    double x = margin, y = margin;
                    foreach (var b in strip) { b.X = x; b.Y = y; x += b.W + gap; }
                    boardTop += bandH + gap;
                    boardRight = Math.Max(boardRight, x);
                    break;
                }
            }
        }
        interiorRight = boardRight;
        interiorTop = boardTop;
    }

    // ---- empty-plan tidy grid (= v2.2 behavior, courtyard-aware) ----

    private static PlaceResult Grid(List<Box> boxes, double margin, double gap)
    {
        int n = boxes.Count;
        var result = new Dictionary<string, Placement>(StringComparer.OrdinalIgnoreCase);
        if (n == 0) return new PlaceResult(result, Outline(2 * margin, 2 * margin));

        int cols = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(n)));
        double cellW = boxes.Max(b => b.W) + gap;
        double cellH = boxes.Max(b => b.H) + gap;

        double maxX = 0, maxY = 0;
        for (int i = 0; i < n; i++)
        {
            var b = boxes[i];
            double x = margin + (i % cols) * cellW;
            double y = margin + (i / cols) * cellH;
            result[b.Ref] = new Placement(Snap(x + b.W / 2), Snap(y + b.H / 2), b.Rot);
            maxX = Math.Max(maxX, x + b.W);
            maxY = Math.Max(maxY, y + b.H);
        }
        return new PlaceResult(result, Outline(Snap(maxX + margin), Snap(maxY + margin)));
    }

    // ---- geometry helpers ----

    private static bool Overlaps(Box a, IEnumerable<Box> others) =>
        others.Any(b => a.X < b.X + b.W && a.X + a.W > b.X && a.Y < b.Y + b.H && a.Y + a.H > b.Y);

    private static IReadOnlyList<double[]> Outline(double w, double h) => new List<double[]>
    {
        new[] { 0.0, 0.0, w, 0.0 },
        new[] { w, 0.0, w, h },
        new[] { w, h, 0.0, h },
        new[] { 0.0, h, 0.0, 0.0 },
    };

    private static double SnapRot(double rot)
    {
        double r = rot % 360; if (r < 0) r += 360;
        return Math.Round(r / 90) * 90 % 360;
    }

    private static double Snap(double v) => Math.Round(v / 0.05) * 0.05;

    private static double RotHintFor(PlacedItem it, PlacementPlan plan)
    {
        var h = plan.Hints.FirstOrDefault(x => x.Ref.Equals(it.Ref, StringComparison.OrdinalIgnoreCase) && x.Rotation != 0);
        return h is not null ? h.Rotation : it.RotHint;
    }

    private static string? NearRefFor(PlacedItem it, PlacementPlan plan) =>
        plan.Hints.FirstOrDefault(x => x.Ref.Equals(it.Ref, StringComparison.OrdinalIgnoreCase) && x.NearRef is not null)?.NearRef;
}
