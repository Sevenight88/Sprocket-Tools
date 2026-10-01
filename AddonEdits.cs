using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SprocketTools;

/// Blueprint edits that create or combine add-on structures. Plain JSON, no game code, so they're tested offline.
public static class AddonEdits
{
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// A palette part in the same format as the game's cube (Parts\cubePlateStructurePart.json): a cube-sized
    /// n-sided cylinder of 5 mm plate, placed from "Addon Structures" and reshaped with the game's own tools.
    public static string RoundAddonPart(string guid, string name, int sides)
    {
        var (meshData, _) = Cylinder(0.125f, 0.25f, sides, 5);
        var src = meshData["mesh"]!;
        var faces = src["faces"]!.AsArray().Select(f => (JsonNode?)new JsonObject { ["v"] = Clone(f!["v"]), ["t"] = Clone(f["t"]) });
        var part = new JsonObject
        {
            ["v"] = "0.0", ["guid"] = guid, ["name"] = name, ["tags"] = new JsonArray("plateStructurePrefab"), ["transform"] = null,
            ["components"] = new JsonArray(new JsonObject
            {
                ["fileID"] = "plateStructurePrefab", ["type"] = "plateStructurePrefab",
                ["info"] = new JsonObject
                {
                    ["v"] = "0.0",
                    ["mesh"] = new JsonObject
                    {
                        ["v"] = "0.0", ["smoothAngle"] = meshData["smoothAngle"]!.GetValue<float>(), ["gridSize"] = 1, ["format"] = "freeform",
                        ["mesh"] = new JsonObject { ["majorVersion"] = 0, ["minorVersion"] = 0, ["vertices"] = Clone(src["vertices"]), ["faces"] = new JsonArray(faces.ToArray()) },
                    },
                },
            }),
        };
        return part.ToJsonString(Indented);
    }

    /// What an edit changes, so the game can apply it in place as one undoable step: the edited design, the mesh id of
    /// each part whose shape changed (and its face count before), parts to remove, parts to move onto a new parent,
    /// and whether in place is possible (not when a changed part shares its settings block with another part).
    public sealed record EditPlan(string DesignJson, Dictionary<int, int> MeshIds, Dictionary<int, int> OldFaces, List<int> Remove,
                                  List<(int Child, int Parent)> Reparent, bool Live, int Focus, string Summary);

    /// Whether the settings blocks of `owners` (one part, or a mirror pair) are used by no other part. A mesh shared only
    /// in the saved file (every unedited palette cube shares one) is fine: in the game each part has its own, checked when applying.
    static bool OwnsMesh(Dictionary<int, JsonObject> objects, ICollection<int> owners)
    {
        var ids = owners.Select(v => Conversion.Id(objects[v], "structureBlueprintVuid")).ToHashSet();
        return objects.All(kv => owners.Contains(kv.Key) || kv.Value["structureBlueprintVuid"] is not JsonValue id || !ids.Contains(id.GetValue<int>()));
    }

    static readonly Matrix4x4 Flip = Matrix4x4.CreateScale(-1, 1, 1);

    /// Where a part's shape sits: a flipped part (a mirror twin, or flipped to its side) shows its shape mirrored along its own x.
    static Matrix4x4 Shape(JsonObject o, Matrix4x4 world) => ((o["flags"]?.GetValue<int>() ?? 0) & 1) != 0 ? Flip * world : world;

    /// Part `v`'s mirror twin when it really is one: linked both ways, the same mesh, and its shape the mirror image across
    /// the vehicle's centre (a link left over from moving one side with Mirror off doesn't count).
    static int? Twin(Dictionary<int, JsonObject> objects, JsonArray blocks, Dictionary<int, Matrix4x4> world, int v)
    {
        var o = objects[v];
        int? Mesh(JsonObject x) => x["structureBlueprintVuid"] is JsonValue id ? Block(blocks, id.GetValue<int>())["blueprint"]?["bodyMeshVuid"]?.GetValue<int>() : null;
        if (o["transform"]?["mirrorVuid"] is not JsonValue mv || !mv.TryGetValue<int>(out int t) || t == v || !objects.TryGetValue(t, out var twin)
            || twin["transform"]?["mirrorVuid"]?.GetValue<int>() != v || Mesh(o) is not int mesh || Mesh(twin) != mesh) return null;
        return Conversion.Near(Shape(o, world[v]) * Flip, Shape(twin, world[t])) ? t : null;
    }

    /// A part saved once that the game shows twice: marked mirrored, with no twin part. The second one (its image) is its
    /// shape reflected across the vehicle's centre plane.
    static bool Imaged(Dictionary<int, JsonObject> objects, int v) =>
        ((objects[v]["flags"]?.GetValue<int>() ?? 0) & 4) != 0
        && !(objects[v]["transform"]?["mirrorVuid"] is JsonValue m && m.TryGetValue<int>(out int t) && t != v && objects.ContainsKey(t));

    /// A part stops being one of a mirror pair: no twin, and no mirrored mark (with the mark and no twin the game would
    /// show an image of it on the other side).
    internal static void Unlink(JsonObject o)
    {
        o["transform"]!["mirrorVuid"] = -1;
        o["flags"] = (o["flags"]?.GetValue<int>() ?? 0) & ~4;
    }

    /// Part `o` stops being shown twice: its image, `source` (its shape before the edit) placed by `imageIntoShape` (the
    /// image's place in the part's own shape coordinates), becomes part of its own shape instead, so what shows stays the same.
    static void BakeImage(JsonObject o, Matrix4x4 imageIntoShape, JsonObject meshData, JsonObject source, List<JsonObject> structures, double armour)
    {
        Append(meshData["mesh"]!.AsObject(), meshData["rivets"]?.AsObject(), source["mesh"]!.AsObject(), source["rivets"]?.AsObject(), imageIntoShape);
        foreach (var s in structures) s["armourVolume"] = s["armourVolume"]!.GetValue<double>() + armour;
        Unlink(o);
    }

    /// Face count of every part's hand-made shape in a design (parts without one are left out).
    public static Dictionary<int, int> FaceCounts(string json)
    {
        var b = Conversion.Parse(json);
        var blocks = b["blueprints"]!.AsArray();
        var meshes = b["meshes"]!.AsArray();
        var counts = new Dictionary<int, int>();
        foreach (var (vuid, o) in Conversion.Objects(b))
        {
            var id = o["structureBlueprintVuid"];
            var block = id == null ? null : blocks.FirstOrDefault(x => x?["id"]?.GetValue<int>() == id.GetValue<int>());
            var mesh = block?["blueprint"]?["bodyMeshVuid"];
            if (mesh != null && MeshOf(meshes, mesh.GetValue<int>())?["mesh"]?["faces"] is JsonArray faces) counts[vuid] = faces.Count;
        }
        return counts;
    }

    /// The mesh number part `vuid`'s structure uses in a design.
    public static int MeshIdOf(string json, int vuid)
    {
        var b = Conversion.Parse(json);
        var block = Block(b["blueprints"]!.AsArray(), Conversion.Id(Conversion.Objects(b)[vuid], "structureBlueprintVuid"));
        return Conversion.Id(block["blueprint"]!, "bodyMeshVuid");
    }

    /// Folds add-ons `others` into `target`: their faces join target's shape at the same place in the world (each keeps
    /// its armour), parts attached to them move onto target, and the emptied parts are removed.
    public static string MergeAddons(string json, int target, IEnumerable<int> others) => PlanMerge(json, target, others).DesignJson;

