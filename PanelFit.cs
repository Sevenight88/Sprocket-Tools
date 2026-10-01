using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SprocketTools;

/// An inspector row is a ListLayout.Element: the game keeps its height in a heightSource delegate, and the row's
/// label is a child rect whose width the prefab decided. Both are sized for English. Chinese is about twice as wide
/// and wraps to more lines, so labels get cut off mid-glyph and multi-line descriptions get overlapped by the next
/// row. Instead of shortening the wording, ask TextMeshPro what the text needs with the font actually in use, and
/// hand the layout those numbers.
///
/// Every measurement is logged once as TOOL_FIT. This is the calibration probe as much as the fix: read the numbers
/// from one game run, then decide what stays.
internal static class PanelFit
{
    const float WidthMargin = 3f;  // air between a label and whatever sits to its right
    const float HeightMargin = 4f; // a fitted description box gets some bottom padding
    const float LabelCap = 0.55f;  // a label may never take more than this share of its row
    const float FullWidth = 0.8f;  // at this share of the row the text is a button or a heading, not a label

    static bool busy;
    static bool swept;
    static float next;
    static int layouts, rows, fields, taller, labels, blocked;

    static readonly Dictionary<long, List<(RectTransform rect, Vector2 min, Vector2 max)>> saved = new();
    static readonly Dictionary<long, (Il2CppSystem.Func<float> keep, IntPtr source, float from, float to)> fitted = new();
    static readonly HashSet<string> logged = new();

    [HarmonyPatch(typeof(DynamicGUI.DynamicGUILayout), nameof(DynamicGUI.DynamicGUILayout.Apply))]
    static void AfterApply(DynamicGUI.DynamicGUILayout __instance) => Fit(__instance);

    [HarmonyPatch(typeof(DynamicGUI.DynamicGUILayout), nameof(DynamicGUI.DynamicGUILayout.Refresh))]
    static void AfterRefresh(DynamicGUI.DynamicGUILayout __instance) => Fit(__instance);

    /// The patches above are the fast path, but a panel that never calls them would then never be fitted: the editor
    /// pump re-runs the pass over whatever panels are on screen, so a missing hook shows up as a delay, not a bug.
    /// Its first round also reports what the pass found, so a run that changes nothing still says why.
    internal static void Tick()
    {
        if (Time.unscaledTime < next) return;
        next = Time.unscaledTime + 0.25f;
        var panels = UnityEngine.Object.FindObjectsOfType<DynamicGUI.DynamicGUILayout>();
        if (!swept) { layouts = rows = fields = taller = labels = blocked = 0; }
        foreach (var panel in panels) Fit(panel);
        if (swept) return;
        swept = true;
        Plugin.ModLog.LogInfo($"TOOL_FIT sweep: {panels.Length} panels, {layouts} layouts, {rows} rows, {fields} label fields, " +
                              $"{taller} grown, {labels} labels wanting room ({blocked} of them capped)");
    }

    static void Fit(DynamicGUI.DynamicGUILayout layout) => Ui.Guard("Panel fit", () =>
    {
        if (busy) return;
        if (layout.root == null) { Log("warn a panel's root layout is null"); return; }
        var found = layout.root.GetComponentsInChildren<ListLayout>(true);
        busy = true;
        try
        {
            // Deepest first: a dropdown's own layout has to settle before its parent measures it.
            layouts += found.Length;
            for (var i = found.Length - 1; i >= 0; i--) Pass(found[i]);
        }
        finally { busy = false; }
    });


    static void Pass(ListLayout list)
    {
        if (list.layout == null) return;
        var touched = false;
        foreach (var element in list.layout)
        {
            if (element?.transform == null) continue;
            var row = element.transform;
            rows++;
            Restore(row);
            touched |= Description(element, row);
            touched |= Labels(row);
        }
        if (touched) list.ForceRelayout();
    }

    // ---------- descriptions: give the box the height its text really needs ----------

