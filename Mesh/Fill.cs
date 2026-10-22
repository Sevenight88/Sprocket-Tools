using System.Numerics;

namespace SprocketTools;

/// Fills a flat region (an outer loop, maybe with holes) with faces that are easy to edit afterwards: a constrained
/// Delaunay triangulation (the best possible from the outline's own points: it maximises the smallest angle), refined
/// with added points inside the region where it allows them (Delaunay refinement, no long thin fans), and triangles
/// then paired into convex quads wherever they fit. Plain maths, tested offline.
public static class Fill
{
    /// A vertex the fill adds: its position, and the existing vertices (with weights) its settings blend from.
    public sealed record Added(Vector3 P, (int V, float W)[] Blend);

    /// How a region is filled: from its own points only (the fewest points; triangles paired into quads), with points
    /// added until no triangle has an angle under 22° where the outline allows it (light), with a ring of quads along
    /// curved outline and 28° (smooth), or with a rectangular box enclosing the cut (rectangle box).
    public enum Mode { Fewest, Light, Smooth, Rectangle, TriangleBox = Rectangle }

    public static readonly string[] ModeNames = { "最少点", "轻量", "平滑", "矩形框" };

    /// Faces (vertex indices, turning the same way as `outer`) covering the region between `outer` and `holes`. New
    /// vertices are appended to `pos` and described in `added` (same order).
    /// Which way each region was filled, for the log and tests ("quality mesh, N points", "delaunay", "as is").
    public static readonly List<string> Paths = new();

    /// `light`: as few new points as will still avoid long thin fans. Otherwise "smooth": a quad ring hugging curved
    /// outline and evener faces, more points. `added` null: no new points at all (fewest faces from the outline's own points).
    public static List<int[]> Region(List<Vector3> pos, List<int> outer, List<List<int>> holes, Vector3 normal, List<Added>? added, bool light = true, Mode mode = Mode.Fewest)
    {
        var faces = RegionFaces(pos, outer, holes, normal, added, light, mode, out string path);
        if (Paths.Count >= 4096) Paths.RemoveRange(0,2048);
        Paths.Add(path);
        return faces;
    }

    static List<int[]> RegionFaces(List<Vector3> pos, List<int> outer, List<List<int>> holes, Vector3 normal, List<Added>? added, bool light, Mode mode, out string path)
    {
        path = "as is";
        outer = Clean(outer);
        holes = holes.Select(Clean).Where(h => h.Count >= 3).ToList();
        if (outer.Count < 3) return new();
        if (!Finite(normal) || normal.LengthSquared() < 1e-20f || outer.Concat(holes.SelectMany(h => h)).Any(v => v < 0 || v >= pos.Count || !Finite(pos[v])))
        { path = "invalid region"; return new(); }
        var plane = new Frame(pos, normal, outer);
        if (Math.Abs(plane.Area(outer)) < 1e-14) return new();
        if (holes.Any(h => h.Any(v => !plane.Inside(outer,plane.P(v)))))
        { path = "hole outside region"; return new(); }
        // Worked on with the outline counter-clockwise and holes clockwise, each loop starting at its lowest point and the
        // holes in that order: with the frame above, the result depends only on the shape, never on how its points are
        // numbered, so a face and its mirror twin are filled alike. Faces are turned back at the end if need be.
        bool turned = plane.Area(outer) < 0;
        if (turned) outer = Enumerable.Reverse(outer).ToList();
        outer = plane.Lowest(outer);
        holes = holes.Select(h => plane.Lowest(plane.Area(h) > 0 ? Enumerable.Reverse(h).ToList() : h))
                     .OrderBy(h => plane.P(h[0]).X).ThenBy(h => plane.P(h[0]).Y).ToList();
        string how = "as is";
        var rim = outer.Concat(holes.SelectMany(h => h)).ToList();
        var loops = holes.Select(h => h.Count).Prepend(outer.Count).ToArray();
        int posBefore = pos.Count, addedBefore = added?.Count ?? 0;
        var faces = Recall(pos, plane, rim, loops, added, light, mode, ref how) ?? Remember(Faces(), pos, plane, rim, loops, posBefore, added, addedBefore, light, mode, how);
        path = how;
        return turned ? faces.Select(f => f.Reverse().ToArray()).ToList() : faces;

        List<int[]> Faces()
        {
            if (holes.Count == 0 && outer.Count <= 4 && plane.StrictlyConvex(outer)) return new() { outer.ToArray() };
            if (added != null && mode == Mode.Rectangle && holes.Count > 0)
            {
                var box = RectangleBox(pos, plane, outer, holes, added);
                if (box != null) { how = "rectangle box"; return box; }
            }
            var tris = Delaunay(plane, outer, holes);
            how = "delaunay";
            if (added != null && mode != Mode.Rectangle && tris.Count > 0)
            {
                int before = pos.Count;
                tris = Refine(pos, plane, tris, outer, holes, added, light);
                how = $"quality mesh, {pos.Count - before} points";
            }
            return PairUp(plane, tris, Boundary(outer, holes));
        }
    }

    // ---------- mirror twins ----------

    /// A recent fill: its outline in its frame's 2D coordinates, its faces (numbered: outline points first, then its added
    /// points), the added points and what each blends from (outline numbers).
    sealed record Memo(Vector2[] Rim, int[] Loops, List<int[]> Faces, Vector2[] Points, (int V, float W)[][] Blends, bool Adds, bool Light, Mode Mode, string How);
    static readonly List<Memo> memos = new();

