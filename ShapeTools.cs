using HarmonyLib;
using Sprocket.UI;
using Sprocket.Vehicles.PlateStructures.Design;

namespace SprocketTools;

/// Add-on panel: "Merge add-ons" folds the other selected add-ons into this one, and "Cut with this add-on" cuts the
/// add-on's shape out of the structure under it (a Boolean cut).
/// (Round add-ons are palette parts in the "Round Add-on Parts" data mod, placed like the game's cube.)
[HarmonyPatch]
public static class ShapeTools
{
    [HarmonyPostfix, HarmonyPatch(typeof(PlateStructureEditor), nameof(PlateStructureEditor.OnGUI))]
    static void Draw(PlateStructureEditor __instance, IGUILayout layout) => Ui.Guard("Merge and cut", () =>
    {
        var part = __instance.Component.VehicleObject;
        var editor = DesignEditor.Instance;
        var ui = layout.TryCast<IGUIElementDrawer>();
        if (editor == null || ui == null || part.GUID is not (Conversion.AddonGuid or Conversion.CompartmentGuid)) return;
        int addon = (int)part.VUID;
        bool body = part.GUID == Conversion.CompartmentGuid; // a turret or hull: add-ons can merge into it
        var others = editor.SelectedParts(Conversion.AddonGuid).Where(v => v != addon).ToList();
        if (body && others.Count == 0) return; // only offered when add-ons are selected with it
        Ui.Section(layout, body ? "将附加部件合并到此部件" : "合并附加部件");
        if (others.Count == 0) ui.InfoField("同时选中其他附加部件，即可把它们合并到本部件。\nCtrl+J 会把所有选中的部件合并到最后选中的那个。", 2);
        else
        {
            var tip = new UITooltip("合并附加部件", body
                ? "选中的附加部件会成为本炮塔或车体的一部分：形状、位置与装甲并入，并随其一起转动。它们的内面计入本部件的内面。镜像对应体会一起合并。"
                : "其余选中的附加部件会成为本部件的一部分：形状、位置和装甲都并入本部件，附着在它们上的部件也会移到本部件上。若本附加部件与其他附加部件都有镜像对应体，对应体会同样合并。");
            ui.Button($"将{others.Count}个选中附加部件{(others.Count == 1 ? "" : "s")}合并到此部件", Ui.Callback(() =>
                editor.RequestLiveEdit("正在合并附加部件", $"已把{others.Count}个附加部件{(others.Count == 1 ? "" : "s")}并入部件{addon}。", json => AddonEdits.PlanMerge(json, addon, others, editor.LiveShapes(json)))), ref tip);
        }
        if (body) return; // cutting with a part is for add-ons

        Ui.Section(layout, "用此附加部件切割");
        var targets = editor.SelectedParts().Where(v => v != addon).ToList();
        string what = targets.Count == 0 ? "它所在的形状" : $"{targets.Count}个选中部件{(targets.Count == 1 ? "" : "s")}";
        // Explicit line breaks: the panel shows exactly the lines it's told, and cuts off the rest.
        ui.InfoField($"把该附加部件的形状从{what}上切下来。\n通孔切穿；凹槽会加侧壁和底面。\n可用 Ctrl+Z 撤销。", 3);
        // Short labels: a toggle's label only gets the narrow left column.
        ui.ToggleField("保留附加部件", keepCutter, Ui.BoolCallback(v => keepCutter = v),
            "关闭：切割用的附加部件会被删除。开启：它保留下来（例如可在别处再切一次）。");
        ui.ToggleField("矩形框", rectangleBox, Ui.BoolCallback(v =>
        {
            rectangleBox = v;
            fill = v ? Fill.Mode.Rectangle : (fill == Fill.Mode.Rectangle ? Fill.Mode.Fewest : fill);
            __instance.RequestRedraw();
        }), "关闭：孔洞直接与装甲板的角点相连。开启：在切割口四周加一圈整齐的矩形框，矩形框以外的板面保持干净。");
        var fillTip = new UITooltip("填充", "切割口周围的装甲板如何填充。最少点：只用切割口自身的点和" +
            "面的角点，不生成新点（类似 Blender 的布尔运算）。矩形框：在切割口四周加一圈整齐的矩形框。" +
            "轻环：孔与角点之间加一圈新点，" +
            "面较均匀。平滑环：紧贴孔加一圈四边面，再逐圈向外扩展（点数最多，切片最均匀）。");
        ui.Button($"填充：{Fill.ModeNames[(int)fill]}（点击更换）", Ui.Callback(() =>
        {
            fill = (Fill.Mode)(((int)fill + 1) % Fill.ModeNames.Length);
            rectangleBox = fill == Fill.Mode.Rectangle;
            __instance.RequestRedraw();
        }), ref fillTip);
        foreach (bool pocket in new[] { false, true })
        {
            var tip = new UITooltip(pocket ? "切割凹槽" : "切割通孔", pocket
                ? "按附加部件的形状切出凹陷：其在结构内部的表面会变成带装甲的板。"
                : "按附加部件的形状切穿它经过的每一块装甲板。镜像板（对应体对，或两侧都显示的板）会两侧同时切穿。");
            var mode = rectangleBox ? Fill.Mode.Rectangle : fill;
            ui.Button(pocket ? "切割凹槽（带侧壁）" : "切割通孔", Ui.Callback(() =>
                editor.RequestLiveEdit(pocket ? "正在切割凹槽" : "正在切割通孔", pocket ? "已切出凹槽。" : "已切出通孔。",
                    json => AddonEdits.PlanCut(json, addon, targets, !keepCutter, pocket, mode, editor.LiveShapes(json)))), ref tip);
        }
    });

    static bool keepCutter;
    static bool rectangleBox;
    static Fill.Mode fill = Fill.Mode.Fewest;
}
