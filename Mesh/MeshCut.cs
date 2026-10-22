using System.Numerics;
using System.Text.Json.Nodes;

namespace SprocketTools;

/// Boolean cut of a plate mesh by closed shapes (any shape, dents and all). Faces the shapes pass through are split
/// along the shapes' surfaces, and the pieces inside a shape are removed (a hole of that shape). With `pocket`, the
/// parts of each shape's surface that lie inside the plate structure become new plates, facing out, so the cut is a
/// recess with walls and a floor. Pieces of one face are merged back and turned into quads where they can be, the
/// mesh stays joined (no cracks), and rivets on changed faces move onto the new faces. Plain maths on the JSON.
public static class MeshCut
{
    /// A closed shape in the cut mesh's space: vertices, faces, and each face corner's thickness (mm) and thicken mode.
    public sealed record Solid(IReadOnlyList<Vector3> Verts, IReadOnlyList<int[]> Faces, IReadOnlyList<float[]> Thickness, IReadOnlyList<byte[]> Modes);

    public sealed record Result(int FacesCut, int PocketFaces, double ArmourChange, int RivetsMoved, int RivetsDropped);

    const float Eps = 1e-5f;    // 0.01 mm: this close to a plane counts as on it
    const float Tiny = 1e-9f;   // m²: slivers smaller than this are dropped
    const float Weld = 1e-5f;   // corners closer than this are the same vertex
    const float Flat = 2e-4f;   // 0.2 mm: pieces this close to a surface lie on it (touching isn't cutting)

    readonly record struct Plane(Vector3 N, float D) { public float Dist(Vector3 p) => Vector3.Dot(N, p) - D; }

    sealed class Tri
    {
        public Vector3 A, B, C, Min, Max;
        public Plane Plane;
        public Tri(Vector3 a, Vector3 b, Vector3 c)
        {
            A = a; B = b; C = c;
            var n = Vector3.Cross(b - a, c - a);
            Plane = new Plane(n.LengthSquared() > 0 ? Vector3.Normalize(n) : Vector3.Zero, 0);
            Plane = Plane with { D = Vector3.Dot(Plane.N, a) };
            Min = Vector3.Min(a, Vector3.Min(b, c)); Max = Vector3.Max(a, Vector3.Max(b, c));
        }
        public bool Degenerate => Plane.N == Vector3.Zero;
    }

    /// A closed shape's triangles filed by where they lie across (y, z), so a question about one spot looks only at the
    /// triangles there, not all of them (a detailed add-on has thousands, asked about thousands of pieces).
    sealed class TriGrid
    {
        readonly List<Tri> tris;
        readonly List<int>[] cells;
        readonly int n;
        readonly float y0, z0, dy, dz;

        public TriGrid(List<Tri> tris)
        {
            this.tris = tris;
            n = Math.Clamp((int)Math.Sqrt(tris.Count / 4.0), 1, 64);
            var min = tris.Aggregate(new Vector3(float.MaxValue), (m, t) => Vector3.Min(m, t.Min));
            var max = tris.Aggregate(new Vector3(float.MinValue), (m, t) => Vector3.Max(m, t.Max));
            (y0, z0) = (min.Y, min.Z);
            dy = Math.Max(1e-6f, (max.Y - min.Y) / n);
            dz = Math.Max(1e-6f, (max.Z - min.Z) / n);
            cells = Enumerable.Range(0, n * n).Select(_ => new List<int>()).ToArray();
            for (int i = 0; i < tris.Count; i++)
                foreach (int c in Cells(tris[i].Min.Y - Flat, tris[i].Max.Y + Flat, tris[i].Min.Z - Flat, tris[i].Max.Z + Flat)) cells[c].Add(i);
        }

        IEnumerable<int> Cells(float yMin, float yMax, float zMin, float zMax)
        {
            int Clamp(float v) => Math.Clamp((int)MathF.Floor(v), 0, n - 1);
            int ya = Clamp((yMin - y0) / dy), yb = Clamp((yMax - y0) / dy), za = Clamp((zMin - z0) / dz), zb = Clamp((zMax - z0) / dz);
            for (int y = ya; y <= yb; y++)
                for (int z = za; z <= zb; z++) yield return y * n + z;
        }

        /// The triangles whose box, grown by `pad`, meets the box min-max, in their original order.
        public List<Tri> Near(Vector3 min, Vector3 max, float pad)
        {
            var found = new SortedSet<int>();
            foreach (int c in Cells(min.Y - pad, max.Y + pad, min.Z - pad, max.Z + pad))
                foreach (int i in cells[c])
                    if (Overlaps(min, max, tris[i].Min - new Vector3(pad), tris[i].Max + new Vector3(pad))) found.Add(i);
            return found.Select(i => tris[i]).ToList();
        }

        /// Inside the shape, and not on its surface: a line from p along +x crosses the shape's faces; counting each
        /// crossing by the way it faces gives the winding number (1 inside a closed shape, 0 outside), as the full sum of
        /// solid angles does, from only the triangles along the line. The line runs a hair off p, so it never runs
        /// exactly along an edge or through a corner.
        public bool Inside(Vector3 p)
        {
            float py = p.Y + 1.37e-6f, pz = p.Z + 2.71e-6f;
            int winding = 0;
            foreach (int c in Cells(py, py, pz, pz))
                foreach (int i in cells[c])
                {
                    var t = tris[i];
                    if (t.Max.X < p.X || py < t.Min.Y || py > t.Max.Y || pz < t.Min.Z || pz > t.Max.Z || MathF.Abs(t.Plane.N.X) < 1e-9f) continue;
                    // Where the line meets the triangle's plane, and whether that's inside the triangle (seen along x).
                    float x = (t.Plane.D - t.Plane.N.Y * py - t.Plane.N.Z * pz) / t.Plane.N.X;
                    if (x <= p.X) continue;
                    float E(Vector3 a, Vector3 b) => (b.Y - a.Y) * (pz - a.Z) - (b.Z - a.Z) * (py - a.Y);
                    float e0 = E(t.A, t.B), e1 = E(t.B, t.C), e2 = E(t.C, t.A);
                    if ((e0 < 0 || e1 < 0 || e2 < 0) && (e0 > 0 || e1 > 0 || e2 > 0)) continue;
                    winding += t.Plane.N.X > 0 ? 1 : -1;
                }
            return winding != 0 && !OnSurface(Near(p, p, Flat), p);
        }
    }

    /// A face corner while cutting. V = vertex number (-1 until given one); A,B = the original edge a new corner lies
    /// on (-1 if none); Other = the vertex its thicken edge goes to; Raw = stored thicken edge value to keep when Other
    /// is -1; Fallback = give it the edge to its next corner.
    record struct Corner(Vector3 P, float T, int V, int A, int B, byte Mode, int Other, ushort Raw, bool Fallback);

    sealed class Poly
    {
        public List<Corner> C = new();
        public int Source;      // original face (target) or shape face (pocket)
        public bool Pocket;     // a new wall/floor plate from a cutting shape
        public int Group;       // pieces with the same group may merge back together
        public bool Changed;
    }

