using BepInEx;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Sprocket.UI;
using Sprocket.VehicleDesigner;
using Sprocket.VehicleDesigner.Access;
using Sprocket.Vehicles;
using Sprocket.Vehicles.MeshGeneration;
using Sprocket.Vehicles.PlateStructures;
using Sprocket.Vehicles.PlateStructures.Design;
using UnityEngine;
using NativeTask = Il2CppSystem.Threading.Tasks.Task;

namespace SprocketTools;

/// What a design edit produced: the new blueprint, the part to offer Restore on, and a line for the log.
public record EditResult(string Json, int FocusPart, string Log);

/// Runs QoL design edits: save the open design as JSON, change it, back both up, and have the game load the result.
/// The game has no way to change a part's type in place, and its own loader always rebuilds a valid vehicle.
public sealed class DesignEditor : MonoBehaviour
{
    internal static DesignEditor? Instance;
    private VehicleDesignerCore? core;
    private VehicleBlueprintSerializer? serializer;
    private NativeTask? pending;
    private System.Action? queued;
    private Func<string, EditResult>? edit;
    private Func<string, AddonEdits.EditPlan>? liveEdit;
    private static readonly List<object> alive = new(); // delegates handed to the game's undo history must not be collected
    private string editName = "", doneMessage = "", status = "";
    private string? recoveryJson, recoveryDir;
    private bool restoring, ready, busy;
    private int waits;
    private float nextLookup, statusUntil;
    internal int LastEditedPart { get; private set; } = -1;
    internal bool CanRestore => recoveryJson != null && !busy;
    internal VehicleDesignerCore? Core => core;
    public DesignEditor(IntPtr pointer) : base(pointer) { Instance = this; }

    internal void Say(string text, float seconds = 6) { status = text; statusUntil = Time.unscaledTime + seconds; }

    internal void RequestEdit(string name, string done, Func<string, EditResult> change)
    {
        if (busy) return;
        Plugin.ModLog.LogInfo($"QOL requested: {name}");
        editName = name; doneMessage = done; edit = change;
        busy = true; Say(name + "……", 30);
        queued = RunEdit; // runs from Update, outside the game's inspector drawing
    }

    /// An edit applied in place as one of the game's undoable steps (Ctrl+Z), falling back to loading the edited
    /// design when it can't be (a changed part shares its mesh, or applying in place fails).
    internal void RequestLiveEdit(string name, string done, Func<string, AddonEdits.EditPlan> change)
    {
        if (busy) return;
        Plugin.ModLog.LogInfo($"QOL requested: {name}");
        editName = name; doneMessage = done; liveEdit = change;
        busy = true; Say(name + "……", 30);
        queued = RunLiveEdit;
    }

    private Func<string, (string Json, List<List<(int Source, int Added)>> Groups, string Log)>? separate;

    /// Separate as one of the game's own steps, so Ctrl+Z undoes it: for each new add-on (a group: the part's copy, and
    /// its twin's) the game duplicates the part (and its twin), then the copies take their faces and the part keeps the
    /// rest. The result is saved to memory and checked against the planned design; if anything differs (or the part is a
    /// hull or turret, which can't be duplicated into an add-on), the planned design is loaded instead.
    internal void RequestSeparate(string name, string done, Func<string, (string Json, List<List<(int Source, int Added)>> Groups, string Log)> plan)
    {
        if (busy) return;
        Plugin.ModLog.LogInfo($"QOL requested: {name}");
        editName = name; doneMessage = done; separate = plan;
        busy = true; Say(editName + "……", 30);
        queued = RunSeparate;
    }

    private void RunSeparate()
    {
        if (!EditorIdle(RunSeparate)) return;
        string original = Snapshot();
        var (planned, groups, log) = separate!(original);
        Backup(original, planned);
        Plugin.ModLog.LogInfo($"TOOL_EDIT {editName}: {log}; backup={recoveryDir}");
        string? why = null;
        try { why = SeparateInPlace(original, planned, groups); }
        catch (Exception ex) { why = ex.Message; Plugin.ModLog.LogWarning($"TOOL_LIVE separate: {ex}"); }
        if (why == null) { busy = false; return; }
        Plugin.ModLog.LogInfo($"TOOL_LIVE separate not in place ({why}); loading the planned design instead");
        recoveryJson = original;
        LastEditedPart = groups[0][0].Added;
        restoring = false;
        doneMessage += "可用“恢复”撤销。";
        pending = core!.Load(serializer!.DeserializeJSON(planned), Il2CppSystem.Threading.CancellationToken.None);
    }

