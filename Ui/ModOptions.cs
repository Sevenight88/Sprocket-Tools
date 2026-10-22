using BepInEx.Configuration;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Sprocket.SettingConfiguration;
using Sprocket.UI;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;

namespace SprocketTools;

/// 游戏自带设置界面里的「SprocketTools」一页，排在 Controls 之后，用游戏自己的控件画。
/// 游戏会为每个设置页建一个标签，所以这一页就是它的一个页（注册进游戏运行时的一个游戏页类型）：
/// SprocketTools 的按键（点一下、再按新键）和那些没有自己面板的设置。改动即时存档，界面的 Apply / Cancel 不碰它们。
public sealed class ToolsOptionsMenu : SettingsSubMenu
{
    public ToolsOptionsMenu(IntPtr pointer) : base(pointer) { }

    public ToolsOptionsMenu() : base(ClassInjector.DerivedConstructorPointer<ToolsOptionsMenu>())
    {
        ClassInjector.DerivedConstructorBody(this);
        MenuName = "Sprocket工具包";
    }

    public override void DisplayGUI(IGUILayout layout) => Ui.Guard("Mod Options", () => ModOptions.Draw(this, layout));
    public override void Update() => Ui.Guard("Mod Options", () => ModOptions.Tick(this));
    public override void ClearGUI() => ModOptions.Stop();
    public override void Apply() { }
    public override void Cancel() => ModOptions.Stop();
}

[HarmonyPatch]
internal static class ModOptions
{
    static bool registered;
    static readonly List<ToolsOptionsMenu> pages = new();              // kept: the game holds them
    static readonly List<UnityAction> keep = new();                    // the page's button callbacks, while it shows
    static readonly List<Il2CppSystem.Action<bool>> keepBools = new();
    static readonly List<Il2CppSystem.Action<float>> keepFloats = new();
    static readonly List<Il2CppSystem.Action<int>> keepInts = new();
    static string? capturing;                                    // the action waiting for its new key

    /// The page's class made known to the game's runtime (once, before the settings screen first opens).
    internal static void Register()
    {
        try { ClassInjector.RegisterTypeInIl2Cpp<ToolsOptionsMenu>(); registered = true; }
        catch (Exception ex) { Plugin.ModLog.LogError($"Mod Options: couldn't add a settings tab ({ex.Message}); the keys stay in the {Keybinds.Shown("menu")} window."); }
    }

    /// Before the settings screen makes its tabs: Mod Options added to its pages (once per screen).
    [HarmonyPrefix, HarmonyPatch(typeof(SettingsMenu), nameof(SettingsMenu.SetupMenuButtons))]
    static void AddTab(SettingsMenu __instance) => Ui.Guard("Mod Options tab", () =>
    {
        var menus = __instance.subMenus;
        if (!registered || menus == null) return;
        for (int i = 0; i < menus.Length; i++) if (menus[i]?.TryCast<ToolsOptionsMenu>() != null) return;
        var page = new ToolsOptionsMenu();
        pages.Add(page);
        var more = new Il2CppReferenceArray<SettingsSubMenu>(menus.Length + 1);
        for (int i = 0; i < menus.Length; i++) more[i] = menus[i];
        more[menus.Length] = page;
        __instance.subMenus = more;
    });

    internal static void Stop() => capturing = null;

    /// Every frame while the page shows: a waiting key takes the next one pressed.
    internal static void Tick(ToolsOptionsMenu page)
    {
        if (capturing == null || Keyboard.current is not { } keys) return;
        if (Keybinds.Capture(capturing, keys) != Keybinds.Captured.Nothing) { capturing = null; page.GUIRepaint = true; }
    }