    /// `fill`: how the faces around each cut are made (see Fill.Mode); the fewest points by default.
    public static Result Cut(JsonObject meshData, IReadOnlyList<Solid> shapes, bool pocket, Fill.Mode fill = Fill.Mode.Fewest)
    {
        var mesh = meshData["mesh"]!.AsObject();
        var raw = mesh["vertices"]!.AsArray().Select(F).ToArray();
        var verts = Enumerable.Range(0, raw.Length / 3).Select(i => new Vector3(raw[3 * i], raw[3 * i + 1], raw[3 * i + 2])).ToList();
        int originalVerts = verts.Count;
        var e = mesh["edges"]!.AsArray().Select(x => x!.GetValue<int>()).ToArray();
        var oldEdges = Enumerable.Range(0, e.Length / 2).Select(i => (e[2 * i], e[2 * i + 1])).ToList();
        var oldFlags = mesh["edgeFlags"]!.AsArray().Select(x => x!.GetValue<int>()).ToList();
        var faceNodes = mesh["faces"]!.AsArray().Select(x => x!.AsObject()).ToList();

        var original = new List<Poly>();
        for (int j = 0; j < faceNodes.Count; j++)
        {
            var node = faceNodes[j];
            var v = node["v"]!.AsArray().Select(x => x!.GetValue<int>()).ToArray();
            var t = node["t"]!.AsArray().Select(F).ToArray();
            long tm = L(node["tm"]), te = L(node["te"]);
            var poly = new Poly { Source = j, Group = j };
            for (int k = 0; k < v.Length; k++)
            {
                ushort r = (ushort)((ulong)te >> (16 * k));
                int other = r < oldEdges.Count && (oldEdges[r].Item1 == v[k] || oldEdges[r].Item2 == v[k])
                    ? oldEdges[r].Item1 == v[k] ? oldEdges[r].Item2 : oldEdges[r].Item1 : -1;
                poly.C.Add(new Corner(verts[v[k]], t[k], v[k], -1, -1, (byte)((ulong)tm >> (8 * k)), other, r, false));
            }
            original.Add(poly);
        }

        var shapeTris = shapes.Select(s => s.Faces.SelectMany(f => FanTris(f.Select(i => s.Verts[i]).ToList())).Where(t => !t.Degenerate).ToList()).ToList();
        var allShapeTris = shapeTris.SelectMany(x => x).ToList();
        var shapeMin = allShapeTris.Aggregate(new Vector3(float.MaxValue), (m, t) => Vector3.Min(m, t.Min)) - new Vector3(Flat);
        var shapeMax = allShapeTris.Aggregate(new Vector3(float.MinValue), (m, t) => Vector3.Max(m, t.Max)) + new Vector3(Flat);
        var grids = shapeTris.Select(t => new TriGrid(t)).ToList();
        var allGrid = new TriGrid(allShapeTris);
        bool InsideShape(Vector3 p) => grids.Any(g => g.Inside(p));

        // 1) Split target faces the shapes pass through, and drop the pieces inside a shape.
        var result = new List<Poly>();
        var replaced = new HashSet<int>();
        foreach (var face in original)
        {
            var (min, max) = Bounds(face.C);
            if (!Overlaps(min, max, shapeMin, shapeMax)) { result.Add(face); continue; }
            var near = allGrid.Near(min, max, Eps);
            var pieces = new List<List<Corner>>();
            Shatter(face.C, near, 0, pieces, verts);
            var kept = pieces.Where(p => !InsideShape(Centre(p))).ToList();
            if (pieces.Count == 1 && kept.Count == 1) { result.Add(face); continue; } // clear of every shape
            replaced.Add(face.Source);
            result.AddRange(kept.Select(p => new Poly { C = p, Source = face.Source, Group = face.Source, Changed = true }));
        }
        int facesCut = replaced.Count;

        // Cut faces lying flat against each other (same plane, same armour, sharing an edge) are rebuilt as one
        // region, so a hole across their shared edge still gets a clean ring around it.
        var groupOf = original.ToDictionary(f => f.Source, f => f.Source);
        int Find(int s) { while (groupOf[s] != s) s = groupOf[s] = groupOf[groupOf[s]]; return s; }
        var cutEdges = new Dictionary<(int, int), List<int>>();
        foreach (int s in replaced)
            for (int k = 0; k < original[s].C.Count; k++)
            {
                var key = Key(original[s].C[k].V, original[s].C[(k + 1) % original[s].C.Count].V);
                if (!cutEdges.TryGetValue(key, out var l)) cutEdges[key] = l = new();
                l.Add(s);
            }
        foreach (var l in cutEdges.Values.Where(l => l.Count == 2))
            if (Flush(original[l[0]], original[l[1]])) groupOf[Find(l[0])] = Find(l[1]);
        foreach (var p in result.Where(p => p.Changed && !p.Pocket)) p.Group = Find(p.Source);

        // 2) Pocket: the shapes' surfaces inside the structure become plates, turned to face out of the structure.
        int pocketFaces = 0;
        if (pocket && facesCut > 0)
        {
            var targetTris = original.SelectMany(f => FanTris(f.C.Select(c => c.P).ToList())).Where(t => !t.Degenerate).ToList();
            var groups = new List<(Plane Plane, float T, int Id)>();
            int nextSource = 0;
            for (int s = 0; s < shapes.Count; s++)
                for (int fi = 0; fi < shapes[s].Faces.Count; fi++, nextSource++)
                {
                    var f = shapes[s].Faces[fi];
                    var corners = f.Select((vi, k) => new Corner(shapes[s].Verts[vi], shapes[s].Thickness[fi][k], -1, -1, -1,
                        shapes[s].Modes[fi][k] == 4 ? (byte)1 : shapes[s].Modes[fi][k], -1, 0, true)).Reverse().ToList(); // reversed: faces into the old solid
                    var n = HoleRing.Normal(corners.Select(c => c.P).ToList());
                    if (n.LengthSquared() < 1e-14f) continue;
                    var plane = new Plane(Vector3.Normalize(n), 0);
                    plane = plane with { D = Vector3.Dot(plane.N, corners[0].P) };
                    float thick = corners.Average(c => c.T);
                    // Coplanar shape faces of equal thickness may merge into one plate (e.g. a cylinder's cap).
                    // Within 0.5 mm and about 0.8°: a placed, rotated add-on's cap isn't exactly flat in float maths.
                    int match = groups.FindIndex(g => Vector3.Dot(g.Plane.N, plane.N) > 0.9999f && Math.Abs(g.Plane.D - plane.D) < 5e-4f && Math.Abs(g.T - thick) < 0.5f);
                    int group = match >= 0 ? groups[match].Id : -1 - groups.Count; // negative: never mixed up with a target face's group
                    if (match < 0) groups.Add((plane, thick, group));
                    var (min, max) = Bounds(corners);
                    var near = targetTris.Where(t => Overlaps(min, max, t.Min - new Vector3(Eps), t.Max + new Vector3(Eps))).ToList();
                    var pieces = new List<List<Corner>>();
                    Shatter(corners, near, 0, pieces, verts);
                    foreach (var p in pieces)
                    {
                        var c = Centre(p);
                        if (Math.Abs(Winding(targetTris, c)) <= 0.5 || OnSurface(targetTris, c)) continue;
                        // Keep only walls inside the part of the shape that actually cuts: other shapes may overlap it.
                        if (grids.Where((_, i) => i != s).Any(g => g.Inside(c))) continue;
                        result.Add(new Poly { C = p, Source = nextSource, Pocket = true, Group = group, Changed = true });
                        pocketFaces++;
                    }
                }
        }
        if (facesCut == 0) return new Result(0, 0, 0, 0, 0);

        // 3) Number the new corners, sharing a vertex where pieces meet (including existing vertices).
        var grid = new Dictionary<(int, int, int), List<int>>();
        (int, int, int) Cell(Vector3 p) => ((int)Math.Floor(p.X / (Weld * 10)), (int)Math.Floor(p.Y / (Weld * 10)), (int)Math.Floor(p.Z / (Weld * 10)));
        void Register(int i) { var k = Cell(verts[i]); if (!grid.TryGetValue(k, out var l)) grid[k] = l = new(); l.Add(i); }
        for (int i = 0; i < verts.Count; i++) Register(i);
        var carrier = new Dictionary<int, (int A, int B)>();
        int VertexAt(Corner c)
        {
            var (x, y, z) = Cell(c.P);
            for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) for (int dz = -1; dz <= 1; dz++)
                if (grid.TryGetValue((x + dx, y + dy, z + dz), out var list))
                    foreach (int i in list) if (Vector3.DistanceSquared(verts[i], c.P) < Weld * Weld) return i;
            verts.Add(c.P); Register(verts.Count - 1);
            if (c.A >= 0) carrier[verts.Count - 1] = (c.A, c.B);
            return verts.Count - 1;
        }
        foreach (var poly in result.Where(p => p.Changed))
        {
            for (int k = 0; k < poly.C.Count; k++)
                if (poly.C[k].V < 0) { int v = VertexAt(poly.C[k]); poly.C[k] = poly.C[k] with { V = v, P = verts[v] }; }
            poly.C = poly.C.Where((c, k) => c.V != poly.C[(k + 1) % poly.C.Count].V).ToList();
        }
        result.RemoveAll(p => p.Changed && (p.C.Count < 3 || Area(p.C) <= Tiny));

