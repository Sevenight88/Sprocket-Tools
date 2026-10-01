using System.Globalization;
using System.Text.RegularExpressions;
using HarmonyLib;
using Sprocket;
using Sprocket.Blueprints;
using Sprocket.Factions;
using Sprocket.UI;
using Sprocket.Vehicles;
using Sprocket.Vehicles.AssetManagement;
using Sprocket.Vehicles.PlateStructures.Design;
using VehicleDesigner.PaintJobs;

namespace SprocketTools;

/// Paint per part: a part can have its own paint job instead of the vehicle's. The game has spare paint slots (User 1
/// to 9) that nothing uses; the part's outside moves to one, with its own paint job ("Own paint 1", ...), edited in the
/// part's panel. Which parts use it is written in that paint job's description, so it's saved with the design and comes
/// back wherever the vehicle is built (editor, battles).
[HarmonyPatch]
public static class PartPaint
{
    const int First = (int)VehicleMaterialSlot.User1, Last = (int)VehicleMaterialSlot.User9;
    const string Tag = "SprocketTools parts:";
    static bool themesLogged;

    static IEditableVehicleMaterialPainter Jobs(VehicleMaterialPainter p) => p.Cast<IEditableVehicleMaterialPainter>();

    // Every vehicle's painter seen lately, newest first. A design loaded or rebuilt gets a new one, and previews have
    // their own, so a part's painter is looked up each time: the one whose materials include the part's.
    static readonly List<VehicleMaterialPainter> painters = new();

    static void Seen(VehicleMaterialPainter p)
    {
        if (painters.Count > 0 && painters[0].Pointer == p.Pointer) return;
        painters.RemoveAll(x => x.Pointer == p.Pointer);
        painters.Insert(0, p);
        if (painters.Count > 12) painters.RemoveAt(painters.Count - 1);
    }

    static int Count<T>(Il2CppSystem.Collections.Generic.IReadOnlyList<T> list) => list.Cast<Il2CppSystem.Collections.Generic.IReadOnlyCollection<T>>().Count;

    /// The painter painting this part now, or null.
    static VehicleMaterialPainter? PainterOf(VehicleObject part)
    {
        var transform = part.GetComponent<VehicleTransform>();
        if (transform == null) return null;
        foreach (var p in painters.ToList())
            try
            {
                if (p.materialRegister != null && p.materialRegister.TryGetMappedMaterials(transform, out var materials) && materials != null && Count(materials) > 0) return p;
            }
            catch (Exception) { painters.Remove(p); } // released with its vehicle
        return null;
    }

    static VehicleMaterialPainter? PainterOf(int vuid) =>
        DesignEditor.Instance?.AllParts().FirstOrDefault(o => (int)o.VUID == vuid) is { } part ? PainterOf(part) : null;

