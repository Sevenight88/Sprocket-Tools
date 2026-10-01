using HarmonyLib;
using Sprocket.PlateMesh;
using Sprocket.PlateMesh.Operations;
using Sprocket.Transformations;
using UnityEngine;

namespace SprocketTools;

/// Rotation snap angle: with a step set (Mesh tools, or the config file), turns snap to it instead of the game's own
/// step: 7.5° makes a 48-sided circle, 5° a 72-sided one. Only while the game's rotation snap is on (its key still
/// switches snapping off). The game rounds each turn to the transform's `snap` in both rotate functions (points in a
/// hand-made structure, and whole parts), and sets `snap` again every frame before them, so it's swapped just before.
[HarmonyPatch]
public static class RotationSnap
{
    static float? gameStep; // the game's own step, as first seen (logged)

    [HarmonyPrefix, HarmonyPatch(typeof(MeshTransformation), nameof(MeshTransformation.ApplyRotation))]
    static void Points(Transformation op) => Ui.Guard("Rotation snap", () => Use(op));

    [HarmonyPrefix, HarmonyPatch(typeof(TransformModes), nameof(TransformModes.ApplyRotate))]
    static void Parts(Transformation op) => Ui.Guard("Rotation snap", () => Use(op));

    static void Use(Transformation op)
    {
        float step = Plugin.RotationSnap?.Value ?? 0;
        if (op == null || op.snap <= 0 || step <= 0) return; // snapping off, or the game's own step wanted
        if (gameStep == null)
        {
            gameStep = op.snap;
            Plugin.ModLog.LogInfo($"Rotation snap: the game's step is {op.snap:0.###}°, turning in steps of {step:0.###}° instead");
        }
        op.snap = step;
    }
}

/// With Mirror on, Merge (M) merges the mirrored points too: the game's merge leaves the other side alone (so the
/// sides stop matching and Mirror quietly stops pairing them). After the game merges the selected points, their
/// mirror images are selected and the same merge runs again, inside the same edit (Ctrl+Z undoes both); the merged
/// point is then put exactly opposite the first, and the selection put back as the game left it.
[HarmonyPatch]
public static class MirrorMerge
{
    static bool again; // inside the second, mirrored merge
    static (List<Vertex> Images, List<Vertex> Chosen)? pending;

    [HarmonyPrefix, HarmonyPatch(typeof(MergeOp), "ExecuteInternal")]
    static void Before(EditMesh mesh) => Ui.Guard("Mirror merge", () =>
    {
        pending = null;
        if (again || !(Plugin.MirrorMerge?.Value ?? true) || Hotkeys.Current is not { } editor || !editor.meshEditor.Symmetry) return;
        if (editor.meshEditor.Mesh?.EditMesh?.Pointer != mesh.Pointer) return; // another mesh than the one being edited
        var v = new MeshTools.View(mesh);
        var chosen = v.SelectedPoints.ToList();
        if (chosen.Count < 2) return;
        var twins = MeshPlans.Twins(v.Pos, chosen, MeshTools.Tolerance);
        if (chosen.Any(p => !twins.ContainsKey(p)))
        {
            Plugin.ModLog.LogInfo($"Mirror merge: {chosen.Count(p => !twins.ContainsKey(p))} of the points have no mirror image, so only this side merges (Fix mirror first)");
            return;
        }
        var images = chosen.Select(p => twins[p]).Distinct().ToList();
        if (images.Any(chosen.Contains)) return; // the selection is its own mirror image (points across the middle)
        pending = (images.Select(i => v.Verts[i]).ToList(), chosen.Select(i => v.Verts[i]).ToList());
    });

