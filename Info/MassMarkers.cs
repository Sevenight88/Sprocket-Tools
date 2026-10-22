using HarmonyLib;
using Sprocket.VehicleDesigner.Access;
using Sprocket.VehicleDesigner.Gizmos;
using Sprocket.Vehicles;

namespace SprocketTools;

/// The overlay's COM checkbox and the per-part COM drawer have separate native switches. This applies the part-marker
/// preference underneath the active overlay's master switch before the native drawer rebuilds its cached gizmos.
/// Selection outlines and other gizmos still draw.
[HarmonyPatch]
public static class MassMarkers
{
    static readonly Dictionary<IntPtr, bool> drawn = new();
    static bool settingNative;
    static bool fallbackPreference = true;

    internal static bool PartMarkersShown => Plugin.PartMassMarkers?.Value ?? fallbackPreference;

    internal static bool MasterShown => DesignEditor.Instance?.Core?.Editor?.Context?.Target?.OverlayApplier?.Current?.DrawCentreOfMass ?? false;

    internal static void SetPartMarkersShown(bool shown) => Ui.Guard("Part mass markers", () =>
    {
        bool changed = PartMarkersShown != shown;
        if (Plugin.PartMassMarkers is { } preference) preference.Value = shown;
        else fallbackPreference = shown;
        if (changed) DesignEditor.Instance?.Core?.Editor?.RequestRedraw(RedrawType.Gizmos);
    });

    // OverlayInstanceConfig.SetCentreOfMassShown writes ShowCOM directly, bypassing the VehicleOverlay property. Reading
    // the active overlay also covers switching overlays, direct flag changes and editor loads, without depending on one
    // checkbox callback.
    [HarmonyPrefix, HarmonyPatch(typeof(VehicleEditorGizmoDrawer), nameof(VehicleEditorGizmoDrawer.LateUpdate))]
    static void BeforeDraw(VehicleEditorGizmoDrawer __instance) => Ui.Guard("Part mass markers", () =>
    {
        var overlay = __instance.editorContext?.Target?.OverlayApplier?.Current;
        if (overlay == null) return;
        bool visible = overlay.DrawCentreOfMass && PartMarkersShown;
        bool nativeChanged = VehicleComponentGizmoDrawing.DrawLocalCentreOfMassGizmos != visible;
        if (nativeChanged) SetNative(visible);
        // A COM change must invalidate the cached meshes even when the camera is still. The native local-COM switch is
        // global, so each native drawer keeps its own record.
        if (nativeChanged || !drawn.TryGetValue(__instance.Pointer, out bool wasVisible) || wasVisible != visible)
            __instance.forceRedraw = true;
        drawn[__instance.Pointer] = visible;
    });

    // Callers of the native local-COM option change this mod's part preference instead; the master gate writes the same
    // native property without overwriting that preference.
    [HarmonyPostfix, HarmonyPatch(typeof(VehicleComponentGizmoDrawing), nameof(VehicleComponentGizmoDrawing.DrawLocalCentreOfMassGizmos), MethodType.Setter)]
    static void LocalPropertyChanged(bool value)
    {
        if (!settingNative) SetPartMarkersShown(value);
    }

    [HarmonyPostfix, HarmonyPatch(typeof(VehicleComponentGizmoDrawing), nameof(VehicleComponentGizmoDrawing.ToggleLocalComGizmoDraw))]
    static void LocalToggleChanged(bool newState)
    {
        if (!settingNative) SetPartMarkersShown(newState);
    }

    [HarmonyPrefix, HarmonyPatch(typeof(VehicleEditorGizmoDrawer), nameof(VehicleEditorGizmoDrawer.Release))]
    static void Release(VehicleEditorGizmoDrawer __instance) => Ui.Guard("Part mass markers", () =>
    {
        drawn.Remove(__instance.Pointer);
        // The overlay's gate belongs to this editor; once every drawer has finished, leave the native local preference
        // available to its own uses again.
        if (drawn.Count == 0) SetNative(PartMarkersShown);
    });

    static void SetNative(bool shown)
    {
        settingNative = true;
        try { VehicleComponentGizmoDrawing.DrawLocalCentreOfMassGizmos = shown; }
        finally { settingNative = false; }
    }
}
