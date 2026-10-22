using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using Sprocket.Vehicles.Fires;
using UnityEngine;

namespace SprocketTools;

/// <summary>
/// 来自 1019mods/CannonSoundPoolFix：连发炮把炮口声源池打爆时，只保留最近的几条。
/// 不关特效：只限制炮口 prefab 的 MaxInstanceCount，并在 1m³ 范围内超过 N 条时停掉最老的 AudioSource。
/// </summary>
[HarmonyPatch]
internal static class CannonPool
{
    sealed class Record
    {
        public Transform Root = null!;
        public AudioSource? Near;
        public AudioSource? Far;
        public long Sequence;
        public int Id;
    }

    static readonly Dictionary<int, Record> Active = new();
    static readonly List<Record> Snapshot = new();
    static readonly HashSet<int> Stop = new();
    static long sequence;
    static bool logged;

    internal static ConfigEntry<bool>? Enabled;
    internal static ConfigEntry<int>? MaxInstances;
    internal static ConfigEntry<int>? MaxVoices;

    internal static void Init(ConfigFile config)
    {
        Enabled = config.Bind("Cannon pool", "Enabled", true,
            "连发炮口声源池限流：超过上限时停掉最老的炮口声源，避免把游戏的 audio pool 打空。");
        MaxInstances = config.Bind("Cannon pool", "Max instances", 192,
            "炮口特效 prefab 的 MaxInstanceCount 上限；原版更小时保留原版。");
        MaxVoices = config.Bind("Cannon pool", "Max voices per area", 5,
            "同一 1m³ 范围内最多保留几条炮口声源；超出的按时间停掉最老的。");
    }

    [HarmonyPostfix, HarmonyPatch(typeof(MuzzleFlashEffect), nameof(MuzzleFlashEffect.Setup))]
    static void Postfix(MuzzleFlashEffect __instance)
    {
        if (Enabled?.Value != true) return;
        try
        {
            var effect = __instance.prototype;
            if (effect != null)
            {
                int limit = MaxInstances?.Value ?? 192;
                if (limit > 0 && effect.MaxInstanceCount != limit) effect.MaxInstanceCount = limit;
            }

            var root = FindRoot(__instance.transform);
            if (root == null) return;
            Prune();
            int id = root.gameObject.GetInstanceID();
            Active[id] = new Record
            {
                Root = root, Near = __instance.audioSource, Far = __instance.longRangeAudioSource,
                Sequence = ++sequence, Id = id
            };
            LimitVoices();
        }
        catch (Exception ex)
        {
            if (!logged) { logged = true; Plugin.ModLog.LogWarning($"TOOL_POOL 炮口声源限流失败：{ex.GetType().Name}: {ex.Message}"); }
        }
    }

    static Transform? FindRoot(Transform start)
    {
        Transform? t = start;
        for (int i = 0; i <= 4 && t != null; i++)
        {
            if (t.gameObject != null && t.gameObject.name == "CannonMuzzleFlashEffect(Clone)") return t;
            t = t.parent;
        }
        return null;
    }

    static void Prune()
    {
        List<int>? dead = null;
        foreach (var pair in Active)
            if (pair.Value.Root == null || !pair.Value.Root.gameObject.activeInHierarchy)
                (dead ??= new()).Add(pair.Key);
        if (dead != null) foreach (int id in dead) Active.Remove(id);
    }

    static void LimitVoices()
    {
        Snapshot.Clear();
        foreach (var r in Active.Values)
            if (r.Root != null && r.Root.gameObject.activeInHierarchy) Snapshot.Add(r);
        if (Snapshot.Count <= (MaxVoices?.Value ?? 5)) return;

        Stop.Clear();
        int max = Mathf.Max(1, MaxVoices?.Value ?? 5);
        foreach (var anchor in Snapshot)
        {
            if (Stop.Contains(anchor.Id)) continue;
            var near = new List<Record>();
            foreach (var cand in Snapshot)
            {
                if (Stop.Contains(cand.Id)) continue;
                Vector3 a = anchor.Root.position, b = cand.Root.position;
                if (Mathf.Abs(a.x - b.x) <= 0.5f && Mathf.Abs(a.y - b.y) <= 0.5f && Mathf.Abs(a.z - b.z) <= 0.5f)
                    near.Add(cand);
            }
            if (near.Count <= max) continue;
            near.Sort((x, y) => y.Sequence.CompareTo(x.Sequence));
            for (int i = max; i < near.Count; i++) Stop.Add(near[i].Id);
        }
        foreach (int id in Stop)
        {
            if (!Active.TryGetValue(id, out var r)) continue;
            try { r.Near?.Stop(); } catch (Exception) { }
            try { r.Far?.Stop(); } catch (Exception) { }
            Active.Remove(id);
        }
    }
}
