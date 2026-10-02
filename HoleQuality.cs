using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Sprocket.PlateMesh;
using Sprocket.UI;
using Sprocket.Vehicles.PlateStructures.Design;
using Num = System.Numerics.Vector3;

namespace SprocketTools;

/// Structure panel (hand-made shapes): "Hole quality" sets how many segments and how big the game's own
/// Create Hole tool makes a hole, and every hole's ring is made a true circle inside the face, running the same
/// way round as the face so the filled-in faces aren't inside out. It stays the game's operation, so undo works as normal.
[HarmonyPatch]
public static class HoleQuality
{
    static int segments = 32, sizePercent = 100;

    static ushort? holeThickness; // the holed face's plate thickness, read before the game takes the face away

    [HarmonyPrefix, HarmonyPatch(typeof(CreateHoleOp), nameof(CreateHoleOp.CreateHole))]
    static void UseQuality(ref int resolution, EditMesh mesh, Il2CppReferenceArray<Vertex> vertices)
    {
        Plugin.ModLog.LogInfo($"Create Hole: game asked for {resolution} segments, using {segments} at {sizePercent}% size");
        resolution = segments;
        holeThickness = null;
        holeRivets = null;
        Ui.Guard("Create Hole", () =>
        {
            var face = FaceOf(vertices);
            holeThickness = face?.firstLoop?.thickness;
            // The game drops the holed face's rivets with it: noted here, put back on the faces round the hole after.
            if (face != null) holeRivets = new MeshTools.RivetKeeper(mesh, new[] { face });
        });
    }

    static MeshTools.RivetKeeper? holeRivets;

