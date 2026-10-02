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
    internal static IEnumerable<string> ComponentKeys(JsonObject o) => o.Where(kv => kv.Value is JsonValue v && v.TryGetValue<int>(out _) && char.IsLetter(kv.Key[0])
        && kv.Key is not ("vuid" or "pvuid" or "flags" or "structureID") && !NamesBlock(kv.Key) && !kv.Key.EndsWith("Vuid") && !kv.Key.EndsWith("ID")).Select(kv => kv.Key).ToList();

    // Settings blocks name other parts by their component numbers. Only these fields do; every other integer in a block
    // is a setting, and settings numbers collide with component numbers by design (ringThickness 5, ratio 50, priority 5).
    internal static readonly string[] PartNumbers = { "motorVuid", "sightVuid", "linkedCannonVuid" };
    internal static readonly string[] PartNumberLists = { "operatedBehaviours", "barrelVuids" };

    /// Which of a part's own numbers name a settings block rather than a part. Named by field, never by value: block ids,
    /// component numbers and part numbers are three ranges that overlap freely. Measured over the community blueprints:
    /// every `traverseConstraintsVuid` (a traverse motor's limits) is a block id, while it looks exactly like a part link.
    /// `powertrainSteeringControls` appears once per design (the driver's compartment) and every time names a block.
    internal static bool NamesBlock(string key) => key.EndsWith("BlueprintVuid") || key.EndsWith("ConstraintsVuid") || key == "powertrainSteeringControls";

    /// A part also names other parts from inside its own nested settings: a mantlet's `mantletBlueprint` dictionary holds
    /// its laying drive and trunnions (component numbers of parts next to it) and sometimes its shield (a part number).
    /// Both go through the same map, so the copy needs no knowing which of the two it was — only that it is a link.
    /// `mirrorVuid` and `structureVuid` are links too but must be able to stay behind, so the mirror/turret body paths
    /// handle them on their own.
    static bool NamesNestedPart(string key) => !NamesBlock(key) && key is not ("vuid" or "pvuid" or "mirrorVuid" or "structureVuid")
        && key.EndsWith("Vuid", StringComparison.OrdinalIgnoreCase);

    static IEnumerable<(string Field, int At)> NestedLinksOf(JsonObject part)
    {
        foreach (var (name, value) in part)
            if (value is JsonObject nested)
                foreach (var (key, at) in nested)
                {
                    if (!NamesNestedPart(key)) continue;
                    if (at is JsonValue one && one.TryGetValue<int>(out int number) && number > 0) yield return ($"{name}.{key}", number);
                    if (at is JsonArray list) foreach (var item in list) if (item is JsonValue each && each.TryGetValue<int>(out int member) && member > 0) yield return ($"{name}.{key}[]", member);
                }
    }

    internal static void RemapNestedLinks(JsonObject part, Func<int, int> resolve)
    {
        foreach (var value in part.Select(kv => kv.Value).OfType<JsonObject>())
            foreach (var (key, at) in value.ToList())
            {
                if (!NamesNestedPart(key)) continue;
                if (at is JsonValue one && one.TryGetValue<int>(out int number) && number > 0) value[key] = resolve(number);
                if (at is JsonArray list)
                    for (int i = 0; i < list.Count; i++) if (list[i] is JsonValue item && item.TryGetValue<int>(out int member) && member > 0) list[i] = resolve(member);
            }
    }
    static bool NamesParts(JsonObject settings) => NamedParts(settings).Any();
    internal static IEnumerable<int> NamedParts(JsonObject settings)
    {
        foreach (var name in PartNumbers) if (settings[name] is JsonValue one && one.TryGetValue<int>(out int at) && at > 0) yield return at;
        foreach (var name in PartNumberLists) if (settings[name] is JsonArray list) foreach (var x in list) if (x is JsonValue iv && iv.TryGetValue<int>(out int at) && at > 0) yield return at;
    }

    internal static void RemapNamedParts(JsonObject settings, IReadOnlyDictionary<int, int> map)
    {
        foreach (var name in PartNumbers)
            if (settings[name] is JsonValue one && one.TryGetValue<int>(out int at) && map.TryGetValue(at, out int to)) settings[name] = to;
        foreach (var name in PartNumberLists)
            if (settings[name] is JsonArray list)
                for (int i = 0; i < list.Count; i++) if (list[i] is JsonValue iv && iv.TryGetValue<int>(out int at) && map.TryGetValue(at, out int to)) list[i] = to;
    }

    static Dictionary<int, List<int>> ChildrenByParent(Dictionary<int, JsonObject> objects) =>
        objects.Values.GroupBy(o => Id(o, "pvuid")).ToDictionary(g => g.Key, g => g.Select(o => Id(o, "vuid")).ToList());

    /// Every part hanging under `top` (the top itself excluded), nearest first.
    static List<int> Below(Dictionary<int, List<int>> children, int top)
    {
        var found = new List<int>();
        for (var queue = new Queue<int>(new[] { top }); queue.Count > 0;)
            foreach (int c in children.GetValueOrDefault(queue.Dequeue()) ?? new()) { found.Add(c); queue.Enqueue(c); }
        return found;
    }

    /// Above every number a part or one of its components already uses: the next free one for copies.
    internal static int HighestNumber(Dictionary<int, JsonObject> objects) =>
        objects.Values.SelectMany(o => o.Where(kv => kv.Value is JsonValue v && v.TryGetValue<int>(out _) && kv.Key is not ("pvuid" or "flags"))
                                       .Select(kv => kv.Value!.GetValue<int>())).DefaultIfEmpty(0).Max();

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
        var children = ChildrenByParent(objects);
        int Flags(JsonObject o) => o["flags"]?.GetValue<int>() ?? 0;
        int? TwinOf(JsonObject o) => o["transform"]?["mirrorVuid"]?.GetValue<int>() is int m && m != Id(o, "vuid") && objects.ContainsKey(m) ? m : null;
        var parts = Below(children, ringId);
        if (parts.Count == 0) throw new Exception("该炮塔上还没有可镜像的内容。");

        if (TwinOf(ring) is not int twinRing)
        {
            if ((Flags(ring) & 4) == 0) throw new Exception("该炮塔没有镜像对应体：请在开启镜像时放置它。");
            int marked = 0;
            foreach (int v in parts)
                if (TwinOf(objects[v]) == null && (Flags(objects[v]) & 4) == 0) { objects[v]["flags"] = Flags(objects[v]) | 4; marked++; }
            return (b.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), marked, "marked mirrored");
        }
        if (GuidOf(objects[twinRing]) != RingGuid || TwinOf(objects[twinRing]) != ringId)
            throw new Exception("该炮塔的镜像链接并非互为镜像的炮塔对；原设计未改动。");

        // New numbers for each copy and its components; the ring's map to the twin ring's.
        int next = HighestNumber(objects) + 1;
        var map = new Dictionary<int, int> { [ringId] = twinRing };
        foreach (var key in ComponentKeys(ring)) if (objects[twinRing][key] is JsonValue tv) map[Id(ring, key)] = tv.GetValue<int>();
        var onTwin = Below(children, twinRing).ToHashSet();
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
            if (o["compartmentBodyID"]?["structureVuid"] is JsonValue nestedBody && map.TryGetValue(nestedBody.GetValue<int>(), out int copiedBody))
                d["compartmentBodyID"]!["structureVuid"] = copiedBody;
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
                if (block["blueprint"]?.AsObject() is not { } settings || !NamesParts(settings)) continue;
                var own = Clone(block).AsObject();
                own["id"] = nextBlock;
                RemapNamedParts(own["blueprint"]!.AsObject(), map);
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
        // A partial twin may already have both drives with either ring on the other one's motor, so repair those too.
        var drives = RepairMirroredTurretDrives(b.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return (drives.Json, copy.Count, drives.Repaired == 0 ? "copied onto the twin ring" : "copied onto the twin ring; drive connections repaired");
    }

    /// Fills the bare ring that Alt-copying a turret ring leaves behind. The game duplicates only the ring it was told
    /// to (its own parts and settings blocks, dropped where the user put it) and never fixes the two links a ring names
    /// by number, so the copy goes on naming the original's traverse motor and turret body: two rings then claim one
    /// drive, the rebuild gives the copy no turret behaviour, and leaving the editor crashes in the game's own audio
    /// stage. So the turret that stands on the original ring is copied here instead, at the design level, onto the
    /// copy's ring: every part with its own numbers, its own settings and its own shape, hung from the copy ring with
    /// the same local transforms it has on the original, and the copy ring left naming its own body and its own drive.
    public static (string Json, int Copied, string How) CopyTurret(string json, int sourceRingId, int copyRingId)
    {
        var b = Parse(json);
        var objects = Objects(b);
        if (!objects.TryGetValue(sourceRingId, out var source) || GuidOf(source) != RingGuid) throw new Exception("被复制的炮塔已不存在。");
        if (!objects.TryGetValue(copyRingId, out var copy) || GuidOf(copy) != RingGuid) throw new Exception("副本齿圈已不存在。");
        var children = ChildrenByParent(objects);
        if (Below(children, copyRingId).Count != 0) throw new Exception("副本齿圈上已经有零件；原设计未改动。");
        var parts = Below(children, sourceRingId);
        if (parts.Count == 0) throw new Exception("被复制的炮塔上还没有内容。");

        // Which part owns each component number: a settings value that happens to match one must never be taken for a
        // link, and a turret that names parts outside itself (a seat linked to a hull gun) would take those links along.
        var owner = new Dictionary<int, int>();
        foreach (var o in objects.Values)
            foreach (var key in ComponentKeys(o)) owner[Id(o, key)] = Id(o, "vuid");
        var onSource = parts.ToHashSet();
        onSource.Add(sourceRingId);
        var theirs = onSource.Concat(owner.Where(kv => onSource.Contains(kv.Value)).Select(kv => kv.Key)).ToHashSet();
        foreach (int v in onSource)
            foreach (var (field, at) in LinksOf(objects[v]))
                if (at > 0 && !theirs.Contains(at) && !(v == sourceRingId && field == "pvuid"))
                    throw new Exception($"这个炮塔的部件 {v} 通过 {field} 引用了塔外的 {at}，复制会连带那个零件；暂不支持。");

        // The numbers the game already gave the copy ring stand; everything it left behind gets a fresh one.
        var map = new Dictionary<int, int> { [sourceRingId] = copyRingId };
        foreach (var key in ComponentKeys(source))
            if (copy[key] is JsonValue has && has.TryGetValue<int>(out int already) && already > 0) map[Id(source, key)] = already;
        int next = HighestNumber(objects) + 1;
        foreach (int v in parts)
        {
            map[v] = next++;
            foreach (var key in ComponentKeys(objects[v])) map[Id(objects[v], key)] = next++;
        }
        int Resolve(int at) => map.TryGetValue(at, out int to) ? to : throw new Exception($"部件 {at} 没有对应的副本；原设计未改动。");

        var blocks = b["blueprints"]!.AsArray();
        var blockById = blocks.ToDictionary(x => Id(x!, "id"), x => x!.AsObject());
        int nextBlock = blockById.Keys.DefaultIfEmpty(0).Max() + 1;
        var meshes = b["meshes"]!.AsArray();
        int nextMesh = meshes.Select(m => m!["vuid"]!.GetValue<int>()).DefaultIfEmpty(0).Max() + 1;
        // Settings blocks the copy ring already owns (the game made its own ring and basket ones) are used as they are;
        // one is only reused when nothing but the copy refers to it, so the two turrets never share a setting by accident.
        var blockMap = new Dictionary<int, int>();
        foreach (var kv in source.Where(kv => NamesBlock(kv.Key)).ToList())
        {
            if (kv.Value is not JsonValue original || !original.TryGetValue<int>(out int from)) continue;
            if (copy[kv.Key] is not JsonValue at || !at.TryGetValue<int>(out int cid) || !blockById.ContainsKey(cid)) continue;
            if (!blockById.TryGetValue(from, out var setting) || setting["type"]?.GetValue<string>() != blockById[cid]["type"]?.GetValue<string>()) continue;
            if (objects.Values.Any(o => Id(o, "vuid") != copyRingId && o.Any(n => NamesBlock(n.Key) && n.Value is JsonValue v && v.TryGetValue<int>(out int x) && x == cid))) continue;
            blockMap[from] = cid;
        }

        var list = b["objects"]!.AsArray();
        foreach (int v in parts)
        {
            var o = objects[v];
            var d = Clone(o);
            d["vuid"] = map[v];
            d["pvuid"] = Id(o, "pvuid") == sourceRingId ? copyRingId : Resolve(Id(o, "pvuid"));
            foreach (var key in ComponentKeys(o)) d[key] = Resolve(Id(o, key));
            if (o["structureID"] is JsonValue shell && shell.TryGetValue<int>(out int body) && body > 0) d["structureID"] = Resolve(body);
            if (o["compartmentBodyID"]?.AsObject()["structureVuid"] is JsonValue nested && nested.TryGetValue<int>(out int inner) && inner > 0 && d["compartmentBodyID"] is JsonObject compartment)
                compartment["structureVuid"] = Resolve(inner);
            RemapNestedLinks(d, Resolve); // and what its own nested settings name: a mantlet's drive, trunnions, shield
            // Its settings name parts, and a shape is only shared on purpose (mirror twins); a copy needs its own.
            foreach (var key in d.Where(kv => NamesBlock(kv.Key)).Select(kv => kv.Key).ToList())
            {
                int from = d[key]!.GetValue<int>();
                if (blockMap.TryGetValue(from, out int reuse)) { d[key] = reuse; continue; }
                if (!blockById.TryGetValue(from, out var setting)) throw new Exception($"蓝图缺少设置 {from}。");
                var own = Clone(setting);
                own["id"] = nextBlock;
                blockById[nextBlock] = own;
                blockMap[from] = nextBlock;
                d[key] = nextBlock;
                if (own["type"]?.GetValue<string>() == "structure") nextMesh = OwnShape(own, meshes, nextMesh);
                blocks.Add(own);
                nextBlock++;
            }
            if (d["transform"]?.AsObject() is { } transform && transform["mirrorVuid"] is JsonValue mv && mv.TryGetValue<int>(out int mirror) && mirror > 0)
                transform["mirrorVuid"] = map.TryGetValue(mirror, out int twin) ? twin : -1; // its mirror image does not belong to this turret
            list.Add(d);
        }
        if (source["structureID"] is JsonValue rb && rb.TryGetValue<int>(out int itsBody) && itsBody > 0) copy["structureID"] = Resolve(itsBody);
        if (source["compartmentBodyID"]?.AsObject()["structureVuid"] is JsonValue rc && rc.TryGetValue<int>(out int itsShell) && itsShell > 0 && copy["compartmentBodyID"] is JsonObject itsInner)
            itsInner["structureVuid"] = Resolve(itsShell);
        foreach (int id in blockMap.Values.Distinct().ToList()) // and the copy's settings must name the copy's own parts
            if (blockById[id]["blueprint"]?.AsObject() is { } settings) RemapNamedParts(settings, map);

        // Then check the result before anything is written: nothing on the copy may name the turret it came from,
        // every part it holds must have come along, and each one must sit where the copy's own ring puts it.
        var after = Objects(b);
        var filled = Below(ChildrenByParent(after), copyRingId);
        if (filled.Count != parts.Count) throw new Exception($"副本齿圈上只有 {filled.Count} 件，应为 {parts.Count} 件；原设计未改动。");
        var ours = blockMap.Values.ToHashSet();
        var ourNumbers = filled.Append(copyRingId).SelectMany(v => new[] { v }.Concat(ComponentKeys(after[v]).Select(k => Id(after[v], k)))).ToHashSet();
        foreach (int v in filled.Append(copyRingId))
        {
            foreach (var (field, at) in LinksOf(after[v]))
                if (at > 0 && !ourNumbers.Contains(at) && !(v == copyRingId && field == "pvuid"))
                    throw new Exception($"副本的部件 {v} 仍通过 {field} 引用原炮塔的 {at}；原设计未改动。");
            foreach (var kv in after[v].Where(kv => NamesBlock(kv.Key)))
                if (!ours.Contains(kv.Value!.GetValue<int>())) throw new Exception($"副本的部件 {v} 仍共用原炮塔的设置 {kv.Value}；原设计未改动。");
        }
        foreach (int id in ours)
            if (blockById[id]["blueprint"]?.AsObject() is { } settings)
                foreach (int at in NamedParts(settings))
                    // Only the turret's own parts had to come along. A seat here that drives the hull's steering keeps
                    // driving it, exactly as the mirror does: numbers outside the source turret stay as they were.
                    if (!ourNumbers.Contains(at) && theirs.Contains(at)) throw new Exception($"副本的设置 {id} 仍引用原炮塔的 {at}；原设计未改动。");
        var world = WorldMatrices(after);
        if (!Matrix4x4.Invert(world[sourceRingId], out var back)) throw new Exception("原齿圈的变换不可逆。");
        var move = back * world[copyRingId];
        foreach (int v in parts)
            if (!Near(world[v] * move, world[map[v]])) throw new Exception($"副本的部件 {v} 会移位或形变；原设计未改动。");
        return (b.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), parts.Count,
            $"ring {sourceRingId} → {copyRingId}: {parts.Count} parts copied with their own body and traverse motor");
    }

    /// The numbers one part names that belong to a part: its own components, the part it hangs from, its body and mirror
    /// links, and what its own nested settings name. Not settings blocks (another number space) and not plain settings.
    internal static List<(string Field, int Value)> LinksOf(JsonObject o)
    {
        var found = new List<(string, int)> { ("pvuid", Id(o, "pvuid")) };
        found.AddRange(ComponentKeys(o).Select(k => (k, Id(o, k))));
        if (o["structureID"] is JsonValue body && body.TryGetValue<int>(out int at)) found.Add(("structureID", at));
        if (o["compartmentBodyID"]?.AsObject()["structureVuid"] is JsonValue shell && shell.TryGetValue<int>(out int nested)) found.Add(("compartmentBodyID", nested));
        if (o["transform"]?["mirrorVuid"] is JsonValue mv && mv.TryGetValue<int>(out int mirror)) found.Add(("mirrorVuid", mirror));
        found.AddRange(NestedLinksOf(o));
        return found;
    }

    /// Gives a copied structure a shape of its own, so armour edited on the copy later cannot change the original.
    static int OwnShape(JsonObject block, JsonArray meshes, int nextMesh)
    {
        if (block["blueprint"]?.AsObject() is not { } settings || settings["bodyMeshVuid"] is not JsonValue mesh || !mesh.TryGetValue<int>(out int meshId)) return nextMesh;
        if (meshes.FirstOrDefault(m => m!["vuid"]?.GetValue<int>() == meshId) is not { } shared) return nextMesh;
        var own = Clone(shared);
        own["vuid"] = nextMesh;
        settings["bodyMeshVuid"] = nextMesh;
        meshes.Add(own);
        return nextMesh + 1;
    }

    internal static JsonObject Clone(JsonNode node) => JsonNode.Parse(node.ToJsonString())!.AsObject();

    public static Matrix4x4 Local(JsonNode transform, bool scaled = true)
    {
        Vector3 Vec(string k) => new(transform[k]![0]!.GetValue<float>(), transform[k]![1]!.GetValue<float>(), transform[k]![2]!.GetValue<float>());
        var r = Vec("rot") * (MathF.PI / 180f);
        // Unity Euler order: Z, then X, then Y. System.Numerics uses row vectors.
        return Matrix4x4.CreateScale(scaled ? Vec("scale") : Vector3.One) * Matrix4x4.CreateRotationZ(r.Z) * Matrix4x4.CreateRotationX(r.X) * Matrix4x4.CreateRotationY(r.Y) * Matrix4x4.CreateTranslation(Vec("pos"));
    }
    public static Dictionary<int, Matrix4x4> WorldMatrices(Dictionary<int, JsonObject> objects, bool attachmentFrames = false)
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
        foreach (int id in objects.Keys) { Get(id); Frame(id); }
        return attachmentFrames ? frames : cache;
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
