using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Sprocket.MeshEditing;
using Sprocket.PlateMesh;
using Sprocket.UI;
using Sprocket.Vehicles.PlateStructures.Design;
using Num = System.Numerics.Vector3;

namespace SprocketTools;

/// Structure panel (hand-made shapes): "圆孔质量" sets how many segments and how big the game's own
/// Create Hole tool makes a hole, and every hole's ring is made a true circle inside the face, running the same
/// way round as the face so the filled-in faces aren't inside out. It stays the game's operation, so undo works as normal.
/// A hole that cannot be done safely is now cancelled whole (the game's own undo snapshot is put back) instead of
/// leaving half a cut behind.
[HarmonyPatch]
public static class HoleQuality
{
    static int segments = 32, sizePercent = 100;

    static ushort? holeThickness; // the holed face's plate thickness, read before the game takes the face away
    static Num? originalSide;     // which way the face being holed faced, remembered before the game deletes it
    static PlateStructureEditor? lastEditor; // where to tell the player a hole was refused

    /// One Create Hole going on: the mesh as it was, the edit before it (the game cuts the mirrored twin second),
    /// and why this one was refused, if it was.
    sealed class HoleEdit
    {
        internal readonly EditMesh Original;
        internal readonly HoleEdit? Previous;
        internal string? Failure;
        internal HoleEdit(MeshTopologyEditOp topology, HoleEdit? previous)
        { Original = topology.mesh; Previous = previous; }
    }
    static HoleEdit? activeEdit;

    /// The game takes the holed face away before it asks anything to fill around it. Keeping its undo snapshot means a
    /// refused ring or a refused fill cannot leave half a cut, including when the second, mirrored cut is the one that
    /// fails. Undo/redo still see the snapshot the game made.
    [HarmonyPrefix, HarmonyPatch(typeof(MeshTopologyEditOp), nameof(MeshTopologyEditOp.Execute))]
    static void BeginHole(MeshTopologyEditOp __instance, out HoleEdit? __state)
    {
        __state = null;
        if (__instance.meshOp?.TryCast<CreateHoleOp>() == null) return; // any other edit is not ours to guard
        activeEdit = __state = new HoleEdit(__instance, activeEdit);
    }

    [HarmonyPostfix, HarmonyPatch(typeof(MeshTopologyEditOp), nameof(MeshTopologyEditOp.Execute))]
    static void EndHole(MeshTopologyEditOp __instance, HoleEdit? __state)
    {
        if (__state == null) return;
        try
        {
            if (__state.Failure == null && __instance.final is { } final)
            {
                MeshTools.RepairThickening(final);
                __state.Failure = MeshTools.CloneProblem(final);
            }
        }
        catch (Exception ex) { __state.Failure = ex.Message; }
        finally
        {
            activeEdit = __state.Previous;
            holeThickness = null;
            holeRivets = null;
            faceSide = null;
            originalSide = null;
        }
        if (__state.Failure == null) return;
        __instance.final = __state.Original;
        __instance.Revert(__instance.context);
        __state.Original.MarkDirty(MeshDirtyFlags.All);
        Plugin.ModLog.LogWarning("Create Hole cancelled; original shape restored: " + __state.Failure);
        Ui.Guard("Create Hole", () => lastEditor?.operations.NotifyError("创建孔：" + __state.Failure + "，形状未改动"));
    }

    /// Why this hole is off, said once: the first reason is the one the player gets.
    static void Reject(string reason)
    {
        if (activeEdit != null) activeEdit.Failure ??= reason;
        Plugin.ModLog.LogWarning("Create Hole rejected: " + reason);
        if (activeEdit == null)
            Ui.Guard("Create Hole", () => lastEditor?.operations.NotifyError("创建孔：" + reason + "，形状未改动"));
    }

