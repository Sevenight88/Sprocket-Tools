using System;
using BepInEx.Configuration;
using DynamicGUI;
using HarmonyLib;
using Properties;
using Sprocket.VehicleDesigner;
using UnityEngine;
using UnityEngine.UI;

namespace SprocketTools;

/// <summary>
/// 来自 1019mods/PenetrationMod：放开设计器里 Penetration / Caliber 的逻辑上限和滑条上限。
/// 原版是 MelonLoader + SprocketModAPI；这里改成 BepInEx 配置项，并在设计器 EditorUpdate 里低频改面板。
/// </summary>
[HarmonyPatch]
internal static class PenetrationLimits
{
    internal static ConfigEntry<bool>? Enabled;
    internal static ConfigEntry<float>? MaxPenetration;
    internal static ConfigEntry<float>? SliderMaxPenetration;
    internal static ConfigEntry<float>? MinCaliber;
    internal static ConfigEntry<float>? MaxCaliber;
    internal static ConfigEntry<float>? SliderMaxCaliber;

    static float next;
    static GameObject? panel;

    internal static void Init(ConfigFile config)
    {
        Enabled = config.Bind("Designer limits", "Enabled", true,
            "设计器里放开 Penetration / Caliber 的逻辑上限和滑条上限。");
        MaxPenetration = config.Bind("Designer limits", "Max penetration", 20000f,
            "穿深逻辑上限。原版滑条最多 1000 左右；这里默认 20000。");
        SliderMaxPenetration = config.Bind("Designer limits", "Penetration slider max", 1000f,
            "穿深滑条能拖到的最大值。");
        MinCaliber = config.Bind("Designer limits", "Min caliber", 1f,
            "口径下限（mm）。");
        MaxCaliber = config.Bind("Designer limits", "Max caliber", 5000f,
            "口径逻辑上限（mm）。");
        SliderMaxCaliber = config.Bind("Designer limits", "Caliber slider max", 500f,
            "口径滑条能拖到的最大值。");
    }

    [HarmonyPostfix, HarmonyPatch(typeof(VehicleDesignerCore), "EditorUpdate")]
    static void Tick()
    {
        if (Enabled?.Value != true) return;
        float now = Time.unscaledTime;
        if (now < next) return;
        next = now + 0.1f;
        try
        {
            if (panel == null) panel = GameObject.Find("ConfigPanel");
            if (panel == null) return;
            var sliders = panel.GetComponentsInChildren<PropertyFieldSlider>(true);
            if (sliders == null) return;
            foreach (var ps in sliders)
            {
                if (ps == null) continue;
                var prop = ps.prop;
                if (prop == null) continue;
                var ui = ps.GetComponentInChildren<Slider>(true);
                if (ui == null) continue;
                string name = prop.name ?? "";
                if (name == "Penetration")
                {
                    if (!Mathf.Approximately(prop.Max, MaxPenetration?.Value ?? 20000f)) prop.Max = MaxPenetration?.Value ?? 20000f;
                    if (!Mathf.Approximately(ui.maxValue, SliderMaxPenetration?.Value ?? 1000f)) ui.maxValue = SliderMaxPenetration?.Value ?? 1000f;
                }
                else if (name == "Caliber")
                {
                    if (!Mathf.Approximately(prop.Min, MinCaliber?.Value ?? 1f)) prop.Min = MinCaliber?.Value ?? 1f;
                    if (!Mathf.Approximately(prop.Max, MaxCaliber?.Value ?? 5000f)) prop.Max = MaxCaliber?.Value ?? 5000f;
                    if (!Mathf.Approximately(ui.minValue, MinCaliber?.Value ?? 1f)) ui.minValue = MinCaliber?.Value ?? 1f;
                    if (!Mathf.Approximately(ui.maxValue, SliderMaxCaliber?.Value ?? 500f)) ui.maxValue = SliderMaxCaliber?.Value ?? 500f;
                }
            }
        }
        catch (Exception) { }
    }
}
