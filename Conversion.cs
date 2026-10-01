using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SprocketTools;

public record TurretChoice(int Id, int BodyId, string Label);
public record ConversionResult(string Json, int BodyId, int RemovedCount, int RetainedCount);

public static class Conversion
{
    public const string RingGuid = "99281776-6b29-4ffb-9d8b-04139ca7b6a2";
    public const string MotorGuid = "147d4042-4a13-4477-9adf-12e8291481e0";
    public const string AddonGuid = "8f8a9d20-eb45-482e-b149-014c964c4e2c";
    public const string CompartmentGuid = "7f8a9d20-eb45-482e-b149-014c964c4e2c";
    public const string DecalGuid = "e59ff736-a6ea-4a1a-a4c5-6437ed15b872";
    public static JsonObject Parse(string json) => JsonNode.Parse(json)?.AsObject() ?? throw new Exception("蓝图无效。");
    internal static int Id(JsonNode o, string key) => o[key]?.GetValue<int>() ?? throw new Exception($"蓝图缺少 {key} 字段。");
    internal static string GuidOf(JsonNode o) => o["guid"]?.GetValue<string>() ?? "";
    static int Body(JsonNode ring) => ring["structureID"]?.GetValue<int>() ?? ring["compartmentBodyID"]?["structureVuid"]?.GetValue<int>() ?? throw new Exception("缺少炮塔塔体引用。");
    public static Dictionary<int, JsonObject> Objects(JsonObject b) => (b["objects"]?.AsArray() ?? throw new Exception("请先在当前版本游戏中打开并重新保存该旧蓝图。"))
        .Select(x => x!.AsObject()).ToDictionary(x => Id(x, "vuid"));

    public static List<TurretChoice> List(string json)
    {
        var objects = Objects(Parse(json));
        return objects.Values.Where(o => GuidOf(o) == RingGuid).Select((o, i) =>
        {
            int ring = Id(o, "vuid");
            bool Under(JsonObject child)
            {
                var seen = new HashSet<int>();
                int parent = Id(child, "pvuid");
                while (parent >= 0 && objects.TryGetValue(parent, out var p))
                {
                    if (!seen.Add(parent)) throw new Exception("部件层级存在循环引用。");
                    if (parent == ring) return true;
                    parent = Id(p, "pvuid");
                }
                return false;
            }
            int guns = objects.Values.Count(c => c.ContainsKey("cannon") && Under(c));
            var pos = o["transform"]!["pos"]!;
            return new TurretChoice(ring, Body(o), $"炮塔 {i + 1}  |  {guns} 门炮  |  部件 {ring}  |  x {pos[0]!.GetValue<float>():0.00}, z {pos[2]!.GetValue<float>():0.00}");
        }).ToList();
    }