    /// Null when done in place; else why not (nothing changed then, or the check found a difference and the planned
    /// design is about to be loaded over it).
    private string? SeparateInPlace(string original, string planned, List<List<(int Source, int Added)>> groups)
    {
        var byId = new Dictionary<int, VehicleObject>();
        foreach (var o in AllParts()) byId[(int)o.VUID] = o;
        PlateStructure StructureOf(VehicleObject o) => Each(o.Components).Select(c => c?.TryCast<PlateStructure>()).FirstOrDefault(s => s?.Mesh != null)
                                                       ?? throw new Exception($"part {(int)o.VUID} has no plate structure");
        var parts = groups[0]; // every new add-on comes from the same part (and its twin)
        var sources = parts.Select(p => byId.TryGetValue(p.Source, out var o) ? o : throw new Exception($"part {p.Source} isn't in the vehicle")).ToList();
        if (sources.Any(o => o.GUID != Conversion.AddonGuid)) return "a hull or turret can't be duplicated into an add-on";
        // The parts' live shape may be shared only with each other and the game's mirror images, or loading it changes others.
        var saved = Conversion.Objects(Conversion.Parse(original)).Keys.ToHashSet();
        var shapes = sources.Select(o => StructureOf(o).Mesh.Pointer).ToHashSet();
        foreach (var (v, o) in byId)
            if (saved.Contains(v) && !parts.Any(p => p.Source == v) && Each(o.Components).Any(c => c?.TryCast<PlateStructure>() is { Mesh: { } m } && shapes.Contains(m.Pointer)))
                return $"part {v} shares the shape";
        var before = Meshes(original);
        var after = Meshes(planned);
        var oldShape = before[AddonEdits.MeshIdOf(original, parts[0].Source)];
        var keptShape = after[AddonEdits.MeshIdOf(planned, parts[0].Source)];
        var newShapes = groups.Select(g => after[AddonEdits.MeshIdOf(planned, g[0].Added)]).ToList();

        var ops = core!.Editor.operations;
        int group = ops.GetNewGroupID();
        var refs = new Il2CppReferenceArray<ISoftVehicleObject>(sources.Select(o => o.GetReference()).ToArray());
        var dups = groups.Select(_ => ops.Duplicate(refs, DuplicateOptions.Copy, group)).ToList(); // one copy per new add-on
        List<VehicleObject> Copies(int i) => sources.Select(s => dups[i].GetDupe(s) ?? throw new Exception($"the game made no copy of part {(int)s.VUID}")).ToList();
        var copies = Enumerable.Range(0, groups.Count).Select(Copies).ToList();
        var allCopies = copies.SelectMany(c => c).ToList();
        if (allCopies.Select(c => c.Pointer).Distinct().Count() != allCopies.Count) return "the game made one copy for two pieces";
        // Parts attached to the part were copied with it: they stay on the part only.
        var extra = allCopies.SelectMany(c => Each(c.GetComponent<VehicleTransform>().Children)).Select(t => t?.VehicleObject).Where(o => o != null).Select(o => o!.GetReference()).ToArray();
        if (extra.Length > 0) ops.Destroy(new Il2CppReferenceArray<ISoftVehicleObject>(extra), group);
        string name = editName, done = doneMessage;
        void Shapes(bool forward)
        {
            foreach (var s in sources) { StructureOf(s).Mesh.LoadBlueprint(forward ? keptShape : oldShape); StructureOf(s).RequestMeshRefresh(); }
            if (!forward) return;
            for (int i = 0; i < groups.Count; i++)
            {
                var now = Copies(i);
                PlateStructureMesh? own = null; // the copies' own shape (a twin pair's copies share it, as twins do)
                for (int j = 0; j < now.Count; j++)
                {
                    // Exactly where the part is (a copy the game offsets, as for Alt-dragging, goes back onto it).
                    var (from, to) = (sources[j].GetComponent<VehicleTransform>(), now[j].GetComponent<VehicleTransform>());
                    to.SetLocalPositionAndRotation(from.LocalPosition, from.LocalRotation);
                    // The game's copy still shares the part's shape (the game splits them only when one is edited): it
                    // gets a new one from the vehicle's shape register, or loading its faces would change the part too.
                    var structure = StructureOf(now[j]);
                    if (sources.Any(s => StructureOf(s).Mesh.Pointer == structure.Mesh.Pointer))
                        structure.Mesh = own ??= core!.Target.Cast<IVehicleGateway>().Meshes.New<PlateStructureMesh>();
                    structure.Mesh.LoadBlueprint(newShapes[i]);
                    structure.RequestMeshRefresh();
                }
                if (now.Count == 2 && now[0].GetComponent<VehicleTransform>() is { } a && now[1].GetComponent<VehicleTransform>() is { } b && a.Mirror?.Pointer != b.Pointer)
                    VehicleTransform.SetMirrors(a, b);
            }
        }
        bool executed = false;
        var callbacks = new VehicleOperationCallbacks
        {
            Execute = Keep(DelegateSupport.ConvertDelegate<Il2CppSystem.Func<IVehicleOperatorContext, VehicleOPExecutionResult>>(
                new Func<IVehicleOperatorContext, VehicleOPExecutionResult>(_ =>
                {
                    try { Shapes(true); executed = true; return VehicleOPExecutionResult.Executed; }
                    catch (Exception ex) { Plugin.ModLog.LogError($"{name}: {ex}"); return VehicleOPExecutionResult.Cancelled; }
                }))!),
            Revert = Keep(DelegateSupport.ConvertDelegate<Il2CppSystem.Action<IVehicleOperatorContext>>(
                new Action<IVehicleOperatorContext>(_ => { try { Shapes(false); } catch (Exception ex) { Plugin.ModLog.LogError($"{name} undo: {ex}"); } }))!),
        };
        var flags = Sprocket.VehicleDesigner.Operations.VehicleOp.InternalOpToVehicleOpFlags(global::Operations.Operation.DefaultFlags)
                    | VehicleOperationFlags.Undo | VehicleOperationFlags.Redo | VehicleOperationFlags.Register;
        ops.CreateOp(name, ref callbacks, flags, group, VehicleOpExecutionMode.Synchronous);
        if (!executed) return "the shapes didn't go in";
        if (allCopies.Any(c => sources.Any(s => StructureOf(s).Mesh.Pointer == StructureOf(c).Mesh.Pointer))) return "a copy still shares the part's shape";

        // The check: saved now, the design must match the plan: every part's face count, and each copy placed, flipped,
        // mirrored and linked as planned.
        string check = Snapshot();
        var want = Conversion.Objects(Conversion.Parse(planned));
        var got = Conversion.Objects(Conversion.Parse(check));
        var wantFaces = AddonEdits.FaceCounts(planned);
        var gotFaces = AddonEdits.FaceCounts(check);
        var added = groups.SelectMany(g => g.Select(p => p.Added)).ToHashSet();
        var copyIds = copies.Select(g => g.Select(c => (int)c.VUID).ToList()).ToList();
        var problems = new List<string>();
        if (got.Count != want.Count) problems.Add($"{got.Count} parts, planned {want.Count}");
        foreach (var (v, n) in wantFaces.Where(kv => want.ContainsKey(kv.Key) && !added.Contains(kv.Key)))
            if (gotFaces.GetValueOrDefault(v) != n) problems.Add($"part {v} has {gotFaces.GetValueOrDefault(v)} faces, planned {n}");
        for (int i = 0; i < groups.Count; i++)
        {
            var pieces = groups[i];
            for (int j = 0; j < pieces.Count; j++)
            {
                int c = copyIds[i][j];
                var (w, g) = (want[pieces[j].Added], got.GetValueOrDefault(c));
                if (g == null) { problems.Add($"copy {c} not saved"); continue; }
                if (gotFaces.GetValueOrDefault(c) != wantFaces[pieces[j].Added]) problems.Add($"copy {c} has {gotFaces.GetValueOrDefault(c)} faces, planned {wantFaces[pieces[j].Added]}");
                if (g["pvuid"]!.GetValue<int>() != w["pvuid"]!.GetValue<int>()) problems.Add($"copy {c} hangs on {g["pvuid"]}, planned {w["pvuid"]}");
                if ((g["flags"]!.GetValue<int>() & 5) != (w["flags"]!.GetValue<int>() & 5)) problems.Add($"copy {c} flags {g["flags"]}, planned {w["flags"]}");
                if (!Conversion.Near(Conversion.Local(g["transform"]!), Conversion.Local(w["transform"]!))) problems.Add($"copy {c} placed elsewhere");
                if (pieces.Count == 2 && g["transform"]!["mirrorVuid"]!.GetValue<int>() != copyIds[i][1 - j]) problems.Add($"copy {c} not linked to its twin");
            }
            // A copy the game should show twice (mirrored mark, no twin): the structure draws its own image (its mirrored
            // meshes, across the centre), which it does when it counts itself mirrored.
            if (pieces.Count == 1 && (want[pieces[0].Added]["flags"]!.GetValue<int>() & 4) != 0 && !StructureOf(copies[i][0]).Mirrored)
                problems.Add($"copy {copyIds[i][0]} isn't shown on the other side");
        }
        if (problems.Count > 0) return "the check found: " + string.Join("; ", problems);
        // The new add-on selected, in the same step: the part's shape editor closes (its own Ctrl+Z covers shape edits
        // only), so Ctrl+Z undoes the separate straight away.
        try { ops.Select(StructureOf(copies[0][0]), Sprocket.Vehicles.Selection.SelectionMode.Set, group); }
        catch (Exception ex) { Plugin.ModLog.LogWarning($"TOOL_LIVE {name}: couldn't select the new add-on: {ex.Message}"); }
        Plugin.ModLog.LogInfo($"TOOL_LIVE {name}: in place, new add-ons {string.Join(", ", copyIds.Select(g => string.Join(" and ", g)))}, checked against the plan");
        Say(done + "可用 Ctrl+Z 撤销。", 8);
        LastEditedPart = -1;
        return null;
    }