    /// Runs before the game cuts anything: the ring is checked as a circle first, so a hole that cannot be round and
    /// inside its face never reaches the game's delete, circle or fill.
    [HarmonyPrefix, HarmonyPatch(typeof(CreateHoleOp), nameof(CreateHoleOp.CreateHole))]
    static bool UseQuality(ref int resolution, EditMesh mesh, Il2CppReferenceArray<Vertex> vertices, ref Il2CppReferenceArray<Vertex> __result)
    {
        Plugin.ModLog.LogInfo($"Create Hole: game asked for {resolution} segments, using {segments} at {sizePercent}% size");
        resolution = segments;
        holeThickness = null;
        holeRivets = null;
        faceSide = null;
        originalSide = null;
        try
        {
            if (activeEdit?.Failure != null) { __result = new Il2CppReferenceArray<Vertex>(0); return false; } // the twin already failed
            if (vertices == null || vertices.Length < 3 || vertices.Any(v => v == null) || vertices.Select(v => v.Pointer).Distinct().Count() != vertices.Length)
                throw new InvalidOperationException("请选同一个面的三个或以上角点，不要重复选");
            if (vertices.Any(v => !Finite(ToNum(v.position)))) throw new InvalidOperationException("面的角点坐标读不出来");
            var normal = UnityEngine.Vector3.zero;
            var centre = UnityEngine.Vector3.zero;
            var sorted = ElementSelection.SortVerticesOnRadialPlane(vertices.Cast<Il2CppSystem.Collections.Generic.IReadOnlyList<Vertex>>(), out normal, out centre);
            var face = Face.GetExistingFace(sorted, sorted.Length);
            // Cutting points spread over several faces would lay a second plate over the ones already there: the game's
            // own hole only ever deletes the one face whose corners are exactly these points.
            if (face == null) throw new InvalidOperationException("请选完整的一个面，不要跨几个面选点");
            var corners = sorted.Select(v => ToNum(v.position)).ToArray();
            var c = ToNum(centre);
            float radius = Enumerable.Range(0, corners.Length).Min(i => DistanceToSegment(c, corners[i], corners[(i + 1) % corners.Length])) * 0.9f;
            var side = HoleRing.Normal(corners);
            if (!Finite(c) || !Finite(side) || side.LengthSquared() < 1e-12f) throw new InvalidOperationException("这个面太小，放不下孔");
            side = Num.Normalize(side);
            // A trial ring at the size being asked for: if even this cannot be a circle inside the face, say so now
            // while the face is still there, rather than after the game has cut it.
            var u = Num.Normalize(new[] { Num.UnitY, Num.UnitZ }.Select(a => a - Num.Dot(a, side) * side).First(a => a.LengthSquared() > 0.1f));
            var v = Num.Cross(side, u);
            var trial = Enumerable.Range(0, segments).Select(k => c + radius * ((float)Math.Cos(k * Math.Tau / segments) * u + (float)Math.Sin(k * Math.Tau / segments) * v)).ToArray();
            if (!HoleRing.TryFit(corners, trial, c, out _, out string reason, sizePercent / 100f))
                throw new InvalidOperationException(reason);
            originalSide = HoleRing.Normal(Corners(face));
            holeThickness = face?.firstLoop?.thickness;
            // The game drops the holed face's rivets with it: noted here, put back on the faces round the hole after.
            if (face != null) holeRivets = new MeshTools.RivetKeeper(mesh, new[] { face });
            return true;
        }
        catch (Exception ex)
        {
            // Exceptions crossing an IL2CPP Harmony trampoline are swallowed, so the refusal is recorded by hand and
            // the game's own delete / circle / fill are skipped.
            Reject(ex.Message);
            __result = new Il2CppReferenceArray<Vertex>(0);
            return false;
        }
    }

    static bool Finite(Num p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);

    static float DistanceToSegment(Num p, Num a, Num b)
    {
        var ab = b - a;
        float t = ab.LengthSquared() < 1e-12f ? 0 : Math.Clamp(Num.Dot(p - a, ab) / ab.LengthSquared(), 0, 1);
        return Num.Distance(p, a + t * ab);
    }