    /// A region whose outline matches one filled recently (a mirror twin has the very same 2D coordinates in its frame)
    /// gets the same faces: the two sides of a vehicle then match exactly, even where rounding in a cut left their
    /// outlines a hair apart and a near tie went the other way.
    static List<int[]>? Recall(List<Vector3> pos, Frame plane, List<int> rim, int[] loops, List<Added>? added, bool light, Mode mode, ref string how)
    {
        var at = rim.Select(plane.P).ToArray();
        Memo[] recent;
        lock (memos) recent = memos.ToArray();
        foreach (var m in recent)
        {
            if (!m.Loops.SequenceEqual(loops) || m.Adds != (added != null) || m.Light != light || m.Mode != mode) continue;
            var map = new int[at.Length];
            bool same = true;
            for (int j = 0; j < at.Length && same; j++)
            {
                map[j] = Array.FindIndex(at, p => (p - m.Rim[j]).LengthSquared() < 1e-10f);
                same = map[j] >= 0;
            }
            if (!same || map.Distinct().Count() != map.Length) continue;
            int first = pos.Count;
            for (int k = 0; k < m.Points.Length; k++)
            {
                pos.Add(plane.At(m.Points[k]));
                added!.Add(new Added(pos[^1], m.Blends[k].Select(b => (rim[map[b.V]], b.W)).ToArray()));
            }
            how = m.How + " (as a recent fill of the same outline, e.g. its mirror twin)";
            return m.Faces.Select(f => f.Select(i => i < at.Length ? rim[map[i]] : first + i - at.Length).ToArray()).ToList();
        }
        return null;
    }

    static List<int[]> Remember(List<int[]> faces, List<Vector3> pos, Frame plane, List<int> rim, int[] loops, int first, List<Added>? added, int addedBefore, bool light, Mode mode, string how)
    {
        var mine = added?.Skip(addedBefore).ToList();
        int count = pos.Count - first;
        if (count != (mine?.Count ?? 0)) return faces;
        var slot = new Dictionary<int, int>();
        for (int j = 0; j < rim.Count; j++) slot.TryAdd(rim[j], j);
        int Slot(int v) => v >= first ? rim.Count + v - first : slot.GetValueOrDefault(v, -1);
        // Only a fill made purely of its outline and its own added points can be replayed.
        if (faces.Any(f => f.Any(v => Slot(v) < 0)) || mine != null && mine.Any(a => a.Blend.Any(b => !slot.ContainsKey(b.V)))) return faces;
        lock (memos)
        {
            if (memos.Count >= 64) memos.RemoveAt(0);
            memos.Add(new Memo(rim.Select(plane.P).ToArray(), loops, faces.Select(f => f.Select(Slot).ToArray()).ToList(),
                Enumerable.Range(first, count).Select(plane.P).ToArray(), mine?.Select(a => a.Blend.Select(b => (slot[b.V], b.W)).ToArray()).ToArray() ?? Array.Empty<(int, float)[]>(),
                added != null, light, mode, how));
        }
        return faces;
    }

    // ---------- rectangle box ----------

    /// Surrounds the hole with a clean rectangular box: a 4-corner rectangle enclosing the cut,
    /// so the region between the hole and the rectangle is triangulated/quad-paired, and the region outside the
    /// rectangle connects the 4 rectangle corners cleanly to the outer plate corners.
    static List<int[]>? RectangleBox(List<Vector3> pos, Frame plane, List<int> outer, List<List<int>> holes, List<Added> added)
    {
        if (holes.Count == 0) return null;
        var allHoleVerts = holes.SelectMany(h => h).Distinct().ToList();
        if (allHoleVerts.Count < 3) return null;

        var angles = new List<float> { 0f };
        foreach (var h in holes)
            for (int i = 0; i < h.Count; i++)
            {
                var d = plane.P(h[(i + 1) % h.Count]) - plane.P(h[i]);
                if (d.LengthSquared() > 1e-8f) angles.Add(MathF.Atan2(d.Y, d.X));
            }
        for (int i = 0; i < outer.Count; i++)
        {
            var d = plane.P(outer[(i + 1) % outer.Count]) - plane.P(outer[i]);
            if (d.LengthSquared() > 1e-8f) angles.Add(MathF.Atan2(d.Y, d.X));
        }

        // Test angle 0 first (axis-aligned rectangle parallel to plate axes), then candidate angles.
        var candidateAngles = angles.Select(NormAngle).Distinct().OrderBy(a => MathF.Abs(a) < 1e-3f ? 0 : 1).ToList();

        Vector2[]? validBox = null;
        foreach (var a in candidateAngles)
        {
            float cos = MathF.Cos(a), sin = MathF.Sin(a);
            float minX = float.MaxValue, maxX = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;
            foreach (int v in allHoleVerts)
            {
                var p = plane.P(v);
                float rx = p.X * cos + p.Y * sin;
                float ry = -p.X * sin + p.Y * cos;
                if (rx < minX) minX = rx; if (rx > maxX) maxX = rx;
                if (ry < minY) minY = ry; if (ry > maxY) maxY = ry;
            }

            float width = maxX - minX, height = maxY - minY;
            if (width <= 1e-4f || height <= 1e-4f) continue;
            float size = MathF.Max(width, height);

            foreach (float scale in new[] { 0.10f, 0.15f, 0.20f, 0.08f, 0.05f, 0.03f })
            {
                float m = MathF.Max(0.015f, scale * size);
                var c0 = new Vector2(minX - m, minY - m);
                var c1 = new Vector2(maxX + m, minY - m);
                var c2 = new Vector2(maxX + m, maxY + m);
                var c3 = new Vector2(minX - m, maxY + m);
                Vector2 RotBack(Vector2 c) => new(c.X * cos - c.Y * sin, c.X * sin + c.Y * cos);
                var box = new[] { RotBack(c0), RotBack(c1), RotBack(c2), RotBack(c3) };

                if (Cross(box[1] - box[0], box[2] - box[0]) <= 1e-8) continue;
                if (box.Any(b => !plane.Inside(outer, b))) continue;
                if (box.Any(b => holes.Any(hole => plane.Inside(hole, b)))) continue;

                bool edgeCross = false;
                for (int k = 0; k < 4 && !edgeCross; k++)
                {
                    var b1 = box[k]; var b2 = box[(k + 1) % 4];
                    for (int j = 0; j < outer.Count && !edgeCross; j++)
                    {
                        var o1 = plane.P(outer[j]); var o2 = plane.P(outer[(j + 1) % outer.Count]);
                        if (SegmentsCross(b1, b2, o1, o2)) edgeCross = true;
                    }
                }
                if (edgeCross) continue;

                for (int k = 0; k < 4 && !edgeCross; k++)
                {
                    var b1 = box[k]; var b2 = box[(k + 1) % 4];
                    foreach (var hole in holes)
                    {
                        for (int j = 0; j < hole.Count && !edgeCross; j++)
                        {
                            var h1 = plane.P(hole[j]); var h2 = plane.P(hole[(j + 1) % hole.Count]);
                            if (SegmentsCross(b1, b2, h1, h2)) edgeCross = true;
                        }
                        if (edgeCross) break;
                    }
                }
                if (edgeCross) continue;

                bool allInside = true;
                foreach (int v in allHoleVerts)
                {
                    var p = plane.P(v);
                    for (int k = 0; k < 4; k++)
                    {
                        if (Cross(box[(k + 1) % 4] - box[k], p - box[k]) <= 1e-4) { allInside = false; break; }
                    }
                    if (!allInside) break;
                }
                if (!allInside) continue;

                validBox = box;
                break;
            }

            if (validBox != null) break;
        }

        if (validBox == null) return null;

        int mark = pos.Count, addedMark = added.Count;
        var boxIndices = new List<int>();
        for (int k = 0; k < 4; k++)
        {
            var p2 = validBox[k];
            var p3 = plane.At(p2);
            var nearest = outer.OrderBy(v => Vector2.DistanceSquared(plane.P(v), p2)).Take(4).ToList();
            var blend = nearest.Select(v => (v, 1f / MathF.Max(1e-4f, Vector2.Distance(plane.P(v), p2)))).ToArray();
            float sumW = blend.Sum(b => b.Item2);
            var normBlend = blend.Select(b => (b.v, b.Item2 / sumW)).ToArray();
            pos.Add(p3);
            added.Add(new Added(p3, normBlend));
            boxIndices.Add(pos.Count - 1);
        }

        var innerTris = Delaunay(plane, boxIndices, holes);
        var innerFaces = PairUp(plane, innerTris, Boundary(boxIndices, holes));

        var boxHole = new List<List<int>> { Enumerable.Reverse(boxIndices).ToList() };
        var outerTris = Delaunay(plane, outer, boxHole);
        var outerFaces = PairUp(plane, outerTris, Boundary(outer, boxHole));

        if (innerFaces == null || outerFaces == null || innerTris.Any(t => plane.Area(t) <= 1e-12) || outerTris.Any(t => plane.Area(t) <= 1e-12))
        {
            pos.RemoveRange(mark, pos.Count - mark);
            added.RemoveRange(addedMark, added.Count - addedMark);
            return null;
        }

        double wantArea = plane.Area(outer) + holes.Sum(h => plane.Area(h));
        double gotArea = innerFaces.Sum(f => plane.Area(f)) + outerFaces.Sum(f => plane.Area(f));
        if (Math.Abs(gotArea - wantArea) > 1e-5)
        {
            pos.RemoveRange(mark, pos.Count - mark);
            added.RemoveRange(addedMark, added.Count - addedMark);
            return null;
        }

        return innerFaces.Concat(outerFaces).ToList();
    }

