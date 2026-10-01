using System.Numerics;

namespace SprocketTools;

/// Carries edge settings through bevel's shortened sides, independently of face creation order.
public static class BevelEdges
{
    public static Dictionary<(int, int), (int, int)> Sources(IReadOnlyList<Vector3> positions,
        IReadOnlyList<int[]> faces, MeshPlans.Rebuild plan, ISet<(int,int)>? roundedEdges = null)
    {
        var original = faces.SelectMany(Sides).ToHashSet();
        var result = new Dictionary<(int, int), (int, int)>();
        IEnumerable<int> Origins(int v) => v < positions.Count ? new[] { v }
            : plan.Points[v - positions.Count].Blend.Where(b => b.W > 0).Select(b => b.V);
        Vector3 At(int v) => v < positions.Count ? positions[v] : plan.Points[v - positions.Count].P;
        bool OnSide(int v, (int A, int B) edge)
        {
            var start = positions[edge.A]; var delta = positions[edge.B] - start;
            float length2 = delta.LengthSquared();
            if (length2 < 1e-16f) return false;
            float t = Vector3.Dot(At(v) - start, delta) / length2;
            return t >= -1e-5f && t <= 1.00001f &&
                Vector3.DistanceSquared(At(v), start + t * delta) <= Math.Max(1e-12f, length2 * 1e-10f);
        }
        foreach (var edge in plan.Add.SelectMany(f => Sides(f.Corners)).Distinct())
        {
            if (original.Contains(edge)) { result[edge] = edge; continue; }
            var from = Origins(edge.Item1).Concat(Origins(edge.Item2)).Distinct().ToArray();
            if (from.Length == 2)
            {
                var source = FaceMerge.Key(from[0], from[1]);
                if (original.Contains(source) && roundedEdges?.Contains(source) != true) result[edge] = source;
            }
            else if (from.Length == 1)
            {
                // A bevel ending inside a face fan keeps the old corner and inserts a point on
                // its side. Both have the same origin, so identify the shortened side geometrically.
                var candidates = original.Where(e => (e.Item1 == from[0] || e.Item2 == from[0]) &&
                    OnSide(edge.Item1, e) && OnSide(edge.Item2, e)).Take(2).ToArray();
                if (candidates.Length == 1) result[edge] = candidates[0];
            }
        }
        return result;
    }

    static IEnumerable<(int, int)> Sides(int[] face) =>
        face.Select((v, i) => FaceMerge.Key(v, face[(i + 1) % face.Length]));
}