    internal static void Draw(ToolsOptionsMenu page, IGUILayout layout)
    {
        keep.Clear(); keepBools.Clear(); keepFloats.Clear(); keepInts.Clear();
        if (layout.TryCast<IGUIElementDrawer>() is not { } ui) return;
        UnityAction Act(Action a) { var c = Ui.Callback(a); keep.Add(c); return c; }
        void Redraw() => page.GUIRepaint = true;

        // Four short pages (the settings screen doesn't scroll, so a long page runs under its Accept / Apply buttons).
        ui.Header("Sprocket工具包");
        var pick = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<int>>(new Action<int>(i => { part = i; capturing = null; Redraw(); }))!;
        keepInts.Add(pick);
        ui.TabGroup(pick, part, Parts);
        if (part == 2) { Settings(ui); return; }
        if (part == 3) { Compatibility(ui); return; }

        ui.InfoField(capturing != null
            ? $"请按下「{Keybinds.All.First(b => b.Id == capturing).Name}」的新按键（要修饰键就连着 Ctrl、Shift 或 Alt 一起按）。Esc 取消，Backspace 则不设按键。"
            : "点一下按键，再按新的键，按完即存。红字表示两个功能共用同一个键。", 2);
        var shared = Keybinds.Shared();
        foreach (var group in Keybinds.All.GroupBy(b => b.Group).Where(g => EditingGroups.Contains(g.Key) == (part == 1)))
        {
            ui.Header(group.Key);
            ui.ColumnCount = 2;
            foreach (var b in group)
            {
                string id = b.Id;
                bool clash = shared.Contains(id);
                ui.InfoField(b.Name, 1, clash ? new Color(1f, 0.5f, 0.45f) : new Color(0.85f, 0.85f, 0.82f));
                var tip = new UITooltip(b.Name, $"{b.Group}。默认：{(b.Default.Length == 0 ? "不设按键" : b.Default)}。点一下，再按新的键。");
                ui.Button(id == capturing ? "请按键……" : Keybinds.Shown(id) + (clash ? "  （与他处共用）" : ""), Act(() => { capturing = id; Redraw(); }), ref tip);
            }
            ui.ColumnCount = 1;
        }
        var resetTip = new UITooltip("恢复全部按键", "把 SprocketTools 的每个按键都改回默认值。");
        ui.Button("把所有按键恢复成默认", Act(() => { foreach (var b in Keybinds.All) Keybinds.Reset(b.Id); capturing = null; Redraw(); }), ref resetTip);
    }

    static int part;
    static readonly string[] Parts = { "工具按键", "编辑按键", "设置", "兼容性" };
    static readonly HashSet<string> EditingGroups = new() { "视图", "网格", "选择", "附加部件" };

    /// 有多少挂点接上了游戏（游戏更新可能改名或删掉某个挂点要用的方法）。
    static void Compatibility(IGUIElementDrawer ui)
    {
        var failed = Hooks.Failures.ToList();
        // 设计器专属的那批挂点是攒着等首次进设计器才接的（#79），所以在主菜单看这里时它们还没接上，得说清楚。
        string waiting = Hooks.Pending > 0 ? $"（另有 {Hooks.Pending} 条设计器挂点还没到接的时候，进一次载具设计器就接上）" : "";
        ui.InfoField(failed.Count == 0
            ? $"SprocketTools 伸进游戏的 {Hooks.Attached} 条挂点全部接上{waiting}：一切按设计工作。"
            : $"SprocketTools 伸进游戏的挂点接上了 {Hooks.Attached} / {Hooks.Report.Count} 条{waiting}。下面这些挂点没接上的功能会停用，直到 SprocketTools 适配这一版游戏。", 2);
        ui.Header(failed.Count == 0 ? "没有缺失" : "没接上的挂点");
        foreach (var f in failed.Take(12)) ui.InfoField($"{f.Feature}：{f.Hook}。{f.Error}", 2, new Color(1f, 0.6f, 0.5f));
        if (failed.Count > 12) ui.InfoField($"……还有 {failed.Count - 12} 条（BepInEx\\LogOutput.log 里有全部）。", 1);
        ui.Header("关于");
        ui.InfoField("作者：七夜已逝。SprocketTools 与 Sprocket 中文补丁由同一人维护；装甲黎明（ArmorDawn）也是同一作者的项目。", 2);
        ui.InfoField("装甲黎明的音效/特效/火控/液气开关都在游戏设置页的「装甲黎明」一页里；工具包不再重复放一份。", 2);
    }

