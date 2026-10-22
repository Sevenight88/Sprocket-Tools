using System.Reflection;
using System.Text;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Sprocket.ContinuousTracks;
using Sprocket.Engines;
using Sprocket.Powertrains.Transmissions;
using Sprocket.UI;
using Sprocket.VehicleDesigner.Powertrains;
using Sprocket.Vehicles.Engines;
using Sprocket.Vehicles.PhysicsSystems;
using Sprocket.Vehicles.Powertrains;
using Sprocket.Vehicles.Tracks;
using Sprocket.Vehicles.Transmissions;
using Sprocket.Vehicles.Transmissions.Editor;
using UnityEngine;
using UnityEngine.InputSystem;
// 发动机面板的实例按基类拿着：真正的那个类在 0.2.56 换了程序集，见文件末尾的 EngineEditor。
using EngineEditorBase = Sprocket.VehicleDesigner.Access.Editors.VehicleComponentEditor<Sprocket.Vehicles.Engines.CombustionEngine>;

namespace SprocketTools;

/// 传动系统：在变速箱和发动机的面板里各加一节「速度与加速」，用游戏自己的传动算法（DriveSim）
/// 按本车的传动件算出每个挡位的极速，以及从静止开始的加速时间。
[HarmonyPatch]
public static class GearSpeeds
{
    const string Title = "速度与加速";
    internal const string LogTag = "Speed & acceleration"; // English-only prefix for logs and error guards
    static string? shown, logged; // last state / inputs written to the log, so each is logged once per change
    static EngineEditorBase? pendingEngineRedraw;
    static TransmissionEditor? pendingGearsRedraw;
    static string? predictionKey, predictionText;
    static TransmissionEditor? transmissionPanel; // 最后画出的变速箱面板：它的「平滑」按钮
    static (float First, float Top, int Count, float[] Gears)? pendingSmooth; // 游戏 Smooth 运行期间使用的间距

    // After a change the panel isn't redrawn by itself: ask, so the numbers follow.
    // If the user is dragging a slider, defer redraw until the mouse button is released,
    // otherwise destroying the slider mid-drag aborts the drag and locks the slider.
    [HarmonyPostfix, HarmonyPatch(typeof(TransmissionEditor), nameof(TransmissionEditor.OnComponentRebuilt))]
    static void GearsChanged(TransmissionEditor __instance) => Ui.Guard(LogTag, () =>
    {
        if (IsDragging()) pendingGearsRedraw = __instance;
        else __instance.RequestRedraw();
    });

    // 发动机面板那两条挂点是手工接的（Plugin.Load 调 Hooks.AttachPostfix），接在 EngineEditor 运行时找到的类上：
    // 这个类在 0.2.56 从 Sprocket.EngineDesigner 挪进了 Sprocket.EngineDesigner.Editor，编译期写死只能认一个版本。
    internal static void EngineChanged(EngineEditorBase __instance) => Ui.Guard(LogTag, () =>
    {
        if (IsDragging()) pendingEngineRedraw = __instance;
        else __instance.RequestRedraw();
    });

    internal static void Update()
    {
        if (accelTask is { IsCompleted: true } done && !IsDragging())
        {
            accelTask = null;
            if (done.IsFaulted) Plugin.ModLog.LogWarning($"{LogTag}: acceleration run failed: {done.Exception?.GetBaseException()}");
            accelText = done.IsFaulted ? "加速时间：算不出来（详见日志）。" : done.Result;
            try { redrawPanel?.Invoke(); } catch { }
        }
        if (pendingEngineRedraw != null && !IsDragging())
        {
            var e = pendingEngineRedraw;
            pendingEngineRedraw = null;
            try { e.RequestRedraw(); } catch { }
        }
        if (pendingGearsRedraw != null && !IsDragging())
        {
            var g = pendingGearsRedraw;
            pendingGearsRedraw = null;
            try { g.RequestRedraw(); } catch { }
        }
    }

    internal static void LeftEditor()
    {
        pendingEngineRedraw = null;
        pendingGearsRedraw = null;
        predictionKey = null;
        predictionText = null;
        accelKey = accelText = null;
        accelTask = null;
        redrawPanel = null;
        transmissionPanel = null;
        pendingSmooth = null;
        EngineEditor.LeftEditor(); // 面板实例没了，反射包装对象跟着丢
    }