    static MeshTools.RivetKeeper? holeRivets;

    /// The holed face's rivets onto the faces now round the hole (those in the hole itself go).
    [HarmonyPostfix, HarmonyPatch(typeof(CreateHoleOp), nameof(CreateHoleOp.FillEdgeLoop))]
    static void KeepRivets(Il2CppReferenceArray<Face> __result)
    {
        try
        {
            var keeper = holeRivets;
            holeRivets = null;
            if (keeper is not { Count: > 0 } || __result == null || activeEdit?.Failure != null) return;
            var (kept, lost) = keeper.Place(__result, reach: 0.002f);
            Plugin.ModLog.LogInfo($"Create Hole: rivets {kept} kept, {lost} in the hole removed");
        }
        catch (Exception ex) { Reject(ex.Message); }
    }

    static Num? faceSide; // which way the face being holed really faces, to check the filled-in faces against
    static int holeFill; // index into FillNames: Fill.Mode's (fewest points first), then the game's own fan
    static readonly string[] FillNames = Fill.ModeNames.Select(n => "孔洞填充：" + n).Append("孔洞填充：游戏自带").ToArray();
    static bool GameFill => holeFill == FillNames.Length - 1;

    /// Runs after the game has made the ring and before it fills the face around it: makes the ring a true circle,
    /// then (clean fill) builds the faces around it itself, a quad ring at the rim stepping out to the face's corners,
    /// instead of the game's fan of long thin triangles.
    [HarmonyPrefix, HarmonyPatch(typeof(CreateHoleOp), nameof(CreateHoleOp.FillEdgeLoop))]
    static bool TrueCircle(EditMesh mesh, Il2CppReferenceArray<Vertex> outer, Il2CppReferenceArray<Vertex> inner, UnityEngine.Vector3 centre, ref Il2CppReferenceArray<Face> __result)
    {
        Il2CppReferenceArray<Face>? mine = null;
        faceSide = null;
        try
        {
            var corners = outer.Select(v => ToNum(v.position)).ToArray();
            if (!HoleRing.TryFit(corners, inner.Select(v => ToNum(v.position)).ToArray(), ToNum(centre), out var ring, out var note, sizePercent / 100f))
                throw new InvalidOperationException(note);
            for (int k = 0; k < inner.Length; k++) inner[k].position = new UnityEngine.Vector3(ring[k].X, ring[k].Y, ring[k].Z);
            // The face we were asked to hole is gone by now: its own recorded side beats any guess from a neighbour.
            int order = originalSide is { } original ? (Num.Dot(HoleRing.Normal(corners), original) > 0 ? 1 : -1) : FaceOrder(outer);
            faceSide = order == 0 ? null : HoleRing.Normal(corners) * order;
            // New faces copy a neighbour's settings and the holed face's thickness.
            Face? proto = GameFill || faceSide == null ? null : FaceOf(outer) ?? Neighbour(outer);
            string fill;
            if (GameFill) fill = "game's fill";
            else if (faceSide == null) fill = "game's fill (can't tell which way the face faces)";
            else if (proto is not { } copy) fill = "game's fill (no face next to it to copy settings from)";
            else
            {
                mine = CleanFill(mesh, outer, inner, faceSide.Value, order, copy, holeThickness ?? copy.firstLoop.thickness);
                fill = $"clean fill ({Fill.Paths[^1]}, {holeThickness?.ToString() ?? "neighbour's"} thickness)";
            }
            Plugin.ModLog.LogInfo($"Create Hole ring: {note}; {fill}");
        }
        catch (Exception ex)
        {
            Reject(ex.Message);
            __result = new Il2CppReferenceArray<Face>(0);
            return false;
        }
        if (mine == null) return true;
        __result = mine;
        return false;
    }

