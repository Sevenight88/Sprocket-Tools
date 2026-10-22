using Sprocket.UI;
using Sprocket.Vehicles.PlateStructures.Design;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SprocketTools;

/// Ctrl + 右键（不拖动）在编辑器里打开的动作菜单。只放工具包自己的动作：
/// 原点、散落、合并、镜像实体、布料、网格工具；不改游戏输入，不替游戏按键。
public static class RightClickMenu
{
    sealed record Item(string Label, string Key, Action Run);
    sealed record Placed(Rect Rect, Item? Item, string? Title);

    static List<Placed>? placed;
    static Rect box;
    static Vector2? pressedAt;
    static float pressedTime;
    static GUIStyle? labelStyle, keyStyle, titleStyle;
    const float Row = 22, Width = 300;

    internal static bool Open => placed != null;
    internal static void Close() { placed = null; pressedAt = null; }

    /// 从 DesignEditor.Update 调用：Ctrl + 右键按下后未拖动，打开；点击项执行；Esc / 再右键 / 点别处关闭。
    internal static void Update()
    {
        if (Mouse.current is not { } mouse) return;
        var p = mouse.position.ReadValue();
        var at = new Vector2(p.x, Screen.height - p.y);
        if (placed != null)
        {
            InputShield.Claim();
            if (Keyboard.current?.escapeKey.wasPressedThisFrame == true || mouse.rightButton.wasPressedThisFrame) { Close(); return; }
            if (!mouse.leftButton.wasPressedThisFrame) return;
            var item = placed.FirstOrDefault(x => x.Item != null && x.Rect.Contains(at))?.Item;
            Close();
            if (item == null) return;
            Plugin.ModLog.LogInfo($"右键菜单：{item.Label}");
            try { item.Run(); }
            catch (Exception e) { Plugin.ModLog.LogWarning($"右键菜单 {item.Label} 失败：{e}"); }
            return;
        }
        if (mouse.rightButton.wasPressedThisFrame)
        {
            pressedAt = Keyboard.current?.ctrlKey.isPressed == true ? at : null;
            pressedTime = Time.unscaledTime;
        }
        if (pressedAt is not { } from || !mouse.rightButton.wasReleasedThisFrame) return;
        pressedAt = null;
        bool click = (at - from).magnitude < 6 && Time.unscaledTime - pressedTime < 0.35f;
        if (!click || MeshTools.Typing() || UnityEngine.EventSystems.EventSystem.current?.IsPointerOverGameObject() == true) return;
        if (Build() is { Count: > 0 } menu) Place(menu, at);
    }

    sealed record Selection(DesignEditor Editor, List<int> Picked, int Active, List<int> Addons, PlateStructureEditor? Shape);

    sealed record Entry(string Section, string Label, Func<string> Key, Func<Selection, bool> When, Action<Selection> Run);

    static string K(string id) => Keybinds.Shown(id) is var k && k != "(none)" ? k : "";
    static bool Part(Selection s) => s.Picked.Count > 0;
    static bool Addon(Selection s) => s.Picked.Count > 0 && s.Addons.Contains(s.Active);
    static bool Shape(Selection s) => s.Shape != null;
    static bool Always(Selection s) => true;