    static float NormAngle(float a)
    {
        while (a < 0) a += MathF.PI;
        while (a >= MathF.PI / 2f) a -= MathF.PI / 2f;
        return a;
    }

    static int[] Turned(Frame plane, params int[] tri) => plane.Area(tri) >= 0 ? tri : new[] { tri[0], tri[2], tri[1] };

    // ---------- quality mesh ----------

    /// Delaunay refinement (Ruppert/Chew, with Üngör's off-centres, as in Shewchuk's Triangle): wherever a triangle has
    /// an angle under the target, a point goes inside the region where it makes that triangle good. Points never go on
    /// the outline (neighbouring faces share it) or so near an outline edge that its triangle would be thin. Triangles
    /// then grade from the outline's short edges out to its long ones, with no fans.
    static List<int[]> Refine(List<Vector3> pos, Frame plane, List<int[]> start, List<int> outer, List<List<int>> holes, List<Added> added, bool light)
    {
        double theta = (light ? 22 : 28) * Math.PI / 180, ratio = 1 / (2 * Math.Sin(theta));
        // A point seeing an outline edge wider than this would make the triangle on that edge thin. The outline can't be
        // split here, so a long edge has to accept a somewhat flatter triangle than the rest, or nothing could go near it.
        double lens = Math.PI - 2 * 0.65 * theta;
        var fixedEdges = Boundary(outer, holes);
        // Outline edges in the direction that keeps the region on their left (outer counter-clockwise, holes clockwise).
        var sides = holes.Append(outer).SelectMany(l => l.Select((v, k) => (v, l[(k + 1) % l.Count]))).ToList();
        var rim = outer.Concat(holes.SelectMany(h => h)).Distinct().ToList();
        double finest = fixedEdges.Min(e => (plane.P(e.Item1) - plane.P(e.Item2)).Length());
        double scale = finest * finest * 1e-6;
        // The region's own corner angles: a corner sharper than the target can't be helped by adding points.
        var corner = new Dictionary<int, double>();
        foreach (var loop in holes.Append(outer))
            for (int k = 0; k < loop.Count; k++)
            {
                Vector2 v = plane.P(loop[k]), next = plane.P(loop[(k + 1) % loop.Count]) - v, prev = plane.P(loop[(k - 1 + loop.Count) % loop.Count]) - v;
                double a = Math.Atan2(Cross(next, prev), Vector2.Dot(next, prev));
                corner[loop[k]] = Math.Min(corner.GetValueOrDefault(loop[k], 2 * Math.PI), a < 0 ? a + 2 * Math.PI : a);
            }
        // Triangles (counter-clockwise; null once replaced) and which triangle owns each directed edge.
        var tris = new List<int[]?>();
        var owner = new Dictionary<(int, int), int>();
        foreach (var t in start) Add(plane.Area(t) >= 0 ? t : new[] { t[0], t[2], t[1] });
        var skip = new HashSet<(int, int, int)>();
        int first = pos.Count, firstAdded = added.Count;
        // A region that is its own mirror image (lying across the part's centre line, which is v = 0 in this frame) gets
        // every added point with its mirror twin, so the editor's Mirror still pairs them.
        static Vector2 Flip(Vector2 p) => new(p.X, -p.Y);
        bool symmetric = rim.All(v => rim.Any(w => (plane.P(w) - Flip(plane.P(v))).Length() < 2e-5f));
        // Smooth: first a layer of points just inside every short-edged (curved) stretch of outline, one per outline
        // point, so the faces along a hole's or cut's rim come out as a ring of quads.
        if (!light)
            foreach (var loop in holes.Append(outer))
                for (int k = 0; k < loop.Count; k++)
                {
                    Vector2 o = plane.P(loop[k]), next = plane.P(loop[(k + 1) % loop.Count]), prev = plane.P(loop[(k - 1 + loop.Count) % loop.Count]);
                    float step = 0.8f * Math.Min((next - o).Length(), (prev - o).Length());
                    if (step < 1e-9f) continue;
                    var inward = Left(o - prev) + Left(next - o);
                    if (inward.LengthSquared() < 1e-12f) continue;
                    var x = o + Vector2.Normalize(inward) * (float)(step / Math.Max(0.5, Math.Sin(corner[loop[k]] / 2)));
                    if (symmetric && Math.Abs(x.Y) < 0.3 * step) x.Y = 0;
                    if (owner.Keys.All(e => (plane.P(e.Item1) - x).Length() > 0.5 * step)) Place(x);
                }
        // Biggest thin triangle first: the long slivers that show go first, so a tiny hole in a big plate isn't graded out
        // with hundreds of points. Points are capped to a share of the outline's (the fine ones by its rim may stay).
        int room = light ? rim.Count / 2 + 8 : rim.Count + 16, placed = pos.Count;
        for (int guard = 0; guard < 3000 && pos.Count - placed < room; guard++)
        {
            int worst = -1; double biggest = 0;
            for (int t = 0; t < tris.Count; t++)
            {
                if (tris[t] is not { } tri) continue;
                var (r, shortest, at) = Shape(tri);
                if (r <= ratio || r * shortest <= biggest || shortest < 0.5 * finest || skip.Contains(Sorted(tri))) continue;
                if (corner.TryGetValue(at, out var input) && input < 1.05 * theta) continue; // a sharp corner of the outline
                (worst, biggest) = (t, r * shortest);
            }
            if (worst < 0) break;
            var x = OffCentre(tris[worst]!);
            if (symmetric && Math.Abs(x.Y) < 0.3 * Shape(tris[worst]!).Shortest) x.Y = 0; // on the centre line, not a hair off it
            // Too near an outline edge: Ruppert would split that edge, but neighbouring faces share it, so give it a
            // good apex instead (if nothing already sits in its way).
            if (Encroached(x) is { } side)
                x = Clear(side) ? new[] { 60.0, 45, theta * 180 / Math.PI + 4 }.Select(b => Apex(side, b)).FirstOrDefault(y => Encroached(y) == null, new(float.NaN)) : new(float.NaN);
            if (float.IsFinite(x.X) && Place(x)) continue;
            skip.Add(Sorted(tris[worst]!));
        }
        Smooth();
        return tris.Where(t => t != null).ToList()!;

        // Each added point moves toward the middle of its neighbours when that leaves every triangle round it better (its
        // smallest angle no smaller), then edges are flipped back to Delaunay; a few rounds even out the spacing.
        void Smooth()
        {
            // Mirror twins move together (a point on the centre line stays on it).
            var twin = new Dictionary<int, int>();
            if (symmetric)
                for (int v = first; v < pos.Count; v++)
                {
                    var want = Flip(plane.P(v));
                    int w = Enumerable.Range(first, pos.Count - first).OrderBy(i => (plane.P(i) - want).LengthSquared()).First();
                    if ((plane.P(w) - want).Length() < 0.1 * finest) twin[v] = w;
                }
            for (int round = 0; round < 6; round++)
            {
                var star = new Dictionary<int, List<int>>();
                for (int t = 0; t < tris.Count; t++)
                    if (tris[t] is { } tri) foreach (int v in tri) if (v >= first) { if (!star.TryGetValue(v, out var l)) star[v] = l = new(); l.Add(t); }
                foreach (var (v, around) in star)
                {
                    var ring = around.SelectMany(t => tris[t]!).Where(w => w != v).Distinct().ToList();
                    var target = ring.Aggregate(Vector2.Zero, (a, w) => a + plane.P(w)) / ring.Count;
                    int w = twin.GetValueOrDefault(v, -1);
                    if (w == v) target.Y = 0;
                    var moving = w >= 0 && w != v ? new[] { (v, target), (w, Flip(target)) } : new[] { (v, target) };
                    var near = moving.SelectMany(m => star.GetValueOrDefault(m.Item1, new())).Distinct().ToList();
                    var was = moving.Select(m => pos[m.Item1]).ToArray();
                    double before = near.Min(t => SmallestAngle(tris[t]!));
                    foreach (var (i, to) in moving) pos[i] = plane.At(to);
                    if (near.Any(t => plane.Area(tris[t]!) <= scale) || near.Min(t => SmallestAngle(tris[t]!)) < before)
                        for (int k = 0; k < moving.Length; k++) pos[moving[k].Item1] = was[k];
                }
                for (int sweep = 0, flipped = 1; flipped > 0 && sweep < 20; sweep++)
                {
                    flipped = 0;
                    foreach (var ((u, v), mine) in owner.ToList())
                    {
                        if (tris[mine] == null || fixedEdges.Contains(Key(u, v)) || !owner.TryGetValue((v, u), out int across)) continue;
                        int p = tris[mine]!.First(w => w != u && w != v), d = tris[across]!.First(w => w != u && w != v);
                        if (!Inside(tris[mine]!, d) || !plane.StrictlyConvex(new[] { u, d, v, p })) continue;
                        Kill(mine); Kill(across);
                        Add(new[] { u, d, p }); Add(new[] { d, v, p });
                        flipped++;
                    }
                }
            }
            for (int i = firstAdded; i < added.Count; i++) added[i] = added[i] with { P = pos[first + i - firstAdded] };
        }

        // d clearly inside the triangle's circumcircle (a tolerance relative to its size, so four points on one circle
        // never flip back and forth).
        bool Inside(int[] t, int d)
        {
            Vector2 o = plane.P(d), a = plane.P(t[0]) - o, b = plane.P(t[1]) - o, c = plane.P(t[2]) - o;
            double aa = a.LengthSquared(), bb = b.LengthSquared(), cc = c.LengthSquared(), big = Math.Max(aa, Math.Max(bb, cc));
            double det = aa * Cross(b, c) - bb * Cross(a, c) + cc * Cross(a, b);
            return det > 1e-7 * big * big;
        }

        double SmallestAngle(int[] t)
        {
            double least = Math.PI;
            for (int k = 0; k < 3; k++)
            {
                Vector2 o = plane.P(t[k]), a = plane.P(t[(k + 1) % 3]) - o, b = plane.P(t[(k + 2) % 3]) - o;
                least = Math.Min(least, Math.Atan2(Math.Abs(Cross(a, b)), Vector2.Dot(a, b)));
            }
            return least;
        }

        void Add(int[] t)
        {
            for (int k = 0; k < 3; k++) owner[(t[k], t[(k + 1) % 3])] = tris.Count;
            tris.Add(t);
        }
        void Kill(int i)
        {
            var t = tris[i]!;
            for (int k = 0; k < 3; k++) owner.Remove((t[k], t[(k + 1) % 3]));
            tris[i] = null;
        }

        // Circumradius over shortest edge, the shortest edge, and the corner with the smallest angle (opposite it).
        (double Ratio, double Shortest, int At) Shape(int[] t)
        {
            Vector2 a = plane.P(t[0]), b = plane.P(t[1]), c = plane.P(t[2]);
            double ab = (b - a).Length(), bc = (c - b).Length(), ca = (a - c).Length(), area = Math.Abs(Cross(b - a, c - a)) / 2;
            double shortest = Math.Min(ab, Math.Min(bc, ca));
            int at = shortest == ab ? t[2] : shortest == bc ? t[0] : t[1];
            return (area < 1e-20 ? double.MaxValue : ab * bc * ca / (4 * area) / shortest, shortest, at);
        }

        // On the shortest edge's bisector, toward the circumcentre, only as far as makes a triangle exactly at the target.
        Vector2 OffCentre(int[] t)
        {
            Vector2 a = plane.P(t[0]), b = plane.P(t[1]), c = plane.P(t[2]);
            var (p, q) = new[] { (a, b), (b, c), (c, a) }.OrderBy(e => (e.Item1 - e.Item2).LengthSquared()).First();
            var cc = Circumcentre(a, b, c);
            var m = (p + q) / 2;
            double reach = (q - p).Length() / 2 / Math.Tan(theta / 2), far = (cc - m).Length();
            return far <= reach ? cc : m + (cc - m) * (float)(reach / far);
        }

        // The outline edge x would make the triangle on worse: x sees it wider than the lens allows, and wider than the
        // corner already opposite it (that corner stays, being the one that sees it widest, so a point seeing it
        // narrower changes nothing there).
        (int, int)? Encroached(Vector2 x)
        {
            foreach (var (u, w) in sides)
            {
                double seen = Seen(u, w, x);
                if (seen <= lens) continue;
                if (owner.TryGetValue((u, w), out int t) && tris[t] is { } tri && seen < Seen(u, w, plane.P(tri.First(v => v != u && v != w)))) continue;
                return (u, w);
            }
            return null;
        }

        double Seen(int u, int w, Vector2 x)
        {
            Vector2 a = plane.P(u) - x, b = plane.P(w) - x;
            return Math.Atan2(Math.Abs(Cross(a, b)), Vector2.Dot(a, b));
        }

        // No vertex already in the edge's lens (that one would be its triangle's corner whatever we add).
        bool Clear((int U, int W) side) => !owner.Keys.Select(e => e.Item1).Distinct().Any(v =>
        {
            if (v == side.U || v == side.W) return false;
            Vector2 a = plane.P(side.U) - plane.P(v), b = plane.P(side.W) - plane.P(v);
            return Cross(plane.P(side.W) - plane.P(side.U), plane.P(v) - plane.P(side.U)) > 0 && Math.Atan2(Math.Abs(Cross(a, b)), Vector2.Dot(a, b)) > lens;
        });

        // The point inside the region making an isosceles triangle on the edge with base angles of `degrees`.
        Vector2 Apex((int U, int W) side, double degrees)
        {
            Vector2 a = plane.P(side.U), b = plane.P(side.W), along = b - a;
            var inward = Vector2.Normalize(new Vector2(-along.Y, along.X)); // the region is on each outline edge's left
            return (a + b) / 2 + inward * (float)(along.Length() / 2 * Math.Tan(degrees * Math.PI / 180));
        }

        // Split the triangle holding x into three (or, with x on one of its edges, both triangles there into two), then
        // flip edges (never the outline's) until Delaunay again.
        // The triangle holding x, and which of its edges x is on (-1: none); null if x is outside, on the outline or too
        // near a corner. Strictly inside a triangle first; failing that, on an edge (a hair outside both in float maths).
        (int Home, int OnEdge)? Locate(Vector2 x)
        {
            foreach (double slack in new[] { 0, -0.01 })
                for (int t = 0; t < tris.Count; t++)
                {
                    if (tris[t] is not { } tri) continue;
                    double size = Enumerable.Range(0, 3).Min(k => (plane.P(tri[(k + 1) % 3]) - plane.P(tri[k])).Length());
                    var side = Enumerable.Range(0, 3).Select(k => Cross(plane.P(tri[(k + 1) % 3]) - plane.P(tri[k]), x - plane.P(tri[k])) / (plane.P(tri[(k + 1) % 3]) - plane.P(tri[k])).Length() / size).ToArray();
                    if (side.Any(d => d <= slack)) continue;
                    if (tri.Any(v => (plane.P(v) - x).Length() < 0.2 * size)) return null;
                    int k0 = Array.IndexOf(side, side.Min());
                    if (side[k0] >= 0.01) return (t, -1);
                    // On an edge: it goes onto the edge, which must be an inner one, well between its ends.
                    Vector2 a = plane.P(tri[k0]), ab = plane.P(tri[(k0 + 1) % 3]) - a;
                    float along = Vector2.Dot(x - a, ab) / ab.LengthSquared();
                    return fixedEdges.Contains(Key(tri[k0], tri[(k0 + 1) % 3])) || along < 0.1f || along > 0.9f ? null : (t, k0);
                }
            return null;
        }

        // Adds x, and in a region that is its own mirror image its mirror twin too: both or neither.
        bool Place(Vector2 x)
        {
            bool twin = symmetric && x.Y != 0;
            if (Encroached(x) != null || twin && Encroached(Flip(x)) != null) return false;
            var keep = twin ? (Tris: tris.ToList(), Owner: owner.ToList(), Pos: pos.Count, Added: added.Count) : default;
            if (!Insert(x)) return false;
            if (!twin || Insert(Flip(x))) return true;
            tris.Clear(); tris.AddRange(keep.Tris);
            owner.Clear(); foreach (var (e, t) in keep.Owner) owner[e] = t;
            pos.RemoveRange(keep.Pos, pos.Count - keep.Pos);
            added.RemoveRange(keep.Added, added.Count - keep.Added);
            return false;
        }

        bool Insert(Vector2 x)
        {
            if (Locate(x) is not var (home, onEdge)) return false;
            var h = tris[home]!;
            var todo = new Stack<(int, int)>();
            int p = pos.Count;
            if (onEdge >= 0)
            {
                int a = h[onEdge], b = h[(onEdge + 1) % 3], c = h[(onEdge + 2) % 3];
                if (fixedEdges.Contains(Key(a, b)) || !owner.TryGetValue((b, a), out int across)) return false;
                int d = tris[across]!.First(w => w != a && w != b);
                Vector2 pa = plane.P(a), ab = plane.P(b) - pa;
                x = pa + ab * (Vector2.Dot(x - pa, ab) / ab.LengthSquared());
                pos.Add(plane.At(x));
                Kill(home); Kill(across);
                Add(new[] { a, p, c }); Add(new[] { p, b, c }); Add(new[] { b, p, d }); Add(new[] { p, a, d });
                foreach (var e in new[] { (b, c), (c, a), (a, d), (d, b) }) todo.Push(e);
            }
            else
            {
                pos.Add(plane.At(x));
                Kill(home);
                Add(new[] { h[0], h[1], p }); Add(new[] { h[1], h[2], p }); Add(new[] { h[2], h[0], p });
                foreach (var e in new[] { (h[0], h[1]), (h[1], h[2]), (h[2], h[0]) }) todo.Push(e);
            }
            for (int flips = 0; todo.Count > 0 && flips < 4 * tris.Count; flips++)
            {
                var (u, v) = todo.Pop();
                if (fixedEdges.Contains(Key(u, v)) || !owner.TryGetValue((u, v), out int mine) || !owner.TryGetValue((v, u), out int across)) continue;
                int d = tris[across]!.First(w => w != u && w != v);
                if (!Inside(tris[mine]!, d) || !plane.StrictlyConvex(new[] { u, d, v, p })) continue;
                Kill(mine); Kill(across);
                Add(new[] { u, d, p }); Add(new[] { d, v, p });
                todo.Push((u, d)); todo.Push((d, v));
            }
            var near = rim.OrderBy(v => (plane.P(v) - x).LengthSquared()).Take(3).Select(v => (v, 1 / Math.Max(1e-6f, (plane.P(v) - x).Length()))).ToArray();
            float total = near.Sum(n => n.Item2);
            added.Add(new Added(pos[p], near.Select(n => (n.v, n.Item2 / total)).ToArray()));
            return true;
        }
    }