        // 4) Any face with a new vertex lying on one of its edges takes it as a corner, so the mesh stays joined.
        var added = result.Where(p => p.Changed).SelectMany(p => p.C).Select(c => c.V).Where(v => v >= originalVerts).Distinct().ToList();
        if (added.Count > 0)
        {
            var region = Bounds(added.Select(i => new Corner(verts[i], 0, i, -1, -1, 0, -1, 0, false)).ToList());
            foreach (var poly in result)
            {
                var (min, max) = Bounds(poly.C);
                if (!Overlaps(min, max, region.Min - new Vector3(Eps), region.Max + new Vector3(Eps))) continue;
                var grown = new List<Corner>();
                for (int k = 0; k < poly.C.Count; k++)
                {
                    Corner p = poly.C[k], q = poly.C[(k + 1) % poly.C.Count];
                    grown.Add(p);
                    var d = q.P - p.P;
                    float len2 = d.LengthSquared();
                    if (len2 < Weld * Weld) continue;
                    foreach (var (i, s) in added.Where(i => i != p.V && i != q.V)
                                 .Select(i => (i, s: Vector3.Dot(verts[i] - p.P, d) / len2))
                                 .Where(x => x.s > 0 && x.s < 1 && Vector3.DistanceSquared(p.P + x.s * d, verts[x.i]) < Weld * Weld)
                                 .OrderBy(x => x.s))
                        grown.Add(new Corner(verts[i], p.T + (q.T - p.T) * s, i, -1, -1, p.Mode, -1, p.Raw, p.Other >= 0 || p.Fallback));
                }
                if (grown.Count != poly.C.Count) { poly.C = grown; poly.Changed = true; }
            }
        }

        // 5) Rebuild each changed face (and each pocket plate) from its outline, the outer edge plus hole rims, with
        //    corners that only sit on a straight edge dropped, and fill it with easy-to-edit faces (see Fill). An outline
        //    that touches itself at a corner instead has its pieces merged back as they are.
        var outlines = new List<(List<Poly> Pieces, List<List<int>> Loops, Dictionary<int, Corner> Corners, Vector3 Normal)>();
        var awkward = new List<List<Poly>>();
        foreach (var group in result.Where(p => p.Changed).GroupBy(p => (p.Pocket, p.Group)).Select(g => g.ToList()).ToList())
        {
            var loops = Outline(group);
            if (loops == null) { awkward.Add(group); continue; }
            var corners = new Dictionary<int, Corner>();
            foreach (var p in group) foreach (var c in p.C) corners.TryAdd(c.V, c);
            var normal = HoleRing.Normal((group[0].Pocket ? group[0].C : original[group[0].Source].C).Select(c => c.P).ToList());
            outlines.Add((group, loops, corners, normal));
        }
        // Straight-edge corners go from every outline at once, unless a face outside the outlines still uses them.
        var pinned = result.Where(p => !p.Changed).Concat(awkward.SelectMany(g => g)).SelectMany(p => p.C.Select(c => c.V)).ToHashSet();
        var loopsWith = new Dictionary<int, List<List<int>>>();
        foreach (var o in outlines) foreach (var loop in o.Loops) foreach (int v in loop) { if (!loopsWith.TryGetValue(v, out var l)) loopsWith[v] = l = new(); l.Add(loop); }
        foreach (var (v, loops) in loopsWith)
            if (v >= originalVerts && !pinned.Contains(v) && loops.All(l => l.Count > 3 && StraightAt(l, l.IndexOf(v), verts)))
                foreach (var l in loops) l.Remove(v);
        foreach (var o in outlines)
        {
            foreach (var p in o.Pieces) result.Remove(p);
            var outers = o.Loops.Where(l => Vector3.Dot(HoleRing.Normal(l.Select(v => verts[v]).ToList()), o.Normal) > 0).ToList();
            var holes = o.Loops.Except(outers).ToList();
            foreach (var outer in outers)
            {
                var mine = outers.Count == 1 ? holes : holes.Where(h => InsideLoop(outer, verts[h[0]], o.Normal, verts)).ToList();
                var extra = new List<Fill.Added>();
                int first = verts.Count;
                var faces = Fill.Region(verts, outer, mine, o.Normal, fill == Fill.Mode.Fewest ? null : extra, fill == Fill.Mode.Light, fill);
                for (int i = 0; i < extra.Count; i++)
                {
                    // A new vertex takes its thickness from the rim and outer corners it lies between.
                    var a = extra[i];
                    var main = o.Corners[a.Blend.OrderByDescending(b => b.W).First().V];
                    float total = a.Blend.Sum(b => b.W);
                    float t = total > 0 ? a.Blend.Sum(b => b.W * o.Corners[b.V].T) / total : main.T;
                    o.Corners[first + i] = new Corner(a.P, t, first + i, -1, -1, main.Mode, -1, main.Raw, true);
                }
                var src = o.Pieces[0];
                result.AddRange(faces.Select(f => new Poly { C = f.Select(v => o.Corners[v]).ToList(), Source = src.Source, Pocket = src.Pocket, Group = src.Group, Changed = true }));
            }
        }
        var awkwardPolys = new HashSet<Poly>();
        foreach (var group in awkward)
        {
            var polys = group.ToList();
            for (bool merged = true; merged;)
            {
                merged = false;
                for (int a = 0; a < polys.Count && !merged; a++)
                    for (int b = a + 1; b < polys.Count && !merged; b++)
                        if (TryMerge(polys[a], polys[b]) is { } union)
                        {
                            result.Remove(polys[a]); result.Remove(polys[b]); polys.RemoveAt(b); polys.RemoveAt(a);
                            result.Add(union); polys.Add(union); merged = true;
                        }
            }
            awkwardPolys.UnionWith(polys);
        }
        var awkwardGroups = awkward.Select(g => (g[0].Pocket, g[0].Group)).ToHashSet();
        result = result.SelectMany(p => awkwardPolys.Contains(p) ? Tessellate(p) : new[] { p }).ToList();
        foreach (var group in result.Where(p => p.Changed && p.C.Count == 3 && awkwardGroups.Contains((p.Pocket, p.Group))).GroupBy(p => (p.Pocket, p.Group)).ToList())
            foreach (var (a, b, quad) in PairUp(group.ToList()))
            {
                result.Remove(a); result.Remove(b);
                result.Add(new Poly { C = quad, Source = a.Source, Pocket = a.Pocket, Group = a.Group, Changed = true });
            }

