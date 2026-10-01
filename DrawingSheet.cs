using BepInEx.Unity.IL2CPP.Utils.Collections;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Sprocket.VehicleDesigner;
using Sprocket.Vehicles.Colliders;
using Sprocket.Vehicles.PlateStructures;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering.HighDefinition;
using N = System.Numerics;

namespace SprocketTools;

/// F9 in the vehicle editor: a drawing sheet of the vehicle, laid out as a maker's drawing: from above and from the
/// front on top, from the side and from the back below, all at one scale, with a 1 m ruler. Saved three times to
/// Documents\My Games\Sprocket\Photos: in lines only (black on white), in colour (the paint, lit evenly from every
/// side, with its decals) with the same lines over it, and see-through (half the colour sheet, half the vehicle with its armour
/// off). Colour and see-through have
/// independently chosen lines: colour defaults to clean part contours, see-through to the silhouette only. Drawing: no
/// wireframe omits both overlays. The lines come from the vehicle's own shapes (where faces meet at an angle, open
/// edges, the outline of curved parts), with what's behind other parts left out; each view's outline comes from its
/// picture, so parts whose shapes can't be read are still outlined.
internal static class DrawingSheet
{
    const int SheetWidth = 3600;   // pixels across the views
    const int Margin = 60;         // round the sheet
    const int Gap = 100;           // between the views
    const int DimLeft = 240;       // left of the views: the height and width dimensions and their measures
    const int DimBelow = 130;      // under a view: its length or width dimension and measure
    const int LabelHigh = 50;      // over a view: its name
    const int RulerHigh = 100;     // at the bottom: the 1 m ruler
    const int Pad = 6;             // round each view's picture
    const int Letter = 4;          // pixels per font pixel
    const int BlockGap = 60;       // between the ruler and the title block (the name, guns and description) under it
    const int BlockLine = 16;      // between the title block's parts
    const int FirstSettle = 40, Settle = 12; // frames each view is left to draw fully (exposure, then anti-aliasing)
    static readonly Color Behind = new(1, 0, 1); // drawn behind the vehicle to tell it apart: no paint is this magenta

    // Each view: the way the camera looks and the sheet's up. The vehicle's front is +z, its right +x.
    static readonly (string Name, Vector3 Look, Vector3 Up)[] Views =
    {
        ("顶视", Vector3.down, Vector3.right),   // from above, front to the left
        ("前视", Vector3.back, Vector3.up),
        ("侧视", Vector3.right, Vector3.up),    // its left side, front to the left
        ("后视", Vector3.forward, Vector3.up),
    };

    static bool busy;

    internal static void Update()
    {
        if (busy || PhotoShot.Capturing || Keyboard.current is not { } keys || !keys.f9Key.wasPressedThisFrame || MeshTools.Typing()) return;
        if (DesignEditor.Instance is not { } editor) return;
        busy = true;
        editor.StartCoroutine(Run().WrapToIl2Cpp());
    }

    static System.Collections.IEnumerator Run()
    {
        Sheet? sheet = null;
        try
        {
            // Nothing under the mouse while the views are taken; a few frames for the editor to let go of what was.
            NoHover.On = true;
            for (int f = 0; f < 3; f++) yield return null;
            sheet = Sheet.Begin();
            if (sheet == null) yield break;
            // The views, then again with the armour off for the see-through sheet.
            foreach (bool inside in new[] { false, true })
            {
                if (inside) sheet.TakeArmourOff();
                for (int i = 0; i < Views.Length; i++)
                {
                    if (!sheet.Aim(i)) yield break;
                    for (int f = 0; f < (i == 0 ? FirstSettle : Settle); f++) { sheet.Hold(); yield return null; }
                    if (!sheet.Grab(i, inside)) yield break;
                }
            }
            sheet.PutArmourBack(); // its shapes make the lines
            DesignEditor.Instance?.Say("工程图纸：正在绘制线条……", 10);
            yield return null; // the message shows before the work (a second or two)
            sheet.Finish();
        }
        finally
        {
            sheet?.End();
            NoHover.On = false;
            busy = false;
        }
    }

    /// The editor's hover highlight moves the part under the mouse onto a layer of its own (so it went missing from the
    /// pictures) and can turn the hull see-through; while the views are taken, the mouse is over nothing.
    [HarmonyPatch(typeof(VehicleComponentHoverer), nameof(VehicleComponentHoverer.SetHoverCandidate))]
    internal static class NoHover
    {
        internal static bool On;
        static void Prefix(ref IVehicleCollider item) { if (On) item = null!; }
    }

    /// The design's own name, cleaned for the filesystem: what F9 names its sheets after, and what F10 reuses so a
    /// vehicle's pictures sort together. "" when the design has no name worth printing.
    internal static string DesignName()
    {
        var raw = Sheet.ResolveVehicleName(DesignEditor.Instance?.AllComponents(), null);
        if (Sheet.Placeholder(raw)) return "";
        return string.Join("_", raw.Split(Path.GetInvalidFileNameChars())).Trim();
    }

    /// One sheet in the making: the views' sizes on it, the camera that takes them, what it changed to take them.
    sealed class Sheet
    {
        readonly bool noWireframe = Plugin.DrawingNoWireframe?.Value ?? false;
        readonly bool seeThroughOutline = Plugin.DrawingSeeThroughOutline?.Value ?? true;
        readonly bool colourOutline = Plugin.DrawingColourOutline?.Value ?? true;
        readonly float intensity = DrawingOptions.Strength(Plugin.DrawingIntensity?.Value ?? 100);
        readonly bool elevation = Plugin.DrawingElevation?.Value ?? false, traverse = Plugin.DrawingTraverse?.Value ?? false;
        readonly bool blue = Plugin.DrawingBlue?.Value ?? false;
        readonly bool grid = Plugin.DrawingGrid?.Value ?? false;
        readonly float gridStrength = DrawingOptions.Strength(Plugin.DrawingGridIntensity?.Value ?? 20);
        readonly bool turretTraverse = Plugin.DrawingTurretTraverse?.Value ?? false;
        readonly GunAnnotationPreferences gunLimits = new(Plugin.DrawingHiddenGunLimits?.Value);
        readonly List<DrawingOptions.Ghost> motion = new();
        Bounds box;
        Bounds frame;
        float scale;                                   // pixels per metre, the same in every view
        readonly Drawing.View[] views = new Drawing.View[Views.Length];
        readonly byte[][] colour = new byte[Views.Length][]; // RGB, rows from the bottom
        readonly bool[][] solid = new bool[Views.Length][];  // the vehicle, not the backdrop
        readonly byte[][] insideColour = new byte[Views.Length][]; // the same with the armour off
        readonly bool[][] insideSolid = new bool[Views.Length][];
        readonly List<Renderer> armour = new();              // switched off for the see-through views
        Camera? cam;
        RenderTexture? target;
        int layers;
        int bottom = Margin;                          // where the drawing starts, over the title block
        bool fullbrightWas, fogWas, lightingCaptured;
        HashSet<IntPtr> aerials = new(); // the antennas' renderers
        // Everything else in the scene, out of sight while the views are taken: the editor's studio floor and wall
        // share the vehicle's layer, and would stand behind it.
        readonly List<Renderer> hidden = new();
        readonly List<Terrain> hiddenGround = new();
        // The vehicle's decals: on the pictures with it, off with the armour for the see-through ones.
        readonly List<DecalProjector> projectors = new();
        readonly List<DecalProjector> armourProjectors = new();
        readonly List<DecalProjector> hiddenEnvironmentProjectors = new();
        readonly List<(DecalProjector Projector, float Distance, float FadeScale)> restoredDecals = new();
        int ownLayer = -1; // the layer the vehicle is moved onto for the pictures (-1: none free, its own layers used)
        readonly Dictionary<IntPtr, (GameObject Object, int Layer)> movedLayers = new();