    static Vector2 Left(Vector2 e) => e.LengthSquared() < 1e-24f ? Vector2.Zero : Vector2.Normalize(new Vector2(-e.Y, e.X));

    static (int, int, int) Sorted(int[] t)
    {
        var s = t.OrderBy(v => v).ToArray();
        return (s[0], s[1], s[2]);
    }

    static Vector2 Circumcentre(Vector2 a, Vector2 b, Vector2 c)
    {
        Vector2 ab = b - a, ac = c - a;
        double d = 2 * Cross(ab, ac);
        double x = (ac.Y * ab.LengthSquared() - ab.Y * ac.LengthSquared()) / d, y = (ab.X * ac.LengthSquared() - ac.X * ab.LengthSquared()) / d;
        return a + new Vector2((float)x, (float)y);
    }

    // ---------- general case ----------

    /// Ear-clipped (holes bridged in), then edges flipped until Delaunay (no needle triangles where avoidable).
    static List<int[]> Delaunay(Frame plane, List<int> outer, List<List<int>> holes)
    {
        var poly = outer.ToList();
        foreach (var hole in holes.OrderByDescending(h => h.Max(v => plane.P(v).X)))
        {
            var best = (d: double.MaxValue, i: -1, j: -1);
            for (int i = 0; i < hole.Count; i++)
                for (int j = 0; j < poly.Count; j++)
                {
                    double d = Vector2.DistanceSquared(plane.P(hole[i]), plane.P(poly[j]));
                    if (d < best.d && Visible(plane, poly, holes, hole[i], poly[j])) best = (d, i, j);
                }
            // Leaving an unbridgeable hole out would fill across it and erase the cut.
            if (best.i < 0) return new();
            // A corner an earlier bridge joined is in the outline twice: join at the copy whose side the hole is on.
            var q = plane.P(hole[best.i]);
            best.j = Enumerable.Range(0, poly.Count).FirstOrDefault(j => poly[j] == poly[best.j] && Opens(plane, poly, j, q), best.j);
            var spliced = poly.Take(best.j + 1).ToList();
            spliced.AddRange(Enumerable.Range(0, hole.Count + 1).Select(k => hole[(best.i + k) % hole.Count]));
            spliced.AddRange(poly.Skip(best.j));
            poly = spliced;
        }
        var tris = new List<int[]>();
        var rest = poly.ToList();
        for (int guard = 0; rest.Count > 3 && guard < 10000; guard++)
        {
            int ear = -1;
            for (int i = 0; i < rest.Count && ear < 0; i++)
                if (IsEar(plane, rest, i)) ear = i;
            if (ear < 0) ear = Enumerable.Range(0, rest.Count).OrderByDescending(i => Turn(plane, rest, i)).First(); // degenerate leftovers
            tris.Add(new[] { rest[(ear - 1 + rest.Count) % rest.Count], rest[ear], rest[(ear + 1) % rest.Count] });
            rest.RemoveAt(ear);
        }
        if (rest.Count == 3) tris.Add(rest.ToArray());
        tris.RemoveAll(t => Math.Abs(plane.Area(t)) < 1e-14);

        var fixedEdges = Boundary(outer, holes);
        for (int pass = 0; pass < 5000; pass++)
        {
            bool flipped = false;
            var byEdge = EdgeMap(tris);
            foreach (var (edge, list) in byEdge)
            {
                if (list.Count != 2 || fixedEdges.Contains(edge)) continue;
                var (a, b) = (tris[list[0]], tris[list[1]]);
                int pa = a.First(v => v != edge.Item1 && v != edge.Item2), pb = b.First(v => v != edge.Item1 && v != edge.Item2);
                if (a.Contains(pb) || b.Contains(pa)) continue;
                if (!InCircle(plane, a, pb)) continue;
                var quad = new[] { pa, edge.Item1, pb, edge.Item2 };
                if (!plane.StrictlyConvex(Turned4(plane, quad))) continue;
                tris[list[0]] = Turned(plane, pa, pb, edge.Item1);
                tris[list[1]] = Turned(plane, pa, pb, edge.Item2);
                flipped = true;
                break;
            }
            if (!flipped) break;
        }
        return tris;
    }

