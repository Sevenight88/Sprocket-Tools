namespace SprocketTools;

/// The orthographic camera can stand beyond the game's 100 m gizmo-picking limit.
public static class GizmoPicking
{
    public static float Reach(float requested, float distanceToCentre, float radius, bool orthographic)
    {
        if (!orthographic || !float.IsFinite(distanceToCentre) || !float.IsFinite(radius)) return requested;
        return Math.Max(requested, distanceToCentre + Math.Max(0, radius) + 1);
    }
}