    /// Part number -> own paint slot, read from the own paint jobs' descriptions.
    static Dictionary<int, int> Owners(VehicleMaterialPainter p)
    {
        var owners = new Dictionary<int, int>();
        var jobs = Jobs(p);
        for (int slot = First; slot < Math.Min(jobs.PaintJobCount, Last + 1); slot++)
            if (jobs.GetPaintJob(slot)?.Description is string d && d.StartsWith(Tag))
                foreach (var word in d[Tag.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    if (int.TryParse(word, out int vuid)) owners[vuid] = slot;
        return owners;
    }

    /// As each part's material is painted: a part with its own paint goes to its slot (only the outside). The
    /// vehicle's own painter holds the list, so this works for any vehicle, in the editor or in battle.
    /// (Whichever way the game paints it: a material just registered, or any repaint.)
    [HarmonyPrefix, HarmonyPatch(typeof(VehicleMaterialPainter), nameof(VehicleMaterialPainter.PaintRegisteredMaterial))]
    static void Registered(VehicleMaterialPainter __instance, VehicleMaterial item) => Place(__instance, item);

    [HarmonyPrefix, HarmonyPatch(typeof(VehicleMaterialPainter), nameof(VehicleMaterialPainter.Process), new[] { typeof(VehicleMaterial) })]
    static void Painted(VehicleMaterialPainter __instance, VehicleMaterial material) => Place(__instance, material);

    [HarmonyPrefix, HarmonyPatch(typeof(VehicleMaterialPainter), nameof(VehicleMaterialPainter.Process), new[] { typeof(VehicleMaterial), typeof(VehicleTransform) })]
    static void PaintedOn(VehicleMaterialPainter __instance, VehicleMaterial material) => Place(__instance, material);

    static void Place(VehicleMaterialPainter painter, VehicleMaterial? item) => Ui.Guard("Own paint", () =>
    {
        Seen(painter);
        if (item == null || item.PaintSlot != VehicleMaterialSlot.Exterior || item.associatedTransform?.VehicleObject is not { } part) return;
        if (OwnersNow(painter).TryGetValue((int)part.VUID, out int slot)) item.PaintSlot = (VehicleMaterialSlot)slot;
    });

    // The game paints every material in turn (thousands on loading): the part lists are read once a frame, not each time.
    static IntPtr ownersFor;
    static int ownersFrame = -1;
    static Dictionary<int, int> ownersNow = new();

    static Dictionary<int, int> OwnersNow(VehicleMaterialPainter p)
    {
        if (p.Pointer != ownersFor || UnityEngine.Time.frameCount != ownersFrame)
        {
            ownersNow = Owners(p);
            ownersFor = p.Pointer;
            ownersFrame = UnityEngine.Time.frameCount;
        }
        return ownersNow;
    }

    [HarmonyPostfix, HarmonyPatch(typeof(PlateStructureEditor), nameof(PlateStructureEditor.OnGUI))]
    static void Draw(PlateStructureEditor __instance, IGUILayout layout) => Ui.Guard("Own paint", () =>
    {
        var editor = DesignEditor.Instance;
        var ui = layout.TryCast<IGUIElementDrawer>();
        if (editor == null || ui == null || PainterOf(__instance.Component.VehicleObject) is not { } p) return;
        int vuid = (int)__instance.Component.VehicleObject.VUID;
        var owners = Owners(p);
        int slot = owners.GetValueOrDefault(vuid);
        int used = owners.Values.DefaultIfEmpty(First - 1).Max() - First + 1; // own paints in use
        var parts = editor.SelectedParts().Append(vuid).Distinct().ToList();
        Ui.Section(layout, "自有涂装");
        // Cycles: vehicle paint, own paint 1, 2, ... up to one more than in use, then back.
        int next = slot == 0 ? First : slot - First + 1 < Math.Min(used + 1, Last - First + 1) ? slot + 1 : 0;
        var tip = new UITooltip("自有涂装", "为本部件（以及其余所选部件）单独创建涂装。新涂装以主涂装为副本起步。" +
                                "只改变外表面；内表面仍使用载具的内侧涂装。");
        ui.Button($"涂装：{(slot == 0 ? "载具" : Name(slot))}  （点击：{(next == 0 ? "载具" : Name(next))}）", Ui.Callback(() => Ui.Guard("Own paint", () =>
        {
            Commit();
            Assign(p, parts, next);
            __instance.RequestRedraw();
        })), ref tip);
        if (slot == 0 || Jobs(p).GetPaintJob(slot) is not { } job) return;

        // The own paint's settings, as the Paint tab has them for the vehicle's paints. Each drag is one Ctrl+Z step.
        void Slider(string label, string what, Func<PaintJob, float> get, Action<PaintJob, float> set, float min = 0, float max = 100) =>
            ui.Slider(label, get(job) * 100, min, max, Ui.FloatCallback(v => Ui.Guard("Own paint", () => Slide(vuid, slot, what, get, set, v / 100))));
        Slider("红（%）", "红色", j => j.TintR, (j, v) => j.TintR = v);
        Slider("绿（%）", "绿色", j => j.TintG, (j, v) => j.TintG = v);
        Slider("蓝（%）", "蓝色", j => j.TintB, (j, v) => j.TintB = v);
        Slider("饱和度（%）", "饱和度", j => j.Saturation, (j, v) => j.Saturation = v);
        Slider("粗糙度（%）", "粗糙度", j => j.Roughness, (j, v) => j.Roughness = v);
        Slider("金属度（%）", "金属度", j => j.Metallic, (j, v) => j.Metallic = v);
        Slider("完好度（%）", "完好度", j => j.Condition, (j, v) => j.Condition = v);
        Slider("污渍（%）", "污渍", j => j.Grime, (j, v) => j.Grime = v);
        Slider("迷彩缩放（%）", "迷彩缩放", j => j.Scale, (j, v) => j.Scale = v, 10, 400);
        // The faction's own schemes, as the Paint tab's dropdown lists them: picking one applies it whole.
        var schemes = PaintSchemes.Load();
        var names = new Il2CppSystem.Collections.Generic.List<string>();
        names.Add("（自定义：不套用）");
        foreach (var s in schemes) names.Add(s.Name);
        int selected = schemes.FindIndex(s => s.SameValuesAs(PaintSchemes.Scheme.Of(job)));
        var schemeTip = "套用当前派系 Blueprints\\Paint Schemes 里自己保存的涂装方案，与“涂装”标签页那个下拉菜单同一份列表：迷彩图连同颜色、缩放、完好度与污渍一起套用。\n" +
                        "“（自定义）”表示现在的涂装和列表里任何一个都不完全相同；在那里改过滑条也会回到这一项。";
        ui.Dropdown("涂装方案", names.Cast<Il2CppSystem.Collections.Generic.IReadOnlyList<string>>(), selected + 1,
            Ui.IntCallback(index => Ui.Guard("Paint scheme", () =>
        {
            if (index <= 0 || index > schemes.Count) return;
            Commit();
            var scheme = schemes[index - 1];
            var was = PaintSchemes.Scheme.Of(job);
            void Put(PaintSchemes.Scheme v)
            {
                if (PainterOf(vuid) is not { } q || Jobs(q).GetPaintJob(slot) is not { } j) return;
                v.ApplyTo(j);
                Repaint(q, slot);
                Hotkeys.Current?.RequestRedraw();
            }
            if (!editor.Undoable($"{Name(slot)} 套用 {scheme.Name}", () => Put(scheme), () => Put(was))) Put(scheme);
        })), schemeTip);
    });

    /// A slider being dragged: the change shows at once; when it's left alone (or another setting is touched) the
    /// whole drag becomes one undoable step.
    sealed class Drag
    {
        public readonly int Part; // whose painter: looked up again each time
        public readonly int Slot;
        public readonly string What;
        public readonly Action<PaintJob, float> Set;
        public readonly float Old;
        public float New, At;
        public Drag(int part, int slot, string what, Action<PaintJob, float> set, float old) =>
            (Part, Slot, What, Set, Old) = (part, slot, what, set, old);
    }
    static Drag? drag;

    static void Slide(int part, int slot, string what, Func<PaintJob, float> get, Action<PaintJob, float> set, float value)
    {
        if (PainterOf(part) is not { } p || Jobs(p).GetPaintJob(slot) is not { } job) return;
        if (drag != null && (drag.Slot != slot || drag.What != what || drag.Part != part)) Commit();
        drag ??= new Drag(part, slot, what, set, get(job));
        set(job, value);
        drag.New = value;
        drag.At = UnityEngine.Time.unscaledTime;
        Repaint(p, slot);
    }

    /// From DesignEditor.Update: a slider left alone for half a second is done.
    internal static void Tick()
    {
        if (drag != null && UnityEngine.Time.unscaledTime - drag.At > 0.5f) Commit();
    }

    static void Commit()
    {
        var d = drag;
        drag = null;
        if (d == null || Math.Abs(d.New - d.Old) < 1e-6f) return;
        void Put(float v)
        {
            if (PainterOf(d.Part) is not { } q || Jobs(q).GetPaintJob(d.Slot) is not { } j) return;
            d.Set(j, v);
            Repaint(q, d.Slot);
            Hotkeys.Current?.RequestRedraw(); // the panel's slider follows
        }
        DesignEditor.Instance?.Undoable($"{Name(d.Slot)} {d.What}", () => Put(d.New), () => Put(d.Old));
    }

    static string Name(int slot) => $"自有涂装 {slot - First + 1}";

    /// The paint job's settings to its materials: reloaded, then every material on that slot repainted.
    static void Repaint(VehicleMaterialPainter p, int slot)
    {
        p.GetPaintJob(slot)?.PaintJobReference.MarkModified(); // saved with the design
        p.GetPaintJob(slot)?.Refresh();
        var materials = p.materialRegister.Materials;
        int count = materials.Cast<Il2CppSystem.Collections.Generic.IReadOnlyCollection<VehicleMaterial>>().Count;
        for (int i = 0; i < count; i++)
            if (materials[i] is { } m && (int)m.PaintSlot == slot && p.materialRegister.TryGetMappedTransform(m, out var t)) p.Process(m, t);
    }

    /// Gives `parts` paint `slot` (0: the vehicle's) as one undoable step: Ctrl+Z puts back each part's paint from before.
    static void Assign(VehicleMaterialPainter p, List<int> parts, int slot)
    {
        var owners = Owners(p);
        var before = parts.ToDictionary(v => v, v => owners.GetValueOrDefault(v));
        var after = parts.ToDictionary(v => v, _ => slot);
        int moved = 0;
        int any = parts[0]; // its painter when the step runs (a rebuilt design has a new one)
        if (DesignEditor.Instance?.Undoable(slot == 0 ? "载具涂装" : Name(slot), () => moved = SetSlots(PainterOf(any) ?? p, after),
                                            () => SetSlots(PainterOf(any) ?? p, before)) != true)
            moved = SetSlots(p, after);
        DesignEditor.Instance?.Say(moved > 0 ? $"共 {parts.Count} 个部件{(parts.Count == 1 ? "" : "")}：{(slot == 0 ? "载具涂装" : Name(slot) + "（其颜色在本面板中调整）")}。可用 Ctrl+Z 撤销。"
                                             : "自有涂装：这些部件上没有可涂装的材质", 5);
    }

    /// Each part to its paint slot (0: the vehicle's), making own paint jobs that aren't there yet, and repaints them.
    /// Returns how many materials moved.
    static int SetSlots(VehicleMaterialPainter p, Dictionary<int, int> slots)
    {
        foreach (int slot in slots.Values.Where(s => s != 0).Distinct()) EnsureJob(p, slot);
        var jobs = Jobs(p);
        // The lists of parts, one per own paint.
        var owners = Owners(p);
        foreach (var (v, slot) in slots) { if (slot == 0) owners.Remove(v); else owners[v] = slot; }
        for (int s = First; s < Math.Min(jobs.PaintJobCount, Last + 1); s++)
            if (jobs.GetPaintJob(s) is { } j && (j.Description ?? "").StartsWith(Tag))
                j.Description = Tag + " " + string.Join(" ", owners.Where(o => o.Value == s).Select(o => o.Key).OrderBy(v => v));
        ownersFrame = -1; // the lists changed: read them again
        // The parts' outside materials to their slots.
        int moved = 0;
        foreach (var part in DesignEditor.Instance!.AllParts().Where(o => slots.ContainsKey((int)o.VUID)))
        {
            int slot = slots[(int)part.VUID];
            var transform = part.GetComponent<VehicleTransform>();
            if (transform == null || !p.materialRegister.TryGetMappedMaterials(transform, out var materials) || materials == null) continue;
            int count = materials.Cast<Il2CppSystem.Collections.Generic.IReadOnlyCollection<VehicleMaterial>>().Count;
            for (int i = 0; i < count; i++)
            {
                var m = materials[i];
                if (m == null || !(m.PaintSlot == VehicleMaterialSlot.Exterior || (int)m.PaintSlot >= First)) continue; // the outside only
                m.PaintSlot = slot == 0 ? VehicleMaterialSlot.Exterior : (VehicleMaterialSlot)slot;
                p.Process(m, transform);
                moved++;
            }
        }
        foreach (int slot in slots.Values.Where(s => s != 0).Distinct()) Repaint(p, slot);
        Hotkeys.Current?.RequestRedraw();
        var check = Owners(p);
        Plugin.ModLog.LogInfo($"Own paint: {string.Join(", ", slots.Select(s => $"{s.Key} -> {(s.Value == 0 ? "vehicle" : Name(s.Value))}"))}, {moved} materials; " +
                              $"read back: {string.Join(",", slots.Keys.Select(v => $"{v}={check.GetValueOrDefault(v)}"))}");
        return moved;
    }

    /// Own paint `slot`'s paint job: made if it isn't there yet (registered with the design, so it's saved, and listed
    /// in the design's paint jobs), starting as a copy of the Primary paint.
    static void EnsureJob(VehicleMaterialPainter p, int slot)
    {
        var jobs = Jobs(p);
        if (slot != 0 && jobs.PaintJobCount <= slot)
        {
            int had = jobs.PaintJobCount;
            p.SetPaintJobCount(slot + 1);
            Plugin.ModLog.LogInfo($"Own paint: paint jobs {had} -> {jobs.PaintJobCount}: " +
                                  string.Join("; ", Enumerable.Range(0, jobs.PaintJobCount).Select(i => jobs.GetPaintJob(i) is { } j ? $"{i} '{j.Name}' slot {j.Slot} '{j.ColourMapUri}'" : $"{i} none")));
        }
        if (!themesLogged)
        {
            themesLogged = true;
            var names = PaintJobDesigner.PaintThemeNames;
            var tags = PaintJobDesigner.PaintThemeTags;
            Plugin.ModLog.LogInfo($"Own paint: Paint tab themes [{(names == null ? "" : string.Join(", ", names))}], tags [{(tags == null ? "" : string.Join(", ", tags))}]");
        }
        // A new slot has no paint job in it yet: make one, registered with the design (so it's saved), and note its
        // number in the design's list of paint jobs.
        if (slot != 0 && jobs.GetPaintJob(slot) == null && p.GetPaintJob(slot) is { } loader)
        {
            var made = loader.PaintJobReference.EnsureReference();
            var register = p.blueprint.Blueprint;
            var ids = Enumerable.Range(0, jobs.PaintJobCount).Select(i => p.GetPaintJob(i) is { } l && l.PaintJobReference.HasBlueprint ? l.PaintJobReference.BlueprintID : -1).ToArray();
            string was = string.Join(",", register.PaintJobIDs ?? new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>(0));
            register.PaintJobIDs = ids;
            p.blueprint.NotifyModified();
            Plugin.ModLog.LogInfo($"Own paint: made a paint job for slot {slot} ({(made == null ? "none" : "id " + loader.PaintJobReference.BlueprintID)}); paint job list [{was}] -> [{string.Join(",", ids)}]");
        }
        if (slot != 0 && jobs.GetPaintJob(slot) is { } job && !(job.Description ?? "").StartsWith(Tag))
        {
            // A new own paint: named, and looking like the Primary paint until it's changed.
            var primary = jobs.GetPaintJob(0);
            job.Name = Name(slot);
            job.Description = Tag;
            ownersFrame = -1;
            job.ColourMapUri = primary.ColourMapUri;
            job.Scale = primary.Scale; job.Roughness = primary.Roughness; job.Metallic = primary.Metallic;
            job.TintR = primary.TintR; job.TintG = primary.TintG; job.TintB = primary.TintB;
            job.Saturation = primary.Saturation; job.Condition = primary.Condition; job.Grime = primary.Grime;
        }
    }
}

/// The paint schemes the faction has saved — the list the Paint tab's 涂装方案 dropdown reads: each
/// Factions\<派系>\Blueprints\Paint Schemes\*.blueprint is one flat JSON paint job. Read straight from the files here
/// rather than through the game's blueprint loader (a generic method call), and at most once a second, since the
/// panel redraws every frame.
static class PaintSchemes
{
    internal sealed class Scheme
    {
        internal string Name = "", ColourMapUri = "";
        internal float Scale, Roughness, Metallic, R, G, B, Saturation, Condition, Grime;

        /// A saved scheme as it stands on a live paint job (used both to show which scheme is on and to undo).
        internal static Scheme Of(PaintJob j) => new()
        {
            ColourMapUri = j.ColourMapUri ?? "", Scale = j.Scale, Roughness = j.Roughness, Metallic = j.Metallic,
            R = j.TintR, G = j.TintG, B = j.TintB, Saturation = j.Saturation, Condition = j.Condition, Grime = j.Grime,
        };

        internal void ApplyTo(PaintJob j)
        {
            j.ColourMapUri = ColourMapUri; j.Scale = Scale; j.Roughness = Roughness; j.Metallic = Metallic;
            j.TintR = R; j.TintG = G; j.TintB = B; j.Saturation = Saturation; j.Condition = Condition; j.Grime = Grime;
        }

        // The panel carries these as percentages, so a round trip through the slider is never bit-for-bit the scheme's value.
        internal bool SameValuesAs(Scheme o) =>
            ColourMapUri == o.ColourMapUri && Close(Scale, o.Scale) && Close(Roughness, o.Roughness) && Close(Metallic, o.Metallic) &&
            Close(R, o.R) && Close(G, o.G) && Close(B, o.B) && Close(Saturation, o.Saturation) && Close(Condition, o.Condition) && Close(Grime, o.Grime);

        static bool Close(float a, float b) => Math.Abs(a - b) < 1e-3f;
    }

    static List<Scheme> cached = new();
    static float nextScan;
    static string loggedFolder = "";

    internal static List<Scheme> Load()
    {
        if (UnityEngine.Time.unscaledTime < nextScan) return cached;
        nextScan = UnityEngine.Time.unscaledTime + 1f;
        var folder = Folder();
        var found = new List<Scheme>();
        if (folder != null && Directory.Exists(folder))
            foreach (var file in Directory.GetFiles(folder, "*.blueprint"))
                try { found.Add(Read(file)); }
                catch (Exception ex) { Note($"{Path.GetFileName(file)}: {ex.Message}"); }
        found.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        if (loggedFolder != folder)
        {
            loggedFolder = folder ?? "";
            Plugin.ModLog.LogInfo($"TOOL_SCHEMES folder='{folder ?? "none"}' schemes={found.Count} [{string.Join(", ", found.Select(s => s.Name))}]");
        }
        return cached = found;
    }

    /// The current faction's scheme folder: the game's own route first, then the documented place under My Games with
    /// the faction name off the current faction.
    static string? Folder()
    {
        try
        {
            var byGame = BlueprintFiler.GetDirectory(GameDataDirectory.CurrentFaction, "Paint Schemes");
            if (!string.IsNullOrEmpty(byGame) && Directory.Exists(byGame)) return byGame;
        }
        catch (Exception ex) { Note("game route: " + ex.Message); }
        try
        {
            var name = FactionManager.CurrentFaction?.Directory;
            if (string.IsNullOrEmpty(name)) return null;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "My Games", "Sprocket", "Factions", name, "Blueprints", "Paint Schemes");
        }
        catch (Exception ex) { Note("faction name: " + ex.Message); return null; }
    }

    static Scheme Read(string file)
    {
        var json = File.ReadAllText(file);
        var scheme = new Scheme
        {
            Name = Text(json, "name"), ColourMapUri = Text(json, "colourMapUrl"),
            Scale = Number(json, "scale", 1f), Roughness = Number(json, "roughness", .5f), Metallic = Number(json, "metallic", .5f),
            R = Number(json, "r", 1f), G = Number(json, "g", 1f), B = Number(json, "b", 1f),
            Saturation = Number(json, "saturation", 1f), Condition = Number(json, "condition", 1f), Grime = Number(json, "grime", 0f),
        };
        if (string.IsNullOrEmpty(scheme.Name)) scheme.Name = Path.GetFileNameWithoutExtension(file);
        return scheme;
    }

    static string Text(string json, string key) => Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"(.*?)\"").Groups[1].Value;