    private readonly HashSet<int> filledRings = new();
    private float nextTurretCheck;

    /// A turret placed or copied with Mirror on gets a twin ring from the game with nothing on it (the game copies only
    /// the ring): once the placing is done, the body, guns and everything else get mirrored onto it. Once per turret.
    private void FillMirroredTurrets()
    {
        if (core?.Editor == null || core.Editor.OperationInProgress) return;
        foreach (var part in AllParts())
        {
            if (part.GUID != Conversion.RingGuid || part.GetComponent<VehicleTransform>() is not { } ring) continue;
            var twin = ring.Mirror;
            if (twin == null) continue; // Unity's own null check: a twin just deleted counts as none
            if (!Each(ring.Children).Any() || Each(twin.Children).Any() || !filledRings.Add((int)part.VUID)) continue;
            int vuid = (int)part.VUID;
            Plugin.ModLog.LogInfo($"Turret mirror: twin ring {(int)(twin.VehicleObject?.VUID ?? default)} of ring {vuid} has nothing on it; mirroring the turret onto it");
            RequestEdit("正在镜像炮塔", "镜像炮塔已获得塔体及其上的全部内容。可用“恢复”撤销。", json =>
            {
                var (result, count, how) = Conversion.MirrorTurret(json, vuid);
                int body = Conversion.Objects(Conversion.Parse(result))[vuid]["structureID"]?.GetValue<int>() ?? vuid;
                return new EditResult(result, body, $"ring {vuid}: {count} parts {how}");
            });
            return;
        }
    }

