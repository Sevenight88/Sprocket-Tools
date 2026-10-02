using HarmonyLib;
using Il2CppInterop.Runtime;
using Sprocket.Transformations;
using Sprocket.UI;
using Sprocket.Vehicles.PlateStructures.Design;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SprocketTools;

/// A "Hotkeys" box beside the panel while a hand-made structure is selected: the mesh editing keys, including the
/// ones the game's hint bar leaves out (box select, circle select, extrude...). The keys come from the game's live
/// bindings, so a rebound key shows as rebound. × closes it, F1 shows or hides it; it remembers which.
/// Also: while moving or scaling, Shift + an axis key locks to the other two axes (Shift + vertical = keep height),
/// using the game's own two-axis constraints, which it has no key for.
[HarmonyPatch]
public static class Hotkeys
{
    // One line per group: actions in the game's DesignerControls ("map/action") and what they do.
    static readonly (string Action, string Does)[][] Groups =
    {
        new[] { ("Core/Select", "选择"), ("Core/ExtendSelect", "扩展选择"), ("Core/GroupSelect", "组选择"), ("MeshEdit/LoopSelect", "循环选择") },
        new[] { ("MeshEdit/BoxSelect", "框选，拖拽"), ("MeshEdit/RadialSelect", "圆选，刷选") },
        new[] { ("MeshEdit/ToggleSelection", "全选 / 取消全选"), ("MeshEdit/InvertSelection", "反选") },
        new[] { ("Core/RotateMode", "旋转圆环：拖动圆环"), ("Core/Rotate", "直接旋转") },
        new[] { ("Core/Move", "移动"), ("Core/Resize", "缩放"), ("Core/RotateSingleAxis", "绕单一轴转动") },
        new[] { ("Core/LateralConstraint", "横向锁定"), ("Core/LongitudinalConstraint", "纵向"), ("Core/VerticalConstraint", "垂向") },
        new[] { ("Core/SnapModifier", "按住：捕捉"), ("Core/HighPrecision", "按住：微调"), ("Core/ToggleRotationSnap", "旋转捕捉") },
        new[] { ("MeshEdit/Extend", "扩展"), ("MeshEdit/Fill", "填充"), ("MeshEdit/Merge", "合并点"), ("MeshEdit/Split", "分割") },
        new[] { ("MeshEdit/Slope", "倾斜"), ("MeshEdit/Duplicate", "复制"), ("MeshEdit/Flip", "翻转面") },
        new[] { ("MeshEdit/Delete", "删除"), ("Core/Undo", "撤销"), ("Core/Redo", "重做") },
        new[] { ("Core/EnableExteriorView", "外部视图"), ("Core/EnableInteriorView", "内部视图"), ("Core/EnableArmourView", "装甲视图") },
        new[] { ("Core/Focus", "对焦"), ("Core/CycleModule", "切换标签页"), ("Core/Save", "保存") },
    };

    static InputActionAsset? controls;
    static PlateStructureEditor? structure; // the hand-made structure whose panel was drawn last

    /// The hand-made structure being edited (its part selected), or null.
    internal static PlateStructureEditor? Current => StructureSelected() ? structure : null;
    static List<string>? lines;
    /// 别的 mod（本仓库内的部件剪贴板）往这张表里追加的按键行，装载时登记一次。空着就是不装那个 mod。
    internal static readonly List<string> Extra = new();
    static float[]? heights; // what each of those lines takes to draw without clipping
    static bool measured;
    static int checkedFrame = -1;
    static bool visible;

    static (InputAction? Action, ConstraintMode Mode)[]? axisKeys;
    static IntPtr planeFor;          // the move or scale that Shift + axis locked to two axes
    static ConstraintMode planeMode;