        // A face that came out exactly as it was is left alone (keeps its rivets and settings untouched).
        foreach (var poly in result.Where(p => p.Changed && !p.Pocket))
            if (SameLoop(poly.C.Select(c => c.V).ToList(), original[poly.Source].C.Select(c => c.V).ToList()) && result.Count(q => q.Source == poly.Source && !q.Pocket) == 1)
            { poly.C = original[poly.Source].C; poly.Changed = false; }
        var survivors = result.Where(f => !f.Pocket).Select(f => f.Source).ToHashSet();
        replaced = result.Where(f => f.Changed && !f.Pocket).Select(f => f.Source).Concat(original.Select(f => f.Source).Where(j => !survivors.Contains(j))).ToHashSet();
        pocketFaces = result.Count(f => f.Pocket);
        if (replaced.Count == 0) return new Result(0, 0, 0, 0, 0); // only touched the shapes' surfaces: nothing to change

        // 6) Rivets: decode where each one sits before anything moves, then place it on the face now under it.
        var rivets = meshData["rivets"]?.AsObject();
        var nodes = rivets?["nodes"]?.AsArray().Select(n => n!.AsObject()).ToList() ?? new();
        var positions = nodes.Select(n => RivetPosition(n, original, verts)).ToList();

        // 7) Rebuild the edge list: surviving edges keep their order, new ones follow; pieces of an edge keep its flag.
        var oldIndex = new Dictionary<(int, int), int>();
        for (int i = 0; i < oldEdges.Count; i++) oldIndex.TryAdd(Key(oldEdges[i].Item1, oldEdges[i].Item2), i);
        var used = new HashSet<(int, int)>();
        foreach (var f in result) for (int k = 0; k < f.C.Count; k++) used.Add(Key(f.C[k].V, f.C[(k + 1) % f.C.Count].V));
        var usedBefore = new HashSet<(int, int)>();
        foreach (var f in original) for (int k = 0; k < f.C.Count; k++) usedBefore.Add(Key(f.C[k].V, f.C[(k + 1) % f.C.Count].V));
        var edges = new List<(int, int)>();
        var flags = new List<int>();
        var newIndex = new Dictionary<(int, int), int>();
        void AddEdge((int, int) key, int flag) { newIndex[key] = edges.Count; edges.Add(key); flags.Add(flag); }
        for (int i = 0; i < oldEdges.Count; i++)
        {
            var key = Key(oldEdges[i].Item1, oldEdges[i].Item2);
            if (!newIndex.ContainsKey(key) && (used.Contains(key) || !usedBefore.Contains(key))) AddEdge(key, oldFlags[i]);
        }
        (int, int)? On(int v) => carrier.TryGetValue(v, out var ab) ? Key(ab.A, ab.B) : null;
        bool Within(int v, (int, int) edge) => v == edge.Item1 || v == edge.Item2 || On(v) == edge;
        foreach (var f in result)
            for (int k = 0; k < f.C.Count; k++)
            {
                var key = Key(f.C[k].V, f.C[(k + 1) % f.C.Count].V);
                if (newIndex.ContainsKey(key)) continue;
                var line = On(key.Item1) ?? On(key.Item2);
                bool piece = line is { } ab && Within(key.Item1, ab) && Within(key.Item2, ab) && oldIndex.ContainsKey(ab);
                AddEdge(key, piece ? oldFlags[oldIndex[line!.Value]] : 0);
            }

        // 8) Drop vertices only removed faces used (keep any the mesh had loose), and renumber.
        var usedOriginally = oldEdges.SelectMany(x => new[] { x.Item1, x.Item2 }).Concat(original.SelectMany(f => f.C.Select(c => c.V))).ToHashSet();
        var keepVerts = new SortedSet<int>(edges.SelectMany(x => new[] { x.Item1, x.Item2 }));
        foreach (var f in result) foreach (var c in f.C) keepVerts.Add(c.V);
        for (int i = 0; i < originalVerts; i++) if (!usedOriginally.Contains(i)) keepVerts.Add(i);
        var renumber = new Dictionary<int, int>();
        foreach (var i in keepVerts) renumber[i] = renumber.Count;

        // 9) Write the faces. Untouched faces keep everything but vertex numbers and (renumbered) thicken edges.
        var template = faceNodes.Count > 0 ? faceNodes[0] : new JsonObject();
        var outFaces = new JsonArray();
        var faceOf = new Dictionary<Poly, int>();
        foreach (var f in result)
        {
            var node = Clone(f.Pocket ? template : faceNodes[f.Source]);
            var cs = f.C;
            node["v"] = new JsonArray(cs.Select(c => (JsonNode?)renumber[c.V]).ToArray());
            ulong te = 0; uint tm = 0; bool moved = false, unset = true;
            for (int k = 0; k < cs.Count; k++)
            {
                var c = cs[k];
                byte mode = c.Mode;
                int edge = c.Other >= 0 ? EdgeTowards(c.V, c.Other) : -1;
                if (edge < 0 && (c.Fallback || c.Other >= 0))
                {
                    edge = newIndex[Key(c.V, cs[(k + 1) % cs.Count].V)];
                    if (mode == 4) mode = 1; // its hand-picked thicken edge is gone: back to Auto
                }
                ushort r = edge >= 0 ? (ushort)edge : c.Raw;
                if (r == 0xFFFF && mode == 4) mode = 1; // no thicken edge left to hand-pick: Auto
                moved |= r != c.Raw;
                unset &= r == 0xFFFF;
                te |= (ulong)r << (16 * k);
                tm |= (uint)mode << (8 * k);
            }
            if (f.Changed)
            {
                node["t"] = new JsonArray(cs.Select(c => (JsonNode?)(int)Math.Round(c.T)).ToArray());
                node["tm"] = (int)tm;
            }
            if (f.Changed || moved) node["te"] = Te(unset ? ulong.MaxValue : te);
            faceOf[f] = outFaces.Count;
            outFaces.Add(node);
        }

