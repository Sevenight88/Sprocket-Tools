using System;
using System.Collections.Generic;
using HarmonyLib;
using Sprocket.Vehicles;
using UnityEngine;

namespace SprocketTools;

/// 爆反起爆特效（**自造，不依赖任何美术资产**）：在这块板的弹孔外侧放一颗会膨胀的发光球 + 一盏点光闪，
/// 尺寸按这块板渲染器包围盒的最大边长给，默认 0.25 秒收干净。
///
/// 为什么不是克隆游戏自己的爆炸特效：`ExplosionEffects`／`LandmineDetonationEffect`／`Explosive.flameFX`
/// 三条都得先在场景里有一个实例才拿得到素材（interop 里类型都在，实例不保证），而 2026-10-07 用户定的口径是"直接自造"，
/// 所以这里只用最普通的运行时 API：`CreatePrimitive` ＋ `Light` ＋材质改色，零资产依赖、零游戏数据改动。
///
/// 球**不靠淡出**收尾：半透明着色器变体很可能没打进包（这包查得到的只有 `HDRP/Unlit`/`HDRP/Lit`），
/// 所以收场方式是"缓出膨胀到上限＋寿命一到当场销毁"，光靠强度衰减。用了哪个着色器会打进日志，验收一眼能看出来。
/// 2026-10-08 用户报"会出现一个橙色球然后消失"——根因就在这儿：旧版寿命走完后只把自己从名单里摘掉，
/// GameObject 要等出生时订的 1.5 秒兜底才没，于是那颗不透明的球在**最大尺寸**上多挂了 1.25 秒。
/// 现在 `Backstop` 收到 `Life+0.5`，并且驱动走完 `t>=1` 那一刻直接 `Destroy` 整个根节点。
///
/// 驱动挂在 `VehicleBehaviour.GameUpdate`（战斗与设计器里每辆车每帧都走，`VehicleLightSwitch` 已验过这条挂点活着）：
/// 一帧只走一次、名单空了立刻返回，不做任何场景扫描。另外每个特效出生那一刻就订了 `Destroy(go, 兜底秒)`，
/// 万一驱动那一轮没跑到，Unity 自己的计时器照样把它收掉，不会留残骸。
[HarmonyPatch]
public static class EraFx
{
    const float Life = 0.25f;              // 寿命（秒）：到点就**自己摘掉**，不留残影
    const float Backstop = Life + 0.5f;    // 兜底销毁只比寿命宽半秒（旧值 1.5s ⇒ 动画早停了、球还在场上挂 1.25s，用户看到的就是那颗"橙色球"）
    const int MaxLive = 32;                // 自己名单的上限：超了先忘最老的一条（残骸由兜底销毁负责收）
    const float FallbackSize = 0.4f;       // 板尺寸取不到时给这么大（一块标准爆反板）
    const float FlashShare = 8f;           // 闪光亮度 = 太阳的 8 倍（过曝一下）
    const float GrowStart = 0.3f;          // 出生时的直径比例
    const float GrowEnd = 1.2f;            // 收场时的直径比例（缓出：前半秒长得快、后半几乎停住）

    // 候选着色器，按顺序取第一个 `Shader.Find` 命中的。前两个是这包里查得到的（在 `Sprocket_Data\globalgamemanagers.assets`
    // 里 grep 到 `HDRP/Unlit`、`HDRP/Lit`），后三档是通用兜底。全落空就只剩点光闪，不会出品红或方框。
    static readonly string[] ShaderNames =
    {
        "HDRP/Unlit", "HDRP/Lit", "Sprites/Default", "Legacy Shaders/Particles/Additive", "UI/Default"
    };
    static Shader? shader;
    static bool shaderTried;
    static string shaderNote = "未取";

    sealed class Flash
    {
        public GameObject Go = null!;
        public Light? Lamp;
        public UnityEngine.Rendering.HighDefinition.HDAdditionalLightData? Hd;
        public float At;
        public float Size;
        public float Lux;
    }

    static readonly List<Flash> live = new();
    static int lastFrame = -1;
    static int logged;