    /// 变速箱「平滑」：一挡和最高挡不动，中间各挡按本车最快到最高速 90% 的间距排，取代游戏的等比。
    /// 游戏自己的 ApplySmooth 仍按它那一步撤销；这里只在它调用 ComputeIdealRatios 时换掉间距。
    [HarmonyPrefix, HarmonyPatch(typeof(GearAssemblyProperties), nameof(GearAssemblyProperties.ApplySmooth))]
    static void Smooth(GearAssemblyProperties __instance)
    {
        pendingSmooth = null;
        try
        {
            var panel = transmissionPanel;
            var ratios = __instance.ratios;
            if (ratios == null || ratios.Length <= 2) return;
            if (panel?.properties?.drive?.Pointer != __instance.Pointer)
            {
                Plugin.ModLog.LogInfo($"{LogTag}: 平滑的不是当前打开的变速箱主动齿轮，用游戏的等比");
                return;
            }
            var gearbox = panel.Component;
            var (engine, _, tracks) = Drivetrain(gearbox, null);
            if (VehicleOf(engine, gearbox, tracks, gearbox.Vehicle?.Mass ?? 0, out _, out var problem) is not { } vehicle)
            {
                Plugin.ModLog.LogInfo($"{LogTag}: 平滑用游戏的等比（{problem}）");
                return;
            }
            float first = ratios[0].Value, top = ratios[^1].Value, auxiliary = gearbox.Blueprint.AuxiliaryGearRatio;
            if (GearSpacing.Fastest(vehicle, first, top, ratios.Length, auxiliary) is not { } choice) return;
            string To(float seconds) => $"0-{choice.Target * 3.6f:0} km/h 用 {(seconds < 10 ? seconds.ToString("0.0") : seconds.ToString("0"))} 秒";
            if (choice.Exponent != 1)
            {
                pendingSmooth = (first, top, ratios.Length, choice.Gears);
                Plugin.ModLog.LogInfo($"{LogTag}: 平滑改用最快间距，{To(choice.Seconds)}（等比 {To(choice.EvenSeconds)}），指数 {choice.Exponent}");
            }
            else Plugin.ModLog.LogInfo($"{LogTag}: 等比已经是这里最快的间距（{To(choice.Seconds)}）");
        }
        catch (Exception ex)
        {
            Plugin.ModLog.LogWarning($"{LogTag}: 平滑用游戏的等比：{ex.GetType().Name}: {ex.Message}");
        }
    }

    [HarmonyPostfix, HarmonyPatch(typeof(GearAssemblyProperties), nameof(GearAssemblyProperties.ApplySmooth))]
    static void Smoothed() => pendingSmooth = null;

    /// 只在主动齿轮的 Smooth 运行、且挡位对得上时，用上面的间距取代游戏的等比。
    [HarmonyPrefix, HarmonyPatch(typeof(TransmissionRules), nameof(TransmissionRules.ComputeIdealRatios))]
    static bool IdealRatios(float __0, float __1, int __2, ref Il2CppStructArray<float> __result)
    {
        if (pendingSmooth is not { } p || p.First != __0 || p.Top != __1 || p.Count != __2) return true;
        __result = new Il2CppStructArray<float>(p.Gears);
        return false;
    }

    static bool IsDragging()
    {
        try
        {
            if (Mouse.current is { } m && m.leftButton.isPressed) return true;
        }
        catch { }
        try
        {
            if (Input.GetMouseButton(0)) return true;
        }
        catch { }
        return false;
    }

    [HarmonyPostfix, HarmonyPatch(typeof(TransmissionEditor), nameof(TransmissionEditor.OnGUI))]
    static void InTransmission(TransmissionEditor __instance, IGUILayout layout) => Ui.Guard(LogTag, () =>
    {
        transmissionPanel = __instance;
        redrawPanel = () => __instance.RequestRedraw();
        Draw(layout, __instance.Component, null, __instance.Component.Vehicle?.Mass ?? 0);
    });

    // 同 EngineChanged：手工接在运行时找到的那个类上。blueprint 只在子类上有，走 EngineEditor 反射读。
    internal static void InEngine(EngineEditorBase __instance, IGUILayout layout) => Ui.Guard(LogTag, () =>
    {
        redrawPanel = () => __instance.RequestRedraw();
        Draw(layout, null, EngineEditor.Blueprint(__instance), __instance.Component.Vehicle?.Mass ?? 0);
    });