    public static EditPlan PlanMerge(string json, int target, IEnumerable<int> others, LiveShapes? live = null)
    {
        var b = Conversion.Parse(json);
        var objects = Conversion.Objects(b);
        var merge = others.Where(v => v != target).Distinct().ToList();
        if (merge.Count == 0) throw new Exception("请至少再选中一个要合并的附加部件。");
        foreach (var v in merge)
            if (!objects.TryGetValue(v, out var o) || Conversion.GuidOf(o) != Conversion.AddonGuid)
                throw new Exception("只有附加部件才能并入其他部件；请重新选择。");
        // Into another add-on, or into a turret or hull body (its shape then holds the add-on's too).
        if (!objects.TryGetValue(target, out var targetObject) || Conversion.GuidOf(targetObject) is not (Conversion.AddonGuid or Conversion.CompartmentGuid))
            throw new Exception("附加部件可并入另一个附加部件、炮塔或车体；请重新选择。");

        var blocks = b["blueprints"]!.AsArray();
        var meshes = b["meshes"]!.AsArray();
        var before = Conversion.WorldMatrices(objects);

        // Mirror twins. With Mirror on, selecting the target selects its twin too: that one isn't merged into it.
        int? targetTwin = Twin(objects, blocks, before, target);
        if (targetTwin is int tt) merge.Remove(tt);
        if (merge.Count == 0) throw new Exception("请至少再选中一个要合并的附加部件。");
        var twin = new Dictionary<int, int>();
        foreach (int v in merge) if (Twin(objects, blocks, before, v) is int t && t != target) { twin[v] = t; twin[t] = v; }
        var newParent = new Dictionary<int, int>(); // each part that goes -> the part its attached parts move onto
        var shapes = new List<int>();               // the parts whose shapes go into the target's
        var images = new List<int>();               // parts shown twice by the game whose image goes in as well
        bool targetImaged = Imaged(objects, target), bake = false;
        bool Mirrored(int v) => twin.ContainsKey(v) || Imaged(objects, v);
        string mirror;
        if ((targetTwin != null || targetImaged) && merge.All(Mirrored))
        {
            // Mirrored into mirrored: the add-on on the target's side goes into the target's shape, and the target's twin
            // (which shares it) or image shows it mirrored on the other side, just as the add-on's twin or image looked.
            float Distance(int v) => Vector3.Distance(before[v].Translation, before[target].Translation);
            foreach (int v in merge.Select(v => twin.TryGetValue(v, out int t) && Distance(t) < Distance(v) ? t : v).Distinct())
            {
                shapes.Add(v);
                newParent[v] = target;
                if (twin.TryGetValue(v, out int t)) newParent[t] = targetTwin ?? target;
            }
            mirror = "both sides";
        }
        else if (targetTwin == null && !targetImaged)
        {
            // Into a part on the centre line (or one not mirrored): each add-on brings its twin or image, mirrored into place.
            foreach (int v in merge.Concat(merge.Where(twin.ContainsKey).Select(v => twin[v])).Distinct()) { shapes.Add(v); newParent[v] = target; }
            images.AddRange(merge.Where(v => Imaged(objects, v)));
            mirror = shapes.Count > merge.Count || images.Count > 0 ? "with twins" : "no twins";
        }
        else
        {
            // Some add-ons aren't mirrored: the target stops being mirrored and takes just the selected add-ons (with their
            // images). Its twin stays as it is; its image becomes part of its own shape.
            // A flipped part that stops being mirrored can show unflipped afterwards (seen in the game: a skirt's image,
            // made part of its shape, showed metres away), so this merge never takes the mirror mark off a flipped one.
            var unmarked = new[] { target }.Concat(targetTwin is int t2 ? new[] { t2 } : System.Array.Empty<int>())
                .Concat(merge.Where(twin.ContainsKey).Select(v => twin[v]).Where(t => !merge.Contains(t)));
            if (unmarked.Any(v => ((objects[v]["flags"]?.GetValue<int>() ?? 0) & 1) != 0))
                throw new Exception("目标是镜像件，而部分附加部件不是：翻转后部件会失去镜像关系。请一并选中附加部件的对应体（开启镜像），或合并到非镜像部件上。");
            foreach (int v in merge) { shapes.Add(v); newParent[v] = target; }
            images.AddRange(merge.Where(v => Imaged(objects, v)));
            if (targetTwin is int pair)
            {
                Unlink(targetObject);
                Unlink(objects[pair]);
                targetTwin = null;
                mirror = "target unpaired";
            }
            else { bake = true; mirror = "target's image kept in its shape"; }
        }
        var gone = newParent.Keys.ToList();
        var owners = targetTwin is int keep ? new[] { target, keep } : new[] { target };
        foreach (int start in owners)
            for (int p = Conversion.Id(objects[start], "pvuid"); objects.TryGetValue(p, out var up); p = Conversion.Id(up, "pvuid"))
                if (gone.Contains(p)) throw new Exception("展开最外层附加部件的面板，并合并到它上面。");
        var (At, ImageAt, differ) = Placement(objects, before, live);
        if (!Matrix4x4.Invert(At(target), out var intoTarget)) throw new Exception("该附加部件的变换矩阵不可逆。");

        bool inPlace = OwnsMesh(objects, owners);
        var reparent = new List<(int Child, int Parent)>();
        var (targetStructures, targetMesh) = OwnMesh(b, objects, owners, "合并");
        var into = targetMesh["mesh"]!.AsObject();
        var intoRivets = targetMesh["rivets"]?.AsObject();
        int facesBefore = into["faces"]!.AsArray().Count;
        if (bake)
        {
            BakeImage(targetObject, ImageAt(target) * intoTarget, targetMesh, Clone(targetMesh).AsObject(), targetStructures, targetStructures[0]["armourVolume"]?.GetValue<double>() ?? 0);
            inPlace = false; // the game's mirrored mark changes, which only a reload shows
        }

        // Each shape as it shows in the vehicle (mirrored if flipped; an image reflected across the centre), then into
        // the target's shape (mirrored if the target is).
        foreach (var (v, image) in shapes.Select(v => (v, false)).Concat(images.Select(v => (v, true))))
        {
            var structure = Block(blocks, Conversion.Id(objects[v], "structureBlueprintVuid"))["blueprint"]!;
            var meshData = MeshOf(meshes, Conversion.Id(structure, "bodyMeshVuid"));
            var mesh = meshData?["mesh"] ?? throw new Exception("只有手工（自由形式）附加部件可以合并。");
            Append(into, intoRivets, mesh.AsObject(), meshData!["rivets"]?.AsObject(), (image ? ImageAt(v) : At(v)) * intoTarget);
            foreach (var s in targetStructures)
                s["armourVolume"] = s["armourVolume"]!.GetValue<double>() + (structure["armourVolume"]?.GetValue<double>() ?? 0);
        }
        foreach (var (v, parent) in newParent)
        {
            if (!Matrix4x4.Invert(before[parent], out var intoParent)) throw new Exception($"部件 {parent} 的变换矩阵不可逆，无法处理。");
            var shift = before[v] * intoParent; // part v's frame expressed in its attached parts' new parent's frame
            foreach (var child in objects.Values.Where(o => Conversion.Id(o, "pvuid") == v && !gone.Contains(Conversion.Id(o, "vuid"))))
            {
                reparent.Add((Conversion.Id(child, "vuid"), parent));
                var t = child["transform"]!.AsObject();
                if (IsTranslation(shift))
                {
                    // Usual case, add-ons not rotated against each other: move the part, keep its exact rotation and scale
                    // (re-deriving Euler angles near 90 degrees loses precision).
                    var p = t["pos"]!.AsArray().Select(x => x!.GetValue<float>()).ToArray();
                    var q = Vector3.Transform(new Vector3(p[0], p[1], p[2]), shift);
                    t["pos"] = Array(q.X, q.Y, q.Z);
                    child["pvuid"] = parent;
                    continue;
                }
                try { Conversion.WriteTransform(t, before[Conversion.Id(child, "vuid")] * intoParent); }
                catch (Exception ex)
                {
                    throw new Exception($"附加部件 {v} 上的部件 {Conversion.Id(child, "vuid")} 无法精确移动到合并后的附加部件上（{ex.Message}）；合并已取消。");
                }
                child["pvuid"] = parent;
            }
        }

        RemoveParts(b, objects, gone);
        var after = Conversion.WorldMatrices(Conversion.Objects(b));
        foreach (var (id, matrix) in after)
            if (!Conversion.Near(before[id], matrix)) throw new Exception($"部件 {id} 会发生移动；合并已取消。");
        int meshId = Conversion.Id(targetStructures[0], "bodyMeshVuid");
        return new EditPlan(b.ToJsonString(Indented), owners.ToDictionary(v => v, _ => meshId), owners.ToDictionary(v => v, _ => facesBefore),
                            gone, reparent, inPlace, target, $"target={string.Join("+", owners)}, merged={string.Join(",", shapes)}, images={string.Join(",", images)}, removed={string.Join(",", gone)}, mirror={mirror}, moved parts={reparent.Count}"
                            + differ(owners.Concat(shapes).Concat(images)));
    }

