using System.Numerics;

namespace SprocketTools;

/// Blender-style cloth drape: a flat sheet of points falls under gravity onto solid triangles (the vehicle's shapes) and
/// settles, held together by springs along its sides, across its cells and over every second point (bending), the usual
/// position-based way. A point that lands stays put (friction), and nothing goes below `floor`. Pure maths.
public static class Cloth
{
    public sealed record Result(Vector3[] Points, int Columns, int Rows, int Steps, bool Settled);

    const float Step = 1 / 60f, Gravity = 9.81f, Damping = 0.985f;
    const int Iterations = 8, QuietSteps = 30, LiftRounds = 20;

    /// Points across and along a sheet `width` by `length` with points about `cell` apart (at most 160 a side).
    public static (int Columns, int Rows) Size(float width, float length, float cell) =>
        (Math.Clamp((int)MathF.Round(width / cell) + 1, 2, 160), Math.Clamp((int)MathF.Round(length / cell) + 1, 2, 160));

    /// The sheet starts flat: `corner`, then `across` (its columns) and `along` (its rows), points about `cell` apart.
    /// `gap`: how far it rests above what it lands on; `bend`: how hard it resists bending (0.15 drapes like canvas).
    public static Result Drape(Vector3 corner, Vector3 across, Vector3 along, float cell, IReadOnlyList<(Vector3 A, Vector3 B, Vector3 C)> solid,
                               float floor, float gap = 0.015f, int maxSteps = 900, float bend = 0.15f)
    {
        var (cols, rows) = Size(across.Length(), along.Length(), cell);
        int n = cols * rows;
        var p = new Vector3[n];
        for (int j = 0; j < rows; j++)
            for (int i = 0; i < cols; i++) p[j * cols + i] = corner + across * (i / (cols - 1f)) + along * (j / (rows - 1f));
        var prev = (Vector3[])p.Clone();
        var springs = new List<(int A, int B, float Length, float K)>();
        void Spring(int a, int b, float k) => springs.Add((a, b, Vector3.Distance(p[a], p[b]), k));
        for (int j = 0; j < rows; j++)
            for (int i = 0; i < cols; i++)
            {
                int a = j * cols + i;
                if (i + 1 < cols) Spring(a, a + 1, 1);
                if (j + 1 < rows) Spring(a, a + cols, 1);
                if (i + 1 < cols && j + 1 < rows) { Spring(a, a + cols + 1, 1); Spring(a + 1, a + cols, 1); }
                if (i + 2 < cols) Spring(a, a + 2, bend);
                if (j + 2 < rows) Spring(a, a + 2 * cols, bend);
            }
        var grid = new TriangleGrid(solid, Math.Max(cell * 2, 0.2f), gap);
        var fall = new Vector3(0, -Gravity * Step * Step, 0);
        var start = new Vector3[n];
        var touching = new bool[n];
        // Points out of the solid and off the floor; a point that touches slides along the face (the springs need that
        // to drape it over an edge).
        void Collide()
        {
            for (int k = 0; k < n; k++)
            {
                if (grid.Push(start[k], p[k], out var at)) { p[k] = at; touching[k] = true; }
                if (p[k].Y < floor + gap) { p[k].Y = floor + gap; touching[k] = true; }
            }
        }
        // Points along the edges of what it lands on (corners included, half a cell apart), filed by grid cell: one poking
        // up between the sheet's points (an edge, a handle, a periscope) lifts the cloth over it, as Blender's cloth keeps
        // its faces, not only its points, off what it covers.
        float size = Math.Max(cell * 2, 0.2f), reach = Math.Max(cell, gap);
        (int, int, int) CellOf(Vector3 q) => ((int)MathF.Floor(q.X / size), (int)MathF.Floor(q.Y / size), (int)MathF.Floor(q.Z / size));
        var corners = new List<Vector3>();
        var filed = new Dictionary<(int, int, int), List<int>>();
        foreach (var (ta, tb, tc) in solid)
            foreach (var (u, w) in new[] { (ta, tb), (tb, tc), (tc, ta) })
            {
                int parts = Math.Max(1, (int)MathF.Ceiling(Vector3.Distance(u, w) / (cell / 2)));
                for (int s = 0; s < parts; s++)
                {
                    var q = Vector3.Lerp(u, w, s / (float)parts); // the edge's far end is the next edge's start
                    (filed.TryGetValue(CellOf(q), out var l) ? l : filed[CellOf(q)] = new()).Add(corners.Count);
                    corners.Add(q);
                }
            }
        float outward = Vector3.Cross(across, along).Y >= 0 ? 1 : -1; // the sheet's outer side: up, as it starts
        // Each corner's nearest spot on the cloth within reach: a cloth triangle and the spot's share of its points.
        var nearest = new (float Distance, int X, int Y, int Z, Vector3 W)[corners.Count];
        void Nearest(int x, int y, int z)
        {
            var (e1, e2) = (p[y] - p[x], p[z] - p[x]);
            var (lo, hi) = (CellOf(Vector3.Min(p[x], Vector3.Min(p[y], p[z])) - new Vector3(reach)), CellOf(Vector3.Max(p[x], Vector3.Max(p[y], p[z])) + new Vector3(reach)));
            for (int cx = lo.Item1; cx <= hi.Item1; cx++)
                for (int cy = lo.Item2; cy <= hi.Item2; cy++)
                    for (int cz = lo.Item3; cz <= hi.Item3; cz++)
                        if (filed.TryGetValue((cx, cy, cz), out var near))
                            foreach (int i in near)
                            {
                                var w = ClosestOnTriangle(corners[i] - p[x], e1, e2);
                                float distance = Vector3.Distance(corners[i], p[x] + e1 * w.Y + e2 * w.Z);
                                if (distance < nearest[i].Distance) nearest[i] = (distance, x, y, z, w);
                            }
        }
        bool Border(int k) => k < cols || k >= n - cols || k % cols == 0 || k % cols == cols - 1;
        var lift = new Vector3[n]; // each point's largest lift this round (several corners share a point: no adding up)
        // A corner out past its nearest spot on the cloth, or less than `gap` inside it: that spot is raised `gap` clear of
        // it, each of its points by its share. The nearest spot, not the triangle straight below: over a fold between two
        // triangles (a cell tented over a periscope) the corner lies straight below neither, and the fold itself rises.
        void Lift(int i)
        {
            var (distance, x, y, z, w) = nearest[i];
            if (x < 0) return;
            var normal = Vector3.Cross(p[y] - p[x], p[z] - p[x]) * outward;
            if (normal.LengthSquared() < 1e-14f) return;
            normal = Vector3.Normalize(normal);
            var r = corners[i] - (p[x] * w.X + p[y] * w.Y + p[z] * w.Z);
            float side = Vector3.Dot(r, normal); // + out past the cloth, - inside it
            // Beside the cloth rather than over or under it (past its border, or nearly in line with it): not to cover.
            if (Math.Abs(side) < 0.25f * distance ||
                (w.X == 0 || w.Y == 0 || w.Z == 0) && (w.X == 0 || Border(x)) && (w.Y == 0 || Border(y)) && (w.Z == 0 || Border(z))) return;
            float s = (MathF.Sign(side) * distance + gap) / Vector3.Dot(w, w);
            if (s <= 0) return;
            var dir = distance > 1e-6f ? r * (MathF.Sign(side) / distance) : normal;
            Raise(x, dir * (s * w.X)); Raise(y, dir * (s * w.Y)); Raise(z, dir * (s * w.Z));
        }
        void Raise(int k, Vector3 by) { if (by.LengthSquared() > lift[k].LengthSquared()) lift[k] = by; }
        void Springs()
        {
            foreach (var (a, b, length, stiff) in springs)
            {
                var d = p[b] - p[a];
                float len = d.Length();
                if (len < 1e-9f) continue;
                var fix = d * ((len - length) / len * 0.5f * stiff);
                p[a] += fix; p[b] -= fix;
            }
        }
        int quiet = 0, step = 0;
        for (; step < maxSteps && quiet < QuietSteps; step++)
        {
            Array.Copy(p, start, n);
            Array.Clear(touching);
            for (int k = 0; k < n; k++) { var v = (p[k] - prev[k]) * Damping; prev[k] = p[k]; p[k] += v + fall; }
            Collide();
            for (int it = 0; it < Iterations; it++)
            {
                Springs();
                Collide(); // springs and contacts solved together: over an edge, a spring pulls a touching point along the face
            }
            float moved = 0;
            for (int k = 0; k < n; k++)
            {
                if (touching[k]) prev[k] = p[k] - (p[k] - start[k]) * (1 - Friction); // friction: most of its sliding speed goes
                moved = Math.Max(moved, Vector3.Distance(p[k], start[k]));
            }
            quiet = moved < 5e-4f ? quiet + 1 : 0;
        }
        // Once it has settled: lifted over what pokes out between its points, the springs evening the lifted cells out,
        // a few rounds, lifting last so nothing pokes through.
        for (int round = 0; round < LiftRounds; round++)
        {
            Array.Copy(p, start, n);
            Springs();
            Collide();
            Array.Fill(nearest, (reach, -1, -1, -1, Vector3.Zero));
            for (int j = 0; j + 1 < rows; j++)
                for (int i = 0; i + 1 < cols; i++)
                {
                    int a = j * cols + i;
                    Nearest(a, a + 1, a + cols + 1);
                    Nearest(a, a + cols + 1, a + cols);
                }
            Array.Clear(lift);
            for (int i = 0; i < corners.Count; i++) Lift(i);
            for (int k = 0; k < n; k++) p[k] += lift[k];
        }
        return new Result(p, cols, rows, step, quiet >= QuietSteps);
    }

