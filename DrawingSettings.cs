using HarmonyLib;
using Sprocket.UI;
using Sprocket.Vehicles.PlateStructures.Design;
using Sprocket.Vehicles.Turrets.Editor;
using Sprocket.Vehicles.Cannons.Editor;
using Sprocket.Vehicles.Cannons;

namespace SprocketTools;

[HarmonyPatch]
public static class DrawingSettings
{
    [HarmonyPostfix, HarmonyPatch(typeof(PlateStructureEditor), nameof(PlateStructureEditor.OnGUI))]
    static void Structure(IGUILayout layout) => Draw(layout);
    [HarmonyPostfix, HarmonyPatch(typeof(TurretRingEditor), nameof(TurretRingEditor.OnGUI))]
    static void Ring(IGUILayout layout) => Draw(layout);
    [HarmonyPostfix, HarmonyPatch(typeof(CannonEditor), nameof(CannonEditor.OnGUI))]
    static void Cannon(CannonEditor __instance, IGUILayout layout) => Draw(layout, __instance.Component);

    internal static string GunKey(Cannon gun) => GunAnnotationPreferences.Key(gun.Vehicle?.DesignInfo?.Name, (int)gun.VehicleObject.VUID);

    static string? cachedLimits;
    static GunAnnotationPreferences gunLimits = new(null);