    /// Ctrl+J, as in Blender: the selected add-ons join the last add-on, turret or hull selected (the active one).
    private void JoinHotkey()
    {
        var keys = UnityEngine.InputSystem.Keyboard.current;
        if (keys == null || !keys.ctrlKey.isPressed || !keys.jKey.wasPressedThisFrame) return;
        var picked = SelectedParts();
        var addons = SelectedParts(Conversion.AddonGuid);
        var bodies = SelectedParts(Conversion.CompartmentGuid);
        int target = picked.LastOrDefault(v => addons.Contains(v) || bodies.Contains(v), -1);
        var others = addons.Where(v => v != target).ToList();
        if (target < 0 || others.Count == 0) { Say("Ctrl+J：先选要合并的附加部件，最后选它们并入的附加部件、炮塔或车体。", 5); return; }
        Plugin.ModLog.LogInfo($"Ctrl+J: joining {string.Join(", ", others)} into {target} (selection order {string.Join(", ", picked)})");
        RequestLiveEdit("正在合并附加部件", $"已把 {others.Count} 个附加部件合并到最后选中的部件。", json => AddonEdits.PlanMerge(json, target, others, LiveShapes(json)));
    }

    internal void RequestRestore()
    {
        if (busy || recoveryJson == null) return;
        busy = true; Say("正在恢复上次编辑之前的设计……", 30);
        queued = Restore;
    }

    /// Parts currently selected in the editor, optionally only those of one part type.
    internal List<int> SelectedParts(string? guid = null)
    {
        var selection = core?.Editor?.SelectionReader;
        var found = new List<int>();
        if (selection == null) return found;
        var items = selection.Items;
        for (int i = 0; i < selection.Count; i++)
        {
            var part = items[i]?.VehicleObject;
            if (part != null && (guid == null || part.GUID == guid)) found.Add((int)part.VUID);
        }
        return found.Distinct().ToList();
    }

    /// Every part of the vehicle being edited.
    internal IEnumerable<VehicleObject> AllParts()
    {
        if (core?.Target == null) yield break;
        foreach (var part in Each(core.Target.Cast<IVehicleGateway>().ObjectReader.Items))
            if (part != null) yield return part;
    }

    /// Every component of every part of the vehicle being edited.
    internal IEnumerable<VehicleComponent> AllComponents()
    {
        if (core?.Target == null) yield break;
        foreach (var part in Each(core.Target.Cast<IVehicleGateway>().ObjectReader.Items))
            if (part != null)
                foreach (var c in Each(part.Components))
                    if (c != null) yield return c;
    }

    private bool liveShapesFailed;

    /// Where the game shows each part's shape (shape coordinates to vehicle space, by the game's own conversion), for the
    /// saved parts and the images of parts it shows twice. Null if the game won't say: edits then use the design's maths.
    internal AddonEdits.LiveShapes? LiveShapes(string savedJson)
    {
        if (liveShapesFailed) return null;
        try
        {
            var saved = Conversion.Objects(Conversion.Parse(savedJson)).Keys.ToHashSet();
            var parts = new Dictionary<int, System.Numerics.Matrix4x4>();
            var byMesh = new Dictionary<IntPtr, List<int>>();
            var unsaved = new List<(IntPtr Mesh, System.Numerics.Matrix4x4 At)>();
            foreach (var obj in AllParts())
            {
                var s = Each(obj.Components).Select(c => c?.TryCast<PlateStructure>()).FirstOrDefault(x => x?.Mesh != null);
                var t = obj.GetComponent<VehicleTransform>();
                if (s == null || t == null) continue;
                Vector3 At(float x, float y, float z) => t.WorldToVehicleSpace(s.StructureToWorldSpace(new Vector3(x, y, z)));
                Vector3 o = At(0, 0, 0), ex = At(1, 0, 0) - o, ey = At(0, 1, 0) - o, ez = At(0, 0, 1) - o;
                var m = new System.Numerics.Matrix4x4(ex.x, ex.y, ex.z, 0, ey.x, ey.y, ey.z, 0, ez.x, ez.y, ez.z, 0, o.x, o.y, o.z, 1);
                int v = (int)obj.VUID;
                if (!saved.Contains(v)) { unsaved.Add((s.Mesh.Pointer, m)); continue; }
                parts[v] = m;
                (byMesh.TryGetValue(s.Mesh.Pointer, out var list) ? list : byMesh[s.Mesh.Pointer] = new()).Add(v);
            }
            // An image is a part of the game's own, not saved, sharing the shape of the one saved part it mirrors.
            var images = new Dictionary<int, System.Numerics.Matrix4x4>();
            foreach (var (mesh, at) in unsaved)
                if (byMesh.TryGetValue(mesh, out var owners) && owners.Count == 1) images[owners[0]] = at;
            return new AddonEdits.LiveShapes(parts, images);
        }
        catch (Exception ex)
        {
            liveShapesFailed = true;
            Plugin.ModLog.LogWarning($"Live part placement unavailable, using the design's maths: {ex.Message}");
            return null;
        }
    }