    static void Draw(IGUILayout layout, TransmissionBlock? gearbox, EngineBlueprint? engine, float mass)
    {
        var ui = layout.TryCast<IGUIElementDrawer>();
        if (ui == null) return;
        var text = Describe(gearbox, engine, mass);
        var summary = string.Join(" | ", text.Split('\n').Where(l => !l.StartsWith("档位")));
        if (summary != shown) { shown = summary; Plugin.ModLog.LogInfo($"{LogTag}: {shown}"); }
        Ui.Section(layout, Title);
        // 面板窄时会换行，所以长行按两行留高度。
        ui.InfoField(text, text.Split('\n').Sum(l => l.Length > 40 ? 2 : 1));
    }

    static float Try(Func<float> read, float fallback = 0) { try { return read(); } catch { return fallback; } }

    /// The engine never revs past its rev limit: its own setting, or (by default) the upshift rpm + 50.
    internal static float RevLimit(EngineBlueprint engine)
    {
        float limit = Try(() => engine.RevLimit);
        return limit <= 0 || limit > engine.MaxRPM ? engine.MaxRPM : limit;
    }

    /// Disengaging and engaging time of the gearbox's type (synchromesh, constant or sliding mesh).
    static (float Disengage, float Engage) ShiftTimes(TransmissionBlock gearbox)
    {
        try
        {
            return TransmissionMeshTypes.ParseMeshType(gearbox.Blueprint.meshType) switch
            {
                TransmissionMeshType.Synchromesh => (TransmissionBehaviour.SynchromeshDisengageTime, TransmissionBehaviour.SynchromeshEngageTime),
                TransmissionMeshType.ConstantMesh => (TransmissionBehaviour.ConstantMeshDisengageTime, TransmissionBehaviour.ConstantMeshEngageTime),
                _ => (TransmissionBehaviour.SlidingMeshDisengageTime, TransmissionBehaviour.SlidingMeshEngageTime),
            };
        }
        catch { return (0, 0); }
    }

    const float Rads = MathF.PI / 30; // rpm -> rad/s

    /// 按游戏起步时那样把整车搭起来：EngineBehaviour.SetBlueprint 里的发动机（扭矩模板按最高转速和最大扭矩缩放、
    /// 降挡转速作为工作转速）、双流变速箱的离合器（最大扭矩的 5 倍和 1.5 倍），以及 TrackAssembly.CreateTrackController
    /// 里的每条履带：皮带按皮带包裹半径包住驱动轮、弯曲阻力是单节质量²（再乘履带科技的系数）、驱动轮黏滞阻力
    /// 0.25 × 单节质量 + 3、滚动阻力 0.03 × (1 + 0.001 v²)、动摩擦 0.8。不论装了几条履带，数值都按每侧一份计；
    /// 质量取整车刚体上的那个。
    static DriveSim.Vehicle Build(EngineBlueprint engine, float[] ratios, TransmissionBlock gearbox, List<TrackAssembly> tracks, float finalDrive, float radius, float mass)
    {
        float revLimit = RevLimit(engine);
        var e = DriveSim.Engine.Of(engine.MaxTorque, engine.MaxRPM * Rads, Try(() => engine.Inertia), revLimit * Rads,
            Try(() => engine.FrictionCoefficient), Try(() => engine.Downshift) * Rads, engine.IdleRPM * Rads);
        var (disengage, engage) = ShiftTimes(gearbox);
        float Tech(TrackAssembly t, string key) => Try(() => t.TrackTech?.GetFloat(key, 1) ?? 1, 1);
        float Segment(TrackAssembly t) => Try(() => t.Belt.SegmentMass);
        float PerSide(Func<TrackAssembly, float> f) => tracks.Sum(f) / 2;
        var sprocket = new DriveSim.Sprocket(finalDrive,
            PerSide(t => Tech(t, "bendingResistanceCoefficient") * Segment(t) * Segment(t)),
            PerSide(t => Tech(t, "viscousDragCoefficient") * (0.25f * Segment(t) + 3)),
            PerSide(t => Try(() => t.ComputeSprocketInertia())), 0.8f, radius);
        float rolling = Tech(tracks[0], "rollingResistanceCoefficient");
        // An upshift at or past the rev limit is taken just under it (the engine never gets there).
        float upshift = Math.Min(engine.Upshift, revLimit - 25) * Rads;
        float torque = engine.MaxTorque, step = Try(() => Time.fixedDeltaTime, 0.01f);
        return new DriveSim.Vehicle(e, ratios, disengage, engage, 5 * torque, 1.5f * torque, upshift, sprocket, mass, 0.03f * rolling, 0.001f * rolling,
            FixedDeltaTime: step > 0 ? step : 0.01f);
    }

