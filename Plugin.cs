using System.Diagnostics;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Sprocket.UI;
using UnityEngine;
using UnityEngine.Events;

namespace SprocketTools;

/// SprocketTools: small editor improvements, each one a section in the game's own inspector panels.
[BepInPlugin("local.sprocket.tools", "SprocketTools", "1.0.2")]
public sealed class Plugin : BasePlugin
{
    internal static ManualLogSource ModLog = null!;
    internal static ConfigEntry<string>? Folded;
    internal static ConfigEntry<bool>? ShowHotkeys;
    internal static ConfigEntry<bool>? DrawingNoWireframe;
    internal static ConfigEntry<bool>? DrawingSeeThroughOutline;
    internal static ConfigEntry<bool>? DrawingColourOutline;
    internal static ConfigEntry<float>? DrawingIntensity;
    internal static ConfigEntry<bool>? DrawingElevation, DrawingTraverse, DrawingBlue, DrawingTurretTraverse;
    internal static ConfigEntry<bool>? DrawingCreaseNormals;
    internal static ConfigEntry<bool>? DrawingSeamFilter;
    internal static ConfigEntry<bool>? DrawingGrid;
    internal static ConfigEntry<float>? DrawingGridIntensity;
    internal static ConfigEntry<string>? DrawingHiddenGunLimits;
    internal static ConfigEntry<float>? ExplodeSpread, FlashlightPercent, FullbrightPercent;
    internal static ConfigEntry<int>? BackupsKept;
    internal static ConfigEntry<float>? RotationSnap;
    internal static ConfigEntry<bool>? MirrorMerge;
    internal static ConfigEntry<int>? CardSideOffset, CardElevation, CardAimHeight, CardWidth, CardHeight, CardLightingRecipe;
    internal static ConfigEntry<bool>? CardLeftSide, CardWhiteBackground, CardFlatLighting, CardLightingProbe;
    internal static ConfigEntry<float>? CardFieldOfView, CardFraming, CardFullbrightPercent;
    public override void Load()
    {
        var loadWatch = Stopwatch.StartNew();
        ModLog = Log;
        DrawingNoWireframe = Config.Bind("Drawing sheet", "No wireframe", false, "F9 彩色图与透视导出中省略线框叠加层。单独的纯线条图纸不受影响。");
        DrawingSeeThroughOutline = Config.Bind("Drawing sheet", "See-through outline only", true, "透视内部视图只保留载具轮廓剪影，不再绘制网格边线。“取消线框”时该轮廓也一并关闭。");
        DrawingColourOutline = Config.Bind("Drawing sheet", "Colour outline only", true, "彩色图纸只画可见的部件轮廓与锐边，不含网格三角化线。“取消线框”时这些轮廓线也一并关闭。");
        DrawingIntensity = Config.Bind("Drawing sheet", "Wireframe intensity percent", 100f, "F9 图纸上几何线稿的浓度，0 至 100。涂装、尺寸标注与文字保持原有浓度。");
        DrawingCreaseNormals = Config.Bind("Drawing sheet", "Creases from shading normals", false, "F9 线稿改按网格自己烘好的顶点法线判断硬边，而不是按每个三角形的几何法线：光滑着色的圆弧面（炮塔弧面、翼子板圆角）不再画出分面棱，真硬边照旧。默认关闭。");
        DrawingSeamFilter = Config.Bind("Drawing sheet", "Hide plate joints", false, "F9 线稿隐去装甲板对拼的拼接缝：一条开边的另一侧若被另一块网格以同高同向的面接住，就不画。车体外轮廓与折棱不受影响；与板面齐平的舱盖边线也会一并消失。默认关闭。");
        DrawingElevation = Config.Bind("Drawing sheet", "Gun elevation and depression", false, "侧视图中用虚线标出瞄准驱动上下极限处的火炮位置。不改动载具。");
        DrawingTraverse = Config.Bind("Drawing sheet", "Gun traverse", false, "顶视图中用虚线标出瞄准驱动左右极限处的火炮位置（含炮廓车型）。不改动载具。");
        DrawingTurretTraverse = Config.Bind("Drawing sheet", "Turret rotation", false, "顶视图中按方向机的最小/最大角度标出炮塔旋转。整周旋转画成 360 度圆弧。");
        DrawingBlue = Config.Bind("Drawing sheet", "Blue blueprint", false, "另外保存一张蓝底白线图纸，尺寸与火炮活动选项相同。");
        DrawingGrid = Config.Bind("Drawing sheet", "Blueprint grid", false, "仅给蓝底图纸添加淡色方格背景。小格按绘图比例为 0.25 米，每米一条较深的线。");
        DrawingGridIntensity = Config.Bind("Drawing sheet", "Grid intensity percent", 20f, "蓝底图纸背景网格的浓度，0 至 100。载具线条与文字绘制在其上。");
        DrawingHiddenGunLimits = Config.Bind("Drawing sheet", "Hidden gun limits", "[]", "仅从火炮活动极限标注中排除的火炮。由火炮面板中的“该炮极限标注”管理。按设计名和火炮 ID 存储；重命名设计后会另起一份选择。");
        Folded = Config.Bind("Panels", "Folded sections", "", "在编辑器面板中折叠起来的 SprocketTools 小节，用 | 分隔");
        ShowHotkeys = Config.Bind("Panels", "Show hotkeys box", true, "手工结构面板旁边的快捷键面板（编辑器中按 F1 显示或隐藏）");
        ExplodeSpread = Config.Bind("Panels", "Exploded view spread", 0.5f, "爆炸视图（F2）把部件移开的距离，单位为米（F3 / F4 调整）");
        FlashlightPercent = Config.Bind("Panels", "Flashlight brightness", 80f, "手电筒（F6）照射位置的亮度，按太阳光强度的百分比计");
        FullbrightPercent = Config.Bind("Panels", "Fullbright brightness", 25f, "全亮（F7）的 14 个灯各自的亮度，按太阳光强度的百分比计");
        RotationSnap = Config.Bind("Editor", "Rotation snap (degrees)", 0f, "游戏旋转捕捉开启时，以本值（度）作为转动步进，代替游戏自带的（7.5 可作出 48 边圆，5 作出 72 边）。填 0 保持游戏步进。");
        MirrorMerge = Config.Bind("Editor", "Mirror merge", true, "编辑器镜像开启时，合并（M）会连同另一侧的镜像点一起合并。");
        BackupsKept = Config.Bind("Backups", "Backups kept", 50, "保留多少份设计备份（BepInEx\\SprocketToolsBackups，每次修改一份）；超出时先删最旧的。填 0 表示全部保留。");
        // The F10 photo card (War Thunder profile view). Sections and keys stay English (they live in the cfg file);
        // the Card prefix keeps these apart from the drawing sheet's own entries.
        CardSideOffset = Config.Bind("View", "Side offset degrees", 60,
            "机位的侧偏角度，从正侧视起算（0 = 正侧视，90 = 正对车头）。对着参考图量出来定的是 60。");
        CardElevation = Config.Bind("View", "Elevation degrees", 0,
            "俯角，度数。正数=从上往下看，0=平视，负数=轻微仰视。对着参考图量出来在 0 到 -4 之间；" +
            "画面的高低感用下面那项视线高度调，别动俯角。");
        CardAimHeight = Config.Bind("View", "Aim height percent", 40,
            "视线对准车高的百分之几（0=地面、50=正中、100=车顶），相机离地高度跟着这个点走。" +
            "对准正中会把相机抬到车体上沿之上、读着像俯拍；参考图那种平视感在 40 上下，可以自己扫这个值。");
        CardLeftSide = Config.Bind("View", "Camera on the left", false,
            "相机放到车的左侧（画面里车头朝左）。车左右不对称时只能拍到一侧，默认拍右侧。");
        CardFieldOfView = Config.Bind("View", "Field of view", 20f, "竖直视场角，度。参考图量出来是 20。");
        CardFraming = Config.Bind("View", "Framing", 0.92f, "车的投影占画面多满（0 到 1）。0.92 表示留 8% 的边。");
        CardWidth = Config.Bind("Picture", "Width pixels", 4096, "出图宽度（像素）。资料卡的画布是 2:1，不受屏幕分辨率限制。4096 下一张约 90 MB 显存/内存峰值，探针连拍时更吃内存。");
        CardHeight = Config.Bind("Picture", "Height pixels", 2048, "出图高度（像素）。");
        CardWhiteBackground = Config.Bind("Picture", "White background", true,
            "白底（开）还是透明底（关）。两种都要先把车后面的品红抠掉，抠不干净会在边上留一丝粉。");
        CardFlatLighting = Config.Bind("Picture", "Flat lighting", false,
            "平光：开=关掉所有投影并在车周围铺 14 盏补光（每个面一样亮，像泥模）；关=照游戏自己的太阳和阴影拍（有明暗、有投影）。资料卡要的是关。");
        CardFullbrightPercent = Config.Bind("Picture", "Fullbright brightness", 25f,
            "平光（上面那项开着才用）铺的 14 盏补光灯各自的亮度，按日照度的百分比算。");
        CardLightingRecipe = Config.Bind("Picture", "Lighting recipe", 5,
            "按 F10 正常出图时用哪组打光，编号跟下面探针那 8 张一一对应：0=不加（就用游戏自己的太阳和阴影，等于以前那张）、" +
            "1 SSGI、2 天反、3 全开（镜面反射/SSGI/天空反射/贴花全留）、4 天光补、5 轮位补（轮拱两盏补光，默认）、" +
            "6 提亮（曝光 +0.7EV）、7 冷调（4500K）。觉得黑了就试 5 或 6，想原样就填 0。");
        CardLightingProbe = Config.Bind("Picture", "Lighting probe", false,
            "打光探针：开=按 F10 时机位不变连拍 8 组打光（基线、SSGI、天反、全开、天光补、轮位补、提亮、冷调），" +
            "存成“资料卡探针 N 名称.png”，用来跟参考图对比挑打光；关=照常只出一张（用上面那组的打光）。挑完就关。");
        AddComponent<DesignEditor>();
        long setupMs = loadWatch.ElapsedMilliseconds;
        var harmony = new Harmony("local.sprocket.tools");
        var features = new[] { typeof(InspectorSection), typeof(TurretDriveRepair), typeof(ShapeTools), typeof(RestoreSection), typeof(HoleQuality), typeof(MeshTools), typeof(MergeFaces), typeof(Hotkeys), typeof(TurretCopy), typeof(ExplodedView), typeof(GunLength), typeof(GearSpeeds), typeof(PartPaint), typeof(ImageAddresses), typeof(DrawingSheet.NoHover), typeof(DrawingSettings), typeof(RotationSnap), typeof(MirrorMerge), typeof(PanelFit) };
        var timings = new List<(string name, long ms)>();
        foreach (var feature in features)
        {
            var watch = Stopwatch.StartNew();
            try { harmony.PatchAll(feature); }
            catch (Exception ex) { Log.LogError($"{feature.Name} disabled, could not attach to the game: {ex}"); }
            watch.Stop();
            timings.Add((feature.Name, watch.ElapsedMilliseconds));
        }
        var slowest = timings.OrderByDescending(t => t.ms).Take(5).Select(t => $"{t.name} {t.ms}ms");
        Log.LogInfo($"[timing] SprocketTools load: total {loadWatch.ElapsedMilliseconds}ms｜配置与组件 {setupMs}ms｜挂点 {loadWatch.ElapsedMilliseconds - setupMs}ms（{features.Length} 个类）｜最慢 {string.Join(", ", slowest)}");
        Log.LogInfo("SprocketTools 1.0.2 loaded: Turret to Add-on, Merge add-ons, Cut with add-on, Hole quality, Merge faces, Mesh tools, Smooth edge, Split faces, Hotkeys, Turret copy, Exploded view, Gun length, Speed & acceleration, Max-quality photo, Photo card, Own paint, Bridge, Circle, Fix mirror, Mirror merge, Rotation snap.");
    }
}