    /// Where the game really shows each part's shape (shape coordinates to vehicle space), read from the running game:
    /// saved parts by number, and the images of parts shown twice by the part they mirror. Where a part is missing the
    /// design's own maths is used.
    public sealed record LiveShapes(IReadOnlyDictionary<int, Matrix4x4> Parts, IReadOnlyDictionary<int, Matrix4x4> Images);

    /// Boolean cut: the shape of add-on `cutter` (and its mirror twin, if any) is cut out of the other selected
    /// structures, or out of the structure it sits on when nothing else is selected. Any closed shape works. With
    /// `pocket`, the add-on's surface inside the structure becomes walls and a floor (a recess instead of a hole).
    /// A mirrored target (a twin pair sharing its shape, or a part shown twice) is cut on both sides, as the game's own
    /// shape editing does: its mirror marks are never changed.
    public static EditPlan PlanCut(string json, int cutter, IEnumerable<int> selected, bool removeCutter, bool pocket, Fill.Mode fill = Fill.Mode.Fewest, LiveShapes? live = null)
    {
        var b = Conversion.Parse(json);
        var objects = Conversion.Objects(b);
        if (!objects.TryGetValue(cutter, out var cutObj) || Conversion.GuidOf(cutObj) != Conversion.AddonGuid)
            throw new Exception("只有附加部件可以执行切割；请重新选择。");
        var blocks = b["blueprints"]!.AsArray();
        var meshes = b["meshes"]!.AsArray();
        var before = Conversion.WorldMatrices(objects);
        var cutters = new List<int> { cutter };
        if (Twin(objects, blocks, before, cutter) is int twin && Conversion.GuidOf(objects[twin]) == Conversion.AddonGuid) cutters.Add(twin);
        bool Structure(int v) => !cutters.Contains(v) && objects.TryGetValue(v, out var o) && o["structureBlueprintVuid"] != null;
        var targets = selected.Where(Structure).Distinct().ToList();
        if (targets.Count == 0) targets = cutters.Select(c => Conversion.Id(objects[c], "pvuid")).Where(Structure).Distinct().ToList();
        if (targets.Count == 0) throw new Exception("请同时选中被切割的形状（该附加部件并未安装在形状上）。");
        var (At, ImageAt, differ) = Placement(objects, before, live);

        // A cutter the game shows twice cuts with its image too; with a twin or an image the cut is mirrored.
        bool imagedCutter = Imaged(objects, cutter), mirroredCut = cutters.Count == 2 || imagedCutter;
        var placed = cutters.Select(c => (Part: c, At: At(c))).ToList(); // where each shape shows
        if (imagedCutter) placed.Add((cutter, ImageAt(cutter)));
        var shapes = placed.Select(pc =>
        {
            var (c, at) = pc;
            var mesh = MeshOf(meshes, Conversion.Id(Block(blocks, Conversion.Id(objects[c], "structureBlueprintVuid"))["blueprint"]!, "bodyMeshVuid"))?["mesh"]
                       ?? throw new Exception("用于切割的附加部件没有手工形状。");
            var raw = mesh["vertices"]!.AsArray().Select(x => MeshCut.F(x)).ToArray();
            var world = Enumerable.Range(0, raw.Length / 3).Select(i => Vector3.Transform(new Vector3(raw[3 * i], raw[3 * i + 1], raw[3 * i + 2]), at)).ToList();
            var faces = mesh["faces"]!.AsArray().Select(f => f!.AsObject()).ToList();
            if (OpenEdges(world, faces.Select(f => f["v"]!.AsArray().Select(x => x!.GetValue<int>()).ToArray()).ToList()) is int open and > 0)
                throw new Exception($"用于切割的附加部件不是闭合形状（{open} 条边只有一侧有面），因此没有可用于切割的内部。请先封闭它的开口边，或改用闭合的附加部件切割。");
            return (World: world,
                    Faces: faces.Select(f => f["v"]!.AsArray().Select(x => x!.GetValue<int>()).ToArray()).ToList(),
                    Thickness: faces.Select(f => f["t"]!.AsArray().Select(x => MeshCut.F(x)).ToArray()).ToList(),
                    Modes: faces.Select(f => Enumerable.Range(0, f["v"]!.AsArray().Count).Select(k => (byte)((ulong)MeshCut.L(f["tm"]) >> (8 * k))).ToArray()).ToList());
        }).ToList();

        bool inPlace = true;
        var meshIds = new Dictionary<int, int>();
        var oldFaces = new Dictionary<int, int>();
        var report = new List<string>();
        // A mirror pair of targets shares one shape, and a target the game shows twice shares it with its image: the shape
        // is cut once and both sides show the cut. A mirrored cut uses the target's side only (the twin cutter makes the
        // other side's cut); a cut on one side looks from both sides, so the hole lands wherever the cutter is.
        var groups = new List<int[]>();
        foreach (int t in targets)
        {
            if (groups.Any(g => g.Contains(t))) continue;
            groups.Add(Twin(objects, blocks, before, t) is int tt && !cutters.Contains(tt) ? new[] { t, tt } : new[] { t });
        }
        foreach (var group in groups)
        {
            int target = group[0];
            var views = new List<Matrix4x4> { At(target) };
            if (!mirroredCut && group.Length == 2) views.Add(At(group[1]));
            else if (!mirroredCut && Imaged(objects, target)) views.Add(ImageAt(target));
            var solids = new List<MeshCut.Solid>();
            foreach (var view in views)
            {
                if (!Matrix4x4.Invert(view, out var intoTarget)) throw new Exception($"部件 {target} 的变换矩阵不可逆，无法处理。");
                foreach (var s in shapes.Select(s => Outward(s.World.Select(p => Vector3.Transform(p, intoTarget)).ToList(), s.Faces, s.Thickness, s.Modes)))
                    // A cutter on the centre line looks the same from both sides: cut with it once.
                    if (!solids.Any(o => o.Verts.Count == s.Verts.Count && s.Verts.All(p => o.Verts.Any(q => Vector3.DistanceSquared(p, q) < 1e-8f)))) solids.Add(s);
            }
            inPlace &= OwnsMesh(objects, group); // a part sharing its settings with another only gets its own by reloading
            var (owned, meshData) = OwnMesh(b, objects, group, "切割");
            int facesBefore = meshData["mesh"]!["faces"]!.AsArray().Count;
            var result = MeshCut.Cut(meshData, solids, pocket, fill);
            foreach (int t in group) oldFaces[t] = facesBefore;
            if (result.FacesCut == 0) continue;
            foreach (var structure in owned)
                structure["armourVolume"] = Math.Max(0, (structure["armourVolume"]?.GetValue<double>() ?? 0) + result.ArmourChange);
            foreach (int t in group) meshIds[t] = Conversion.Id(owned[0], "bodyMeshVuid");
            report.Add($"{result.FacesCut} faces of part {string.Join(" and its twin ", group)}" + (pocket ? $" ({result.PocketFaces} pocket plates)" : "")
                       + (result.RivetsMoved + result.RivetsDropped > 0 ? $", rivets {result.RivetsMoved} moved {result.RivetsDropped} removed" : ""));
        }
        if (report.Count == 0) throw new Exception("该附加部件未与要切割的形状相交；请先把它移入装甲板。");

        string removed = "";
        var remove = new List<int>();
        if (removeCutter)
        {
            if (objects.Values.Any(o => cutters.Contains(Conversion.Id(o, "pvuid")))) removed = " Kept the add-on: other parts are attached to it.";
            else { RemoveParts(b, objects, cutters); remove = cutters; removed = " Removed the cutting add-on."; }
        }
        var after = Conversion.WorldMatrices(Conversion.Objects(b));
        foreach (var (id, matrix) in after)
            if (!Conversion.Near(before[id], matrix)) throw new Exception($"部件 {id} 会发生移动；切割已取消。");
        return new EditPlan(b.ToJsonString(Indented), meshIds, oldFaces, remove, new(), inPlace, remove.Count > 0 ? meshIds.Keys.First() : cutter,
                           "Cut " + string.Join(", ", report) + "." + removed + differ(cutters.Concat(groups.SelectMany(g => g))));
    }

