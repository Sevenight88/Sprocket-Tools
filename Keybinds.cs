using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SprocketTools;

/// SprocketTools 的全部按键，可在「游戏设置 → Mod Options」或 SprocketTools 自己的按键窗口里改，
/// 存在配置文件的 [Keybinds] 一节：写成 F9、P、Numpad1 这样的键名，需要修饰键就在前面加 Ctrl+、Shift+、Alt+。
/// 留空表示不绑键。id（配置里的键名）保持英文。
internal static class Keybinds
{
    internal sealed record Bind(string Id, string Group, string Name, string Default);

    internal static readonly Bind[] All =
    {
        new("menu", "SprocketTools", "按键与设置窗口", "Ctrl+K"),
        new("hotkeys", "面板", "显示或隐藏快捷键框", "F1"),
        new("explode", "间距", "爆炸视图", "F2"),
        new("explodeCloser", "间距", "爆炸视图：拉近", "F3"),
        new("explodeFurther", "间距", "爆炸视图：推远", "F4"),
        new("shadows", "光照", "阴影关闭 / 开启", "F5"),
        new("flashlight", "光照", "跟随鼠标的手电筒", "F6"),
        new("fullbright", "光照", "全亮", "F7"),
        new("lights", "光照", "车灯开 / 关", "L"),
        new("photo", "出图", "以最高画质拍照（拍照模式内）", "F8"),
        new("drawing", "出图", "工程图纸", "F9"),
        new("card", "出图", "资料卡出图（战争雷霆视角）", "F10"),
        new("obj", "模型", "OBJ 导出 / 导入菜单", "Ctrl+O"),
        new("front", "视图", "前视（Ctrl：后视）", "Numpad1"),
        new("side", "视图", "右侧视（Ctrl：左侧视）", "Numpad3"),
        new("top", "视图", "顶视（Ctrl：仰视）", "Numpad7"),
        new("opposite", "视图", "反向视图", "Numpad9"),
        new("ortho", "视图", "正交视图", "Numpad5"),
        new("zoomIn", "视图", "正交放大", "NumpadPlus"),
        new("zoomOut", "视图", "正交缩小", "NumpadMinus"),
        new("flatten", "网格", "展平", "N"),
        new("loopCut", "网格", "循环切割", "T"),
        new("inset", "网格", "内插面", "I"),
        new("bevel", "网格", "倒角", "V"),
        new("selectFlat", "选择", "选择同向相连面", "U"),
        new("proportional", "选择", "比例编辑", "O"),
        new("join", "附加部件", "合并到最后选中的那个", "Ctrl+J"),
    };

    static readonly Dictionary<string, ConfigEntry<string>> entries = new();
    static readonly Dictionary<string, (Key Key, bool Ctrl, bool Shift, bool Alt)> parsed = new();

    internal static void Load(ConfigFile config)
    {
        foreach (var b in All)
        {
            entries[b.Id] = config.Bind("Keybinds", b.Id, b.Default,
                $"{b.Group}：{b.Name}。写成 F9、P、Numpad1 这样的键名，需要时前面加 Ctrl+、Shift+、Alt+；留空表示不绑键。");
            parsed[b.Id] = Parse(entries[b.Id].Value);
        }
    }

