using System.Diagnostics;
using System.Numerics;

namespace SprocketTools;

/// Makes an imported OBJ shell editable again. A render mesh arrives triangulated and split at its UV and normal
/// seams; once the points are welded, this joins coplanar neighbours back into as few plate faces as the game's
/// format allows (three or four corners). The maths is FaceMerge's, the engine behind the in-game "合并面" button,
/// driven here over the whole mesh with every face chosen and no points of its own added.
/// Shells that touch nowhere are merged and checked one at a time, so a patch that would crack, crowd or flip
/// somewhere costs the merge only in its own shell. Pure geometry, so an offline probe can measure it against
/// real plate structures.
public static class ObjWeld
{
    /// `FacesBefore`/`UsableBefore` say how much of the mesh could be joined at all; `Shells` how many pieces the mesh
    /// falls into; `Refused`/`Unsound` how many candidate patches FaceMerge and our own guards would not take;
    /// `LeftAlone` how many shells kept every triangle. `Why` explains a shell left alone (null when all joined well).
    public sealed record Result(int[][] Faces, int Triangles, int Quads, int FacesBefore, int UsableBefore,
        int Shells, int Refused, int Unsound, int LeftAlone, long Milliseconds, string? Why);

    /// FaceMerge's straight-side search is quadratic in an outline's points, so a single dense fan from another
    /// tool's exporter would stall the whole import instead of only slowing it. Such a patch keeps its triangles.
    const int MaxPatchFaces = 4000;

    /// A shell bigger than this is left alone: one OBJ of a whole vehicle can be tens of thousands of faces, and
    /// an import nobody watches should not sit computing for minutes.
    const int MaxShellFaces = 20000;

    /// How far the corners of a merged face may stand off its own plane, as a fraction of its longest diagonal, and
    /// at least this many metres. The probe over 411 real plate structures (_probe184) found the game's own authors
    /// leave quads bent by up to 0.20 of their diagonal, so this only cuts off the nonsense above what the game
    /// already stores; FaceMerge's 20° join is what actually decides which faces may meet.
    const float MaxBendRatio = 0.25f, MinBend = 0.0005f;

    /// `faces` must already hold only 3- and 4-corner faces with distinct, in-range indices, which is exactly what
    /// the OBJ import produces after it has split larger polygons down.
    public static Result Merge(IReadOnlyList<Vector3> pos, IReadOnlyList<int[]> faces)
    {
        int before = faces.Count;
        int usable = faces.Count(f => f.Length is 3 or 4);
        Result Unchanged(string? why, int shells = 0, int refused = 0, int unsound = 0, int alone = 0) =>
            new(faces.ToArray(), faces.Count(f => f.Length == 3), faces.Count(f => f.Length == 4),
                before, usable, shells, refused, unsound, alone, 0, why);
        if (before < 2 || usable < 2) return Unchanged(null); // nothing to join, and nothing to explain
        if (faces.Any(f => f.Length is < 3 or > 4) || faces.Any(f => f.Distinct().Count() != f.Length) ||
            faces.Any(f => f.Any(i => i < 0 || i >= pos.Count)) ||
            pos.Any(p => !float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z)))
            return Unchanged("网格含有无效的点或面，没有合并");