    /// Where shapes show: as the running game reports (when it did), else by the design's maths (a flipped part mirrored
    /// along its own x; an image reflected across the centre). The third function names parts where the two disagree, for
    /// the log.
    static (Func<int, Matrix4x4> At, Func<int, Matrix4x4> ImageAt, Func<IEnumerable<int>, string> Differ) Placement(
        Dictionary<int, JsonObject> objects, Dictionary<int, Matrix4x4> world, LiveShapes? live)
    {
        Matrix4x4 Model(int v) => Shape(objects[v], world[v]);
        Matrix4x4 At(int v) => live != null && live.Parts.TryGetValue(v, out var m) ? m : Model(v);
        Matrix4x4 ImageAt(int v) => live != null && live.Images.TryGetValue(v, out var m) ? m : At(v) * Flip;
        // How far apart the two put the corners of a 1 m cube at the shape's origin.
        float Gap(Matrix4x4 a, Matrix4x4 c) => Enumerable.Range(0, 8).Select(i => new Vector3(i & 1, (i >> 1) & 1, (i >> 2) & 1))
            .Max(p => Vector3.Distance(Vector3.Transform(p, a), Vector3.Transform(p, c)));
        string Differ(IEnumerable<int> parts)
        {
            if (live == null) return "";
            var off = parts.Distinct().Where(live.Parts.ContainsKey).Select(v => (v, d: Gap(Model(v), live.Parts[v]))).Where(x => x.d > 0.001f).ToList();
            return off.Count == 0 ? " [placement: game and design maths agree]"
                : " [placement from the game; the design maths had " + string.Join(", ", off.Select(x => $"part {x.v} (flags {objects[x.v]["flags"]}) off by {x.d * 1000:0} mm")) + "]";
        }
        return (At, ImageAt, Differ);
    }

    /// Separate, as Blender's P: the faces of `part` matching `selected` (each face's corners in the shape's own
    /// coordinates, as the editor has them) move into a new add-on at the same place. The new add-on hangs where the part
    /// does (same parent, place and flip; on a hull, attached to it), so it shows exactly where the faces were. A mirror
    /// twin sharing the shape gives up the same faces to a new twin; a part the game shows twice makes one shown twice.
    public static (string Json, List<(int Source, int Added)> Parts, string Log) Separate(string json, int part, IReadOnlyList<Vector3[]> selected)
    {
        var b = Conversion.Parse(json);
        var objects = Conversion.Objects(b);
        if (!objects.TryGetValue(part, out var source) || source["structureBlueprintVuid"] == null) throw new Exception("该部件没有手工形状；请重新选择。");
        var blocks = b["blueprints"]!.AsArray();
        var meshes = b["meshes"]!.AsArray();
        var owners = Twin(objects, blocks, Conversion.WorldMatrices(objects), part) is int twin ? new[] { part, twin } : new[] { part };
        var (structures, meshData) = OwnMesh(b, objects, owners, "分离");
        var mesh = meshData["mesh"]!.AsObject();
        var raw = mesh["vertices"]!.AsArray().Select(x => MeshCut.F(x)).ToArray();
        var pos = Enumerable.Range(0, raw.Length / 3).Select(i => new Vector3(raw[3 * i], raw[3 * i + 1], raw[3 * i + 2])).ToList();
        var faces = mesh["faces"]!.AsArray().Select(f => f!["v"]!.AsArray().Select(x => x!.GetValue<int>()).ToArray()).ToList();

        var moved = Matching(pos, faces, selected, out int missing);
        if (moved.Count == 0) throw new Exception("在已保存的形状中找不到所选面；请重新点击该部件后再试。");
        if (moved.Count == faces.Count) throw new Exception("已选中全部面：至少给部件留下一个面（要复制整个部件请用 Alt）。");

        var tList = mesh["faces"]!.AsArray().Select(f => f!["t"]!.AsArray().Select(x => MeshCut.F(x)).ToArray()).ToList();
        double armour = moved.Sum(f => Area(pos, faces[f]) * tList[f].Average() / 1000.0);
        foreach (var s in structures) s["armourVolume"] = Math.Max(0, (s["armourVolume"]?.GetValue<double>() ?? 0) - armour);
        var entry = meshes.First(m => m!["vuid"]!.GetValue<int>() == Conversion.Id(structures[0], "bodyMeshVuid"))!;
        var newMesh = Clone(entry).AsObject();
        newMesh["vuid"] = NextId(meshes, "vuid");
        newMesh["meshData"] = Subset(meshData, moved.Contains, false);
        var rest = Subset(meshData, f => !moved.Contains(f), true);
        meshData["mesh"] = Clone(rest["mesh"]);
        if (rest["rivets"] is JsonNode rivets) meshData["rivets"] = Clone(rivets);
        meshes.Add(newMesh);

        // New numbers for each new add-on and its structure component, past every number the parts use.
        int next = objects.Values.SelectMany(o => o.Where(kv => kv.Value is JsonValue v && v.TryGetValue<int>(out _) && kv.Key is not ("pvuid" or "flags"))
                                                   .Select(kv => kv.Value!.GetValue<int>())).DefaultIfEmpty(0).Max() + 1;
        var made = new List<int>();
        var list = b["objects"]!.AsArray();
        var newBlocks = new Dictionary<int, int>(); // the part's settings block -> the new add-on's (twins sharing one share the copy)
        foreach (int owner in owners)
        {
            var o = objects[owner];
            int sourceBlock = Conversion.Id(o, "structureBlueprintVuid");
            if (!newBlocks.ContainsKey(sourceBlock))
            {
                var block = Clone(Block(blocks, sourceBlock)).AsObject();
                block["id"] = newBlocks[sourceBlock] = NextId(blocks, "id");
                var settings = block["blueprint"]!.AsObject();
                settings["bodyMeshVuid"] = newMesh["vuid"]!.GetValue<int>();
                settings["armourVolume"] = armour;
                if (Conversion.GuidOf(o) != Conversion.AddonGuid) { settings["partRepositioning"] = true; settings["collisionEnabled"] = false; } // an add-on's usual settings
                blocks.Add(block);
            }
            int flags = o["flags"]?.GetValue<int>() ?? 0;
            JsonObject transform;
            int parent = Conversion.Id(o, "pvuid");
            if (parent >= 0) transform = Clone(o["transform"]).AsObject();
            else
            {
                // The hull: nothing to hang beside, so the add-on goes on it, at its place.
                if ((flags & 1) != 0) throw new Exception("该车体已被翻转；请改为从附加部件分离。");
                parent = owner;
                transform = new JsonObject { ["mirrorVuid"] = -1, ["pos"] = Array(0, 0, 0), ["rot"] = Array(0, 0, 0, 0), ["scale"] = Array(1, 1, 1) };
            }
            if (owners.Length == 1 && !Imaged(objects, owner)) flags &= ~4; // mirrored only if it has a twin or an image
            transform["mirrorVuid"] = -1;
            var added = new JsonObject
            {
                ["guid"] = Conversion.AddonGuid, ["vuid"] = next++, ["pvuid"] = parent, ["flags"] = flags, ["plateStructure"] = next++,
                ["transform"] = transform, ["structureBlueprintVuid"] = newBlocks[sourceBlock],
            };
            list.Add(added);
            made.Add(added["vuid"]!.GetValue<int>());
        }
        if (made.Count == 2)
        {
            var pair = Conversion.Objects(b);
            pair[made[0]]["transform"]!["mirrorVuid"] = made[1];
            pair[made[1]]["transform"]!["mirrorVuid"] = made[0];
        }
        return (b.ToJsonString(Indented), owners.Zip(made).ToList(), $"part {string.Join(" and its twin ", owners)}: {moved.Count} of {faces.Count} faces into new add-on {string.Join(" and its twin ", made)}"
                                                    + (missing > 0 ? $" ({missing} selected faces not found)" : ""));
    }

