using System.Text;
using HarmonyLib;
using Sprocket.ContinuousTracks;
using Sprocket.Engines;
using Sprocket.Powertrains.Transmissions;
using Sprocket.UI;
using Sprocket.VehicleDesigner.Powertrains;
using Sprocket.Vehicles.Engines;
using Sprocket.Vehicles.Engines.Editor;
using Sprocket.Vehicles.PhysicsSystems;
using Sprocket.Vehicles.Powertrains;
using Sprocket.Vehicles.Tracks;
using Sprocket.Vehicles.Transmissions;
using Sprocket.Vehicles.Transmissions.Editor;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SprocketTools;

/// Powertrain: a "Speed & acceleration" section in the Transmission and Engine panels with every gear's top speed
/// (the game's own formula at the engine's rev limit, capped by the tracks' speed limit) and how long the vehicle takes
/// to reach top speed, driven the way the game drives (see Acceleration).
[HarmonyPatch]
public static class GearSpeeds
{
    const string Title = "速度与加速";
    internal const string LogTag = "Speed & acceleration"; // English-only prefix for logs and error guards
    static string? shown, logged; // last state / inputs written to the log, so each is logged once per change
    static CombustionEngineComponentEditor? pendingEngineRedraw;
    static TransmissionEditor? pendingGearsRedraw;

    // After a change the panel isn't redrawn by itself: ask, so the numbers follow.
    // If a slider is being dragged, wait for the mouse to come up: redrawing mid-drag destroys the slider being
    // dragged, which aborts the drag and leaves it locked.
    [HarmonyPostfix, HarmonyPatch(typeof(TransmissionEditor), nameof(TransmissionEditor.OnComponentRebuilt))]
    static void GearsChanged(TransmissionEditor __instance) => Ui.Guard(LogTag, () =>
    {
        if (IsDragging()) pendingGearsRedraw = __instance;
        else __instance.RequestRedraw();
    });

    [HarmonyPostfix, HarmonyPatch(typeof(CombustionEngineComponentEditor), nameof(CombustionEngineComponentEditor.OnComponentRebuilt))]
    static void EngineChanged(CombustionEngineComponentEditor __instance) => Ui.Guard(LogTag, () =>
    {
        if (IsDragging()) pendingEngineRedraw = __instance;
        else __instance.RequestRedraw();
    });

    internal static void Update()
    {
        if (pendingEngineRedraw != null && !IsDragging())
        {
            var engine = pendingEngineRedraw;
            pendingEngineRedraw = null;
            try { engine.RequestRedraw(); } catch { }
        }
        if (pendingGearsRedraw != null && !IsDragging())
        {
            var gears = pendingGearsRedraw;
            pendingGearsRedraw = null;
            try { gears.RequestRedraw(); } catch { }
        }
    }

    internal static void LeftEditor()
    {
        pendingEngineRedraw = null;
        pendingGearsRedraw = null;
    }