    const float Friction = 0.7f;

    /// The spot of triangle (0, e1, e2) nearest q, as its share of the three corners; a share of 0 puts it on the far edge
    /// (or two of them, on a corner). Ericson, Real-Time Collision Detection, 5.1.5.
    static Vector3 ClosestOnTriangle(Vector3 q, Vector3 e1, Vector3 e2)
    {
        float d1 = Vector3.Dot(e1, q), d2 = Vector3.Dot(e2, q);
        if (d1 <= 0 && d2 <= 0) return new(1, 0, 0);
        float d3 = Vector3.Dot(e1, q - e1), d4 = Vector3.Dot(e2, q - e1);
        if (d3 >= 0 && d4 <= d3) return new(0, 1, 0);
        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) { float t = d1 / (d1 - d3); return new(1 - t, t, 0); }
        float d5 = Vector3.Dot(e1, q - e2), d6 = Vector3.Dot(e2, q - e2);
        if (d6 >= 0 && d5 <= d6) return new(0, 0, 1);
        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) { float t = d2 / (d2 - d6); return new(1 - t, 0, t); }
        float va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 >= d3 && d5 >= d6) { float t = (d4 - d3) / (d4 - d3 + d5 - d6); return new(0, 1 - t, t); }
        float den = va + vb + vc;
        return new(va / den, vb / den, vc / den);
    }

    /// Solid triangles filed by grid cell, each with its outward normal (the faces' winding: plates face out).
    sealed class TriangleGrid
    {
        readonly (Vector3 A, Vector3 E1, Vector3 E2, Vector3 N)[] tris;
        readonly Dictionary<(int, int, int), List<int>> cells = new();
        readonly float size, gap;
        readonly int[] seen;
        int stamp;

        internal TriangleGrid(IReadOnlyList<(Vector3 A, Vector3 B, Vector3 C)> solid, float size, float gap)
        {
            this.size = size; this.gap = gap;
            var list = new List<(Vector3, Vector3, Vector3, Vector3)>();
            foreach (var (a, b, c) in solid)
            {
                var n = Vector3.Cross(b - a, c - a);
                if (n.LengthSquared() < 1e-14f) continue;
                int index = list.Count;
                list.Add((a, b - a, c - a, Vector3.Normalize(n)));
                var (lo, hi) = (Cell(Vector3.Min(a, Vector3.Min(b, c)) - new Vector3(gap)), Cell(Vector3.Max(a, Vector3.Max(b, c)) + new Vector3(gap)));
                for (int x = lo.Item1; x <= hi.Item1; x++)
                    for (int y = lo.Item2; y <= hi.Item2; y++)
                        for (int z = lo.Item3; z <= hi.Item3; z++)
                        {
                            if (!cells.TryGetValue((x, y, z), out var l)) cells[(x, y, z)] = l = new();
                            l.Add(index);
                        }
            }
            tris = list.ToArray();
            seen = new int[tris.Length];
        }

        (int, int, int) Cell(Vector3 p) => ((int)MathF.Floor(p.X / size), (int)MathF.Floor(p.Y / size), (int)MathF.Floor(p.Z / size));

        /// A point moving from `from` to `to` that reaches or passes a face (over the face, not beside it): pushed out to
        /// `gap` in front of the first such face, sliding along it as far as it still lies over it; false if it reaches none.
        internal bool Push(Vector3 from, Vector3 to, out Vector3 at)
        {
            at = to;
            stamp++;
            float best = float.MaxValue;
            var (lo, hi) = (Cell(Vector3.Min(from, to) - new Vector3(gap)), Cell(Vector3.Max(from, to) + new Vector3(gap)));
            for (int x = lo.Item1; x <= hi.Item1; x++)
                for (int y = lo.Item2; y <= hi.Item2; y++)
                    for (int z = lo.Item3; z <= hi.Item3; z++)
                    {
                        if (!cells.TryGetValue((x, y, z), out var list)) continue;
                        foreach (int i in list)
                        {
                            if (seen[i] == stamp) continue;
                            seen[i] = stamp;
                            var (a, e1, e2, n) = tris[i];
                            float df = Vector3.Dot(from - a, n), dt = Vector3.Dot(to - a, n);
                            if (df < -gap || dt >= gap) continue; // started behind the face, or ends clear of it
                            float t = df > gap ? (df - gap) / (df - dt) : 0; // when it comes within `gap`, or at once if it started there
                            var hit = Vector3.Lerp(from, to, t);
                            if (t >= best || !Over(hit - n * Vector3.Dot(hit - a, n) - a, e1, e2)) continue;
                            best = t;
                            var end = Over(to - n * dt - a, e1, e2) ? to : hit; // slides on while still over the face
                            at = end + n * (gap - Vector3.Dot(end - a, n));
                        }
                    }
            return best < float.MaxValue;
        }

        /// Whether point q (relative to a corner, in the triangle's plane) lies over the triangle.
        static bool Over(Vector3 q, Vector3 e1, Vector3 e2) => Weights(q, e1, e2) != null;

        /// Point q's share of the triangle's three corners (its corner, then along e1, then along e2) when q (relative to the
        /// first corner, in the triangle's plane) lies over the triangle; null when it doesn't.
        static Vector3? Weights(Vector3 q, Vector3 e1, Vector3 e2)
        {
            float d11 = Vector3.Dot(e1, e1), d12 = Vector3.Dot(e1, e2), d22 = Vector3.Dot(e2, e2), q1 = Vector3.Dot(q, e1), q2 = Vector3.Dot(q, e2);
            float den = d11 * d22 - d12 * d12;
            if (Math.Abs(den) < 1e-14f) return null;
            float u = (d22 * q1 - d12 * q2) / den, v = (d11 * q2 - d12 * q1) / den;
            return u >= -1e-4f && v >= -1e-4f && u + v <= 1 + 1e-4f ? new Vector3(1 - u - v, u, v) : null;
        }
    }
}