        // Rivets on untouched faces stay; on changed faces they move to the face now under them; in the hole they go.
        int movedRivets = 0, droppedRivets = 0;
        if (nodes.Count > 0)
        {
            var keep = new List<(JsonObject Node, int Old)>();
            for (int i = 0; i < nodes.Count; i++)
            {
                var n = Clone(nodes[i]);
                int src = n["face"]!.GetValue<int>();
                var home = result.FirstOrDefault(f => !f.Pocket && !f.Changed && f.Source == src);
                if (home != null) { n["face"] = faceOf[home]; keep.Add((n, i)); continue; }
                Poly? on = null;
                (float U, float V, float W, int Offset)? at = null;
                if (positions[i] is { } p)
                    foreach (var f in result.Where(f => !f.Pocket && (f.Source == src || (f.Changed && f.Group == Find(src)))))
                        if ((at = RivetAt(f.C.Select(c => c.P).ToList(), p)) != null) { on = f; break; }
                if (on == null || at is not { } w) { droppedRivets++; continue; }
                n["face"] = faceOf[on]; n["u"] = w.U; n["v"] = w.V; n["w"] = w.W; n["faceOffset"] = w.Offset;
                keep.Add((n, i)); movedRivets++;
            }
            var index = keep.Select((k, i) => (k.Old, i)).ToDictionary(x => x.Old, x => x.i);
            foreach (var (n, old) in keep)
                foreach (var link in new[] { "next", "prev" })
                {
                    int to = n[link]?.GetValue<int>() ?? -1;
                    // A rivet line can't run across the hole: break it where it would.
                    bool across = to >= 0 && positions[old] is { } a && to < positions.Count && positions[to] is { } b
                                  && Enumerable.Range(1, 7).Any(s => InsideShape(Vector3.Lerp(a, b, s / 8f)));
                    n[link] = to >= 0 && !across && index.TryGetValue(to, out int ni) ? ni : -1;
                }
            rivets!["nodes"] = new JsonArray(keep.Select(k => (JsonNode?)k.Node).ToArray());
        }

        double armour = result.Where(f => f.Changed).Sum(f => Armour(f.C)) - replaced.Sum(j => Armour(original[j].C));
        mesh["vertices"] = new JsonArray(keepVerts.SelectMany(i => new[] { verts[i].X, verts[i].Y, verts[i].Z }).Select(x => (JsonNode?)x).ToArray());
        mesh["edges"] = new JsonArray(edges.SelectMany(x => new[] { renumber[x.Item1], renumber[x.Item2] }).Select(x => (JsonNode?)x).ToArray());
        mesh["edgeFlags"] = new JsonArray(flags.Select(x => (JsonNode?)x).ToArray());
        mesh["faces"] = outFaces;
        return new Result(replaced.Count, pocketFaces, armour, movedRivets, droppedRivets);

