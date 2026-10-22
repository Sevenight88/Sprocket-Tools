using System.Numerics;

namespace SprocketTools;

/// Maths for Create Hole, kept free of game types so it can be tested offline: turn the game's hole ring into a
/// true circle that lies in the face, stays inside it, and runs the same way round as the face (so the faces
/// the game fills in around it face outwards like the original face did).
public static class HoleRing
{
    /// New positions for the ring's vertices, in ring order. outer = the face's corners in the face's order,
    /// inner = the ring the game made, centre = where the game put the hole, size = the Hole size slider (1 = the
    /// game's size). The size is applied here, never through the game's own hole radius: a hole the game cuts bigger
    /// than its face crashes the game in its own code, before this can shrink it.
    public static Vector3[] Fit(Vector3[] outer, Vector3[] inner, Vector3 centre, out string note, float size = 1f) =>
        TryFit(outer, inner, centre, out var ring, out note, size) ? ring : inner;

    /// A rejected fit has no usable ring, and the caller must not touch the face it was going to replace. Live code
    /// uses this, not Fit above: Fit only keeps the old "leave the game's ring alone" answer for callers that already
    /// handle their own failure.
    public static bool TryFit(Vector3[] outer, Vector3[] inner, Vector3 centre, out Vector3[] ring, out string note, float size = 1f)
    {
        ring = Array.Empty<Vector3>();
        int n = outer.Length, m = inner.Length;
        if (n < 3 || m < 3 || outer.Any(p => !Finite(p)) || inner.Any(p => !Finite(p)) || !float.IsFinite(size) || size <= 0)
        { note = "面、圆环或尺寸数值无效"; return false; }

        // Work about the face, not absolute coordinates, so small plates far from the world origin stay accurate.
        var normal = Normal(outer);
        float normalSq = normal.LengthSquared();
        if (!Finite(normal) || !float.IsFinite(normalSq) || normalSq < 1e-12f)
        { note = "面太小或者已经退化，放不下孔"; return false; }
        normal = Vector3.Normalize(normal);
        var origin = outer[0];
        var mid = origin + outer.Aggregate(Vector3.Zero, (s, p) => s + (p - origin)) / n;
        Vector3 Flat(Vector3 p) => p - Vector3.Dot(p - mid, normal) * normal;
        var corners = outer.Select(Flat).ToArray();
        var gameRing = inner.Select(Flat).ToArray();
        if (!Finite(mid) || corners.Any(p => !Finite(p)) || gameRing.Any(p => !Finite(p)))
        { note = "面或圆环没法安全地压平到这个面上"; return false; }
        float extent = corners.Max(p => (p - mid).Length());
        float epsilon = Math.Max(1e-7f, extent * 1e-6f);
        if (!float.IsFinite(extent) || Enumerable.Range(0, n).Any(i => Enumerable.Range(i + 1, n - i - 1)
            .Any(j => Vector3.DistanceSquared(corners[i], corners[j]) <= epsilon * epsilon)))
        { note = "面的角点有重复或者塌在一起"; return false; }

        // Square to the part: measured from its up direction laid into the face (its forward one on a level face), half a
        // segment round, so the circle has a flat top, its mirror twin is its mirror image and 4 segments make an upright
        // square. (Starting from the face's first corner tilted it by a few degrees, differently on each side.) This same
        // basis is what makes mirror twins agree, so it is fixed before anything is measured.
        var u = Vector3.Normalize(new[] { Vector3.UnitY, Vector3.UnitZ }.Select(a => a - Vector3.Dot(a, normal) * normal).First(a => a.LengthSquared() > 0.1f));
        var v = Vector3.Cross(normal, u);
        Vector2 InPlane(Vector3 p) => new(Vector3.Dot(p - mid, u), Vector3.Dot(p - mid, v));
        var outline = corners.Select(InPlane).ToArray();
        double Cross(Vector2 a, Vector2 b) => (double)a.X * b.Y - (double)a.Y * b.X;

        // Two outline segments crossing each other: the face is a figure eight, and no circle can be said to be in it.
        bool Crosses(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
        {
            double tolerance = epsilon * Math.Max(extent, epsilon);
            double abC = Cross(b - a, c - a), abD = Cross(b - a, d - a), cdA = Cross(d - c, a - c), cdB = Cross(d - c, b - c);
            bool On(Vector2 p, Vector2 x, Vector2 y) => p.X >= Math.Min(x.X, y.X) - epsilon && p.X <= Math.Max(x.X, y.X) + epsilon &&
                p.Y >= Math.Min(x.Y, y.Y) - epsilon && p.Y <= Math.Max(x.Y, y.Y) + epsilon;
            return (Math.Abs(abC) <= tolerance && On(c, a, b)) || (Math.Abs(abD) <= tolerance && On(d, a, b)) ||
                (Math.Abs(cdA) <= tolerance && On(a, c, d)) || (Math.Abs(cdB) <= tolerance && On(b, c, d)) ||
                ((abC > tolerance && abD < -tolerance || abC < -tolerance && abD > tolerance) &&
                 (cdA > tolerance && cdB < -tolerance || cdA < -tolerance && cdB > tolerance));
        }
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
                if (j != (i + 1) % n && i != (j + 1) % n && Crosses(outline[i], outline[(i + 1) % n], outline[j], outline[(j + 1) % n]))
                { note = "面的轮廓自己交叉了"; return false; }

        // Measure the ring the game made about its own middle, before the hole is moved anywhere. Otherwise a ring the
        // game collapsed onto an edge would turn into a big requested radius just by its middle being relocated.
        var ringOrigin = gameRing[0];
        var ringCentre = ringOrigin + gameRing.Aggregate(Vector3.Zero, (s, p) => s + (p - ringOrigin)) / m;
        float asked = gameRing.Average(p => (p - ringCentre).Length()) * size;
        float ringSpread = Math.Abs(Vector3.Dot(Normal(gameRing), normal));
        if (!float.IsFinite(asked) || asked < 1e-4f || !float.IsFinite(ringSpread) ||
            ringSpread < 1e-4f * (asked / size) * (asked / size))
        { note = "游戏做的圆环已经塌成一条线或者太小"; return false; }

        float Room(Vector3 p) => Enumerable.Range(0, n).Min(i => DistanceToSegment(p, corners[i], corners[(i + 1) % n]));

        // Point in polygon (any shape, concave included): add up the angle the corners turn through as seen from p.
        // A point on the edge has no room round it, so it is never a usable centre.
        bool Inside(Vector3 p)
        {
            if (!Finite(p) || Room(p) <= epsilon) return false;
            var q = InPlane(p);
            double total = 0;
            for (int i = 0; i < n; i++)
            {
                var a = outline[i] - q; var b = outline[(i + 1) % n] - q;
                total += Math.Atan2(Cross(a, b), Vector2.Dot(a, b));
            }
            return Math.Abs(total) > Math.PI; // about 2 pi inside, about 0 outside
        }

        var c = Finite(centre) ? Flat(centre) : mid;
        bool moved = !Inside(c);
        if (moved)
        {
            // The game's centre can sit outside the face, and a bent or hollowed face's middle can too. Try the spots
            // that are likely to be inside (the middle, each corner's notch, a coarse grid) and keep the one with the
            // most room; a point on an edge never counts, so a circle placed here has real clearance.
            var candidates = new List<Vector3> { mid };
            for (int i = 0; i < n; i++) candidates.Add((corners[(i + n - 1) % n] + corners[i] + corners[(i + 1) % n]) / 3);
            float left = outline.Min(p => p.X), right = outline.Max(p => p.X), bottom = outline.Min(p => p.Y), top = outline.Max(p => p.Y);
            for (int x = 1; x < 8; x++)
                for (int y = 1; y < 8; y++) candidates.Add(mid + u * (left + (right - left) * x / 8) + v * (bottom + (top - bottom) * y / 8));
            var safe = candidates.Where(Inside).OrderByDescending(Room).ToList();
            if (safe.Count == 0) { note = "这个面里找不到能安全放孔的位置"; return false; }
            c = safe[0];
        }
        float radius = Math.Min(asked, Room(c) * 0.95f); // clear of every edge, so the circle stays inside the face
        if (!float.IsFinite(radius) || radius < 1e-4f) { note = "这个面的余量放不下孔"; return false; }
        var result = new Vector3[m];
        for (int k = 0; k < m; k++)
        {
            double angle = 2 * Math.PI * (k + 0.5) / m;
            result[k] = c + radius * ((float)Math.Cos(angle) * u + (float)Math.Sin(angle) * v);
        }
        if (result.Any(p => !Inside(p))) { note = "圆环没能全部留在面里"; return false; }
        ring = result;
        note = $"{m} 段，半径 {asked * 1000:0} -> {radius * 1000:0} mm" + (moved ? "，圆心已挪进面里" : "");
        return true;
    }

    /// Newell's normal: the side a polygon faces given its corner order (also right for slightly bent faces).
    public static Vector3 Normal(IReadOnlyList<Vector3> corners)
    {
        if (corners.Count < 3) return Vector3.Zero;
        // Work relative to one corner: absolute-coordinate products lose the normal of
        // small faces far from the origin. The signed fan sum equals Newell's normal.
        var origin = corners[0];
        var normal = Vector3.Zero;
        for (int i = 1; i + 1 < corners.Count; i++)
            normal += Vector3.Cross(corners[i] - origin, corners[i + 1] - origin);
        return normal;
    }

    static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);

    static float DistanceToSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        var ab = b - a;
        float t = ab.LengthSquared() < 1e-12f ? 0 : Math.Clamp(Vector3.Dot(p - a, ab) / ab.LengthSquared(), 0, 1);
        return Vector3.Distance(p, a + t * ab);
    }
}