    static bool Enabled() => Plugin.EraFxOn?.Value ?? true;
    static float Scale() => Mathf.Clamp(Plugin.EraFxScale?.Value ?? 1f, 0.05f, 8f);

    /// 起爆那一刻调用（在命中后贴里，主线程）。`plateSize` 是这块板渲染器包围盒最大边长（米），取不到给 0。
    public static void Play(Vector3 hole, Vector3 outward, float plateSize)
    {
        if (!Enabled()) return;
        float size = Mathf.Clamp((plateSize > 0.05f ? plateSize : FallbackSize) * Scale(), 0.12f, 6f);
        var f = new Flash { At = Time.unscaledTime, Size = size };
        try
        {
            var go = new GameObject("SprocketTools_EraFx");
            f.Go = go;
            go.transform.position = hole + outward.normalized * (size * 0.35f);   // 往外挪一点，免得整个埋进板子里

            var orb = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            orb.name = "SprocketTools_EraOrb";
            orb.transform.SetParent(go.transform, false);
            orb.transform.localScale = Vector3.one * GrowStart;
            // 原始图元自带碰撞体 —— 一定要摘掉，否则场上凭空多一颗能挡射线、能参与物理的球。
            var col = orb.GetComponent<Collider>();
            if (col != null) { col.enabled = false; UnityEngine.Object.Destroy(col); }
            var mr = orb.GetComponent<MeshRenderer>();
            var sh = PickShader();
            if (mr != null && sh != null)
            {
                var mat = new Material(sh);
                SetVec(mat, "_BaseColor", new Vector4(1f, 0.55f, 0.18f, 1f));
                SetVec(mat, "_EmissiveColor", new Vector4(9f, 3.4f, 0.7f, 1f));
                SetFloat(mat, "_EmissiveColorMode", 1f);
                try { mat.EnableKeyword("_EMISSION"); } catch (Exception) { }
                try { mat.color = new Color(1f, 0.55f, 0.18f, 1f); } catch (Exception) { }   // 内置着色器走 _Color
                mr.material = mat;
            }

            Lamp(go, size, f);
            if (live.Count >= MaxLive) live.RemoveAt(0);
            live.Add(f);
            UnityEngine.Object.Destroy(go, Backstop);
            if (logged++ < 8)
                Plugin.ModLog.LogInfo($"TOOL_ERA fx 起爆 尺寸={size:0.##}m 材质={shaderNote} " +
                    $"位置=({hole.x:0.#},{hole.y:0.#},{hole.z:0.#}) 在屏 {live.Count} 个");
        }
        catch (Exception ex)
        {
            if (f.Go != null) { try { UnityEngine.Object.Destroy(f.Go); } catch (Exception) { } }
            if (logged++ < 8) Plugin.ModLog.LogWarning($"TOOL_ERA fx 起爆失败：{(ex.InnerException ?? ex).GetType().Name} {ex.Message}");
        }
    }

    /// 点光闪。HDRP 的点光强度是 candela 量级，绝对数没意义 ⇒ 照 `Card` 那套补光的换算来：想让靶上亮多少 × 距离²。
    static void Lamp(GameObject root, float size, Flash f)
    {
        try
        {
            var go = new GameObject("SprocketTools_EraLight");
            go.transform.SetParent(root.transform, false);
            var lamp = go.AddComponent<Light>();
            lamp.type = LightType.Point;
            lamp.color = new Color(1f, 0.6f, 0.25f, 1f);
            lamp.range = size * 14f;
            lamp.shadows = LightShadows.None;
            float dist = Mathf.Max(1f, size * 3f);
            float want = SunLux() * FlashShare * dist * dist;
            var hd = lamp.GetComponent<UnityEngine.Rendering.HighDefinition.HDAdditionalLightData>();
            if (hd == null) hd = go.AddComponent<UnityEngine.Rendering.HighDefinition.HDAdditionalLightData>();
            f.Hd = hd;
            f.Lamp = lamp;
            f.Lux = want;
            if (hd != null) hd.SetIntensity(want, UnityEngine.Rendering.LightUnit.Candela);
            else lamp.intensity = want;
        }
        catch (Exception ex)
        {
            if (logged++ < 8) Plugin.ModLog.LogWarning($"TOOL_ERA 光闪没接上（只留球）：{(ex.InnerException ?? ex).GetType().Name} {ex.Message}");
        }
    }

