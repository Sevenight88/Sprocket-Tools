using System.Reflection;
using HarmonyLib;

namespace SprocketTools;

/// Attaching QoL to the game one hook at a time: a hook whose game method is gone (renamed or removed by a game update)
/// turns off only that hook, not every hook of its feature, and the report says which (Mod Options > Compatibility, and
/// the log).
internal static class Hooks
{
    internal sealed record Entry(string Feature, string Hook, string? Error);
    internal static readonly List<Entry> Report = new();

    internal static int Attached => Report.Count(e => e.Error == null);
    internal static IEnumerable<Entry> Failures => Report.Where(e => e.Error != null);

    internal static void Failed(string feature, string hook, Exception ex) =>
        Report.Add(new(feature, hook, (ex.InnerException ?? ex).GetType().Name + ": " + (ex.InnerException ?? ex).Message));

    /// 攒着、等首次进设计器再挂的那批挂点（自己的补丁方法与目标都在开机时解析好，只差没往游戏身上接）。
    sealed record Deferred(string Feature, MethodInfo Patch, HarmonyMethod Info, string Kind, string Hook);
    static readonly List<Deferred> deferred = new();
    internal static int Pending => deferred.Count + manual.Count; // manual 见下面的 AttachPostfix

    /// 开机就要挂的游戏类型。判据只有一条：这条挂点若拖到进设计器才接上，会不会在设计器之外少做一次事。
    /// 名单内：主菜单设置页的 Mod Options 页签、科技表（装甲材质要在读表时并进）、战斗里的弹种与穿甲判定、
    /// 车灯的开关（挂在 VehicleBehaviour.GameUpdate）、涂装贴图的地址桥、炮塔方向机的销毁链（任何一辆车生成都会走，不只是设计器）、
    /// 部件定义（炮塔「可用 Alt 复制」要在读表时就标好）、面板排版（设置页用的也是同一套 UI），
    /// 以及自有涂装的读取与上色（那节的注释自己写着「编辑器里和战斗里都要生效」，推迟会让战斗里的车掉回默认涂装）。
    /// 名单外的那些目标只会被设计器里的面板和操作调到，一律推迟（实测每条挂点约 20 毫秒，量都在这边）。
    /// 按类名而不是完整命名空间来认：名单写错命名空间会静默漏挂，同名撞上另一种游戏类型只多花一条挂点的钱。
    static readonly HashSet<string> AtBoot = new()
    {
        "SettingsMenu", "TechTreeLoader", "ProjectileTypeRegister",
        // 命中层的唯一活接缝（#108）：`ArmourPiercingProjectileFunction.HitDamageModel` 是 override、不会被内联。
        // 必须开机就挂 —— 进战斗前未必会先进设计器，推迟挂点就等于整场战斗都没有它。
        "ArmourPiercingProjectileFunction",
        // 转正（normalization）的落地点：`DamageModelHit.Simulate(PenetratorInfo, …)` 是 `PenetratorInfo` **按值**传进去的那一站。
        // 同样只在战斗里走到，必须开机就挂。
        "DamageModelHit",
        // 玻璃透明：板材质贴上去那一刻就要判（战斗里生成的车也得透，推迟到进设计器就等于战斗里不生效）。
        "PlateStructure",
        // VehicleLights / VehicleLight 不在名单：车灯开关挂在 VehicleBehaviour.GameUpdate。
        "VehicleBehaviour",
        "UnityWebRequest", "UnityWebRequestTexture", "VehicleObjectRegister", "VehicleComponent",
        "TurretRing", "PartDefinitionIO", "DynamicGUILayout", "PaintMain", "VehicleMaterialPainter",
    };
    static readonly HashSet<string> atBootSeen = new();

    /// 名单里没对上任何一条挂点的名字：游戏改名了，或名单打错了。这会让本该开机挂的挂点悄悄被推迟，所以每次启动都要报出来。
    internal static IEnumerable<string> UnusedBootNames => AtBoot.Where(n => !atBootSeen.Contains(n));