    static string Inputs(DriveSim.Vehicle v, float[] reverse)
    {
        var e = v.Engine; var p = v.Sprocket;
        static string F(float x) => x.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        return $"engine {F(e.MaxTorque)} N·m to {F(e.CurveSpeed[^2] / Rads)} rpm, rev limit {F(e.RevLimit / Rads)}, upshift {F(v.UpshiftSpeed / Rads)}, " +
               $"operating {F(e.OperatingSpeed / Rads)}, idle {F(e.IdleSpeed / Rads)}, inertia {F(e.Inertia)}, friction {F(e.Friction)}; " +
               $"gears {string.Join("/", v.Gears.Select(F))} (reverse {string.Join("/", reverse.Select(F))}), shift {F(v.Disengage)} + {F(v.Engage)} s; " +
               $"final drive {F(p.FinalDrive)}, belt radius {F(p.Radius)}, per side bending {F(p.Bending)}, drag {F(p.Viscous)}, sprocket inertia {F(p.Inertia)}; " +
               $"rolling {F(v.RollingResistance)} (1 + {F(v.RollingPerSpeed2)} v²); mass {F(v.Mass)} kg; step {F(v.FixedDeltaTime)} s";
    }

    // The acceleration run takes a moment, so it runs in the background; the panel shows it once it's done.
    static string? accelKey, accelText;
    static Task<string>? accelTask;
    static Action? redrawPanel;

    static string Acceleration(DriveSim.Vehicle vehicle, string key, float top)
    {
        if (accelKey == key && accelText != null) return accelText;
        if (accelKey != key || accelTask == null)
        {
            accelKey = key; accelText = null;
            accelTask = Task.Run(() => Milestones(vehicle, top));
        }
        return "正在计算加速时间……";
    }

    /// Seconds to each round speed on the way to top speed (0-20, 0-40... or 0-10, 0-20... for slow vehicles).
    static string Milestones(DriveSim.Vehicle vehicle, float top)
    {
        var run = DriveSim.Accelerate(vehicle);
        float topKmh = top * 3.6f, step = topKmh > 45 ? 20 : 10;
        var parts = new List<string>();
        for (float kmh = step; kmh < topKmh * 0.97f; kmh += step)
        {
            int i = run.FindIndex(r => r.Speed * 3.6f >= kmh);
            if (i < 0) break;
            float t = run[i].Time;
            parts.Add($"0-{kmh:0} km/h：{(t < 10 ? t.ToString("0.0") : t.ToString("0"))} 秒");
        }
        return parts.Count == 0 ? "加速时间：车速太慢，不作计时。" : string.Join("  ·  ", parts);
    }

    /// Each gear's top speed, solved where the drive settles (DriveSim.TopSpeed), the fastest of them, peak power under
    /// the rev limit, and the acceleration from a standstill (DriveSim.Accelerate, in the background).
    static string Describe(TransmissionBlock? gearbox, EngineBlueprint? engine, float mass)
    {
        var parts = DesignEditor.Instance?.AllComponents().ToList() ?? new();
        var engines = parts.Select(c => c.TryCast<CombustionEngine>()).Where(e => e?.Blueprint != null).ToList();
        engine ??= (engines.FirstOrDefault(e => e!.SelectedInPowertrain) ?? engines.FirstOrDefault())?.Blueprint;
        var gearboxes = parts.Select(c => c.TryCast<TransmissionBlock>()).Where(t => t != null).ToList();
        gearbox ??= gearboxes.FirstOrDefault(t => t!.SelectedInPowertrain) ?? gearboxes.FirstOrDefault();
        var tracks = parts.Select(c => c.TryCast<TrackAssembly>()).Where(t => t?.BlueprintSlot?.HasBlueprint == true).Select(t => t!).ToList();
        if (VehicleOf(engine, gearbox, tracks, mass, out var reverse, out var problem) is not { } vehicle) return problem;
        string key = Inputs(vehicle, reverse);
        if (key != predictionKey || predictionText == null)
        {
            if (key != logged) { logged = key; Plugin.ModLog.LogInfo($"{LogTag} inputs: {key}"); }
            predictionKey = key;
            predictionText = Speeds(vehicle, reverse, out topSpeed);
        }
        return predictionText + "\n" + (topSpeed > 0 ? Acceleration(vehicle, key, topSpeed) : "任何挡位都推不动车。") +
               "\n平地、全油门、自动换挡：用游戏自己的传动算法，按本车的传动件一步步算出。";
    }