    static float sunLux = -1f;
    static float sunAt = -1f;

    /// 场上最亮那盏平行光的亮度，当闪光的比例基准。每 5 秒重认一次 ⇒ 一次爆炸最多扫一遍灯，不是每帧取数。
    static float SunLux()
    {
        float now = Time.unscaledTime;
        if (sunLux > 0f && now - sunAt < 5f) return sunLux;
        sunAt = now;
        sunLux = 1000f;                                  // 认不到太阳时的起点（数量级够，不至于看不见）
        try
        {
            foreach (var l in UnityEngine.Object.FindObjectsOfType<Light>())
            {
                if (l == null || l.type != LightType.Directional) continue;
                float v = LuxOf(l);
                if (v > sunLux) sunLux = v;
            }
        }
        catch (Exception) { }
        return sunLux;
    }

    static float LuxOf(Light l)
    {
        try
        {
            var hd = l.GetComponent<UnityEngine.Rendering.HighDefinition.HDAdditionalLightData>();
            if (hd != null && hd.intensity > 0f) return hd.intensity;
        }
        catch (Exception) { }
        try { return l.intensity; } catch (Exception) { return 0f; }
    }

    static Shader? PickShader()
    {
        if (shaderTried) return shader;
        shaderTried = true;
        foreach (var name in ShaderNames)
        {
            try
            {
                var s = Shader.Find(name);
                if (s != null) { shader = s; shaderNote = name; return s; }
            }
            catch (Exception) { }
        }
        shaderNote = "无（只留光闪）";
        Plugin.ModLog.LogWarning("TOOL_ERA 没有可用的内置着色器 ⇒ 爆反特效退化为纯点光闪烁（不影响消失与挡弹判定）。");
        return null;
    }

    static void SetVec(Material m, string name, Vector4 v)
    {
        try { m.SetVector(name, v); } catch (Exception) { }
    }

    static void SetFloat(Material m, string name, float v)
    {
        try { m.SetFloat(name, v); } catch (Exception) { }
    }

    /// 收场：动画走完（或这一条已经管不动了）就立刻销毁整个根节点，连带那颗球与点光。
    static void Drop(Flash f)
    {
        try { if (f.Go != null) UnityEngine.Object.Destroy(f.Go); } catch (Exception) { }
    }

    /// 每帧驱动：长球、衰减光。挂点每辆车都会回调，所以用 `Time.frameCount` 认一次（与车灯那条同一个手法）。
    [HarmonyPostfix, HarmonyPatch(typeof(VehicleBehaviour), nameof(VehicleBehaviour.GameUpdate))]
    static void Driver(VehicleBehaviour __instance) => Ui.Guard("ERA blast fx", () =>
    {
        if (live.Count == 0) return;
        if (Time.frameCount == lastFrame) return;
        lastFrame = Time.frameCount;
        float now = Time.unscaledTime;
        for (int i = live.Count - 1; i >= 0; i--)
        {
            var f = live[i];
            float t = (now - f.At) / Life;
            if (t >= 1f) { live.RemoveAt(i); Drop(f); continue; }   // 动画走完就**当场销毁**（旧版只摘名单 ⇒ 球挂到兜底那一秒多才没）
            try
            {
                float e = 1f - (1f - t) * (1f - t);                 // 缓出：前半段冲开、后半段几乎停住，像爆轰而不是匀速放气球
                if (f.Go != null) f.Go.transform.localScale = Vector3.one * (f.Size * (GrowStart + (GrowEnd - GrowStart) * e));
                float fade = (1f - t) * (1f - t);
                if (f.Hd != null) f.Hd.SetIntensity(f.Lux * fade, UnityEngine.Rendering.LightUnit.Candela);
                else if (f.Lamp != null) f.Lamp.intensity = f.Lux * fade;
            }
            catch (Exception) { live.RemoveAt(i); Drop(f); }
        }
    });
}
