using HarmonyLib;
using Sprocket.UI;
using Sprocket.Vehicles.Turrets.Editor;
using VehicleDesigner.Compartments.Design;

namespace SprocketTools;

[HarmonyPatch]
public static class TurretDriveRepair
{
    [HarmonyPostfix, HarmonyPatch(typeof(TurretRingEditor), nameof(TurretRingEditor.OnGUI))]
    static void Ring(TurretRingEditor __instance, IGUILayout layout) => Ui.Inspector("Turret drive repair", layout, () =>
        Draw(layout, (int)__instance.Component.VehicleObject.VUID));

    [HarmonyPostfix, HarmonyPatch(typeof(TraverseMotorEditor), nameof(TraverseMotorEditor.OnGUI))]
    static void Motor(TraverseMotorEditor __instance, IGUILayout layout) => Ui.Inspector("Turret drive repair", layout, () =>
        Draw(layout, (int)__instance.Component.VehicleObject.VUID));

    static void Draw(IGUILayout layout, int focus)
    {
        var editor = DesignEditor.Instance;
        var ui = layout.TryCast<IGUIElementDrawer>();
        if (editor == null || ui == null) return;
        Ui.Section(layout, "镜像炮塔旋转驱动");
        ui.InfoField("修复共用一个旋转驱动的镜像对应体。\n每个炮塔会重新连接各自的方向机。", 2);
        var tip = new UITooltip("修复镜像炮塔旋转驱动", "检查载具的镜像炮塔对，并把每个炮塔重新连接到各自附着的方向机。" +
            "方向机缺失或有歧义的对应体对保持不动。结果会以未保存副本载入，原设计已备份；可用“恢复”撤销修复。");
        ui.Button("修复镜像炮塔旋转驱动", Ui.Callback(() => editor.RequestEdit("正在修复镜像炮塔旋转驱动",
            "已修复镜像炮塔旋转驱动。可用“恢复”撤销。", json =>
            {
                var result = Conversion.RepairMirroredTurretDrives(json);
                if (result.Repaired == 0) throw new Exception(result.Unresolved > 0
                    ? "未修复旋转驱动：有镜像对附着的方向机缺失或有歧义。"
                    : "镜像炮塔对上未发现交叉或共用的旋转驱动连接。");
                return new EditResult(result.Json, focus, $"{result.Repaired} ring drive references repaired; {result.Unresolved} ambiguous pairs left unchanged");
            })), ref tip);
        if (editor.CanRestore && editor.LastEditedPart == focus)
        {
            var restore = new UITooltip("恢复", "重新载入上一次旋转驱动修复之前的设计。");
            ui.Button("恢复到上次修改前的设计", Ui.Callback(editor.RequestRestore), ref restore);
        }
    }
}
