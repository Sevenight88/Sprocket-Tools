using HarmonyLib;
using Sprocket.UI;
using Sprocket.Vehicles.Cannons;
using Sprocket.Vehicles.Weapons;
using Sprocket.WeaponFramework;
using System.Collections.Generic;
using System.Linq;

namespace SprocketTools;

/// 火炮稳定器的开关，画在设计器「瞄准驱动」那一格面板里（就是显示 已移动质量／俯仰角／方位角／极速／
/// 至极速时间／比率 的那格，绘制者是游戏的 `LayingDriveEditor.OnGUI`，dump.cs:871000）。
///
/// 两个勾选框写进这套驱动所服务的那门炮的 `CannonInstanceBlueprint.Flags`（bit2=垂稳、bit4=双稳）：
/// 这一格随设计一起存盘（`CannonInstanceBlueprint` 的 Save/Load 认识 "flags"），战斗侧 `Stabilizer`
/// 直读同一个字段，所以不需要 mod 自己的旁路文件——`objects[].guid` 是部件**类型** id（同型炮共用一个），
/// VUID 每次 Build 重编号，都当不了稳定器要的那个「就这一门炮」的键。
///
/// 找炮走游戏自己的装配关系：`LayingDrive.Trunnions` 就是这门炮所在的摇架件（图纸标注那条已经在这条路上
/// 跑通过：`DrawingSheet.ReadGunMotion`），炮挂在它下面。同轴两门炮挂同一个摇架时，两个勾会一起写给它们。
public static class StabilizerPanel
{
    [HarmonyPostfix, HarmonyPatch(typeof(LayingDriveEditor), nameof(LayingDriveEditor.OnGUI))]
    static void Draw(LayingDriveEditor __instance, IGUILayout layout) => Ui.Inspector("Stabilizer panel", layout, () =>
    {
        var ui = layout.TryCast<IGUIElementDrawer>();
        var drive = __instance.Component;
        if (ui == null || drive == null) return;
        Ui.Section(layout, "火炮稳定器");

        var guns = LinkedGuns(drive);
        if (guns.Count == 0)
        {
            ui.InfoField("没找到这套瞄准驱动服务的火炮（摇架下没有炮）。装好炮再回来勾。\n" +
                         "稳定器是逐炮设置的：勾上后随设计存盘，战斗中生效。", 3);
            return;
        }

        bool vertical = guns.All(g => Has(g, Stabilizer.VerticalBit));
        bool full = guns.All(g => Has(g, Stabilizer.FullBit));

        ui.ToggleField("垂直稳定器", vertical, Ui.BoolCallback(on => Write(__instance, guns, Stabilizer.VerticalBit, on, 0, Stabilizer.FullBit)),
            "稳定俯仰面：车体上下俯仰时，火炮自动反向补偿，瞄准线保持在世界坐标里的同一条线上。方位（左右）仍随车体转动，" +
            "与真实单稳一致。勾完随这张设计图存盘，战斗中生效，逐门炮各记各的。");
        ui.ToggleField("双向稳定器", full, Ui.BoolCallback(on => Write(__instance, guns, Stabilizer.FullBit, on, Stabilizer.VerticalBit, 0)),
            "在垂直稳定之上再稳定方位面：车体转向时炮塔反向补偿，整条瞄准线钉在世界坐标里。" +
            "勾它会自动把「垂直稳定器」一起勾上；关掉它不影响垂直稳定。真实车型要到 1950 年代以后才有这套，游戏里不设年代门槛，自己取舍。");

        if (guns.Count > 1)
            ui.InfoField($"这套驱动下面挂着 {guns.Count} 门炮（同轴／共摇架），上面的勾会一起写到它们身上。", 2);

        // 机制状态行（4L）：挂点齐不齐、游戏版本对不对、当前旋钮是多少。设计器里 `接管 N 条驱动` 恒为 0，
        // 那个计数只有进战斗才涨——所以要带一句，免得看成「没生效」。
        ui.InfoField("机制状态（设计器里「接管 0 条」是正常的，进战斗才涨）：" + Stabilizer.MechanismStatus(), 4);
    });

    static bool Has(Cannon gun, byte bit)
    {
        try { return gun.InstanceBlueprint != null && (unchecked((byte)gun.InstanceBlueprint.Flags) & bit) != 0; }
        catch (System.Exception) { return false; }
    }

    /// 置位／清位。`alsoOn` 是开这条时顺带要开的位（双稳带垂稳），`alsoOff` 是关这条时顺带要关的位
    /// （关垂稳就不该留着一个只剩方位的双稳）。写完立刻标脏，游戏才把它写进存档。
    static void Write(LayingDriveEditor editor, List<Cannon> guns, byte bit, bool on, byte alsoOn, byte alsoOff)
    {
        foreach (var gun in guns)
        {
            try
            {
                var bp = gun.InstanceBlueprint;
                if (bp == null) continue;
                byte raw = unchecked((byte)bp.Flags);
                raw = on ? (byte)(raw | bit | alsoOn) : (byte)(raw & ~bit & ~alsoOff);
                bp.Flags = (WeaponFlags)raw;
                bp.MarkModified();
            }
            catch (System.Exception ex) { Plugin.ModLog.LogWarning($"TOOL_STAB panel write: {ex.Message}"); }
        }
        editor.RequestRedraw();   // 不喊重绘，勾选框文字会停在上一项
        if (Stabilizer.Log?.Value == true)
            Plugin.ModLog.LogInfo($"TOOL_STAB 面板改位 {bit}={(on ? "开" : "关")} 炮数={guns.Count} 改后=" +
                string.Join(",", guns.Select(g => { try { return unchecked((byte)g.InstanceBlueprint.Flags).ToString(); } catch (System.Exception) { return "?"; } })));
    }

    /// 这套驱动服务的火炮：先按游戏自己的装配关系（摇架件往下找），找不到再沿父链往上找最近一个带炮的父件。
    static List<Cannon> LinkedGuns(LayingDrive drive)
    {
        var result = new List<Cannon>();
        try
        {
            var trunnions = drive.Trunnions?.VehicleObject;
            if (trunnions != null) result.AddRange(GunsUnder(trunnions));
        }
        catch (System.Exception ex) { Plugin.ModLog.LogWarning($"TOOL_STAB panel 摇架找炮失败：{ex.Message}"); }
        if (result.Count > 0) return Distinct(result);

        // 退路：有些炮座没有独立摇架件（车体炮／炮廓），沿父件往上找到第一个装炮的父件为止。
        try
        {
            var parent = drive.VehicleObject?.VehicleTransform?.Parent;
            for (int depth = 0; parent?.VehicleObject != null && depth < 8; depth++)
            {
                var found = GunsUnder(parent.VehicleObject);
                if (found.Count > 0) { result.AddRange(found); break; }
                parent = parent.Parent;
            }
        }
        catch (System.Exception ex) { Plugin.ModLog.LogWarning($"TOOL_STAB panel 父链找炮失败：{ex.Message}"); }
        return Distinct(result);
    }

    static List<Cannon> GunsUnder(Sprocket.Vehicles.VehicleObject root)
    {
        var list = new List<Cannon>();
        try { list.AddRange((root.GetComponentsInChildren<Cannon>(true) ?? System.Array.Empty<Cannon>()).Where(g => g != null)); }
        catch (System.Exception) { }
        return list;
    }

    static List<Cannon> Distinct(List<Cannon> guns) => guns.GroupBy(g => g.Pointer).Select(g => g.First()).ToList();
}
