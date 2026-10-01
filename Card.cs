using BepInEx.Unity.IL2CPP.Utils.Collections;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering.HighDefinition;

namespace SprocketTools;

/// F10: a War Thunder tech-tree profile card of the vehicle on a white backdrop, saved as a high-resolution PNG.
/// The camera, layer culling, fog off and magenta keying follow the routes the drawing sheet (F9) proved in game;
/// only the keyed-out backdrop is written pure white (or kept transparent).
internal static class Card
{
    static bool busy;
    const int FirstSettle = 40; // frames: the exposure settles first, then the anti-aliasing fills this frame

    /// F10: take the card. Angle, side, focal length, framing, canvas size and backdrop come from the config.
    /// Mutually exclusive with F8's max-quality photo, which owns the renderer while it runs.
    internal static void Keys()
    {
        if (busy || PhotoShot.Capturing || DesignEditor.Instance is not { } editor || Keyboard.current is not { } keys || MeshTools.Typing()) return;
        if (!keys.f10Key.wasPressedThisFrame) return;
        busy = true;
        editor.StartCoroutine(Run().WrapToIl2Cpp());
    }

    static System.Collections.IEnumerator Run()
    {
        Sheet? sheet = null;
        int wide = Plugin.CardWidth?.Value ?? 2048, high = Plugin.CardHeight?.Value ?? 1024;
        int offSide = Plugin.CardSideOffset?.Value ?? 60, elevation = Plugin.CardElevation?.Value ?? 0;
        int side = Plugin.CardLeftSide?.Value == true ? -1 : 1;
        float fov = Plugin.CardFieldOfView?.Value ?? 20f, fill = Plugin.CardFraming?.Value ?? 0.92f;
        bool white = Plugin.CardWhiteBackground?.Value ?? true;
        string where = side > 0 ? "右侧" : "左侧";
        try
        {
            if (PhotoShot.Capturing) yield break; // F8 started since the key press: leave the renderer to it
            DesignEditor.Instance?.Say($"资料卡：正在摆机位（侧偏{offSide}°、俯角{elevation}°、{where}）……", 6);
            DrawingSheet.NoHover.On = true;
            for (int f = 0; f < 3; f++) yield return null;
            sheet = Sheet.Begin();
            if (sheet == null) yield break;
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Sprocket", "Photos");
            Directory.CreateDirectory(dir);
            int azimuth = 90 - offSide;   // the side offset counts from the profile view (offset 90 = head-on at the front)
            if (!sheet.AimAt(azimuth, elevation, wide, high, fov, side, fill)) yield break;
            if (Plugin.CardLightingProbe?.Value == true)
            {
                DesignEditor.Instance?.Say($"资料卡探针：连拍 {Variants.Length} 组打光，机位不变……", 10);
                foreach (var v in Variants)
                {
                    sheet.Apply(v);
                    for (int f = 0; f < FirstSettle; f++) { sheet.Hold(); PlaceFills(); yield return null; }
                    if (sheet.Grab(wide, high) is not { } shot) continue;
                    var probeFile = Path.Combine(dir, $"资料卡探针 {v.No} {v.Name}.png");
                    Drawing.SavePng(probeFile, wide, high, shot, rgba: !white);
                    Plugin.ModLog.LogInfo($"CARD_PROBE {v.No} {v.Name} -> {probeFile}");
                }
                DesignEditor.Instance?.Say($"资料卡探针完成：{Variants.Length} 张已存到 Photos 目录", 8);
            }
            else
            {
                var v = Recipe(); // 打光：按配置选探针里的那一组；0=不动
                if (v != null) sheet.Apply(v);
                for (int f = 0; f < FirstSettle; f++) { sheet.Hold(); PlaceFills(); yield return null; }
                if (sheet.Grab(wide, high) is not { } picture) yield break;
                var file = Path.Combine(dir, $"资料卡 {Name()}.png");
                Drawing.SavePng(file, wide, high, picture, rgba: !white);
                Plugin.ModLog.LogInfo($"CARD_SAVED side offset {offSide} elevation {elevation} {(side > 0 ? "right" : "left")} fov {fov:0} framing {fill * 100:0}% lighting {(v == null ? "0 as-is" : $"{v.No} {v.Name}")} {wide}x{high} backdrop {(white ? "white" : "transparent")} -> {file}");
                DesignEditor.Instance?.Say($"资料卡已保存：{file}", 6);
            }
        }
        finally
        {
            sheet?.End();
            DrawingSheet.NoHover.On = false;
            busy = false;
        }
    }

    /// What the picture is named after: the design's own name, as F9 uses it, so a vehicle's drawings and cards sort
    /// together. The moment's time when the design has no name worth printing.
    static string Name()
    {
        var design = DrawingSheet.DesignName();
        return design.Length > 0 ? design : DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss");
    }

    // ---------- the card's own fill lights ----------
    // A set of its own, kept apart from F7's fullbright (MeshTools.ToggleFullbright): shooting a card must not
    // change the state of the player's F7 lights, and F7's own set (named "SprocketTools fill light") is only
    // looked at, never touched. The card's lights are "PhotoCard fill light"; that name is compared, keep it.