    /// The saved shape's faces matching the editor's (each by its corners, to 0.1 mm), looked up in 1 cm slices along x;
    /// `missing` counts editor faces with no match.
    static HashSet<int> Matching(IReadOnlyList<Vector3> pos, IReadOnlyList<int[]> faces, IReadOnlyList<Vector3[]> selected, out int missing)
    {
        Vector3 Middle(IEnumerable<Vector3> ps) => ps.Aggregate(Vector3.Zero, (s, p) => s + p) / ps.Count();
        var slices = Enumerable.Range(0, faces.Count).ToLookup(f => (int)MathF.Floor(Middle(faces[f].Select(i => pos[i])).X * 100));
        var found = new HashSet<int>();
        missing = 0;
        foreach (var corners in selected)
        {
            int slice = (int)MathF.Floor(Middle(corners).X * 100), before = found.Count;
            foreach (int f in Enumerable.Range(slice - 1, 3).SelectMany(s => slices[s]))
                if (faces[f].Length == corners.Length && corners.All(p => faces[f].Any(i => Vector3.DistanceSquared(p, pos[i]) < 1e-8f))) found.Add(f);
            if (found.Count == before) missing++;
        }
        return found;
    }

    /// Separate picked pieces: each piece of `part`'s shape that nothing joins to the rest (no point in common, points at
    /// the same place counting as one) with a face in `picked` becomes its own add-on in the same place, so one click on
    /// any face of a piece takes the whole piece. With every piece picked, the biggest stays on the part. One group of
    /// (part, new add-on) per piece: two with a mirror twin.
    public static (string Json, List<List<(int Source, int Added)>> Groups, string Log) SeparatePieces(string json, int part, IReadOnlyList<Vector3[]> picked)
    {
        var b = Conversion.Parse(json);
        var objects = Conversion.Objects(b);
        if (!objects.TryGetValue(part, out var source) || source["structureBlueprintVuid"] == null) throw new Exception("该部件没有手工形状；请重新选择。");
        var mesh = MeshOf(b["meshes"]!.AsArray(), Conversion.Id(Block(b["blueprints"]!.AsArray(), Conversion.Id(source, "structureBlueprintVuid"))["blueprint"]!, "bodyMeshVuid"))?["mesh"]
                   ?? throw new Exception("只有手工（自由形式）形状可以分离。");
        var raw = mesh["vertices"]!.AsArray().Select(x => MeshCut.F(x)).ToArray();
        var pos = Enumerable.Range(0, raw.Length / 3).Select(i => new Vector3(raw[3 * i], raw[3 * i + 1], raw[3 * i + 2])).ToList();
        var faces = mesh["faces"]!.AsArray().Select(f => f!["v"]!.AsArray().Select(x => x!.GetValue<int>()).ToArray()).ToList();
        var pieces = LooseParts(pos, faces);
        if (pieces.Count < 2) throw new Exception("该形状是一整块：没有可分离的独立块（要拆开请先选面，再用“分离所选”）。");
        var hit = Matching(pos, faces, picked, out _);
        var chosen = pieces.Where(p => p.Any(hit.Contains)).ToList();
        if (chosen.Count == 0) throw new Exception("请先在每个独立块上各点选一个要分离的面。");
        if (chosen.Count == pieces.Count) chosen.Remove(chosen.MaxBy(p => p.Count)!); // every piece picked: the biggest stays
        var groups = new List<List<(int Source, int Added)>>();
        foreach (var piece in chosen)
        {
            var (next, parts, _) = Separate(json, part, piece.Select(f => faces[f].Select(i => pos[i]).ToArray()).ToList());
            json = next;
            groups.Add(parts);
        }
        return (json, groups, $"part {part}: {pieces.Count} loose pieces ({string.Join(", ", pieces.Select(p => p.Count))} faces), {chosen.Count} picked, " +
                              $"new add-ons {string.Join(", ", groups.Select(g => string.Join(" and ", g.Select(p => p.Added))))}");
    }

    /// The faces of a shape in pieces: faces sharing a point (or points at the same place, to 0.01 mm) are one piece.
    internal static List<List<int>> LooseParts(IReadOnlyList<Vector3> pos, IReadOnlyList<int[]> faces)
    {
        var parent = Enumerable.Range(0, faces.Count).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        var first = new Dictionary<(long, long, long), int>(); // a place -> the first face with a corner there
        for (int f = 0; f < faces.Count; f++)
            foreach (int v in faces[f])
            {
                var key = ((long)MathF.Round(pos[v].X * 1e5f), (long)MathF.Round(pos[v].Y * 1e5f), (long)MathF.Round(pos[v].Z * 1e5f));
                if (first.TryGetValue(key, out int g)) parent[Find(f)] = Find(g); else first[key] = f;
            }
        return Enumerable.Range(0, faces.Count).GroupBy(Find).Select(g => g.ToList()).ToList();
    }