    /// 本设计的发动机、变速箱和履带：给定优先，否则用动力系统里选中的。
    static (EngineBlueprint? Engine, TransmissionBlock? Gearbox, List<TrackAssembly> Tracks) Drivetrain(TransmissionBlock? gearbox, EngineBlueprint? engine)
    {
        var parts = DesignEditor.Instance?.AllComponents().ToList() ?? new();
        var engines = parts.Select(c => c.TryCast<CombustionEngine>()).Where(e => e?.Blueprint != null).ToList();
        engine ??= (engines.FirstOrDefault(e => e!.SelectedInPowertrain) ?? engines.FirstOrDefault())?.Blueprint;
        var gearboxes = parts.Select(c => c.TryCast<TransmissionBlock>()).Where(t => t != null).ToList();
        gearbox ??= gearboxes.FirstOrDefault(t => t!.SelectedInPowertrain) ?? gearboxes.FirstOrDefault();
        return (engine, gearbox, parts.Select(c => c.TryCast<TrackAssembly>()).Where(t => t?.BlueprintSlot?.HasBlueprint == true).Select(t => t!).ToList());
    }

    static float topSpeed;

    /// The vehicle as the game sets it up for a drive (Build), from its engine, gearbox, tracks and mass; null, with
    /// what's missing, if it can't be. `reverse`: the reverse gears' ratios.
    static DriveSim.Vehicle? VehicleOf(EngineBlueprint? engine, TransmissionBlock? gearbox, List<TrackAssembly> tracks, float mass, out float[] reverse, out string problem)
    {
        reverse = Array.Empty<float>();
        problem = engine == null ? "加装发动机后可查看速度。" : gearbox == null ? "加装变速箱后可查看速度。" : tracks.Count == 0 ? "加装履带后可查看速度。" : "";
        if (engine == null || gearbox == null || tracks.Count == 0) return null;
        var ratios = (gearbox.resultingDriveGearRatios?.ToArray() ?? Array.Empty<float>()).Select(Math.Abs).Where(r => r > 0).ToArray();
        reverse = (gearbox.resultingReverseGearRatios?.ToArray() ?? Array.Empty<float>()).Select(Math.Abs).Where(r => r > 0).ToArray();
        if (ratios.Length == 0) { problem = "暂无前进档位。"; return null; }
        var track = tracks[0];
        float finalDrive = track.BlueprintSlot.Blueprint.FinalDriveRatio;
        float radius = Try(() => track.SprocketAssembly.BeltWrapRadius);
        if (radius <= 0 || finalDrive <= 0 || engine.MaxRPM <= 0 || engine.MaxTorque <= 0) { problem = "暂时读不到履带的驱动轮。"; return null; }
        if (mass <= 0) { problem = "暂时读不到整车质量。"; return null; }
        return Build(engine, ratios, gearbox, tracks, finalDrive, radius, mass);
    }

    /// Top speed forward and in reverse (km/h) on level ground, as the Speed & acceleration panel works it out (the
    /// fastest gear where the drive settles; 0 for none); null if the drivetrain can't be read.
    internal static (float Forward, float Reverse)? TopSpeeds(EngineBlueprint? engine, TransmissionBlock? gearbox, List<TrackAssembly> tracks, float mass)
    {
        if (VehicleOf(engine, gearbox, tracks, mass, out var reverse, out _) is not { } v) return null;
        float forward = v.Gears.Max(r => DriveSim.TopSpeed(v, r).Speed) * 3.6f;
        float back = reverse.Length > 0 ? DriveSim.TopSpeed(v, reverse.Min()).Speed * 3.6f : 0;
        return (forward, back);
    }