    /// The holed face's rivets onto the faces now round the hole (those in the hole itself go).
    [HarmonyPostfix, HarmonyPatch(typeof(CreateHoleOp), nameof(CreateHoleOp.FillEdgeLoop))]
    static void KeepRivets(Il2CppReferenceArray<Face> __result) => Ui.Guard("Create Hole", () =>
    {
        if (holeRivets is not { Count: > 0 } keeper || __result == null) return;
        holeRivets = null;
        var (kept, lost) = keeper.Place(__result, reach: 0.002f);
        Plugin.ModLog.LogInfo($"Create Hole: rivets {kept} kept, {lost} in the hole removed");
    });

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
        Ui.Guard("Create Hole", () =>
        {
            var corners = outer.Select(v => ToNum(v.position)).ToArray();
            var ring = HoleRing.Fit(corners, inner.Select(v => ToNum(v.position)).ToArray(), ToNum(centre), out var note, sizePercent / 100f);
            for (int k = 0; k < inner.Length; k++) inner[k].position = new UnityEngine.Vector3(ring[k].X, ring[k].Y, ring[k].Z);
            int order = FaceOrder(outer);
            faceSide = order == 0 ? null : HoleRing.Normal(corners) * order;
            // The holed face is already gone by now: new faces copy a neighbour's settings and the holed face's thickness.
            string fill = GameFill ? "game's fill" : faceSide == null ? "game's fill (can't tell which way the face faces)"
                : (FaceOf(outer) ?? Neighbour(outer)) is not { } like ? "game's fill (no face next to it to copy settings from)"
                : (mine = CleanFill(mesh, outer, inner, faceSide.Value, order, like, holeThickness ?? like.firstLoop.thickness)) == null ? "game's fill (clean fill didn't fit)"
                : $"clean fill ({Fill.Paths[^1]}, {holeThickness?.ToString() ?? "neighbour's"} thickness)";
            Plugin.ModLog.LogInfo($"Create Hole ring: {note}; {fill}");
        });
        if (mine == null) return true;
        __result = mine;
        return false;
    }

    /// Builds the faces between the face's corners and the hole's ring with the mesh's own calls, turned the way the
    /// original face faces, copying its settings.
    static Il2CppReferenceArray<Face>? CleanFill(EditMesh mesh, Il2CppReferenceArray<Vertex> outer, Il2CppReferenceArray<Vertex> inner, Num normal, int order, Face prototype, ushort thickness)
    {
        var verts = outer.Concat(inner).ToList();
        var pos = verts.Select(v => ToNum(v.position)).ToList();
        var added = new List<Fill.Added>();
        // The fill turns its faces the way the outline runs: give it the corners in the face's own order.
        var corners = Enumerable.Range(0, outer.Length).ToList();
        if (order < 0) corners.Reverse();
        var mode = (Fill.Mode)holeFill;
        var faces = Fill.Region(pos, corners, new List<List<int>> { Enumerable.Range(outer.Length, inner.Length).ToList() }, normal,
                                mode == Fill.Mode.Fewest ? null : added, mode == Fill.Mode.Light);
        if (faces.Count == 0 || faces.Any(f => f.Length is < 3 or > 4 || f.Distinct().Count() != f.Length)) return null;
        foreach (var a in added)
        {
            // CreateVertex copies its prototype's position, so place each new vertex after creating it.
            var at = new UnityEngine.Vector3(a.P.X, a.P.Y, a.P.Z);
            var v = mesh.CreateVertex(inner[0], at);
            v.position = at;
            verts.Add(v);
        }
        var edgePrototype = Edge.GetConnectingEdge(outer[0], outer[1]);
        var created = new List<Face>();
        foreach (var f in faces)
        {
            var vs = f.Select(i => verts[i]).ToArray();
            var es = new Edge[vs.Length];
            for (int k = 0; k < vs.Length; k++)
                es[k] = Edge.GetConnectingEdge(vs[k], vs[(k + 1) % vs.Length]) ?? mesh.CreateEdge(vs[k], vs[(k + 1) % vs.Length], edgePrototype);
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
    static void FixSides(Il2CppReferenceArray<Face> __result) => Ui.Guard("Create Hole", () =>
    {
        if (faceSide is not { } side || __result == null) return;
        // The game's list can name a face twice; turning it twice would put it back inside out.
        var faces = __result.GroupBy(f => f.Pointer).Select(g => g.First()).ToList();
        var wrong = faces.Where(f => Num.Dot(HoleRing.Normal(Corners(f)), side) < 0).ToList();
        wrong.ForEach(Flip);
        var problems = faces.Select(Problem).Where(p => p != null).Distinct().ToList();
        int right = faces.Count(f => Num.Dot(HoleRing.Normal(Corners(f)), side) > 0);
        Plugin.ModLog.LogInfo($"Create Hole fill: turned {wrong.Count} of {faces.Count} faces the right way round, {right} now face out" +
                              (problems.Count == 0 ? ", mesh checks OK" : ", MESH CHECK FAILED: " + string.Join("; ", problems)));
    });

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
        Ui.Section(layout, "圆孔质量");
        ui.Slider("圆孔边数", segments, 4, 96, Ui.FloatCallback(v => segments = (int)Math.Round(v)));
        // Applied in HoleRing.Fit, never through the game's CreateHoleOp.HoleRadiusScale: see there.
        ui.Slider("圆孔尺寸（%）", sizePercent, 10, 300, Ui.FloatCallback(v => sizePercent = (int)Math.Round(v)));
        var tip = new UITooltip("孔洞填充", "最少点：只用孔的环和面的角点，不生成新点（三角形两两配对成四边面）。" +
                                "轻环：孔与角点之间加一圈点，面更均匀。" +
                                "平滑环：紧贴孔加一圈四边面，再逐圈向外扩展（点数更多，切片更均匀）。游戏自带：游戏自己的三角扇。");
        // The panel only redraws when asked, so ask, or the button would keep showing the old choice.
        ui.Button(FillNames[holeFill], Ui.Callback(() => { holeFill = (holeFill + 1) % FillNames.Length; __instance.RequestRedraw(); }), ref tip);
        ui.InfoField("供“面”编辑模式下的“创建孔”按钮使用。孔会保持圆形并落在面内。", 2);
    });
}