    static int[] Turned4(Frame plane, int[] q) => plane.Area(q) >= 0 ? q : q.Reverse().ToArray();

    static bool InCircle(Frame plane, int[] tri, int d)
    {
        var t = Turned(plane, tri);
        Vector2 a = plane.P(t[0]) - plane.P(d), b = plane.P(t[1]) - plane.P(d), c = plane.P(t[2]) - plane.P(d);
        double det = (a.X * a.X + a.Y * a.Y) * ((double)b.X * c.Y - (double)c.X * b.Y)
                   - (b.X * b.X + b.Y * b.Y) * ((double)a.X * c.Y - (double)c.X * a.Y)
                   + (c.X * c.X + c.Y * c.Y) * ((double)a.X * b.Y - (double)b.X * a.Y);
        return det > 1e-18;
    }

    static bool IsEar(Frame plane, List<int> p, int i)
    {
        int a = p[(i - 1 + p.Count) % p.Count], b = p[i], c = p[(i + 1) % p.Count];
        if (Turn(plane, p, i) <= 1e-14) return false;
        Vector2 pa = plane.P(a), pb = plane.P(b), pc = plane.P(c);
        for (int k = 0; k < p.Count; k++)
        {
            int v = p[k];
            if (v == a || v == b || v == c) continue;
            var x = plane.P(v);
            if (x == pa || x == pb || x == pc) continue;
            if (Cross(pb - pa, x - pa) >= -1e-14 && Cross(pc - pb, x - pb) >= -1e-14 && Cross(pa - pc, x - pc) >= -1e-14) return false;
        }
        return true;
    }