    [HarmonyPostfix, HarmonyPatch(typeof(MergeOp), "ExecuteInternal")]
    static void After(MergeOp __instance, EditMesh mesh) => Ui.Guard("Mirror merge", () =>
    {
        if (again || pending is not { } p) return;
        pending = null;
        var alive = Alive(mesh);
        var kept = p.Chosen.Where(x => alive.Contains(x.Pointer)).ToList();
        if (kept.Count != 1 || p.Images.Any(x => !alive.Contains(x.Pointer)))
        {
            Plugin.ModLog.LogInfo($"Mirror merge: the game's merge left {kept.Count} of the points; the other side is left alone");
            return;
        }
        var at = kept[0].position;
        var selected = Selection.Save(mesh);
        Selection.Only(mesh, p.Images);
        again = true;
        try { AccessTools.Method(typeof(MergeOp), "ExecuteInternal").Invoke(__instance, new object[] { mesh }); }
        finally { again = false; }
        alive = Alive(mesh);
        var image = p.Images.Where(x => alive.Contains(x.Pointer)).ToList();
        // Exactly opposite, so Mirror pairs the two merged points.
        if (image.Count == 1) image[0].position = new Vector3(-at.x, at.y, at.z);
        selected.Restore(mesh);
        mesh.MarkDirty(MeshDirtyFlags.All);
        Plugin.ModLog.LogInfo(image.Count == 1 ? $"Mirror merge: the {p.Images.Count} mirrored points merged too, at ({-at.x:0.####}, {at.y:0.####}, {at.z:0.####})"
                                               : $"Mirror merge: the game's merge of the mirrored points left {image.Count} of them");
    });

    static HashSet<IntPtr> Alive(EditMesh mesh)
    {
        var alive = new HashSet<IntPtr>();
        var vertices = mesh.vertices;
        for (int i = 0; i < vertices.Count; i++) alive.Add(vertices[i].Pointer);
        return alive;
    }

    /// The mesh's selection: each element's Selected flag, the ordered list and the counts.
    sealed class Selection
    {
        readonly List<Element> flagged = new(), listed = new();
        ushort points, edges, faces, loops;

        public static Selection Save(EditMesh mesh)
        {
            var s = new Selection { points = mesh.selectedVertexCount, edges = mesh.selectedEdgeCount, faces = mesh.selectedFaceCount, loops = mesh.selectedLoopCount };
            foreach (var e in All(mesh)) if (e.HasFlag(ElementFlags.Selected)) s.flagged.Add(e);
            var list = mesh.selection;
            for (int i = 0; i < list.Count; i++) s.listed.Add(list[i]);
            return s;
        }

        /// Nothing selected but these points.
        public static void Only(EditMesh mesh, List<Vertex> points)
        {
            foreach (var e in All(mesh)) e.DisableFlag(ElementFlags.Selected);
            mesh.selection.Clear();
            foreach (var v in points) { v.EnableFlag(ElementFlags.Selected); mesh.selection.Add(v); }
            mesh.selectedVertexCount = (ushort)points.Count;
            mesh.selectedEdgeCount = mesh.selectedFaceCount = mesh.selectedLoopCount = 0;
        }

        public void Restore(EditMesh mesh)
        {
            foreach (var e in All(mesh)) e.DisableFlag(ElementFlags.Selected);
            var alive = All(mesh).Select(e => e.Pointer).ToHashSet();
            foreach (var e in flagged) if (alive.Contains(e.Pointer)) e.EnableFlag(ElementFlags.Selected);
            mesh.selection.Clear();
            foreach (var e in listed) if (alive.Contains(e.Pointer)) mesh.selection.Add(e);
            mesh.selectedVertexCount = points;
            mesh.selectedEdgeCount = edges;
            mesh.selectedFaceCount = faces;
            mesh.selectedLoopCount = loops;
        }

        static IEnumerable<Element> All(EditMesh mesh)
        {
            var vertices = mesh.vertices;
            for (int i = 0; i < vertices.Count; i++) yield return vertices[i];
            var edges = mesh.edges;
            for (int i = 0; i < edges.Count; i++) yield return edges[i];
            var faces = mesh.faces;
            for (int i = 0; i < faces.Count; i++) yield return faces[i];
        }
    }
}