    /// Builds the faces between the face's corners and the hole's ring with the mesh's own calls, turned the way the
    /// original face faces, copying its settings. The whole planned plate is checked before a single element is made:
    /// the game's own face checks only look at links, so a collapsed ring could otherwise leave a solid plate.
    static Il2CppReferenceArray<Face> CleanFill(EditMesh mesh, Il2CppReferenceArray<Vertex> outer, Il2CppReferenceArray<Vertex> inner, Num normal, int order, Face prototype, ushort thickness)
    {
        var verts = outer.Concat(inner).ToList();
        var pos = verts.Select(v => ToNum(v.position)).ToList();
        var added = new List<Fill.Added>();
        // The fill turns its faces the way the outline runs: give it the corners in the face's own order.
        var corners = Enumerable.Range(0, outer.Length).ToList();
        if (order < 0) corners.Reverse();
        var mode = (Fill.Mode)holeFill;
        var faces = Fill.Region(pos, corners, new List<List<int>> { Enumerable.Range(outer.Length, inner.Length).ToList() }, normal,
                                mode == Fill.Mode.Fewest ? null : added, mode == Fill.Mode.Light, mode);
        if (HoleFill.Check(pos, corners, Enumerable.Range(outer.Length, inner.Length).ToArray(), faces, normal) is string problem)
            throw new InvalidOperationException("孔周围的填面放不下：" + problem);
        foreach (var a in added)
        {
            // CreateVertex copies its prototype's position, so place each new vertex after creating it.
            var at = new UnityEngine.Vector3(a.P.X, a.P.Y, a.P.Z);
            var v = mesh.CreateVertex(inner[0], at);
            v.position = at;
            verts.Add(v);
        }
        var created = new List<Face>();
        foreach (var f in faces)
        {
            var vs = f.Select(i => verts[i]).ToArray();
            var es = new Edge[vs.Length];
            for (int k = 0; k < vs.Length; k++)
                if (Edge.GetConnectingEdge(vs[k], vs[(k + 1) % vs.Length]) is { } existing) es[k] = existing;
                else
                {
                    // No template: a hole's own sides are smooth and unconnected, whatever the face it goes through was.
                    es[k] = mesh.CreateEdge(vs[k], vs[(k + 1) % vs.Length], null);
                    es[k].flags = ElementFlags.None;
                }
            created.Add(mesh.CreateFace(new Il2CppReferenceArray<Vertex>(vs), new Il2CppReferenceArray<Edge>(es), prototype,
                new Il2CppStructArray<ushort>(vs.Select(_ => thickness).ToArray())));
        }
        return new Il2CppReferenceArray<Face>(created.ToArray());
    }

    /// Any face on the other side of one of the loop's edges.
    static Face? Neighbour(Il2CppReferenceArray<Vertex> outer)
    {
        for (int i = 0; i < outer.Length; i++)
        {
            var first = Edge.GetConnectingEdge(outer[i], outer[(i + 1) % outer.Length])?.loop;
            var l = first;
            for (int guard = 0; l != null && guard < 16; guard++)
            {
                if (l.face != null) return l.face;
                l = l.radialNext;
                if (l == null || l.Pointer == first!.Pointer) break;
            }
        }
        return null;
    }

    /// The face whose corners are exactly these vertices, found through the edge between the first two.
    static Face? FaceOf(Il2CppReferenceArray<Vertex> outer)
    {
        if (outer.Length < 3) return null;
        var mine = outer.Select(v => v.Pointer).ToHashSet();
        var first = Edge.GetConnectingEdge(outer[0], outer[1])?.loop;
        var l = first;
        for (int guard = 0; l != null && guard < 16; guard++)
        {
            if (IsFace(l.face, mine)) return l.face;
            l = l.radialNext;
            if (l == null || l.Pointer == first!.Pointer) break;
        }
        return null;
    }