    static bool IsDragging()
    {
        try
        {
            if (Mouse.current is { } mouse && mouse.leftButton.isPressed) return true;
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
    static void InTransmission(TransmissionEditor __instance, IGUILayout layout) =>
        Ui.Guard(LogTag, () => Draw(layout, __instance.Component, null, __instance.Component.Vehicle?.Mass ?? 0));

    [HarmonyPostfix, HarmonyPatch(typeof(CombustionEngineComponentEditor), nameof(CombustionEngineComponentEditor.OnGUI))]
    static void InEngine(CombustionEngineComponentEditor __instance, IGUILayout layout) =>
        Ui.Guard(LogTag, () => Draw(layout, null, __instance.blueprint, __instance.Component.Vehicle?.Mass ?? 0));

    static void Draw(IGUILayout layout, TransmissionBlock? gearbox, EngineBlueprint? engine, float mass)
    {
        var ui = layout.TryCast<IGUIElementDrawer>();
        if (ui == null) return;
        var text = Describe(gearbox, engine, mass);
        var summary = string.Join(" | ", text.Split('\n').Where(l => !l.StartsWith("档位")));
        if (summary != shown) { shown = summary; Plugin.ModLog.LogInfo($"{LogTag}: {shown}"); }
        Ui.Section(layout, Title);
        // A line each, and one more for each long line (it wraps in a narrow panel, and would run over what's below).
        ui.InfoField(text, text.Split('\n').Sum(l => l.Length > 40 ? 2 : 1));
    }

    /// Everything the drive needs, read straight off the parts.
    sealed record Drive(EngineBlueprint Engine, float[] Ratios, float FinalDrive, float Radius, float Mass, float Limit, float RevLimit,
                        float Disengage, float Engage, float EngineInertia, float SprocketInertia, float Drag, int Tracks);

    /// The tracks' losses and grip as the game set them up when the vehicle last drove (a test drive or battle): rolling
    /// resistance, sprocket drag, belt bending and friction depend on the track's technology and belt, which the design
    /// editor doesn't hold. Until then, the game's defaults for standard tracks.
    sealed record TrackPhysics(float Rolling, float RollingPerSpeed2, float Viscous, float Bending, float Friction, bool Measured);
    static TrackPhysics tracksSeen = new(0.03f, 0.001f, 0, 0, 0.8f, false);

    [HarmonyPostfix, HarmonyPatch(typeof(TrackAssembly), nameof(TrackAssembly.EnableBehaviour))]
    static void TrackStarted(TrackAssembly __instance) => Ui.Guard(LogTag, () =>
    {
        var track = __instance.Controller?.TryCast<TrackBehaviour>();
        if (track == null) return;
        var info = track.UpdateInfo;
        var job = track.UpdateJob;
        var rr = info.rrParameters;
        tracksSeen = new(rr.rollingResistanceConstant, rr.velocityDependentRollingResistanceConstant, job.viscousDragCoefficient,
                         job.bendingResistance, info.dynamicFrictionCoefficient, true);
        Plugin.ModLog.LogInfo($"{LogTag}: track physics {tracksSeen}");
    });

    /// Gear ratios (this transmission, else the powertrain's), the engine (this one, else the powertrain's), the
    /// tracks' final drive, drive sprocket and speed limit.
    static string Describe(TransmissionBlock? gearbox, EngineBlueprint? engine, float mass)
    {
        var parts = DesignEditor.Instance?.AllComponents().ToList() ?? new();
        var engines = parts.Select(c => c.TryCast<CombustionEngine>()).Where(e => e?.Blueprint != null).ToList();
        engine ??= (engines.FirstOrDefault(e => e!.SelectedInPowertrain) ?? engines.FirstOrDefault())?.Blueprint;
        var gearboxes = parts.Select(c => c.TryCast<TransmissionBlock>()).Where(t => t != null).ToList();
        gearbox ??= gearboxes.FirstOrDefault(t => t!.SelectedInPowertrain) ?? gearboxes.FirstOrDefault();
        var tracks = parts.Select(c => c.TryCast<TrackAssembly>()).Where(t => t?.BlueprintSlot?.HasBlueprint == true).ToList();
        var track = tracks.FirstOrDefault();
        if (engine == null) return "加装发动机后可查看速度。";
        if (gearbox == null) return "加装变速箱后可查看速度。";
        if (track == null) return "加装履带后可查看速度。";
        var ratios = (gearbox.resultingDriveGearRatios?.ToArray() ?? Array.Empty<float>()).Select(Math.Abs).Where(r => r > 0).ToArray();
        var reverse = (gearbox.resultingReverseGearRatios?.ToArray() ?? Array.Empty<float>()).Select(Math.Abs).Where(r => r > 0).ToArray();
        if (ratios.Length == 0) return "暂无前进档位。";
        float finalDrive = track.BlueprintSlot.Blueprint.FinalDriveRatio;
        var sprocket = track.SprocketAssembly?.WheelBlueprint;
        float radius = sprocket?.HasBlueprint == true ? sprocket.Blueprint.Radius : 0;
        if (radius <= 0 || finalDrive <= 0 || engine.MaxRPM <= 0) return "暂时读不到履带的驱动轮。";

        // The engine never revs past its rev limit: its own setting, or (by default) the upshift rpm + 50.
        float revLimit = Try(() => engine.RevLimit);
        if (revLimit <= 0 || revLimit > engine.MaxRPM) revLimit = engine.MaxRPM;
        var (disengage, engage) = ShiftTimes(gearbox);
        var d = new Drive(engine, ratios, finalDrive, radius, mass, Try(() => track.TopSpeed), revLimit, disengage, engage, Try(() => engine.Inertia),
            tracks.Sum(t => Try(() => t!.ComputeSprocketInertia())), Try(() => VehiclePhysics.DefaultLinearDrag), Math.Max(1, tracks.Count));
        var p = tracksSeen;
        string inputs = $"{engine.MaxRPM} rpm (idle {engine.IdleRPM}, upshift {engine.Upshift}, rev limit {revLimit}), {engine.MaxTorque:0} torque, " +
                        $"gears {string.Join("/", ratios.Select(r => r.ToString("0.##")))}, final drive {finalDrive:0.##}, sprocket radius {radius:0.###}, " +
                        $"mass {mass:0} kg, track limit {d.Limit * 3.6f:0.#} km/h, shift {disengage:0.##} + {engage:0.##} s, engine inertia {d.EngineInertia:0.###}, " +
                        $"sprocket inertia {d.SprocketInertia:0.###}, drag {d.Drag:0.####}, tracks {p} x {tracks.Count}";
        if (inputs != logged) { logged = inputs; Plugin.ModLog.LogInfo($"{LogTag} inputs: {inputs}"); }

        float limit = d.Limit > 0 ? d.Limit * 3.6f : float.MaxValue;
        float Speed(float ratio) => PowertrainInfo.CalculateSpeed(revLimit, ratio * finalDrive, radius) * 3.6f; // m/s -> km/h
        var text = new StringBuilder();
        for (int i = 0; i < ratios.Length; i++)
            text.Append(Speed(ratios[i]) > limit ? $"档位 {i + 1}：  {limit:0} km/h（受履带限速；纯传动极速 {Speed(ratios[i]):0}）\n" : $"档位 {i + 1}：  {Speed(ratios[i]):0} km/h\n");
        if (reverse.Length > 0) text.Append($"倒档：  {Math.Min(Speed(reverse.Min()), limit):0} km/h\n");
        bool custom = Try(() => engine.RevLimitOverride ? 1 : 0) > 0;
        text.Append(custom ? $"按您自设的转速上限 {revLimit:0} rpm\n"
                  : revLimit < engine.MaxRPM ? $"按转速上限 {revLimit:0} rpm（升档转速 {engine.Upshift} + 50）\n" : $"按最高转速 {revLimit:0} rpm\n");
        var torque = EngineTorque(engine, revLimit);
        if (torque == null) return text.ToString().TrimEnd('\n');
        // The most power the engine gives below its rev limit (not the engine's rated figure at max revs).
        var (power, powerRpm) = Enumerable.Range(0, 201).Select(k => torque.Idle + (revLimit - torque.Idle) * k / 200f)
            .Select(rpm => (Kw: torque.At(rpm) * rpm * MathF.PI / 30 / 1000, Rpm: rpm)).MaxBy(x => x.Kw);
        text.Append($"转速内最大功率 {power:0} kW（{power * 1.341f:0} 马力），出现在 {powerRpm:0} rpm\n");
        if (mass <= 0) return text.ToString().TrimEnd('\n');
        var (seconds, reached, shifts) = Accelerate(d, p, torque);
        text.Append($"0 到 {reached * 3.6f:0} km/h 约需 {seconds:0} s（换挡 {shifts} 次）\n");
        text.Append(p.Measured ? "平地、全油门、自动换挡；履带阻力取自本车最近一次行驶。"
                               : "平地、全油门、自动换挡；按标准履带阻力（先试车一次可得本车实际阻力）。");
        return text.ToString();
    }

    static float Try(Func<float> read) { try { return read(); } catch { return 0; } }

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

    /// The engine's torque (N·m) at a rev count: its torque curve from the game's power figures, scaled so its peak is
    /// the engine's max torque (works whatever unit the power comes back in), under the game's rev limiter.
    sealed record Torque(Func<float, float> At, float Idle);

    static Torque? EngineTorque(EngineBlueprint e, float revLimit)
    {
        float idle = Math.Clamp(e.IdleRPM, 1, Math.Max(1, e.MaxRPM - 1)), max = Math.Max(idle + 1, e.MaxRPM);
        static float Omega(float rpm) => rpm * MathF.PI / 30; // rad/s
        var torque = Enumerable.Range(0, 101).Select(k => idle + (max - idle) * k / 100f)
            .Select(rpm => EngineRules.CalculatePowerAtRPM(e.MaxTorque, rpm, max) / Omega(rpm)).ToArray();
        float peak = torque.Max();
        if (peak <= 0) return null;
        float Curve(float rpm)
        {
            float x = Math.Clamp((rpm - idle) / (max - idle), 0, 1) * 100;
            int k = Math.Min((int)x, 99);
            return e.MaxTorque * (torque[k] + (torque[k + 1] - torque[k]) * (x - k)) / peak;
        }
        return new(Acceleration.RevLimited(Curve, revLimit, Try(() => e.FrictionCoefficient)), idle);
    }

    /// Seconds from standing to top speed on flat ground at full throttle (see Acceleration): the engine's torque under
    /// its rev limiter, the automatic gearbox's shifts, the tracks' losses and grip, and drag. Top speed: where nothing
    /// pushes harder, or the tracks' speed limit.
    static (float Seconds, float Reached, int Shifts) Accelerate(Drive d, TrackPhysics p, Torque torque)
    {
        float weight = d.Mass * 9.81f;
        float Resist(float v) => Acceleration.TrackLosses(v, weight, p.Rolling, p.RollingPerSpeed2, p.Viscous, p.Bending, d.Tracks, d.Radius) + d.Drag * d.Mass * v;
        // The gearbox shifts up once the engine passes the upshift rpm; the engine never gets past its rev limit, so an
        // upshift set at or above it is taken just under it (else it would never leave first gear).
        float upshift = Math.Min(d.Engine.Upshift, d.RevLimit - 25);
        var drive = new Acceleration.Drivetrain(torque.At, d.Ratios, d.FinalDrive, d.Radius, d.Mass, d.EngineInertia, d.SprocketInertia,
            torque.Idle, upshift, d.Disengage, d.Engage, p.Friction * weight);
        return Acceleration.Run(drive, Resist, d.Limit > 0 ? d.Limit : float.MaxValue);
    }
}