    /// Turret rings of every selected turret, whether its ring or its body was clicked.
    internal List<int> SelectedTurretRings()
    {
        var rings = new List<int>();
        var selection = core?.Editor?.SelectionReader;
        if (selection == null) return rings;
        var items = selection.Items;
        for (int i = 0; i < selection.Count; i++)
        {
            var component = items[i];
            var part = component?.VehicleObject;
            if (part == null) continue;
            if (part.GUID == Conversion.RingGuid) rings.Add((int)part.VUID);
            else if (component!.VehicleTransform?.Parent?.GetComponent<VehicleObject>() is { } parent && parent.GUID == Conversion.RingGuid)
                rings.Add((int)parent.VUID);
        }
        return rings.Distinct().ToList();
    }

    public void Update()
    {
        // The per-frame features each on their own: one that fails (every frame) is logged once and stops no other.
        Ui.Guard("Editor lookup", () =>
        {
            if (Time.unscaledTime >= nextLookup)
            {
                nextLookup = Time.unscaledTime + 0.5f;
                bool wasInEditor = core != null;
                core = UnityEngine.Object.FindObjectOfType<VehicleDesignerCore>();
                if (wasInEditor && core == null) { MeshTools.LeftEditor(); ExplodedView.LeftEditor(); GearSpeeds.LeftEditor(); } // editor-only views end with it
            }
            ready = core != null && core.HasEditor && core.editorState == VehicleDesignerCore.EditorState.Running;
        });
        if (ready) Ui.Guard("Panel fit", PanelFit.Tick);
        if (ready && !busy) Ui.Guard("Ctrl+J", JoinHotkey);
        if (ready && !busy && Time.unscaledTime >= nextTurretCheck) { nextTurretCheck = Time.unscaledTime + 0.5f; Ui.Guard("Turret mirror", FillMirroredTurrets); }
        if (ready)
        {
            Ui.Guard("Mesh tools keys", MeshTools.Keys);
            Ui.Guard("Exploded view", ExplodedView.Keys);
            Ui.Guard("Own paint", PartPaint.Tick);
            Ui.Guard("Hotkeys", Hotkeys.Keys);
            Ui.Guard(GearSpeeds.LogTag, GearSpeeds.Update);
        }
        Ui.Guard("Photo", PhotoShot.Update);
        if (ready) Ui.Guard("Drawing sheet", DrawingSheet.Update);
        if (ready) Ui.Guard("Photo card", Card.Keys);
        // Design edits: a failure ends the edit (so the editor isn't left busy) and says why.
        try
        {
            if (pending != null && pending.IsCompleted)
            {
                var task = pending; pending = null; busy = false;
                if (task.IsFaulted || task.IsCanceled)
                {
                    Say("游戏无法加载修改结果。您修改前的设计已备份；请使用“恢复”。", 10);
                    Plugin.ModLog.LogError(task.Exception?.ToString() ?? "Vehicle load cancelled");
                }
                else
                {
                    Say(restoring ? "已恢复设计。" : doneMessage, 8);
                    Plugin.ModLog.LogInfo(restoring ? "TOOL_RESTORE_OK" : $"TOOL_EDIT_OK: {editName}");
                    if (core?.Target != null && recoveryDir != null)
                        File.WriteAllText(Path.Combine(recoveryDir, "loaded-result.blueprint"), Snapshot());
                    if (restoring) LastEditedPart = -1;
                }
            }
            if (queued != null)
            {
                var action = queued; queued = null;
                action();
            }
        }
        catch (Exception ex)
        {
            busy = false; waits = 0;
            Say("未完成：" + ex.Message, 10);
            Plugin.ModLog.LogError(ex);
        }
    }

    internal string Snapshot()
    {
        if (core == null || !core.HasEditor || core.Target == null) throw new Exception("请先在编辑器中打开一个载具。");
        serializer ??= new VehicleBlueprintSerializer();
        return serializer.SerializeToJSON(serializer.ToBlueprint(core.Target.Cast<IVehicleGateway>()), true);
    }

    /// The click also reaches the game's editor; if that started an operation, wait (~2 s at 60 fps) for it to finish.
    private bool EditorIdle(System.Action retry)
    {
        if (!ready || core == null) throw new Exception("请先在编辑器中打开一个载具。");
        if (!core.DesignIOPossible || (core.Editor != null && core.Editor.OperationInProgress))
        {
            if (waits++ == 0) Plugin.ModLog.LogInfo($"WAIT editor busy: io={core.DesignIOPossible}, op={core.Editor?.OperationInProgress}");
            if (waits > 120) { waits = 0; throw new Exception("编辑器仍处于忙碌状态。请点击空白处取消选择，然后重试。"); }
            queued = retry;
            return false;
        }
        waits = 0;
        return true;
    }