internal static class Ui
{
    static UnityEngine.Font? cjk;
    static bool cjkTried;
    static readonly string[] CjkNames = { "Microsoft YaHei", "Microsoft JhengHei", "SimHei", "SimSun" };

    /// The few things drawn with IMGUI (the hotkeys box, the measurements, the status bar) write with Unity's built-in
    /// font, which has no Chinese glyphs. Point the styles at a Windows font that has them, before any of that is drawn.
    internal static void CjkFont()
    {
        if (cjkTried) return;
        cjkTried = true;
        foreach (var name in CjkNames)
        {
            cjk = DynamicFont(name);
            if (cjk == null) continue;
            Plugin.ModLog.LogInfo($"TOOL_FONT IMGUI using {name}");
            break;
        }
        if (cjk == null) { Plugin.ModLog.LogWarning("TOOL_FONT no Windows font with Chinese glyphs found; IMGUI text stays as the built-in font"); return; }
        var skin = GUI.skin;
        skin.font = cjk;
        skin.label.font = skin.box.font = skin.button.font = skin.window.font = cjk;
    }

    /// Font.CreateDynamicFontFromOSFont has no IL2CPP binding in this game (its call resolves to a Font constructor
    /// the interop assembly doesn't carry), so the internal call it would have made is made directly.
    static UnityEngine.Font? DynamicFont(string name)
    {
        try
        {
            var type = typeof(UnityEngine.Font);
            var font = type.GetConstructor(Type.EmptyTypes)?.Invoke(null) as UnityEngine.Font;
            var create = type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name == "Internal_CreateDynamicFont" && m.GetParameters().Length == 3);
            if (font == null || create == null) return null;
            create.Invoke(null, new object[] { font, new Il2CppStringArray(new[] { name }), 16 });
            return font.Pointer == IntPtr.Zero ? null : font;
        }
        catch (Exception ex) { Plugin.ModLog.LogInfo($"TOOL_FONT {name}: {ex.Message}"); return null; }
    }

    /// 游戏的原生部件编辑器会共用同一份 inspector 布局（例如炮塔环和炮塔架）。我们的后挂要么在同一帧里
    /// 被下一个原生编辑器接着画：最后一个下拉/折叠必须替游戏关掉，否则它读到的是没闭合的状态。
    internal static void Inspector(string feature, IGUILayout layout, Action draw) => Guard(feature, () =>
    {
        try { draw(); }
        finally { layout.EndAllDropdowns(); }
    });

    /// An exception thrown back into the game's inspector drawing could take the game down; log it instead.
    internal static void Guard(string feature, Action draw)
    {
        try { draw(); }
        catch (Exception ex)
        {
            // The same error again (a GUI drawn every frame) is counted, not logged again: no wall of red.
            string text = ex.ToString();
            if (lastErrors.TryGetValue(feature, out var last) && last.Text == text)
            {
                lastErrors[feature] = (text, ++last.Count);
                if (last.Count % 1000 == 0) Plugin.ModLog.LogError($"{feature}: the same error again, {last.Count} times so far");
                return;
            }
            lastErrors[feature] = (text, 1);
            Plugin.ModLog.LogError($"{feature}: {ex}");
        }
    }

    static readonly Dictionary<string, (string Text, int Count)> lastErrors = new();

    internal static UnityAction Callback(Action action) => DelegateSupport.ConvertDelegate<UnityAction>(action)!;

    internal static Il2CppSystem.Action<float> FloatCallback(Action<float> action) =>
        DelegateSupport.ConvertDelegate<Il2CppSystem.Action<float>>(action)!;

    internal static Il2CppSystem.Action<bool> BoolCallback(Action<bool> action) =>
        DelegateSupport.ConvertDelegate<Il2CppSystem.Action<bool>>(action)!;

    internal static UnityAction<int> IntCallback(Action<int> action) =>
        DelegateSupport.ConvertDelegate<UnityAction<int>>(action)!;

    static HashSet<string>? closed;

    /// Starts a top-level section the player can fold away (the game's own dropdown). It stays folded, across restarts
    /// too (saved in the plugin's config file).
    internal static void Section(IGUILayout layout, string title)
    {
        closed ??= (Plugin.Folded?.Value ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        layout.EndAllDropdowns();
        layout.BeginDropdown(title, !closed.Contains(title), BoolCallback(open =>
        {
            if (open ? !closed.Remove(title) : !closed.Add(title)) return;
            if (Plugin.Folded != null) Plugin.Folded.Value = string.Join("|", closed);
        }));
    }
}