    public static ConversionResult Convert(string json, int ringId)
    {
        var b = Parse(json); // Work only on a new parsed copy, never the live vehicle.
        var objects = Objects(b);
        if (!objects.TryGetValue(ringId, out var ring) || GuidOf(ring) != RingGuid) throw new Exception("该炮塔已不存在。请刷新炮塔列表。");
        int bodyId = Body(ring), parentId = Id(ring, "pvuid");
        if (!objects.TryGetValue(bodyId, out var body) || Id(body, "pvuid") != ringId) throw new Exception("不支持的炮塔塔体层级结构；原设计未改动。");
        if (!objects.ContainsKey(parentId)) throw new Exception("该炮塔没有承载它的车辆部件。");
        var before = WorldMatrices(objects);
        if (!Matrix4x4.Invert(before[parentId], out var inverseParent)) throw new Exception("父级变换矩阵不可逆。");
        var removed = objects.Values.Where(o => Id(o,"pvuid") == bodyId && GuidOf(o) == MotorGuid).Select(o => Id(o,"vuid")).ToHashSet();
        removed.Add(ringId);
        foreach (var o in objects.Values.Where(o => !removed.Contains(Id(o,"vuid"))))
        {
            int id = Id(o,"vuid"), parent = Id(o,"pvuid");
            bool move = id == bodyId || removed.Contains(parent) ||
                (parent == bodyId && GuidOf(o) != AddonGuid && GuidOf(o) != DecalGuid);
            if (!move) continue;
            WriteTransform(o["transform"]!.AsObject(), before[id] * inverseParent);
            o["pvuid"] = parentId;
        }
        body["guid"] = AddonGuid;
        bool BlockKey(string k) => k.EndsWith("BlueprintVuid") || k.EndsWith("ConstraintsVuid");
        var gone = removed.Select(id => objects[id]).ToArray();
        var survivors = objects.Values.Where(o => !removed.Contains(Id(o,"vuid"))).ToArray();
        var deadComponents = gone.SelectMany(o => o.Where(kv => !BlockKey(kv.Key) && kv.Key is not ("vuid" or "pvuid" or "flags" or "structureID") && kv.Value is JsonValue v && v.TryGetValue<int>(out _)))
            .Select(kv => kv.Value!.GetValue<int>()).ToHashSet();
        var liveBlocks = survivors.SelectMany(o => o.Where(kv => BlockKey(kv.Key))).Select(kv => kv.Value!.GetValue<int>()).ToHashSet();
        var deadBlocks = gone.SelectMany(o => o.Where(kv => BlockKey(kv.Key))).Select(kv => kv.Value!.GetValue<int>()).Where(v => !liveBlocks.Contains(v)).ToHashSet();
        var objectArray = b["objects"]!.AsArray();
        for (int i = objectArray.Count - 1; i >= 0; --i) if (removed.Contains(Id(objectArray[i]!,"vuid"))) objectArray.RemoveAt(i);
        var blocks = b["blueprints"]!.AsArray();
        for (int i = blocks.Count - 1; i >= 0; --i)
        {
            var block = blocks[i]!;
            if (deadBlocks.Contains(Id(block,"id"))) { blocks.RemoveAt(i); continue; }
            // This is an actual component-reference field. Never filter arbitrary integer
            // arrays (mesh indices, paint IDs, etc. can overlap component numbers).
            if (block["blueprint"]?["operatedBehaviours"] is JsonArray operated)
                for (int j = operated.Count - 1; j >= 0; --j)
                    if (operated[j] is JsonValue v && v.TryGetValue<int>(out int refId) && deadComponents.Contains(refId)) operated.RemoveAt(j);
        }
        foreach (var o in survivors)
            if (o["transform"]?["mirrorVuid"] is JsonValue m && m.TryGetValue<int>(out int mirror) && removed.Contains(mirror)) AddonEdits.Unlink(o);
        var after = WorldMatrices(Objects(b));
        foreach (var (id, matrix) in after)
            if (!Near(before[id], matrix)) throw new Exception($"部件 {id} 会发生位移或形变；转换已取消。");
        var name = b["header"]?["name"]?.GetValue<string>() ?? "Vehicle";
        while (name.EndsWith(" (Addon)")) name = name[..^" (Addon)".Length]; // one suffix, even after several conversions
        b["header"]!["name"] = name + " (Addon)";
        return new ConversionResult(b.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), bodyId, removed.Count, survivors.Length);
    }

    /// A part's own numbers (its components: "cannon", "turretRing", ...), keyed by name; not links to other parts or settings.
    static IEnumerable<string> ComponentKeys(JsonObject o) => o.Where(kv => kv.Value is JsonValue v && v.TryGetValue<int>(out _) && char.IsLetter(kv.Key[0])
        && kv.Key is not ("vuid" or "pvuid" or "flags" or "structureID") && !kv.Key.EndsWith("Vuid") && !kv.Key.EndsWith("ID")).Select(kv => kv.Key).ToList();

    // Ring settings can be shared between mirror twins, but the motor is a component reference, not a setting.
    // Clone before remapping so changing the twin never redirects the original ring as well.
    static void RemapRingMotor(JsonObject ring, JsonArray blocks, IReadOnlyDictionary<int, int> map, ref int nextBlock)
    {
        if (GuidOf(ring) != RingGuid || ring["ringBlueprintVuid"] is not JsonValue slot) return;
        var block = blocks.First(x => Id(x!, "id") == slot.GetValue<int>())!;
        if (block["blueprint"]?["motorVuid"] is not JsonValue motor || !map.TryGetValue(motor.GetValue<int>(), out int replacement)
            || replacement == motor.GetValue<int>()) return;
        var own = JsonNode.Parse(block.ToJsonString())!.AsObject();
        own["id"] = nextBlock;
        own["blueprint"]!["motorVuid"] = replacement;
        blocks.Add(own);
        ring["ringBlueprintVuid"] = nextBlock++;
    }

    /// Repair existing explicit mirror pairs using their own motor's parent hierarchy. Ambiguous/missing motors
    /// are left alone; proximity is not ownership, and nested turrets must keep their own drive.
    public static (string Json, int Repaired, int Unresolved) RepairMirroredTurretDrives(string json)
    {
        var b = Parse(json);
        var objects = Objects(b);
        var blocks = b["blueprints"]!.AsArray();
        int nextBlock = blocks.Select(x => Id(x!, "id")).DefaultIfEmpty(0).Max() + 1;
        int Owner(JsonObject motor)
        {
            var seen = new HashSet<int>();
            for (int p = Id(motor, "pvuid"); objects.TryGetValue(p, out var parent); p = Id(parent, "pvuid"))
            {
                if (!seen.Add(p)) throw new Exception("部件层级存在循环引用。");
                if (GuidOf(parent) == RingGuid) return p;
            }
            return -1;
        }
        var motors = objects.Values.Where(o => GuidOf(o) == MotorGuid && o["motor"] is JsonValue)
            .GroupBy(Owner).ToDictionary(g => g.Key, g => g.Select(o => Id(o, "motor")).ToArray());
        int Motor(JsonObject ring) => blocks.First(x => Id(x!, "id") == Id(ring, "ringBlueprintVuid"))!["blueprint"]?["motorVuid"]?.GetValue<int>() ?? -1;
        int repaired = 0, unresolved = 0;
        foreach (var ring in objects.Values.Where(o => GuidOf(o) == RingGuid))
        {
            int id = Id(ring, "vuid");
            if (ring["transform"]?["mirrorVuid"]?.GetValue<int>() is not int twinId || twinId <= id || !objects.TryGetValue(twinId, out var twin)
                || GuidOf(twin) != RingGuid || twin["transform"]?["mirrorVuid"]?.GetValue<int>() != id) continue;
            var left = motors.GetValueOrDefault(id) ?? Array.Empty<int>();
            var right = motors.GetValueOrDefault(twinId) ?? Array.Empty<int>();
            if (left.Length != 1 || right.Length != 1 || left[0] == right[0]) { unresolved++; continue; }
            int oldLeft = Motor(ring), oldRight = Motor(twin);
            // Only redirect a reference to this pair's drives. An intentional external connection is untouched.
            if (oldLeft != left[0] && oldLeft == right[0])
            { RemapRingMotor(ring, blocks, new Dictionary<int, int> { [oldLeft] = left[0] }, ref nextBlock); repaired++; }
            if (oldRight != right[0] && oldRight == left[0])
            { RemapRingMotor(twin, blocks, new Dictionary<int, int> { [oldRight] = right[0] }, ref nextBlock); repaired++; }
        }
        return (repaired == 0 ? json : b.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), repaired, unresolved);
    }

    /// Fills a mirrored turret's twin: the game's Mirror copies only the ring, so every part on ring `ringId` (turret body,
    /// guns, seats, decals, ...) gets a mirrored copy on the twin ring, linked to it as a mirror pair, as Mirror places
    /// parts (a body pair shares its shape, so later edits go to both). If the ring is saved once, marked mirrored (the
    /// game draws its twin itself), the parts on it are marked the same way instead.
    public static (string Json, int Mirrored, string How) MirrorTurret(string json, int ringId)
    {
        var b = Parse(json);
        var objects = Objects(b);
        if (!objects.TryGetValue(ringId, out var ring) || GuidOf(ring) != RingGuid) throw new Exception("该炮塔已不存在。");
        var children = objects.Values.GroupBy(o => Id(o, "pvuid")).ToDictionary(g => g.Key, g => g.Select(o => Id(o, "vuid")).ToList());
        List<int> Below(int top)
        {
            var found = new List<int>();
            for (var queue = new Queue<int>(new[] { top }); queue.Count > 0;)
                foreach (int c in children.GetValueOrDefault(queue.Dequeue()) ?? new()) { found.Add(c); queue.Enqueue(c); }
            return found;
        }
        int Flags(JsonObject o) => o["flags"]?.GetValue<int>() ?? 0;
        int? TwinOf(JsonObject o) => o["transform"]?["mirrorVuid"]?.GetValue<int>() is int m && m != Id(o, "vuid") && objects.ContainsKey(m) ? m : null;
        var parts = Below(ringId);
        if (parts.Count == 0) throw new Exception("该炮塔上还没有可镜像的内容。");

        if (TwinOf(ring) is not int twinRing)
        {
            if ((Flags(ring) & 4) == 0) throw new Exception("该炮塔没有镜像对应体：请在开启镜像时放置它。");
            int marked = 0;
            foreach (int v in parts)
                if (TwinOf(objects[v]) == null && (Flags(objects[v]) & 4) == 0) { objects[v]["flags"] = Flags(objects[v]) | 4; marked++; }
            return (b.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), marked, "marked mirrored");
        }

        // New numbers for each copy and its components; the ring's map to the twin ring's.
        int next = objects.Values.SelectMany(o => o.Where(kv => kv.Value is JsonValue v && v.TryGetValue<int>(out _) && kv.Key is not ("pvuid" or "flags"))
                                                   .Select(kv => kv.Value!.GetValue<int>())).DefaultIfEmpty(0).Max() + 1;
        var map = new Dictionary<int, int> { [ringId] = twinRing };
        foreach (var key in ComponentKeys(ring)) if (objects[twinRing][key] is JsonValue tv) map[Id(ring, key)] = tv.GetValue<int>();
        var onTwin = Below(twinRing).ToHashSet();
        var copy = new List<int>();
        foreach (int v in parts)
        {
            var o = objects[v];
            if (TwinOf(o) is int t && onTwin.Contains(t))
            {
                map[v] = t; // already mirrored onto the twin: references still need its component numbers
                foreach (var key in ComponentKeys(o)) if (objects[t][key] is JsonValue component) map[Id(o, key)] = component.GetValue<int>();
                continue;
            }
            copy.Add(v);
            map[v] = next++;
            foreach (var key in ComponentKeys(o)) map[Id(o, key)] = next++;
        }
        if (copy.Count == 0)
        {
            var repair = RepairMirroredTurretDrives(json);
            if (repair.Repaired > 0) return (repair.Json, repair.Repaired, "drive connections repaired");
            throw new Exception("该炮塔上的内容在其对应体上已经齐全。");
        }

        var blocks = b["blueprints"]!.AsArray();
        int nextBlock = blocks.Select(x => x!["id"]?.GetValue<int>() ?? 0).DefaultIfEmpty(0).Max() + 1;
        var list = b["objects"]!.AsArray();
        RemapRingMotor(objects[twinRing], blocks, map, ref nextBlock);
        foreach (int v in copy)
        {
            var o = objects[v];
            var d = JsonNode.Parse(o.ToJsonString())!.AsObject();
            d["vuid"] = map[v];
            d["pvuid"] = map.TryGetValue(Id(o, "pvuid"), out int p) ? p : Id(o, "pvuid");
            foreach (var key in ComponentKeys(o)) d[key] = map[Id(o, key)];
            RemapRingMotor(d, blocks, map, ref nextBlock);
            if (o["structureID"] is JsonValue s && map.TryGetValue(s.GetValue<int>(), out int body)) d["structureID"] = body;
            // Mirrored across the vehicle's centre: seen from its (mirrored) parent, x the other way and the turn mirrored.
            var t = d["transform"]!.AsObject();
            var pos = t["pos"]!.AsArray();
            pos[0] = -pos[0]!.GetValue<float>();
            var rot = t["rot"]!.AsArray();
            rot[1] = -rot[1]!.GetValue<float>();
            rot[2] = -rot[2]!.GetValue<float>();
            // Settings that name other parts (a seat's guns, a gun's barrels) get their own copy naming the copies.
            foreach (var key in d.Where(kv => kv.Key.EndsWith("BlueprintVuid")).Select(kv => kv.Key).ToList())
            {
                var block = blocks.First(x => x!["id"]?.GetValue<int>() == d[key]!.GetValue<int>())!;
                var named = block["blueprint"]?.AsObject().Where(kv => kv.Key is "operatedBehaviours" or "barrelVuids" && kv.Value is JsonArray).Select(kv => kv.Key).ToList() ?? new();
                if (named.Count == 0) continue;
                var own = JsonNode.Parse(block.ToJsonString())!.AsObject();
                own["id"] = nextBlock;
                foreach (var name in named)
                {
                    var ids = own["blueprint"]![name]!.AsArray();
                    for (int i = 0; i < ids.Count; i++) if (ids[i] is JsonValue iv && map.TryGetValue(iv.GetValue<int>(), out int to)) ids[i] = to;
                }
                blocks.Add(own);
                d[key] = nextBlock++;
            }
            if (TwinOf(o) == null)
            {
                t["mirrorVuid"] = v;
                o["transform"]!["mirrorVuid"] = map[v];
                d["flags"] = (Flags(o) ^ 1) | 4;
                o["flags"] = Flags(o) | 4;
            }
            else
            {
                t["mirrorVuid"] = -1; // mirrored inside its own turret already: its copy stands on its own
                d["flags"] = (Flags(o) ^ 1) & ~4;
            }
            list.Add(d);
        }
        // The twin ring's turret body is the copy of this ring's.
        if (ring["structureID"] is JsonValue rb && map.TryGetValue(rb.GetValue<int>(), out int twinBody)) objects[twinRing]["structureID"] = twinBody;
        if (ring["compartmentBodyID"]?["structureVuid"] is JsonValue cb && map.TryGetValue(cb.GetValue<int>(), out int twinCompartment) && objects[twinRing]["compartmentBodyID"] is JsonObject tc)
            tc["structureVuid"] = twinCompartment;

        // Each copy must show as the mirror image of its part, or the twin ring isn't a true mirror of this one.
        var world = WorldMatrices(Objects(b));
        var flip = Matrix4x4.CreateScale(-1, 1, 1);
        Matrix4x4 Shape(JsonObject o) => ((o["flags"]?.GetValue<int>() ?? 0) & 1) != 0 ? flip * world[Id(o, "vuid")] : world[Id(o, "vuid")];
        var all = Objects(b);
        foreach (int v in copy)
            if (!Near(Shape(all[v]) * flip, Shape(all[map[v]]))) throw new Exception($"对应炮塔与本炮塔不成镜像（部件 {v}）；未作改动。");
        return (b.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), copy.Count, "copied onto the twin ring");
    }

    public static Matrix4x4 Local(JsonNode transform, bool scaled = true)
    {
        Vector3 Vec(string k) => new(transform[k]![0]!.GetValue<float>(), transform[k]![1]!.GetValue<float>(), transform[k]![2]!.GetValue<float>());
        var r = Vec("rot") * (MathF.PI / 180f);
        // Unity Euler order: Z, then X, then Y. System.Numerics uses row vectors.
        return Matrix4x4.CreateScale(scaled ? Vec("scale") : Vector3.One) * Matrix4x4.CreateRotationZ(r.Z) * Matrix4x4.CreateRotationX(r.X) * Matrix4x4.CreateRotationY(r.Y) * Matrix4x4.CreateTranslation(Vec("pos"));
    }
    public static Dictionary<int, Matrix4x4> WorldMatrices(Dictionary<int, JsonObject> objects)
    {
        var cache = new Dictionary<int, Matrix4x4>();
        var frames = new Dictionary<int, Matrix4x4>();
        var visiting = new HashSet<int>();
        Matrix4x4 Get(int id)
        {
            if (cache.TryGetValue(id,out var ready)) return ready;
            if (!visiting.Add(id)) throw new Exception("蓝图层级存在循环引用。");
            if (!objects.TryGetValue(id,out var o)) throw new Exception($"缺少父级部件 {id}。");
            int parent = Id(o,"pvuid");
            var matrix = Local(o["transform"]!) * (parent < 0 ? Matrix4x4.Identity : Frame(parent));
            visiting.Remove(id); cache[id] = matrix; return matrix;
        }
        // What parts on `id` hang from: the part itself, but a mantlet's scale sizes the mantlet only (the game puts a tank
        // built on a mantlet scaled 2.77 where unscaled maths does, not 2.77 times as far out).
        Matrix4x4 Frame(int id)
        {
            if (frames.TryGetValue(id, out var ready)) return ready;
            var o = objects.TryGetValue(id, out var found) ? found : throw new Exception($"缺少父级部件 {id}。");
            int parent = Id(o, "pvuid");
            return frames[id] = o.ContainsKey("mantlet") ? Local(o["transform"]!, scaled: false) * (parent < 0 ? Matrix4x4.Identity : Frame(parent)) : Get(id);
        }
        foreach (int id in objects.Keys) Get(id);
        return cache;
    }
    internal static void WriteTransform(JsonObject transform, Matrix4x4 matrix)
    {
        if (!Matrix4x4.Decompose(matrix, out var s, out var q, out var p)) throw new Exception("该炮塔经过缩放，转换会产生形变，无法转换。");
        var r = Matrix4x4.CreateFromQuaternion(q);
        float x = MathF.Asin(Math.Clamp(-r.M32,-1,1));
        float y = Math.Abs(r.M32) < 0.999999f ? MathF.Atan2(r.M31,r.M33) : MathF.Atan2(-r.M13,r.M11);
        float z = Math.Abs(r.M32) < 0.999999f ? MathF.Atan2(r.M12,r.M22) : 0;
        JsonArray Array(params float[] values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
        var oldRot = transform["rot"]!.AsArray();
        var newRot = Array(x * 180/MathF.PI, y * 180/MathF.PI, z * 180/MathF.PI);
        for (int i=3;i<oldRot.Count;i++) newRot.Add(JsonNode.Parse(oldRot[i]!.ToJsonString()));
        transform["pos"] = Array(p.X,p.Y,p.Z); transform["rot"] = newRot; transform["scale"] = Array(s.X,s.Y,s.Z);
        if (!Near(matrix, Local(transform))) throw new Exception("非均匀缩放会产生剪切变形；原设计未改动。");
    }
    public static bool Near(Matrix4x4 a, Matrix4x4 b)
    {
        float[] Values(Matrix4x4 m) => new[] {m.M11,m.M12,m.M13,m.M14,m.M21,m.M22,m.M23,m.M24,m.M31,m.M32,m.M33,m.M34,m.M41,m.M42,m.M43,m.M44};
        return Values(a).Zip(Values(b)).All(v => float.IsFinite(v.First) && float.IsFinite(v.Second) && Math.Abs(v.First-v.Second) < 0.001f);
    }
}