    static double Turn(Frame plane, List<int> p, int i) =>
        Cross(plane.P(p[i]) - plane.P(p[(i - 1 + p.Count) % p.Count]), plane.P(p[(i + 1) % p.Count]) - plane.P(p[i]));

    /// A bridge from a hole vertex to a polygon vertex must not cross any edge.
    static bool Visible(Frame plane, List<int> poly, List<List<int>> holes, int from, int to)
    {
        Vector2 a = plane.P(from), b = plane.P(to);
        foreach (var loop in holes.Append(poly))
            for (int k = 0; k < loop.Count; k++)
            {
                int u = loop[k], w = loop[(k + 1) % loop.Count];
                if (u == from || u == to || w == from || w == to) continue;
                if (SegmentsCross(a, b, plane.P(u), plane.P(w))) return false;
            }
        return true;
    }

    /// Whether q lies inside the outline's corner j: turning left from the edge out round to the edge in.
    static bool Opens(Frame plane, List<int> poly, int j, Vector2 q)
    {
        Vector2 v = plane.P(poly[j]), a = plane.P(poly[(j + 1) % poly.Count]) - v, b = plane.P(poly[(j - 1 + poly.Count) % poly.Count]) - v, d = q - v;
        return Cross(a, b) > 0 ? Cross(a, d) > 0 && Cross(d, b) > 0 : Cross(a, d) > 0 || Cross(d, b) > 0;
    }

