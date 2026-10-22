using System.IO;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using BepInEx;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Sprocket.TechTrees;
using Sprocket.UI;
using Sprocket.Vehicles;
using Sprocket.Vehicles.PlateStructures;
using Sprocket.Vehicles.PlateStructures.Design;

namespace SprocketTools;

/// #105 自造装甲材质：七条材质接进游戏自己的科技表，装甲板面板里给一个可选的材质下拉。
/// 走的是 2026-10-03 拍板的丙路线：json 随 DLL 内嵌 → 落地到 BepInEx\plugins\SprocketMaterials\ →
/// 用游戏自己的 TechTreeLoader.LoadTechnologies(目录) 解析 → 追加进那个 loader 实例的 technologies。
/// 探针取证已经把三条口径钉死，写在这儿免得日后再猜：查找用的键是裸 type（表里 Tech.Identifier 是空串）、
/// 同一个 type 的多条会在帧里塌合（官方 111 份 json 只有 28 个 type）⇒ 我们每条材质独享一个 type、不许改名；
/// 表内缺 density 与 costMultiplier 时游戏取 7850 与 1（官方 armour 那四条就缺）⇒ 七条把四个键都带齐。
/// 材质只管弹道与造价：半透明外观不归这张表管（那是部件 json 的材质引用），复合与爆反"按弹种分裂"
/// 也还没接（伤害模型看到的材质只有 rhaFactor/spallFactor/density/priority 四个数，没有弹种这一维）。
[HarmonyPatch]
public static class ArmorMaterials
{
    /// 落地目录固定在插件目录里：不碰玩家的 StreamingAssets，材质跟 DLL 同进退。
    static readonly string Folder = Path.Combine(Paths.PluginPath, "SprocketMaterials");

    record Candidate(string Type, string Name, string Date, string Note);

    /// 板上没有材质键时游戏走的那个默认值（PlateStructure.RhaTechID）；0.2 版的板删键不划算，就把这个值写回去。
    const string DefaultTech = "rha";

    /// 游戏自带的四条：科技表里没有显示名（整张表找不到 sheetMetal 的中文名），名称只能我们自己给。
    static readonly Candidate[] Vanilla =
    {
        new("sheetMetal", "均质钢板材", "1916 起五档", "游戏自带：倍率随年代从 0.4 涨到 1.8"),
        new("rha", "轧制均质装甲", "1920.01.01", "游戏自带的基准，下面所有倍率都以它为 1.0"),
        new("armour", "装甲钢（年代总表）", "1945 起四档", "游戏自带：表里没写密度与造价倍率，游戏取 7850 与 1"),
        new("sand", "沙袋", "不限年代", "游戏自带：密度低、便宜"),
    };

    static List<Candidate>? embeddedCandidates;
    static string lastResult = "";

    static bool? armorDawnPresent;
    static bool ArmorDawnLoaded()
    {
        if (armorDawnPresent.HasValue) return armorDawnPresent.Value;
        try { armorDawnPresent = System.Array.Exists(System.AppDomain.CurrentDomain.GetAssemblies(), a => a.GetName().Name == "ArmorDawn"); }
        catch { armorDawnPresent = false; }
        if (armorDawnPresent.Value) Plugin.ModLog.LogInfo("TOOL_MAT 检测到装甲黎明：工具包的装甲材质面板让位，只用装甲黎明的材质功能。");
        return armorDawnPresent.Value;
    }
    static bool Enabled() => (Plugin.ArmourMaterials?.Value ?? true);

    /// 记哪些 loader 实例已经并过表（按原生指针；托管包装每次 TryCast 可能新建，不能拿引用相等当键）。
    static readonly HashSet<IntPtr> merged = new();