    /// The game's fill turns some of its new faces inside out (a different few each time): turn those back round.
    [HarmonyPostfix, HarmonyPatch(typeof(CreateHoleOp), nameof(CreateHoleOp.FillEdgeLoop))]
    static void FixSides(Il2CppReferenceArray<Face> __result, Il2CppReferenceArray<Vertex> outer, Il2CppReferenceArray<Vertex> inner)
    {
        try
        {
            if (faceSide is not { } side || __result == null || activeEdit?.Failure != null) return;
            // The game's list can name a face twice; turning it twice would put it back inside out.
            var faces = __result.GroupBy(f => f.Pointer).Select(g => g.First()).ToList();
            var wrong = faces.Where(f => Num.Dot(HoleRing.Normal(Corners(f)), side) < 0).ToList();
            wrong.ForEach(Flip);
            var problems = faces.Select(Problem).Where(p => p != null).Distinct().ToList();
            // Also check the game's own triangle fan. Sound links do not prove that every ring edge was used, or that
            // the faces really leave an open hole instead of filling it back in.
            var positions = new List<Num>();
            var indices = new Dictionary<IntPtr, int>();
            int Index(Vertex vertex)
            {
                if (!indices.TryGetValue(vertex.Pointer, out int index))
                { indices[vertex.Pointer] = index = positions.Count; positions.Add(ToNum(vertex.position)); }
                return index;
            }
            var outerIds = outer.Select(Index).ToArray();
            if (Num.Dot(HoleRing.Normal(outer.Select(v => ToNum(v.position)).ToArray()), side) < 0) Array.Reverse(outerIds);
            var innerIds = inner.Select(Index).ToArray();
            var made = faces.Select(f => Corners(f, Index)).ToArray();
            if (HoleFill.Check(positions, outerIds, innerIds, made, side) is string geometry) problems.Add(geometry);
            int right = faces.Count(f => Num.Dot(HoleRing.Normal(Corners(f)), side) > 0);
            if (problems.Count > 0) Reject(string.Join("; ", problems));
            Plugin.ModLog.LogInfo($"Create Hole fill: turned {wrong.Count} of {faces.Count} faces the right way round, {right} now face out" +
                                  (problems.Count == 0 ? ", mesh checks OK" : ", MESH CHECK FAILED: " + string.Join("; ", problems)));
        }
        catch (Exception ex) { Reject(ex.Message); }
    }

    /// Reverses a face's corner order: each corner keeps its vertex, thickness and thicken edge, and moves onto the
    /// edge behind it. Done with the mesh's own link helpers.
    static void Flip(Face face)
    {
        var ring = new Loop[face.vertexCount];
        var l = face.firstLoop;
        for (int i = 0; i < ring.Length; i++, l = l.next) ring[i] = l;
        var edges = ring.Select(x => x.edge).ToArray();
        for (int i = 0; i < ring.Length; i++) Loop.RemoveLoopFromEdgeRadialCycle(edges[i], ring[i]);
        for (int i = 0; i < ring.Length; i++)
        {
            (ring[i].next, ring[i].prev) = (ring[i].prev, ring[i].next);
            ring[i].edge = edges[(i - 1 + ring.Length) % ring.Length];
        }
        for (int i = 0; i < ring.Length; i++) Loop.AddLoopToEdge(ring[i].edge, ring[i]);
        face.normal = -face.normal;
    }

    /// The game's own consistency checks for a face and its corners, plus "every corner's edge joins it to the next".
    internal static string? Problem(Face face)
    {
        var faceProblem = Face.Validate(face);
        if ((int)faceProblem != 0) return "face: " + faceProblem;
        var l = face.firstLoop;
        for (int i = 0; i < face.vertexCount; i++, l = l.next)
        {
            var loopProblem = Loop.Validate(l);
            if ((int)loopProblem != 0) return "corner: " + loopProblem;
            if (!Loop.ValidateRadialCycle(l)) return "corner not linked to its edge";
            if (!l.edge.ContainsVertices(l.vertex, l.next.vertex)) return "corner edge doesn't join it to the next corner";
        }
        return null;
    }