    /// "Ctrl+Shift+J" -> J with Ctrl and Shift; an unknown key name -> no key.
    internal static (Key Key, bool Ctrl, bool Shift, bool Alt) Parse(string? text)
    {
        bool ctrl = false, shift = false, alt = false;
        var key = Key.None;
        foreach (var part in (text ?? "").Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)) ctrl = true;
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase)) shift = true;
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase)) alt = true;
            else if (Enum.TryParse<Key>(part, true, out var k)) key = k;
        }
        return (key, ctrl, shift, alt);
    }

    internal static string Format(Key key, bool ctrl, bool shift, bool alt) =>
        key == Key.None ? "" : $"{(ctrl ? "Ctrl+" : "")}{(shift ? "Shift+" : "")}{(alt ? "Alt+" : "")}{key}";

    /// The key as shown in panels and the hotkeys box ("Numpad 1", "Ctrl+J"), or "(none)".
    internal static string Shown(string id)
    {
        var text = entries.TryGetValue(id, out var e) ? e.Value : All.FirstOrDefault(b => b.Id == id)?.Default ?? "";
        if (string.IsNullOrWhiteSpace(text)) return "(none)";
        return text.Replace("NumpadPlus", "Numpad +").Replace("NumpadMinus", "Numpad -").Replace("Numpad", "Numpad ").Replace("Numpad  ", "Numpad ");
    }

    internal static void Set(string id, string text)
    {
        if (!entries.TryGetValue(id, out var e)) return;
        // 一键只能给一个功能：给新功能就从旧功能上摘掉，避免两个功能抢同一个键。
        var normalized = Format(Parse(text).Key, Parse(text).Ctrl, Parse(text).Shift, Parse(text).Alt);
        if (normalized.Length > 0)
            foreach (var other in All)
                if (other.Id != id && entries.TryGetValue(other.Id, out var oe))
                {
                    var p = Parse(oe.Value);
                    if (Format(p.Key, p.Ctrl, p.Shift, p.Alt) == normalized)
                    {
                        oe.Value = "";
                        parsed[other.Id] = Parse("");
                    }
                }
        e.Value = text;
        parsed[id] = Parse(text);
    }

    internal static void Reset(string id) { if (All.FirstOrDefault(b => b.Id == id) is { } b) Set(id, b.Default); }

    /// The action's key went down this frame, with the modifiers it needs held (others may be held too: tools that
    /// read Ctrl as "the other way" check it themselves).
    static string? synthetic; static int syntheticFrame;
    /// 右键菜单等我们自己面板里的“按一下”动作：下一帧当作该键被按了一次。
    internal static void Press(string id) { synthetic = id; syntheticFrame = Time.frameCount + 1; }

    internal static bool Pressed(string id)
    {
        if (synthetic == id && Time.frameCount == syntheticFrame) { synthetic = null; return true; }
        if (!parsed.TryGetValue(id, out var k) || k.Key == Key.None || Keyboard.current is not { } keys) return false;
        try
        {
            if (!keys[k.Key].wasPressedThisFrame) return false;
        }
        catch (Exception) { return false; } // a key this keyboard doesn't have
        return (!k.Ctrl || keys.ctrlKey.isPressed) && (!k.Shift || keys.shiftKey.isPressed) && (!k.Alt || keys.altKey.isPressed);
    }

    /// Whether the action's own binding asks for Ctrl (then Ctrl can't also mean "the other way").
    internal static bool NeedsCtrl(string id) => parsed.TryGetValue(id, out var k) && k.Ctrl;

    internal enum Captured { Nothing, Cancelled, Cleared, Set }

    static readonly Key[] Modifiers = { Key.LeftCtrl, Key.RightCtrl, Key.LeftShift, Key.RightShift, Key.LeftAlt, Key.RightAlt, Key.LeftMeta, Key.RightMeta, Key.ContextMenu };

    /// Waiting for a new key for `id`: Esc cancels, Backspace leaves it without a key, any other key (with the Ctrl,
    /// Shift and Alt held) becomes its key.
    internal static Captured Capture(string id, Keyboard keys)
    {
        if (keys.escapeKey.wasPressedThisFrame) return Captured.Cancelled;
        if (keys.backspaceKey.wasPressedThisFrame) { Set(id, ""); return Captured.Cleared; }
        foreach (Key k in Enum.GetValues(typeof(Key)))
        {
            if (k == Key.None || Modifiers.Contains(k)) continue;
            bool down;
            try { down = keys[k].wasPressedThisFrame; } catch (Exception) { continue; } // a key this keyboard doesn't have
            if (!down) continue;
            Set(id, Format(k, keys.ctrlKey.isPressed, keys.shiftKey.isPressed, keys.altKey.isPressed));
            Plugin.ModLog.LogInfo($"Keys: {id} is now {Shown(id)}");
            return Captured.Set;
        }
        return Captured.Nothing;
    }

    /// The actions whose keys are also another's (none counted twice for "no key").
    internal static HashSet<string> Shared() =>
        All.GroupBy(b => Shown(b.Id)).Where(g => g.Key != "(none)" && g.Count() > 1).SelectMany(g => g.Select(b => b.Id)).ToHashSet();
}