    static string Speeds(DriveSim.Vehicle vehicle, float[] reverse, out float top)
    {
        var text = new StringBuilder();
        var gears = vehicle.Gears.Select(r => DriveSim.TopSpeed(vehicle, r)).ToArray();
        float limited = vehicle.Engine.RevLimit - 2 * 5.235988f; // into the rev limiter's band: out of revs, not power
        for (int i = 0; i < gears.Length; i++)
            text.Append(gears[i].Speed <= 0 ? $"档位 {i + 1}：  推不动车\n"
                : $"档位 {i + 1}：  {gears[i].Speed * 3.6f:0.0} km/h{(gears[i].EngineSpeed < limited ? "（未到转速上限）" : "")}\n");
        if (reverse.Length > 0)
        {
            var back = DriveSim.TopSpeed(vehicle, reverse.Min());
            text.Append(back.Speed > 0 ? $"倒档：  {back.Speed * 3.6f:0.0} km/h\n" : "倒档：  推不动车\n");
        }
        int best = 0;
        for (int i = 1; i < gears.Length; i++) if (gears[i].Speed > gears[best].Speed) best = i;
        top = gears[best].Speed;
        if (top > 0)
            text.Append($"极速：  {top * 3.6f:0.0} km/h（第 {best + 1} 档，{gears[best].EngineSpeed / Rads:0} rpm，履带打滑 {gears[best].Slip / top * 100:0.0}%）\n");
        // The most power the engine gives below its rev limit (not the engine's rated figure at max revs).
        var e = vehicle.Engine;
        var (power, at) = Enumerable.Range(0, 201).Select(k => e.IdleSpeed + (e.RevLimit - e.IdleSpeed) * k / 200f)
            .Select(w => (Kw: e.FullThrottle(w) * w / 1000, Rpm: w / Rads)).MaxBy(x => x.Kw);
        text.Append($"转速内最大功率：{power:0} kW（{power * 1.341f:0} 马力），出现在 {at:0} rpm");
        return text.ToString();
    }
}

/// 发动机面板的那个类，运行时按名字找：0.2.56 把它从 Sprocket.EngineDesigner 挪进了 Sprocket.EngineDesigner.Editor
/// （两份 interop 都现场核过：全名、OnGUI、OnComponentRebuilt、public blueprint 都在，只有程序集不同）。
/// 在代码里写死类型＝只认一个游戏版本——引用不上的那份每帧抛 FileNotFound／TypeLoad，整个小节连面板一起没了。
/// 面板实例用它的基类 VehicleComponentEditor&lt;CombustionEngine&gt; 拿着，挂点由 Plugin 手工接到找到的类上。
internal static class EngineEditor
{
    internal const string Name = "Sprocket.Vehicles.Engines.Editor.CombustionEngineComponentEditor";
    internal static readonly Type? Type = Find();
    static PropertyInfo? blueprint;
    // blueprint 在子类上，得先把指针包成那个类的托管对象才能反射读；OnGUI 每帧都要，包装对象攒着用。
    static IntPtr boxed;
    static Il2CppObjectBase? box;

    static Type? Find()
    {
        foreach (var assembly in new[] { "Sprocket.EngineDesigner.Editor", "Sprocket.EngineDesigner" })
            try { if (Assembly.Load(assembly).GetType(Name) is { } type) return type; }
            catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException) { }
        return null;
    }

    /// 面板正在显示的发动机蓝图；这个版本没这个类、或者读不到就返回 null（小节照常提示「加装发动机后可查看速度」）。
    internal static EngineBlueprint? Blueprint(EngineEditorBase editor)
    {
        if (Type == null || (blueprint ??= AccessTools.Property(Type, "blueprint")) is not { } property) return null;
        if (box == null || boxed != editor.Pointer)
        {
            boxed = editor.Pointer;
            box = Activator.CreateInstance(Type, editor.Pointer) as Il2CppObjectBase;
        }
        return box == null ? null : property.GetValue(box) as EngineBlueprint;
    }

    /// 离开设计器：面板实例要被游戏丢掉，指针可能落到别的对象身上，包装对象一起清掉。
    internal static void LeftEditor()
    {
        boxed = IntPtr.Zero;
        box = null;
    }
}