    static float Number(string json, string key, float fallback) =>
        float.TryParse(Regex.Match(json, "\"" + key + "\"\\s*:\\s*(-?[0-9.eE+-]+)").Groups[1].Value,
            NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : fallback;

    static readonly HashSet<string> notes = new();

    static void Note(string what)
    {
        if (notes.Add(what)) Plugin.ModLog.LogInfo($"TOOL_SCHEMES {what}");
    }
}

/// Images the game loads by address (camo in the Paint tab, decals): each address is logged once, and a local image the
/// address doesn't find, but which is in My Games\Sprocket, is loaded from there (the address written properly, spaces
/// and accents escaped).
[HarmonyPatch]
public static class ImageAddresses
{
    static readonly HashSet<string> seen = new();

    // Where every image download gets its address, whichever game code asks (hooks on the game's own image loader
    // never ran: its small methods are compiled into their callers).
    [HarmonyPrefix, HarmonyPatch(typeof(UnityEngine.Networking.UnityWebRequestTexture), nameof(UnityEngine.Networking.UnityWebRequestTexture.GetTexture), new[] { typeof(string) })]
    static void Texture(ref string uri) { var u = uri; Ui.Guard("Image address", () => u = Fix(u)); uri = u; }

    [HarmonyPrefix, HarmonyPatch(typeof(UnityEngine.Networking.UnityWebRequestTexture), nameof(UnityEngine.Networking.UnityWebRequestTexture.GetTexture), new[] { typeof(string), typeof(bool) })]
    static void TextureReadable(ref string uri) { var u = uri; Ui.Guard("Image address", () => u = Fix(u)); uri = u; }