    /// Every Harmony patch method of `feature` (its [HarmonyPatch] target, merged with the class's), each one on its own.
    /// 目标类型在 AtBoot 里的当场接上，其余的攒给 AttachDeferred（进设计器那一次性接完）。
    internal static void Attach(Harmony harmony, Type feature)
    {
        MethodInfo[] methods;
        try { methods = feature.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly); }
        catch (Exception ex) { Failed(feature.Name, "(the whole feature)", ex); return; }
        HarmonyMethod classInfo;
        try { classInfo = HarmonyMethod.Merge(HarmonyMethodExtensions.GetFromType(feature)); }
        catch (Exception ex) { Failed(feature.Name, "(the whole feature)", ex); return; }
        foreach (var method in methods)
        {
            string? kind;
            List<HarmonyMethod> attributes;
            try
            {
                var names = method.GetCustomAttributes(true).Select(a => a.GetType().Name).ToList();
                kind = names.Contains(nameof(HarmonyPrefix)) ? "prefix" : names.Contains(nameof(HarmonyPostfix)) ? "postfix"
                     : names.Contains(nameof(HarmonyFinalizer)) ? "finalizer" : names.Contains(nameof(HarmonyTranspiler)) ? "transpiler"
                     : method.Name is "Prefix" or "Postfix" or "Finalizer" or "Transpiler" ? method.Name.ToLowerInvariant() : null; // Harmony's naming convention
                if (kind == null) continue;
                attributes = HarmonyMethodExtensions.GetFromMethod(method);
            }
            catch (Exception ex) { Failed(feature.Name, method.Name, ex); continue; }
            var info = classInfo.Clone();
            info = info.Merge(HarmonyMethod.Merge(attributes));
            string hook = $"{info.declaringType?.Name ?? "?"}.{info.methodName ?? "?"} ({kind})";
            string target = info.declaringType?.Name ?? "";
            bool atBoot = AtBoot.Contains(target);
            if (atBoot) atBootSeen.Add(target);
            else { deferred.Add(new(feature.Name, method, info, kind, hook)); continue; } // 只有进设计器才会被调到：先记着
            Apply(harmony, feature.Name, method, info, kind, hook);
        }
    }

    /// 首次进设计器：把攒下的挂点一次接完，返回接了几条（好让日志把开机那批和这批分开报）。
    internal static int AttachDeferred(Harmony harmony)
    {
        var batch = deferred.ToArray();
        deferred.Clear();
        foreach (var d in batch) Apply(harmony, d.Feature, d.Patch, d.Info, d.Kind, d.Hook);
        var hand = manual.ToArray();
        manual.Clear();
        foreach (var m in hand) Apply(harmony, m);
        return batch.Length + hand.Length;
    }

    /// 一个后挂：目标类要在运行时按名找。0.2.56 把发动机面板从 Sprocket.EngineDesigner 挪进了
    /// Sprocket.EngineDesigner.Editor，编译期写死类型只能认一个版本，于是这份 DLL 在另一个版本上会每帧抛
    /// FileNotFound／TypeLoad（上游 1.8.6／1.8.7 正是这样丢了整个小节）。type 为 null 就说明这个版本没有它：
    /// 只记这一条失败（兼容性页看得到），别的小节照旧。只在设计器里被调到的，照 Attach 的路子先攒着。
    internal static void AttachPostfix(Harmony harmony, string feature, Type? type, string typeName, string methodName, MethodInfo? postfix)
    {
        string hook = $"{typeName}.{methodName} (postfix)";
        if (type == null || postfix == null) { Failed(feature, hook, new MissingMethodException($"{typeName} isn't in the game")); return; }
        if (!AtBoot.Contains(type.Name)) { manual.Add(new Manual(feature, hook, type, methodName, postfix)); return; }
        atBootSeen.Add(type.Name);
        Apply(harmony, new Manual(feature, hook, type, methodName, postfix));
    }

    sealed record Manual(string Feature, string Hook, Type Target, string MethodName, MethodInfo Postfix);
    static readonly List<Manual> manual = new();

    static void Apply(Harmony harmony, Manual m)
    {
        try
        {
            var original = AccessTools.DeclaredMethod(m.Target, m.MethodName)
                ?? throw new MissingMethodException($"{m.Hook} isn't in the game");
            var processor = harmony.CreateProcessor(original);
            processor.AddPostfix(new HarmonyMethod(m.Postfix));
            processor.Patch();
            Report.Add(new(m.Feature, m.Hook, null));
        }
        catch (Exception ex) { Failed(m.Feature, m.Hook, ex); }
    }

    static void Apply(Harmony harmony, string feature, MethodInfo patchMethod, HarmonyMethod info, string kind, string hook)
    {
        try
        {
            var original = Original(info) ?? throw new MissingMethodException($"{info.declaringType?.FullName}.{info.methodName} isn't in the game");
            var patch = new HarmonyMethod(patchMethod);
            var processor = harmony.CreateProcessor(original);
            switch (kind)
            {
                case "prefix": processor.AddPrefix(patch); break;
                case "postfix": processor.AddPostfix(patch); break;
                case "finalizer": processor.AddFinalizer(patch); break;
                default: processor.AddTranspiler(patch); break;
            }
            processor.Patch();
            Report.Add(new(feature, hook, null));
        }
        catch (Exception ex) { Failed(feature, hook, ex); }
    }

    static MethodBase? Original(HarmonyMethod info)
    {
        if (info.declaringType == null) return null;
        return info.methodType switch
        {
            MethodType.Getter => AccessTools.DeclaredPropertyGetter(info.declaringType, info.methodName) ?? AccessTools.PropertyGetter(info.declaringType, info.methodName),
            MethodType.Setter => AccessTools.DeclaredPropertySetter(info.declaringType, info.methodName) ?? AccessTools.PropertySetter(info.declaringType, info.methodName),
            MethodType.Constructor => AccessTools.DeclaredConstructor(info.declaringType, info.argumentTypes),
            _ => AccessTools.DeclaredMethod(info.declaringType, info.methodName, info.argumentTypes) ?? AccessTools.Method(info.declaringType, info.methodName, info.argumentTypes),
        };
    }

    /// For the log once everything is attached.
    internal static string Summary()
    {
        var failed = Failures.ToList();
        string state = failed.Count == 0 ? $"all {Attached} game hooks attached"
            : $"{Attached} of {Report.Count} game hooks attached; not attached: " + string.Join("; ", failed.Select(f => $"{f.Feature}: {f.Hook} ({f.Error})"));
        if (deferred.Count > 0) state += $"（另 {deferred.Count} 条设计器挂点等首次进设计器）";
        var unused = UnusedBootNames.ToList();
        if (unused.Count > 0) state += $"；开机名单里没对上任何挂点的类型：{string.Join("、", unused)}（游戏改名了？那些挂点被当成设计器专属推迟了）";
        return state;
    }
}
