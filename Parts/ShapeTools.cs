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
        Ui.Section(layout, "原点");
        ui.InfoField("原点在形状包围盒的中心；点、旋转和复制都绕它进行。移动原点不会移动部件外观。", 2);
        var originTip = new UITooltip("把原点移到形状中心",
            "像 Blender 的“原点到几何中心”：形状留在原处，原点移到它的包围盒中心；挂在它下面的部件会一起补偿，外观不动。镜像对侧会一起移动。");
        ui.Button("把原点移到形状中心", Ui.Callback(() =>
            editor.RequestLiveEdit("正在移动原点", "原点已移到形状中心。可用“恢复”撤销。",
                json => AddonEdits.PlanOrigin(json, addon))), ref originTip);
        if (!body) Detach(ui, layout, __instance, editor, addon);
        if (body && others.Count == 0) return; // only offered when add-ons are selected with it
        Ui.Section(layout, body ? "将附加部件合并到此部件" : "合并附加部件");
        if (others.Count == 0) ui.InfoField($"同时选中其他附加部件，即可把它们合并到本部件。\n{Keybinds.Shown("join")} 会把所有选中的部件合并到最后选中的那个。", 2);
        else
        {
            var tip = new UITooltip("合并附加部件", body
                ? "选中的附加部件会成为本炮塔或车体的一部分：形状、位置与装甲并入，并随其一起转动。它们的内面计入本部件的内面。镜像对应体会一起合并。"
                : "其余选中的附加部件会成为本部件的一部分：形状、位置和装甲都并入本部件，附着在它们上的部件也会移到本部件上。若本附加部件与其他附加部件都有镜像对应体，对应体会同样合并。");
            ui.Button($"将{others.Count}个选中附加部件{(others.Count == 1 ? "" : "s")}合并到此部件", Ui.Callback(() =>
                editor.RequestLiveEdit("正在合并附加部件", $"已把{others.Count}个附加部件{(others.Count == 1 ? "" : "s")}并入部件{addon}。", json => AddonEdits.PlanMerge(json, addon, others, editor.LiveShapes(json)))), ref tip);
        }
        Ui.Section(layout, "在它上面散落附加件");
        if (others.Count == 0) ui.InfoField("先选中要散落的附加件（叶子、石头；可混多种），最后选中要落到的部件。", 2);
        else
        {
            ui.InfoField($"把选中的 {others.Count} 种附加件随机复制到本部件表面，每份都像平放在地面上那样贴着该面。", 2);
            ui.Slider("份数", scatterCount, 1, AddonEdits.MaxScatter, Ui.FloatCallback(v => scatterCount = (int)Math.Round(v)));
            ui.ToggleField("只落在朝上的面", scatterUp, Ui.BoolCallback(v => scatterUp = v),
                "开：只落在朝上的面（甲板、车顶），像叶子落地。关：侧面和底面也会落。");
            var spreadTip = new UITooltip("散落", "每份都是附着在本部件上的附加件，随机转向；种类从选中的附加件里随机挑。面越大落得越多。每次点击重新散落，“恢复”可撤销。");
            ui.Button("散落这些复制件", Ui.Callback(() => Scatter(addon, others)), ref spreadTip);
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
        var fillTip = new UITooltip("填充", "点击切换切割口周围的填充方式。最少点：只用面原有的角点和切割口本身，" +
            "不生成新点。轻量：只加少量点，避免出现细长的面。" +
            "平滑：沿切割口加一圈四边面，再向外增点使各面更均匀。" +
            "矩形框：在切割口四周加一圈整齐的矩形边框。");
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
        Ui.Section(layout, "披挂为布料");
        ui.InfoField("把此附加部件变成一张和它顶部一样宽长的布，落到车上直到稳定：篷布、盖布、伪装网。先把附加部件放在要盖住的位置上方。", 3);
        ui.Slider("点间距（厘米）", clothCell * 100, 5, 25, Ui.FloatCallback(v => clothCell = MathF.Round(v) / 100));
        ui.Slider("硬度（%）", clothStiffness * 100, 2, 100, Ui.FloatCallback(v => clothStiffness = MathF.Round(v) / 100));
        ui.ToggleField("网", clothNet, Ui.BoolCallback(v => { clothNet = v; __instance.RequestRedraw(); }),
            "开：布变成网，点与点之间的每格留一个洞，四周是网绳，用于伪装网。关：实心布。");
        if (clothNet) ui.Slider("洞大小（%）", netHole * 100, 40, 85, Ui.FloatCallback(v => netHole = MathF.Round(v) / 100));
        var landTip = new UITooltip("落在", "点击循环。全部：其他每块板都会接住它。仅选中部件：先选它要落在的部件，最后选这个附加部件。除选中部件外：它会穿过这些部件落下（天线、炮管）。镜像对应体随其伙伴一起计算。");
        ui.Button($"落在：{LandsOnNames[(int)landsOn]}", Ui.Callback(() =>
        {
            landsOn = (AddonEdits.LandsOn)(((int)landsOn + 1) % LandsOnNames.Length);
            __instance.RequestRedraw();
        }), ref landTip);
        var drapeTip = new UITooltip("披挂为布料",
            "附加部件的顶部变成一张点阵布，落到下方的车体、炮塔和附加部件上并稳定；舱盖、潜望镜、车轮、履带等没有形状数据的部件会以包围盒接住它。点越密越贴合，但面更多、耗时更长；布越硬，越过边缘时弯得越少。稳定后的布替换附加部件的形状，最厚 10 毫米。镜像附加部件两侧一起披挂。Ctrl+Z 可撤销。");
        ui.Button(clothNet ? "披挂为网" : "披挂为布", Ui.Callback(() => Drape(addon)), ref drapeTip);

    });

    /// 把附加部件按当前面板设置披挂成布料（AddonEdits.PlanDrape）；其余选中部件就是“落在”所指的部件。
    /// 把选中的附加件随机散落到本部件表面（AddonEdits.PlanScatter），每次点击重新散落。
    internal static void Scatter(int target, List<int> types)
    {
        if (DesignEditor.Instance is not { } editor) return;
        int count = scatterCount, seed = Environment.TickCount;
        bool upOnly = scatterUp;
        editor.RequestLiveEdit("正在散落附加件", $"已散落 {count} 份。“恢复”可撤销。",
            json => AddonEdits.PlanScatter(json, target, types, count, seed, upOnly, editor.LiveShapes(json)));
    }

    internal static void Drape(int addon)
    {
        if (DesignEditor.Instance is not { } editor) return;
        var options = new AddonEdits.DrapeOptions(clothCell, clothNet ? netHole : 0, clothStiffness, landsOn, editor.SelectedParts().Where(v => v != addon).ToList());
        editor.RequestLiveEdit(clothNet ? "正在披挂网" : "正在披挂布料", clothNet ? "网已披挂。" : "布料已披挂。",
            json => AddonEdits.PlanDrape(json, addon, options, editor.LiveShapes(json), editor.PartBoxes()));
    }

    /// 右键菜单：把镜像侧变成独立附加件（与面板按钮走同一份改动）。
    internal static void RealMirror(int addon)
    {
        if (DesignEditor.Instance is not { } editor) return;
        editor.RequestLiveEdit("正在把镜像侧变成实体", "已把镜像侧变成独立附加件，它上面的附加件也一起变成实体。可用“恢复”撤销。",
            json => AddonEdits.PlanRealMirror(json, addon, editor.LiveShapes(json)));
    }

    /// 拆镜像：镜像件在编辑器里没有点、改形状两边一起变，这一段把它变成两个各自独立的普通部件。
    /// 改的是蓝图数据（要重载设计）：Ctrl+Z 撤不回，用"恢复"。
    static void Detach(IGUIElementDrawer ui, IGUILayout layout, PlateStructureEditor plateEditor, DesignEditor editor, int addon)
    {
        string state;
        try { state = plateEditor.Component.VehicleObject?.VehicleTransform?.Mirror != null ? "twin" : plateEditor.Component.Mirrored ? "image" : ""; }
        catch (Exception) { state = ""; } // 面板上这一段问不到就不显示成色，别把合并和切割一起带走
        Ui.Section(layout, "把镜像件拆成两个独立件");
        if (state == "")
        {
            ui.InfoField("本部件没有镜像对应体：两边本来就是各自独立的。", 1);
            return;
        }
        var tip = new UITooltip("拆成两个独立件", state == "twin"
            ? "这块板和对侧那块是两条互相指着的记录，共用同一份形状：改一边的形状另一边跟着变，对侧还没有点。断开后两侧各拿一份形状，变成能分别点选、分别编辑的两个部件。"
            : "这块板只存了一份，对侧是游戏按「镜像显示」现画的：那一半没有点，也不能单独改。拆开后对侧成为一条自己的部件记录（摆到镜像位置、拿一份自己的形状），这块板不再画对侧。");
        ui.Button(state == "twin" ? "断开镜像：两侧变成独立部件" : "把镜像画出的那一侧变成独立部件", Ui.Callback(() =>
            editor.RequestLiveEdit("正在把镜像侧变成实体", "已把镜像侧变成独立附加件，它上面的附加件也一起变成实体。可用“恢复”撤销。",
                json => AddonEdits.PlanRealMirror(json, addon, editor.LiveShapes(json)))), ref tip);
        ui.InfoField("拆开后再改一边，另一边不会跟着变。\n镜像侧上挂着的附加件会一起变成实体。改的是蓝图数据，会重载设计：Ctrl+Z 撤不回，要用“恢复”。", 2);
    }

    static bool keepCutter;
    static bool rectangleBox;
    static Fill.Mode fill = Fill.Mode.Fewest;
    static float clothCell = 0.1f, clothStiffness = 0.15f, netHole = 0.6f;
    static bool clothNet;
    static AddonEdits.LandsOn landsOn;
    static readonly string[] LandsOnNames = { "全部", "仅选中部件", "除选中部件外" };
    static int scatterCount = 40;
    static bool scatterUp = true;
}