        var watch = Stopwatch.StartNew();
        var gone = new bool[before];
        var joined = new List<int[]>();
        int shells = 0, refused = 0, unsound = 0, alone = 0;
        string? why = null;
        foreach (var shell in Shells(faces))
        {
            if (shell.Count < 2) continue;
            shells++;
            if (shell.Count > MaxShellFaces) { alone++; continue; }
            // Renumber: FaceMerge and MeshPlans walk every face they are given, so a shell must be handed over on its
            // own or a hundred small ones would each rescan the whole import.
            var map = new Dictionary<int, int>();
            var points = new List<Vector3>();
            var local = new List<int[]>(shell.Count);
            var back = new List<int>(); // a shell's point number, and the point of the whole mesh it came from
            foreach (int f in shell)
            {
                var corners = new int[faces[f].Length];
                for (int k = 0; k < corners.Length; k++)
                {
                    int global = faces[f][k];
                    if (!map.TryGetValue(global, out int at))
                    {
                        at = points.Count; map[global] = at; points.Add(pos[global]); back.Add(global);
                    }
                    corners[k] = at;
                }
                local.Add(corners);
            }
            List<FaceMerge.Group> groups;
            try
            {
                // Every face chosen, no loose edges, and side points kept: a patch never rebuilds a face beside it.
                groups = FaceMerge.Plan(points, local, Enumerable.Range(0, local.Count).ToHashSet(),
                    new HashSet<(int, int)>(), FaceMerge.SidePoints.Keep);
            }
            catch (Exception e) { alone++; refused++; why ??= "合并已跳过：" + e.Message; continue; }
            // Patches FaceMerge itself would not take (a bend, an unclosed outline, a filling that failed) are counted
            // where they have more than one face: a single face has nothing to join, which is not a refusal.
            refused += groups.Count(g => g.Why != null && g.Faces.Count >= 2);
            var todo = new List<FaceMerge.Group>();
            foreach (var g in groups.Where(g => g.Why == null && g.Faces.Count >= 2 && g.Faces.Count <= MaxPatchFaces &&
                g.NewFaces.Count > 0 && g.NewFaces.Count < g.Faces.Count))
                if (Sound(points, g, local)) todo.Add(g); else unsound++;
            if (todo.Count == 0) continue;
            var remove = todo.SelectMany(g => g.Faces).ToList();
            var add = todo.SelectMany(g => g.NewFaces.Select(nf => new MeshPlans.NewFace(nf, g.Faces[0]))).ToList();
            if (MeshPlans.Check(points, local, new MeshPlans.Rebuild(remove, add, new(), null)) is string broken)
            {
                alone++;
                why ??= "有部件的合并会弄破形状，已保留原样：" + broken;
                continue;
            }
            foreach (int f in remove) gone[shell[f]] = true;
            joined.AddRange(add.Select(a => a.Corners.Select(i => back[i]).ToArray()));
        }
        watch.Stop();
        if (joined.Count == 0)
            return Unchanged(why ?? (refused + unsound > 0 ? "部分面的朝向或形状不适合合并" : "面已经是最少的了"),
                shells, refused, unsound, alone);
        var output = new List<int[]>(before + joined.Count);
        for (int f = 0; f < before; f++) if (!gone[f]) output.Add(faces[f]);
        output.AddRange(joined);
        return new(output.ToArray(), output.Count(f => f.Length == 3), output.Count(f => f.Length == 4), before,
            usable, shells, refused, unsound, alone, watch.ElapsedMilliseconds, alone == 0 && refused + unsound == 0 ? null : why);
    }

    /// Faces joined edge to edge, however many separate shells the mesh holds. Shells that only touch at a point are
    /// kept apart: nothing can join across a point the two sides merely share.
    static List<List<int>> Shells(IReadOnlyList<int[]> faces)
    {
        var owner = new Dictionary<(int, int), int>();
        var parent = Enumerable.Range(0, faces.Count).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        for (int f = 0; f < faces.Count; f++)
            for (int k = 0; k < faces[f].Length; k++)
            {
                int a = faces[f][k], b = faces[f][(k + 1) % faces[f].Length];
                var key = a < b ? (a, b) : (b, a);
                if (owner.TryGetValue(key, out int other)) parent[Find(other)] = Find(f);
                else owner[key] = f;
            }
        var shells = new List<List<int>>();
        var at = new Dictionary<int, int>();
        for (int f = 0; f < faces.Count; f++)
        {
            int root = Find(f);
            if (!at.TryGetValue(root, out int i)) { at[root] = i = shells.Count; shells.Add(new()); }
            shells[i].Add(f);
        }
        return shells;
    }

    /// Whether a patch's new faces are worth taking at all: they must cover their patch without a squashed or
    /// turned-over face, must not lose area, and must not bend a plate more than MaxBendRatio. Anything else and the
    /// patch keeps the triangles it came with.
    static bool Sound(IReadOnlyList<Vector3> pos, FaceMerge.Group g, IReadOnlyList<int[]> faces)
    {
        var normal = Vector3.Zero;
        double old = 0;
        foreach (int f in g.Faces)
        {
            var n = Newell(pos, faces[f]);
            normal += n;
            old += n.Length() / 2.0;
        }
        if (old <= 0 || !double.IsFinite(old)) return false;
        normal = Vector3.Normalize(normal);
        double added = 0;
        foreach (var face in g.NewFaces)
        {
            if (face.Length is < 3 or > 4 || face.Distinct().Count() != face.Length ||
                face.Any(i => i < 0 || i >= pos.Count)) return false;
            var n = Newell(pos, face);
            float area = n.Length() / 2f;
            added += area;
            if (area < 1e-5f * old) return false; // squashed to next to nothing
            if (Vector3.Dot(n, normal) < 0.5f * n.Length()) return false; // turned over against its patch
            float span = 0;
            for (int a = 0; a + 1 < face.Length; a++)
                for (int b = a + 1; b < face.Length; b++) span = Math.Max(span, Vector3.Distance(pos[face[a]], pos[face[b]]));
            if (Bend(pos, face, n) > Math.Max(MinBend, MaxBendRatio * span)) return false;
        }
        return added >= 0.995 * old; // merging a patch only takes the diagonals out, so its area stays
    }

    /// Twice the area-weighted normal of a face, fanned from one of its own corners: products of absolute
    /// coordinates lose the direction of a small face far from the origin.
    static Vector3 Newell(IReadOnlyList<Vector3> pos, IReadOnlyList<int> face)
    {
        var origin = pos[face[0]];
        var n = Vector3.Zero;
        for (int k = 1; k + 1 < face.Count; k++) n += Vector3.Cross(pos[face[k]] - origin, pos[face[k + 1]] - origin);
        return n;
    }

    /// How far the corners stand off the face's own area-weighted plane, in metres.
    static float Bend(IReadOnlyList<Vector3> pos, IReadOnlyList<int> face, Vector3 plane)
    {
        if (plane.LengthSquared() < 1e-20f) return 0;
        var n = Vector3.Normalize(plane);
        float worst = 0;
        foreach (int i in face) worst = Math.Max(worst, Math.Abs(Vector3.Dot(pos[i] - pos[face[0]], n)));
        return worst;
    }
}
