using System.Numerics;

namespace SprocketTools;

/// Geometry shared by the native exporter and offline tests. Matrices use System.Numerics row vectors.
public static class ObjExportGeometry
{
    public readonly record struct ProjectedVertex(Vector3 Local, Vector3 World, Vector3 Normal);

    public static Matrix4x4 FromBasis(Vector3 origin, Vector3 x, Vector3 y, Vector3 z) =>
        new(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, origin.X, origin.Y, origin.Z, 1);

    public static Vector3 Reflect(Vector3 point) => new(-point.X, point.Y, point.Z);

    public static Vector3 TransformNormal(Vector3 normal, Matrix4x4 localToOutput)
    {
        return ApplyNormalMatrix(normal, NormalMatrix(localToOutput));
    }

    public static Matrix4x4 NormalMatrix(Matrix4x4 localToOutput)
    {
        if (!Matrix4x4.Invert(localToOutput, out var inverse))
            throw new InvalidOperationException("有一个部件的缩放是零，没法导出。");
        return Matrix4x4.Transpose(inverse);
    }

    public static Vector3 ApplyNormalMatrix(Vector3 normal, Matrix4x4 normalMatrix)
    {
        var transformed = Vector3.TransformNormal(normal, normalMatrix);
        float length = transformed.Length();
        return length > 1e-12f ? transformed / length : Vector3.Zero;
    }

    public static bool ReverseWinding(Matrix4x4 localToOutput) => localToOutput.GetDeterminant() < 0;

    public static bool HasSurface(IReadOnlyList<Vector3> points, IReadOnlyList<int> face)
    {
        for (int i = 1; i + 1 < face.Count; i++)
            if (Vector3.Cross(points[face[i]] - points[face[0]], points[face[i + 1]] - points[face[0]]).LengthSquared() > 0)
                return true;
        return false;
    }

    /// Clips receiver triangles against the HDRP unit projection cube, interpolating the actual surface.
    public static ProjectedVertex[] ClipToProjector(IReadOnlyList<ProjectedVertex> source)
    {
        var polygon = source.ToList();
        for (int axis = 0; axis < 3 && polygon.Count > 0; axis++)
            foreach (int sign in new[] { -1, 1 })
            {
                var output = new List<ProjectedVertex>();
                float Distance(ProjectedVertex p) => 0.5f - sign * (axis == 0 ? p.Local.X : axis == 1 ? p.Local.Y : p.Local.Z);
                for (int i = 0; i < polygon.Count; i++)
                {
                    var a = polygon[i]; var b = polygon[(i + 1) % polygon.Count];
                    float da = Distance(a), db = Distance(b);
                    bool insideA = da >= -1e-7f, insideB = db >= -1e-7f;
                    if (insideA) output.Add(a);
                    if (insideA != insideB)
                    {
                        float t = Math.Clamp(da / (da - db), 0, 1);
                        output.Add(new(Vector3.Lerp(a.Local, b.Local, t), Vector3.Lerp(a.World, b.World, t),
                            Vector3.Lerp(a.Normal, b.Normal, t)));
                    }
                }
                polygon = output;
            }
        // Plane intersections can duplicate a triangle vertex that already lies on a clipping plane.
        for (int i = polygon.Count - 1; i >= 0 && polygon.Count > 1; i--)
            if (Vector3.DistanceSquared(polygon[i].World, polygon[(i + 1) % polygon.Count].World) < 1e-14f)
                polygon.RemoveAt(i);
        return polygon.Count >= 3 ? polygon.ToArray() : Array.Empty<ProjectedVertex>();
    }

    /// Native vertex buffers may use packed or half precision attributes.
    public static float ReadAttribute(byte[] bytes, int offset, int format)
    {
        return format switch
        {
            0 => BitConverter.ToSingle(bytes, offset),           // Float32
            1 => (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(bytes, offset)), // Float16
            2 => bytes[offset] / 255f,                          // UNorm8
            3 => Math.Max(-1, unchecked((sbyte)bytes[offset]) / 127f), // SNorm8
            4 => BitConverter.ToUInt16(bytes, offset) / 65535f,  // UNorm16
            5 => Math.Max(-1, BitConverter.ToInt16(bytes, offset) / 32767f), // SNorm16
            6 => bytes[offset],                                // UInt8
            7 => unchecked((sbyte)bytes[offset]),               // SInt8
            8 => BitConverter.ToUInt16(bytes, offset),           // UInt16
            9 => BitConverter.ToInt16(bytes, offset),            // SInt16
            10 => BitConverter.ToUInt32(bytes, offset),          // UInt32
            11 => BitConverter.ToInt32(bytes, offset),           // SInt32
            _ => throw new NotSupportedException("这个网格用了不支持的顶点属性格式。")
        };
    }

    public static int AttributeBytes(int format) => format switch
    {
        0 or 10 or 11 => 4,
        1 or 4 or 5 or 8 or 9 => 2,
        2 or 3 or 6 or 7 => 1,
        _ => throw new NotSupportedException("这个网格用了不支持的顶点属性格式。")
    };
}
