using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SprocketTools;

public record ObjBlueprintImportResult(string Json, int ObjectCount, int VertexCount, int FaceCount, int SkippedFaces = 0,
    int FacesBeforeMerge = 0, string? MergeWhy = null);

/// Converts an OBJ to the game's saved Plate Structures format without touching a live vehicle or a file.
/// OBJ coordinates are metres, right-handed and Y-up; reflection and winding reversal match the OBJ exporter.
public static class ObjBlueprintImport
{
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    sealed record Prepared(string Name, Vector3[] Vertices, int[][] Faces, double Area);

    /// Builds one editable plate structure from all OBJ objects in their shared coordinate frame.
    /// Different objects stay disconnected even where their points coincide. No object is recentered.
    /// The result is the bare PlateStructureMeshBlueprint JSON expected by a .blueprint file in
    /// Factions/<faction>/Blueprints/Plate Structures, not a vehicle blueprint or a live-tank edit.
    public static ObjBlueprintImportResult Build(ObjDocument document, string name,
        float scale = 1f, float armourMm = 10f, float weldTolerance = 0.000001f, bool merge = true)
    {
        var (parts, skipped, mergedFrom, why) = Prepare(document, scale, armourMm, weldTolerance, merge);
        var vertices = new List<Vector3>();
        var faces = new List<int[]>();
        foreach (var part in parts)
        {
            int offset = vertices.Count;
            vertices.AddRange(part.Vertices);
            faces.AddRange(part.Faces.Select(face => face.Select(index => checked(index + offset)).ToArray()));
        }
        string blueprintName = string.IsNullOrWhiteSpace(name) ? "导入的 OBJ" : name.Trim();
        var data = Mesh(blueprintName, vertices.ToArray(), faces.ToArray(), armourMm);
        return new(data.ToJsonString(Indented), parts.Length, vertices.Count, faces.Count, skipped, mergedFrom, why);
    }

