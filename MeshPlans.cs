using System.Numerics;

namespace SprocketTools;

/// Blender-style mesh tools for hand-made structures, planned as plain maths on point indices (tested offline):
/// Flatten, Loop cut, Inset, Bevel, and Proportional editing's falloff. MeshTools applies the plans to the game's mesh.
public static class MeshPlans
{
    /// A point a tool adds: where, and the existing points (with weights) its corner thickness blends from.
    public sealed record NewPoint(Vector3 P, (int V, float W)[] Blend);

    /// A face a tool adds: its corners (existing points, or pos.Count + i for Points[i]), turning the way `Source` did,
    /// and the old face it's made from (thickness and settings carry over).
    public sealed record NewFace(int[] Corners, int Source);

    /// Faces to take out, faces to put in, and the points they need. Points no face uses afterwards go too.
    public sealed record Rebuild(List<int> Remove, List<NewFace> Add, List<NewPoint> Points, string? Why)
    {
        public static Rebuild Fail(string why) => new(new(), new(), new(), why);
    }

    // ---------- checks, before anything changes ----------

    /// Why a rebuild would leave the shape broken, or null: a new face repeating a corner, squashed to next to no area, or
    /// turned over against the face it's made from; edges shared by more than two faces, longer in all than before
    /// (faces laid over each other); or open edges longer in all than before (a crack: faces no longer joined). `gaps`
    /// false skips the last, for tools asked to leave points unjoined. Faces that were squashed already are left be.
    public static string? Check(IReadOnlyList<Vector3> pos, IReadOnlyList<int[]> faces, Rebuild plan, bool gaps = true)
    {
        if (plan.Why != null) return plan.Why;
        if (pos.Any(p => !Finite(p)) || faces.Any(f => f.Length < 3 || f.Any(v => v < 0 || v >= pos.Count)))
            return "原始网格含有无效的顶点或面";
        if (plan.Remove.Any(f => f < 0 || f >= faces.Count) || plan.Add.Any(f => f.Source < 0 || f.Source >= faces.Count))
            return "所选的面已不在当前网格上，请重新选择";
        if (plan.Points.Any(p => !Finite(p.P) || p.Blend.Length == 0 || p.Blend.Any(b => b.V < 0 || b.V >= pos.Count || !float.IsFinite(b.W) || b.W < 0)
                || !float.IsFinite(p.Blend.Sum(b => b.W)) || p.Blend.Sum(b => b.W) <= 0))
            return "新顶点或它的厚度权重无效";
        var at = pos.Concat(plan.Points.Select(p => p.P)).ToList();
        foreach (var nf in plan.Add)
        {
            var c = nf.Corners;
            if (c.Length < 3 || c.Distinct().Count() != c.Length || c.Any(i => i < 0 || i >= at.Count)) return "新面会重复使用同一个角点";
            var n = Newell(at, c);
            var s = Newell(pos, faces[nf.Source]);
            if (!Finite(n) || !Finite(s) || !float.IsFinite(n.Length()) || !float.IsFinite(s.Length())) return "面的面积数值无效";
            if (s.Length() < 1e-9f) continue; // made from a squashed face: nothing to compare with
            if (n.Length() < 1e-5f * s.Length()) return "新面会被压成几乎零面积";
            if (Vector3.Dot(n, s) < -0.5f * n.Length() * s.Length()) return "新面会被翻转";
        }
        var removed = plan.Remove.ToHashSet();
        var after = faces.Where((_, i) => !removed.Contains(i)).Concat(plan.Add.Select(a => a.Corners)).ToList();
        // Tolerance follows the size of the patch being edited, not its distance from the world origin.
        var touched = plan.Remove.Concat(plan.Add.Select(f => f.Source)).Distinct().SelectMany(f => faces[f]).Distinct().Select(v => pos[v]).ToList();
        float extent = touched.Count == 0 ? 0 : Vector3.Distance(touched.Aggregate(Vector3.Min), touched.Aggregate(Vector3.Max));
        float tolerance = 1e-5f + 1e-4f * extent;
        var beforeLengths = EdgeLengths(faces, at);
        var afterLengths = EdgeLengths(after, at);
        if (afterLengths.Crowded > beforeLengths.Crowded + tolerance) return "面会互相叠覆";
        if (afterLengths.SameWay > beforeLengths.SameWay + tolerance) return "相连的面沿共用边朝同一个方向绕行";
        if (gaps && afterLengths.Open > beforeLengths.Open + tolerance) return "会留下裂缝（面不再相连）";
        return null;
    }

    /// Why moving points to `moved` would fold a face over or squash it flat, or null.
    public static string? Folds(IReadOnlyList<Vector3> pos, IReadOnlyList<int[]> faces, IReadOnlyDictionary<int, Vector3> moved)
    {
        foreach (var f in faces)
        {
            if (!f.Any(moved.ContainsKey)) continue;
            if (f.Any(v => v < 0 || v >= pos.Count || !Finite(moved.TryGetValue(v, out var to) ? to : pos[v]))) return "要移动到的顶点位置无效";
            var was = Newell(pos, f);
            // Fan the triangles from one corner of the face: absolute-coordinate products lose a small face far from the origin.
            var origin = moved.TryGetValue(f[0], out var first) ? first : pos[f[0]];
            var now = Vector3.Zero;
            for (int k = 1; k + 1 < f.Length; k++) now += Vector3.Cross((moved.TryGetValue(f[k], out var a) ? a : pos[f[k]]) - origin,
                (moved.TryGetValue(f[k + 1], out var b) ? b : pos[f[k + 1]]) - origin);
            if (was.Length() < 1e-8f) continue; // squashed already: not this tool's doing
            if (now.Length() < 1e-8f) return "某个面会被压成零面积";
            if (Vector3.Dot(now, was) <= 0) return "某个面会翻折";
        }
        return null;
    }

    /// The point of a face (its corners, fanned into triangles from the first) nearest to `p`, and how far away it is.
    public static (Vector3 Point, float Distance) Closest(IReadOnlyList<Vector3> corners, Vector3 p)
    {
        var best = (Point: corners[0], Distance: float.MaxValue);
        for (int k = 1; k + 1 < corners.Count; k++)
        {
            var q = OnTriangle(p, corners[0], corners[k], corners[k + 1]);
            float d = Vector3.Distance(p, q);
            if (d < best.Distance) best = (q, d);
        }
        return best;
    }