    // Lights whose shadows are off, with what they had, to give it back exactly.
    static readonly List<(Light Light, LightShadows Was)> shadowless = new();
    static readonly List<GameObject> fills = new();
    static bool fullbrightShadows; // whether turning the fills on also turned the shadows off (then it turns them back)
    static float fillsAt = -10;

    /// The card's own fills are on: counted from our list only, F7's set doesn't count (and vice versa).
    static bool FillsOn { get { fills.RemoveAll(f => f == null); return fills.Count > 0; } }

    static float FillShare => (Plugin.CardFullbrightPercent?.Value ?? 25) / 100; // of the sun's strength, per light
    // From every side and every corner: 14 lights (with the sun, within the 16 directional lights HDRP draws at once).
    static readonly Vector3[] FillFrom = new[] { Vector3.down, Vector3.up, Vector3.left, Vector3.right, Vector3.forward, Vector3.back }
        .Concat(Enumerable.Range(0, 8).Select(i => new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1).normalized)).ToArray();

    /// Even light from every side with shadows off, so every face shows clearly whichever way it faces.
    static void ToggleFills() => Ui.Guard("Photo card fills", () =>
    {
        fills.RemoveAll(f => f == null); // gone with a scene change
        if (fills.Count > 0)
        {
            foreach (var f in fills) UnityEngine.Object.Destroy(f);
            fills.Clear();
            if (fullbrightShadows && shadowless.Count > 0) ToggleShadows();
            fullbrightShadows = false;
            return;
        }
        var sun = Sun();
        if (sun == null) { Plugin.ModLog.LogWarning("CARD_LIGHT no sun to measure against in this scene: the fills can't be placed"); return; }
        if (shadowless.Count == 0)
        {
            ToggleShadows();
            fullbrightShadows = true;
        }
        // Point lights round the vehicle (the game's renderer draws only one directional light, the sun's).
        foreach (var dir in FillFrom)
        {
            var go = new GameObject("PhotoCard fill light");
            var light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = sun.color;
            light.useColorTemperature = sun.useColorTemperature;
            light.colorTemperature = sun.colorTemperature;
            light.shadows = LightShadows.None;
            var hd = LikeTheSun(light, sun);
            hd.EnableShadows(false);
            light.shadows = LightShadows.None;
            fills.Add(go);
        }
        fillsAt = -10;
        PlaceFills();
        Plugin.ModLog.LogInfo($"CARD_LIGHT {fills.Count} point lights round the vehicle, {SunLux(sun) * FillShare:0} lux each on it ({FillShare:P0} of the sun); the renderer draws {MaxDirectional()} directional light(s) at once");
    });

    /// Even light means no shadow anywhere, so every light already casting one goes off first; then the fills go on.
    /// (The card has no key of its own for shadows: only ToggleFills gets here.)
    static void ToggleShadows()
    {
        if (shadowless.Count > 0)
        {
            foreach (var (light, was) in shadowless)
                if (light != null)
                {
                    light.GetComponent<UnityEngine.Rendering.HighDefinition.HDAdditionalLightData>()?.EnableShadows(true);
                    light.shadows = was;
                }
            shadowless.Clear();
            return;
        }
        foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Light>()))
            if (o.TryCast<Light>() is { } light && light.shadows != LightShadows.None)
            {
                shadowless.Add((light, light.shadows));
                light.GetComponent<UnityEngine.Rendering.HighDefinition.HDAdditionalLightData>()?.EnableShadows(false);
                light.shadows = LightShadows.None;
            }
        Plugin.ModLog.LogInfo($"CARD_SHADOWS shadows off on {shadowless.Count} lights");
    }

    // Sun / LikeTheSun / MaxDirectional / SunLux are the card's own copies: MeshTools has the same helpers but they
    // are private to it, and its Sun() filters our lights out by the other set's name ("SprocketTools").

    /// A new light lights what the sun lights: the same light layers (HDRP lights only reach objects on their layers,
    /// and a new light starts on the default one). Returns its HDRP settings.
    static UnityEngine.Rendering.HighDefinition.HDAdditionalLightData LikeTheSun(Light light, Light sun)
    {
        light.cullingMask = sun.cullingMask;
        light.renderingLayerMask = sun.renderingLayerMask;
        var hd = light.GetComponent<UnityEngine.Rendering.HighDefinition.HDAdditionalLightData>() ?? light.gameObject.AddComponent<UnityEngine.Rendering.HighDefinition.HDAdditionalLightData>();
        if (sun.GetComponent<UnityEngine.Rendering.HighDefinition.HDAdditionalLightData>() is { } sunHd) hd.lightlayersMask = sunHd.lightlayersMask;
        // Not a sun in the sky: the sky dims a directional light near the horizon and blacks it out below it (so light
        // from below, from the sides and back did nearly nothing). Ours light the vehicle at full strength from anywhere.
        hd.interactsWithSky = false;
        return hd;
    }

    /// How many directional lights the renderer draws at once (lights past it are left out), 16 if it won't say.
    static int MaxDirectional()
    {
        try
        {
            if (UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline?.TryCast<UnityEngine.Rendering.HighDefinition.HDRenderPipelineAsset>() is { } asset)
                return asset.currentPlatformRenderPipelineSettings.lightLoopSettings.maxDirectionalLightsOnScreen;
        }
        catch (Exception ex) { Plugin.ModLog.LogWarning($"Photo card: couldn't read the directional light limit: {ex.Message}"); }
        return 16;
    }

    static float SunLux(Light sun) => sun.GetComponent<UnityEngine.Rendering.HighDefinition.HDAdditionalLightData>() is { } hd ? hd.intensity : sun.intensity;

    /// The brightest directional light that isn't one of ours.
    static Light? Sun()
    {
        Light? sun = null;
        foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Light>()))
            if (o.TryCast<Light>() is { } light && light.type == LightType.Directional && !light.name.StartsWith("PhotoCard") && (sun == null || light.intensity > sun.intensity)) sun = light;
        return sun;
    }

    /// The fill lights round the vehicle, five times its size away (so each side is lit evenly), each as strong as it
    /// takes to put the set share of the sun on the vehicle (candela = lux x distance squared). Again every 2 s: the
    /// vehicle grows as it's built.
    static void PlaceFills()
    {
        fills.RemoveAll(f => f == null);
        if (fills.Count == 0 || Time.unscaledTime - fillsAt < 2) return;
        fillsAt = Time.unscaledTime;
        var sun = Sun();
        if (sun == null || MeshTools.VehicleBounds() is not { } box) return;
        float radius = Math.Max(0.5f, box.extents.magnitude), distance = 5 * radius + 5;
        for (int i = 0; i < fills.Count && i < FillFrom.Length; i++)
        {
            fills[i].transform.position = box.center - FillFrom[i] * distance; // it shines along FillFrom[i]
            fills[i].GetComponent<Light>().range = 3 * (distance + radius);
            fills[i].GetComponent<UnityEngine.Rendering.HighDefinition.HDAdditionalLightData>()?.SetIntensity(SunLux(sun) * FillShare * distance * distance, UnityEngine.Rendering.LightUnit.Candela);
        }
    }

    /// The five indirect-lighting switches the card camera pins off by default. The probe re-enables subsets of them.
    static readonly FrameSettingsField[] IndirectFields =
    {
        FrameSettingsField.SSR, FrameSettingsField.SSGI, FrameSettingsField.ReflectionProbe,
        FrameSettingsField.PlanarProbe, FrameSettingsField.SkyReflection
    };
    static readonly FrameSettingsField[] NoIndirect = { };
    static readonly FrameSettingsField[] SkySet = { FrameSettingsField.SSGI, FrameSettingsField.SkyReflection, FrameSettingsField.ReflectionProbe };

    /// One lighting setup of the F10 probe. Every axis is varied alone against variant 2 (sky reflections on),
    /// so each picture answers exactly one question about the reference card's look.
    internal sealed class Variant
    {
        internal int No;
        internal string Name = "";
        internal FrameSettingsField[] Keep = NoIndirect;
        internal string Fill = "";            // "", "sky", "wheels": which extra fill lights to stand up
        internal float SunMul = 1, Exposure, Temperature;   // sun multiplier, post-exposure EV, white-balance kelvin
    }

    internal static readonly Variant[] Variants =
    {
        new() { No = 0, Name = "基线" },
        new() { No = 1, Name = "SSGI", Keep = new[] { FrameSettingsField.SSGI } },
        new() { No = 2, Name = "天反", Keep = SkySet },
        new() { No = 3, Name = "全开", Keep = IndirectFields },
        new() { No = 4, Name = "天光补", Keep = SkySet, Fill = "sky" },
        new() { No = 5, Name = "轮位补", Keep = SkySet, Fill = "wheels" },
        new() { No = 6, Name = "提亮", Keep = SkySet, Exposure = 0.7f },
        new() { No = 7, Name = "冷调", Keep = SkySet, Temperature = 4500 },
    };

    const float ProbeFillShare = 0.15f;   // of the sun's lux, per probe fill light

    /// 正常出图用哪组打光：配置 [Picture] Lighting recipe，编号跟探针那 8 张一一对应；0 或写错=不加，跟以前那张一样。
    static Variant? Recipe()
    {
        int no = Plugin.CardLightingRecipe?.Value ?? 5;
        return no > 0 ? Variants.FirstOrDefault(v => v.No == no) : null;
    }

    // ---------- the probe's own lights, sun and grade ----------
    // All three are stood up per variant and taken down in Sheet.End, so a probe that dies halfway leaves the
    // scene as it found it. The fill lights carry the "PhotoCard" name so Sun() keeps filtering them out.

    static readonly List<GameObject> probeFills = new();
    static Light? probedSun;
    static float sunWasLux = -1;
    static GameObject? probeVolumeGo;
    static UnityEngine.Rendering.HighDefinition.ColorAdjustments? probeGrade;
    static UnityEngine.Rendering.HighDefinition.WhiteBalance? probeBalance;

    static void ClearProbeLights()
    {
        probeFills.RemoveAll(f => f == null);
        foreach (var f in probeFills) UnityEngine.Object.Destroy(f);
        probeFills.Clear();
        if (probedSun != null && sunWasLux >= 0)
        {
            if (probedSun.GetComponent<UnityEngine.Rendering.HighDefinition.HDAdditionalLightData>() is { } hd)
                hd.SetIntensity(sunWasLux, UnityEngine.Rendering.LightUnit.Lux);
            else probedSun.intensity = sunWasLux;
        }
        probedSun = null; sunWasLux = -1;
    }

    static void ClearProbeVolume()
    {
        if (probeVolumeGo != null) UnityEngine.Object.Destroy(probeVolumeGo);
        probeVolumeGo = null; probeGrade = null; probeBalance = null;
    }

    /// The variant's sun strength: the first call remembers the scene's own lux so End can hand it back.
    static void ProbeSun(float mul)
    {
        var sun = Sun();
        if (sun == null) return;
        if (sunWasLux < 0) { probedSun = sun; sunWasLux = SunLux(sun); }
        if (sun.GetComponent<UnityEngine.Rendering.HighDefinition.HDAdditionalLightData>() is { } hd)
            hd.SetIntensity(sunWasLux * mul, UnityEngine.Rendering.LightUnit.Lux);
        else sun.intensity = sunWasLux * mul;
    }

    /// The variant's fill lights: "sky" is one lamp straight above the vehicle, "wheels" two low lamps on the
    /// camera's side at the front and rear of the running gear, where the hull's shadow leaves nothing to light it.
    static void ProbeFills(Sheet sheet, Variant v)
    {
        probeFills.RemoveAll(f => f == null);
        foreach (var f in probeFills) UnityEngine.Object.Destroy(f);
        probeFills.Clear();
        if (v.Fill.Length == 0) return;
        var sun = Sun();
        if (sun == null || MeshTools.VehicleBounds() is not { } box) return;
        float radius = Math.Max(0.5f, box.extents.magnitude), distance = 5 * radius + 5;
        var spots = new List<Vector3>();
        if (v.Fill.Contains("sky")) spots.Add(box.center + Vector3.up * distance);
        if (v.Fill.Contains("wheels"))
            foreach (int s in new[] { -1, 1 })
            {
                var at = box.center + sheet.ToCam * (distance * 0.7f);
                at.y = box.min.y + 0.3f * box.size.y;
                at += new Vector3(0, 0, s * 0.4f * box.size.z);
                spots.Add(at);
            }
        foreach (var at in spots)
        {
            var go = new GameObject("PhotoCard fill light");
            var light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = sun.color;
            light.useColorTemperature = sun.useColorTemperature;
            light.colorTemperature = sun.colorTemperature;
            light.shadows = LightShadows.None;
            var hd = LikeTheSun(light, sun);
            hd.EnableShadows(false);
            light.shadows = LightShadows.None;
            float dist = (at - box.center).magnitude;
            light.range = 3 * (dist + radius);
            hd.SetIntensity(SunLux(sun) * ProbeFillShare * dist * dist, UnityEngine.Rendering.LightUnit.Candela);
            go.transform.position = at;
            probeFills.Add(go);
        }
        Plugin.ModLog.LogInfo($"CARD_PROBE_LIGHTS {probeFills.Count} fill light(s) at {ProbeFillShare:P0} of the sun ({SunLux(sun) * ProbeFillShare:0} lux on the vehicle)");
    }

    /// The variant's grade on a global volume of the card's own: post-exposure in EV and a white-balance shift.
    /// Components are added through the non-generic Add(Type) and edited as their concrete parameter classes, so no
    /// generic instantiation the game never compiled is asked for.
    static void ProbeVolume(Variant v)
    {
        bool need = v.Exposure != 0 || v.Temperature != 0;
        if (!need) { ClearProbeVolume(); return; }
        if (probeVolumeGo == null)
        {
            probeVolumeGo = new GameObject("PhotoCard volume");
            var vol = probeVolumeGo.AddComponent<UnityEngine.Rendering.Volume>();
            vol.isGlobal = true;
            vol.priority = 1000;
            var live = vol.profile;   // the profile this volume actually blends from, internal copy included
            probeGrade = live.Add(Il2CppType.Of<UnityEngine.Rendering.HighDefinition.ColorAdjustments>(), true)
                ?.TryCast<UnityEngine.Rendering.HighDefinition.ColorAdjustments>();
            probeBalance = live.Add(Il2CppType.Of<UnityEngine.Rendering.HighDefinition.WhiteBalance>(), true)
                ?.TryCast<UnityEngine.Rendering.HighDefinition.WhiteBalance>();
            Plugin.ModLog.LogInfo($"CARD_PROBE_VOLUME global volume up: grade {(probeGrade != null ? "ok" : "MISSING")}, balance {(probeBalance != null ? "ok" : "MISSING")}");
        }
        if (probeGrade != null)
        {
            probeGrade.active = v.Exposure != 0;
            probeGrade.postExposure.overrideState = true;
            probeGrade.postExposure.value = v.Exposure;
        }
        if (probeBalance != null)
        {
            probeBalance.active = v.Temperature != 0;
            probeBalance.temperature.overrideState = true;
            probeBalance.temperature.value = v.Temperature;
        }
    }

    /// Everything one shot changes on set: the camera, the layers kept and hidden, the fog and the fills, and how to
    /// put it all back afterwards.
    sealed class Sheet
    {
        static readonly Color Behind = new(1, 0, 1); // the colour behind the vehicle: no paint is this magenta, keying it out is how the backdrop goes
        Bounds box;
        Camera? cam;
        HDAdditionalCameraData? camHd;
        Vector3 toCamDir = Vector3.forward; // unit vector from the vehicle to the camera, for the probe's fill spots
        internal Vector3 ToCam => toCamDir;
        RenderTexture? target;
        int layers;
        bool flat, fullbrightWas, fogWas;
        HashSet<IntPtr> aerials = new(); // antennas: a whip metres tall would distort the bounding box
        // Everything else in the scene hides for the shot: the editor's workshop floor and walls share the vehicle's
        // layers and stand right behind it.
        readonly List<Renderer> hidden = new();
        readonly List<Terrain> hiddenGround = new();
        readonly List<DecalProjector> projectors = new();
        readonly List<DecalProjector> hiddenEnvironmentProjectors = new();
        readonly List<(DecalProjector Projector, float Distance, float FadeScale)> restoredDecals = new();
        int ownLayer = -1; // the layer the vehicle moves to for the shot (-1: no free layer, shoot on its own layers)
        readonly Dictionary<IntPtr, (GameObject Object, int Layer)> movedLayers = new();

        internal static Sheet? Begin()
        {
            var sheet = new Sheet();
            try { return sheet.Setup() ? sheet : null; }
            catch (Exception ex) { Fail("could not begin", "无法开始", ex); sheet.End(); return null; }
        }

        bool Setup()
        {
            var main = Camera.main;
            // Only the vehicle, lit evenly: its own layers (no sky or the editor's handles), everything else hidden.
            var vehicle = new HashSet<IntPtr>();
            Bounds? around = null;
            Renderer? topPart = null, lowPart = null; // which part sets the box's top and bottom: an inflated mesh bound here lifts the camera and shrinks the car
            // Antennas left off: a whip metres tall would set the vehicle's height and leave the views empty.
            aerials = MeshTools.AntennaRenderers();
            var vehicleParts = (DesignEditor.Instance?.AllParts() ?? Enumerable.Empty<Sprocket.Vehicles.VehicleObject>()).ToList();
            var vehiclePartPointers = vehicleParts.Select(p => p.Pointer).ToHashSet();
            foreach (var part in vehicleParts)
                foreach (var r in part.GetComponentsInChildren<Renderer>())
                {
                    if (aerials.Contains(r.Pointer) || !MeshTools.Drawn(r)) continue;
                    layers |= 1 << r.gameObject.layer;
                    vehicle.Add(r.Pointer);
                    if (topPart == null || r.bounds.max.y > topPart.bounds.max.y) topPart = r;
                    if (lowPart == null || r.bounds.min.y < lowPart.bounds.min.y) lowPart = r;
                    if (around is { } a) { a.Encapsulate(r.bounds); around = a; } else around = r.bounds;
                }
            if (main == null || around is not { } b) { DesignEditor.Instance?.Say("资料卡：这一场里没有可拍的载具", 4); return false; }
            box = b;
            Plugin.ModLog.LogInfo($"CARD_BBOX box {box.min.y:0.00}..{box.max.y:0.00} m over the part origins: top set by {(topPart != null ? $"{topPart.gameObject.name} at {topPart.bounds.max.y:0.00}" : "-")}, " +
                                  $"bottom by {(lowPart != null ? $"{lowPart.gameObject.name} at {lowPart.bounds.min.y:0.00}" : "-")}");
            var size = box.size;
            MeshTools.LogMaterials("CARD"); // before anything is switched off: what the vehicle is made of as the game has it

            // Vehicle decals: find all projectors on vehicle parts or components.
            foreach (var part in vehicleParts)
                foreach (var dp in part.GetComponentsInChildren<DecalProjector>(true))
                    if (dp != null && dp.enabled && dp.gameObject.activeInHierarchy && !projectors.Any(p => p.Pointer == dp.Pointer))
                        projectors.Add(dp);

            foreach (var c in DesignEditor.Instance?.AllComponents() ?? Enumerable.Empty<Sprocket.Vehicles.VehicleComponent>())
                if (c?.TryCast<Sprocket.Vehicles.AttachedBehaviours.ProjectedDecal>() is { } pd && pd.projector is { } dp)
                    if (dp != null && dp.enabled && dp.gameObject.activeInHierarchy && !projectors.Any(p => p.Pointer == dp.Pointer))
                        projectors.Add(dp);

            foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<DecalProjector>()))
            {
                if (o.TryCast<DecalProjector>() is { } dp)
                {
                    if (projectors.Any(p => p.Pointer == dp.Pointer)) continue;
                    if (dp.GetComponentInParent<Sprocket.Vehicles.VehicleObject>() is { } vo && vehiclePartPointers.Contains(vo.Pointer))
                    {
                        if (dp.enabled && dp.gameObject.activeInHierarchy)
                            projectors.Add(dp);
                    }
                    else if (dp.enabled)
                    {
                        dp.enabled = false;
                        hiddenEnvironmentProjectors.Add(dp);
                    }
                }
            }

            foreach (var dp in projectors)
                if (dp.gameObject != null) layers |= 1 << dp.gameObject.layer;

            var used = new HashSet<int>();
            foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Renderer>()))
                if (o.TryCast<Renderer>() is { } r)
                {
                    used.Add(r.gameObject.layer);
                    if (r.enabled && !vehicle.Contains(r.Pointer)) { r.enabled = false; hidden.Add(r); }
                }
            foreach (var dp in projectors)
                if (dp.gameObject != null) used.Add(dp.gameObject.layer);
            foreach (var t in Terrain.activeTerrains)
                if (t != null && t.enabled) { t.enabled = false; hiddenGround.Add(t); }
            // The vehicle on a layer of its own while the views are taken, and the camera sees only that: what the map
            // draws without a renderer to switch off (its grass) stays out of the pictures. Back in End.
            for (int l = 31; l >= 8 && ownLayer < 0; l--)
                if (!used.Contains(l) && string.IsNullOrEmpty(LayerMask.LayerToName(l))) ownLayer = l;
            if (ownLayer >= 0)
            {
                foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Renderer>()))
                    if (o.TryCast<Renderer>() is { } r && vehicle.Contains(r.Pointer) && !movedLayers.ContainsKey(r.gameObject.Pointer))
                    {
                        movedLayers[r.gameObject.Pointer] = (r.gameObject, r.gameObject.layer);
                        r.gameObject.layer = ownLayer;
                    }
                foreach (var dp in projectors)
                {
                    if (dp.gameObject == null) continue;
                    foreach (var t in dp.gameObject.GetComponentsInChildren<Transform>())
                    {
                        if (t?.gameObject != null && !movedLayers.ContainsKey(t.gameObject.Pointer))
                        {
                            movedLayers[t.gameObject.Pointer] = (t.gameObject, t.gameObject.layer);
                            t.gameObject.layer = ownLayer;
                        }
                    }
                }
            }
            float minDistance = 200f;
            foreach (var dp in projectors)
            {
                if (dp != null)
                {
                    float origDist = dp.drawDistance;
                    float origFade = dp.fadeScale;
                    if (origDist < minDistance || origFade < 1f)
                    {
                        restoredDecals.Add((dp, origDist, origFade));
                        if (origDist < minDistance) dp.drawDistance = minDistance;
                        if (origFade < 1f) dp.fadeScale = 1f;
                    }
                }
            }
            // The game's fog, off too: it stood in front of the backdrop (below hull height, and all of the view from
            // above) and hazed the paint.
            fogWas = MeshTools.FogOff;
            MeshTools.Fog(off: true);
            // Even light is optional and off by default: a card wants the vehicle's own shading and shadows, not a
            // clay-model look with every face equally bright. SprocketTools's own F7 fullbright counts as one set
            // already on: don't add a second (HDRP budgets its lights; two sets overexpose the vehicle and drop some).
            flat = Plugin.CardFlatLighting?.Value ?? false;
            bool otherFills = MeshTools.FullbrightOn;
            fullbrightWas = FillsOn || otherFills;
            if (flat && !fullbrightWas) ToggleFills();
            else Plugin.ModLog.LogInfo($"CARD_LIGHT flat {(flat ? "on" : "off")}: {(flat ? "keeping the fills already on the set" : "shooting under the game's own sun and shadows")}" +
                                       (otherFills ? " (note: SprocketTools's fullbright fills are on and will flatten the picture)" : ""));
            var go = new GameObject("PhotoCard camera");
            cam = go.AddComponent<Camera>();
            cam.CopyFrom(main);
            if (main.GetComponent<HDAdditionalCameraData>() is { } hd)
            {
                var own = go.AddComponent<HDAdditionalCameraData>();
                hd.CopyTo(own);
                camHd = own;
                own.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color;
                own.backgroundColorHDR = Behind;
                // Not temporal: each view is still, but the camera jumps between them.
                own.antialiasing = HDAdditionalCameraData.AntialiasingMode.SubpixelMorphologicalAntiAliasing;
                own.customRenderingSettings = true;
                // The paint's stencil decals on, their layer filter off (it would clip them to the editor's layers).
                // The five indirect switches go through Indirect, which the lighting probe re-runs per variant.
                SetField(FrameSettingsField.Decals, true, pin: true);
                SetField(FrameSettingsField.DecalLayers, false, pin: true);
                Indirect(NoIndirect);
            }
            cam.orthographic = true;
            cam.cullingMask = ownLayer >= 0 ? 1 << ownLayer : layers;
            DesignEditor.Instance?.Say("资料卡：正在摆机位……", 6);
            Plugin.ModLog.LogInfo($"CARD_STAGE vehicle {size.x:0.00} x {size.y:0.00} x {size.z:0.00} m, {aerials.Count} antenna renderers kept out of the box, {projectors.Count} decals, " +
                                  (ownLayer >= 0 ? $"{movedLayers.Count} objects moved to layer {ownLayer} for the shot" : "no free layer: shooting on the vehicle's own layers"));
            return true;
        }

        /// Every frame the views are taken: the editor moves the part under the mouse to a highlight layer when the mouse
        /// goes over it or off it (out of the pictures), so any part it moved goes back on ours. The layer the editor last
        /// chose is the one the part gets back at the end.
        internal void Hold()
        {
            if (ownLayer < 0) return;
            List<IntPtr>? moved = null;
            foreach (var (key, (obj, _)) in movedLayers)
                if (obj != null && obj.layer != ownLayer) (moved ??= new()).Add(key);
            if (moved == null) return;
            foreach (var key in moved)
            {
                var obj = movedLayers[key].Object;
                movedLayers[key] = (obj, obj.layer);
                obj.layer = ownLayer;
            }
        }

        /// One frame-settings switch on the card camera, pinned in the override mask so the scene's own volumes
        /// can't put it back. Read-modify-write: the frame settings are a struct, so a member call on the property's
        /// copy alone would be thrown away with the copy.
        void SetField(FrameSettingsField field, bool on, bool pin)
        {
            if (camHd == null) return;
            var fs = camHd.renderingPathCustomFrameSettings;
            fs.SetEnabled(field, on);
            camHd.renderingPathCustomFrameSettings = fs;
            if (!pin) return;
            var mask = camHd.renderingPathCustomFrameSettingsOverrideMask;
            mask.mask[(uint)field] = true;
            camHd.renderingPathCustomFrameSettingsOverrideMask = mask;
        }

        /// Which of the five indirect-lighting switches stay on for this shot. All off by default: seen from above,
        /// the paint mirrored the sky and the map's grass round the editor. The probe walks the subsets and the
        /// read-back line proves each write landed (the settings are a struct copy across the interop boundary).
        internal void Indirect(FrameSettingsField[] keep)
        {
            foreach (var field in IndirectFields) SetField(field, keep.Contains(field), pin: true);
            if (camHd == null) return;
            var back = camHd.renderingPathCustomFrameSettings;
            Plugin.ModLog.LogInfo($"CARD_LIGHT indirect {string.Join("+", keep.Select(k => k.ToString()))}: readback " +
                                  string.Join(" ", IndirectFields.Select(f => $"{f}={(back.IsEnabled(f) ? "on" : "off")}")));
        }

        /// One probe variant on: its indirect switches, its sun strength, its fill lights and its grade.
        internal void Apply(Variant v)
        {
            Indirect(v.Keep);
            ProbeSun(v.SunMul);
            ProbeFills(this, v);
            ProbeVolume(v);
        }

        /// Point the camera at the vehicle from a given azimuth/elevation, in perspective, so its projection fills
        /// `fill` of the frame. side=+1 puts the camera on the vehicle's right (+x): the nose points right in the
        /// picture (how the profile cards read); side=-1 is the left, nose left. The correspondence isn't a guess: it's
        /// reversed from the proven route above (the sheet's side view looks along +x from -x, printing nose left, so
        /// moving the camera to +x mirrors the picture). Framing solves the distance by projecting the bounds box's
        /// eight corners through the camera's own right/up axes: a bounding-sphere radius would shrink the vehicle to
        /// a third of the frame height.
        internal bool AimAt(int azimuthDeg, int elevationDeg, int width, int height, float fieldOfView, int side, float fill = 0.92f)
        {
            try
            {
                float az = azimuthDeg * Mathf.Deg2Rad, el = elevationDeg * Mathf.Deg2Rad;
                // The nose is +z and right is +x: azimuth 0 is dead ahead, more swings round to the camera's side.
                var toCam = new Vector3(side * MathF.Sin(az) * MathF.Cos(el), MathF.Sin(el), MathF.Cos(az) * MathF.Cos(el));
                toCamDir = toCam.normalized;
                float radius = box.extents.magnitude;
                float half = fieldOfView * 0.5f * Mathf.Deg2Rad;
                float aspect = width / (float)height;

                // The line of sight aims at a chosen fraction of the vehicle's height instead of the box's middle:
                // the middle puts the camera above the hull top and the card reads as a top-down shot. The camera
                // rides at that same height, so with elevation 0 the view stays level.
                float aimPct = (Plugin.CardAimHeight?.Value ?? 40) / 100f;
                var aim = new Vector3(box.center.x, box.min.y + box.size.y * aimPct, box.center.z);

                // The camera's own axes (Unity's left-handed: right = up x forward).
                var fwd = -toCam.normalized;
                var right = Vector3.Cross(Vector3.up, fwd).normalized;
                var up = Vector3.Cross(fwd, right);
                var corners = new Vector3[8];
                for (int c = 0; c < 8; c++)
                    corners[c] = new Vector3(c % 2 == 0 ? box.min.x : box.max.x, c % 4 < 2 ? box.min.y : box.max.y, c < 4 ? box.min.z : box.max.z);
                float halfAcross = 0, halfHigh = 0;
                foreach (var c in corners)
                {
                    var o = c - aim;
                    halfAcross = Math.Max(halfAcross, Math.Abs(Vector3.Dot(o, right)));
                    halfHigh = Math.Max(halfHigh, Math.Abs(Vector3.Dot(o, up)));
                }

                // Distance: estimate with the whole vehicle on the centre plane, then correct by projecting the real
                // corners. Perspective can't be skipped: on an 8 m vehicle the near corner sits over 3 m in front of
                // the centre plane, and sizing by the plane alone pushes the muzzle out of frame.
                float tan = MathF.Tan(half), need = fill, dist = Math.Max(halfAcross / (fill * tan * aspect), halfHigh / (fill * tan));
                for (int pass = 0; pass < 8; pass++)
                {
                    var probe = aim + toCam * dist;
                    need = 0;
                    foreach (var c in corners)
                    {
                        var v = c - probe;
                        float depth = Vector3.Dot(v, fwd);
                        if (depth < 0.5f) { need = Math.Max(need, 4f); continue; }   // a corner near the camera: back off hard
                        need = Math.Max(need, Math.Max(Math.Abs(Vector3.Dot(v, right)) / (depth * tan * aspect),
                                                       Math.Abs(Vector3.Dot(v, up)) / (depth * tan)));
                    }
                    if (MathF.Abs(need - fill) < 0.004f) break;
                    dist *= need / fill;
                }
                var at = aim + toCam * dist;
                cam!.transform.SetPositionAndRotation(at, Quaternion.LookRotation(aim - at, Vector3.up));
                cam.orthographic = false;
                cam.fieldOfView = fieldOfView;
                cam.aspect = aspect;
                cam.nearClipPlane = Math.Max(0.05f, dist - radius - 0.5f);   // the wide shot stands very close: 0.5 m is enough, don't waste depth precision on the far end
                cam.farClipPlane = dist + radius + 5f;
                if (target != null) { cam.targetTexture = null; target.Release(); UnityEngine.Object.Destroy(target); }
                target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                cam.targetTexture = target;
                Plugin.ModLog.LogInfo($"CARD_CAM distance {dist:0.0} m, camera {at.y - box.min.y:0.00} m over the ground (roof {box.max.y - box.min.y:0.00} m, aim {aim.y - box.min.y:0.00} m = {aimPct * 100:0}% of the height), " +
                                      $"elevation {elevationDeg}, view filled to {need / fill * 100:0}% (target {fill * 100:0}%)");
                return true;
            }
            catch (Exception ex) { Fail($"camera setup failed (side offset {90 - azimuthDeg}, elevation {elevationDeg})", $"摆机位失败（侧偏{90 - azimuthDeg}°、俯角{elevationDeg}°）", ex); return false; }
        }

        /// Grab one picture (rows bottom-up, as Drawing.SavePng expects). The frame carries no usable alpha (both
        /// probes on the render showed a=255 everywhere), so the backdrop is keyed out by its magenta: the same
        /// proven test the colour sheet uses. White writes 255,255,255 where the backdrop is; transparent writes
        /// a=0 (and the file goes out as RGBA).
        internal byte[]? Grab(int width, int height)
        {
            try
            {
                bool white = Plugin.CardWhiteBackground?.Value ?? true;
                var was = RenderTexture.active;
                RenderTexture.active = target;
                var picture = new Texture2D(width, height, TextureFormat.RGBA32, 1, false);
                picture.ReadPixelsImpl(new Rect(0, 0, width, height), 0, 0, false);
                RenderTexture.active = was;
                var pixels = picture.GetPixels32();
                UnityEngine.Object.Destroy(picture);
                int step = white ? 3 : 4;
                var outPixels = new byte[width * height * step];
                int back = 0;
                for (int p = 0; p < pixels.Length; p++)
                {
                    var c = pixels[p];
                    // The same proven magenta test as the drawing sheet's Grab(): red and blue close, green far below them.
                    int lo = Math.Min(c.r, c.b), hi = Math.Max(c.r, c.b);
                    if (lo > 30 && c.g < lo / 2 && hi - lo < hi * 0.35f)
                    {
                        back++;
                        if (white) { outPixels[p * step] = outPixels[p * step + 1] = outPixels[p * step + 2] = 255; }
                        continue; // transparent backdrop: a=0, colours cleared too, so no magenta fringe survives
                    }
                    outPixels[p * step] = c.r; outPixels[p * step + 1] = c.g; outPixels[p * step + 2] = c.b;
                    if (!white) outPixels[p * step + 3] = 255;
                }
                Plugin.ModLog.LogInfo($"CARD_GRAB {width}x{height} backdrop {(white ? "white" : "transparent")}: corner a={pixels[0].a}, keyed as backdrop {back * 100 / pixels.Length}% (the render texture's own alpha is unusable, keying is the only way)");
                return outPixels;
            }
            catch (Exception ex) { Fail("picture grab failed", "取画面失败", ex); return null; }
        }

        internal void End()
        {
            try
            {
                if (cam != null) { cam.targetTexture = null; UnityEngine.Object.Destroy(cam.gameObject); }
                if (target != null) { target.Release(); UnityEngine.Object.Destroy(target); }
                cam = null; target = null;
                ClearProbeLights();
                ClearProbeVolume();
                if (!fullbrightWas && FillsOn) ToggleFills();
                foreach (var r in hidden) if (r != null) r.enabled = true;
                foreach (var dp in hiddenEnvironmentProjectors) if (dp != null) dp.enabled = true;
                hiddenEnvironmentProjectors.Clear();
                foreach (var (dp, dist, fade) in restoredDecals) if (dp != null) { dp.drawDistance = dist; dp.fadeScale = fade; }
                restoredDecals.Clear();
                projectors.Clear();
                foreach (var t in hiddenGround) if (t != null) t.enabled = true;
                foreach (var (obj, layer) in movedLayers.Values) if (obj != null) obj.layer = layer;
                movedLayers.Clear();
                if (!fogWas) MeshTools.Fog(off: false);
                hidden.Clear();
                hiddenGround.Clear();
            }
            catch (Exception ex) { Plugin.ModLog.LogError($"TOOL_DRAWING couldn't tidy up: {ex}"); }
        }

        static void Fail(string what, string whatCn, Exception ex)
        {
            Plugin.ModLog.LogError($"TOOL_DRAWING {what}: {ex}");
            DesignEditor.Instance?.Say($"资料卡失败：{whatCn}：{ex.Message}", 8);
        }
    }
}