        // The edge from v towards `other`: the original edge, or the first piece of it if it was split.
        int EdgeTowards(int v, int other)
        {
            if (newIndex.TryGetValue(Key(v, other), out int i)) return i;
            var line = Key(v, other);
            int best = -1; float bestD = float.MaxValue;
            foreach (var (n, ab) in carrier)
            {
                if (Key(ab.A, ab.B) != line || !newIndex.TryGetValue(Key(v, n), out int idx)) continue;
                float d = Vector3.DistanceSquared(verts[n], verts[v]);
                if (d < bestD) { bestD = d; best = idx; }
            }
            return best;
        }
    }

    // ---------- splitting ----------

    /// Splits a polygon along the planes of the shape triangles that actually cross it, until no triangle crosses any
    /// piece: then each piece is wholly inside or wholly outside the shape.
    static void Shatter(List<Corner> poly, List<Tri> tris, int start, List<List<Corner>> output, List<Vector3> verts)
    {
        for (int i = start; i < tris.Count; i++)
        {
            if (!Crosses(poly, tris[i])) continue;
            var (outside, inside) = Split(poly, tris[i].Plane, verts);
            if (Area(outside) > Tiny) Shatter(outside, tris, i + 1, output, verts);
            if (Area(inside) > Tiny) Shatter(inside, tris, i + 1, output, verts);
            return;
        }
        output.Add(poly);
    }

    /// Whether a triangle cuts through a (flat, convex) polygon: each crosses the other's plane and the two crossing
    /// segments overlap. A triangle lying in the polygon's plane doesn't cut it.
    static bool Crosses(List<Corner> poly, Tri t)
    {
        var (min, max) = Bounds(poly);
        if (!Overlaps(min, max, t.Min - new Vector3(Eps), t.Max + new Vector3(Eps))) return false;
        var dp = poly.Select(c => t.Plane.Dist(c.P)).ToList();
        if (dp.All(d => d > -Eps) || dp.All(d => d < Eps)) return false;
        var n = HoleRing.Normal(poly.Select(c => c.P).ToList());
        if (n.LengthSquared() < 1e-14f) return false;
        var pp = new Plane(Vector3.Normalize(n), 0);
        pp = pp with { D = Vector3.Dot(pp.N, Centre(poly)) };
        var dt = new[] { pp.Dist(t.A), pp.Dist(t.B), pp.Dist(t.C) };
        if (dt.All(d => d > -Eps) || dt.All(d => d < Eps)) return false;
        var line = Vector3.Cross(t.Plane.N, pp.N);
        if (line.LengthSquared() < 1e-12f) return false;
        var (a0, a1) = Span(poly.Select(c => c.P).ToList(), dp, line);
        var (b0, b1) = Span(new List<Vector3> { t.A, t.B, t.C }, dt.ToList(), line);
        return a0 < b1 - Eps && b0 < a1 - Eps;
    }

    /// Where a polygon crosses a plane, as an interval along `line`.
    static (float, float) Span(List<Vector3> pts, List<float> d, Vector3 line)
    {
        float lo = float.MaxValue, hi = float.MinValue;
        for (int i = 0; i < pts.Count; i++)
        {
            int j = (i + 1) % pts.Count;
            if (Math.Abs(d[i]) <= Eps) { float s = Vector3.Dot(pts[i], line); lo = Math.Min(lo, s); hi = Math.Max(hi, s); }
            if ((d[i] > Eps && d[j] < -Eps) || (d[i] < -Eps && d[j] > Eps))
            {
                float s = Vector3.Dot(pts[i] + (pts[j] - pts[i]) * (d[i] / (d[i] - d[j])), line);
                lo = Math.Min(lo, s); hi = Math.Max(hi, s);
            }
        }
        return (lo, hi);
    }

    /// Splits a polygon by a plane into the parts on each side, keeping corner order (so each part faces the same way).
    static (List<Corner> Outside, List<Corner> Inside) Split(List<Corner> poly, Plane plane, List<Vector3> verts)
    {
        var d = poly.Select(c => plane.Dist(c.P)).ToArray();
        var outside = new List<Corner>();
        var inside = new List<Corner>();
        for (int i = 0; i < poly.Count; i++)
        {
            Corner p = poly[i], q = poly[(i + 1) % poly.Count];
            float dp = d[i], dq = d[(i + 1) % poly.Count];
            if (dp >= -Eps) outside.Add(p);
            if (dp <= Eps) inside.Add(p);
            if (!(dp > Eps && dq < -Eps) && !(dp < -Eps && dq > Eps)) continue;
            var x = Cross(p, q, dp, dq, plane, verts);
            outside.Add(x);
            inside.Add(x);
        }
        return (outside, inside);
    }

    /// Where edge p-q crosses the plane. On an original edge the point is worked out from that edge's own ends, so the
    /// face on the other side of the edge gets exactly the same point.
    static Corner Cross(Corner p, Corner q, float dp, float dq, Plane plane, List<Vector3> verts)
    {
        (int, int)? line = null;
        (int, int)? Ends(Corner c) => c.A >= 0 ? Key(c.A, c.B) : null;
        if (p.A < 0 && q.A < 0 && p.V >= 0 && q.V >= 0) line = Key(p.V, q.V);
        else if (Ends(p) is { } ep && (Ends(q) == ep || (q.A < 0 && (q.V == ep.Item1 || q.V == ep.Item2)))) line = ep;
        else if (Ends(q) is { } eq && p.A < 0 && (p.V == eq.Item1 || p.V == eq.Item2)) line = eq;

        float s = dp / (dp - dq);
        Vector3 at;
        if (line is { } ab)
        {
            Vector3 a = verts[ab.Item1], b = verts[ab.Item2];
            float da = plane.Dist(a), db = plane.Dist(b);
            at = a + (b - a) * (da / (da - db));
        }
        else at = p.P + (q.P - p.P) * s;
        return new Corner(at, p.T + (q.T - p.T) * s, -1, line?.Item1 ?? -1, line?.Item2 ?? -1, p.Mode, -1, p.Raw, p.Other >= 0 || p.Fallback);
    }

    // ---------- inside / outside ----------

    /// Generalised winding number: about ±1 inside a closed surface, about 0 outside (an inside wall only shifts it by
    /// less than a half, so "more than a half" still means inside).
    static double Winding(List<Tri> tris, Vector3 p)
    {
        double sum = 0;
        foreach (var t in tris)
        {
            Vector3 a = t.A - p, b = t.B - p, c = t.C - p;
            double la = a.Length(), lb = b.Length(), lc = c.Length();
            double det = Vector3.Dot(a, Vector3.Cross(b, c));
            double div = la * lb * lc + Vector3.Dot(a, b) * lc + Vector3.Dot(b, c) * la + Vector3.Dot(c, a) * lb;
            sum += 2 * Math.Atan2(det, div);
        }
        return sum / (4 * Math.PI);
    }

    static bool OnSurface(List<Tri> tris, Vector3 p) =>
        tris.Any(t => Math.Abs(t.Plane.Dist(p)) < Flat && p.X >= t.Min.X - Flat && p.X <= t.Max.X + Flat && p.Y >= t.Min.Y - Flat && p.Y <= t.Max.Y + Flat
                      && p.Z >= t.Min.Z - Flat && p.Z <= t.Max.Z + Flat && Barycentric(t.A, t.B, t.C, p) is { } w && w.X >= -1e-4f && w.Y >= -1e-4f && w.Z >= -1e-4f);

    // ---------- tidying ----------

    /// Two pieces of one face sharing an edge become one polygon if the result is still flat and convex.
    static Poly? TryMerge(Poly a, Poly b)
    {
        for (int i = 0; i < a.C.Count; i++)
        {
            int u = a.C[i].V, w = a.C[(i + 1) % a.C.Count].V;
            int j = b.C.FindIndex(c => c.V == w);
            if (j < 0 || b.C[(j + 1) % b.C.Count].V != u) continue;
            var first = Enumerable.Range(0, a.C.Count).Select(k => a.C[(i + 1 + k) % a.C.Count]).ToList();       // w ... u
            var second = Enumerable.Range(0, b.C.Count).Select(k => b.C[(j + 1 + k) % b.C.Count]).ToList();      // u ... w
            var union = first.Concat(second.Skip(1).Take(second.Count - 2)).ToList();
            if (union.Select(c => c.V).Distinct().Count() != union.Count) return null;
            var n = HoleRing.Normal(union.Select(c => c.P).ToList());
            if (n.LengthSquared() < 1e-14f) return null;
            n = Vector3.Normalize(n);
            var centre = Centre(union);
            if (union.Any(c => Math.Abs(Vector3.Dot(c.P - centre, n)) > Flat)) return null;
            float scale = union.Max(c => (c.P - centre).LengthSquared());
            for (int k = 0; k < union.Count; k++)
                if (Turn(union, k, n) < -1e-6f * scale) return null;
            return new Poly { C = union, Source = a.Source, Pocket = a.Pocket, Group = a.Group, Changed = true };
        }
        return null;
    }

    static float Turn(List<Corner> p, int k, Vector3 n) =>
        Vector3.Dot(Vector3.Cross(p[k].P - p[(k - 1 + p.Count) % p.Count].P, p[(k + 1) % p.Count].P - p[k].P), n);

    /// A loop corner sitting on a straight edge (its neighbours and it are in a line, going on in the same direction).
    static bool StraightAt(List<int> loop, int k, List<Vector3> verts)
    {
        if (k < 0) return false;
        Vector3 a = verts[loop[(k - 1 + loop.Count) % loop.Count]], b = verts[loop[k]], c = verts[loop[(k + 1) % loop.Count]];
        var ab = b - a; var bc = c - b;
        return Vector3.Dot(ab, bc) > 0 && Vector3.Cross(ab, bc).Length() <= 1e-6f * Math.Max(ab.Length() * bc.Length(), 1e-12f) + Weld * Math.Max(ab.Length(), bc.Length());
    }

    /// The outline of a set of pieces: the edges only one piece has, chained into closed loops. Null if the outline
    /// touches itself at a corner (two outline edges leave one vertex).
    static List<List<int>>? Outline(List<Poly> pieces)
    {
        var directed = new HashSet<(int, int)>();
        foreach (var p in pieces) for (int k = 0; k < p.C.Count; k++) directed.Add((p.C[k].V, p.C[(k + 1) % p.C.Count].V));
        var next = new Dictionary<int, int>();
        foreach (var (a, b) in directed)
            if (!directed.Contains((b, a)))
            {
                if (next.ContainsKey(a)) return null;
                next[a] = b;
            }
        var loops = new List<List<int>>();
        var seen = new HashSet<int>();
        foreach (int start in next.Keys)
        {
            if (seen.Contains(start)) continue;
            var loop = new List<int>();
            for (int v = start; seen.Add(v);)
            {
                loop.Add(v);
                if (!next.TryGetValue(v, out v)) return null;
            }
            if (loop.Count < 3 || next[loop[^1]] != start) return null;
            loops.Add(loop);
        }
        return loops.Count > 0 ? loops : null;
    }

    /// Two faces in the same plane with the same armour all over, which can be rebuilt as one.
    static bool Flush(Poly a, Poly b)
    {
        var na = HoleRing.Normal(a.C.Select(c => c.P).ToList());
        var nb = HoleRing.Normal(b.C.Select(c => c.P).ToList());
        if (na.LengthSquared() < 1e-14f || nb.LengthSquared() < 1e-14f) return false;
        na = Vector3.Normalize(na); nb = Vector3.Normalize(nb);
        float t = a.C[0].T;
        return Vector3.Dot(na, nb) > 0.99995f
            && a.C.Concat(b.C).All(c => Math.Abs(Vector3.Dot(c.P - a.C[0].P, na)) < 1e-4f && Math.Abs(c.T - t) < 0.5f && c.Mode == a.C[0].Mode);
    }

    /// Whether a point lies inside a loop, seen along the normal.
    static bool InsideLoop(List<int> loop, Vector3 p, Vector3 normal, List<Vector3> verts)
    {
        var n = Vector3.Normalize(normal);
        var u = Vector3.Normalize(Math.Abs(n.X) < 0.9f ? Vector3.Cross(n, Vector3.UnitX) : Vector3.Cross(n, Vector3.UnitY));
        var w = Vector3.Cross(n, u);
        Vector2 P(Vector3 x) => new(Vector3.Dot(x, u), Vector3.Dot(x, w));
        var q = P(p);
        bool inside = false;
        for (int k = 0; k < loop.Count; k++)
        {
            Vector2 a = P(verts[loop[k]]), b = P(verts[loop[(k + 1) % loop.Count]]);
            if ((a.Y > q.Y) != (b.Y > q.Y) && q.X < a.X + (q.Y - a.Y) / (b.Y - a.Y) * (b.X - a.X)) inside = !inside;
        }
        return inside;
    }

    /// Triangles of one face (or one pocket plate) sharing an edge, paired into flat convex quads, squarest first.
    static IEnumerable<(Poly A, Poly B, List<Corner> Quad)> PairUp(List<Poly> tris)
    {
        var byEdge = new Dictionary<(int, int), List<int>>();
        for (int t = 0; t < tris.Count; t++)
            for (int k = 0; k < 3; k++)
            {
                var key = Key(tris[t].C[k].V, tris[t].C[(k + 1) % 3].V);
                if (!byEdge.TryGetValue(key, out var l)) byEdge[key] = l = new();
                l.Add(t);
            }
        var pairs = new List<(int A, int B, List<Corner> Quad, float Score)>();
        foreach (var l in byEdge.Values.Where(l => l.Count == 2))
        {
            var (a, b) = (tris[l[0]].C, tris[l[1]].C);
            Vector3 na = HoleRing.Normal(a.Select(c => c.P).ToList()), nb = HoleRing.Normal(b.Select(c => c.P).ToList());
            if (na.LengthSquared() < 1e-20f || nb.LengthSquared() < 1e-20f || Vector3.Dot(Vector3.Normalize(na), Vector3.Normalize(nb)) < 0.9999f) continue;
            var n = Vector3.Normalize(na + nb);
            float scale = a.Concat(b).Max(c => (c.P - a[0].P).LengthSquared());
            if (QuadOf(a, b, n, scale) is { } q) pairs.Add((l[0], l[1], q.Quad, q.Score));
        }
        var used = new bool[tris.Count];
        foreach (var (a, b, quad, _) in pairs.OrderByDescending(x => x.Score))
        {
            if (used[a] || used[b]) continue;
            used[a] = used[b] = true;
            yield return (tris[a], tris[b], quad);
        }
    }

    /// Faces are triangles or quads: a clean one stays; anything else is cut into triangles (paired into quads later).
    static IEnumerable<Poly> Tessellate(Poly face)
    {
        var poly = face.C.Where((c, k) => c.V != face.C[(k + 1) % face.C.Count].V).ToList();
        if (poly.Count < 3 || Area(poly) <= Tiny) return Array.Empty<Poly>();
        var n = Vector3.Normalize(HoleRing.Normal(poly.Select(c => c.P).ToList()));
        float scale = poly.Max(c => (c.P - poly[0].P).LengthSquared());
        Poly Make(List<Corner> c) => new() { C = c, Source = face.Source, Pocket = face.Pocket, Group = face.Group, Changed = true };
        if (poly.Count <= 4 && Enumerable.Range(0, poly.Count).All(i => Turn(poly, i, n) > 1e-6f * scale)) return new[] { Make(poly) };

        var tris = new List<List<Corner>>();
        var rest = poly.ToList();
        for (int guard = 0; rest.Count > 3 && guard < 2000; guard++)
        {
            int ear = Enumerable.Range(0, rest.Count).FirstOrDefault(i => Turn(rest, i, n) > 1e-6f * scale && !Blocked(rest, i), -1);
            if (ear < 0) break;
            tris.Add(new() { rest[(ear - 1 + rest.Count) % rest.Count], rest[ear], rest[(ear + 1) % rest.Count] });
            rest.RemoveAt(ear);
        }
        if (rest.Count == 3 && Area(rest) > Tiny) tris.Add(rest);
        return tris.Select(Make).ToList();

        // Another corner inside the ear, or on its new edge (which would leave a crack beside it).
        bool Blocked(List<Corner> p, int i)
        {
            Vector3 a = p[(i - 1 + p.Count) % p.Count].P, b = p[i].P, c = p[(i + 1) % p.Count].P;
            float on = -1e-6f * scale;
            for (int k = 0; k < p.Count; k++)
            {
                if (k == i || k == (i - 1 + p.Count) % p.Count || k == (i + 1) % p.Count) continue;
                var x = p[k].P;
                if (Vector3.Dot(Vector3.Cross(b - a, x - a), n) >= on && Vector3.Dot(Vector3.Cross(c - b, x - b), n) >= on && Vector3.Dot(Vector3.Cross(a - c, x - c), n) >= on)
                    return true;
            }
            return false;
        }
    }

    /// Two triangles sharing an edge as one strictly convex quad, scored by its smallest corner angle.
    static (List<Corner> Quad, float Score)? QuadOf(List<Corner> a, List<Corner> b, Vector3 n, float scale)
    {
        for (int i = 0; i < 3; i++)
        {
            int u = a[i].V, w = a[(i + 1) % 3].V;
            int j = b.FindIndex(c => c.V == w);
            if (j < 0 || b[(j + 1) % 3].V != u) continue;
            var quad = new List<Corner> { a[(i + 1) % 3], a[(i + 2) % 3], a[i], b[(j + 2) % 3] }; // w, a-other, u, b-other
            if (Enumerable.Range(0, 4).Any(k => Turn(quad, k, n) <= 1e-4f * scale)) return null;
            float worst = Enumerable.Range(0, 4).Min(k =>
            {
                var p = quad[(k + 3) % 4].P - quad[k].P; var q = quad[(k + 1) % 4].P - quad[k].P;
                return 1 - Math.Abs(Vector3.Dot(Vector3.Normalize(p), Vector3.Normalize(q)));
            });
            return (quad, worst);
        }
        return null;
    }

    static bool SameLoop(List<int> a, List<int> b)
    {
        if (a.Count != b.Count) return false;
        int start = b.IndexOf(a[0]);
        return start >= 0 && Enumerable.Range(0, a.Count).All(k => a[k] == b[(start + k) % b.Count]);
    }

    // ---------- rivets ----------

    // A rivet sits on one of its face's triangles by weights: faceOffset 0/+1 = corners (0,1,2), +2 = (2,3,0),
    // -1 = (2,3,1), -2 = (3,0,1) (worked out from saved designs: generated rivets land exactly `padding` in).
    internal static readonly Dictionary<int, int[]> RivetTriangles = new() { [0] = new[] { 0, 1, 2 }, [1] = new[] { 0, 1, 2 }, [2] = new[] { 2, 3, 0 }, [-1] = new[] { 2, 3, 1 }, [-2] = new[] { 3, 0, 1 } };

    static Vector3? RivetPosition(JsonObject node, List<Poly> faces, List<Vector3> verts)
    {
        int f = node["face"]?.GetValue<int>() ?? -1;
        int offset = node["faceOffset"]?.GetValue<int>() ?? 0;
        if (f < 0 || f >= faces.Count || !RivetTriangles.TryGetValue(offset, out var tri) || tri.Max() >= faces[f].C.Count) return null;
        float u = F(node["u"]), v = F(node["v"]), w = F(node["w"]);
        var c = faces[f].C;
        return u * c[tri[0]].P + v * c[tri[1]].P + w * c[tri[2]].P;
    }

    /// Weights and faceOffset placing point p on a triangle or quad face, or null if p isn't on it.
    static (float U, float V, float W, int Offset)? RivetAt(List<Vector3> face, Vector3 p)
    {
        foreach (int offset in face.Count == 3 ? new[] { 0 } : new[] { 1, 2 })
        {
            var t = RivetTriangles[offset];
            Vector3 a = face[t[0]], b = face[t[1]], c = face[t[2]];
            var n = Vector3.Cross(b - a, c - a);
            if (n.LengthSquared() < 1e-14f || Math.Abs(Vector3.Dot(Vector3.Normalize(n), p - a)) > 1e-3f) continue;
            if (Barycentric(a, b, c, p) is { } w && w.X >= -1e-4f && w.Y >= -1e-4f && w.Z >= -1e-4f)
            {
                var clamped = Vector3.Max(w, Vector3.Zero);
                clamped /= clamped.X + clamped.Y + clamped.Z;
                return (clamped.X, clamped.Y, clamped.Z, offset);
            }
        }
        return null;
    }

    // ---------- small helpers ----------

    static Vector3? Barycentric(Vector3 a, Vector3 b, Vector3 c, Vector3 p)
    {
        Vector3 v0 = b - a, v1 = c - a, v2 = p - a;
        float d00 = Vector3.Dot(v0, v0), d01 = Vector3.Dot(v0, v1), d11 = Vector3.Dot(v1, v1), d20 = Vector3.Dot(v2, v0), d21 = Vector3.Dot(v2, v1);
        float den = d00 * d11 - d01 * d01;
        if (Math.Abs(den) < 1e-20f) return null;
        float v = (d11 * d20 - d01 * d21) / den, w = (d00 * d21 - d01 * d20) / den;
        return new Vector3(1 - v - w, v, w);
    }

    static IEnumerable<Tri> FanTris(List<Vector3> p) => Enumerable.Range(1, Math.Max(0, p.Count - 2)).Select(k => new Tri(p[0], p[k], p[k + 1]));

    static (Vector3 Min, Vector3 Max) Bounds(List<Corner> poly) =>
        (poly.Aggregate(new Vector3(float.MaxValue), (m, c) => Vector3.Min(m, c.P)), poly.Aggregate(new Vector3(float.MinValue), (m, c) => Vector3.Max(m, c.P)));

    static bool Overlaps(Vector3 aMin, Vector3 aMax, Vector3 bMin, Vector3 bMax) =>
        aMin.X <= bMax.X && aMax.X >= bMin.X && aMin.Y <= bMax.Y && aMax.Y >= bMin.Y && aMin.Z <= bMax.Z && aMax.Z >= bMin.Z;

    static Vector3 Centre(List<Corner> poly)
    {
        // Area-weighted centre: always inside a convex piece, even a thin one.
        var sum = Vector3.Zero; float total = 0;
        for (int k = 1; k < poly.Count - 1; k++)
        {
            float a = Vector3.Cross(poly[k].P - poly[0].P, poly[k + 1].P - poly[0].P).Length();
            sum += a * (poly[0].P + poly[k].P + poly[k + 1].P) / 3; total += a;
        }
        return total > 0 ? sum / total : poly.Aggregate(Vector3.Zero, (s, c) => s + c.P) / poly.Count;
    }

    static (int, int) Key(int a, int b) => a < b ? (a, b) : (b, a);

    static float Area(List<Corner> poly) => poly.Count < 3 ? 0 : HoleRing.Normal(poly.Select(c => c.P).ToList()).Length() / 2;

    /// Plate volume in m³: area x average corner thickness (mm), as the blueprint's armourVolume counts it.
    static double Armour(List<Corner> poly) => Area(poly) * poly.Average(c => c.T) / 1000.0;

    // Numbers read back whether they were parsed from text or written by this mod as whole numbers.
    internal static float F(JsonNode? n) => n is not JsonValue v ? 0 : v.TryGetValue(out float f) ? f : v.TryGetValue(out int i) ? i : (float)v.GetValue<double>();
    internal static long L(JsonNode? n) => n is not JsonValue v ? 0 : v.TryGetValue(out long l) ? l : v.TryGetValue(out ulong u) ? (long)u : v.TryGetValue(out int i) ? i : (long)v.GetValue<double>();

    /// A face's thicken edges as the game stores them: one unsigned number, 16 bits a corner, with "none" (0xFFFF while
    /// cutting) stored as 0. Written signed, a corner with none made it negative, and the game can't read the design.
    internal static JsonNode Te(ulong te)
    {
        for (int k = 0; k < 4; k++) if (((te >> (16 * k)) & 0xFFFF) == 0xFFFF) te &= ~(0xFFFFUL << (16 * k));
        return JsonValue.Create(te);
    }

    static JsonObject Clone(JsonNode node) => JsonNode.Parse(node.ToJsonString())!.AsObject();
}