    /// 合并点（真正的靶心）：造帧之前。23:38 那次实跑判定，游戏那份 loader 是 `[RuntimeInitializeOnLoadMethod] Initiate()`
    /// 建的（dump.cs:942916），那一刻早于本插件的挂点装上 ⇒ 曾经挂在 `TechTreeLoader` 构造函数上的那条 postfix 整场一次都没触发
    /// （它也已经删掉：`MethodType.Constructor` 不写全参数类型时每次开机还要报一条 patch 失败）。
    /// Vehicle.Build 期间造的每一帧若不含我们七条，`SetArmourMaterial` 只能回落到默认值（日志指纹：json 写 5600/4.5，板上读到 7850/1）。
    /// 而 `TechTreeLoader` 是全游戏唯一的 ITechFrameFactory 实现、`TechFrame` 是 internal sealed 只能由它现造（942902 / 942778）
    /// ⇒ 拦住这个方法就拦住了游戏拿帧的唯一出口，比"构造之后补"没有死角。
    /// prefix 只收 __instance、不收 TechDate：结构体参数按挂点安全区算未验证形态，没必要为一个用不上的参数冒险。
    [HarmonyPrefix, HarmonyPatch(typeof(TechTreeLoader), nameof(TechTreeLoader.GetTechFrameAtDate))]
    static void BeforeFrame(TechTreeLoader __instance) => Ui.Guard("Armour materials", () =>
    {
        if (!Enabled() || __instance == null) return;
        var ptr = __instance.Pointer;
        if (ptr == IntPtr.Zero || !merged.Add(ptr)) return; // 每个实例只扫一次全表：这方法是面板每帧都会调的热路径
        Merge(__instance);
    });