    static bool SegmentsCross(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        double d1 = Cross(b - a, c - a), d2 = Cross(b - a, d - a), d3 = Cross(d - c, a - c), d4 = Cross(d - c, b - c);
        return ((d1 > 0) != (d2 > 0)) && ((d3 > 0) != (d4 > 0));
    }

    // ---------- quads ----------

    /// Pairs triangles sharing an inner edge into strictly convex quads, squarest first.
    static List<int[]> PairUp(Frame plane, List<int[]> faces, HashSet<(int, int)> boundary)
    {
        var tris = faces.Where(f => f.Length == 3).ToList();
        var others = faces.Where(f => f.Length != 3).ToList();
        var pairs = new List<(int A, int B, int[] Quad, double Score)>();
        foreach (var (edge, list) in EdgeMap(tris))
        {
            if (list.Count != 2 || boundary.Contains(edge)) continue;
            var (a, b) = (tris[list[0]], tris[list[1]]);
            int pa = a.First(v => v != edge.Item1 && v != edge.Item2), pb = b.First(v => v != edge.Item1 && v != edge.Item2);
            // Walk a from the vertex after the shared edge: a = (x, u, w) with u->w shared; quad = x, u, pb, w.
            int ia = Array.IndexOf(a, pa);
            var quad = new[] { a[ia], a[(ia + 1) % 3], pb, a[(ia + 2) % 3] };
            if (plane.Area(quad) <= 0 || !plane.StrictlyConvex(quad)) continue;
            pairs.Add((list[0], list[1], quad, plane.Squareness(quad)));
        }
        var match = Enumerable.Repeat(-1, tris.Count).ToArray();
        foreach (var (a, b, _, _) in pairs.OrderByDescending(p => p.Score))
            if (match[a] < 0 && match[b] < 0) (match[a], match[b]) = (b, a);
        // Then more quads wherever a chain of re-pairings frees a partner for a lone triangle (augmenting paths, the
        // matching behind Blossom-Quad, without blossoms: exact for a region without holes, close otherwise).
        var next = Enumerable.Range(0, tris.Count).Select(_ => new List<int>()).ToArray();
        var quads = new Dictionary<(int, int), int[]>();
        foreach (var (a, b, quad, _) in pairs) { next[a].Add(b); next[b].Add(a); quads[(Math.Min(a, b), Math.Max(a, b))] = quad; }
        for (int s = 0; s < tris.Count; s++)
        {
            if (match[s] >= 0 || next[s].Count == 0) continue;
            var back = new Dictionary<int, int> { [s] = -1 }; // reached triangle -> the one before it on the chain
            var queue = new Queue<int>(new[] { s });
            int free = -1;
            while (queue.Count > 0 && free < 0)
            {
                int x = queue.Dequeue();
                foreach (int u in next[x])
                {
                    if (back.ContainsKey(u)) continue;
                    back[u] = x;
                    if (match[u] < 0) { free = u; break; }
                    if (back.ContainsKey(match[u])) continue;
                    back[match[u]] = u;
                    queue.Enqueue(match[u]);
                }
            }
            // Re-pair along the chain: free - x - (x's old partner) - ... - s.
            for (int u = free; u >= 0;)
            {
                int x = back[u], after = x == s ? -1 : back[x];
                (match[u], match[x]) = (x, u);
                u = after;
            }
        }
        others.AddRange(Enumerable.Range(0, tris.Count).Where(i => match[i] > i).Select(i => quads[(i, match[i])]));
        others.AddRange(tris.Where((_, i) => match[i] < 0));
        return others;
    }

