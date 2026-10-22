using HarmonyLib;
using Sprocket.Vehicles.PlateStructures;
using UnityEngine;
using UnityEngine.Rendering;

namespace SprocketTools;

/// 观察窗玻璃 → **玻璃灰**（`Glass look mode` 可切回透明路线）。
///
/// 根因：`armourTechID`（含我们的 `visionBlockGlass`）**只带弹道数字** —— `rhaFactor / spallFactor / density / costMultiplier`，
/// 没有一个"外观/透明度"字段；板的透明与否归 `PlateStructure.materialReference`（一个 Unity 材质资产）。
/// 所以"选了观察窗玻璃就该透明"从来没被实现过，不是设置没调对。
///
/// 做法：板材质装配好之后，若这块板的 `armourTechID` 是 `visionBlockGlass`，就按 `Glass look mode` 换掉它自己的渲染器材质。
/// **用 `Renderer.material` 取材质**：Unity 这一步会自动实例化一份**副本**，所以我们改的是这块板自己的那份，
/// 不会污染共享资产、也不会连累同车的其他板（这一点是必须的 —— 材质是共享的，直接改会全场变样）。
///
/// 挂点选 `PlateStructure.ApplyMaterials`（材质真正贴到渲染器上那一刻），补一条 `SetArmourMaterial` 兜底；
/// 两条路都幂等：认出自己那份材质（按名字）就直接跳过，不再新建实例。
///
/// 实机定论（2026-10-06 12:09）：`transparent` 那条路**材质确实换上了**（`TOOL_GLASS 板 281 换 …：3/3 个渲染器`），
/// 画面仍然不透明 ⇒ 本游戏 build 的 HDRP/Lit 没把透明变体打进来（shader stripping），运行时无 pass 可切。
/// 属性路线（只写 `_SurfaceType=1`）此前也已判死：VehicleLit 的 Surface Type 是**建图时的编译选项**；
/// 而且那块板的 `_BaseColour` 实测 RGBA(0,0,0,0) 却照画 ⇒ 这个属性多半没进图（真进了整车会是黑的），改它无效。
/// 所以默认走 `gray`：仍换成 HDRP/Lit（12:09 换上之后那块板仍看得见，只是没透明 ⇒ 换 shader 这条路本身走得通），
/// 但保持不透明，用它的 `_BaseColor`（HDRP/Lit 的真反照率）把玻璃涂成一眼可辨的玻璃灰。
[HarmonyPatch]
internal static class GlassLook
{
    /// 触发外观改造的材质 id。面板里那条「观察窗玻璃」写进 `armourTechID` 的就是它。
    internal const string GlassTech = "visionBlockGlass";

    /// 透明路线的 alpha（太小看不见板，太大不像玻璃）。实机已判死，留着给以后再试。
    const float Alpha = 0.35f;

    /// 玻璃灰：偏冷的浅灰，跟车体装甲的涂装一眼分得开。
    static readonly Color GrayColor = new(0.55f, 0.58f, 0.60f, 1f);

    const string GrayMatName = "SprocketTools 观察窗玻璃（灰）";
    const string TransparentMatName = "SprocketTools 观察窗玻璃（透明）";

    enum Mode { Off, Gray, Transparent }

    static bool Enabled() => Plugin.GlassTransparent?.Value ?? true;

    static int modeWarn;

    static Mode CurrentMode()
    {
        var raw = (Plugin.GlassLookMode?.Value ?? "gray").Trim().ToLowerInvariant();
        switch (raw)
        {
            case "off":
            case "none":
                return Mode.Off;
            case "transparent":
            case "hdrp":
                return Mode.Transparent;
            case "gray":
            case "grey":
                return Mode.Gray;
        }
        if (modeWarn++ < 1)
            Plugin.ModLog.LogWarning($"TOOL_GLASS `Glass look mode` 里的值「{raw}」不认识，按 gray 处理（可用：gray / transparent / off）");
        return Mode.Gray;
    }

    [HarmonyPostfix, HarmonyPatch(typeof(PlateStructure), "ApplyMaterials")]
    static void AfterApplyMaterials(PlateStructure __instance) => Ui.Guard("Glass look", () => Apply(__instance));

    internal static bool IsGlass(PlateStructure? plate) =>
        plate != null && string.Equals(plate.armourTechID, GlassTech, StringComparison.OrdinalIgnoreCase);

    [HarmonyPostfix, HarmonyPatch(typeof(PlateStructure), "SetArmourMaterial")]
    static void AfterSetArmourMaterial(PlateStructure __instance) => Apply(__instance);

    static void Apply(PlateStructure? plate) => Ui.Guard("Glass look", () =>
    {
        if (!Enabled() || !IsGlass(plate)) return;
        var mode = CurrentMode();
        if (mode == Mode.Off) return;
        int changed = 0, seen = 0;
        foreach (var r in plate!.GetComponentsInChildren<Renderer>(true))
        {
            if (r == null) continue;
            seen++;
            var m = r.material;                     // 自动实例化副本，这是关键
            if (m == null) continue;
            string marker = mode == Mode.Gray ? GrayMatName : TransparentMatName;
            if (m.name == marker) continue;         // 已经是我们的那份 ⇒ 幂等，不再新建实例
            if (SwapToLit(r, m, mode, marker)) changed++;
        }
        if (changed > 0)
            Plugin.ModLog.LogInfo($"TOOL_GLASS 板 {plate!.VehicleObject?.VUID} 换 HDRP/Lit（{(mode == Mode.Gray ? "玻璃灰" : "半透明")}）：{changed}/{seen} 个渲染器");
    });

