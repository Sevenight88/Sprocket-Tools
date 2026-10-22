using System.Numerics;

namespace SprocketTools;

/// Checks the whole planned plate around a hole before any game mesh element is created. The game's own face checks
/// only look at the links between elements, so a ring that has collapsed onto itself can still produce faces whose
/// links are all correct while the plate it fills is solid, crossed, or leaving the face.
public static class HoleFill
{
    /// `outer` runs outwards according to `normal`; `inner` may run either way. Positions include any points
    /// Fill.Region appended. Returns null only when positively-turned triangles and quads cover exactly the open plate.
    public static string? Check(IReadOnlyList<Vector3> positions, IReadOnlyList<int> outer,
        IReadOnlyList<int> inner, IReadOnlyList<int[]> faces, Vector3 normal)
    {
        if (!Finite(normal) || normal.LengthSquared() < 1e-20f) return "这块板没有可用的朝向";
        if (outer.Count < 3 || inner.Count < 3 || faces.Count == 0) return "孔或者孔周围的面缺失了";
        if (positions.Any(p => !Finite(p))) return "孔里有读不出来的坐标";
        bool Index(int i) => i >= 0 && i < positions.Count;
        if (outer.Concat(inner).Any(i => !Index(i))) return "孔的边界指到一个不存在的点";
        if (outer.Distinct().Count() != outer.Count || inner.Distinct().Count() != inner.Count || outer.Intersect(inner).Any())
            return "孔的边界上有点被用了两次";

        var n = Vector3.Normalize(normal);
        var u = Vector3.Normalize(new[] { Vector3.UnitY, Vector3.UnitZ }
            .Select(axis => axis - Vector3.Dot(axis, n) * n).First(axis => axis.LengthSquared() > .1f));
        var vAxis = Vector3.Cross(n, u);
        var origin = positions[outer[0]];
        var p = positions.Select(at => Project(at, origin, u, vAxis)).ToArray();
        double minX = outer.Min(i => p[i].X), maxX = outer.Max(i => p[i].X);
        double minY = outer.Min(i => p[i].Y), maxY = outer.Max(i => p[i].Y);
        double scale = Math.Sqrt((maxX - minX) * (maxX - minX) + (maxY - minY) * (maxY - minY));
        double lengthEpsilon = Math.Max(1e-8, scale * 1e-7);
        double areaEpsilon = Math.Max(1e-16, scale * scale * 1e-12);

        if (!Simple(outer) || !Simple(inner)) return "孔的边界塌成一条线，或者自己交叉了";
        double outerArea = Area(outer), innerArea = Area(inner);
        if (outerArea <= areaEpsilon) return "板的轮廓没有朝外的面积";
        if (Math.Abs(innerArea) <= areaEpsilon) return "孔没有开口面积";
        for (int k = 0; k < inner.Count; k++)
        {
            var point = p[inner[k]];
            if (!Inside(outer, point) || Enumerable.Range(0, outer.Count).Any(j =>
                Distance(point, p[outer[j]], p[outer[(j + 1) % outer.Count]]) <= lengthEpsilon))
                return "孔贴到了板边，或者跑出了板外";
            for (int j = 0; j < outer.Count; j++)
                if (Meet(point, p[inner[(k + 1) % inner.Count]], p[outer[j]], p[outer[(j + 1) % outer.Count]]))
                    return "孔穿过了板的边界";
        }

        var boundary = new Dictionary<(int, int), (int A, int B)>();
        AddBoundary(outer, reverse: false);
        AddBoundary(inner, reverse: innerArea > 0);
        var uses = new Dictionary<(int, int), List<(int A, int B)>>();
        double madeArea = 0;
        foreach (var face in faces)
        {
            if (face == null || face.Length is < 3 or > 4 || face.Distinct().Count() != face.Length || face.Any(i => !Index(i)))
                return "孔周围有面不是合法的三角面或四边面";
            double area = Area(face);
            if (!double.IsFinite(area) || area <= areaEpsilon) return "孔周围有面塌了，或者朝里";
            // A positive signed area alone does not rule out a crossed or concave quad.
            for (int k = 0; k < face.Length; k++)
                if (Cross(p[face[(k + 1) % face.Length]] - p[face[k]], p[face[(k + 2) % face.Length]] - p[face[(k + 1) % face.Length]]) <= areaEpsilon)
                    return "孔周围有面折起来了，或者自己交叉";
            madeArea += area;
            for (int k = 0; k < face.Length; k++)
            {
                int a = face[k], b = face[(k + 1) % face.Length];
                var key = Key(a, b);
                if (!uses.TryGetValue(key, out var list)) uses[key] = list = new();
                list.Add((a, b));
            }
        }
        foreach (var (edge, direction) in boundary)
            if (!uses.TryGetValue(edge, out var list) || list.Count != 1 || list[0] != direction)
                return "孔周围的面没有把孔边和板边每条都正好保留一次";
        foreach (var (edge, list) in uses)
            if (!boundary.ContainsKey(edge) && (list.Count != 2 || list[0].A != list[1].B || list[0].B != list[1].A))
                return "孔周围的面在内侧边上留了缝，或者叠在了一起";
        double wantedArea = outerArea - Math.Abs(innerArea);
        if (wantedArea <= areaEpsilon || Math.Abs(madeArea - wantedArea) > Math.Max(areaEpsilon * 16, outerArea * 2e-5))
            return "孔周围的面没有正好盖住孔外面那块板";
        return null;

        double Area(IReadOnlyList<int> loop)
        {
            double area = 0;
            for (int k = 0; k < loop.Count; k++) area += Cross(p[loop[k]], p[loop[(k + 1) % loop.Count]]);
            return area * .5;
        }
        bool Simple(IReadOnlyList<int> loop)
        {
            for (int k = 0; k < loop.Count; k++)
            {
                for (int j = k + 1; j < loop.Count; j++)
                    if ((p[loop[k]] - p[loop[j]]).Squared <= lengthEpsilon * lengthEpsilon) return false;
                int next = (k + 1) % loop.Count;
                for (int j = k + 1; j < loop.Count; j++)
                {
                    int after = (j + 1) % loop.Count;
                    if (next == j || after == k) continue;
                    if (Meet(p[loop[k]], p[loop[next]], p[loop[j]], p[loop[after]])) return false;
                }
            }
            return true;
        }
        bool Meet(Point a, Point b, Point c, Point d)
        {
            if (Math.Max(a.X, b.X) + lengthEpsilon < Math.Min(c.X, d.X) || Math.Max(c.X, d.X) + lengthEpsilon < Math.Min(a.X, b.X) ||
                Math.Max(a.Y, b.Y) + lengthEpsilon < Math.Min(c.Y, d.Y) || Math.Max(c.Y, d.Y) + lengthEpsilon < Math.Min(a.Y, b.Y)) return false;
            double abC = Cross(b - a, c - a), abD = Cross(b - a, d - a);
            double cdA = Cross(d - c, a - c), cdB = Cross(d - c, b - c);
            if ((abC > areaEpsilon && abD < -areaEpsilon || abC < -areaEpsilon && abD > areaEpsilon) &&
                (cdA > areaEpsilon && cdB < -areaEpsilon || cdA < -areaEpsilon && cdB > areaEpsilon)) return true;
            return Distance(a, c, d) <= lengthEpsilon || Distance(b, c, d) <= lengthEpsilon ||
                Distance(c, a, b) <= lengthEpsilon || Distance(d, a, b) <= lengthEpsilon;
        }
        bool Inside(IReadOnlyList<int> loop, Point point)
        {
            bool inside = false;
            for (int k = 0; k < loop.Count; k++)
            {
                var a = p[loop[k]]; var b = p[loop[(k + 1) % loop.Count]];
                if ((a.Y > point.Y) != (b.Y > point.Y) && point.X < a.X + (point.Y - a.Y) / (b.Y - a.Y) * (b.X - a.X)) inside = !inside;
            }
            return inside;
        }
        void AddBoundary(IReadOnlyList<int> loop, bool reverse)
        {
            for (int k = 0; k < loop.Count; k++)
            {
                int a = loop[k], b = loop[(k + 1) % loop.Count];
                boundary.Add(Key(a, b), reverse ? (b, a) : (a, b));
            }
        }
    }

    readonly record struct Point(double X, double Y)
    {
        public static Point operator -(Point a, Point b) => new(a.X - b.X, a.Y - b.Y);
        public double Squared => X * X + Y * Y;
    }
    static Point Project(Vector3 point, Vector3 origin, Vector3 u, Vector3 v)
    {
        double x = (double)point.X - origin.X, y = (double)point.Y - origin.Y, z = (double)point.Z - origin.Z;
        return new(x * u.X + y * u.Y + z * u.Z, x * v.X + y * v.Y + z * v.Z);
    }
    static double Cross(Point a, Point b) => a.X * b.Y - a.Y * b.X;
    static double Distance(Point point, Point a, Point b)
    {
        var ab = b - a; var ap = point - a;
        double t = ab.Squared == 0 ? 0 : Math.Clamp((ap.X * ab.X + ap.Y * ab.Y) / ab.Squared, 0, 1);
        double x = ap.X - t * ab.X, y = ap.Y - t * ab.Y;
        return Math.Sqrt(x * x + y * y);
    }
    static (int, int) Key(int a, int b) => a < b ? (a, b) : (b, a);
    static bool Finite(Vector3 point) => float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z);
}