    static void Draw(IGUILayout layout, Cannon? gun = null) => Ui.Inspector("Drawing sheet settings", layout, () =>
    {
        var ui = layout.TryCast<IGUIElementDrawer>();
        if (ui == null) return;
        Ui.Section(layout, "工程图纸（F9）");
        ui.InfoField("按F9保存工程图纸。\n设置会被记住。", 2);
        if (gun != null)
        {
            string stored = Plugin.DrawingHiddenGunLimits?.Value ?? "[]";
            if (cachedLimits != stored) { gunLimits = new(stored); cachedLimits = stored; }
            string key = GunKey(gun);
            if (key.Length > 0)
                ui.ToggleField("该炮极限标注", gunLimits.Shows(key), Ui.BoolCallback(show =>
                {
                    // Re-read here so a delayed inspector callback cannot overwrite another gun's choice.
                    var choices = new GunAnnotationPreferences(Plugin.DrawingHiddenGunLimits?.Value);
                    choices.Set(key, show);
                    if (Plugin.DrawingHiddenGunLimits != null) Plugin.DrawingHiddenGunLimits.Value = choices.Serialize();
                }), "在F9图纸上标注该炮的仰角、转向与炮塔旋转极限。火炮本身仍然可见。全局运动选项也须开启。按设计名称与火炮ID记忆；重命名设计会重新开始选择。共享炮架时，以已启用的最大口径火炮提供极限值。");
        }
        ui.ToggleField("无线框", Plugin.DrawingNoWireframe?.Value ?? false, Ui.BoolCallback(v =>
        {
            if (Plugin.DrawingNoWireframe != null) Plugin.DrawingNoWireframe.Value = v;
        }), "F9彩色图与透视图中不叠加任何线条（含轮廓线）。默认关闭；跨会话记住。单独输出的纯线条图不受影响。");
        ui.Slider("浓度（%）", Plugin.DrawingIntensity?.Value ?? 100, 0, 100,
            Ui.FloatCallback(v => { if (Plugin.DrawingIntensity != null) Plugin.DrawingIntensity.Value = MathF.Round(v); }));
        ui.ToggleField("着色法线折棱", Plugin.DrawingCreaseNormals?.Value ?? false, Ui.BoolCallback(v =>
        {
            if (Plugin.DrawingCreaseNormals != null) Plugin.DrawingCreaseNormals.Value = v;
        }), "F9 线稿改按网格自己烘好的顶点法线判断硬边，而不是按每个三角形的几何法线。开启后光滑着色的圆弧面（炮塔弧面、翼子板圆角、大半径过渡）不再画出分面棱，真正的板缘与硬棱照旧保留；编辑器里“圆滑边”处理过的位置自动消失。彩色图与透视图的轮廓不受影响。默认关闭。");
        ui.ToggleField("拼接缝抑制", Plugin.DrawingSeamFilter?.Value ?? false, Ui.BoolCallback(v =>
        {
            if (Plugin.DrawingSeamFilter != null) Plugin.DrawingSeamFilter.Value = v;
        }), "F9 线稿隐去装甲板对拼留下的拼接缝。车体由几十块独立装甲板拼成，每块四周都是开边，两板共面板对拼时深度与朝向都相同，线稿就把每条板缘都描出来。开启后：在开边另一侧 3 像素处取三个点，三点都被另一块网格以“高差小于 10 毫米、法线夹角小于 15°”的面接住，整条边判为拼接缝隐去。折棱与车体外轮廓照旧；与板面齐平、凸起不足 10 毫米的舱盖边线也会一并消失。默认关闭。");
        ui.ToggleField("线条抗锯齿", Plugin.DrawingAntiAliasing?.Value ?? false, Ui.BoolCallback(v =>
        {
            if (Plugin.DrawingAntiAliasing != null) Plugin.DrawingAntiAliasing.Value = v;
        }), "F9 的线条图（以及由它生成的蓝图纸）按两倍分辨率重画一遍再折回：每个像素按线条盖住的比例上墨，斜边和圆弧上的阶梯换成浓淡。四个视图都要重画，出图明显更慢；只影响线条图，彩色图与透视图上的描边不变。默认关闭。");
        ui.ToggleField("开发者模式", Plugin.DrawingDevMode?.Value ?? false, Ui.BoolCallback(v =>
        {
            if (Plugin.DrawingDevMode != null) Plugin.DrawingDevMode.Value = v;
        }), "开发者模式：常规三张图照常出，另外补出同槽位的对照图，名字带“-开发者图纸”后缀。其一，“名称-开发者图纸（彩色图）／（透视图）”——常规版供色已改成黑底（背景那层品红不再往轮廓里晕出一条边），这两张保留旧的品红底供色好比对差别。其二，开着抗锯齿时另出“名称-开发者图纸”，即未做抗锯齿的线条图。日志同时给出品红像素数、黑底与品红底两趟颜色的逐像素差异、半色像素占比与补拍耗时。平时请关掉。");
        ui.ToggleField("火炮俯仰角", Plugin.DrawingElevation?.Value ?? false,
            Ui.BoolCallback(v => { if (Plugin.DrawingElevation != null) Plugin.DrawingElevation.Value = v; }),
            "侧视图：用虚线画出瞄准驱动极限处的火炮位置，并标注角度。不会改动载具。");
        ui.ToggleField("火炮转向", Plugin.DrawingTraverse?.Value ?? false,
            Ui.BoolCallback(v => { if (Plugin.DrawingTraverse != null) Plugin.DrawingTraverse.Value = v; }),
            "顶视图：分别标注左右火炮转向极限，适用于无炮塔的炮廓车型。读取火炮瞄准驱动，而非炮塔旋转。");
        ui.ToggleField("炮塔旋转", Plugin.DrawingTurretTraverse?.Value ?? false,
            Ui.BoolCallback(v => { if (Plugin.DrawingTurretTraverse != null) Plugin.DrawingTurretTraverse.Value = v; }),
            "顶视图：标注绕炮塔环的旋转，取方向机的最小与最大极限。整周旋转显示为一段360°圆弧。");
        ui.ToggleField("蓝色蓝图", Plugin.DrawingBlue?.Value ?? false,
            Ui.BoolCallback(v => { if (Plugin.DrawingBlue != null) Plugin.DrawingBlue.Value = v; }),
            "在常规F9图纸之外，另存一张蓝底白线的蓝图。");
        ui.ToggleField("蓝图网格", Plugin.DrawingGrid?.Value ?? false,
            Ui.BoolCallback(v => { if (Plugin.DrawingGrid != null) Plugin.DrawingGrid.Value = v; }),
            "在蓝色蓝图背后添加淡色方格线，需同时启用蓝色蓝图。按图纸比例每格代表 0.25 米，每隔一米线更重。");
        ui.Slider("网格浓度（%）", Plugin.DrawingGridIntensity?.Value ?? 20, 0, 100,
            Ui.FloatCallback(v => { if (Plugin.DrawingGridIntensity != null) Plugin.DrawingGridIntensity.Value = MathF.Round(v); }));
        ui.ToggleField("彩色图轮廓", Plugin.DrawingColourOutline?.Value ?? true, Ui.BoolCallback(v =>
        {
            if (Plugin.DrawingColourOutline != null) Plugin.DrawingColourOutline.Value = v;
        }), "F9彩色图：勾勒可见部件与锐角，隐去三角接缝与浅斜面。默认开启；关闭后只剩三角线框。若开启无线框选项，两张彩色图都不再添加线条。");
        ui.ToggleField("透视轮廓", Plugin.DrawingSeeThroughOutline?.Value ?? true, Ui.BoolCallback(v =>
        {
            if (Plugin.DrawingSeeThroughOutline != null) Plugin.DrawingSeeThroughOutline.Value = v;
        }), "F9透视图：在可见的内部之上只勾勒载具外轮廓，不画网格边线。默认开启；关闭后只剩三角线框。若开启无线框选项，两张彩色图都不再添加线条。");
    });
}