    private void Backup(string original, string edited)
    {
        recoveryDir = Path.Combine(Paths.BepInExRootPath, "SprocketToolsBackups", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + System.Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(recoveryDir);
        File.WriteAllText(Path.Combine(recoveryDir, "original.blueprint"), original);
        File.WriteAllText(Path.Combine(recoveryDir, "edited.blueprint"), edited);
        File.WriteAllText(Path.Combine(recoveryDir, "README.txt"), $"{editName}。original.blueprint 是修改前的完整设计（含未保存的更改），edited.blueprint 是修改后的结果。将其中任意一个复制到阵营的 Blueprints\\Vehicles 文件夹即可载入。没有覆盖任何已保存的蓝图。");
        PruneBackups();
    }

    /// Only the newest backups are kept (setting "Backups kept"; 0 keeps all): each is a whole design or two, and they
    /// add up. Only the mod's own backup folders (named by time and a code) are ever taken away.
    private static void PruneBackups()
    {
        int keep = Plugin.BackupsKept?.Value ?? 50;
        if (keep <= 0) return;
        try
        {
            var root = Path.Combine(Paths.BepInExRootPath, "SprocketToolsBackups");
            var ours = new System.Text.RegularExpressions.Regex(@"^\d{8}-\d{6}-[0-9a-f]{8}$");
            var old = Directory.GetDirectories(root).Where(d => ours.IsMatch(Path.GetFileName(d))).OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal).Skip(keep).ToList();
            foreach (var d in old) Directory.Delete(d, recursive: true);
            if (old.Count > 0) Plugin.ModLog.LogInfo($"Backups: kept the newest {keep}, removed {old.Count} older");
        }
        catch (Exception ex) { Plugin.ModLog.LogWarning($"Backups: couldn't tidy the old ones: {ex.Message}"); }
    }

    private void RunEdit()
    {
        if (!EditorIdle(RunEdit)) return;
        string original = Snapshot();
        var result = edit!(original);
        var nativeBlueprint = serializer!.DeserializeJSON(result.Json);
        Backup(original, result.Json);
        recoveryJson = original;
        LastEditedPart = result.FocusPart;
        restoring = false;
        Plugin.ModLog.LogInfo($"TOOL_EDIT {editName}: {result.Log}; backup={recoveryDir}");
        pending = core!.Load(nativeBlueprint, Il2CppSystem.Threading.CancellationToken.None);
    }

    private void RunLiveEdit()
    {
        if (!EditorIdle(RunLiveEdit)) return;
        string original = Snapshot();
        var plan = liveEdit!(original);
        Backup(original, plan.DesignJson);
        Plugin.ModLog.LogInfo($"TOOL_EDIT {editName}: {plan.Summary}; in place={plan.Live}; backup={recoveryDir}");
        if (plan.Live)
        {
            try { ApplyInPlace(plan, original); busy = false; return; }
            catch (Exception ex) { Plugin.ModLog.LogWarning($"TOOL_LIVE couldn't apply in place, reloading the design instead: {ex}"); }
        }
        recoveryJson = original;
        LastEditedPart = plan.Focus;
        restoring = false;
        pending = core!.Load(serializer!.DeserializeJSON(plan.DesignJson), Il2CppSystem.Threading.CancellationToken.None);
    }

    sealed record MeshSwap(PlateStructure Structure, PlateStructureMesh Mesh, PlateStructureMeshBlueprint Old, PlateStructureMeshBlueprint New);

    /// Swaps the parts' meshes as one of the game's own undoable operations, grouped with moving attached parts and
    /// removing parts, so Ctrl+Z and Ctrl+Y work on it like on any other edit. The meshes come from the game reading
    /// the design before and after the edit (the same reader a reload uses). Everything is checked before anything changes.
    /// A change as one of the game's own undoable steps: `apply` runs now (and again on Ctrl+Y), `undo` on Ctrl+Z.
    /// False (nothing done) when there's no editor to hold the step.
    internal bool Undoable(string name, System.Action apply, System.Action undo)
    {
        var ops = core?.Editor?.operations;
        if (ops == null) return false;
        bool done = false;
        var callbacks = new VehicleOperationCallbacks
        {
            Execute = Keep(DelegateSupport.ConvertDelegate<Il2CppSystem.Func<IVehicleOperatorContext, VehicleOPExecutionResult>>(
                new Func<IVehicleOperatorContext, VehicleOPExecutionResult>(_ =>
                {
                    try { apply(); done = true; return VehicleOPExecutionResult.Executed; }
                    catch (Exception ex) { Plugin.ModLog.LogError($"{name}: {ex}"); return VehicleOPExecutionResult.Cancelled; }
                }))!),
            Revert = Keep(DelegateSupport.ConvertDelegate<Il2CppSystem.Action<IVehicleOperatorContext>>(
                new Action<IVehicleOperatorContext>(_ =>
                {
                    try { undo(); } catch (Exception ex) { Plugin.ModLog.LogError($"{name} undo: {ex}"); }
                }))!),
        };
        var flags = Sprocket.VehicleDesigner.Operations.VehicleOp.InternalOpToVehicleOpFlags(global::Operations.Operation.DefaultFlags)
                    | VehicleOperationFlags.Undo | VehicleOperationFlags.Redo | VehicleOperationFlags.Register;
        ops.CreateOp(name, ref callbacks, flags, ops.GetNewGroupID(), VehicleOpExecutionMode.Synchronous);
        return done;
    }