    /// 面板合并（2026-10-06 18:3x 定稿）。上一版把下拉搬去挂 `PlateStructureEditor.DrawMaterialSelect`，实机两头落空：
    /// 那个方法在这个 build 里**没有任何调用点**（xref.py 扫 .text 与 il2cpp 两段的 E8 call／E9 jmp／RIP 相对取址，
    /// 目标 0x1E8A2B0 命中 0 处）⇒ prefix 一次都没跑，而 postfix 里原来的下拉被我删了，于是面板上什么都不剩。
    /// 他截图上那行「轧制均质装甲 ☐」也不是这个方法画的，是属性面板画 `PlateStructureProperties.UseRHA`（0x48）那颗 BoolProperty；
    /// 那颗勾选框的处理器 `<.ctor>b__3_2`（0x1E8BCC0）末尾是 `mov [rcx+0x40], rax` ⇒ 它和我们的下拉写同一个键，
    /// 所以「并成一处」= 下拉留在我们这节、游戏那颗勾选框在属性组构造时 Deregister 掉（见 RhaRowOff）。
    /// `Dropdown`（0x6ABD00）只往当前 layout 摆一个元素、对 `layouts` 栈（0x30）只 Peek 不 Push ⇒ 画完不必替游戏收尾，
    /// 这点很关键：这里是在游戏自己的 OnGUI 后面接笔，误用 `EndDropdown`/`EndAllDropdowns`（0x6A9420/0x6A94C0）会把游戏那一节整个收掉。
    /// 当前值直接读这块板自己（原来用全局 choice，板上是 rha、下拉却停在上一张图选的「均质钢板材」，看着就像两处不同步）。
    [HarmonyPostfix, HarmonyPatch(typeof(PlateStructureEditor), nameof(PlateStructureEditor.OnGUI))]
    static void Draw(PlateStructureEditor __instance, IGUILayout layout) => Ui.Inspector("Armour materials", layout, () =>
    {
        if (!Enabled()) return;
        var design = DesignEditor.Instance;
        var ui = layout.TryCast<IGUIElementDrawer>();
        var plate = __instance.Component;
        if (design == null || !design.IsReady || ui == null || plate == null) return;

        var loader = Loader(design);
        var frame = loader == null ? null : Frame(design);
        var allCandidates = CandidateList(loader);
        // 爆炸反应装甲与观察窗玻璃不再作为“装甲材质”出现：前者由 ActiveProtection 的反应装甲接管，
        // 后者在本 build 里只能涂成灰白，玩家明确不要。
        var candidates = allCandidates.Where(c => !Hidden(c)).ToArray();
        string current = plate.armourTechID ?? "";
        var picked = CurrentCandidate(current, allCandidates) ?? candidates.FirstOrDefault() ?? Vanilla[1];
        int bid = plate.blueprintRef?.BlueprintID ?? -1;
        var vo = plate.VehicleObject;

        Ui.Section(layout, "装甲材质");

        // 0 这一格是「（不设定）」：板上真没有这个键才停在这儿；有键但表里查不到的，读数那行会照实说。
        int at = current.Length == 0 ? 0 : 1 + Array.FindIndex(candidates, c => c.Type == current);
        if (at < 0) at = 0;
        bool eraOn = EraOn(vo);
        var names = new Il2CppSystem.Collections.Generic.List<string>();
        names.Add("（不设定）");
        foreach (var candidate in candidates) names.Add(candidate.Name);
        ui.Dropdown("装甲材质", names.Cast<Il2CppSystem.Collections.Generic.IReadOnlyList<string>>(), at,
            Ui.IntCallback(index => Ui.Guard("Armour materials", () =>
            {
                string? type = index <= 0 ? null : candidates[index - 1].Type;
                string label = index <= 0 ? "未设定" : candidates[index - 1].Name;
                Edit(design, $"装甲材质：板块 {bid}", $"板块 {bid} 的材质已写成 {label}；退出重进还在就算持久化成功。",
                    type, new HashSet<int> { bid }, Inspected(plate));
                lastResult = "已写入这一块板的装甲材质。";
                __instance.RequestRedraw();
            })),
            "游戏自带的材质在前，SprocketTools 自带的材质在后，已装的材质 mod 排在最后（判据是表里带 rhaFactor）。" +
            "爆炸反应装甲保留在材质列表里；观察窗玻璃从本下拉移除。" +
            "选中装甲即写这一块板，走设计的正常修改通道：改前有备份。第一项「（不设定）」＝删掉这个字段。");

        string text = $"板块 {bid}：现在用的材质 {Current(current, candidates)}\n" +
            $"该材质读数：{Describe(frame, picked)}\n" +
            (lastResult.Length == 0 ? "改前有备份，撤销走设计器自己的那套。" : lastResult);
        ui.InfoField(text, text.Split('\n').Length + 1);

        var allTip = new UITooltip("整车统一", "把下拉现在选中的材质铺到这份设计里所有装甲板，用来看密度、造价与整车质量的连锁。");
        ui.Button($"整车统一：{picked.Name}", Ui.Callback(() => Ui.Guard("Armour materials", () =>
        {
            Edit(design, $"装甲材质：整车 {picked.Name}", $"整车装甲材质已写成 {picked.Name}，看质量与造价跟着变了没有。",
                picked.Type, null, Inspected(plate));
            SyncLook(picked.Type, vo);
        })), ref allTip);

        var clearTip = new UITooltip("清除材质", "把装甲板蓝图里的 armourTechID 字段删掉，回到「未设定」，并把这条记录的版本号一并降回 0.0（老存档里七成板子就是这个形状）。0.2 版的板不降：降了游戏连碰撞开关都不读，所以那种只写成默认材质 rha。");
        ui.Button("清除：回到未设定", Ui.Callback(() => Ui.Guard("Armour materials", () =>
        {
            Edit(design, "装甲材质：清除", "已按各板自己的版本号清除材质字段（0.2 版的板写成默认 rha）。",
                null, null, Inspected(plate));
            SyncLook(null, vo);
        })), ref clearTip);

        // 反应装甲整节固定放在装甲材质下面（不再进下拉，也不留在“部件”区）。
        if (eraOn || EraEligible(vo)) DrawEra(layout, ui, vo);

        // 材质 / 面材质由装甲黎明画同一节内部，保证只有一个“装甲材质”入口。
        try
        {
            var panelType = System.Type.GetType("ArmorDawn.Features.VehicleMaterials.Panel, ArmorDawn");
            panelType?.GetMethod("DrawEmbedded", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                ?.Invoke(null, new object[] { layout, vo });
        }
        catch (Exception ex)
        {
            Ui.Guard("Armour materials", () => throw ex);
        }
    });

    /// 撤掉游戏自己那颗「轧制均质装甲」勾选框：属性面板画的是 `PropertyGroup` 私有表 0x18 那份有序列表，
    /// `PropertyGroup.Deregister`（0x1EE8250）的开头正是 `List.Remove([this+0x18])`，成功后才转手调 `IPropertyRegister.Deregister`（槽 6）
    /// ⇒ 只调接口那个公共口不够，必须走属性组自己的 Deregister。属性组每选一块板构造一次，这里每次都撤；只打一次日志。
    /// 关掉「Enable custom materials」就整段不生效：那颗勾选框回到游戏自己画，材质入口退回原版那一格。
    static bool rowOffLogged;

    /// 0.2.56 的构造器走 IL2CPP 补丁后端会报“Derived classes must provide an implementation”，
    /// 只为撤一颗勾选框不值得污染日志；方法保留但不注册，原版「轧制均质装甲」勾选框照常显示。
    static void RhaRowOff(PlateStructureProperties __instance) => Ui.Guard("Armour materials", () =>
    {
        if (!Enabled() || __instance?.UseRHA == null) return;
        __instance.Deregister(__instance.UseRHA);
        if (rowOffLogged) return;
        rowOffLogged = true;
        Plugin.ModLog.LogInfo("TOOL_TECH 并格 属性组的 UseRHA 勾选框已 Deregister：材质只留「装甲材质」那一格下拉（看到这条日志≠画面上没了，那要实机看）");
    });

    /// 面板上两处都要拿 loader，合表这件事只在这儿做一次：已经并过的实例直接跳过，别每帧重扫全表。
    /// （合并点二：游戏开机那一次构造早于本插件挂点，上面的 ctor postfix 追不上，只能在这里兜。）
    static TechTreeLoader? Loader(DesignEditor design)
    {
        var loader = design.Core?.techFrameFactory?.TryCast<TechTreeLoader>();
        var ptr = loader?.Pointer ?? IntPtr.Zero;
        if (loader != null && (ptr == IntPtr.Zero || !merged.Contains(ptr))) Merge(loader);
        return loader;
    }

    /// 内嵌的七份 json 是名称与年代的唯一来源：面板上的中文名和发明日期都从这儿读，免得代码里再抄一份走岔。
    static List<Candidate> Candidates()
    {
        if (embeddedCandidates != null) return embeddedCandidates;
        var list = new List<Candidate>();
        var assembly = typeof(ArmorMaterials).Assembly;
        foreach (string resource in assembly.GetManifestResourceNames()
                     .Where(n => n.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(n => n, StringComparer.Ordinal))
        {
            JsonObject? root;
            try
            {
                using var stream = assembly.GetManifestResourceStream(resource);
                using var reader = new StreamReader(stream!);
                root = JsonNode.Parse(reader.ReadToEnd())?.AsObject();
            }
            catch (Exception ex) { Plugin.ModLog.LogError($"TOOL_TECH 内嵌资源 {resource} 读不出：{ex.GetType().Name} {ex.Message}"); continue; }
            string type = Text(root, "type"), date = Text(root, "date");
            string name = root?["properties"] is JsonObject properties ? Text(properties, "displayName") : "";
            if (type.Length == 0)
            {
                Plugin.ModLog.LogError($"TOOL_TECH 内嵌资源 {resource} 没有可用的 type 字段，跳过（查找用的键就是 type）");
                continue;
            }
            list.Add(new Candidate(type, name.Length > 0 ? name : type, date, "SprocketTools 自带"));
        }
        if (list.Count == 0) Plugin.ModLog.LogError("TOOL_TECH 内嵌的材质一条都没读到：DLL 里没带上 Materials\\*.json？");
        else Plugin.ModLog.LogInfo($"TOOL_TECH embedded {list.Count} 条：" + string.Join("、", list.Select(c => c.Type)));
        return embeddedCandidates = list;
    }

    static Candidate[]? candidateCache;
    static IntPtr candidateCacheFrom;
    static int candidateCacheLength;

    /// 面板上的候选集：点名的四条官方 + 七条自带，再加上表里其他带 rhaFactor 的条目（已装材质 mod 的那些）。
    /// OnGUI 每帧要跑好几遍，而每条读数都是一次 IL2CPP 往返，所以整表缓存在「loader 的原生指针 + 条目数」上，变了才重算
    /// （托管包装对象每次 TryCast 可能新建，不能拿引用相等当键）。
    static Candidate[] CandidateList(TechTreeLoader? loader)
    {
        int length = loader?.technologies?.Length ?? 0;
        IntPtr ptr = loader?.Pointer ?? IntPtr.Zero;
        if (candidateCache != null && candidateCacheFrom == ptr && candidateCacheLength == length) return candidateCache;
        var others = Discovered(loader).ToList();
        Plugin.ModLog.LogInfo($"TOOL_TECH candidates 表内条目 {length} 条 ⇒ 下拉 {Vanilla.Length + Candidates().Count + others.Count} 项，" +
            $"其中判据 rhaFactor 另外认出 {others.Count} 种材质（{string.Join("、", others.Select(o => o.Type))}；没有说明这张表里只有官方四条加我们七条）");
        candidateCache = Vanilla.Concat(Candidates()).Concat(others).ToArray();
        candidateCacheFrom = ptr;
        candidateCacheLength = length;
        return candidateCache;
    }

    /// 判据 rhaFactor 是离线普查定的：官方 28 个 type 里恰好只有 armour / rha / sand / sheetMetal 四个带它，
    /// 其余 24 个（炮、传动、履带…）一个都没有 ⇒ "表里有 rhaFactor"＝"这是一种装甲材质"。第三方材质 mod 也走这个键进伤害模型。
    static IEnumerable<Candidate> Discovered(TechTreeLoader? loader)
    {
        var table = loader?.technologies;
        int length = table?.Length ?? 0;
        if (length == 0) yield break;
        var listed = new HashSet<string>(Vanilla.Select(v => v.Type).Concat(Candidates().Select(c => c.Type)));
        var seen = new HashSet<string>();
        for (int i = 0; i < length; i++)
        {
            var tech = table![i];
            if (tech == null) continue;
            string type = TypeOf(tech);
            if (type.Length == 0 || listed.Contains(type) || !seen.Add(type)) continue;
            float factor;
            try { factor = tech.GetFloat("rhaFactor", -1f); }
            catch (Exception ex) { Plugin.ModLog.LogInfo($"TOOL_TECH 读 {type} 的 rhaFactor 撞了 {ex.GetType().Name}，这条不进下拉"); continue; }
            if (factor < 0f) continue;
            string name = "";
            string date = "";
            try
            {
                name = tech.GetString("displayName", "");
                date = tech.TryCast<Tech>()?.InventionDate.ToString() ?? "";
            }
            catch (Exception ex) { Plugin.ModLog.LogInfo($"TOOL_TECH 读 {type} 的名称或年代撞了 {ex.GetType().Name}"); }
            yield return new Candidate(type, name.Length > 0 ? name : type, date,
                $"科技表里的条目，对 RHA 倍率 {factor:0.####}（多半来自已装的材质 mod；表里没写中文名就只能显示代号）");
        }
    }

    /// 拼接只做一件事：按 Type 去重后把缺的那几条接在数组尾巴上。解析与装箱全部交给游戏自己的 LoadTechnologies。
    static void Merge(TechTreeLoader? loader)
    {
        if (loader == null) return;
        var current = loader.technologies;
        int length = current?.Length ?? 0;
        if (length == 0) return; // 游戏那份表都没读出来就别急着往里塞

        var have = new HashSet<string>();
        for (int i = 0; i < length; i++) have.Add(TypeOf(current![i]));
        var wanted = Candidates().Where(c => !have.Contains(c.Type)).ToList();
        if (wanted.Count == 0) { merged.Add(loader.Pointer); return; }

        Il2CppReferenceArray<Tech>? parsed;
        try
        {
            EnsureFiles();
            parsed = TechTreeLoader.LoadTechnologies(Folder);
        }
        catch (Exception ex) { Plugin.ModLog.LogError($"TOOL_TECH 解析 {Folder} 失败：{ex.GetType().Name} {ex.Message}"); return; }

        int count = parsed?.Length ?? 0;
        if (count == 0) { Plugin.ModLog.LogError("TOOL_TECH LoadTechnologies 一条都没解析出来：目录或 json 格式有问题，下拉里只剩游戏自带的四条"); return; }

        var names = new HashSet<string>(wanted.Select(w => w.Type));
        var added = new List<Tech>();
        for (int i = 0; i < count; i++)
        {
            var tech = parsed![i];
            if (tech != null && names.Contains(TypeOf(tech))) added.Add(tech);
        }
        var combined = new Il2CppReferenceArray<Tech>(length + added.Count);
        for (int i = 0; i < length; i++) combined[i] = current![i];
        for (int i = 0; i < added.Count; i++) combined[length + i] = added[i];
        loader.technologies = combined;
        merged.Add(loader.Pointer);

        var missing = names.Except(added.Select(TypeOf)).ToList();
        Plugin.ModLog.LogInfo($"TOOL_TECH merge 表内={length} 目录解析={count} 追加={added.Count}（{string.Join("、", added.Select(TypeOf))}）" +
            (missing.Count == 0 ? "" : $" 没解析出来：{string.Join("、", missing)}"));
    }

    /// 内嵌资源的逻辑名带命名空间前缀（SprocketTools.Materials.compositeArmour.json），落地时去掉前缀，
    /// 让 BepInEx\plugins\SprocketMaterials\ 里的文件名跟 json 里的 type 同名，玩家翻目录时一眼对得上。
    static readonly string ResourcePrefix = typeof(ArmorMaterials).Namespace + ".Materials.";

    static string FileName(string resource) => resource.StartsWith(ResourcePrefix, StringComparison.Ordinal)
        ? resource[ResourcePrefix.Length..] : Path.GetFileName(resource);

    static void EnsureFiles()
    {
        Directory.CreateDirectory(Folder);
        var assembly = typeof(ArmorMaterials).Assembly;
        foreach (string resource in assembly.GetManifestResourceNames().Where(n => n.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            using var stream = assembly.GetManifestResourceStream(resource);
            using var reader = new StreamReader(stream!);
            File.WriteAllText(Path.Combine(Folder, FileName(resource)), reader.ReadToEnd());
        }
    }

    static void Edit(DesignEditor design, string name, string done, string? type, HashSet<int>? only, int focus)
    {
        lastResult = $"上一次改动：{(type == null ? "清除材质字段" : $"写入 {type}")}。等设计器把设计重载入，看密度与造价跟着变了没有。";
        design.RequestEdit(name, done, json => Write(json, type, only, focus));
    }

    /// 写入走设计 json（跟镜像炮塔、弹种预设同一条通道），所以撤销与备份都是游戏自己那套。
    /// 键集跟版本号是绑死的（依据见 VersionNote）：改这个键必须同时把 v 抬到它会读的形状，否则游戏按老形状走，写了等于没写。
    static EditResult Write(string json, string? type, HashSet<int>? only, int focus)
    {
        var root = Conversion.Parse(json);
        var blocks = root["blueprints"]?.AsArray() ?? throw new Exception("这份设计没有 blueprints 数组。");
        var detail = new List<string>();
        var versions = new List<string>();
        foreach (var node in blocks)
        {
            var block = node?.AsObject();
            if (block == null || Text(block, "type") != "structure") continue;
            int bid = Conversion.Id(block, "id");
            if (only != null && !only.Contains(bid)) continue;
            var blueprint = block["blueprint"]?.AsObject();
            if (blueprint == null) { detail.Add($"{bid} 没有 blueprint 块，跳过"); continue; }
            string before = blueprint["armourTechID"] == null ? "（原来没这个字段）" : Text(blueprint, "armourTechID");
            string versionNote = Apply(blueprint, type);
            versions.Add(Text(blueprint, "v"));
            detail.Add($"{bid}：{before} → {(blueprint["armourTechID"] == null ? "（已清除）" : Text(blueprint, "armourTechID"))}{versionNote}");
        }
        if (detail.Count == 0) throw new Exception(only == null ? "这份设计里没有装甲板蓝图。" : $"这份设计里没找到板块 {string.Join("/", only)}。");
        Plugin.ModLog.LogInfo($"TOOL_TECH write 材质={(type ?? "清除")} 蓝图版本={string.Join("/", versions.Distinct())} 改动={string.Join("; ", detail)}");
        return new EditResult(root.ToJsonString(), focus,
            $"{detail.Count} 块板改完（蓝图版本 {string.Join("/", versions.Distinct())}）");
    }

    /// <summary>
    /// 改一块板的 armourTechID，并把 v 挪到「游戏自己会写出这个键形」的那一档，返回打给玩家看的一句话。
    /// 依据（离线全量普查 D 盘 5115 份存档 / 53069 块装甲板，52 个「记录类型×版本号」组合的键形零例外）：
    /// 0.0＝没有 armourTechID，0.1＝比 0.0 只多这一个键，0.2＝再多 collisionEnabled；
    /// 配合 dump.cs 里 StructureBlueprint 的 Load(带 FileVersion) 与 LoadV0 两条路 ⇒ 记录自带的 v 决定游戏读哪批键。
    /// 0.2 的板不往下降：降到 0.0 游戏就不读 collisionEnabled 了，会连带把碰撞开关的设置丢掉，所以那种板只写默认值。
    /// </summary>
    static string Apply(JsonObject blueprint, string? type)
    {
        int minor = Minor(Text(blueprint, "v"));
        if (minor < 0)
        {
            if (type == null) blueprint.Remove("armourTechID"); else blueprint["armourTechID"] = type;
            return "（这份记录的版本号读不出，v 原样不动）";
        }
        if (type != null)
        {
            blueprint["armourTechID"] = type;
            if (minor > 0) return "";
            blueprint["v"] = "0.1";
            return "，版本号 0.0 抬到 0.1（老形状里游戏不读这个键）";
        }
        if (blueprint["collisionEnabled"] != null)
        {
            blueprint["armourTechID"] = DefaultTech;
            return $"，这块板带着 collisionEnabled（0.2 版），删键还得降版本号、游戏就不读它了 ⇒ 改成默认材质 {DefaultTech}";
        }
        blueprint.Remove("armourTechID");
        if (minor == 0) return "";
        blueprint["v"] = "0.0";
        return "，版本号 0.1 降回 0.0";
    }

    /// 只认 "0.x" 这种写法，别的（缺键、纯数字、将来跳到大版本）一律回 -1 让调用处保持原样。
    static int Minor(string version)
    {
        var parts = version.Split('.');
        return parts.Length == 2 && parts[0] == "0" && int.TryParse(parts[1], out int minor) ? minor : -1;
    }

    static ITechFrame? Frame(DesignEditor design)
    {
        var factory = design.Core?.techFrameFactory;
        if (factory == null) return null;
        try { return factory.GetTechFrameAtDate(design.Core!.TechnologyDate); }
        catch (Exception ex) { Plugin.ModLog.LogInfo($"TOOL_TECH 取帧失败：{ex.GetType().Name}"); return null; }
    }

    /// 这块板现在实际用着哪一条：没设定就是游戏那个默认值，表里查不到的代号照代号报（读数那行如实标出来）。
    static Candidate CurrentCandidate(string identifier, Candidate[] candidates)
    {
        if (identifier.Length == 0) return Vanilla[1];
        return candidates.FirstOrDefault(c => c.Type == identifier)
            ?? new Candidate(identifier, identifier, "", "游戏表与我们的材质里都没有：按未知材质走");
    }

    /// 选中那条的读数：官方那四个数 + **新机制的装甲档那两列**（由命中层按弹种生效）。
    static string Describe(ITechFrame? frame, Candidate candidate)
    {
        string cls = AmmoArmourRules.ClassOfTech(candidate.Type);
        string rule = $"{AmmoArmourRules.Describe(AmmoArmourRules.ArmourFor(cls))}（档={cls}，命中层按弹种生效）";
        if (frame == null) return $"{candidate.Type}（取不到科技帧，读数没处查）\n{rule}";
        if (!Invented(frame, candidate.Type, out var tech) || tech == null) return $"{candidate.Type}：科技表里查不到它\n{rule}";
        try
        {
            return $"{rule}\n对 RHA 倍率 {Number(tech, "rhaFactor")}｜密度 {Number(tech, "density")}｜造价倍率 {Number(tech, "costMultiplier")}｜溅射 {Number(tech, "spallFactor")}";
        }
        catch (Exception ex) { return $"{candidate.Type}·读数撞了 {ex.GetType().Name}"; }
    }

    static string Number(IReadOnlyTech tech, string key)
    {
        float value = tech.GetFloat(key, -1f);
        return value < 0f ? "（表内无此键）" : value.ToString("0.####");
    }

    static bool Invented(ITechFrame? frame, string type) => frame != null && frame.TryGetTech(type, out _);

    static bool Invented(ITechFrame? frame, string type, out IReadOnlyTech? tech)
    {
        tech = null;
        return frame != null && frame.TryGetTech(type, out tech) && tech != null;
    }

    /// 存档里第三方材质的代号是自由字符串（5113 份存档里出现过 36 种），查不到就照"未知材质"走，这里如实标出来而不是只显示一串英文。
    static string Current(string? identifier, Candidate[] candidates)
    {
        if (string.IsNullOrEmpty(identifier)) return "（未设定）";
        var known = candidates.FirstOrDefault(c => c.Type == identifier);
        return known == null ? $"{identifier}（游戏表与我们的材质里都没有：按未知材质走）" : $"{known.Name}（{identifier}）";
    }

    static int Inspected(PlateStructure plate) => (int)plate.VehicleObject.VUID;

    static string TypeOf(Tech? tech) => tech?.Type ?? "";

    /// 蓝图里的 v 有时写成字符串（"0.2"）、有时写成数字，这里两种都认：它只是打给玩家看的一行读数，不值得为它把整次改动带崩。
    static string Text(JsonObject? o, string key) => o?[key] is JsonValue value
        ? value.TryGetValue<string>(out var text) ? text : value.ToString() : "";
    static System.Reflection.Assembly? VmAssembly() =>
        AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "ArmorDawn");

    static int AppearanceWhole(VehicleObject vo)
    {
        try
        {
            var asm = VmAssembly();
            var store = asm?.GetType("ArmorDawn.Features.VehicleMaterials.Store");
            var get = store?.GetMethod("Get", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            var part = get?.Invoke(null, new object[] { vo });
            var whole = part?.GetType().GetField("Whole")?.GetValue(part);
            return whole == null ? 0 : Convert.ToInt32(whole);
        }
        catch { return 0; }
    }

    /// <summary>装甲材质下拉兼任外观开关：观察窗玻璃→半透明玻璃，其余装甲→默认车漆。</summary>
    static void SyncLook(string? type, VehicleObject? vo)
    {
        if (vo == null) return;
        bool glass = !string.IsNullOrEmpty(type) && type.IndexOf("glass", StringComparison.OrdinalIgnoreCase) >= 0;
        SetAppearanceWhole(vo, glass ? 1 : 0);
        lastResult = glass ? "整车外观已切换为半透明玻璃。" : "整车外观已恢复为默认车漆。";
    }

    static void SetAppearanceWhole(VehicleObject vo, int index)
    {
        try
        {
            var asm = VmAssembly();
            var ops = asm?.GetType("ArmorDawn.Features.VehicleMaterials.Ops");
            var kindType = asm?.GetType("ArmorDawn.Features.VehicleMaterials.MaterialKind");
            var set = ops?.GetMethod("SetWhole", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (set == null || kindType == null) return;
            set.Invoke(null, new[] { (object)vo, Enum.ToObject(kindType, index) });
        }
        catch (Exception ex) { Plugin.ModLog.LogWarning($"TOOL_MAT 外观材质切换失败：{ex.GetType().Name}: {ex.Message}"); }
    }


    static readonly string[] LookNames = { "玻璃（半透明）", "镍", "铬", "钢", "帆布", "橡胶" };

    static bool Hidden(Candidate c) =>
        c.Type.Equals("visionBlockGlass", StringComparison.OrdinalIgnoreCase) ||
        c.Name.Contains("观察窗玻璃");

    static System.Reflection.Assembly? ApsAssembly() =>
        AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "ArmorDawn");

    static System.Type? ApsType(string name) =>
        ApsAssembly()?.GetType("ArmorDawn.Features.ActiveProtection." + name);

    static object? EraHost(VehicleObject vo)
    {
        try
        {
            var hosts = ApsType("EraHosts");
            var get = hosts?.GetMethod("Get", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            return get?.Invoke(null, new object[] { vo });
        }
        catch { return null; }
    }

    static bool EraOn(VehicleObject vo)
    {
        try
        {
            var host = EraHost(vo);
            var c = host?.GetType().GetField("C")?.GetValue(host);
            var on = c?.GetType().GetField("On")?.GetValue(c);
            return on is bool b && b;
        }
        catch { return false; }
    }

    static bool EraEligible(VehicleObject vo)
    {
        try
        {
            var hosts = ApsType("EraHosts");
            var eligible = hosts?.GetMethod("Eligible", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            return eligible?.Invoke(null, new object[] { vo }) is bool b && b;
        }
        catch { return false; }
    }

    static void DrawEra(IGUILayout layout, IGUIElementDrawer ui, VehicleObject vo)
    {
        try
        {
            var panel = ApsType("EraHostPanel");
            var draw = panel?.GetMethod("Draw", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var host = EraHost(vo);
            var c = host?.GetType().GetField("C")?.GetValue(host);
            // 只放行这一次反射调用，原“部件”区那次会被 AdTankPanel 的 prefix 挡掉。
            var at = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "ArmorDawn")?.GetType("ArmorDawn.AdTankPanel");
            var flag = at?.GetField("EraDrawAllowed", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            flag?.SetValue(null, true);
            try { draw?.Invoke(null, new object?[] { layout, ui, vo, c }); }
            finally { flag?.SetValue(null, false); }
        }
        catch (Exception ex) { Plugin.ModLog.LogWarning($"TOOL_MAT 反应装甲面板失败：{ex.GetType().Name}: {ex.Message}"); }
    }

    static void ToggleEra(VehicleObject vo, bool on)
    {
        try
        {
            var panel = ApsType("EraHostPanel");
            var toggle = panel?.GetMethod("Toggle", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            toggle?.Invoke(null, new object[] { vo, on });
        }
        catch (Exception ex) { Plugin.ModLog.LogWarning($"TOOL_MAT 反应装甲开关失败：{ex.GetType().Name}: {ex.Message}"); }
    }
}