        static N.Vector3 V(Vector3 v) => new(v.x, v.y, v.z);

        internal static Sheet? Begin()
        {
            var sheet = new Sheet();
            try
            {
                if (sheet.Setup()) return sheet;
                sheet.End();
                return null;
            }
            catch (Exception ex) { Fail("无法开始", ex); sheet.End(); return null; }
        }

        bool Setup()
        {
            var main = Camera.main;
            // Only the vehicle, lit evenly: its own layers (no sky or the editor's handles), everything else hidden.
            var vehicle = new HashSet<IntPtr>();
            Bounds? around = null;
            // Antennas left off: a whip metres tall would set the vehicle's height and leave the views empty.
            aerials = MeshTools.AntennaRenderers();
            var vehicleParts = (DesignEditor.Instance?.AllParts() ?? Enumerable.Empty<Sprocket.Vehicles.VehicleObject>()).ToList();
            foreach (var part in vehicleParts)
                foreach (var r in part.GetComponentsInChildren<Renderer>())
                {
                    if (aerials.Contains(r.Pointer) || !MeshTools.Drawn(r)) continue;
                    layers |= 1 << r.gameObject.layer;
                    vehicle.Add(r.Pointer);
                    if (around is { } a) { a.Encapsulate(r.bounds); around = a; } else around = r.bounds;
                }
            if (main == null || around is not { } b) { DesignEditor.Instance?.Say("工程图纸：没有可绘制的载具", 4); return false; }
            box = b;
            frame = box;
            MeshTools.LogMaterials("TOOL_DRAWING"); // before anything is switched off: what the vehicle is made of as the game has it
            if (elevation || traverse) ReadGunMotion();
            if (turretTraverse) ReadTurretMotion();
            foreach (var ghost in motion)
                foreach (var p in ghost.Shapes.SelectMany(s => s.P).Concat(ghost.Arc).Append(ghost.Pivot).Append(ghost.Tip))
                    frame.Encapsulate(new Vector3(p.X, p.Y, p.Z));
            var size = frame.size;

            // The vehicle's decals: under its parts, or held by its decal components.
            var decalSeen = new HashSet<IntPtr>();
            void AddDecal(DecalProjector? dp)
            {
                if (dp != null && dp.enabled && dp.gameObject.activeInHierarchy && decalSeen.Add(dp.Pointer)) projectors.Add(dp);
            }
            foreach (var part in vehicleParts)
                foreach (var dp in part.GetComponentsInChildren<DecalProjector>(true)) AddDecal(dp);
            foreach (var c in DesignEditor.Instance?.AllComponents() ?? Enumerable.Empty<Sprocket.Vehicles.VehicleComponent>())
                if (c?.TryCast<Sprocket.Vehicles.AttachedBehaviours.ProjectedDecal>() is { } pd) AddDecal(pd.projector);
            foreach (var dp in projectors) layers |= 1 << dp.gameObject.layer;

            // Left column: the top and side views, as long as the vehicle. Right: the front and back, as wide.
            scale = (SheetWidth - Gap - 4 * Pad) / Math.Max(0.1f, size.z + size.x);
            for (int i = 0; i < Views.Length; i++)
            {
                var turn = Quaternion.LookRotation(Views[i].Look, Views[i].Up);
                var right = turn * Vector3.right;
                var up = turn * Vector3.up;
                float across = Math.Abs(Vector3.Dot(size, right)), high = Math.Abs(Vector3.Dot(size, up));
                // Room for angle labels outside the barrel limits; the physical dimensions still use the original box.
                int pad = motion.Count > 0 ? 100 : Pad;
                int w = (int)MathF.Ceiling(across * scale) + 2 * pad, h = (int)MathF.Ceiling(high * scale) + 2 * pad;
                views[i] = new Drawing.View(V(frame.center), V(right), V(up), V(Views[i].Look), scale, w, h);
            }
            var used = new HashSet<int>();
            foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Renderer>()))
                if (o.TryCast<Renderer>() is { } r)
                {
                    used.Add(r.gameObject.layer);
                    if (r.enabled && !vehicle.Contains(r.Pointer)) { r.enabled = false; hidden.Add(r); }
                }
            // Every decal's layer too (the map's included): a decal shows only to a camera that sees its layer, so the
            // pictures' layer must be one no decal is on, and only the vehicle's are moved onto it.
            foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<DecalProjector>()))
                if (o.TryCast<DecalProjector>() is { } dp)
                {
                    used.Add(dp.gameObject.layer);
                    // Hide non-vehicle projectors so they don't bleed into the drawing.
                    if (dp.enabled && !projectors.Any(p => p.Pointer == dp.Pointer))
                    {
                        dp.enabled = false;
                        hiddenEnvironmentProjectors.Add(dp);
                    }
                }
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
                // The decals with it: the projector's own object is enough (what's under it is the vehicle's, or hidden).
                foreach (var dp in projectors)
                    if (!movedLayers.ContainsKey(dp.gameObject.Pointer))
                    {
                        movedLayers[dp.gameObject.Pointer] = (dp.gameObject, dp.gameObject.layer);
                        dp.gameObject.layer = ownLayer;
                    }
            }
            // The camera stands further back than the editor's: decals that fade with distance are drawn fully. Back in End.
            foreach (var dp in projectors)
            {
                if (dp != null)
                {
                    float origDist = dp.drawDistance;
                    float origFade = dp.fadeScale;
                    if (origDist < 200f || origFade < 1f)
                    {
                        restoredDecals.Add((dp, origDist, origFade));
                        if (origDist < 200f) dp.drawDistance = 200f;
                        if (origFade < 1f) dp.fadeScale = 1f;
                    }
                    // HDRP caches gameObject.layer; changing the layer alone does not update its decal data.
                    // Refresh even if this projector already had the required distance/fade settings.
                    dp.OnValidate();
                }
            }
            // The game's fog, off too: it stood in front of the backdrop (below hull height, and all of the view from
            // above) and hazed the paint.
            fogWas = MeshTools.FogOff;
            fullbrightWas = MeshTools.FullbrightOn;
            lightingCaptured = true;
            MeshTools.Fog(off: true);
            if (!fullbrightWas) MeshTools.ToggleFullbright();
            var go = new GameObject("SprocketTools drawing camera");
            cam = go.AddComponent<Camera>();
            cam.CopyFrom(main);
            if (main.GetComponent<HDAdditionalCameraData>() is { } hd)
            {
                var own = go.AddComponent<HDAdditionalCameraData>();
                hd.CopyTo(own);
                own.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color;
                own.backgroundColorHDR = Behind;
                // Not temporal: each view is still, but the camera jumps between them.
                own.antialiasing = HDAdditionalCameraData.AntialiasingMode.SubpixelMorphologicalAntiAliasing;
                // No reflections: seen from above, the paint mirrored the sky and the map's grass round the editor.
                own.customRenderingSettings = true;
                var overrides = own.renderingPathCustomFrameSettingsOverrideMask;
                foreach (var field in new[] { FrameSettingsField.SSR, FrameSettingsField.SSGI, FrameSettingsField.ReflectionProbe, FrameSettingsField.PlanarProbe, FrameSettingsField.SkyReflection })
                {
                    overrides.mask[(uint)field] = true;
                    own.renderingPathCustomFrameSettings.SetEnabled(field, false);
                }
                // Decals on (the vehicle's are on the pictures); their layers as the game has them, so each lands only
                // where it does in the editor.
                overrides.mask[(uint)FrameSettingsField.Decals] = true;
                own.renderingPathCustomFrameSettings.SetEnabled(FrameSettingsField.Decals, true);
                overrides.mask[(uint)FrameSettingsField.DecalLayers] = true;
                own.renderingPathCustomFrameSettings.SetEnabled(FrameSettingsField.DecalLayers, false);
                own.renderingPathCustomFrameSettingsOverrideMask = overrides;
            }
            cam.orthographic = true;
            cam.cullingMask = ownLayer >= 0 ? 1 << ownLayer : layers;
            DesignEditor.Instance?.Say("工程图纸：正在拍摄各视图……", 10);
            Plugin.ModLog.LogInfo($"TOOL_DRAWING vehicle {size.x:0.00} x {size.y:0.00} x {size.z:0.00} m, {scale:0} px a metre, views " +
                                  string.Join(", ", views.Select((v, i) => $"{Views[i].Name} {v.Width}x{v.Height}")) + $", {aerials.Count} antenna pieces left out, {projectors.Count} decals, " +
                                  (ownLayer >= 0 ? $"{movedLayers.Count} objects on layer {ownLayer} for the pictures" : "no free layer: the vehicle's own layers"));
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
            foreach (var dp in projectors) if (dp != null && dp.enabled) dp.OnValidate();
        }

        /// The camera straight at view `i`, just far enough back, drawing only the depth the vehicle fills.
        internal bool Aim(int i)
        {
            try
            {
                var v = views[i];
                var look = Views[i].Look;
                float radius = frame.extents.magnitude;
                var at = frame.center - look * (radius + 10);
                cam!.transform.SetPositionAndRotation(at, Quaternion.LookRotation(look, Views[i].Up));
                cam.orthographicSize = v.Height / 2f / scale;
                cam.aspect = v.Width / (float)v.Height;
                cam.nearClipPlane = 0.1f;
                cam.farClipPlane = 10 + 2 * radius + 10f;
                if (target != null) { cam.targetTexture = null; target.Release(); UnityEngine.Object.Destroy(target); }
                target = new RenderTexture(v.Width, v.Height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                cam.targetTexture = target;
                return true;
            }
            catch (Exception ex) { Fail($"无法对准{Views[i].Name}", ex); return false; }
        }

        /// View `i` as the camera drew it: its colours, and which pixels are the vehicle (not the magenta behind it).
        /// The armour out of the pictures: every plate structure (hull, turret, add-on) and nothing hung on it.
        internal void TakeArmourOff()
        {
            foreach (var part in DesignEditor.Instance?.AllParts() ?? Enumerable.Empty<Sprocket.Vehicles.VehicleObject>())
                foreach (var r in part.GetComponentsInChildren<Renderer>())
                    if (r.enabled && r.GetComponentInParent<Sprocket.Vehicles.VehicleObject>()?.GetComponent<PlateStructure>() != null) { r.enabled = false; armour.Add(r); }
            // The decals go with it: left on, they'd paint whatever is behind the armour.
            foreach (var dp in projectors)
                if (dp != null && dp.enabled) { dp.enabled = false; armourProjectors.Add(dp); }
        }

        internal void PutArmourBack()
        {
            foreach (var r in armour) Restore("armour renderer", () => { if (r != null) r.enabled = true; });
            armour.Clear();
            foreach (var dp in armourProjectors) Restore("armour decal", () => { if (dp != null) dp.enabled = true; });
            armourProjectors.Clear();
        }

        internal bool Grab(int i, bool inside)
        {
            try
            {
                var v = views[i];
                var was = RenderTexture.active;
                RenderTexture.active = target;
                var picture = new Texture2D(v.Width, v.Height, TextureFormat.RGBA32, 1, false);
                picture.ReadPixelsImpl(new Rect(0, 0, v.Width, v.Height), 0, 0, false);
                RenderTexture.active = was;
                var pixels = picture.GetPixels32();
                UnityEngine.Object.Destroy(picture);
                var rgb = new byte[v.Width * v.Height * 3];
                var mask = new bool[v.Width * v.Height];
                for (int p = 0; p < mask.Length; p++)
                {
                    var c = pixels[p];
                    rgb[p * 3] = c.r; rgb[p * 3 + 1] = c.g; rgb[p * 3 + 2] = c.b;
                    // The magenta behind (darker at the corners, with the game's vignette): red and blue alike, green far below.
                    int lo = Math.Min(c.r, c.b), hi = Math.Max(c.r, c.b);
                    mask[p] = !(lo > 30 && c.g < lo / 2 && hi - lo < hi * 0.35f);
                }
                (inside ? insideColour : colour)[i] = rgb;
                (inside ? insideSolid : solid)[i] = mask;
                var corner = pixels[0];
                Plugin.ModLog.LogInfo($"TOOL_DRAWING {Views[i].Name}{(inside ? $" (armour off, {armour.Count} pieces)" : "")}: backdrop drawn as ({corner.r}, {corner.g}, {corner.b}), {mask.Count(m => m) * 100 / mask.Length}% of the picture is the vehicle");
                return true;
            }
            catch (Exception ex) { Fail($"无法拍摄{Views[i].Name}", ex); return false; }
        }

        /// The lines of every view, all three sheets put together and saved.
        internal void Finish()
        {
            try
            {
                var shapes = Shapes(out int unreadable);
                // Both counts every run, so one sheet tells how much the shading normals would change.
                Plugin.ModLog.LogInfo($"TOOL_DRAW_CREASE 折棱：几何 {shapes.Sum(s => s.CreasesGeometric)} → 着色 {shapes.Sum(s => s.CreasesShaded)}" +
                    $"（本次按{(Plugin.DrawingCreaseNormals?.Value ?? false ? "着色" : "几何")}，网格 {shapes.Count}，读不到 {unreadable}）");
                var ink = new bool[Views.Length][];
                var outlines = new bool[Views.Length][];
                var cleanOutlines = new bool[Views.Length][];
                bool seams = Plugin.DrawingSeamFilter?.Value ?? false;
                int seamsDropped = 0;
                for (int i = 0; i < Views.Length; i++)
                {
                    var v = views[i];
                    ink[i] = new bool[v.Width * v.Height];
                    var depth = Drawing.Depths(shapes, v);
                    // Every shape read: the vehicle is only where they are (unread ones are only in the picture).
                    if (unreadable == 0) { Drawing.Clip(solid[i], depth.Z, v.Width, v.Height, 3); Drawing.Clip(insideSolid[i], depth.Z, v.Width, v.Height, 3); }
                    seamsDropped += Drawing.Lines(shapes, v, depth, ink[i], seamFilter: seams);
                    // Keep the silhouette separate: mesh creases must not bleed into the see-through overlay.
                    outlines[i] = new bool[v.Width * v.Height];
                    Drawing.Outline(solid[i], v.Width, v.Height, outlines[i]);
                    cleanOutlines[i] = (bool[])outlines[i].Clone();
                    if (colourOutline && !noWireframe) Drawing.CleanContours(shapes, v, depth, cleanOutlines[i]);
                    for (int p = 0; p < ink[i].Length; p++) ink[i][p] |= outlines[i][p];
                }
                Plugin.ModLog.LogInfo($"TOOL_DRAW_SEAM 拼接缝：{(seams ? "已开" : "未开")}，四个视图共隐去开边 {seamsDropped}" +
                    $"（单视图开边 {shapes.Sum(s => s.E.Count(e => e.F2 < 0))}，阈值 {Drawing.SeamDepth * 1000:0} 毫米 / {Drawing.SeamDegrees:0} 度，探边 {Drawing.SeamProbe} 像素）");
                // Top row: from above, then the front; below: the side, then the back (their bottoms level: the ground).
                // Left of the views and under them, room for the dimensions; over each, its name; under them the ruler,
                // and at the bottom the vehicle's name, guns and description.
                int left = Math.Max(views[0].Width, views[2].Width), right = Math.Max(views[1].Width, views[3].Width);
                int low = Math.Max(views[2].Height, views[3].Height), high = Math.Max(views[0].Height, views[1].Height);
                int xLeft = Margin + DimLeft, xRight = xLeft + left + Gap;
                var block = TitleBlock(xLeft, xRight + right - xLeft);
                int blockHigh = block.Height;
                bottom = Margin + (blockHigh > 0 ? blockHigh + BlockGap : 0);
                int yLow = bottom + RulerHigh + DimBelow, yHigh = yLow + low + LabelHigh + Gap + DimBelow;
                int w = xRight + right + Margin, h = yHigh + high + LabelHigh + Margin;
                var at = new (int X, int Y)[]
                {
                    (xLeft + (left - views[0].Width) / 2, yHigh + (high - views[0].Height) / 2),
                    (xRight + (right - views[1].Width) / 2, yHigh + (high - views[1].Height) / 2),
                    (xLeft + (left - views[2].Width) / 2, yLow),
                    (xRight + (right - views[3].Width) / 2, yLow),
                };
                var lines = new byte[w * h * 3];
                var painted = new byte[w * h * 3];
                var seeThrough = new byte[w * h * 3];
                Array.Fill(lines, (byte)255);
                Array.Fill(painted, (byte)255);
                Array.Fill(seeThrough, (byte)255);
                for (int i = 0; i < Views.Length; i++)
                {
                    var v = views[i];
                    for (int y = 0; y < v.Height; y++)
                        for (int x = 0; x < v.Width; x++)
                        {
                            int p = y * v.Width + x, q = ((at[i].Y + y) * w + at[i].X + x) * 3;
                            if (ink[i][p]) DrawingOptions.Ink(lines, q, 0, intensity);
                            bool shell = solid[i][p], core = insideSolid[i][p];
                            for (int c = 0; c < 3; c++)
                            {
                                int paint = shell ? colour[i][p * 3 + c] : 255, under = core ? insideColour[i][p * 3 + c] : 255;
                                painted[q + c] = (byte)paint;
                                // Half the paint, half what's inside (white where there's nothing): the armour as glass.
                                seeThrough[q + c] = (byte)((paint + under) / 2);
                            }
                            if (!noWireframe)
                            {
                                if (colourOutline ? cleanOutlines[i][p] : ink[i][p]) DrawingOptions.Ink(painted, q, colourOutline ? (byte)65 : (byte)30, intensity);
                                if (seeThroughOutline ? outlines[i][p] : ink[i][p]) DrawingOptions.Ink(seeThrough, q, 30, intensity);
                            }
                        }
                }
                DrawingOptions.DrawMotion(motion, new[] { lines, painted, seeThrough }, views, w, h, at);
                foreach (var sheet in new[] { lines, painted, seeThrough }) Annotate(sheet, w, h, at, xLeft);
                // The title block, from its top down, over a rule the width of the views.
                if (blockHigh > 0)
                    foreach (var sheet in new[] { lines, painted, seeThrough })
                    {
                        Drawing.Box(sheet, w, h, xLeft, Margin + blockHigh + BlockGap / 2, xRight + right, Margin + blockHigh + BlockGap / 2 + 1, 0);
                        int top = Margin + blockHigh - 1;
                        foreach (var (x, yOffset, words) in block.Items)
                            Drawing.Stamp(sheet, w, h, x, top - yOffset, words, 0);
                    }
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Sprocket", "Photos");
                Directory.CreateDirectory(dir);
                string vName = !string.IsNullOrWhiteSpace(block.VehicleName)
                    ? block.VehicleName
                    : ResolveVehicleName(DesignEditor.Instance?.AllComponents(), null);
                string safeName = string.Join("_", (vName ?? "").Split(Path.GetInvalidFileNameChars())).Trim();
                if (string.IsNullOrWhiteSpace(safeName)) safeName = "工程图纸";
                var name = Path.Combine(dir, safeName);
                Drawing.SavePng(name + ".png", w, h, lines);
                Drawing.SavePng(name + "（彩色图）.png", w, h, painted);
                Drawing.SavePng(name + "（透视图）.png", w, h, seeThrough);
                if (blue) Drawing.SavePng(name + "（蓝图）.png", w, h,
                    DrawingOptions.Blueprint(lines, w, h, grid ? Math.Max(1, (int)MathF.Round(scale / 4)) : 0, gridStrength));
                Plugin.ModLog.LogInfo($"TOOL_DRAWING saved {name}.png, (colour) and (see-through), {w}x{h}: {shapes.Count} shapes drawn, {unreadable} meshes the game keeps unreadable (outlined only); overlays: colour={(noWireframe ? "none" : colourOutline ? "outline" : "wireframe")}, see-through={(noWireframe ? "none" : seeThroughOutline ? "outline" : "wireframe")}; intensity={intensity:P0}, gun limits={motion.Count}, blue blueprint={blue}");
                DesignEditor.Instance?.Say($"工程图纸已保存（线条图、彩色图、透视图{(blue ? "、蓝图" : "")}）：{name}.png", 8);
            }
            catch (Exception ex) { Fail("无法绘制图纸", ex); }
        }

        static void Set(byte[] rgb, int q, byte grey) { rgb[q] = grey; rgb[q + 1] = grey; rgb[q + 2] = grey; }

        sealed class TitleBlockLayout
        {
            public int Height;
            public string VehicleName = "";
            public readonly List<(int X, int YOffset, (int W, int H, byte[] Ink) Words)> Items = new();
        }

        /// The title block in three columns across `totalWidth` pixels:
        /// The game's own placeholder names, in either language — a drawing shouldn't print them as if the player chose them.
        internal static bool Placeholder(string? name) => string.IsNullOrWhiteSpace(name) ||
            name.StartsWith("Unnamed") || name.StartsWith("Untitled") || name.StartsWith("未命名");

        static string CleanName(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            raw = raw.Trim();
            if (raw.EndsWith("(Clone)", StringComparison.OrdinalIgnoreCase))
                raw = raw[..^7].Trim();
            if (raw.Equals("Vehicle", StringComparison.OrdinalIgnoreCase) || raw.Equals("Root", StringComparison.OrdinalIgnoreCase))
                return "";
            return raw;
        }

        /// The design's own name, from whichever of the game's routes to it still reads: the components' design info,
        /// the powertrain target, the parts' hierarchy, and last the snapshot header. "" when none of them say.
        internal static string ResolveVehicleName(IEnumerable<Sprocket.Vehicles.VehicleComponent>? components, System.Text.Json.Nodes.JsonNode? header)
        {
            try
            {
                if (components != null)
                {
                    foreach (var c in components)
                    {
                        if (c?.Vehicle?.DesignInfo is { } info && !string.IsNullOrWhiteSpace(info.Name))
                            return info.Name.Trim();
                    }
                }
            }
            catch { }

            try
            {
                if (DesignEditor.Instance?.Core?.Target?.TryCast<Sprocket.Vehicles.Vehicle>() is { } v && v.DesignInfo is { } info && !string.IsNullOrWhiteSpace(info.Name))
                    return info.Name.Trim();
            }
            catch { }

            try
            {
                if (DesignEditor.Instance?.AllParts() is { } parts)
                {
                    foreach (var p in parts)
                    {
                        var v = p.GetComponentInParent<Sprocket.Vehicles.Vehicle>();
                        if (v?.DesignInfo is { } info && !string.IsNullOrWhiteSpace(info.Name))
                            return info.Name.Trim();
                    }
                }
            }
            catch { }

            try
            {
                if (DesignEditor.Instance?.Core?.Target?.TryCast<Component>() is { } targetComp && !string.IsNullOrWhiteSpace(targetComp.gameObject.name))
                {
                    string cleaned = CleanName(targetComp.gameObject.name);
                    if (!string.IsNullOrWhiteSpace(cleaned)) return cleaned;
                }
            }
            catch { }

            try
            {
                if (DesignEditor.Instance?.AllParts() is { } parts)
                {
                    foreach (var p in parts)
                    {
                        string rootName = CleanName(p.transform.root.name);
                        if (!string.IsNullOrWhiteSpace(rootName)) return rootName;
                    }
                }
            }
            catch { }

            try
            {
                if (components != null)
                {
                    foreach (var c in components)
                    {
                        if (c?.Vehicle?.TryCast<Component>() is { } vComp && !string.IsNullOrWhiteSpace(vComp.gameObject.name))
                        {
                            string cleaned = CleanName(vComp.gameObject.name);
                            if (!string.IsNullOrWhiteSpace(cleaned)) return cleaned;
                        }
                    }
                }
            }
            catch { }

            try
            {
                string? hName = header?["name"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(hName)) return hName.Trim();
            }
            catch { }

            return "";
        }

        /// Full-width header: vehicle name and weight (56pt bold). Column 1: horsepower and top speed (36pt).
        /// Column 2 (middle): weapons/armament (each kind of gun with caliber, name, L/length, count).
        /// Column 3 (right): vehicle description.
        /// Whatever can't be read is left out.
        static TitleBlockLayout TitleBlock(int xLeft, int totalWidth)
        {
            var layout = new TitleBlockLayout();
            try
            {
                string name = "", description = "";
                System.Text.Json.Nodes.JsonNode? header = null;
                try
                {
                    if (DesignEditor.Instance is { } editor) header = System.Text.Json.Nodes.JsonNode.Parse(editor.Snapshot())?["header"];
                }
                catch { }
                name = header?["name"]?.GetValue<string>() ?? "";
                description = header?["desc"]?.GetValue<string>() ?? "";
                var guns = new List<string>();
                var seen = new HashSet<IntPtr>();
                foreach (var part in DesignEditor.Instance?.AllParts() ?? Enumerable.Empty<Sprocket.Vehicles.VehicleObject>())
                    foreach (var cannon in part.GetComponentsInChildren<Sprocket.Vehicles.Cannons.Cannon>())
                        if (seen.Add(cannon.Pointer) && cannon.Blueprint is { Caliber: > 0 } gun)
                        {
                            string called = Placeholder(gun.Name) ? "" : "  " + gun.Name.Trim();
                            guns.Add($"{gun.Caliber} 毫米{called}   L/{gun.BarrelLength / (float)gun.Caliber:0.#}");
                        }
                var armament = string.Join("\n", guns.GroupBy(g => g).Select(g => (g.Count() > 1 ? $"{g.Count()} × " : "") + g.Key));

                var allComponents = DesignEditor.Instance?.AllComponents().ToList() ?? new();
                if (Placeholder(name)) name = ResolveVehicleName(allComponents, header);
                layout.VehicleName = Placeholder(name) ? "" : name.Trim();
                var engines = allComponents.Select(c => c.TryCast<Sprocket.Vehicles.Engines.CombustionEngine>()).Where(e => e?.Blueprint != null).OfType<Sprocket.Vehicles.Engines.CombustionEngine>().ToList();
                if (engines.Count == 0 && DesignEditor.Instance != null)
                    engines = DesignEditor.Instance.AllParts().SelectMany(p => p.GetComponentsInChildren<Sprocket.Vehicles.Engines.CombustionEngine>()).Where(e => e?.Blueprint != null).ToList();
                var engine = (engines.FirstOrDefault(e => e.SelectedInPowertrain) ?? engines.FirstOrDefault())?.Blueprint;

                var gearboxes = allComponents.Select(c => c.TryCast<Sprocket.Vehicles.Transmissions.TransmissionBlock>()).OfType<Sprocket.Vehicles.Transmissions.TransmissionBlock>().ToList();
                if (gearboxes.Count == 0 && DesignEditor.Instance != null)
                    gearboxes = DesignEditor.Instance.AllParts().SelectMany(p => p.GetComponentsInChildren<Sprocket.Vehicles.Transmissions.TransmissionBlock>()).ToList();
                var gearbox = gearboxes.FirstOrDefault(t => t.SelectedInPowertrain) ?? gearboxes.FirstOrDefault();

                var tracks = allComponents.Select(c => c.TryCast<Sprocket.Vehicles.Tracks.TrackAssembly>()).Where(t => t?.BlueprintSlot?.HasBlueprint == true).OfType<Sprocket.Vehicles.Tracks.TrackAssembly>().ToList();
                if (tracks.Count == 0 && DesignEditor.Instance != null)
                    tracks = DesignEditor.Instance.AllParts().SelectMany(p => p.GetComponentsInChildren<Sprocket.Vehicles.Tracks.TrackAssembly>()).Where(t => t?.BlueprintSlot?.HasBlueprint == true).ToList();
                var track = tracks.FirstOrDefault();

                string powerText = "";
                if (engine is { MaxRPM: > 0, MaxTorque: > 0 })
                {
                    float hp = 0;
                    try
                    {
                        float idle = Math.Clamp(engine.IdleRPM, 1, Math.Max(1, engine.MaxRPM - 1)), max = Math.Max(1, engine.MaxRPM);
                        float peakKw = Enumerable.Range(0, 101)
                            .Select(k => idle + (max - idle) * k / 100f)
                            .Select(rpm => Sprocket.Engines.EngineRules.CalculatePowerAtRPM(engine.MaxTorque, rpm, max))
                            .DefaultIfEmpty(0)
                            .Max();
                        if (peakKw > 0) hp = peakKw * 1.341022f;
                    }
                    catch { }
                    if (hp <= 0) hp = engine.MaxTorque * engine.MaxRPM / 7120.54f;
                    string called = Placeholder(engine.Name) ? "" : "  " + engine.Name.Trim();
                    powerText = $"{MathF.Round(hp).ToString("0", System.Globalization.CultureInfo.InvariantCulture)} 马力{called}";
                }

                string speedText = "";
                if (engine is { MaxRPM: > 0 } && gearbox != null && track?.BlueprintSlot?.HasBlueprint == true)
                {
                    var ratios = (gearbox.resultingDriveGearRatios?.ToArray() ?? Array.Empty<float>()).Select(Math.Abs).Where(r => r > 0).ToArray();
                    var reverse = (gearbox.resultingReverseGearRatios?.ToArray() ?? Array.Empty<float>()).Select(Math.Abs).Where(r => r > 0).ToArray();
                    float finalDrive = track.BlueprintSlot.Blueprint.FinalDriveRatio;
                    var sprocket = track.SprocketAssembly?.WheelBlueprint;
                    float radius = sprocket?.HasBlueprint == true ? sprocket.Blueprint.Radius : 0;
                    if (ratios.Length > 0 && radius > 0 && finalDrive > 0)
                    {
                        float limit = float.MaxValue;
                        try { if (track.TopSpeed > 0) limit = track.TopSpeed * 3.6f; } catch { }
                        float Speed(float ratio) => Sprocket.VehicleDesigner.Powertrains.PowertrainInfo.CalculateSpeed(engine.MaxRPM, ratio * finalDrive, radius) * 3.6f;
                        float fwdSpeed = Math.Min(Speed(ratios.Min()), limit);
                        string fwdStr = MathF.Round(fwdSpeed).ToString("0", System.Globalization.CultureInfo.InvariantCulture);
                        if (reverse.Length > 0)
                        {
                            float revSpeed = Math.Min(Speed(reverse.Min()), limit);
                            string revStr = MathF.Round(revSpeed).ToString("0", System.Globalization.CultureInfo.InvariantCulture);
                            speedText = $"极速：  {fwdStr} km/h（前进） / {revStr} km/h（倒车）";
                        }
                        else
                        {
                            speedText = $"极速：  {fwdStr} km/h";
                        }
                    }
                }

                var mobility = string.Join("\n", new[] { powerText, speedText }.Where(s => !string.IsNullOrEmpty(s)));

                const int colGap = 80;
                const int headerGap = 10;
                const int sectionGap = 20;
                int colWidth = Math.Max(100, (totalWidth - 2 * colGap) / 3);
                int col1X = xLeft;
                int col2X = xLeft + colWidth + colGap;
                int col3X = xLeft + 2 * (colWidth + colGap);
                int col3Width = Math.Max(100, (xLeft + totalWidth) - col3X);

                var col1Items = new List<(int YOffset, (int W, int H, byte[] Ink) Words)>();
                int col1High = 0;
                string weight = DrawingOptions.Weight(allComponents.FirstOrDefault(c => c.Vehicle != null)?.Vehicle?.Mass ?? 0);
                var weightWords = Drawing.Words(weight, 56, true, Math.Max(100, totalWidth / 4));
                var nameWords = Drawing.Words(name, 56, true, totalWidth - weightWords.W - 60);
                int titleHigh = Math.Max(nameWords.H, weightWords.H);
                if (titleHigh > 0)
                {
                    layout.Items.Add((xLeft, 0, nameWords));
                    layout.Items.Add((xLeft + totalWidth - weightWords.W, 0, weightWords));
                    titleHigh += sectionGap;
                }
                if (!string.IsNullOrWhiteSpace(mobility))
                {
                    if (col1High > 0) col1High += sectionGap;
                    if (Drawing.Words("机动性", 36, true, colWidth) is { } mobHeaderWords)
                    {
                        col1Items.Add((col1High, mobHeaderWords));
                        col1High += mobHeaderWords.H + headerGap;
                    }
                    if (Drawing.Words(mobility, 36, false, colWidth) is { } mobWords)
                    {
                        col1Items.Add((col1High, mobWords));
                        col1High += mobWords.H;
                    }
                }

                var col2Items = new List<(int YOffset, (int W, int H, byte[] Ink) Words)>();
                int col2High = 0;
                if (!string.IsNullOrWhiteSpace(armament))
                {
                    if (Drawing.Words("武器", 36, true, colWidth) is { } gunHeaderWords)
                    {
                        col2Items.Add((col2High, gunHeaderWords));
                        col2High += gunHeaderWords.H + headerGap;
                    }
                    if (Drawing.Words(armament, 36, false, colWidth) is { } gunWords)
                    {
                        col2Items.Add((col2High, gunWords));
                        col2High += gunWords.H;
                    }
                }

                var col3Items = new List<(int YOffset, (int W, int H, byte[] Ink) Words)>();
                int col3High = 0;
                if (!string.IsNullOrWhiteSpace(description))
                {
                    if (Drawing.Words("说明", 36, true, col3Width) is { } descHeaderWords)
                    {
                        col3Items.Add((col3High, descHeaderWords));
                        col3High += descHeaderWords.H + headerGap;
                    }
                    if (Drawing.Words(description, 36, false, col3Width) is { } descWords)
                    {
                        col3Items.Add((col3High, descWords));
                        col3High += descWords.H;
                    }
                }

                int blockHigh = Math.Max(col1High, Math.Max(col2High, col3High));
                layout.Height = titleHigh + blockHigh;
                foreach (var (y, w) in col1Items) layout.Items.Add((col1X, titleHigh + y, w));
                foreach (var (y, w) in col2Items) layout.Items.Add((col2X, titleHigh + y, w));
                foreach (var (y, w) in col3Items) layout.Items.Add((col3X, titleHigh + y, w));

                Plugin.ModLog.LogInfo($"TOOL_DRAWING title block: \"{name}\", {guns.Count} guns, {powerText}, {speedText}, {description.Length} characters of description");
            }
            catch (Exception ex) { Plugin.ModLog.LogWarning($"TOOL_DRAWING no title block: {ex.Message}"); }
            return layout;
        }

        /// The sheet's writing: each view's name over it; the overall length and height beside the side view, the width
        /// beside the view from above and under the front view (to the centimetre); a 1 m ruler, ticks every 10 cm.
        void Annotate(byte[] rgb, int w, int h, (int X, int Y)[] at, int xLeft)
        {
            for (int i = 0; i < Views.Length; i++)
            {
                string name = Views[i].Name.ToUpperInvariant();
                Drawing.Text(rgb, w, h, name, at[i].X + (views[i].Width - Drawing.TextWidth(name, Letter)) / 2, at[i].Y + views[i].Height + 14, Letter, 0);
            }
            var size = box.size;
            int Px(float metres) => (int)MathF.Round(metres * scale);
            string M(float metres) => metres.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " 米"; // invariant: a comma-decimal locale would print 12,34 米
            (int x, int y) Origin(int i, float across, float high)
            {
                var centre = views[i].Project(V(box.center));
                return (at[i].X + (int)MathF.Round(centre.X - across * scale / 2), at[i].Y + (int)MathF.Round(centre.Y - high * scale / 2));
            }
            var side = Origin(2, size.z, size.y); var top = Origin(0, size.z, size.x); var front = Origin(1, size.x, size.y);
            Drawing.Dimension(rgb, w, h, side.x, at[2].Y - 50, side.x + Px(size.z), at[2].Y - 50, M(size.z), Letter, 0);
            Drawing.Dimension(rgb, w, h, at[2].X - 50, side.y, at[2].X - 50, side.y + Px(size.y), M(size.y), Letter, 0);
            Drawing.Dimension(rgb, w, h, at[0].X - 50, top.y, at[0].X - 50, top.y + Px(size.x), M(size.x), Letter, 0);
            Drawing.Dimension(rgb, w, h, front.x, at[1].Y - 50, front.x + Px(size.x), at[1].Y - 50, M(size.x), Letter, 0);
            // The ruler.
            int x0 = xLeft, y0 = bottom + 50;
            Drawing.Box(rgb, w, h, x0, y0, x0 + Px(1), y0 + 2, 0);
            for (int k = 0; k <= 10; k++)
            {
                int x = x0 + (int)MathF.Round(k * scale / 10);
                Drawing.Box(rgb, w, h, x, y0, x + 1, y0 + (k % 5 == 0 ? 24 : 12), 0);
            }
            Drawing.Text(rgb, w, h, "1 米", x0 + Px(1) + 20, y0, Letter, 0);
        }

        // Read the gun and its actual laying drive without posing anything in the editor. One principal barrel per
        // trunnion keeps coaxial guns from covering the drawing in duplicate angle labels.
        void ReadGunMotion()
        {
            var components = DesignEditor.Instance?.AllComponents().ToList() ?? new();
            var guns = components.Select(c => c.TryCast<Sprocket.Vehicles.Cannons.Cannon>()).OfType<Sprocket.Vehicles.Cannons.Cannon>()
                .GroupBy(g => (int)g.VehicleObject.VUID).ToDictionary(g => g.Key, g => g.First());
            var parts = new Dictionary<int, DrawingMounts.Part>();
            foreach (var part in DesignEditor.Instance?.AllParts() ?? Enumerable.Empty<Sprocket.Vehicles.VehicleObject>())
            {
                var parent = part.VehicleTransform?.Parent?.VehicleObject;
                parts[(int)part.VUID] = new(parent == null ? -1 : (int)parent.VUID,
                    part.GUID == DrawingMounts.TrunnionGuid, part.GUID == Conversion.RingGuid);
            }
            var candidates = guns.Select(g => (Id: g.Key, Caliber: (int)(g.Value.Blueprint?.Caliber ?? 0))).ToArray();
            var included = guns.Where(g => gunLimits.Shows(DrawingSettings.GunKey(g.Value))).Select(g => g.Key).ToHashSet();
            foreach (var drive in components.Select(c => c.TryCast<Sprocket.Vehicles.Weapons.LayingDrive>()).OfType<Sprocket.Vehicles.Weapons.LayingDrive>())
            {
                try
                {
                    var trunnions = drive.Trunnions;
                    if (trunnions == null || drive.BlueprintSlot?.HasBlueprint != true) continue;
                    int mountId = (int)trunnions.VehicleObject.VUID;
                    int? gunId = DrawingMounts.SelectGun(mountId, candidates, parts, included.Contains);
                    if (!gunId.HasValue)
                    {
                        Plugin.ModLog.LogInfo($"TOOL_DRAWING mount {mountId}: no enabled gun on this mount; annotations skipped");
                        continue;
                    }
                    var cannon = guns[gunId.Value];
                    if (cannon?.Barrel == null) continue;
                    var shapes = new List<Drawing.Shape>();
                    var seen = new HashSet<IntPtr>();
                    foreach (var t in cannon.Barrel.BarrelTransforms)
                        if (t != null)
                            foreach (var filter in t.GetComponentsInChildren<MeshFilter>())
                                if (seen.Add(filter.Pointer) && filter.sharedMesh is { isReadable: true } mesh)
                                    shapes.Add(Shape(mesh, filter.transform.localToWorldMatrix));
                    var points = shapes.SelectMany(s => s.P).ToArray();
                    if (points.Length == 0) { Plugin.ModLog.LogWarning("TOOL_DRAWING gun limits: barrel mesh unavailable; skipped this mount"); continue; }
                    var pivot = V(trunnions.transform.position);
                    var forward = V(cannon.transform.forward);
                    float end = points.Max(p => N.Vector3.Dot(p, forward));
                    var muzzle = points.Where(p => end - N.Vector3.Dot(p, forward) < 0.002f).ToArray();
                    if (muzzle.Length == 0) { Plugin.ModLog.LogWarning($"TOOL_DRAWING gun limits: no muzzle vertices matched; skipped this mount"); continue; }
                    var tip = muzzle.Aggregate(N.Vector3.Zero, (sum, p) => sum + p) / muzzle.Length;
                    var bp = drive.BlueprintSlot.Blueprint;
                    Plugin.ModLog.LogInfo($"TOOL_DRAWING mount {mountId}: gun {gunId.Value}, {cannon.Blueprint?.Caliber} mm, elevation {bp.Elevation.Min} to {bp.Elevation.Max}");
                    motion.AddRange(DrawingOptions.Motion(shapes, pivot, tip, V(trunnions.transform.right), V(trunnions.transform.up),
                        bp.Elevation.Min, bp.Elevation.Max, bp.Azimuth.Min, bp.Azimuth.Max, elevation, traverse));
                }
                catch (Exception ex) { Plugin.ModLog.LogWarning($"TOOL_DRAWING gun limits: {ex.Message}; skipped this mount"); }
            }
            Plugin.ModLog.LogInfo($"TOOL_DRAWING {motion.Count} gun limit positions (elevation={elevation}, traverse={traverse})");
        }

        // A separate ring-centred overlay, including guns mounted directly to the turret with no laying drive.
        // Use the nearest turret ancestor so a nested turret is not attributed to its outer turret as well.
        void ReadTurretMotion()
        {
            var components = DesignEditor.Instance?.AllComponents().ToList() ?? new();
            var guns = components.Select(c => c.TryCast<Sprocket.Vehicles.Cannons.Cannon>()).OfType<Sprocket.Vehicles.Cannons.Cannon>()
                .Where(g => gunLimits.Shows(DrawingSettings.GunKey(g))).ToList();
            IntPtr Owner(Sprocket.Vehicles.Cannons.Cannon cannon)
            {
                var seen = new HashSet<IntPtr>();
                for (var t = cannon.VehicleTransform; t != null && seen.Add(t.Pointer); t = t.Parent)
                    if (t.VehicleObject?.GUID == Conversion.RingGuid) return t.VehicleObject.Pointer;
                return IntPtr.Zero;
            }
            foreach (var ring in components.Select(c => c.TryCast<Sprocket.Vehicles.Turrets.TurretRing>()).OfType<Sprocket.Vehicles.Turrets.TurretRing>())
            {
                try
                {
                    var slot = ring.motor?.TraverseBlueprintSlot;
                    if (slot?.HasBlueprint != true) continue;
                    var cannon = guns.Where(g => Owner(g) == ring.VehicleObject.Pointer).OrderByDescending(c => c.Blueprint?.Caliber ?? 0).FirstOrDefault();
                    if (cannon?.Barrel == null) continue;
                    var shapes = new List<Drawing.Shape>();
                    var seen = new HashSet<IntPtr>();
                    foreach (var t in cannon.Barrel.BarrelTransforms)
                        if (t != null)
                            foreach (var filter in t.GetComponentsInChildren<MeshFilter>())
                                if (seen.Add(filter.Pointer) && filter.sharedMesh is { isReadable: true } mesh)
                                    shapes.Add(Shape(mesh, filter.transform.localToWorldMatrix));
                    var points = shapes.SelectMany(s => s.P).ToArray();
                    if (points.Length == 0) { Plugin.ModLog.LogWarning("TOOL_DRAWING turret limits: unreadable barrel, skipped ring"); continue; }
                    var forward = V(cannon.transform.forward);
                    float end = points.Max(p => N.Vector3.Dot(p, forward));
                    var muzzle = points.Where(p => end - N.Vector3.Dot(p, forward) < 0.002f).ToArray();
                    if (muzzle.Length == 0) { Plugin.ModLog.LogWarning("TOOL_DRAWING turret limits: unreadable muzzle, skipped ring"); continue; }
                    var tip = muzzle.Aggregate(N.Vector3.Zero, (sum, p) => sum + p) / muzzle.Length;
                    var bp = slot.Blueprint;
                    motion.AddRange(DrawingOptions.TurretMotion(shapes, V(ring.transform.position), tip, V(ring.transform.up), bp.MinAngle, bp.MaxAngle));
                    Plugin.ModLog.LogInfo($"TOOL_DRAWING turret {ring.VehicleObject.VUID}: limits {bp.MinAngle} to {bp.MaxAngle} degrees");
                }
                catch (Exception ex) { Plugin.ModLog.LogWarning($"TOOL_DRAWING turret limits: {ex.Message}; skipped ring"); }
            }
        }

        /// Every mesh of the vehicle as a shape (world space). Some meshes the game keeps only on the graphics card:
        /// those are counted, and drawn by their outline alone.
        List<Drawing.Shape> Shapes(out int unreadable)
        {
            unreadable = 0;
            var shapes = new List<Drawing.Shape>();
            var seen = new HashSet<IntPtr>();
            foreach (var part in DesignEditor.Instance?.AllParts() ?? Enumerable.Empty<Sprocket.Vehicles.VehicleObject>())
                foreach (var filter in part.GetComponentsInChildren<MeshFilter>())
                {
                    if (!seen.Add(filter.Pointer)) continue; // a part's children are parts too
                    var r = filter.GetComponent<Renderer>();
                    var mesh = filter.sharedMesh;
                    if (r == null || aerials.Contains(r.Pointer) || !MeshTools.Drawn(r) || !r.gameObject.activeInHierarchy || mesh == null) continue;
                    if (!mesh.isReadable) { unreadable++; continue; }
                    var shape = Shape(mesh, filter.transform.localToWorldMatrix);
                    shape.ContourGroup = filter.GetComponentInParent<Sprocket.Vehicles.VehicleObject>()?.VUID ?? part.VUID;
                    shapes.Add(shape);
                }
            // Crew figures (anything animated), as they stand now: they hide what's behind them and hold their place
            // in the picture. Their outline is the picture's; the lines inside a figure would only clutter it.
            foreach (var part in DesignEditor.Instance?.AllParts() ?? Enumerable.Empty<Sprocket.Vehicles.VehicleObject>())
                foreach (var skin in part.GetComponentsInChildren<SkinnedMeshRenderer>())
                {
                    if (!seen.Add(skin.Pointer) || aerials.Contains(skin.Pointer) || !MeshTools.Drawn(skin) || !skin.gameObject.activeInHierarchy) continue;
                    var posed = new Mesh();
                    try
                    {
                        skin.BakeMesh(posed, true); // with its scale; its place and turn below
                        var shape = Shape(posed, Matrix4x4.TRS(skin.transform.position, skin.transform.rotation, Vector3.one));
                        shape.Edges = false;
                        shapes.Add(shape);
                    }
                    catch (Exception ex) { unreadable++; Plugin.ModLog.LogWarning($"TOOL_DRAWING couldn't shape {skin.name}: {ex.Message}"); }
                    finally { UnityEngine.Object.Destroy(posed); }
                }
            return shapes;
        }

        /// A mesh (its own space) placed by `world`, as a shape.
        static Drawing.Shape Shape(Mesh mesh, Matrix4x4 world)
        {
            var vertices = mesh.vertices;
            var triangles = mesh.triangles;
            var points = new N.Vector3[vertices.Length];
            for (int i = 0; i < points.Length; i++) points[i] = V(world.MultiplyPoint3x4(vertices[i]));
            var tris = new int[triangles.Length];
            for (int i = 0; i < tris.Length; i++) tris[i] = triangles[i];
            // The mesh's own vertex normals carry how the game shaded it (which edges the "smooth edge" tool rounded).
            // They only ever meet other normals of the same mesh, so a plain turn with the part's rotation is enough.
            var local = mesh.normals;
            N.Vector3[]? shading = null;
            if (local != null && local.Length == vertices.Length)
            {
                shading = new N.Vector3[local.Length];
                for (int i = 0; i < shading.Length; i++)
                {
                    var turned = world.MultiplyVector(local[i]);
                    shading[i] = V(turned.normalized);
                }
            }
            return Drawing.Weld(points, tris, shading, Plugin.DrawingCreaseNormals?.Value ?? false);
        }

        /// Everything as it was: the camera gone, the light and the floor back.
        internal void End()
        {
            // Restore layers BEFORE re-enabling or refreshing projectors. HDRP caches the layer on registration
            // and OnValidate; doing this in the opposite order leaves the decal cached on the export-only layer.
            foreach (var (obj, layer) in movedLayers.Values)
                Restore("object layer", () => { if (obj != null) obj.layer = layer; });
            movedLayers.Clear();
            PutArmourBack();
            foreach (var dp in hiddenEnvironmentProjectors)
                Restore("environment decal", () => { if (dp != null) dp.enabled = true; });
            hiddenEnvironmentProjectors.Clear();
            foreach (var (dp, dist, fade) in restoredDecals)
            {
                Restore("decal distance", () => { if (dp != null) dp.drawDistance = dist; });
                Restore("decal fade", () => { if (dp != null) dp.fadeScale = fade; });
            }
            restoredDecals.Clear();
            // Includes projectors whose distance/fade never needed changing, and those re-enabled for geometry.
            int refreshed = 0;
            foreach (var dp in projectors)
                Restore("decal render cache", () => { if (dp != null && dp.enabled) { dp.OnValidate(); refreshed++; } });
            if (projectors.Count > 0) Plugin.ModLog.LogInfo($"TOOL_DRAWING restored decal render caches after layers: {refreshed}/{projectors.Count}");
            projectors.Clear();
            foreach (var r in hidden) Restore("scene renderer", () => { if (r != null) r.enabled = true; });
            hidden.Clear();
            foreach (var t in hiddenGround) Restore("terrain", () => { if (t != null) t.enabled = true; });
            hiddenGround.Clear();
            Restore("drawing camera", () => { if (cam != null) { cam.targetTexture = null; UnityEngine.Object.Destroy(cam.gameObject); } });
            Restore("render target", () => { if (target != null) { target.Release(); UnityEngine.Object.Destroy(target); } });
            cam = null; target = null;
            if (lightingCaptured)
            {
                Restore("fullbright", () => { if (!fullbrightWas && MeshTools.FullbrightOn) MeshTools.ToggleFullbright(); });
                Restore("fog", () => { if (!fogWas) MeshTools.Fog(off: false); });
                lightingCaptured = false;
            }
        }

        // One removed scene object or failed restoration must not prevent all remaining decals from returning.
        static void Restore(string what, Action action)
        {
            try { action(); }
            catch (Exception ex) { Plugin.ModLog.LogError($"TOOL_DRAWING couldn't restore {what}: {ex}"); }
        }

        static void Fail(string what, Exception ex)
        {
            Plugin.ModLog.LogError($"TOOL_DRAWING {what}: {ex}");
            DesignEditor.Instance?.Say($"工程图纸：{what}：{ex.Message}", 8);
        }
    }
}
