using UnityEngine;
using UnityEngine.InputSystem;

namespace SprocketTools;

/// While the mouse is over one of QoL's own boxes (the QoL panel, the Shortcuts box), the game's mouse controls are
/// off: a click, a drag or the wheel there is QoL's, not a camera turn, zoom or selection in the editor.
internal static class InputShield
{
    static readonly List<InputAction> off = new();
    static int claimedFrame = -1;

    /// The mouse is over a QoL box this frame.
    internal static void Claim() => claimedFrame = Time.frameCount;

    /// Once a frame (late): the game's mouse controls off while claimed, back on once not.
    internal static void Update()
    {
        if (claimedFrame >= Time.frameCount - 1)
        {
            var on = InputSystem.ListEnabledActions();
            for (int i = 0; i < on.Count; i++)
                if (on[i]?.actionMap?.name is not "UI" && UsesMouse(on[i])) { on[i].Disable(); off.Add(on[i]); }
        }
        else Release();
    }

    internal static void Release()
    {
        foreach (var a in off) try { a?.Enable(); } catch (Exception) { }
        off.Clear();
    }

    static bool UsesMouse(InputAction action)
    {
        try
        {
            var bindings = action.bindings;
            for (int i = 0; i < bindings.Count; i++)
                if (bindings[i].effectivePath is { } path && path.Contains("Mouse", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        catch (Exception) { return true; } // can't tell: off while over the box
    }

    /// A box dragged by its title: where it is now, given where the drag started (screen pixels, from the top left).
    internal sealed class Drag
    {
        Vector2? grab;
        internal bool Active => grab != null;

        /// Call each frame with the box's title area (screen pixels from the top left) and its position; returns the
        /// new position while dragged, else null. `done` gets the final position when the button is let go.
        internal Vector2? Update(Rect title, Vector2 at, Action<Vector2> done)
        {
            if (Mouse.current is not { } mouse) { grab = null; return null; }
            var p = mouse.position.ReadValue();
            var gui = new Vector2(p.x, Screen.height - p.y);
            if (grab == null && mouse.leftButton.wasPressedThisFrame && title.Contains(gui)) grab = gui - at;
            if (grab is not { } g) return null;
            var to = gui - g;
            if (!mouse.leftButton.isPressed) { grab = null; done(to); }
            return to;
        }
    }

    /// "x,y" from the config, or null for the automatic place.
    internal static Vector2? Read(string? saved)
    {
        if (string.IsNullOrWhiteSpace(saved)) return null;
        var parts = saved.Split(',');
        return parts.Length == 2 && float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x)
               && float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y)
            ? new Vector2(x, y) : null;
    }

    internal static string Write(Vector2 at) => FormattableString.Invariant($"{MathF.Round(at.x)},{MathF.Round(at.y)}");
}
