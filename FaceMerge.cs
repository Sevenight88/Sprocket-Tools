using System.Numerics;

namespace SprocketTools;

/// "Merge faces": selected faces that share edges become as few faces as their outline allows (one quad or triangle
/// where it can be, else triangles paired into quads). Points inside go, and so do points along straight sides. The
/// selection splits into flat patches at bends, and a straight line of points two patches share (across a corner) goes
/// from both. A side point an unselected face uses too is handled as `SidePoints` says. Plain maths on vertex indices,
/// tested offline; MergeFaces applies it to the game's mesh.
public static class FaceMerge
{
    /// What to do with a point on a straight side that an unselected face also uses.
    public enum SidePoints
    {
        /// Take its line out: the faces along it, up to where it meets a real corner or the plate's edge, are rebuilt
        /// without it too, so everything stays joined.
        TakeOutLine,
        /// The merged face runs straight past it and the other face keeps it (like Delete + Fill): not joined there.
        RunPast,
        /// It stays a corner of the merged faces.
        Keep,
    }

    /// One flat patch of faces: the faces it replaces (indices into the mesh's faces), the faces replacing them, the
    /// vertices no longer used, and for each new outline side that skips removed points, the old side it continues
    /// ((low, high) -> (from, old next)). `LeftOn`: points the merged face runs past that other faces still use.
    /// `Why` says why the patch was left alone (then NewFaces is empty).
    public sealed record Group(List<int> Faces, List<int[]> NewFaces, List<int> Removed, Dictionary<(int, int), (int From, int OldNext)> Joined, List<int> LeftOn, string? Why);

    sealed class Patch
    {
        public List<int> Faces = new();
        public List<List<int>> Outline = new(), Loops = new(); // as found, and with straight points taken out
        public bool Closed;                                     // the outline chained into loops
        public HashSet<int> Inside = new(), Points = new();
        public Vector3 Normal;
        public string? Why;
        public List<int[]> NewFaces = new();
        public List<int> Removed = new(), LeftOn = new();
        public Dictionary<(int, int), (int, int)> Joined = new();
    }

    /// Faces within 20° of each other across a shared edge are one patch; a patch whose faces stray more than 20° from
    /// its average direction would fold when flattened into one outline, so it's left alone.
    const float SameWay = 0.94f;