    [HarmonyPostfix, HarmonyPatch(typeof(Transformation), nameof(Transformation.Update))]
    static void PlaneLock(Transformation __instance) => Ui.Guard("Axis lock", () =>
    {
        controls ??= Find();
        if (controls == null || __instance.input?.TryCast<WindingInput>() != null) return; // rotating: two axes mean nothing
        // The game's three axis keys, looked up once (this runs every frame of a move).
        axisKeys ??= new[] { ("Core/LateralConstraint", Constraints.Axis12), ("Core/VerticalConstraint", Constraints.Axis20), ("Core/LongitudinalConstraint", Constraints.Axis01) }
            .Select(k => ((InputAction?)controls.FindAction(k.Item1, false), k.Item2)).ToArray();
        bool shift = Keyboard.current?.shiftKey.isPressed == true;
        // Shift + an axis key: every axis but that one. The key alone: the game's own one-axis lock again.
        foreach (var (action, mode) in axisKeys)
            if (action?.WasPressedThisFrame() == true)
            {
                planeFor = shift ? __instance.Pointer : IntPtr.Zero;
                planeMode = mode;
            }
        // Kept every frame, in case the game's own handling of the same key press set its one-axis lock after us.
        if (planeFor == __instance.Pointer && __instance.ConstraintMode != planeMode) __instance.ConstraintMode = planeMode;
    });

    [HarmonyPostfix, HarmonyPatch(typeof(Transformation), nameof(Transformation.TransformEnd))]
    static void TransformDone() => planeFor = IntPtr.Zero;

    [HarmonyPostfix, HarmonyPatch(typeof(PlateStructureEditor), nameof(PlateStructureEditor.OnGUI))]
    static void Seen(PlateStructureEditor __instance) => Ui.Guard("Hotkeys", () =>
    {
        if (__instance.TryCast<FreeformPlateStructureEditor>() == null) return; // mesh editing is freeform only
        structure = __instance;
        lines = null; // read the keys again, in case they were rebound
    });

    static Rect closeRect; // where the × was drawn last (from the screen's top left); empty while the box is hidden

    // A label style of our own: the game's has no word wrap, so our longest lines ran past the right edge. Wrapping is
    // also what lets CalcHeight hand back the real height of a line (a wrapped one counts as two). The skin it was
    // copied from is remembered by pointer, since the game can rebuild its skin and our copy would keep the old face.
    static GUIStyle? wrapped;
    static IntPtr wrappedSkin = IntPtr.Zero;

    static GUIStyle Style
    {
        get
        {
            var skin = GUI.skin;
            if (wrapped == null || skin.Pointer != wrappedSkin)
            {
                wrappedSkin = skin.Pointer;
                wrapped = new GUIStyle(skin.label) { wordWrap = true };
            }
            return wrapped;
        }
    }

    internal static void DrawBox()
    {
        if (!(Plugin.ShowHotkeys?.Value ?? true) || !StructureSelected()) { closeRect = default; return; }
        if (lines == null) { lines = Lines(); heights = null; } // read the keys again, and measure the new lines
        if (lines.Count == 0) { closeRect = default; return; }
        // Left of the game's panel (about the right 23% of the screen), under the top bar.
        const float width = 470, margin = 10;
        // 16 px Chinese glyphs need over 20 px or they lose their bottoms, and 20 px was the line height the box used;
        // ask the style what each line actually takes, and lay the box out from that.
        heights ??= Heights(Style, width - margin * 2);
        var box = new Rect(Screen.width * 0.765f - width - 12, 90, width, 26 + heights.Sum() + margin);
        GUI.Box(box, "快捷键  (F1 显示/隐藏)");
        closeRect = new Rect(box.xMax - 26, box.y + 3, 22, 20);
        GUI.Box(closeRect, "×");
        float y = box.y + 26;
        for (int i = 0; i < lines.Count; i++)
        {
            GUI.Label(new Rect(box.x + margin, y, width - margin * 2, heights[i]), lines[i]);
            y += heights[i];
        }
    }

    /// What every line needs, in pixels: measured, plus a little air, and never less than 22 (the floor is what fixes
    /// the clipped bottoms in case the measurement ever comes back short).
    static float[] Heights(GUIStyle style, float atWidth)
    {
        const float floor = 22, air = 5;
        var result = new float[lines!.Count];
        for (int i = 0; i < result.Length; i++) result[i] = MathF.Max(floor, style.CalcHeight(new GUIContent(lines[i]), atWidth)) + air;
        if (!measured)
        {
            measured = true;
            Plugin.ModLog.LogInfo($"TOOL_FONT box lines {result.Min():0.#}..{result.Max():0.#} px over {result.Length} lines (was a flat 20)");
        }
        return result;
    }

