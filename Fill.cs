using System.Numerics;

namespace SprocketTools;

/// Fills a flat region (an outer loop, maybe with holes) with faces that are easy to edit afterwards. Around a hole: a
/// ring of quads hugging the rim, then rings that step the vertex count down toward the outer corners (no long thin
/// fans). A many-sided polygon (like a pocket floor) gets rings stepping in to a centre quad. Anything else gets a
/// Delaunay triangulation. Triangles are then paired into convex quads wherever they fit. Plain maths, tested offline.
public static class Fill
{
    /// A vertex the fill adds: its position, and the existing vertices (with weights) its settings blend from.
    public sealed record Added(Vector3 P, (int V, float W)[] Blend);

    /// How a region is filled: from its own points only (the fewest points; triangles paired into quads), with one ring
    /// of new points between a hole and the corners (light), with a quad ring hugging the rim and more rings (smooth),
    /// or with a rectangular box enclosing the cut (rectangle box).
    public enum Mode { Fewest, Light, Smooth, Rectangle, TriangleBox = Rectangle }

    public static readonly string[] ModeNames = { "最少点", "轻环", "平滑环", "矩形框" };

    /// Faces (vertex indices, turning the same way as `outer`) covering the region between `outer` and `holes`. New
    /// vertices are appended to `pos` and described in `added` (same order).
    /// Which way each region was filled, for the log and tests ("rings", "cap", "delaunay", "as is").
    public static readonly List<string> Paths = new();

    /// `light`: as few new points as will still avoid long thin fans (one ring between a hole and the face's corners).
    /// Otherwise "smooth": a quad ring hugging the rim plus rings stepping down, more points but all even slices.
    /// `added` null: no new points at all (fewest faces from the outline's own points).
    public static List<int[]> Region(List<Vector3> pos, List<int> outer, List<List<int>> holes, Vector3 normal, List<Added>? added, bool light = true, Mode mode = Mode.Fewest)
    {
        var faces = RegionFaces(pos, outer, holes, normal, added, light, mode, out string path);
        Paths.Add(path);
        return faces;
    }

