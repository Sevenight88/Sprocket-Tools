using HarmonyLib;
using Sprocket.UI;
using Sprocket.Vehicles;
using Sprocket.Vehicles.PlateStructures.Design;
using Sprocket.Vehicles.Turrets.Editor;

namespace SprocketTools;

/// Turret to Add-on: a section in the game's own Turret Ring and Structure (turret body) panels.
/// Selecting the part is the game's normal selection, so the game highlights it.
[HarmonyPatch]
public static class InspectorSection
{
    [HarmonyPostfix, HarmonyPatch(typeof(TurretRingEditor), nameof(TurretRingEditor.OnGUI))]
    static void Ring(TurretRingEditor __instance, IGUILayout layout) => Ui.Inspector("Turret to Add-on", layout, () =>
        Draw(layout, (int)__instance.Component.VehicleObject.VUID));

    [HarmonyPostfix, HarmonyPatch(typeof(PlateStructureEditor), nameof(PlateStructureEditor.OnGUI))]
    static void Structure(PlateStructureEditor __instance, IGUILayout layout) => Ui.Guard("Turret to Add-on", () =>
    {
        var parent = __instance.Component.VehicleTransform?.Parent?.GetComponent<VehicleObject>();
        if (parent != null && parent.GUID == Conversion.RingGuid) Draw(layout, (int)parent.VUID);
    });

    static void Draw(IGUILayout layout, int ringVuid)
    {
        var editor = DesignEditor.Instance;
        var ui = layout.TryCast<IGUIElementDrawer>();
        if (editor == null || ui == null) return;
        // This turret plus any other turrets selected alongside it, converted together in one reload.
        var rings = editor.SelectedTurretRings().Prepend(ringVuid).Distinct().ToList();
        Ui.Section(layout, "炮塔转附加部件");
        ui.InfoField("将该炮塔变成固定的附加部件。\n火炮、乘员与附着部件都保持原位。", 2);
        var tip = new UITooltip("转换为附加部件",
            "移除炮塔环与方向机，把塔体保留为附加结构。" +
            "同时选中多个炮塔可一次性全部转换。结果会以新的未保存副本载入，转换前的设计已备份。");
        string label = rings.Count == 1 ? "转换为附加部件" : $"将{rings.Count}个选中炮塔转换为附加部件";
        ui.Button(label, Ui.Callback(() => editor.RequestEdit(rings.Count == 1 ? "正在将炮塔转换为附加部件" : $"正在将{rings.Count}个炮塔转换为附加部件",
            $"{(rings.Count == 1 ? "该炮塔" : $"{rings.Count} 个炮塔")}已转换为附加部件。请以新名称保存以保留。", json =>
            {
                var bodies = new List<int>();
                // A turret's mirror twin converts with it, so the pair stays alike.
                var objects = Conversion.Objects(Conversion.Parse(json));
                var all = rings.ToList();
                foreach (int ring in rings)
                    if (objects.TryGetValue(ring, out var r) && r["transform"]?["mirrorVuid"]?.GetValue<int>() is int t && !all.Contains(t)
                        && objects.TryGetValue(t, out var twin) && Conversion.GuidOf(twin) == Conversion.RingGuid && twin["transform"]?["mirrorVuid"]?.GetValue<int>() == ring)
                        all.Add(t);
                foreach (int ring in all)
                {
                    var r = Conversion.Convert(json, ring);
                    json = r.Json;
                    bodies.Add(r.BodyId);
                }
                return new EditResult(json, bodies[^1], $"rings={string.Join(",", all)} -> add-on bodies={string.Join(",", bodies)}");
            })), ref tip);
    }
}