    private void ApplyInPlace(AddonEdits.EditPlan plan, string original)
    {
        var byId = new Dictionary<int, VehicleObject>();
        foreach (var o in Each(core!.Target.Cast<IVehicleGateway>().ObjectReader.Items)) if (o != null) byId[(int)o.VUID] = o;
        var before = Meshes(original);
        var after = Meshes(plan.DesignJson);
        var changes = new List<MeshSwap>();
        foreach (var (vuid, meshId) in plan.MeshIds)
        {
            if (!byId.TryGetValue(vuid, out var obj)) throw new Exception($"part {vuid} isn't in the vehicle");
            var structure = Each(obj.Components).Select(c => c?.TryCast<PlateStructure>()).FirstOrDefault(s => s != null) ?? throw new Exception($"part {vuid} has no plate structure");
            var mesh = structure.Mesh ?? throw new Exception($"part {vuid} has no mesh");
            if (plan.OldFaces.TryGetValue(vuid, out int expected) && mesh.FaceCount != expected)
                throw new Exception($"part {vuid}'s live mesh has {mesh.FaceCount} faces, the saved design {expected}");
            // The part's shape before: its own mesh number in the original (a shared palette mesh gets a new number when edited).
            int oldId = AddonEdits.MeshIdOf(original, vuid);
            changes.Add(new MeshSwap(structure, mesh,
                before.TryGetValue(oldId, out var old) ? old : throw new Exception($"mesh {oldId} missing before the edit"),
                after.TryGetValue(meshId, out var @new) ? @new : throw new Exception($"mesh {meshId} missing after the edit")));
        }
        VehicleObject Part(int v) => byId.TryGetValue(v, out var o) ? o : throw new Exception($"part {v} isn't in the vehicle");
        CheckOnlyTargetsChange(changes, plan, original, byId);
        var remove = plan.Remove.Select(v => Part(v).GetReference()).ToArray();
        var moves = plan.Reparent.GroupBy(r => r.Parent).Select(g => (Parent: Part(g.Key), Children: g.Select(r => Part(r.Child)).ToList())).ToList();

        var ops = core.Editor.operations;
        int group = ops.GetNewGroupID();
        string name = editName, done = doneMessage;
        bool executed = false;
        var callbacks = new VehicleOperationCallbacks
        {
            Execute = Keep(DelegateSupport.ConvertDelegate<Il2CppSystem.Func<IVehicleOperatorContext, VehicleOPExecutionResult>>(
                new Func<IVehicleOperatorContext, VehicleOPExecutionResult>(_ =>
                {
                    executed = Swap(changes, true, name);
                    if (executed) Say(done + "可用 Ctrl+Z 撤销。", 8);
                    return executed ? VehicleOPExecutionResult.Executed : VehicleOPExecutionResult.Cancelled;
                }))!),
            Revert = Keep(DelegateSupport.ConvertDelegate<Il2CppSystem.Action<IVehicleOperatorContext>>(
                new Action<IVehicleOperatorContext>(_ => Swap(changes, false, name)))!),
        };
        // The game's usual flags plus undo/redo: with Register alone the step is kept in the history but Ctrl+Z skips it.
        var flags = Sprocket.VehicleDesigner.Operations.VehicleOp.InternalOpToVehicleOpFlags(global::Operations.Operation.DefaultFlags)
                    | VehicleOperationFlags.Undo | VehicleOperationFlags.Redo | VehicleOperationFlags.Register;
        var op = ops.CreateOp(name, ref callbacks, flags, group, VehicleOpExecutionMode.Synchronous);
        Plugin.ModLog.LogInfo($"TOOL_LIVE {name}: operation created, ran now={executed}, flags={op?.Flags}");
        if (!executed) throw new Exception("the in-place change didn't go through"); // the caller reloads the design instead
        // Parts on a merged add-on move onto the one it merged into (before that add-on goes, or they'd go with it).
        foreach (var (parent, children) in moves)
        {
            var where = children.Select(c => c.transform.position).ToList();
            ops.SetParent(parent.GetReference(), new Il2CppReferenceArray<ISoftVehicleObject>(children.Select(c => c.GetReference()).ToArray()), group);
            float drift = children.Select((c, i) => (c.transform.position - where[i]).magnitude).DefaultIfEmpty(0).Max();
            Plugin.ModLog.LogInfo($"TOOL_LIVE moved {children.Count} parts onto part {(int)parent.VUID}, largest shift {drift * 1000:0.#} mm");
        }
        if (remove.Length > 0) ops.Destroy(new Il2CppReferenceArray<ISoftVehicleObject>(remove), group);
        LastEditedPart = -1; // Ctrl+Z replaces the Restore button for in-place edits
    }

    /// The plate meshes of a design, by mesh id, as the game reads them from the blueprint.
    private Dictionary<int, PlateStructureMeshBlueprint> Meshes(string designJson)
    {
        var found = new Dictionary<int, PlateStructureMeshBlueprint>();
        var blueprint = serializer!.DeserializeJSON(designJson).TryCast<Sprocket.Vehicles.Serialization.VehicleBlueprint>() ?? throw new Exception("the design didn't read back");
        foreach (var m in blueprint.Meshes)
            if (m?.Mesh?.TryCast<PlateStructureMeshBlueprint>() is { } mesh) found[m.MeshID] = mesh;
        return found;
    }