    // ---------- helpers ----------

    static Dictionary<(int, int), List<int>> EdgeMap(List<int[]> tris)
    {
        var map = new Dictionary<(int, int), List<int>>();
        for (int t = 0; t < tris.Count; t++)
            for (int k = 0; k < tris[t].Length; k++)
            {
                var key = Key(tris[t][k], tris[t][(k + 1) % tris[t].Length]);
                if (!map.TryGetValue(key, out var l)) map[key] = l = new();
                l.Add(t);
            }
        return map;
    }

    static HashSet<(int, int)> Boundary(List<int> outer, List<List<int>> holes)
    {
        var set = new HashSet<(int, int)>();
        foreach (var loop in holes.Append(outer))
            for (int k = 0; k < loop.Count; k++) set.Add(Key(loop[k], loop[(k + 1) % loop.Count]));
        return set;
    }

    static List<int> Clean(List<int> loop) => loop.Where((v, k) => v != loop[(k + 1) % loop.Count]).ToList();
    static (int, int) Key(int a, int b) => a < b ? (a, b) : (b, a);
    static double Cross(Vector2 a, Vector2 b) => (double)a.X * b.Y - (double)a.Y * b.X;

    static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);

    /// The region's plane as 2D coordinates (origin at the first outer vertex).
    sealed class Frame
    {
        readonly List<Vector3> pos;
        readonly Vector3 origin;
        Vector3 u, v;
        /// Mirror-exact: built from the part's own axes, the normal rounded and an origin on a millimetre grid, and
        /// read from the region's own side of the centre line, so a region and its mirror image (x -> -x) get the very
        /// same 2D coordinates. One lying across the centre line has it along v = 0.
        public Frame(List<Vector3> pos, Vector3 normal, List<int> outer)
        {
            this.pos = pos;
            static float Snap(float x, float grid) => MathF.Round(x / grid) * grid;
            var mid = outer.Aggregate(Vector3.Zero, (a, i) => a + pos[i]) / outer.Count;
            origin = new Vector3(Snap(mid.X, 1e-3f), Snap(mid.Y, 1e-3f), Snap(mid.Z, 1e-3f));
            var n = Vector3.Normalize(normal);
            n = Vector3.Normalize(new Vector3(Snap(n.X, 1e-6f), Snap(n.Y, 1e-6f), Snap(n.Z, 1e-6f)));
            u = Vector3.Normalize(new[] { Vector3.UnitY, Vector3.UnitZ }.Select(a => a - Vector3.Dot(a, n) * n).First(a => a.LengthSquared() > 0.1f));
            v = Vector3.Cross(n, u);
            if (origin.X < 0) v = -v;
        }
        /// The loop started at its lowest point (then leftmost).
        public List<int> Lowest(List<int> loop)
        {
            int k = Enumerable.Range(0, loop.Count).OrderBy(i => P(loop[i]).Y).ThenBy(i => P(loop[i]).X).First();
            return loop.Skip(k).Concat(loop.Take(k)).ToList();
        }
        public Vector2 P(int i) => new(Vector3.Dot(pos[i] - origin, u), Vector3.Dot(pos[i] - origin, v));
        public Vector3 At(Vector2 p) => origin + p.X * u + p.Y * v;
        public double Area(IList<int> loop)
        {
            double s = 0;
            for (int k = 0; k < loop.Count; k++) s += Cross(P(loop[k]), P(loop[(k + 1) % loop.Count]));
            return s / 2;
        }
        public bool Inside(List<int> loop, Vector2 q)
        {
            bool inside = false;
            for (int k = 0; k < loop.Count; k++)
            {
                Vector2 a = P(loop[k]), b = P(loop[(k + 1) % loop.Count]);
                if ((a.Y > q.Y) != (b.Y > q.Y) && q.X < a.X + (q.Y - a.Y) / (b.Y - a.Y) * (b.X - a.X)) inside = !inside;
            }
            return inside;
        }
        public bool StrictlyConvex(IList<int> loop)
        {
            double scale = loop.Max(i => (P(i) - P(loop[0])).LengthSquared());
            for (int k = 0; k < loop.Count; k++)
            {
                Vector2 a = P(loop[(k - 1 + loop.Count) % loop.Count]), b = P(loop[k]), c = P(loop[(k + 1) % loop.Count]);
                if (Cross(b - a, c - b) <= 1e-4 * scale) return false;
            }
            return true;
        }
        /// 1 for a square, towards 0 as a corner closes up or opens out flat.
        public double Squareness(int[] q)
        {
            double worst = 1;
            for (int k = 0; k < q.Length; k++)
            {
                Vector2 a = P(q[(k - 1 + q.Length) % q.Length]) - P(q[k]), b = P(q[(k + 1) % q.Length]) - P(q[k]);
                worst = Math.Min(worst, 1 - Math.Abs(Vector2.Dot(Vector2.Normalize(a), Vector2.Normalize(b))));
            }
            return worst;
        }
    }
}