    static void Settings(IGUIElementDrawer ui)
    {
        var placeTip = new UITooltip("放回原位", "把快捷键框放回部件面板旁边。");
        ui.Button("把快捷键框放回原位", Ui.Callback(() => { if (Plugin.ShortcutsPosition != null) Plugin.ShortcutsPosition.Value = ""; }), ref placeTip);
        Toggle(ui, Plugin.ShowHotkeys, "手工制作的部件旁显示快捷键框", "列出编辑按键的那个框（它的按键也能开关）。");
        Toggle(ui, Plugin.PartMassMarkers, "部件质心标记", "编辑器的质心（COM）视图筛选开着时，每个部件画自己的蓝色质心菱形。");
        Toggle(ui, Plugin.MirrorMerge, "镜像合并", "编辑器的镜像开着时，合并（M）会连另一侧的镜像点一起合并。");
        Slider(ui, Plugin.RotationSnap, "旋转捕捉步进（度，0 用游戏自带）", 0, 45, 0.5f);
        Slider(ui, Plugin.ExplodeSpread, "爆炸视图距离（米）", 0.1f, 3, 0.05f);
        Slider(ui, Plugin.FlashlightPercent, "手电筒亮度（%）", 5, 300, 5);
        Slider(ui, Plugin.FullbrightPercent, "全亮亮度（%）", 1, 100, 1);
        if (Plugin.BackupsKept is { } backups)
        {
            var change = Ui.FloatCallback(v => backups.Value = (int)MathF.Round(v));
            keepFloats.Add(change);
            ui.Slider("保留的设计备份份数（0 全留）", backups.Value, 0, 500, change);
        }
        ui.Header("照片（F8）");
        Choice(ui, Plugin.PhotoResolution, "照片尺寸", PhotoOutput.Resolutions, "屏幕就是当前窗口原样；2K/4K/6K/8K 会重画或放大到该尺寸。");
        Choice(ui, Plugin.PhotoMethod, "拍摄方式", PhotoOutput.Methods, "渲染：按高分辨率重画，线条最准；放大：放大屏幕截图，最省显存。屏幕尺寸下无效。");
        ui.Header("1019 功能");
        Toggle(ui, CannonPool.Enabled, "炮口声源限流", "连发炮口声源超过上限时停掉最老的几条，避免打空游戏 audio pool、连发没声音。");
        Toggle(ui, PenetrationLimits.Enabled, "设计器上限放开", "放开设计器里 Penetration / Caliber 的逻辑上限与滑条上限。");
        Slider(ui, PenetrationLimits.MaxPenetration, "穿深逻辑上限", 1000, 100000, 1000);
        Slider(ui, PenetrationLimits.SliderMaxPenetration, "穿深滑条上限", 100, 20000, 100);
        Slider(ui, PenetrationLimits.MinCaliber, "口径下限（mm）", 0.1f, 100, 0.1f);
        Slider(ui, PenetrationLimits.MaxCaliber, "口径逻辑上限（mm）", 500, 100000, 500);
        Slider(ui, PenetrationLimits.SliderMaxCaliber, "口径滑条上限（mm）", 100, 10000, 100);
    }

    static void Toggle(IGUIElementDrawer ui, ConfigEntry<bool>? entry, string label, string tooltip)
    {
        if (entry == null) return;
        var change = Ui.BoolCallback(v => entry.Value = v);
        keepBools.Add(change);
        ui.ToggleField(label, entry.Value, change, tooltip);
    }

    static void Slider(IGUIElementDrawer ui, ConfigEntry<float>? entry, string label, float min, float max, float step)
    {
        if (entry == null) return;
        var change = Ui.FloatCallback(v => entry.Value = MathF.Round(v / step) * step);
        keepFloats.Add(change);
        ui.Slider(label, entry.Value, min, max, change);
    }

    static void Choice(IGUIElementDrawer ui, ConfigEntry<string>? entry, string label, string[] values, string tooltip)
    {
        if (entry == null) return;
        var tip = new UITooltip(label, tooltip);
        ui.Button($"{label}：{entry.Value}（点击更换）", Ui.Callback(() =>
        {
            int at = Array.IndexOf(values, entry.Value);
            entry.Value = values[(at + 1) % values.Length];
        }), ref tip);
    }
}