    /// `faces`: every face of the mesh (vertex indices, each in its own turning order); `selected`: the ones to merge;
    /// `loose`: edges no face uses (their points stay).
    public static List<Group> Plan(IReadOnlyList<Vector3> pos, IReadOnlyList<int[]> faces, ISet<int> selected, ISet<(int, int)> loose, SidePoints sides)
    {
        if (selected.Count == 0) return new();
        if (selected.Any(f => f < 0 || f >= faces.Count) || pos.Any(p => !float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z)) ||
            faces.Any(f => f.Length < 3 || f.Distinct().Count() != f.Length || f.Any(v => v < 0 || v >= pos.Count)))
            return new() { new Group(selected.ToList(), new(), new(), new(), new(), "所选范围或网格含有无效的顶点或面") };
        var chosen = new HashSet<int>(selected);
        var lines = new HashSet<(int, int)>(); // edges of a line being taken out: faces across them may join
        var edgeFaces = new Dictionary<(int, int), List<int>>();
        var pointFaces = new Dictionary<int, List<int>>();
        for (int f = 0; f < faces.Count; f++)
            for (int k = 0; k < faces[f].Length; k++)
            {
                int a = faces[f][k], b = faces[f][(k + 1) % faces[f].Length];
                if (!edgeFaces.TryGetValue(Key(a, b), out var ef)) edgeFaces[Key(a, b)] = ef = new();
                if (!pointFaces.TryGetValue(a, out var pf)) pointFaces[a] = pf = new();
                ef.Add(f);
                pf.Add(f);
            }
        var loosePoints = loose.SelectMany(e => new[] { e.Item1, e.Item2 }).ToHashSet();
        while (true)
        {
            var patches = Patches(pos, faces, chosen, selected, lines, loose, sides == SidePoints.RunPast, out var others);
            // A straight side point that only unchosen faces keep: take its line on through them, then look again.
            var was = chosen.ToHashSet();
            if (sides == SidePoints.TakeOutLine && chosen.Count < selected.Count + 5000) // ponytail: cap on how far lines run
                foreach (var l in patches.Where(p => p.Closed).SelectMany(p => p.Outline))
                    for (int k = 0; k < l.Count; k++)
                    {
                        int v = l[k];
                        if (!others.Contains(v) || loosePoints.Contains(v) || !Straight(pos, l, k)) continue;
                        foreach (int f in pointFaces[v].Where(f => !was.Contains(f)))
                        {
                            chosen.Add(f);
                            int at = Array.IndexOf(faces[f], v), n = faces[f].Length;
                            foreach (int w in new[] { faces[f][(at + 1) % n], faces[f][(at - 1 + n) % n] })
                                if (edgeFaces[Key(v, w)].All(g => !was.Contains(g))) lines.Add(Key(v, w));
                        }
                    }
            if (chosen.Count == was.Count)
                return patches.Where(p => p.Why == null || p.Faces.Any(selected.Contains)).Select(p => p.Why != null
                    ? new Group(p.Faces, new(), new(), new(), new(), p.Why)
                    : new Group(p.Faces, p.NewFaces, p.Removed.Distinct().ToList(), p.Joined, p.LeftOn.Distinct().ToList(), null)).ToList();
        }
    }

    /// Splits the chosen faces into flat patches and plans each one. Faces the player didn't select only join across
    /// `lines`. `others`: points of unchosen faces.
    static List<Patch> Patches(IReadOnlyList<Vector3> pos, IReadOnlyList<int[]> faces, HashSet<int> chosen, ISet<int> selected,
                               HashSet<(int, int)> lines, ISet<(int, int)> loose, bool runPast, out HashSet<int> others)
    {
        var keepEdges = new HashSet<(int, int)>(loose);
        others = new HashSet<int>();
        var uses = new Dictionary<(int, int), List<(int Face, int From, int To)>>();
        for (int f = 0; f < faces.Count; f++)
            for (int k = 0; k < faces[f].Length; k++)
            {
                int a = faces[f][k], b = faces[f][(k + 1) % faces[f].Length];
                if (!chosen.Contains(f)) { keepEdges.Add(Key(a, b)); others.Add(a); continue; }
                if (!uses.TryGetValue(Key(a, b), out var list)) uses[Key(a, b)] = list = new();
                list.Add((f, a, b));
            }
        var normals = new Dictionary<int, Vector3>();
        foreach (int f in chosen) normals[f] = Vector3.Normalize(Newell(pos, faces[f]));
        // An edge inside a patch: two chosen faces run it opposite ways, lie flat to each other, nothing else uses it,
        // and both were selected or it's part of a line being taken out.
        bool Inner((int, int) key) => uses[key] is { Count: 2 } u && u[0].From == u[1].To && !keepEdges.Contains(key) &&
                                      Vector3.Dot(normals[u[0].Face], normals[u[1].Face]) >= SameWay &&
                                      (selected.Contains(u[0].Face) && selected.Contains(u[1].Face) || lines.Contains(key));
        var parent = chosen.ToDictionary(f => f, f => f);
        int Find(int x) => parent[x] == x ? x : parent[x] = Find(parent[x]);
        foreach (var key in uses.Keys.Where(Inner))
            parent[Find(uses[key][0].Face)] = Find(uses[key][1].Face);
        var patches = chosen.OrderBy(f => f).GroupBy(Find).Select(g => Outline(pos, faces, g.ToList(), Inner)).ToList();

        // Points a patch left alone still uses stay; a patch whose fill fails is left alone too, then try again.
        var keep = new HashSet<int>(others);
        keep.UnionWith(loose.SelectMany(e => new[] { e.Item1, e.Item2 }));
        while (true)
        {
            for (bool more = true; more;)
            {
                foreach (var p in patches.Where(p => p.Why != null)) keep.UnionWith(p.Points);
                var stuck = patches.FirstOrDefault(p => p.Why == null && (p.Inside.Overlaps(keep) || patches.Any(q => q != p && p.Inside.Overlaps(q.Points))));
                if (stuck != null) stuck.Why = "面内的点同时被其他内容使用";
                more = stuck != null;
            }
            var live = patches.Where(p => p.Why == null).ToList();
            RemoveStraight(pos, live, keep, runPast);
            foreach (var p in live)
            {
                var outer = p.Loops.OrderByDescending(l => Math.Abs(Vector3.Dot(Newell(pos, l), p.Normal))).First();
                p.NewFaces = Fill.Region(pos.ToList(), outer, p.Loops.Where(l => l != outer).ToList(), p.Normal, null);
                if (p.NewFaces.Count == 0 || p.NewFaces.Any(f => f.Length is < 3 or > 4 || f.Distinct().Count() != f.Length))
                    p.Why = "其外轮廓无法填充";
            }
            if (live.All(p => p.Why == null)) break;
        }

        // No fewer faces: leave it alone, unless it gave up points a neighbouring patch gave up too (both must change).
        var otherPoints = others;
        foreach (var p in patches.Where(p => p.Why == null && p.NewFaces.Count >= p.Faces.Count))
            if (!patches.Any(q => q != p && q.Why == null && q.Removed.Concat(q.LeftOn).Intersect(p.Removed.Concat(p.LeftOn)).Any()))
            {
                int blocked = p.Outline.Sum(l => l.Where((v, k) => otherPoints.Contains(v) && Straight(pos, l, k)).Count());
                p.Why = blocked == 0 ? "其外轮廓已是最少面数"
                    : $"{blocked} 个边上的点是您未选中面的角点；请一并选中这些面，或更改“被其他面共用的点”的处理方式";
            }
        return patches;
    }

    /// A patch's outline loops (face sides whose edge isn't inside it, chained head to tail in the faces' own turning),
    /// the points inside it, and its average direction.
    static Patch Outline(IReadOnlyList<Vector3> pos, IReadOnlyList<int[]> faces, List<int> members, Func<(int, int), bool> inner)
    {
        var p = new Patch { Faces = members };
        foreach (int f in members) p.Points.UnionWith(faces[f]);
        if (members.Count < 2) { p.Why = "它没有与另一个同向的所选面共边"; return p; }
        var next = new Dictionary<int, int>();
        foreach (int f in members)
            for (int k = 0; k < faces[f].Length; k++)
            {
                int a = faces[f][k], b = faces[f][(k + 1) % faces[f].Length];
                if (!inner(Key(a, b)) && !next.TryAdd(a, b)) { p.Why = "这些面只在角点相接，或朝向相反"; return p; }
            }
        var left = next.Keys.ToHashSet();
        while (left.Count > 0)
        {
            var loop = new List<int>();
            int start = left.First();
            for (int v = start; ;)
            {
                loop.Add(v);
                left.Remove(v);
                if (!next.TryGetValue(v, out v) || v != start && !left.Contains(v)) { p.Why = "其外轮廓不闭合"; return p; }
                if (v == start) break;
            }
            p.Outline.Add(loop);
        }
        p.Closed = true;
        p.Inside = p.Points.Except(next.Keys).ToHashSet();
        foreach (int f in members) p.Normal += Newell(pos, faces[f]);
        p.Normal = Vector3.Normalize(p.Normal);
        if (members.Any(f => Vector3.Dot(Vector3.Normalize(Newell(pos, faces[f])), p.Normal) < SameWay)) p.Why = "这些面的弯折超过 20°";
        return p;
    }

    /// Takes out outline points that sit on a straight line in every patch outline they're on, and that nothing kept
    /// uses (or, `runPast`, even if something does: then the point stays for it), from all those outlines at once,
    /// so patches sharing a side stay joined.
    // ponytail: finds a point's outlines by scanning them all, O(outline points²); index them if selections get huge.
    static void RemoveStraight(IReadOnlyList<Vector3> pos, List<Patch> live, HashSet<int> keep, bool runPast)
    {
        foreach (var p in live)
        {
            p.Loops = p.Outline.Select(l => l.ToList()).ToList();
            p.Removed = p.Inside.ToList();
            p.LeftOn = new();
            p.Joined = new();
        }
        var loops = live.SelectMany(p => p.Loops.Select(l => (Patch: p, Loop: l))).ToList();
        for (bool changed = true; changed;)
        {
            changed = false;
            foreach (var (_, loop) in loops)
                for (int k = 0; k < loop.Count; k++)
                {
                    int v = loop[k];
                    var on = loops.Where(x => x.Loop.Contains(v)).ToList();
                    if (keep.Contains(v) && !runPast || on.Any(x => x.Loop.Count <= 3 || !Straight(pos, x.Loop, x.Loop.IndexOf(v)))) continue;
                    foreach (var (p, l) in on)
                    {
                        int at = l.IndexOf(v), a = l[(at - 1 + l.Count) % l.Count], b = l[(at + 1) % l.Count];
                        // The new side a->b continues whatever old side left a (a's own, or the one it took over).
                        p.Joined[Key(a, b)] = p.Joined.TryGetValue(Key(a, v), out var was) ? was : (a, v);
                        p.Joined.Remove(Key(a, v));
                        p.Joined.Remove(Key(v, b));
                        (keep.Contains(v) ? p.LeftOn : p.Removed).Add(v);
                        l.RemoveAt(at);
                    }
                    k = -1; // this loop changed under us: look again from the start
                    changed = true;
                }
        }
    }

    /// Point k of a loop sits on the straight line between its neighbours (within about 0.3°).
    static bool Straight(IReadOnlyList<Vector3> pos, List<int> loop, int k)
    {
        Vector3 a = pos[loop[(k - 1 + loop.Count) % loop.Count]], b = pos[loop[k]], c = pos[loop[(k + 1) % loop.Count]];
        Vector3 ab = b - a, bc = c - b;
        return Vector3.Dot(ab, bc) > 0 && Vector3.Cross(ab, bc).Length() <= 0.005f * ab.Length() * bc.Length();
    }

    /// Twice the area-weighted normal of a loop (Newell's method), pointing the way the loop turns.
    static Vector3 Newell(IReadOnlyList<Vector3> pos, IReadOnlyList<int> loop)
    {
        var n = Vector3.Zero;
        if (loop.Count < 3) return n;
        // Fanned from the loop's own corner: products of absolute coordinates lose a small face far from the origin.
        var origin = pos[loop[0]];
        for (int k = 1; k + 1 < loop.Count; k++) n += Vector3.Cross(pos[loop[k]] - origin, pos[loop[k + 1]] - origin);
        return n;
    }

    /// The faces mirroring `which` across x = 0 (the plane the game's Mirror uses), matched by corner positions within
    /// `tolerance` metres. A face on the plane can be its own twin; faces without a twin are left out.
    public static List<int> Mirrored(IReadOnlyList<Vector3> pos, IReadOnlyList<int[]> faces, IEnumerable<int> which, float tolerance)
    {
        var which2 = which.ToList();
        var point = MeshPlans.Twins(pos, which2.SelectMany(f => faces[f]).Distinct(), tolerance);
        int Twin(int v) => point.TryGetValue(v, out int t) ? t : -1;
        static string Corners(IEnumerable<int> vs) => string.Join(",", vs.OrderBy(v => v));
        var byCorners = new Dictionary<string, int>();
        for (int f = 0; f < faces.Count; f++) byCorners.TryAdd(Corners(faces[f]), f);
        var twins = new List<int>();
        foreach (int f in which2)
        {
            var mirrored = faces[f].Select(Twin).ToArray();
            if (!mirrored.Contains(-1) && byCorners.TryGetValue(Corners(mirrored), out int twin)) twins.Add(twin);
        }
        return twins;
    }

    public static (int, int) Key(int a, int b) => a < b ? (a, b) : (b, a);
}