    /// A copy of `meshData` with only the faces `keep` picks, and the points, lines, thickening and rivets they use (loose
    /// points and lines too, with `loose`). A corner thickening along a line that isn't kept goes back to Auto.
    static JsonObject Subset(JsonObject meshData, Func<int, bool> keep, bool loose)
    {
        var copy = Clone(meshData).AsObject();
        var mesh = copy["mesh"]!.AsObject();
        var raw = mesh["vertices"]!.AsArray().Select(x => MeshCut.F(x)).ToArray();
        var ends = mesh["edges"]!.AsArray().Select(x => x!.GetValue<int>()).ToArray();
        var edgeFlags = mesh["edgeFlags"]!.AsArray();
        var faceNodes = mesh["faces"]!.AsArray();
        static (int, int) Key(int a, int b) => a < b ? (a, b) : (b, a);
        var kept = Enumerable.Range(0, faceNodes.Count).Where(keep).ToList();
        var corners = faceNodes.Select(f => f!["v"]!.AsArray().Select(x => x!.GetValue<int>()).ToArray()).ToList();
        IEnumerable<(int, int)> Sides(int[] c) => c.Select((v, k) => Key(v, c[(k + 1) % c.Length]));
        var used = kept.SelectMany(f => Sides(corners[f])).ToHashSet();
        var anyFace = corners.SelectMany(Sides).ToHashSet();
        var points = kept.SelectMany(f => corners[f]).ToHashSet();
        var edgeMap = new Dictionary<int, int>();
        for (int e = 0; e < ends.Length / 2; e++)
        {
            var key = Key(ends[2 * e], ends[2 * e + 1]);
            if (!used.Contains(key) && !(loose && !anyFace.Contains(key))) continue;
            edgeMap[e] = edgeMap.Count;
            points.Add(key.Item1); points.Add(key.Item2);
        }
        if (loose) points.UnionWith(Enumerable.Range(0, raw.Length / 3).Except(corners.SelectMany(c => c)));
        var order = points.OrderBy(i => i).ToList();
        var renumber = order.Select((v, i) => (v, i)).ToDictionary(x => x.v, x => x.i);
        mesh["vertices"] = new JsonArray(order.SelectMany(i => new[] { raw[3 * i], raw[3 * i + 1], raw[3 * i + 2] }).Select(x => (JsonNode?)x).ToArray());
        var edgeOrder = edgeMap.OrderBy(kv => kv.Value).Select(kv => kv.Key).ToList();
        mesh["edges"] = new JsonArray(edgeOrder.SelectMany(e => new[] { renumber[ends[2 * e]], renumber[ends[2 * e + 1]] }).Select(x => (JsonNode?)x).ToArray());
        mesh["edgeFlags"] = new JsonArray(edgeOrder.Select(e => Clone(edgeFlags[e])).ToArray());

        var faceMap = new Dictionary<int, int>();
        var outFaces = new JsonArray();
        foreach (int f in kept)
        {
            var node = Clone(faceNodes[f]).AsObject();
            node["v"] = new JsonArray(corners[f].Select(v => (JsonNode?)renumber[v]).ToArray());
            // Thicken edge per corner (first four): 16 bits each, an index into the edges (0xFFFF = none), renumbered.
            if (node["te"] != null)
            {
                ulong te = (ulong)MeshCut.L(node["te"]), tm = (ulong)MeshCut.L(node["tm"]);
                for (int k = 0; k < Math.Min(corners[f].Length, 4); k++)
                {
                    ulong r = (te >> (16 * k)) & 0xFFFF;
                    if (r == 0xFFFF) continue;
                    ulong to = edgeMap.TryGetValue((int)r, out int n) ? (ulong)n : 0xFFFF;
                    te = te & ~(0xFFFFUL << (16 * k)) | to << (16 * k);
                    if (to == 0xFFFF && ((tm >> (8 * k)) & 0xFF) == 4) tm = tm & ~(0xFFUL << (8 * k)) | 1UL << (8 * k); // Manual -> Auto
                }
                node["te"] = MeshCut.Te(te);
                if (node["tm"] != null) node["tm"] = (int)tm;
            }
            faceMap[f] = outFaces.Count;
            outFaces.Add(node);
        }
        mesh["faces"] = outFaces;

        if (copy["rivets"]?["nodes"] is JsonArray nodes)
        {
            var keepNodes = Enumerable.Range(0, nodes.Count).Where(i => faceMap.ContainsKey(nodes[i]!["face"]!.GetValue<int>())).ToList();
            var nodeMap = keepNodes.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i);
            copy["rivets"]!["nodes"] = new JsonArray(keepNodes.Select(i =>
            {
                var n = Clone(nodes[i]).AsObject();
                n["face"] = faceMap[n["face"]!.GetValue<int>()];
                foreach (var link in new[] { "next", "prev" })
                    if (n[link]?.GetValue<int>() is int to && to >= 0) n[link] = nodeMap.TryGetValue(to, out int ni) ? ni : -1;
                return (JsonNode?)n;
            }).ToArray());
        }
        return copy;
    }

    /// How many edges of a shape have a face on one side only (0: closed). Points at the same place count as one, and an
    /// edge split on the other side by a point of its own line (the faces are still joined) counts as closed.
    internal static int OpenEdges(IReadOnlyList<Vector3> verts, IReadOnlyList<int[]> faces)
    {
        var same = new Dictionary<(long, long, long), int>();
        var rep = verts.Select((p, i) => same.TryGetValue(((long)MathF.Round(p.X * 1e5f), (long)MathF.Round(p.Y * 1e5f), (long)MathF.Round(p.Z * 1e5f)), out int r) ? r
            : same[((long)MathF.Round(p.X * 1e5f), (long)MathF.Round(p.Y * 1e5f), (long)MathF.Round(p.Z * 1e5f))] = i).ToArray();
        var sides = new HashSet<(int, int)>();
        foreach (var f in faces)
            for (int k = 0; k < f.Length; k++)
                if (rep[f[k]] != rep[f[(k + 1) % f.Length]]) sides.Add((rep[f[k]], rep[f[(k + 1) % f.Length]]));
        var open = sides.Where(s => !sides.Contains((s.Item2, s.Item1))).ToList();
        if (open.Count > 400) return open.Count; // not worth looking for split edges: it's open
        bool Covered((int A, int B) s)
        {
            var mid = (verts[s.A] + verts[s.B]) / 2;
            var dir = verts[s.B] - verts[s.A];
            return open.Any(o =>
            {
                var e = verts[o.Item2] - verts[o.Item1];
                if (Vector3.Dot(e, dir) >= 0) return false;
                float t = Vector3.Dot(mid - verts[o.Item1], e) / Math.Max(e.LengthSquared(), 1e-18f);
                return t >= -1e-6f && t <= 1 + 1e-6f && Vector3.Distance(verts[o.Item1] + e * t, mid) < 1e-5f;
            });
        }
        return open.Count(s => !Covered(s));
    }

    /// A cutting shape with its faces turned to face outward (a mirrored add-on's faces point inward).
    static MeshCut.Solid Outward(List<Vector3> verts, List<int[]> faces, List<float[]> thickness, List<byte[]> modes)
    {
        double volume = faces.Sum(f => Enumerable.Range(1, f.Length - 2).Sum(k => Vector3.Dot(verts[f[0]], Vector3.Cross(verts[f[k]], verts[f[k + 1]])) / 6.0));
        if (volume >= 0) return new MeshCut.Solid(verts, faces, thickness, modes);
        return new MeshCut.Solid(verts, faces.Select(f => f.Reverse().ToArray()).ToList(),
            thickness.Select(t => t.Reverse().ToArray()).ToList(), modes.Select(m => m.Reverse().ToArray()).ToList());
    }

    /// The structure blocks and shared mesh data of `owners` (one part, or a mirror pair sharing a mesh), copied first if
    /// other parts share them, so an edit changes these parts only.
    static (List<JsonObject> Structures, JsonObject MeshData) OwnMesh(JsonObject b, Dictionary<int, JsonObject> objects, ICollection<int> owners, string what)
    {
        var blocks = b["blueprints"]!.AsArray();
        var meshes = b["meshes"]!.AsArray();
        var structures = new List<JsonObject>();
        foreach (int id in owners.Select(v => Conversion.Id(objects[v], "structureBlueprintVuid")).Distinct().ToList())
        {
            var block = Block(blocks, id);
            var users = objects.Values.Where(o => o["structureBlueprintVuid"]?.GetValue<int>() == id).ToList();
            if (users.Any(o => !owners.Contains(Conversion.Id(o, "vuid"))))
            {
                block = Clone(block).AsObject();
                block["id"] = NextId(blocks, "id");
                blocks.Add(block);
                foreach (var o in users.Where(o => owners.Contains(Conversion.Id(o, "vuid")))) o["structureBlueprintVuid"] = block["id"]!.GetValue<int>();
            }
            structures.Add(block["blueprint"]!.AsObject());
        }
        int meshId = Conversion.Id(structures[0], "bodyMeshVuid");
        if (structures.Any(s => Conversion.Id(s, "bodyMeshVuid") != meshId)) throw new Exception("形状不同的镜像对应体无法一起编辑。");
        if (blocks.Count(x => x!["type"]?.GetValue<string>() == "structure" && x["blueprint"]?["bodyMeshVuid"]?.GetValue<int>() == meshId) > structures.Count)
        {
            var copy = Clone(meshes.First(m => m!["vuid"]!.GetValue<int>() == meshId)).AsObject();
            copy["vuid"] = meshId = NextId(meshes, "vuid");
            meshes.Add(copy);
            foreach (var s in structures) s["bodyMeshVuid"] = meshId;
        }
        var meshData = MeshOf(meshes, meshId);
        if (meshData?["mesh"] is not JsonObject) throw new Exception($"只有手工（自由形式）形状可以{what}。");
        return (structures, meshData);
    }

    /// Removes parts, then the settings blocks and meshes only they used, and unlinks mirror twins pointing at them.
    static void RemoveParts(JsonObject b, Dictionary<int, JsonObject> objects, ICollection<int> gone)
    {
        var blocks = b["blueprints"]!.AsArray();
        var meshes = b["meshes"]!.AsArray();
        var goneBlocks = gone.Select(v => Conversion.Id(objects[v], "structureBlueprintVuid")).ToHashSet();
        var goneMeshes = goneBlocks.Select(id => Conversion.Id(Block(blocks, id)["blueprint"]!, "bodyMeshVuid")).ToHashSet();
        var list = b["objects"]!.AsArray();
        for (int i = list.Count - 1; i >= 0; i--) if (gone.Contains(Conversion.Id(list[i]!, "vuid"))) list.RemoveAt(i);
        var liveBlocks = list.SelectMany(o => o!.AsObject()).Where(kv => kv.Key.EndsWith("BlueprintVuid")).Select(kv => kv.Value!.GetValue<int>()).ToHashSet();
        for (int i = blocks.Count - 1; i >= 0; i--)
        {
            int id = Conversion.Id(blocks[i]!, "id");
            if (goneBlocks.Contains(id) && !liveBlocks.Contains(id)) blocks.RemoveAt(i);
        }
        var liveMeshes = blocks.Where(x => x!["type"]?.GetValue<string>() == "structure")
            .Select(x => x!["blueprint"]?["bodyMeshVuid"]?.GetValue<int>() ?? -1).ToHashSet();
        for (int i = meshes.Count - 1; i >= 0; i--)
        {
            int id = meshes[i]!["vuid"]!.GetValue<int>();
            if (goneMeshes.Contains(id) && !liveMeshes.Contains(id)) meshes.RemoveAt(i);
        }
        foreach (var o in list)
            if (o!["transform"]?["mirrorVuid"] is JsonValue mv && mv.TryGetValue<int>(out int mirror) && gone.Contains(mirror)) Unlink(o.AsObject());
    }

    // Adds `mesh` (vertices mapped through `m`) to `into`, keeping each face's armour, thickening settings and any rivets.
    static void Append(JsonObject into, JsonObject? intoRivets, JsonObject mesh, JsonObject? rivets, Matrix4x4 m)
    {
        var verts = into["vertices"]!.AsArray();
        int baseV = verts.Count / 3, baseE = into["edges"]!.AsArray().Count / 2, baseF = into["faces"]!.AsArray().Count;
        bool mirrored = m.GetDeterminant() < 0; // a mirrored placement turns faces inside out unless reversed
        var src = mesh["vertices"]!.AsArray().Select(x => x!.GetValue<float>()).ToArray();
        for (int i = 0; i < src.Length; i += 3)
        {
            var p = Vector3.Transform(new Vector3(src[i], src[i + 1], src[i + 2]), m);
            verts.Add((JsonNode?)p.X); verts.Add((JsonNode?)p.Y); verts.Add((JsonNode?)p.Z); // JsonNode casts, not generic Add<T>
        }
        foreach (var e in mesh["edges"]!.AsArray()) into["edges"]!.AsArray().Add((JsonNode?)(e!.GetValue<int>() + baseV));
        foreach (var f in mesh["edgeFlags"]!.AsArray()) into["edgeFlags"]!.AsArray().Add(Clone(f));
        var sizes = new List<int>();
        foreach (var face in mesh["faces"]!.AsArray())
        {
            var copy = Clone(face).AsObject();
            var v = copy["v"]!.AsArray().Select(x => x!.GetValue<int>() + baseV).ToList();
            var t = copy["t"]!.AsArray().Select(x => Clone(x)).ToList();
            int n = v.Count;
            sizes.Add(n);
            // Per corner (first four): thicken mode, a byte each, and thicken edge, 16 bits each (0xFFFF = none),
            // an index into this mesh's edges, so it moves up by the edges already there.
            int Corner(int k) => mirrored ? n - 1 - k : k;
            if (copy["tm"] != null)
            {
                ulong tm = (ulong)MeshCut.L(copy["tm"]), outTm = 0;
                for (int k = 0; k < Math.Min(n, 4); k++)
                    outTm |= (Corner(k) < 4 ? (tm >> (8 * Corner(k))) & 0xFF : 1) << (8 * k); // no setting stored: Auto
                copy["tm"] = (int)outTm;
            }
            if (copy["te"] != null)
            {
                ulong te = (ulong)MeshCut.L(copy["te"]), outTe = 0;
                for (int k = 0; k < 4; k++)
                {
                    if (k >= n) { outTe |= te & (0xFFFFUL << (16 * k)); continue; } // unused slot: as it was
                    ulong r = Corner(k) < 4 ? (te >> (16 * Corner(k))) & 0xFFFF : 0xFFFF;
                    if (r != 0xFFFF) r = r + (ulong)baseE < 0xFFFF ? r + (ulong)baseE : 0xFFFF;
                    outTe |= r << (16 * k);
                }
                copy["te"] = MeshCut.Te(outTe);
            }
            if (mirrored) { v.Reverse(); t.Reverse(); }
            copy["v"] = new JsonArray(v.Select(x => (JsonNode?)x).ToArray());
            copy["t"] = new JsonArray(t.ToArray());
            into["faces"]!.AsArray().Add(copy);
        }
        if (rivets == null || intoRivets == null) return;
        var profiles = intoRivets["profiles"]!.AsArray();
        int baseP = profiles.Count, baseN = intoRivets["nodes"]!.AsArray().Count;
        foreach (var p in rivets["profiles"]!.AsArray()) profiles.Add(Clone(p));
        foreach (var n in rivets["nodes"]!.AsArray())
        {
            var node = Clone(n).AsObject();
            if (mirrored) ReverseRivet(node, sizes[node["face"]!.GetValue<int>()]);
            node["face"] = node["face"]!.GetValue<int>() + baseF;
            node["profile"] = node["profile"]!.GetValue<int>() + baseP;
            foreach (var link in new[] { "next", "prev" })
                if (node[link]?.GetValue<int>() is int to && to >= 0) node[link] = to + baseN;
            intoRivets["nodes"]!.AsArray().Add(node);
        }
    }

    /// A rivet on a face whose corners were reversed (k -> n-1-k): the same spot, on the triangle of the same corners.
    static void ReverseRivet(JsonObject node, int n)
    {
        if (!MeshCut.RivetTriangles.TryGetValue(node["faceOffset"]?.GetValue<int>() ?? 0, out var old) || old.Max() >= n) return;
        var w = new[] { "u", "v", "w" }.Select(k => node[k]!.GetValue<float>()).ToArray();
        var corners = old.Select(k => n - 1 - k).ToHashSet();
        foreach (var (offset, tri) in MeshCut.RivetTriangles)
        {
            if (offset == 0 && n == 4 || !corners.SetEquals(tri)) continue; // quads use 1 for corners (0,1,2), as the game writes
            for (int j = 0; j < 3; j++) node["uvw"[j].ToString()] = w[System.Array.IndexOf(old, n - 1 - tri[j])];
            node["faceOffset"] = offset;
            return;
        }
    }

    /// A closed n-sided cylinder standing on y = 0, with the same face layout the game writes.
    internal static (JsonObject MeshData, double ArmourVolume) Cylinder(float r, float h, int n, int thicknessMm)
    {
        var verts = new List<Vector3>();
        for (int ring = 0; ring < 2; ring++)
            for (int i = 0; i < n; i++)
            {
                double a = 2 * Math.PI * i / n;
                verts.Add(new((float)(r * Math.Cos(a)), ring * h, (float)(r * Math.Sin(a))));
            }
        int bottom = verts.Count; verts.Add(new(0, 0, 0));
        int top = verts.Count; verts.Add(new(0, h, 0));
        var faces = new List<int[]>();
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            faces.Add(new[] { i, n + i, n + j, j });   // side, facing out
            faces.Add(new[] { top, n + j, n + i });     // top cap, facing up
            faces.Add(new[] { bottom, i, j });          // bottom cap, facing down
        }
        // Smooth across the sides (360/n degrees apart) but keep the 90-degree cap edges sharp.
        float smooth = n >= 6 ? 360f / n + 1 : 0;
        return (MeshData(verts, faces, thicknessMm, smooth), faces.Sum(f => Area(verts, f)) * thicknessMm / 1000.0);
    }

    static JsonObject MeshData(List<Vector3> verts, List<int[]> faces, int thicknessMm, float smoothAngle)
    {
        var edges = new List<(int, int)>();
        var seen = new HashSet<(int, int)>();
        foreach (var f in faces)
            for (int k = 0; k < f.Length; k++)
            {
                var e = (Math.Min(f[k], f[(k + 1) % f.Length]), Math.Max(f[k], f[(k + 1) % f.Length]));
                if (seen.Add(e)) edges.Add(e);
            }
        return new JsonObject
        {
            ["v"] = "0.2", ["name"] = null, ["smoothAngle"] = smoothAngle, ["gridSize"] = 1, ["format"] = "freeform",
            ["mesh"] = new JsonObject
            {
                ["majorVersion"] = 0, ["minorVersion"] = 3,
                ["vertices"] = new JsonArray(verts.SelectMany(v => new[] { v.X, v.Y, v.Z }).Select(x => (JsonNode?)x).ToArray()),
                ["edges"] = new JsonArray(edges.SelectMany(e => new[] { e.Item1, e.Item2 }).Select(x => (JsonNode?)x).ToArray()),
                ["edgeFlags"] = new JsonArray(edges.Select(_ => (JsonNode?)0).ToArray()),
                ["faces"] = new JsonArray(faces.Select(f => (JsonNode?)new JsonObject
                {
                    ["v"] = new JsonArray(f.Select(x => (JsonNode?)x).ToArray()),
                    ["t"] = new JsonArray(f.Select(_ => (JsonNode?)thicknessMm).ToArray()),
                    ["tm"] = f.Length == 4 ? 0x01010101 : 0x010101, // one byte per corner, as the game writes
                    ["te"] = 0,
                }).ToArray()),
            },
            ["rivets"] = new JsonObject
            {
                ["profiles"] = new JsonArray(new JsonObject { ["model"] = 0, ["spacing"] = 0.1, ["diameter"] = 0.05, ["height"] = 0.025, ["padding"] = 0.04 }),
                ["nodes"] = new JsonArray(),
            },
        };
    }

    static double Area(List<Vector3> v, int[] f)
    {
        var sum = Vector3.Zero;
        for (int k = 1; k < f.Length - 1; k++) sum += Vector3.Cross(v[f[k]] - v[f[0]], v[f[k + 1]] - v[f[0]]);
        return sum.Length() / 2;
    }

    internal static JsonObject Block(JsonArray blocks, int id) =>
        blocks.FirstOrDefault(x => x!["id"]?.GetValue<int>() == id)?.AsObject() ?? throw new Exception($"缺少设置块 {id}");

    internal static JsonObject? MeshOf(JsonArray meshes, int vuid) =>
        meshes.FirstOrDefault(m => m!["vuid"]?.GetValue<int>() == vuid)?["meshData"]?.AsObject();

    static bool IsTranslation(Matrix4x4 m) =>
        new[] { m.M11 - 1, m.M22 - 1, m.M33 - 1, m.M12, m.M13, m.M21, m.M23, m.M31, m.M32 }.All(x => Math.Abs(x) < 1e-5f);

    static int NextId(JsonArray list, string key) => list.Select(x => x![key]?.GetValue<int>() ?? 0).DefaultIfEmpty(0).Max() + 1;

    // JsonNode.DeepClone only exists from .NET 8; the game runs mods on .NET 6.
    static JsonNode Clone(JsonNode? node) => JsonNode.Parse(node!.ToJsonString())!;

    static JsonArray Array(params float[] values) => new(values.Select(v => (JsonNode?)v).ToArray());
}