    static readonly Entry[] Entries =
    {
        new("对象", "把原点移到形状中心", () => "", s => s.Picked.Count == 1 && s.Shape != null, s =>
            s.Editor.RequestLiveEdit("正在移动原点", "原点已移到形状中心。可用“恢复”撤销。",
                json => AddonEdits.PlanOrigin(json, s.Active))),
        new("对象", "把选中的附加件散落到本件", () => "", s => Part(s) && s.Addons.Any(v => v != s.Active),
            s => ShapeTools.Scatter(s.Active, s.Addons.Where(v => v != s.Active).ToList())),
        new("对象", "合并选中的附加件", () => K("join"), s => Addon(s) && s.Addons.Count > 1, _ => Keybinds.Press("join")),
        new("附加件", "把镜像侧变成独立件", () => "", Addon, s => ShapeTools.RealMirror(s.Active)),
        new("附加件", "披挂为布料（按面板设置）", () => "", Addon, s => ShapeTools.Drape(s.Active)),
        new("网格", "平滑边", () => "", Shape, s => MeshTools.MenuSmoothEdge(s.Shape!)),
        new("网格", "圆角", () => "", Shape, s => MeshTools.MenuFillet(s.Shape!)),
        new("网格", "桥接选中边", () => "", Shape, s => MeshTools.MenuBridge(s.Shape!)),
        new("网格", "把选中点排成圆", () => "", Shape, s => MeshTools.MenuCircle(s.Shape!)),
        new("网格", "修镜像", () => "", Shape, s => MeshTools.MenuFixMirror(s.Shape!)),
        new("视图", "前视", () => K("front"), Always, _ => Keybinds.Press("front")),
        new("视图", "侧视", () => K("side"), Always, _ => Keybinds.Press("side")),
        new("视图", "顶视", () => K("top"), Always, _ => Keybinds.Press("top")),
        new("视图", "正交视图", () => K("ortho"), Always, _ => Keybinds.Press("ortho")),
        new("视图", "爆炸视图", () => K("explode"), Always, _ => Keybinds.Press("explode")),
        new("视图", "阴影开关", () => K("shadows"), Always, _ => Keybinds.Press("shadows")),
        new("视图", "全亮开关", () => K("fullbright"), Always, _ => Keybinds.Press("fullbright")),
        new("视图", "工程图纸", () => K("drawing"), Always, _ => Keybinds.Press("drawing")),
        new("视图", "OBJ 导出 / 导入", () => K("obj"), Always, _ => Keybinds.Press("obj")),
    };

    static List<(string Title, List<Item> Items)> Build()
    {
        if (DesignEditor.Instance is not { } editor) return new();
        var picked = editor.SelectedParts();
        int active = picked.Count > 0 ? picked[^1] : -1;
        var s = new Selection(editor, picked, active, editor.SelectedParts(Conversion.AddonGuid), Hotkeys.Current);
        return Entries.Where(e => e.When(s)).GroupBy(e => e.Section)
            .Select(g => (g.Key, g.Select(e => new Item(e.Label, e.Key(), () => e.Run(s))).ToList())).ToList();
    }

    static void Place(List<(string Title, List<Item> Items)> menu, Vector2 at)
    {
        var rows = new List<Placed>();
        float x = 0, y = 0, bottom = 0, limit = Math.Max(Row * 4, Screen.height - 16);
        foreach (var (title, items) in menu)
        {
            if (y > 0 && y + Row * (items.Count + 1) > limit) { x += Width; y = 0; }
            rows.Add(new(new Rect(x, y, Width, Row), null, title)); y += Row;
            foreach (var item in items) { rows.Add(new(new Rect(x, y, Width, Row), item, null)); y += Row; }
            bottom = Math.Max(bottom, y);
        }
        float width = x + Width, left = Math.Clamp(at.x, 0, Math.Max(0, Screen.width - width)), top = Math.Clamp(at.y, 0, Math.Max(0, Screen.height - bottom));
        box = new Rect(left, top, width, bottom);
        placed = rows.Select(r => r with { Rect = new Rect(r.Rect.x + left, r.Rect.y + top, r.Rect.width, r.Rect.height) }).ToList();
    }

    internal static void Draw()
    {
        if (placed == null) return;
        labelStyle ??= new GUIStyle(GUI.skin.label) { fontSize = 13, alignment = TextAnchor.MiddleLeft };
        if (keyStyle == null) { keyStyle = new GUIStyle(labelStyle) { alignment = TextAnchor.MiddleRight }; keyStyle.normal.textColor = new Color(0.68f, 0.68f, 0.68f); }
        titleStyle ??= new GUIStyle(labelStyle) { fontStyle = FontStyle.Bold };
        GUI.Box(box, ""); GUI.Box(box, "");
        var m = Mouse.current?.position.ReadValue() ?? Vector2.zero;
        var mouse = new Vector2(m.x, Screen.height - m.y);
        foreach (var row in placed)
        {
            var r = row.Rect;
            if (row.Title != null) { GUI.Label(new Rect(r.x + 8, r.y, r.width - 16, r.height), row.Title, titleStyle); continue; }
            if (r.Contains(mouse)) GUI.Box(r, "");
            GUI.Label(new Rect(r.x + 18, r.y, r.width - 28, r.height), row.Item!.Label, labelStyle);
            if (row.Item.Key.Length > 0) GUI.Label(new Rect(r.x + 18, r.y, r.width - 28, r.height), row.Item.Key, keyStyle);
        }
    }
}