    /// From DesignEditor.Update: F1 and a click on the ×, read from the keyboard and mouse themselves (the simple
    /// on-screen GUI's own key and button events don't reach mods in this game).
    internal static void Keys()
    {
        if (Keyboard.current?.f1Key.wasPressedThisFrame == true) Show(!(Plugin.ShowHotkeys?.Value ?? true));
        if (closeRect.width > 0 && Mouse.current is { } mouse && mouse.leftButton.wasPressedThisFrame)
        {
            var p = mouse.position.ReadValue();
            if (closeRect.Contains(new Vector2(p.x, Screen.height - p.y))) Show(false);
        }
    }

    static void Show(bool show)
    {
        if (Plugin.ShowHotkeys != null) Plugin.ShowHotkeys.Value = show;
    }

    /// Is that structure still selected? Checked once a frame (the GUI is drawn several times a frame).
    static bool StructureSelected()
    {
        if (checkedFrame == Time.frameCount) return visible;
        checkedFrame = Time.frameCount;
        try { visible = structure != null && DesignEditor.Instance?.SelectedParts().Contains((int)structure.Component.VehicleObject.VUID) == true; }
        catch { visible = false; structure = null; } // its part is gone
        return visible;
    }

    static List<string> Lines()
    {
        var found = new List<string>();
        controls ??= Find();
        if (controls == null) return found;
        foreach (var group in Groups)
        {
            var parts = new List<string>();
            foreach (var (name, does) in group)
                if (controls.FindAction(name, false) is { } action &&
                    InputActionRebindingExtensions.GetBindingDisplayString(action, default(InputBinding.DisplayStringOptions), (string?)null) is { Length: > 0 } key)
                    parts.Add($"{key.Replace("Control", "Ctrl")}  {does}");
            if (parts.Count > 0) found.Add(string.Join("     ", parts));
        }
        if (controls.FindAction("Core/VerticalConstraint", false) is { } vertical &&
            InputActionRebindingExtensions.GetBindingDisplayString(vertical, default(InputBinding.DisplayStringOptions), (string?)null) is { Length: > 0 } up)
            found.Insert(5, $"Shift+{up}  移动/缩放时不改高度   [mod]");
        found.Add("Ctrl+J  将所选附加部件合并到最后选中的那个   [mod]");
        // 另一个程序集（部件剪贴板）装载时交过来的按键行：它自己画不了这个框，这里的行不来自游戏的绑定表。
        foreach (var line in Extra) found.Add(line);
        found.Add("N  展平     T  循环切割（需选边）     I  内插面     V  倒角（需选边）   [mod]");
        found.Add("O  比例编辑     U  选择同向相连面   [mod]");
        found.Add("小键盘5  正交视图     小键盘 +/-  正交缩放   [mod]");
        found.Add("小键盘1 / 3 / 7  前视 / 侧视 / 顶视     小键盘9  反向（底部、后部……）   [mod]");
        found.Add("F2  爆炸视图     F3 / F4  拉近 / 推远   [mod]");
        found.Add("F5  阴影关闭/开启     F6  跟随鼠标的手电筒     F7  全亮   [mod]");
        found.Add("F8（拍照模式）  以最高画质设置拍照     F9  工程图纸   [mod]");
        found.Add("F10  战争雷霆视角资料卡（白底）   [mod]");
        return found;
    }

    /// The editor's control set (the game builds it from its DesignerControls layout).
    static InputActionAsset? Find()
    {
        foreach (var o in UnityEngine.Resources.FindObjectsOfTypeAll(Il2CppType.Of<InputActionAsset>()))
            if (o.TryCast<InputActionAsset>() is { } asset && asset.FindActionMap("MeshEdit", false) != null) return asset;
        return null;
    }
}