    /// A changed part's live shape may only be shared with other changed parts (its mirror twin): otherwise swapping it
    /// would change that part too. Checked on the live objects, then by a trial swap and a save to memory, where only
    /// the changed parts may differ.
    private void CheckOnlyTargetsChange(List<MeshSwap> changes, AddonEdits.EditPlan plan, string original, Dictionary<int, VehicleObject> byId)
    {
        var owners = new Dictionary<IntPtr, List<int>>();
        foreach (var (vuid, obj) in byId)
            foreach (var s in Each(obj.Components).Select(c => c?.TryCast<PlateStructure>()).Where(s => s?.Mesh != null))
                (owners.TryGetValue(s!.Mesh.Pointer, out var list) ? list : owners[s.Mesh.Pointer] = new()).Add(vuid);
        // A part the game shows twice has its image as a part of its own, not saved in the design: that one follows.
        var saved = Conversion.Objects(Conversion.Parse(original)).Keys.ToHashSet();
        var sharers = changes.SelectMany(c => owners[c.Mesh.Pointer]).Where(v => !plan.MeshIds.ContainsKey(v)).Distinct().ToList();
        if (sharers.Any(saved.Contains)) throw new Exception("a changed part shares its live shape with another part");
        if (sharers.Count > 0) Plugin.ModLog.LogInfo($"TOOL_LIVE the game's mirror images {string.Join(", ", sharers)} share the changed shape and follow it");

        Swap(changes, true, "trial of " + editName);
        string trial;
        try { trial = Snapshot(); }
        finally { Swap(changes, false, "trial of " + editName); }
        var was = AddonEdits.FaceCounts(original);
        var now = AddonEdits.FaceCounts(trial);
        var want = AddonEdits.FaceCounts(plan.DesignJson);
        foreach (var (vuid, faces) in was)
        {
            bool changed = plan.MeshIds.ContainsKey(vuid);
            if (!now.TryGetValue(vuid, out int n) || n != (changed ? want[vuid] : faces))
                throw new Exception($"trial: part {vuid} saved {(now.ContainsKey(vuid) ? n : 0)} faces, expected {(changed ? want[vuid] : faces)}");
        }
    }

    static bool Swap(List<MeshSwap> changes, bool forward, string name)
    {
        try
        {
            foreach (var c in changes) { c.Mesh.LoadBlueprint(forward ? c.New : c.Old); c.Structure.RequestMeshRefresh(); }
            Plugin.ModLog.LogInfo($"TOOL_LIVE {(forward ? "applied" : "undone")}: {name} ({string.Join(", ", changes.Select(c => c.Mesh.FaceCount + " faces"))})");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.ModLog.LogError($"TOOL_LIVE {(forward ? "apply" : "undo")} failed, putting the meshes back: {ex}");
            foreach (var c in changes) try { c.Mesh.LoadBlueprint(forward ? c.Old : c.New); c.Structure.RequestMeshRefresh(); } catch { }
            return false;
        }
    }

    static T Keep<T>(T value) where T : class { alive.Add(value); return value; }

    internal static IEnumerable<T> Each<T>(Il2CppSystem.Collections.Generic.IReadOnlyList<T> list)
    {
        int count = list.Cast<Il2CppSystem.Collections.Generic.IReadOnlyCollection<T>>().Count;
        for (int i = 0; i < count; i++) yield return list[i];
    }

    private void Restore()
    {
        if (recoveryJson == null || core == null || !core.DesignIOPossible) throw new Exception("没有可恢复的记录，或编辑器正忙。");
        // Keep any work done after the edit before replacing it.
        File.WriteAllText(Path.Combine(recoveryDir!, "before-restore-" + DateTime.Now.ToString("HHmmssfff") + ".blueprint"), Snapshot());
        restoring = true;
        pending = core.Load(serializer!.DeserializeJSON(recoveryJson), Il2CppSystem.Threading.CancellationToken.None);
    }

    // Result message and the hotkeys box; the controls live in the game's inspector.
    public void OnGUI()
    {
        if (PhotoShot.Capturing) return; // nothing of ours in the photo
        Ui.Guard("IMGUI font", Ui.CjkFont); // before any of the three below caches a GUIStyle of its own
        Ui.Guard("Hotkeys", Hotkeys.DrawBox);
        Ui.Guard("Measurements", MeshTools.DrawMeasures);
        if (Time.unscaledTime > statusUntil || string.IsNullOrEmpty(status)) return;
        float width = Math.Min(620, Screen.width - 40);
        GUI.Box(new Rect((Screen.width - width) / 2, 80, width, 30), status); // below the game's vehicle name bar
    }
}

/// "Restore design before last edit" in the panel of the part the last QoL edit produced.
[HarmonyPatch]
public static class RestoreSection
{
    [HarmonyPostfix, HarmonyPatch(typeof(PlateStructureEditor), nameof(PlateStructureEditor.OnGUI))]
    static void Draw(PlateStructureEditor __instance, IGUILayout layout) => Ui.Guard("Restore", () =>
    {
        var editor = DesignEditor.Instance;
        var ui = layout.TryCast<IGUIElementDrawer>();
        if (editor == null || ui == null || !editor.CanRestore || editor.LastEditedPart != (int)__instance.Component.VehicleObject.VUID) return;
        Ui.Section(layout, "撤销上一次修改");
        var tip = new UITooltip("恢复", "重新载入上一次 SprocketTools 修改（炮塔转换、圆形附加部件或合并）之前的设计。");
        ui.Button("恢复到上次修改前的设计", Ui.Callback(editor.RequestRestore), ref tip);
    });
}