    // Nearest point of triangle abc to p (Ericson, Real-Time Collision Detection 5.1.5).
    static Vector3 OnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 ab = b - a, ac = c - a, ap = p - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return a;
        Vector3 bp = p - b;
        float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return b;
        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) return a + ab * (d1 / (d1 - d3));
        Vector3 cp = p - c;
        float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return c;
        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) return a + ac * (d2 / (d2 - d6));
        float va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return b + (c - b) * ((d4 - d3) / (d4 - d3 + (d5 - d6)));
        float denom = 1 / (va + vb + vc);
        return a + ab * (vb * denom) + ac * (vc * denom);
    }

    static IEnumerable<(int, int)> Sides(int[] f) => f.Select((v, k) => v < f[(k + 1) % f.Length] ? (v, f[(k + 1) % f.Length]) : (f[(k + 1) % f.Length], v));

    // Total length of the edges whose use the rebuild changes: crowded (over two faces, so faces laid over each other),
    // open (one face, so a crack), and same-way (two faces running the same way along it, so they fold on each other).
    static (double Crowded, double Open, double SameWay) EdgeLengths(IEnumerable<int[]> faces, IReadOnlyList<Vector3> at)
    {
        var uses = new Dictionary<(int, int), (int Count, int Turn)>();
        foreach (var f in faces)
            for (int k = 0; k < f.Length; k++)
            {
                int a = f[k], b = f[(k + 1) % f.Length];
                var e = Key(a, b);
                var old = uses.GetValueOrDefault(e);
                uses[e] = (old.Count + 1, old.Turn + (a < b ? 1 : -1));
            }
        double crowded = 0, open = 0, sameWay = 0;
        foreach (var (edge, use) in uses)
        {
            double length = Vector3.Distance(at[edge.Item1], at[edge.Item2]);
            if (use.Count > 2) crowded += length;
            if (use.Count == 1) open += length;
            if (use.Count == 2 && use.Turn != 0) sameWay += length;
        }
        return (crowded, open, sameWay);
    }

    // ---------- Flatten ----------

    public enum FlattenMode { BestFit, Level, Sideways, Lengthways }

    /// New positions for `points`: onto their best-fit plane (facing `normal` when faces were selected), or all at the
    /// same height (y), side (x) or length (z) position: the average of theirs. Empty if there's nothing to flatten.
    public static Dictionary<int, Vector3> Flatten(IReadOnlyList<Vector3> pos, ICollection<int> points, FlattenMode mode, Vector3? normal = null)
    {
        var result = new Dictionary<int, Vector3>();
        if (points.Count < (mode == FlattenMode.BestFit ? 3 : 2)) return result;
        var c = points.Aggregate(Vector3.Zero, (s, v) => s + pos[v]) / points.Count;
        Vector3 n = mode switch
        {
            FlattenMode.Level => Vector3.UnitY,
            FlattenMode.Sideways => Vector3.UnitX,
            FlattenMode.Lengthways => Vector3.UnitZ,
            _ => normal is { } given && given.LengthSquared() > 1e-12f ? Vector3.Normalize(given) : LeastSpread(pos, points, c),
        };
        foreach (int v in points)
            result[v] = mode switch
            {
                // Set straight to the shared value, so the points agree exactly.
                FlattenMode.Level => new Vector3(pos[v].X, c.Y, pos[v].Z),
                FlattenMode.Sideways => new Vector3(c.X, pos[v].Y, pos[v].Z),
                FlattenMode.Lengthways => new Vector3(pos[v].X, pos[v].Y, c.Z),
                _ => pos[v] - n * Vector3.Dot(pos[v] - c, n),
            };
        return result;
    }

    /// The direction the points spread least along: the covariance's smallest eigenvector,
    /// computed via Jacobi eigenvalue decomposition of the 3x3 covariance matrix.
    static Vector3 LeastSpread(IReadOnlyList<Vector3> pos, ICollection<int> points, Vector3 c)
    {
        double xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
        foreach (int pt in points)
        {
            var d = pos[pt] - c;
            xx += d.X * d.X; xy += d.X * d.Y; xz += d.X * d.Z; yy += d.Y * d.Y; yz += d.Y * d.Z; zz += d.Z * d.Z;
        }
        // 3x3 symmetric covariance matrix A and eigenvector matrix V
        double[,] a = { { xx, xy, xz }, { xy, yy, yz }, { xz, yz, zz } };
        double[,] v = { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
        for (int iter = 0; iter < 50; iter++)
        {
            int p = 0, q = 1;
            double max = Math.Abs(a[0, 1]);
            if (Math.Abs(a[0, 2]) > max) { max = Math.Abs(a[0, 2]); p = 0; q = 2; }
            if (Math.Abs(a[1, 2]) > max) { max = Math.Abs(a[1, 2]); p = 1; q = 2; }
            if (max < 1e-15) break;
            double app = a[p, p], aqq = a[q, q], apq = a[p, q];
            double phi = 0.5 * Math.Atan2(2.0 * apq, aqq - app);
            double cos = Math.Cos(phi), sin = Math.Sin(phi);
            for (int k = 0; k < 3; k++)
            {
                if (k != p && k != q)
                {
                    double akp = a[k, p], akq = a[k, q];
                    a[k, p] = a[p, k] = cos * akp - sin * akq;
                    a[k, q] = a[q, k] = sin * akp + cos * akq;
                }
            }
            a[p, p] = cos * cos * app - 2.0 * sin * cos * apq + sin * sin * aqq;
            a[q, q] = sin * sin * app + 2.0 * sin * cos * apq + cos * cos * aqq;
            a[p, q] = a[q, p] = 0.0;
            for (int k = 0; k < 3; k++)
            {
                double vkp = v[k, p], vkq = v[k, q];
                v[k, p] = cos * vkp - sin * vkq;
                v[k, q] = sin * vkp + cos * vkq;
            }
        }
        // Smallest eigenvalue index
        int minIdx = 0;
        double minVal = a[0, 0];
        if (a[1, 1] < minVal) { minVal = a[1, 1]; minIdx = 1; }
        if (a[2, 2] < minVal) { minVal = a[2, 2]; minIdx = 2; }
        var n = new Vector3((float)v[0, minIdx], (float)v[1, minIdx], (float)v[2, minIdx]);
        return n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : Vector3.UnitY;
    }

    // ---------- Loop cut ----------

    /// A loop through the ring of quads each start edge crosses: every edge of the ring gets a middle point and each quad
    /// is cut in two between them. The ring stops at the plate's edge, at a triangle (cut in two as well), or where it
    /// closes. Start edges already on a ring are skipped (a mirrored twin on the same ring).
    public static Rebuild LoopCut(IReadOnlyList<Vector3> pos, IReadOnlyList<int[]> faces, IEnumerable<(int A, int B)> starts)
    {
        var edgeFaces = EdgeFaces(faces);
        var mids = new Dictionary<(int, int), int>();
        var points = new List<NewPoint>();
        var cut = new Dictionary<int, int>(); // face -> its corner starting the side the ring enters by
        void Mid(int a, int b)
        {
            if (mids.ContainsKey(Key(a, b))) return;
            mids[Key(a, b)] = pos.Count + points.Count;
            points.Add(new NewPoint((pos[a] + pos[b]) / 2, new[] { (a, 0.5f), (b, 0.5f) }));
        }
        foreach (var (a0, b0) in starts)
        {
            if (!edgeFaces.TryGetValue(Key(a0, b0), out var first) || mids.ContainsKey(Key(a0, b0))) continue;
            foreach (int start in first)
            {
                int f = start;
                var e = Key(a0, b0);
                while (!cut.ContainsKey(f))
                {
                    int k = SideIndex(faces[f], e);
                    if (k < 0) break;
                    cut[f] = k;
                    Mid(e.Item1, e.Item2);
                    if (faces[f].Length != 4) break; // a triangle ends the ring
                    var q = faces[f];
                    e = Key(q[(k + 2) % 4], q[(k + 3) % 4]);
                    Mid(e.Item1, e.Item2);
                    int next = edgeFaces[e].FirstOrDefault(g => g != f, -1);
                    if (next < 0) break; // the plate's edge
                    f = next;
                }
            }
        }
        if (cut.Count == 0) return Rebuild.Fail("选择面上的一条边");
        // Every face on a cut edge must be cut across it too, or it would be left with a gap.
        foreach (var edge in mids.Keys)
            foreach (int g in edgeFaces[edge])
                if (!cut.TryGetValue(g, out int k) || !CutSides(faces[g], k).Contains(edge))
                    return Rebuild.Fail("切割环会自交，或撞到环外的面");
        var add = new List<NewFace>();
        foreach (var (f, k) in cut)
        {
            var c = faces[f];
            if (c.Length == 4)
            {
                int m1 = mids[Key(c[k], c[(k + 1) % 4])], m2 = mids[Key(c[(k + 2) % 4], c[(k + 3) % 4])];
                add.Add(new NewFace(new[] { c[k], m1, m2, c[(k + 3) % 4] }, f));
                add.Add(new NewFace(new[] { m1, c[(k + 1) % 4], c[(k + 2) % 4], m2 }, f));
            }
            else
            {
                int m = mids[Key(c[k], c[(k + 1) % 3])];
                add.Add(new NewFace(new[] { c[k], m, c[(k + 2) % 3] }, f));
                add.Add(new NewFace(new[] { m, c[(k + 1) % 3], c[(k + 2) % 3] }, f));
            }
        }
        return new Rebuild(cut.Keys.ToList(), add, points, null);
    }

    static IEnumerable<(int, int)> CutSides(int[] face, int k)
    {
        yield return Key(face[k], face[(k + 1) % face.Length]);
        if (face.Length == 4) yield return Key(face[(k + 2) % 4], face[(k + 3) % 4]);
    }

    // ---------- Inset ----------

    /// The selected faces shrunk inward by `width` along their surface (each outline side moves in parallel), with a ring
    /// of quads joining the old outline to the new one. Faces sharing edges inset together, around holes too.
    public static Rebuild Inset(IReadOnlyList<Vector3> pos, IReadOnlyList<int[]> faces, ICollection<int> selected, float width)
    {
        if (selected.Count == 0) return Rebuild.Fail("请先选择面");
        var uses = new Dictionary<(int, int), int>();
        foreach (int f in selected)
            for (int k = 0; k < faces[f].Length; k++) uses[Key(faces[f][k], faces[f][(k + 1) % faces[f].Length])] = uses.GetValueOrDefault(Key(faces[f][k], faces[f][(k + 1) % faces[f].Length])) + 1;
        // The outline: selected faces' sides that no other selected face has, in the faces' own turning.
        var next = new Dictionary<int, int>();
        var prev = new Dictionary<int, int>();
        var owner = new List<(int A, int B, int Face)>();
        foreach (int f in selected)
            for (int k = 0; k < faces[f].Length; k++)
            {
                int a = faces[f][k], b = faces[f][(k + 1) % faces[f].Length];
                if (uses[Key(a, b)] != 1) continue;
                if (!next.TryAdd(a, b) || !prev.TryAdd(b, a)) return Rebuild.Fail("所选区域在角点处自我相接");
                owner.Add((a, b, f));
            }
        if (next.Count == 0) return Rebuild.Fail("所选区域是闭合形状，没有外轮廓");
        var normal = new Dictionary<int, Vector3>();
        foreach (int f in selected)
        {
            var n = Newell(pos, faces[f]);
            foreach (int v in faces[f]) if (next.ContainsKey(v)) normal[v] = normal.GetValueOrDefault(v) + n;
        }
        var inner = new Dictionary<int, int>();
        var points = new List<NewPoint>();
        foreach (var (v, b) in next)
        {
            if (!prev.TryGetValue(v, out int a)) return Rebuild.Fail("所选区域的外轮廓不闭合");
            var n = Vector3.Normalize(normal[v]);
            Vector3 in1 = Vector3.Normalize(Vector3.Cross(n, pos[v] - pos[a])), in2 = Vector3.Normalize(Vector3.Cross(n, pos[b] - pos[v]));
            var dir = in1 + in2;
            dir = dir.LengthSquared() < 1e-8f ? in1 : Vector3.Normalize(dir);
            float along = Math.Max(0.25f, Vector3.Dot(dir, in1)); // each side moves in by `width`; sharp corners go at most 4× that
            inner[v] = pos.Count + points.Count;
            points.Add(new NewPoint(pos[v] + dir * (width / along), new[] { (v, 1f) }));
        }
        var all = pos.Concat(points.Select(p => p.P)).ToList();
        var add = new List<NewFace>();
        foreach (int f in selected)
        {
            var shrunk = faces[f].Select(v => inner.TryGetValue(v, out int w) ? w : v).ToArray();
            if (Vector3.Dot(Newell(all, shrunk), Newell(pos, faces[f])) <= 0) return Rebuild.Fail("对这些面来说内插宽度过大");
            add.Add(new NewFace(shrunk, f));
        }
        foreach (var (a, b, f) in owner)
        {
            // Too wide, the inner outline passes through itself: a side then points backwards.
            if (Vector3.Dot(all[inner[b]] - all[inner[a]], pos[b] - pos[a]) <= 0) return Rebuild.Fail("对这些面来说内插宽度过大");
            add.Add(new NewFace(new[] { a, b, inner[b], inner[a] }, f));
        }
        return new Rebuild(selected.ToList(), add, points, null);
    }

    // ---------- Bevel ----------

    /// The selected edges become chamfer strips `width` wide on each side. Where bevelled edges meet, each fan of faces
    /// between them gets its own copy of the point, slid along its own edge; where three or more meet, a cap face closes
    /// the corner. A bevelled edge ending at a point with no other bevelled edge cuts the faces there in (like Blender).
    public static Rebuild Bevel(IReadOnlyList<Vector3> pos, IReadOnlyList<int[]> faces, IEnumerable<(int A, int B)> edges, float width, int segments = 1)
    {
        if (!float.IsFinite(width) || width <= 0) return Rebuild.Fail("倒角宽度须为正数");
        if (segments < 1 || segments > 16) return Rebuild.Fail("分段数请在 1 到 16 之间");
        var edgeFaces = EdgeFaces(faces);
        var sel = edges.Select(e => Key(e.A, e.B)).Where(e => edgeFaces.TryGetValue(e, out var fs) && fs.Count == 2).ToHashSet();
        if (sel.Count == 0) return Rebuild.Fail("请选择两侧都有面的边");
        var pointFaces = new Dictionary<int, List<int>>();
        for (int f = 0; f < faces.Count; f++)
            foreach (int v in faces[f])
            {
                if (!pointFaces.TryGetValue(v, out var l)) pointFaces[v] = l = new();
                l.Add(f);
            }
        var points = new List<NewPoint>();
        // How each face's corner at a bevelled point changes: one point in its place (or two, cutting the corner off).
        var corner = new Dictionary<(int Face, int Point), int[]>();
        var endsAt = new HashSet<int>(); // points where a bevel ends inside the plate and the point stays
        int Add(Vector3 p, int from)
        {
            points.Add(new NewPoint(p, new[] { (from, 1f) }));
            return pos.Count + points.Count - 1;
        }
        Vector3 Slide(int u, int w) { var d = pos[w] - pos[u]; return pos[u] + Vector3.Normalize(d) * Math.Min(width, 0.45f * d.Length()); }

        foreach (int u in sel.SelectMany(e => new[] { e.Item1, e.Item2 }).Distinct())
        {
            var around = pointFaces[u];
            var parent = around.ToDictionary(f => f, f => f);
            int Find(int x) => parent[x] == x ? x : parent[x] = Find(parent[x]);
            foreach (int f in around)
                foreach (int w in Beside(faces[f], u))
                    if (!sel.Contains(Key(u, w)))
                        foreach (int g in edgeFaces[Key(u, w)]) if (g != f && parent.ContainsKey(g)) parent[Find(g)] = Find(f);
            var sectors = around.GroupBy(Find).Select(g => g.ToList()).ToList();
            if (sectors.Count >= 2)
            {
                foreach (var sector in sectors)
                {
                    // Slide along the sector's own edge at u; with none (one face between two bevelled edges) go into
                    // the face along the bisector; with several, along their average.
                    var own = sector.SelectMany(f => Beside(faces[f], u)).Distinct().Where(w => !sel.Contains(Key(u, w))).ToList();
                    Vector3 p;
                    if (own.Count == 1) p = Slide(u, own[0]);
                    else if (own.Count == 0)
                    {
                        var ends = Beside(faces[sector[0]], u).ToArray();
                        Vector3 e1 = Vector3.Normalize(pos[ends[0]] - pos[u]), e2 = Vector3.Normalize(pos[ends[1]] - pos[u]);
                        float half = MathF.Acos(Math.Clamp(Vector3.Dot(e1, e2), -1f, 1f)) / 2;
                        p = pos[u] + Vector3.Normalize(e1 + e2) * (width / Math.Max(0.2f, MathF.Sin(half)));
                    }
                    else p = pos[u] + Vector3.Normalize(own.Aggregate(Vector3.Zero, (s, w) => s + Vector3.Normalize(pos[w] - pos[u]))) * width;
                    int id = Add(p, u);
                    foreach (int f in sector) corner[(f, u)] = new[] { id };
                }
                continue;
            }
            // One bevelled edge ends here, inside the plate: new points on the two faces' other edges at u. The faces
            // either side take them in place of u; the faces beyond those edges get them added; u goes if nothing
            // else keeps it (a box corner).
            var e0 = sel.First(e => e.Item1 == u || e.Item2 == u);
            var sides = edgeFaces[e0];
            var edgeOf = new Dictionary<int, int>(); // face beside the bevel -> its other edge's far point at u
            var at = new Dictionary<int, int>();     // that far point -> the new point on the edge
            foreach (int f in sides)
            {
                int x = Beside(faces[f], u).First(w => Key(u, w) != e0);
                edgeOf[f] = x;
                if (!at.ContainsKey(x)) at[x] = Add(Slide(u, x), u);
                corner[(f, u)] = new[] { at[x] };
            }
            bool cutOff = false;
            foreach (int g in around.Where(g => !sides.Contains(g)))
            {
                // g's corner at u, between its two neighbours there: add the new point on each side it shares.
                int k = Array.IndexOf(faces[g], u);
                int before = faces[g][(k - 1 + faces[g].Length) % faces[g].Length], after = faces[g][(k + 1) % faces[g].Length];
                bool hasBefore = at.ContainsKey(before), hasAfter = at.ContainsKey(after);
                if (hasBefore && hasAfter) { corner[(g, u)] = new[] { at[before], at[after] }; cutOff = true; } // box corner: u is cut off
                else if (hasBefore) corner[(g, u)] = new[] { at[before], u };
                else if (hasAfter) corner[(g, u)] = new[] { u, at[after] };
            }
            // More faces round u than a box corner: u stays, and the strip's end runs through it (else a hole is left
            // between u and the two new points).
            if (!cutOff) endsAt.Add(u);
        }

        int[] Rebuilt(int f) => faces[f].SelectMany(v => corner.TryGetValue((f, v), out var r) ? r : new[] { v }).ToArray();
        var remove = corner.Keys.Select(k => k.Face).Distinct().ToList();
        var shapes = remove.Select(f => (Corners: Rebuilt(f), Source: f)).ToList();
        // A strip along each bevelled edge between its two faces' new corners (a point where the bevel ends in a cut).
        var capSides = new Dictionary<int, List<(int From, int To)>>();
        var curves = new Dictionary<(int, int), int[]>();
        Vector3 Position(int id) => id < pos.Count ? pos[id] : points[id - pos.Count].P;
        int[] Curve(int from, int to, int u)
        {
            var key = Key(from, to);
            if (!curves.TryGetValue(key, out var path))
            {
                var p = Position(key.Item1); var q = Position(key.Item2); var control = pos[u];
                var d1 = p - control; var d2 = q - control;
                // Rational quadratic fillet: tangent to both sides, circular for equal offsets.
                float weight = MathF.Sqrt(Math.Clamp((1 - Vector3.Dot(Vector3.Normalize(d1), Vector3.Normalize(d2))) / 2, 0.0001f, 1));
                path = new int[segments + 1]; path[0] = key.Item1; path[^1] = key.Item2;
                for (int s = 1; s < segments; s++)
                {
                    float t = s / (float)segments, a = (1 - t) * (1 - t), b = 2 * weight * t * (1 - t), c = t * t;
                    path[s] = Add((a * p + b * control + c * q) / (a + b + c), u);
                }
                curves[key] = path;
            }
            return from == key.Item1 ? path : path.Reverse().ToArray();
        }
        int Origin(int id) => id < pos.Count ? id : points[id - pos.Count].Blend[0].V;
        foreach (var e in sel)
        {
            var fs = edgeFaces[e];
            int f1 = SideIndex(faces[fs[0]], e) is int k0 && faces[fs[0]][k0] == e.Item1 ? fs[0] : fs[1];
            int f2 = fs[0] == f1 ? fs[1] : fs[0];
            int a = e.Item1, b = e.Item2; // f1 runs a -> b, f2 runs b -> a
            int[] At(int f, int v) => corner.TryGetValue((f, v), out var r) ? r : new[] { v };
            if (segments > 1)
            {
                var aa = At(f1, a); var ab = At(f2, a); var ba = At(f1, b); var bb = At(f2, b);
                if (new[] { aa, ab, ba, bb }.Any(c => c.Length != 1)) return Rebuild.Fail("这个交接处无法倒成圆角");
                var ap = Curve(aa[0], ab[0], a); var bp = Curve(ba[0], bb[0], b);
                for (int s = 0; s < segments; s++)
                    shapes.Add((new[] { bp[s], ap[s], ap[s + 1], bp[s + 1] }, s * 2 < segments ? f1 : f2));
                foreach (var (u, path, reverse) in new[] { (a, ap, true), (b, bp, false) })
                {
                    for (int s = 0; s < segments; s++)
                    {
                        int p = reverse ? path[s + 1] : path[s], q = reverse ? path[s] : path[s + 1];
                        if (endsAt.Contains(u))
                        {
                            var n = Vector3.Cross(Position(q) - Position(p), pos[u] - Position(p));
                            int source = pointFaces[u].OrderByDescending(f => Vector3.Dot(n, Vector3.Normalize(Newell(pos, faces[f])))).First();
                            shapes.Add((new[] { p, q, u }, source));
                        }
                        else
                        {
                            if (!capSides.TryGetValue(u, out var sides)) capSides[u] = sides = new();
                            sides.Add((p, q));
                        }
                    }
                }
                continue;
            }
            // f1 runs a -> b: the strip runs b -> a along f1's side and a -> b along f2's side.
            var strip = new List<int>();
            strip.AddRange(At(f1, b).Reverse());
            strip.AddRange(At(f1, a).Reverse());
            if (endsAt.Contains(a)) strip.Add(a);
            strip.AddRange(At(f2, a).Reverse());
            strip.AddRange(At(f2, b).Reverse());
            if (endsAt.Contains(b)) strip.Add(b);
            strip = strip.Where((v, i) => v != strip[(i + 1) % strip.Count]).Distinct().ToList();
            if (strip.Count < 3) continue;
            shapes.Add((strip.ToArray(), f1));
            for (int i = 0; i < strip.Count; i++)
            {
                int p = strip[i], q = strip[(i + 1) % strip.Count];
                if (p != q && Origin(p) == Origin(q) && (p >= pos.Count || q >= pos.Count))
                {
                    if (!capSides.TryGetValue(Origin(p), out var l)) capSides[Origin(p)] = l = new();
                    l.Add((q, p)); // the cap runs the other way along the strip's side
                }
            }
        }
        if (shapes.Count == remove.Count) return Rebuild.Fail("只倒一条边时，该边一端必须是角点或板边");
        // End faces share the same arc vertices as the rounded strip, so no T-junctions are left behind.
        if (segments > 1)
            for (int i = 0; i < remove.Count; i++)
            {
                var (c, source) = shapes[i];
                var expanded = new List<int>();
                for (int k = 0; k < c.Length; k++)
                {
                    int a = c[k], b = c[(k+1)%c.Length]; expanded.Add(a);
                    if (curves.TryGetValue(Key(a,b), out var path))
                        expanded.AddRange((a == path[0] ? path : path.Reverse()).Skip(1).Take(path.Length-2));
                }
                shapes[i] = (expanded.ToArray(), source);
            }
        // Where three or more bevelled edges meet, a cap closes the corner.
        foreach (var (u, sidesAtU) in capSides.Where(c => c.Value.Count >= 3))
        {
            var chain = sidesAtU.GroupBy(s => s.From).ToDictionary(g => g.Key, g => g.First().To);
            var loop = new List<int> { sidesAtU[0].From };
            while (loop.Count <= sidesAtU.Count && chain.TryGetValue(loop[^1], out int to) && to != loop[0]) loop.Add(to);
            if (loop.Count == sidesAtU.Count)
            {
                if (segments == 1) shapes.Add((loop.ToArray(), pointFaces[u][0]));
                else
                {
                    // A curved multi-edge junction is not planar. A centre fan avoids the sliver
                    // ears made by projecting its near-tangent boundary onto one flat plate.
                    int centre = Add(loop.Aggregate(Vector3.Zero, (sum,v) => sum + Position(v)) / loop.Count, u);
                    for (int k = 0; k < loop.Count; k++)
                    {
                        int p = loop[k], q = loop[(k+1)%loop.Count];
                        var n = Vector3.Cross(Position(q)-Position(p), Position(centre)-Position(p));
                        int source = pointFaces[u].OrderByDescending(f => Vector3.Dot(n, Vector3.Normalize(Newell(pos, faces[f])))).First();
                        shapes.Add((new[] { p,q,centre },source));
                    }
                }
            }
        }
        // Faces that grew past four corners are filled with triangles and quads from their own points.
        var all = pos.Concat(points.Select(p => p.P)).ToList();
        var add = new List<NewFace>();
        foreach (var (c, source) in shapes)
        {
            if (c.Length <= 4) { add.Add(new NewFace(c, source)); continue; }
            var filled = Fill.Region(all, c.ToList(), new List<List<int>>(), Newell(all, c), null);
            if (filled.Count == 0) return Rebuild.Fail("倒角周围的面无法重建");
            add.AddRange(filled.Select(x => new NewFace(x, source)));
        }
        return new Rebuild(remove, add, points, null);
    }

    /// The two corners next to `u` in a face.
    static IEnumerable<int> Beside(int[] face, int u)
    {
        int k = Array.IndexOf(face, u);
        yield return face[(k + 1) % face.Length];
        yield return face[(k - 1 + face.Length) % face.Length];
    }

    // ---------- Proportional editing ----------

    /// For each point within `radius` of the moved ones: the moved points it follows and how much. A smooth falloff with
    /// the distance to the nearest moved point, shared between nearby moved points by inverse distance squared.
    public static Dictionary<int, (int[] Moved, float[] Weights)> Falloff(IReadOnlyList<Vector3> pos, ICollection<int> moved, float radius)
    {
        var result = new Dictionary<int, (int[], float[])>();
        if (radius <= 0) return result;
        var movers = moved.ToList();
        for (int v = 0; v < pos.Count; v++)
        {
            if (moved.Contains(v)) continue;
            var near = movers.Select(m => (M: m, D: Vector3.Distance(pos[v], pos[m]))).Where(x => x.D < radius).ToList();
            if (near.Count == 0) continue;
            float x = near.Min(n => n.D) / radius;
            float fall = 1 - x * x * (3 - 2 * x); // smooth: 1 at a moved point, 0 at the radius
            var inverse = near.Select(n => 1 / (n.D * n.D + 1e-8f)).ToArray();
            float sum = inverse.Sum();
            result[v] = (near.Select(n => n.M).ToArray(), inverse.Select(w => fall * w / sum).ToArray());
        }
        return result;
    }

    // ---------- Select linked flat faces ----------

    /// Faces joined to the seeds through shared edges, each within `maxAngle` degrees of the face it's reached from
    /// (Blender's Select Linked Flat Faces).
    public static HashSet<int> LinkedFlat(IReadOnlyList<Vector3> pos, IReadOnlyList<int[]> faces, IEnumerable<int> seeds, float maxAngle)
    {
        var edgeFaces = EdgeFaces(faces);
        float cos = MathF.Cos(maxAngle * MathF.PI / 180);
        var normals = faces.Select(f => Newell(pos, f) is var n && n.LengthSquared() > 0 ? Vector3.Normalize(n) : n).ToArray();
        var found = new HashSet<int>(seeds);
        var queue = new Queue<int>(found);
        while (queue.Count > 0)
        {
            int f = queue.Dequeue();
            for (int k = 0; k < faces[f].Length; k++)
                foreach (int g in edgeFaces[Key(faces[f][k], faces[f][(k + 1) % faces[f].Length])])
                    if (!found.Contains(g) && Vector3.Dot(normals[f], normals[g]) >= cos) { found.Add(g); queue.Enqueue(g); }
        }
        return found;
    }

    // ---------- Mirror ----------

    /// For each of `points`, the point at its mirrored position across x = 0 (the game's Mirror plane) within
    /// `tolerance` metres, if there is one. A point on the plane is its own twin.
    public static Dictionary<int, int> Twins(IReadOnlyList<Vector3> pos, IEnumerable<int> points, float tolerance)
    {
        float cell = Math.Max(tolerance, 1e-5f);
        (int, int, int) Cell(Vector3 p) => ((int)MathF.Round(p.X / cell), (int)MathF.Round(p.Y / cell), (int)MathF.Round(p.Z / cell));
        var grid = new Dictionary<(int, int, int), List<int>>();
        for (int v = 0; v < pos.Count; v++)
        {
            if (!grid.TryGetValue(Cell(pos[v]), out var here)) grid[Cell(pos[v])] = here = new();
            here.Add(v);
        }
        var twins = new Dictionary<int, int>();
        foreach (int v in points)
        {
            var m = new Vector3(-pos[v].X, pos[v].Y, pos[v].Z);
            var (x, y, z) = Cell(m);
            int found = -1;
            for (int dx = -1; dx <= 1 && found < 0; dx++) for (int dy = -1; dy <= 1 && found < 0; dy++) for (int dz = -1; dz <= 1 && found < 0; dz++)
                if (grid.TryGetValue((x + dx, y + dy, z + dz), out var near))
                    foreach (int w in near) if (Vector3.Distance(pos[w], m) <= tolerance) { found = w; break; }
            if (found >= 0) twins[v] = found;
        }
        return twins;
    }

    // ---------- Bridge ----------

    /// Bridge (Blender's Bridge Edge Loops): two chains of selected edges (open, or closed loops) joined by a strip of
    /// quads with `cuts` rows of new points across it. `smooth` 0 runs straight across; above it, the strip leaves each
    /// chain the way the face there runs on and curves round into the other (1 is about round, like Blender's
    /// smoothness). The chains' points pair up in order, the second chain turned (and a loop started) where the strip
    /// is shortest, so it doesn't twist. Each chain runs along a
    /// plate's open edges (one face each) or loose edges, and both have as many points. With `twins` (Mirror on) the
    /// mirror image is bridged too, unless the bridge is its own mirror image.
    public static Rebuild Bridge(IReadOnlyList<Vector3> pos, IReadOnlyList<int[]> faces, IEnumerable<(int A, int B)> edges, int cuts, float smooth,
                                 IReadOnlyDictionary<int, int>? twins = null)
    {
        var chains = Chains(edges.Select(e => Key(e.A, e.B)).Where(e => e.Item1 != e.Item2).Distinct().ToList(), out string? why);
        if (why != null) return Rebuild.Fail(why);
        bool mirrorPair = false;
        if (chains.Count == 4 && twins != null)
        {
            // Both sides selected with Mirror on: bridge one side, and the mirror image does the other.
            int MirrorOf(int c) => Enumerable.Range(0, 4).FirstOrDefault(d => d != c && chains[d].Points.All(v => twins.TryGetValue(v, out int t) && chains[c].Points.Contains(t)), -1);
            int a = 0, a2 = MirrorOf(0), b = Enumerable.Range(1, 3).First(c => c != a2);
            if (a2 < 0 || MirrorOf(b) < 0) return Rebuild.Fail("请先选择两条边链（开启镜像时只需一侧）");
            int b2 = MirrorOf(b);
            float Gap(int x, int y) => Vector3.Distance(Middle(pos, chains[x].Points), Middle(pos, chains[y].Points));
            chains = new List<Chain> { chains[a], Gap(a, b) <= Gap(a, b2) ? chains[b] : chains[b2] };
            mirrorPair = true;
        }
        if (chains.Count != 2)
            return Rebuild.Fail(chains.Count < 2 ? "请先选择两条中间有间距的边链" : $"请先选择两条边链，不是 {chains.Count} 条");
        Chain p = chains[0], q = chains[1];
        if (p.Closed != q.Closed) return Rebuild.Fail("一条边链是闭合环，另一条不是");
        int n = p.Points.Count;
        if (q.Points.Count != n) return Rebuild.Fail($"两条边链的点数须相同（{n} 和 {q.Points.Count}）");

        // Pair the points: the second chain forwards or backwards (and, for loops, from each start), shortest strip.
        var best = (Cost: double.MaxValue, Order: Array.Empty<int>());
        foreach (int dir in new[] { 1, -1 })
            for (int start = 0; start < (p.Closed ? n : 1); start++)
            {
                var order = Enumerable.Range(0, n).Select(i => p.Closed ? q.Points[((start + dir * i) % n + n) % n] : q.Points[dir > 0 ? i : n - 1 - i]).ToArray();
                double cost = Enumerable.Range(0, n).Sum(i => (double)Vector3.Distance(pos[p.Points[i]], pos[order[i]]));
                if (cost < best.Cost - 1e-9) best = (cost, order);
            }
        var pa = p.Points.ToArray();
        var qa = best.Order;

        var edgeFaces = EdgeFaces(faces);
        int FaceOn(int a, int b) => edgeFaces.TryGetValue(Key(a, b), out var l) ? (l.Count == 1 ? l[0] : -2) : -1;
        int segments = p.Closed ? n : n - 1;
        var faceP = new int[segments];
        var faceQ = new int[segments];
        for (int i = 0; i < segments; i++)
        {
            int j = (i + 1) % n;
            faceP[i] = FaceOn(pa[i], pa[j]);
            faceQ[i] = FaceOn(qa[i], qa[j]);
            if (faceP[i] == -2 || faceQ[i] == -2) return Rebuild.Fail("桥接板的开放边：这些边中有的两侧都已有面");
        }
        // The strip turns the way the faces at the chains do (each edge run the other way by the strip): all must agree.
        bool Runs(int f, int a, int b) { var c = faces[f]; int k = Array.IndexOf(c, a); return k >= 0 && c[(k + 1) % c.Length] == b; }
        var votes = new List<bool>();
        for (int i = 0; i < segments; i++)
        {
            int j = (i + 1) % n;
            if (faceP[i] >= 0) votes.Add(Runs(faceP[i], pa[i], pa[j]));
            if (faceQ[i] >= 0) votes.Add(Runs(faceQ[i], qa[j], qa[i]));
        }
        if (votes.Distinct().Count() > 1) return Rebuild.Fail("两条边链处的面朝向相反：先翻转一侧（游戏的“翻转”），再桥接");
        bool forward = votes.Count == 0 || votes[0];
        if (votes.Count == 0 && pos.Count > 0)
        {
            // Loose edges on both sides: the strip faces away from the part's middle, as its outside does.
            var strip = Vector3.Zero;
            for (int i = 0; i < segments; i++) strip += Newell(pos, new[] { pa[(i + 1) % n], pa[i], qa[i], qa[(i + 1) % n] }); // as the strip's faces run
            var outward = Middle(pos, pa.Concat(qa)) - Middle(pos, Enumerable.Range(0, pos.Count));
            forward = Vector3.Dot(strip, outward) >= 0;
        }

        // Which way each chain's face runs on past a point: across its edges there, away from the face.
        Vector3 Onward(int[] chain, int[] faceAt, int i)
        {
            var d = Vector3.Zero;
            foreach (int s in new[] { i - 1, i })
            {
                if (!p.Closed && (s < 0 || s >= segments)) continue; // an open chain's ends have one edge
                int si = (s + segments) % segments, f = faceAt[si];
                if (f < 0) continue;
                Vector3 a = pos[chain[si]], b = pos[chain[(si + 1) % n]], nf = Newell(pos, faces[f]);
                var across = Vector3.Cross(b - a, nf);
                if (across.LengthSquared() < 1e-20f) continue;
                across = Vector3.Normalize(across);
                var centre = faces[f].Aggregate(Vector3.Zero, (sum, v) => sum + pos[v]) / faces[f].Length;
                if (Vector3.Dot(across, centre - (a + b) / 2) > 0) across = -across;
                d += across;
            }
            return d.LengthSquared() < 1e-12f ? Vector3.Zero : Vector3.Normalize(d);
        }

        // The rows: the first chain, `cuts` rows of new points, the second chain.
        cuts = Math.Clamp(cuts, 0, 64);
        var points = new List<NewPoint>();
        var rows = new int[cuts + 2][];
        rows[0] = pa;
        rows[cuts + 1] = qa;
        for (int r = 1; r <= cuts; r++) rows[r] = new int[n];
        for (int i = 0; i < n; i++)
        {
            Vector3 a = pos[pa[i]], b = pos[qa[i]];
            float length = Vector3.Distance(a, b);
            Vector3 ta = Onward(pa, faceP, i) * length * Math.Max(0, smooth), tb = -Onward(qa, faceQ, i) * length * Math.Max(0, smooth);
            for (int r = 1; r <= cuts; r++)
            {
                float s = r / (float)(cuts + 1), s2 = s * s, s3 = s2 * s;
                var at = (2 * s3 - 3 * s2 + 1) * a + (s3 - 2 * s2 + s) * ta + (-2 * s3 + 3 * s2) * b + (s3 - s2) * tb;
                rows[r][i] = pos.Count + points.Count;
                points.Add(new NewPoint(at, new[] { (pa[i], 1 - s), (qa[i], s) }));
            }
        }

        // Settings come from the face at the nearer chain (or the other one, or the nearest face for loose edges).
        var all = pos.Concat(points.Select(x => x.P)).ToList();
        int Nearest(Vector3 at) => Enumerable.Range(0, faces.Count).OrderBy(f => Vector3.Distance(faces[f].Aggregate(Vector3.Zero, (s, v) => s + pos[v]) / faces[f].Length, at)).FirstOrDefault(-1);
        var add = new List<NewFace>();
        for (int i = 0; i < segments; i++)
        {
            int j = (i + 1) % n;
            for (int r = 0; r <= cuts; r++)
            {
                var corners = forward ? new[] { rows[r][j], rows[r][i], rows[r + 1][i], rows[r + 1][j] } : new[] { rows[r][i], rows[r][j], rows[r + 1][j], rows[r + 1][i] };
                bool nearP = 2 * r < cuts + 1;
                int source = (nearP ? faceP[i] : faceQ[i]) is int f1 and >= 0 ? f1 : (nearP ? faceQ[i] : faceP[i]) is int f2 and >= 0 ? f2
                    : Nearest(corners.Aggregate(Vector3.Zero, (s, v) => s + all[v]) / corners.Length);
                if (source < 0) return Rebuild.Fail("该部件上没有可复制新面设置的面");
                add.Add(new NewFace(corners, source));
            }
        }

        // Mirror on: the same bridge on the other side, point for point, turned the other way (a mirror image is).
        if (twins != null)
        {
            var used = pa.Concat(qa).ToHashSet();
            bool ownImage = used.All(v => twins.TryGetValue(v, out int t) && used.Contains(t));
            if (!ownImage || mirrorPair)
            {
                if (used.Any(v => !twins.ContainsKey(v)))
                    return mirrorPair ? Rebuild.Fail("镜像：另一侧的边与本侧不对应（请先校正镜像）")
                        : new Rebuild(new(), add, points, null); // no matching edges on the other side: this side only
                var byCorners = new Dictionary<string, int>();
                for (int f = 0; f < faces.Count; f++) byCorners.TryAdd(string.Join(",", faces[f].OrderBy(v => v)), f);
                var image = new Dictionary<int, int>();
                int Image(int v)
                {
                    if (v < pos.Count) return twins[v];
                    if (image.TryGetValue(v, out int w)) return w;
                    var np = points[v - pos.Count];
                    image[v] = w = pos.Count + points.Count;
                    points.Add(new NewPoint(new Vector3(-np.P.X, np.P.Y, np.P.Z), np.Blend.Select(x => (twins[x.V], x.W)).ToArray()));
                    return w;
                }
                var made = add.Select(a => string.Join(",", a.Corners.OrderBy(v => v))).ToHashSet();
                foreach (var nf in add.ToList())
                {
                    var corners = nf.Corners.Select(Image).Reverse().ToArray();
                    if (!made.Add(string.Join(",", corners.OrderBy(v => v)))) continue;
                    var sourceImage = faces[nf.Source].Select(v => twins.TryGetValue(v, out int t) ? t : -1).ToArray();
                    int source = !sourceImage.Contains(-1) && byCorners.TryGetValue(string.Join(",", sourceImage.OrderBy(v => v)), out int s) ? s : nf.Source;
                    add.Add(new NewFace(corners, source));
                }
            }
        }
        return new Rebuild(new(), add, points, null);
    }

    /// A run of selected edges: its points in order, and whether it closes on itself.
    public sealed record Chain(List<int> Points, bool Closed);

    /// The selected edges as chains; `why` says why they can't be (a point where they branch).
    public static List<Chain> Chains(IReadOnlyList<(int, int)> edges, out string? why)
    {
        why = null;
        var next = new Dictionary<int, List<int>>();
        foreach (var (a, b) in edges)
        {
            (next.TryGetValue(a, out var la) ? la : next[a] = new()).Add(b);
            (next.TryGetValue(b, out var lb) ? lb : next[b] = new()).Add(a);
        }
        if (next.Values.Any(l => l.Count > 2)) { why = "所选边在某个点处分叉：请选择两条不分叉的边链"; return new(); }
        var chains = new List<Chain>();
        var seen = new HashSet<int>();
        // Open chains from their ends first, then what's left is closed loops.
        foreach (int start in next.Keys.Where(v => next[v].Count == 1).Concat(next.Keys.Where(v => next[v].Count == 2)).ToList())
        {
            if (seen.Contains(start)) continue;
            var run = new List<int> { start };
            seen.Add(start);
            int at = start, from = -1;
            while (true)
            {
                int to = next[at].FirstOrDefault(w => w != from && !seen.Contains(w), -1);
                if (to < 0) break;
                run.Add(to);
                seen.Add(to);
                from = at;
                at = to;
            }
            bool closed = next[start].Count == 2 && next[at].Contains(start) && run.Count > 2;
            chains.Add(new Chain(run, closed));
        }
        return chains;
    }

    static Vector3 Middle(IReadOnlyList<Vector3> pos, IEnumerable<int> points)
    {
        var l = points.ToList();
        return l.Aggregate(Vector3.Zero, (s, v) => s + pos[v]) / Math.Max(1, l.Count);
    }

    // ---------- Circle ----------

    /// Circle (LoopTools' Circle): the points spread evenly round a true circle on their best-fit plane, round their
    /// middle, as far out as they are on average (or `radius`, if above 0). They keep their order round the middle, and
    /// the circle is turned to move them least. Empty if there are fewer than three, or they lie along a line.
    public static Dictionary<int, Vector3> Circle(IReadOnlyList<Vector3> pos, ICollection<int> points, float radius = 0, Vector3? normal = null)
    {
        var result = new Dictionary<int, Vector3>();
        var list = points.Distinct().ToList();
        if (list.Count < 3) return result;
        var c = Middle(pos, list);
        var n = normal is { } given && given.LengthSquared() > 1e-12f ? Vector3.Normalize(given) : LeastSpread(pos, list, c);
        Vector3 Flat(Vector3 d) => d - n * Vector3.Dot(d, n);
        int far = list.OrderByDescending(v => Flat(pos[v] - c).LengthSquared()).First();
        if (Flat(pos[far] - c).LengthSquared() < 1e-12f) return result;
        var u = Vector3.Normalize(Flat(pos[far] - c));
        var w = Vector3.Cross(n, u);
        var angle = list.ToDictionary(v => v, v => Math.Atan2(Vector3.Dot(pos[v] - c, w), Vector3.Dot(pos[v] - c, u)));
        var order = list.OrderBy(v => angle[v]).ToList();
        int count = order.Count;
        // Along a line: the points, joined in turn, enclose next to nothing.
        double area = 0;
        for (int k = 0; k < count; k++)
        {
            Vector3 a = Flat(pos[order[k]] - c), b = Flat(pos[order[(k + 1) % count]] - c);
            area += Vector3.Dot(Vector3.Cross(a, b), n) / 2;
        }
        double spread = list.Average(v => Flat(pos[v] - c).Length());
        if (Math.Abs(area) < 0.05 * spread * spread) return result;
        float r = radius > 0 ? radius : (float)spread;
        // Evenly apart, each point as near its own turn as can be: the circle's start is the circular mean of offsets.
        double sx = 0, sy = 0;
        for (int k = 0; k < count; k++)
        {
            double off = angle[order[k]] - 2 * Math.PI * k / count;
            sx += Math.Cos(off);
            sy += Math.Sin(off);
        }
        double start = Math.Atan2(sy, sx);
        for (int k = 0; k < count; k++)
        {
            double a = start + 2 * Math.PI * k / count;
            result[order[k]] = c + r * ((float)Math.Cos(a) * u + (float)Math.Sin(a) * w);
        }
        return result;
    }

    // ---------- Fix mirror ----------

    public enum MirrorKeep { Halfway, Right, Left }

    /// Fix mirror: points that are nearly each other's mirror image (within `tolerance`) made exactly so, and points
    /// within it of the centre put on it, so the editor's Mirror pairs them again (it wants them to a fraction of a
    /// millimetre). `keep` says which side stays put (+x is the vehicle's right); Halfway moves both to meet. Starting
    /// from `points` (their partners anywhere). Also returns the points left with no mirror image: the two sides differ
    /// there (merged, split or filled on one side only).
    public static (Dictionary<int, Vector3> Moved, List<int> Unmatched) FixMirror(IReadOnlyList<Vector3> pos, IEnumerable<int> points, float tolerance, MirrorKeep keep)
    {
        var start = points.Distinct().ToList();
        var moved = new Dictionary<int, Vector3>();
        var unmatched = new List<int>();
        // Each candidate pair, closest first, so every point takes its best partner.
        var candidates = new List<(float D, int R, int L)>();
        var rights = pos.Select((p, i) => i).Where(i => pos[i].X > 0).ToList();
        var lefts = pos.Select((p, i) => i).Where(i => pos[i].X < 0).ToList();
        float cell = Math.Max(tolerance, 1e-5f);
        (int, int, int) Cell(Vector3 v) => ((int)MathF.Floor(v.X / cell), (int)MathF.Floor(v.Y / cell), (int)MathF.Floor(v.Z / cell));
        var grid = new Dictionary<(int, int, int), List<int>>();
        foreach (int l in lefts) (grid.TryGetValue(Cell(pos[l]), out var here) ? here : grid[Cell(pos[l])] = new()).Add(l);
        var wanted = start.ToHashSet();
        foreach (int r in rights)
        {
            var m = new Vector3(-pos[r].X, pos[r].Y, pos[r].Z);
            var (x, y, z) = Cell(m);
            for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) for (int dz = -1; dz <= 1; dz++)
                if (grid.TryGetValue((x + dx, y + dy, z + dz), out var near))
                    foreach (int l in near)
                        if ((wanted.Contains(r) || wanted.Contains(l)) && Vector3.Distance(pos[l], m) <= tolerance) candidates.Add((Vector3.Distance(pos[l], m), r, l));
        }
        var paired = new HashSet<int>();
        foreach (var (_, r, l) in candidates.OrderBy(c => c.D))
        {
            if (paired.Contains(r) || paired.Contains(l)) continue;
            paired.Add(r);
            paired.Add(l);
            Vector3 right = pos[r], left = pos[l];
            Vector3 meet = keep switch
            {
                MirrorKeep.Right => right,
                MirrorKeep.Left => new Vector3(-left.X, left.Y, left.Z),
                _ => new Vector3((right.X - left.X) / 2, (right.Y + left.Y) / 2, (right.Z + left.Z) / 2),
            };
            if (Vector3.DistanceSquared(meet, right) > 1e-14f) moved[r] = meet;
            var image = new Vector3(-meet.X, meet.Y, meet.Z);
            if (Vector3.DistanceSquared(image, left) > 1e-14f) moved[l] = image;
        }
        foreach (int v in start)
        {
            if (paired.Contains(v)) continue;
            if (Math.Abs(pos[v].X) <= tolerance) { if (pos[v].X != 0) moved[v] = new Vector3(0, pos[v].Y, pos[v].Z); }
            else unmatched.Add(v);
        }
        return (moved, unmatched);
    }

    // ---------- Merge points ----------

    /// Merge points into `keep` (the game's M, done again for the other side under Mirror): every face using them uses
    /// `keep` instead; a side between two of them goes, and a face left with under three corners goes too. The caller
    /// moves `keep` to where the merged point belongs. Refused if a face would be pinched (two of its corners merged
    /// that aren't next to each other).
    public static Rebuild MergePoints(IReadOnlyList<Vector3> pos, IReadOnlyList<int[]> faces, ICollection<int> points, int keep)
    {
        var merged = points.Append(keep).ToHashSet();
        if (merged.Count < 2) return Rebuild.Fail("请先选择两个或以上要合并的点");
        var remove = new List<int>();
        var add = new List<NewFace>();
        for (int f = 0; f < faces.Count; f++)
        {
            if (!faces[f].Any(merged.Contains)) continue;
            var mapped = faces[f].Select(v => merged.Contains(v) ? keep : v).ToArray();
            var corners = mapped.Where((v, k) => v != mapped[(k + 1) % mapped.Length]).ToArray();
            if (corners.Length == 0) corners = new[] { keep };
            if (corners.Distinct().Count() != corners.Length) return Rebuild.Fail("这样合并会把某个面掐窄（它的两个不相邻角点被并到了一起）");
            remove.Add(f);
            if (corners.Length >= 3) add.Add(new NewFace(corners, f));
        }
        return new Rebuild(remove, add, new(), null);
    }

    // ---------- helpers ----------

    static Dictionary<(int, int), List<int>> EdgeFaces(IReadOnlyList<int[]> faces)
    {
        var map = new Dictionary<(int, int), List<int>>();
        for (int f = 0; f < faces.Count; f++)
            for (int k = 0; k < faces[f].Length; k++)
            {
                var key = Key(faces[f][k], faces[f][(k + 1) % faces[f].Length]);
                if (!map.TryGetValue(key, out var l)) map[key] = l = new();
                l.Add(f);
            }
        return map;
    }

    /// The corner starting the face's side along `edge`, or -1.
    static int SideIndex(int[] face, (int, int) edge)
    {
        for (int k = 0; k < face.Length; k++) if (Key(face[k], face[(k + 1) % face.Length]) == edge) return k;
        return -1;
    }

    static Vector3 Newell(IReadOnlyList<Vector3> pos, IReadOnlyList<int> loop)
    {
        var n = Vector3.Zero;
        if (loop.Count < 3) return n;
        // Fanned from the loop's own corner: products of absolute coordinates lose the normal of a small face far from the origin.
        var origin = pos[loop[0]];
        for (int k = 1; k + 1 < loop.Count; k++) n += Vector3.Cross(pos[loop[k]] - origin, pos[loop[k + 1]] - origin);
        return n;
    }

    static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);

    static (int, int) Key(int a, int b) => a < b ? (a, b) : (b, a);
}