    static bool Description(ListLayout.Element element, RectTransform row)
    {
        if (row.GetComponent<DynamicGUI.LabelField>() is not { } field) return false;
        fields++;
        var text = field.TextBox;
        if (text == null || string.IsNullOrEmpty(text.text)) return false;
        var box = text.rectTransform.rect;
        var needed = text.GetPreferredValues(text.text, box.width, 0f).y;
        if (needed <= box.height + 0.5f) return false;
        taller++;
        // The row alone isn't enough: the text keeps the rect the prefab gave it and clips inside a row that has grown
        // round it, so the box itself gets the height its text measured. On sizeDelta rather than the rect, so it works
        // whether the box is anchored by its corners or stretched to the row (`SetSizeWithCurrentAnchors`'s Axis enum
        // isn't reachable through this interop).
        var frame = text.rectTransform;
        frame.sizeDelta = new Vector2(frame.sizeDelta.x, frame.sizeDelta.y + needed + HeightMargin - frame.rect.height);
        var key = Key(row);
        fitted.TryGetValue(key, out var was);
        var source = element.heightSource;
        // A row already fitted once and short again means the game put the box back after us: worth its own line.
        if (source != null && source.Pointer == was.source)
            Log($"info 回弹 width={box.width:F0} 框={box.height:F0} 需={needed:F0} text={Clip(text.text)}");
        // What the game asked for. A heightSource of ours from an earlier pass is already fitted, so keep the
        // original that was stored with it rather than growing the box again on top of it.
        var from = source == null ? element.height : source.Pointer == was.source ? was.from : source.Invoke();
        var to = from + (needed - box.height) + HeightMargin;
        element.height = to;
        if (source == null || source.Pointer != was.source || Math.Abs(was.to - to) > 0.01f)
        {
            if (DelegateSupport.ConvertDelegate<Il2CppSystem.Func<float>>(new Func<float>(() => to)) is not { } del) return true;
            element.heightSource = del;
            fitted[key] = (del, del.Pointer, from, to);
        }
        Log($"info width={box.width:F0} reserved={box.height:F0} needed={needed:F0} from={from:F0} to={to:F0} text={Clip(text.text)}");
        return true;
    }

    // ---------- labels: widen the left column, taking the room from the control beside it ----------

    static bool Labels(RectTransform row)
    {
        var changed = false;
        // Transform's enumerator hands back boxed IL2CPP objects, which the interop cast rejects: walk the children by index.
        for (var i = 0; i < row.childCount; i++)
        {
            var child = row.GetChild(i);
            if (child.TryCast<RectTransform>() is not { } rect) continue;
            if (child.GetComponent<TMP_Text>() is not { } text || string.IsNullOrEmpty(text.text)) continue;
            var own = rect.rect;
            if (own.width >= row.rect.width * FullWidth) continue; // it wraps on its own line: not a column label
            var needed = text.GetPreferredValues(text.text, 100000f, 100000f).x + WidthMargin;
            if (needed <= own.width + 0.5f) continue;
            labels++;
            var right = Right(rect);
            var movers = new List<RectTransform>();
            for (var s = 0; s < row.childCount; s++)
            {
                var sibling = row.GetChild(s);
                if (sibling.Pointer == child.Pointer) continue;
                if (sibling.TryCast<RectTransform>() is not { } other) continue;
                if (Left(other) >= right - 1f) movers.Add(other);
            }
            var widen = Math.Min(needed - own.width, row.rect.width * LabelCap - own.width);
            if (widen <= 0.5f || movers.Count == 0)
            {
                blocked++;
                Log($"label width={row.rect.width:F0} have={own.width:F0} need={needed:F0} blocked text={Clip(text.text)}");
                continue;
            }
            Note(row, rect);
            foreach (var m in movers) Note(row, m);
            rect.offsetMax = new Vector2(rect.offsetMax.x + widen, rect.offsetMax.y);
            foreach (var m in movers) m.offsetMin = new Vector2(m.offsetMin.x + widen, m.offsetMin.y);
            text.SetAllDirty();
            Log($"label width={row.rect.width:F0} have={own.width:F0} need={needed:F0} widen={widen:F0} moved={movers.Count} text={Clip(text.text)}");
            changed = true;
        }
        return changed;
    }

    // ---------- the row's rects, put back the way the prefab had them ----------

    static void Restore(RectTransform row)
    {
        if (!saved.Remove(Key(row), out var list)) return;
        foreach (var (rect, min, max) in list) { rect.offsetMin = min; rect.offsetMax = max; }
    }

    static void Note(RectTransform row, RectTransform rect)
    {
        var key = Key(row);
        if (!saved.TryGetValue(key, out var list)) saved[key] = list = new List<(RectTransform, Vector2, Vector2)>();
        if (!list.Any(e => e.Item1.Pointer == rect.Pointer)) list.Add((rect, rect.offsetMin, rect.offsetMax));
    }

    /// x of one of a child's edges, in the parent's own space: the anchor point plus the offset measured from it.
    /// Adding to offsetMin / offsetMax moves that edge however the anchors are set up.
    static float Left(RectTransform r) => Anchor(r, r.anchorMin) + r.offsetMin.x;
    static float Right(RectTransform r) => Anchor(r, r.anchorMax) + r.offsetMax.x;

    static float Anchor(RectTransform r, Vector2 anchor) =>
        r.parent?.TryCast<RectTransform>() is not { } parent ? 0f : parent.rect.x + parent.rect.width * anchor.x;


    static long Key(Il2CppObjectBase o) => o.Pointer.ToInt64();

    static void Log(string what)
    {
        if (logged.Add(what)) Plugin.ModLog.LogInfo($"TOOL_FIT {what}");
        if (logged.Count > 4000) logged.Clear();
    }

    static string Clip(string s)
    {
        var one = s.Replace('\n', '/').Replace('\r', '/');
        return one.Length <= 30 ? one : one[..30] + "…";
    }
}