    /// +1 if the corners run the way the face does, -1 if backwards, 0 if it can't tell. Uses the face itself, or
    /// a neighbour across any of its edges (neighbours run a shared edge in opposite directions).
    static int FaceOrder(Il2CppReferenceArray<Vertex> outer)
    {
        var mine = outer.Select(v => v.Pointer).ToHashSet();
        int answer = 0;
        for (int i = 0; i < outer.Length && outer.Length >= 3; i++)
        {
            Vertex va = outer[i], vb = outer[(i + 1) % outer.Length];
            var first = Edge.GetConnectingEdge(va, vb)?.loop;
            var l = first;
            for (int guard = 0; l != null && guard < 16; guard++)
            {
                IntPtr from = l.vertex.Pointer, to = l.next.vertex.Pointer;
                int direction = from == va.Pointer && to == vb.Pointer ? 1 : from == vb.Pointer && to == va.Pointer ? -1 : 0;
                if (direction != 0 && IsFace(l.face, mine)) return direction;
                if (direction != 0 && answer == 0) answer = -direction;
                l = l.radialNext;
                if (l == null || l.Pointer == first!.Pointer) break;
            }
        }
        return answer;
    }

    static bool IsFace(Face? face, HashSet<IntPtr> corners) =>
        face != null && face.vertexCount == corners.Count && Corners(face, v => corners.Contains(v.Pointer)).All(x => x);

    internal static Num[] Corners(Face face) => Corners(face, v => ToNum(v.position));

    internal static T[] Corners<T>(Face face, Func<Vertex, T> pick)
    {
        var result = new T[face.vertexCount];
        var loop = face.firstLoop;
        for (int i = 0; i < result.Length; i++, loop = loop.next) result[i] = pick(loop.vertex);
        return result;
    }

    internal static Num ToNum(UnityEngine.Vector3 v) => new(v.x, v.y, v.z);

    [HarmonyPostfix, HarmonyPatch(typeof(PlateStructureEditor), nameof(PlateStructureEditor.OnGUI))]
    static void Draw(PlateStructureEditor __instance, IGUILayout layout) => Ui.Guard("Hole quality", () =>
    {
        var ui = layout.TryCast<IGUIElementDrawer>();
        if (ui == null || __instance.TryCast<FreeformPlateStructureEditor>() == null) return; // Create Hole is a freeform tool
        lastEditor = __instance; // where a refused hole tells the player
        Ui.Section(layout, "圆孔质量");
        ui.Slider("圆孔边数", segments, 4, 96, Ui.FloatCallback(v => segments = (int)Math.Round(v)));
        // Applied in HoleRing.TryFit, never through the game's CreateHoleOp.HoleRadiusScale: see there.
        ui.Slider("圆孔尺寸（%）", sizePercent, 10, 300, Ui.FloatCallback(v => sizePercent = (int)Math.Round(v)));
        var tip = new UITooltip("孔洞填充", "点击切换孔洞周围的面如何填充。" +
                                "最少点：只用孔的圆环和面原有的角点，不生成新点。" +
                                "轻量：只加少量点，避免出现细长的面。" +
                                "平滑：紧贴孔加一圈四边面，再向外增点使各面更均匀。" +
                                "游戏自带：使用游戏自己的三角扇布局。圆孔始终位于所选面内。");
        // The panel only redraws when asked, so ask, or the button would keep showing the old choice.
        ui.Button(FillNames[holeFill], Ui.Callback(() => { holeFill = (holeFill + 1) % FillNames.Length; __instance.RequestRedraw(); }), ref tip);
        ui.InfoField("供“面”编辑模式下的“创建孔”按钮使用。孔会保持圆形并落在面内；放不下时整个操作取消并提示，形状保持原样。", 2);
    });
}
