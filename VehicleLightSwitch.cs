using HarmonyLib;
using Sprocket.Vehicles;
using UnityEngine;

namespace SprocketTools;

/// 车灯开关（**按 L**）。
///
/// 为什么走这条路：官方灯部件（`headlightPart` / `tailLightPart`，tags `light,headLight,crewEquipment` / `light,tailLight,crewEquipment`）
/// **只声明了 `model` 组件，没有 `light` 组件** —— 而 `VehicleLight` 那个组件是由 `VehicleLightComponentFactory`
/// 从部件定义里造出来的。缺了这条声明，游戏建 `VehicleLights` 模块时那个 `ILight[]` 就是空的
/// （探针实测 `灯数=0`），所以车灯从来点不亮。游戏自己的 `ToggleLights` 输入在 `PartTags.txt` 与本地化里**都找不到登记**，
/// 很可能压根没绑键。
///
/// 这里的做法：**不去跟游戏的模块较劲**，直接驱动灯部件模型底下**本来就存在**的 `Light` 对象
/// （`AddComponent<Light>` 那条路先前已判死：光源对象本来就有，缺的是接线）。好处是零风险 —— 不动游戏数据、不动存档。
/// 头灯按 `headLight` 标签配成白色聚光、尾灯按 `tailLight` 配成红色点光。
///
/// 挂点用 `VehicleBehaviour.GameUpdate`：战斗与设计器里每辆车每帧都会走，所以两个场景都能开灯；
/// 一帧只处理一次按键（场上可能有好几辆车）。
[HarmonyPatch]
internal static class VehicleLightSwitch
{
    internal static bool On;

    static int lastFrame = -1;
    static readonly Dictionary<IntPtr, Light[]> cache = new();

    static bool Enabled() => Plugin.LightSwitch?.Value ?? true;

    [HarmonyPostfix, HarmonyPatch(typeof(VehicleBehaviour), nameof(VehicleBehaviour.GameUpdate))]
    static void Poll(VehicleBehaviour __instance) => Ui.Guard("Vehicle lights", () =>
    {
        if (!Enabled()) return;
        if (Time.frameCount == lastFrame) return;      // 一帧只认一次（场上多辆车时每个都会回调）
        lastFrame = Time.frameCount;
        if (!Keybinds.Pressed("lights")) return;
        ToggleOnce(__instance);
    });

    static void ToggleOnce(VehicleBehaviour fallback)
    {
        On = !On;
        int lit = 0, cars = 0;
        foreach (var v in Target(fallback))
        {
            cars++;
            lit += Apply(v);
        }
        Plugin.ModLog.LogInfo($"TOOL_LIGHT switch 开={On} 车={cars} 点亮灯对象={lit}");
    }

    /// 优先玩家车；找不到（比如设计器里）就用传进来的那一辆。
    static IEnumerable<VehicleBehaviour> Target(VehicleBehaviour fallback)
    {
        VehicleBehaviour[] all;
        try { all = UnityEngine.Object.FindObjectsOfType<VehicleBehaviour>(); }
        catch (Exception) { all = Array.Empty<VehicleBehaviour>(); }
        var players = all.Where(v => v != null && v.ControlType == Sprocket.Vehicles.Control.ControlType.Player).ToArray();
        if (players.Length > 0) return players;
        return fallback == null ? Array.Empty<VehicleBehaviour>() : new[] { fallback };
    }

    static int Apply(VehicleBehaviour vehicle)
    {
        int lit = 0;
        foreach (var light in LightsOf(vehicle))
        {
            if (light == null) continue;
            light.enabled = On;
            if (On) lit++;
        }
        return lit;
    }

    /// 找出这辆车所有**灯部件**底下的 `Light` 对象。按部件缓存（按车的原生指针），部件不会中途换灯。
    static Light[] LightsOf(VehicleBehaviour vehicle)
    {
        IntPtr key = vehicle.Pointer;
        if (cache.TryGetValue(key, out var cached)) return cached;

        var found = new List<Light>();
        VehicleObject[] parts;
        try { parts = vehicle.GetComponentsInChildren<VehicleObject>(true); }
        catch (Exception) { return cache[key] = Array.Empty<Light>(); }

        // 诊断：实跑里 `点亮灯对象=0` —— 说明这辆车上**一个带 headLight/tailLight 标签的部件都没有**
        // （官方车多数不装灯，灯是玩家自己加的附加件）。把扫到的部件数与标签如实打出来，别让它沉默地失败。
        int scanned = 0;
        var tags = new List<string>();
        foreach (var part in parts)
        {
            if (part == null) continue;
            scanned++;
            bool head, tail;
            try
            {
                head = part.HasTag("headLight");
                tail = part.HasTag("tailLight");
                if (!head && !tail)
                {
                    if (tags.Count < 12)
                    {
                        try
                        {
                            foreach (var t in part.tags ?? Array.Empty<string>())
                                if (!tags.Contains(t)) tags.Add(t);
                        }
                        catch (Exception) { }
                    }
                    continue;      // 只认灯部件，别把车里别的光源也点了
                }
            }
            catch (Exception) { continue; }

            foreach (var light in part.GetComponentsInChildren<Light>(true))
            {
                if (light == null) continue;
                // 只有第一次遇到这盏灯时才配参数，免得每帧覆盖
                if (!found.Contains(light))
                {
                    Configure(light, head, tail);
                    found.Add(light);
                }
            }
        }
        Plugin.ModLog.LogInfo($"TOOL_LIGHT switch 扫车：部件={scanned} 找到灯对象={found.Count} 车上标签=[{string.Join(",", tags)}]");
        return cache[key] = found.ToArray();
    }

    /// 头灯＝白色聚光，尾灯＝红色点光。用户要的是"能亮"，所以给一套一眼看得见的参数。
    static void Configure(Light light, bool head, bool tail)
    {
        try
        {
            if (tail && !head)
            {
                light.type = LightType.Point;
                light.color = new Color(1f, 0.15f, 0.1f);
                light.intensity = 1.2f;
                light.range = 8f;
            }
            else
            {
                light.type = LightType.Spot;
                light.color = Color.white;
                light.intensity = 3f;
                light.range = 90f;
                light.spotAngle = 60f;
            }
            light.enabled = On;
        }
        catch (Exception) { }
    }
}