    static List<int[]> RegionFaces(List<Vector3> pos, List<int> outer, List<List<int>> holes, Vector3 normal, List<Added>? added, bool light, Mode mode, out string path)
    {
        path = "as is";
        var plane = new Frame(pos, normal, outer);
        outer = Clean(outer);
        holes = holes.Select(Clean).Where(h => h.Count >= 3).ToList();
        if (outer.Count < 3) return new();
        if (plane.Area(outer) < 0) plane.Mirror();                         // outer turns counter-clockwise in 2D
        holes = holes.Select(h => plane.Area(h) > 0 ? Enumerable.Reverse(h).ToList() : h).ToList(); // holes clockwise

        List<int[]>? faces = null;
        if (holes.Count == 0)
        {
            if (outer.Count <= 4 && plane.StrictlyConvex(outer)) return new() { outer.ToArray() };
            if (added != null && mode != Mode.Rectangle && outer.Count >= 8 && plane.StrictlyConvex(outer)) { faces = Cap(pos, plane, outer, added, light); path = "cap"; }
        }
        else if (added != null && (mode == Mode.Rectangle || mode == Mode.TriangleBox))
        {
            faces = RectangleBox(pos, plane, outer, holes, added);
            path = faces != null ? "rectangle box" : "delaunay (rectangle box didn't fit)";
            if (faces != null) return faces;
        }
        else if (added != null && holes.Count == 1) { faces = Annulus(pos, plane, outer, holes[0], added, light); path = faces != null ? "rings" : WhyNotRings(plane, outer, holes[0]); }
        if (faces == null) { faces = Delaunay(plane, outer, holes); path = holes.Count == 1 && added != null ? path : "delaunay"; }
        return PairUp(plane, faces, Boundary(outer, holes));
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

    // ---------- rings ----------

    /// One hole inside a face: a quad ring hugging the rim, then rings with fewer vertices shaped like the outer loop
    /// (their corners line up with its corners), so each band steps the count down in small, even slices.
    static List<int[]>? Annulus(List<Vector3> pos, Frame plane, List<int> outer, List<int> hole, List<Added> added, bool light)
    {
        var ccwHole = Enumerable.Reverse(hole).ToList();
        var c = plane.Centroid(ccwHole);
        if (!plane.Sees(ccwHole, c) || !plane.Sees(outer, c)) return null; // rings need both loops round the hole's centre
        var coarse = new List<int>();
        if (light)
        {
            // One ring shaped like the outline, twice its corner count, when the hole has enough points to need it.
            if (ccwHole.Count > 2 * outer.Count) coarse.Add(Math.Min(2 * outer.Count, ccwHole.Count / 2));
        }
        else
            for (int n = ccwHole.Count / 2; n >= 2 * outer.Count && coarse.Count < 2; n /= 2) coarse.Add(n);
        var holeAngles = ccwHole.Select(v => plane.Angle(v, c)).ToList();
        // Try the full set of rings, then fewer, until they sit neatly inside one another.
        foreach (var layout in new[] { coarse, coarse.Skip(coarse.Count - 1).ToList(), new List<int>() }.Distinct())
        {
            int mark = pos.Count, addedMark = added.Count;
            int rings = (light ? 0 : 1) + layout.Count;
            var loops = new List<List<int>> { ccwHole };
            if (!light) loops.Add(Ring(holeAngles, 1f / (rings + 1)));  // a quad ring hugging the rim
            for (int k = 0; k < layout.Count; k++)
                loops.Add(Ring(plane.Spread(outer, c, layout[k]), light ? 0.45f : (k + 2f) / (rings + 1)));
            loops.Add(outer);
            var faces = new List<int[]>();
            for (int k = 0; k + 1 < loops.Count && faces != null; k++)
            {
                bool nested = plane.Nested(loops[k], loops[k + 1]);
                var band = nested ? Zipper(plane, loops[k], loops[k + 1], c) : null;
                if (band == null) misfit = $"layout [{string.Join(",", layout)}] band {k}: {(nested ? "a slice turned inside out" : "rings overlap")}"
;
                faces = band == null ? null : faces.Concat(band).ToList();
            }
            if (faces != null) return faces;
            pos.RemoveRange(mark, pos.Count - mark);
            added.RemoveRange(addedMark, added.Count - addedMark);
        }
        return null;

        List<int> Ring(List<double> angles, float t)
        {
            var ring = new List<int>();
            foreach (var a in angles)
            {
                var (inner, ia, ib, iw) = plane.RayHit(ccwHole, c, a);
                var (outerHit, oa, ob, ow) = plane.RayHit(outer, c, a);
                var p = Vector3.Lerp(inner, outerHit, t);
                pos.Add(p);
                added.Add(new Added(p, new[] { (ia, (1 - t) * (1 - iw)), (ib, (1 - t) * iw), (oa, t * (1 - ow)), (ob, t * ow) }));
                ring.Add(pos.Count - 1);
            }
            return ring;
        }
    }

    static string misfit = "";

    static string WhyNotRings(Frame plane, List<int> outer, List<int> hole)
    {
        var ccw = Enumerable.Reverse(hole).ToList();
        var c = plane.Centroid(ccw);
        return !plane.Sees(ccw, c) ? "delaunay (hole not round enough)" : !plane.Sees(outer, c) ? "delaunay (outline doesn't go round the hole)" : $"delaunay (rings didn't fit: {misfit})";
    }

    /// A many-sided convex polygon: rings stepping in toward the middle, halving the vertex count, ending in one face.
    static List<int[]> Cap(List<Vector3> pos, Frame plane, List<int> outer, List<Added> added, bool light)
    {
        var c2 = plane.Centroid(outer);
        var c = plane.At(c2);
        int mark = pos.Count, addedMark = added.Count;
        var origin = outer.Distinct().ToDictionary(v => v, v => v); // settings of a ring vertex come from the outer vertex it was scaled from
        var loops = new List<List<int>> { outer };
        var from = outer;
        // Light: one ring of about a quarter of the points; smooth: halve the points ring by ring down to four.
        int step = light ? Math.Max(2, outer.Count / Math.Max(4, outer.Count / 4)) : 2;
        while (from.Count > 4 && (!light || loops.Count == 1))
        {
            var ring = new List<int>();
            foreach (int v in from.Where((_, i) => i % step == 0))
            {
                var p = c + (pos[v] - c) * (light ? 0.5f : 0.6f);
                pos.Add(p);
                added.Add(new Added(p, new[] { (origin[v], 1f) }));
                origin[pos.Count - 1] = origin[v];
                ring.Add(pos.Count - 1);
            }
            loops.Add(ring);
            from = ring;
        }
        var faces = new List<int[]>();
        for (int k = 0; k + 1 < loops.Count; k++)
        {
            var band = plane.Nested(loops[k + 1], loops[k]) ? Zipper(plane, loops[k + 1], loops[k], c2) : null;
            if (band == null)
            {
                pos.RemoveRange(mark, pos.Count - mark);
                added.RemoveRange(addedMark, added.Count - addedMark);
                return Delaunay(plane, outer, new());
            }
            faces.AddRange(band);
        }
        faces.AddRange(from.Count <= 4 ? new List<int[]> { from.ToArray() } : Delaunay(plane, from, new())); // the middle, paired into quads later
        return faces;
    }

    /// Joins two nested loops around centre c (both turning counter-clockwise) with triangles, walking both in angle
    /// order so each triangle spans a small slice; equal loops come out as pairs that make quads. Null if a triangle
    /// would come out inside out (the loops don't wind round c together).
    static List<int[]>? Zipper(Frame plane, List<int> inner, List<int> outer, Vector2 c)
    {
        // Start at the pair of vertices closest in angle, then at each step close whichever triangle has the shorter
        // new edge (and doesn't come out inside out).
        double a0 = plane.Angle(inner[0], c);
        int start = Enumerable.Range(0, outer.Count).OrderBy(j => Math.Abs(Wrap(plane.Angle(outer[j], c) - a0))).First();
        var ro = Enumerable.Range(0, outer.Count).Select(j => outer[(start + j) % outer.Count]).ToList();
        int n = inner.Count, m = ro.Count;
        var faces = new List<int[]>();
        int i = 0, j = 0;
        while (i < n || j < m)
        {
            int[]? viaInner = i < n ? new[] { inner[(i + 1) % n], inner[i % n], ro[j % m] } : null;
            int[]? viaOuter = j < m ? new[] { ro[j % m], ro[(j + 1) % m], inner[i % n] } : null;
            bool innerOk = viaInner != null && plane.Area(viaInner) > 1e-14, outerOk = viaOuter != null && plane.Area(viaOuter) > 1e-14;
            if (!innerOk && !outerOk) return null;
            bool takeInner = innerOk && (!outerOk ||
                Vector2.DistanceSquared(plane.P(inner[(i + 1) % n]), plane.P(ro[j % m])) <= Vector2.DistanceSquared(plane.P(inner[i % n]), plane.P(ro[(j + 1) % m])));
            faces.Add(takeInner ? viaInner! : viaOuter!);
            if (takeInner) i++; else j++;
        }
        return faces;
    }

    static int[] Turned(Frame plane, params int[] tri) => plane.Area(tri) >= 0 ? tri : new[] { tri[0], tri[2], tri[1] };

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
            if (best.i < 0) continue;
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
        var used = new bool[tris.Count];
        foreach (var (a, b, quad, _) in pairs.OrderByDescending(p => p.Score))
        {
            if (used[a] || used[b]) continue;
            used[a] = used[b] = true;
            others.Add(quad);
        }
        others.AddRange(tris.Where((_, i) => !used[i]));
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
    static double Wrap(double a) { while (a > Math.PI) a -= 2 * Math.PI; while (a < -Math.PI) a += 2 * Math.PI; return a; }

    static List<double> Unwrap(List<double> a, double? first = null)
    {
        var r = new List<double> { first ?? a[0] };
        for (int i = 1; i < a.Count; i++)
        {
            double d = a[i] - a[i - 1];
            while (d <= 0) d += 2 * Math.PI;
            while (d > 2 * Math.PI) d -= 2 * Math.PI;
            r.Add(r[^1] + d);
        }
        return r;
    }

    /// The region's plane as 2D coordinates (origin at the first outer vertex).
    sealed class Frame
    {
        readonly List<Vector3> pos;
        readonly Vector3 origin;
        Vector3 u, v;
        public Frame(List<Vector3> pos, Vector3 normal, List<int> outer)
        {
            this.pos = pos;
            origin = pos[outer[0]];
            var n = Vector3.Normalize(normal);
            u = Vector3.Normalize(Math.Abs(n.X) < 0.9f ? Vector3.Cross(n, Vector3.UnitX) : Vector3.Cross(n, Vector3.UnitY));
            v = Vector3.Cross(n, u);
        }
        public void Mirror() => v = -v;
        public Vector2 P(int i) => new(Vector3.Dot(pos[i] - origin, u), Vector3.Dot(pos[i] - origin, v));
        public Vector3 At(Vector2 p) => origin + p.X * u + p.Y * v;
        public double Area(IList<int> loop)
        {
            double s = 0;
            for (int k = 0; k < loop.Count; k++) s += Cross(P(loop[k]), P(loop[(k + 1) % loop.Count]));
            return s / 2;
        }
        public Vector2 Centroid(List<int> loop)
        {
            double a = 0, x = 0, y = 0;
            for (int k = 0; k < loop.Count; k++)
            {
                Vector2 p = P(loop[k]), q = P(loop[(k + 1) % loop.Count]);
                double cr = Cross(p, q);
                a += cr; x += (p.X + q.X) * cr; y += (p.Y + q.Y) * cr;
            }
            return Math.Abs(a) < 1e-18 ? loop.Aggregate(Vector2.Zero, (s, i) => s + P(i)) / loop.Count : new Vector2((float)(x / (3 * a)), (float)(y / (3 * a)));
        }
        public double Angle(int i, Vector2 c) { var d = P(i) - c; return Math.Atan2(d.Y, d.X); }

        /// About `count` directions from c: every corner of the loop, plus even steps between them.
        public List<double> Spread(List<int> loop, Vector2 c, int count)
        {
            var a = Unwrap(loop.Select(v => Angle(v, c)).ToList());
            var result = new List<double>();
            for (int k = 0; k < loop.Count; k++)
            {
                double from = a[k], to = k + 1 < loop.Count ? a[k + 1] : a[0] + 2 * Math.PI;
                int steps = Math.Max(1, (int)Math.Round(count * (to - from) / (2 * Math.PI)));
                for (int s = 0; s < steps; s++) result.Add(from + (to - from) * s / steps);
            }
            return result;
        }

        /// The inner loop lies inside the outer one and doesn't touch it.
        public bool Nested(List<int> inner, List<int> outer) =>
            inner.All(v => Inside(outer, P(v))) && outer.All(v => !Inside(inner, P(v)));

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
        /// Every edge of a counter-clockwise loop has c on its inner side (so rays from c cross the loop once).
        public bool Sees(List<int> loop, Vector2 c)
        {
            for (int k = 0; k < loop.Count; k++)
            {
                Vector2 a = P(loop[k]), b = P(loop[(k + 1) % loop.Count]);
                if (Cross(b - a, c - a) <= 1e-12) return false;
            }
            return true;
        }
        /// Where the ray from c at `angle` leaves a loop: the point, the edge's two vertices and how far along it.
        public (Vector3 Point, int A, int B, float W) RayHit(List<int> loop, Vector2 c, double angle)
        {
            var dir = new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle));
            (double t, int k, double s) best = (double.MaxValue, 0, 0);
            for (int k = 0; k < loop.Count; k++)
            {
                Vector2 a = P(loop[k]), b = P(loop[(k + 1) % loop.Count]);
                var e = b - a;
                double den = Cross(dir, e);
                if (Math.Abs(den) < 1e-18) continue;
                double t = Cross(a - c, e) / den, s = Cross(a - c, dir) / den;
                // A ray aimed at a corner may pass a hair beside both edges meeting there: allow a little slack.
                if (t > 0 && s >= -1e-5 && s <= 1 + 1e-5 && t < best.t) best = (t, k, Math.Clamp(s, 0, 1));
            }
            if (best.t == double.MaxValue) // missed everything: the corner nearest in angle
            {
                int nearest = Enumerable.Range(0, loop.Count).OrderBy(k => Math.Abs(Wrap(Angle(loop[k], c) - angle))).First();
                return (pos[loop[nearest]], loop[nearest], loop[nearest], 0);
            }
            int ia = loop[best.k], ib = loop[(best.k + 1) % loop.Count];
            return (Vector3.Lerp(pos[ia], pos[ib], (float)best.s), ia, ib, (float)best.s);
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