    [HarmonyPrefix, HarmonyPatch(typeof(UnityEngine.Networking.UnityWebRequest), nameof(UnityEngine.Networking.UnityWebRequest.url), MethodType.Setter)]
    static void Url(ref string value) { var u = value; Ui.Guard("Image address", () => u = Fix(u)); value = u; }

    /// The same address, or one for the same image in My Games\Sprocket when the address doesn't find it.
    static string Fix(string uri)
    {
        if (string.IsNullOrEmpty(uri)) return uri;
        bool fileUri = uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
        if (!fileUri && (uri.Contains("://") || !uri.Contains('/') && !uri.Contains('\\'))) return uri; // web or game asset: not ours to touch
        string path;
        try { path = fileUri ? new Uri(uri).LocalPath : uri; } catch (Exception) { path = uri; }
        string? found = File.Exists(path) ? path : null;
        if (found == null)
        {
            // Relative ("Sprocket/Paint/x.png") or moved: look under My Games for the same place, then the same name.
            var games = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games");
            var tail = uri.Replace('\\', '/');
            int at = tail.IndexOf("Sprocket/", StringComparison.OrdinalIgnoreCase);
            var candidate = at >= 0 ? Path.Combine(games, tail[at..].Replace('/', Path.DirectorySeparatorChar)) : "";
            if (File.Exists(candidate)) found = candidate;
            else
                foreach (var folder in new[] { "Paint", "Decals" })
                    if (Path.Combine(games, "Sprocket", folder, Path.GetFileName(path)) is var named && File.Exists(named)) { found = named; break; }
        }
        if (found == null) { Log(uri, "file not found in My Games\\Sprocket either"); return uri; }
        // Written properly (spaces and accents escaped); an address already just that stays as it is.
        var proper = new Uri(found).AbsoluteUri;
        if (proper == uri) { Log(uri, null); return uri; }
        Log(uri, "loaded as " + proper);
        return proper;
    }

    static void Log(string uri, string? note)
    {
        if (seen.Add(uri)) Plugin.ModLog.LogInfo($"Image: '{uri}'{(note == null ? "" : ": " + note)}");
    }
}