    static (Prepared[] Parts, int Skipped, int MergedFrom, string? MergeWhy) Prepare(
        ObjDocument document, float scale, float armourMm, float weldTolerance, bool merge)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!float.IsFinite(scale) || scale <= 0 || scale > 1000)
            throw new ArgumentOutOfRangeException(nameof(scale), "OBJ 缩放要大于 0、不超过 1000。");
        if (!float.IsFinite(armourMm) || armourMm < 1 || armourMm > 1000 || armourMm != MathF.Truncate(armourMm))
            throw new ArgumentOutOfRangeException(nameof(armourMm), "板厚要是 1 到 1000 毫米之间的整数。");
        if (!float.IsFinite(weldTolerance) || weldTolerance < 0 || weldTolerance > 0.001f || weldTolerance > 0 && weldTolerance < 1e-9f)
            throw new ArgumentOutOfRangeException(nameof(weldTolerance), "顶点焊接容差要么为 0（只合并完全重合的点），要么在 0.000000001 到 0.001 米之间。");
        if (document.Meshes == null || document.Meshes.Length == 0 || document.Meshes.Length > ObjMeshFormat.MaxMeshes)
            throw new FormatException("这个 OBJ 没有可用的对象，或者对象太多。");
        long totalVertices = 0, totalFaces = 0, totalCorners = 0;
        int skipped = 0, mergedFrom = 0;
        string? why = null;
        var result = new List<Prepared>();
        for (int m = 0; m < document.Meshes.Length; m++)
        {
            var mesh = document.Meshes[m];
            string label = string.IsNullOrWhiteSpace(mesh?.Name) ? $"OBJ 对象 {m + 1}" : mesh.Name.Trim();
            if (mesh?.Vertices == null || mesh.Faces == null || mesh.Vertices.Length < 3 || mesh.Faces.Length == 0)
                throw new FormatException($"{label}：可编辑的对象既要有顶点也要有面。");
            totalVertices += mesh.Vertices.Length;
            if (totalVertices > ObjMeshFormat.MaxVertices) throw new FormatException("这个 OBJ 的顶点太多。");
            var sourceVertices = mesh.Vertices.Select(v => new Vector3(-v.X, v.Y, v.Z) * scale).ToArray();
            if (sourceVertices.Any(v => !Finite(v) || Maximum(v) > ObjMeshFormat.MaxCoordinate))
                throw new FormatException($"{label}：顶点必须是有限数，且落在支持的坐标范围内。");
            // Render meshes split a geometric vertex at hard normals and UV seams. Joining these copies within
            // one OBJ object restores an editable shell. Separate OBJ objects are never welded together.
            var (vertices, vertexMap) = Weld(sourceVertices, weldTolerance);
            var faces = new List<int[]>();
            foreach (var face in mesh.Faces)
            {
                if (face == null || face.Length < 3 || face.Length > ObjMeshFormat.MaxFaceVertices ||
                    face.Any(i => i < 0 || i >= sourceVertices.Length))
                    throw new FormatException($"{label}：有一张面的顶点索引无效。");
                var reversed = face.Reverse().Select(i => vertexMap[i]).ToArray();
                // Real models carry faces that draw nothing: repeated or welded corners, slivers, outlines that cross
                // themselves. They're left out (and counted) rather than failing the whole import.
                var natives = reversed.Distinct().Count() == reversed.Length ? NativeFaces(vertices, reversed) : null;
                if (natives == null) { skipped++; continue; }
                foreach (var native in natives)
                {
                    totalFaces++;
                    totalCorners += native.Length;
                    if (totalFaces > ObjMeshFormat.MaxFaces || totalCorners > ObjMeshFormat.MaxCorners)
                        throw new FormatException("这个 OBJ 的可编辑面太多。");
                    faces.Add(native);
                }
            }
            // A render mesh bakes every flat plate into triangles; joining them back leaves an editable shell of as
            // few faces as its outlines allow. One OBJ object at a time, since objects are never welded together.
            if (merge && faces.Count > 1)
            {
                var weld = ObjWeld.Merge(vertices, faces);
                mergedFrom += faces.Count;
                faces = weld.Faces.ToList();
                why ??= weld.Why;
            }
            double area = Area(vertices, faces);
            if (faces.Count == 0 || !double.IsFinite(area) || area <= 0) continue; // nothing left to draw
            // Only the vertices the kept faces use, in their original order.
            var used = new bool[vertices.Length];
            foreach (var f in faces) foreach (int i in f) used[i] = true;
            var remap = new int[vertices.Length];
            var kept = new List<Vector3>();
            for (int i = 0; i < vertices.Length; i++) if (used[i]) { remap[i] = kept.Count; kept.Add(vertices[i]); }
            result.Add(new(label, kept.ToArray(), faces.Select(f => f.Select(i => remap[i]).ToArray()).ToArray(), area));
        }
        if (result.Count == 0) throw new FormatException("这个 OBJ 没有面积为正的面可用。");
        return (result.ToArray(), skipped, mergedFrom, why);
    }

    static double Area(Vector3[] vertices, List<int[]> faces) =>
        faces.Sum(face => Enumerable.Range(1, face.Length - 2)
            .Sum(k => Vector3.Cross(vertices[face[k]] - vertices[face[0]], vertices[face[k + 1]] - vertices[face[0]]).Length() / 2.0));

    static (Vector3[] Vertices, int[] Map) Weld(Vector3[] source, float tolerance)
    {
        var vertices = new List<Vector3>();
        var map = new int[source.Length];
        var exact = new Dictionary<Vector3, int>();
        var buckets = new Dictionary<(long X, long Y, long Z), List<int>>();
        double squared = (double)tolerance * tolerance;
        for (int i = 0; i < source.Length; i++)
        {
            var point = source[i];
            if (exact.TryGetValue(point, out int match)) { map[i] = match; continue; }
            var key = tolerance == 0 ? (0L, 0L, 0L) : ((long)Math.Floor(point.X / (double)tolerance), (long)Math.Floor(point.Y / (double)tolerance), (long)Math.Floor(point.Z / (double)tolerance));
            match = -1;
            if (tolerance > 0)
                for (int x = -1; x <= 1 && match < 0; x++)
                    for (int y = -1; y <= 1 && match < 0; y++)
                        for (int z = -1; z <= 1 && match < 0; z++)
                            if (buckets.TryGetValue((key.Item1 + x, key.Item2 + y, key.Item3 + z), out var near))
                                foreach (int candidate in near)
                                    if (Vector3.DistanceSquared(point, vertices[candidate]) <= squared) { match = candidate; break; }
            if (match < 0)
            {
                match = vertices.Count; vertices.Add(point);
                if (tolerance > 0)
                {
                    if (!buckets.TryGetValue(key, out var bucket)) buckets.Add(key, bucket = new List<int>());
                    bucket.Add(match);
                }
            }
            exact.Add(point, match); map[i] = match;
        }
        return (vertices.ToArray(), map);
    }

    // Sprocket stores thickness modes in four bytes, so larger OBJ polygons must become triangles.
    // Ear clipping also handles concave faces; a fan can introduce faces outside the original polygon.
    // Null: the face has no usable area (degenerate, a sliver, or an outline that crosses or touches itself).
    static int[][]? NativeFaces(Vector3[] vertices, int[] face)
    {
        Vector3 origin = vertices[face[0]], normal = Vector3.Zero;
        for (int i = 1; i + 1 < face.Length; i++)
            normal += Vector3.Cross(vertices[face[i]] - origin, vertices[face[i + 1]] - origin);
        if (!Finite(normal) || normal.LengthSquared() < 1e-20f) return null;
        var abs = Vector3.Abs(normal);
        int axis = abs.X >= abs.Y && abs.X >= abs.Z ? 0 : abs.Y >= abs.Z ? 1 : 2;
        var points = face.Select(i => vertices[i] - origin).Select(p => axis == 0 ? new Vector2(p.Y, p.Z) : axis == 1 ? new Vector2(p.Z, p.X) : new Vector2(p.X, p.Y)).ToArray();
        double Cross(Vector2 a, Vector2 b, Vector2 c) => (double)(b.X - a.X) * (c.Y - a.Y) - (double)(b.Y - a.Y) * (c.X - a.X);
        double signed = 0;
        for (int i = 0; i < points.Length; i++) signed += (double)points[i].X * points[(i + 1) % points.Length].Y - (double)points[i].Y * points[(i + 1) % points.Length].X;
        double span = points.Max(p => Math.Max(Math.Abs(p.X), Math.Abs(p.Y)));
        double epsilon = Math.Max(1e-16, span * span * 1e-10);
        double distanceEpsilon = Math.Max(1e-12, span * 1e-10);
        double turn = Math.Sign(signed);
        if (Math.Abs(signed) <= epsilon) return null;
        bool OnSegment(Vector2 p, Vector2 a, Vector2 b) => Math.Abs(Cross(a, b, p)) <= epsilon &&
            p.X >= Math.Min(a.X, b.X) - distanceEpsilon && p.X <= Math.Max(a.X, b.X) + distanceEpsilon &&
            p.Y >= Math.Min(a.Y, b.Y) - distanceEpsilon && p.Y <= Math.Max(a.Y, b.Y) + distanceEpsilon;
        for (int a = 0; a < points.Length; a++)
        {
            int b = (a + 1) % points.Length;
            for (int c = a + 1; c < points.Length; c++)
            {
                int d = (c + 1) % points.Length;
                if (b == c || d == a) continue;
                if (Cross(points[a], points[b], points[c]) * Cross(points[a], points[b], points[d]) < -epsilon * epsilon &&
                    Cross(points[c], points[d], points[a]) * Cross(points[c], points[d], points[b]) < -epsilon * epsilon ||
                    OnSegment(points[c], points[a], points[b]) || OnSegment(points[d], points[a], points[b]) ||
                    OnSegment(points[a], points[c], points[d]) || OnSegment(points[b], points[c], points[d]))
                    return null;
            }
        }
        bool convex = Enumerable.Range(0, points.Length).All(i => turn * Cross(points[(i + points.Length - 1) % points.Length], points[i], points[(i + 1) % points.Length]) > epsilon);
        // A four-corner face stays whole as long as its outline is convex: the game's own authors leave quads bent
        // by up to a fifth of their diagonal, so planarity is not what makes a face editable here. Only the corner
        // count is, because the thickness modes occupy one byte per corner.
        if (face.Length <= 4 && convex) return new[] { face };
        var remaining = Enumerable.Range(0, face.Length).ToList();
        var output = new List<int[]>();
        while (remaining.Count > 3)
        {
            bool cut = false;
            for (int k = 0; k < remaining.Count; k++)
            {
                int a = remaining[(k + remaining.Count - 1) % remaining.Count], b = remaining[k], c = remaining[(k + 1) % remaining.Count];
                if (turn * Cross(points[a], points[b], points[c]) <= epsilon) continue;
                if (remaining.Any(p => p != a && p != b && p != c && turn * Cross(points[a], points[b], points[p]) >= -epsilon &&
                    turn * Cross(points[b], points[c], points[p]) >= -epsilon && turn * Cross(points[c], points[a], points[p]) >= -epsilon)) continue;
                output.Add(new[] { face[a], face[b], face[c] });
                remaining.RemoveAt(k); cut = true; break;
            }
            if (!cut) return null;
        }
        if (turn * Cross(points[remaining[0]], points[remaining[1]], points[remaining[2]]) <= epsilon) return null;
        output.Add(remaining.Select(i => face[i]).ToArray());
        return output.ToArray();
    }

    static JsonObject Mesh(string name, Vector3[] vertices, int[][] faces, float armourMm)
    {
        var edges = new List<int>();
        var seen = new HashSet<(int, int)>();
        foreach (var face in faces)
            for (int k = 0; k < face.Length; k++)
            {
                int a = face[k], b = face[(k + 1) % face.Length];
                if (seen.Add((Math.Min(a, b), Math.Max(a, b)))) { edges.Add(a); edges.Add(b); }
            }
        return new JsonObject
        {
            ["v"] = "0.2", ["name"] = name, ["smoothAngle"] = 0, ["gridSize"] = 1, ["format"] = "freeform",
            ["mesh"] = new JsonObject
            {
                ["majorVersion"] = 0, ["minorVersion"] = 3,
                ["vertices"] = new JsonArray(vertices.SelectMany(v => new[] { v.X, v.Y, v.Z }).Select(v => (JsonNode?)v).ToArray()),
                ["edges"] = new JsonArray(edges.Select(e => (JsonNode?)e).ToArray()),
                ["edgeFlags"] = new JsonArray(Enumerable.Range(0, edges.Count / 2).Select(_ => (JsonNode?)0).ToArray()),
                ["faces"] = new JsonArray(faces.Select(f => (JsonNode?)new JsonObject
                {
                    ["v"] = new JsonArray(f.Select(i => (JsonNode?)i).ToArray()),
                    ["t"] = new JsonArray(f.Select(_ => (JsonNode?)(int)armourMm).ToArray()),
                    // Native thickness modes occupy one byte per corner; the JSON reader expects an unsigned
                    // thicken-edge value. Its unset state is zero, not an in-memory sentinel or signed -1.
                    ["tm"] = f.Length == 4 ? 0x01010101 : 0x010101, ["te"] = 0,
                }).ToArray()),
            },
            ["rivets"] = new JsonObject
            {
                ["profiles"] = new JsonArray(new JsonObject { ["model"] = 0, ["spacing"] = 0.1, ["diameter"] = 0.05, ["height"] = 0.025, ["padding"] = 0.04 }),
                ["nodes"] = new JsonArray(),
            },
        };
    }

    static float Maximum(Vector3 v) => Math.Max(Math.Abs(v.X), Math.Max(Math.Abs(v.Y), Math.Abs(v.Z)));
    static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