    // ── 换成 HDRP/Lit 材质（两条外观路线共用一个函数，只差透明那几位）───────────────────────────────
    //
    // 为什么必须换 shader（联网调研 + 实跑证据）：
    //   车体材质是 Shader Graph / VehicleLit。在 Shader Graph 里，**Surface Type（Opaque/Transparent）是图级编译选项**，
    //   不是材质实例上的一个开关 —— 建图时选了 Opaque，就只编译了不透明 pass；材质上根本不存在透明 pass 可切。
    //   所以旧路线（运行时 SetFloat("_SurfaceType",1)）改不动它：属性写进去了，渲染管线却仍按不透明 pass 画。
    //
    // 三条候选路线的取舍：
    //   (a) 新建材质实例、整个换成 HDRP/Lit —— HDRP/Lit 是 HDRP 内置主着色器。✅ 唯一在运行期改得动外观的路。
    //   (b) 改 Shader Graph 资产的 Surface Type —— 需要在编辑器里改源资产并重编译 shader；打包后的游戏没有 AssetDatabase、
    //       没有 ShaderGraph API，运行期做不到。❌ 离线/打包后不可行。
    //   (c) MaterialPropertyBlock 覆盖 —— MPB 只能覆盖**逐实例属性**（颜色/贴图/浮点），改不了渲染状态
    //       （render queue / blend state / surface type）。❌ 对"透明"无效（对"灰"其实可行，但换材质更直观、已经验证画得出来）。
    //
    // 实机结果（2026-10-06 12:09，transparent 路线）：材质确实换上了、渲染器 3/3 全覆盖，画面**仍不透明**
    //   ⇒ 本游戏 build 里 HDRP/Lit 的透明变体被 shader stripping 裁了，keyword 切得动但没有对应的已编译变体。
    //   所以默认模式改成 gray：只要它的 `_BaseColor`（这个 shader 真用），不要它的透明。
    //   换 shader 本身是有效的（12:09 换上之后那块板仍看得见，只是没透明），灰这条才有得谈。
    static bool SwapToLit(Renderer r, Material src, Mode mode, string marker)
    {
        Shader? lit;
        try { lit = Shader.Find("HDRP/Lit"); }
        catch (Exception ex) { Plugin.ModLog.LogWarning($"TOOL_GLASS Shader.Find HDRP/Lit 抛异常：{ex.Message}"); return false; }
        if (lit == null)
        {
            // 只报一次这类失败，免得刷屏。
            if (swapFailLines++ < 3) Plugin.ModLog.LogWarning("TOOL_GLASS 找不到 HDRP/Lit shader（可能被 shader strip 裁掉）⇒ 本块板换材质失败");
            return false;
        }
        try
        {
            // 先拷一份原材质的全部属性，再换 shader —— Unity 会按同名属性尽量迁移。
            var m = new Material(src) { shader = lit, name = marker };
            // 手动迁移基色与贴图（VehicleLit 用英式 `_BaseColour`，HDRP/Lit 用美式 `_BaseColor`；换 shader 后不会自动过去）。
            Color baseCol = src.HasProperty("_BaseColour") ? src.GetColor("_BaseColour") :
                            src.HasProperty("_BaseColor") ? src.GetColor("_BaseColor") : Color.white;
            if (mode == Mode.Gray) baseCol = GrayColor;
            else baseCol.a = Alpha;
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", baseCol);
            if (mode == Mode.Gray)
            {
                // 灰模式刻意**不留贴图**：涂成平灰才一眼是玻璃；留着 `_BaseMap` 就还是原来的迷彩，只是暗了点。
                // 用白图而不是 null —— null 槽位取哪张默认纹理由 shader 声明决定，白图没有歧义。
                if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", Texture2D.whiteTexture);
            }
            else if (src.HasProperty("_BaseMap") && m.HasProperty("_BaseMap"))
            {
                var map = src.GetTexture("_BaseMap");
                if (map != null) m.SetTexture("_BaseMap", map);
            }
            if (mode == Mode.Transparent)
            {
                m.SetFloat("_SurfaceType", 1f);
                if (m.HasProperty("_BlendMode")) m.SetFloat("_BlendMode", 0f);
                if (m.HasProperty("_SrcBlend")) m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                if (m.HasProperty("_DstBlend")) m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                if (m.HasProperty("_ZWrite")) m.SetFloat("_ZWrite", 0f);
                if (m.HasProperty("_AlphaCutoffEnable")) m.SetFloat("_AlphaCutoffEnable", 0f);
                if (m.HasProperty("_TransparentZWrite")) m.SetFloat("_TransparentZWrite", 0f);
                m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                m.DisableKeyword("_SURFACE_TYPE_OPAQUE");
                m.renderQueue = (int)RenderQueue.Transparent;
            }
            else
            {
                // 玻璃灰：老老实实待在不透明队列（游戏自己的 Shadow 代理用的就是 2000）。
                m.SetFloat("_SurfaceType", 0f);
                if (m.HasProperty("_ZWrite")) m.SetFloat("_ZWrite", 1f);
                if (m.HasProperty("_AlphaCutoffEnable")) m.SetFloat("_AlphaCutoffEnable", 0f);
                m.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
                m.EnableKeyword("_SURFACE_TYPE_OPAQUE");
                m.renderQueue = (int)RenderQueue.Geometry;
            }
            // 直接换渲染器材质（r.material 已是实例副本，这里换成我们新建的 HDRP/Lit 实例，不碰共享资产）。
            r.material = m;
            return true;
        }
        catch (Exception ex)
        {
            if (swapFailLines++ < 3) Plugin.ModLog.LogWarning($"TOOL_GLASS 换 HDRP/Lit 失败（本条跳过）：{ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    static int swapFailLines;
}
