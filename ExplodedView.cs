using HarmonyLib;
using Sprocket.Vehicles;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SprocketTools;

/// Exploded view (F2): every part moves away from the part it's on by the spread (F3 / F4: closer / further apart),
/// straight up or down when it sits above or below it, otherwise outward on the level; parts on parts go further. Only
/// what's drawn moves, never the design: around every save or read of the design the parts go back first. F2 again
/// puts everything back.
[HarmonyPatch]
public static class ExplodedView
{
    // Each moved part: its drawn transform and its position in its parent's space before and after (to tell if the
    // game has since put it back itself, e.g. after you moved that part). Parent space, so the order doesn't matter.
    static readonly List<(Transform T, Vector3 Before, Vector3 After)> moved = new();
    static bool on;

    internal static void Keys()
    {
        var keys = Keyboard.current;
        if (keys == null) return;
        if (on && moved.Any(m => Gone(m.T))) { moved.Clear(); Explode(); } // the design was reloaded: new parts
        if (keys.f2Key.wasPressedThisFrame)
        {
            on = !on;
            if (on) Explode(); else Collapse();
            DesignEditor.Instance?.Say(on ? $"爆炸视图：部件间距 {Spread:0.00} 米（F3 拉近 / F4 推远，F2 复位）" : "爆炸视图关闭", 4);
        }
        if (keys.f3Key.wasPressedThisFrame || keys.f4Key.wasPressedThisFrame)
        {
            if (Plugin.ExplodeSpread != null) Plugin.ExplodeSpread.Value = Math.Clamp(Spread * (keys.f4Key.wasPressedThisFrame ? 1.25f : 0.8f), 0.05f, 20);
            if (on) { Collapse(); Explode(); }
            DesignEditor.Instance?.Say($"爆炸视图间距：{Spread:0.00} 米", 3);
        }
    }

    /// The editor closed: parts that still exist go back, and the view is off next time.
    internal static void LeftEditor()
    {
        if (on) Collapse();
        on = false;
    }

    static float Spread => Plugin.ExplodeSpread?.Value ?? 0.5f;

    static bool Gone(Transform t)
    {
        try { return t == null || t.gameObject == null; }
        catch { return true; }
    }

    static void Explode()
    {
        Ui.Guard("Exploded view", () =>
        {
            moved.Clear();
            var parts = DesignEditor.Instance?.AllParts().ToList() ?? new();
            var total = new Dictionary<IntPtr, Vector3>();
            VehicleObject? ParentOf(VehicleObject p) => p.GetComponent<VehicleTransform>()?.Parent?.VehicleObject;
            // Running gear (tracks, road wheels, sprockets, idlers, suspension) and anything on it stays on the ground.
            bool Grounded(VehicleObject? p)
            {
                for (int guard = 0; p != null && guard < 64; guard++, p = ParentOf(p))
                {
                    var components = p.Components;
                    int count = components?.Cast<Il2CppSystem.Collections.Generic.IReadOnlyCollection<VehicleComponent>>().Count ?? 0;
                    for (int i = 0; i < count; i++)
                        if (components![i]?.GetIl2CppType().Namespace == "Sprocket.Vehicles.Tracks") return true;
                }
                return false;
            }
            bool anyGrounded = parts.Any(Grounded);
            // How far a part moves in all: its parent's move plus its own step away from the parent. The body the
            // running gear hangs on lifts off it by one step.
            Vector3 Total(VehicleObject p)
            {
                if (total.TryGetValue(p.Pointer, out var known)) return known;
                total[p.Pointer] = Vector3.zero; // guards against a loop
                if (Grounded(p)) return Vector3.zero;
                var parent = ParentOf(p);
                if (parent == null) return total[p.Pointer] = anyGrounded ? Vector3.up * Spread : Vector3.zero;
                var d = p.transform.position - parent.transform.position;
                var dir = d.sqrMagnitude < 1e-6f ? Vector3.up
                    : Mathf.Abs(d.normalized.y) > 0.7f ? new Vector3(0, Mathf.Sign(d.y), 0)
                    : new Vector3(d.x, 0, d.z).normalized;
                return total[p.Pointer] = Total(parent) + dir * Spread;
            }
            foreach (var p in parts) Total(p); // every direction worked out before anything moves
            foreach (var p in parts)
            {
                var parent = ParentOf(p);
                // If Unity already carries the part along with its parent, only its own step is added.
                var step = parent != null && p.transform.IsChildOf(parent.transform) ? total[p.Pointer] - total[parent.Pointer] : total[p.Pointer];
                if (step.sqrMagnitude < 1e-10f) continue;
                var before = p.transform.localPosition;
                p.transform.position += step;
                moved.Add((p.transform, before, p.transform.localPosition));
            }
            Plugin.ModLog.LogInfo($"Exploded view: {moved.Count} of {parts.Count} parts moved {Spread:0.00} m apart, {parts.Count(Grounded)} running gear parts stay on the ground");
        });
    }

    static void Collapse()
    {
        Ui.Guard("Exploded view", () =>
        {
            // A part the game has already put back (it's no longer where we left it) is left alone.
            foreach (var (t, before, after) in moved)
                if (!Gone(t) && (t.localPosition - after).sqrMagnitude < 1e-8f) t.localPosition = before;
            moved.Clear();
        });
    }

    // The design is read from the parts (saving, the mod's own edits): the parts go back first, and apart again after.
    [HarmonyPrefix, HarmonyPatch(typeof(VehicleBlueprintSerializer), nameof(VehicleBlueprintSerializer.ToBlueprint))]
    static void BeforeRead() { if (on) Collapse(); }

    [HarmonyPostfix, HarmonyPatch(typeof(VehicleBlueprintSerializer), nameof(VehicleBlueprintSerializer.ToBlueprint))]
    static void AfterRead() { if (on) Explode(); }
}
