using System.Numerics;

namespace SprocketTools;

/// Compare geometry instead of retaining live Face pointers that become stale after undo/redo.
public static class SplitFaceSelection
{
    public static bool Matches(IReadOnlyList<Vector3> face, IEnumerable<Vector3[]> targets) => targets.Any(target =>
        face.Count == target.Length && face.Count > 0 && Enumerable.Range(0, face.Count).Any(start =>
            new[] { 1, -1 }.Any(direction => Enumerable.Range(0, face.Count).All(i =>
                Vector3.DistanceSquared(face[i], target[(start + direction*i + face.Count)%face.Count]) <= 1e-12f))));
}
