using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Sprocket.DamageModelling;
using System.Runtime.InteropServices;
using UnityEngine;
using Plate = Sprocket.Vehicles.PlateStructures.PlateStructure;
using VehicleObject = Sprocket.Vehicles.VehicleObject;

namespace SprocketTools;

/// **命中读数：直接读游戏自己算完的那次穿透模拟**，不再靠 Unity 碰撞体距离去猜"这一发打在哪块板"。
/// 本类**只读**：给出「官方认出的部件 ＋ 原弹头在板内的行程」，供 `Ballistics` 定装甲档与后效点名；
/// 认不出就返回 null，命中层自己退回最近板/整车档。
///
/// 为什么非走官方几何不可：Unity 碰撞体那条路实跑给出过 5~7 米的离谱距离（有些板压根没有可用碰撞体），
/// 于是后效伤害落到猜出来的 `部件 0` 上。`CompoundStructure` 是伤害模型自己的 mesh BVH，它的 `LastSimulation`
/// （`internal` 属性，interop 把 internal 摊平成可直读；`get_LastSimulation` RVA 0x5844B0）
/// 就是游戏这次命中跑完的那份穿透模拟本体（`Penetrate` 之后写在这上面）。
/// 官方给的部件身份出口是 `PenetrationFragment.HitTransform` ＝ `meshes[hitStructureIndex].transform` 那一格的
/// `VehicleObject`，它的 `Components` 里的 `PlateStructure.armourTechID` 就是**那块板的材质**。
/// 间隙装甲同理走官方几何：`IDamageModelStructure.Raycast`（接口 Slot 8，dump.cs:911787）吃的就是**世界坐标**
/// （反汇编 `CompoundStructure.Raycast` RVA `0x1D72BB0` 开头先看 `spaceFramesDirty`(0x70)、必要时
/// `UpdateStructureSpaceMatrices`(0x1D73980)，再把原点/方向换算进 sim 空间才走内部求交）
/// ⇒ 我们从着弹点沿弹道再打一条短射线，用的就是伤害模型自己那套板，不再依赖 Unity 物理。
///
/// ⚠️ **本类没有任何 Harmony 挂点，是故意的**（2026-10-04 15:15 离线判死）：把 `GameAssembly.dll` 的 `il2cpp` 段
/// （映像 `0x18054d000`–`0x18356e64c`，约 48 MB，才是真正放游戏代码的段；`.text` 只有 5.5 MB 且装的是 Unity/IL2CPP 运行时）
/// 全量扫一遍 `E8`（rel32 直接 call），**`DamageModelHit.Simulate`（RVA 0x1D81050）的直接调用者 = 0**
/// （同批 `DamageModelHit..ctor` 0x1D81030、`CompoundStructure.Penetrate` 0x1D71C50 与接口实现 0x1D76B80 也都是 0；
/// 对照组成立 ⇒ 扫描本身可信：`ProjectileFunction.NewEffect` 0x1EDE2A0 报 5 处、`DamageModel.Raycast` 0x1D7FB10 报 2 处）。
/// ⇒ `Simulate` 是非虚、非接口的结构体方法，真要有人调它必然是直接 call；一个都没有 ⇒ **它被内联进了 `HitDamageModel`**，
/// 挂在它身上的补丁永不响（原来那条 `AfterSimulate` 就是这么废的，已删）。
/// ⇒ 读数改走**属性**：`get_LastSimulation` 是普通调用，不涉及"方法有没有被内联掉"这一类死缝判据；
///   要官方几何就**主动调接口**（`Sample` / `Raycast`，见下面两个方法），别指望挂在死缝上的补丁。
///
/// 🚫 **class 化结构体的两条 interop 雷（所以本文件按原生偏移自读）**（证据链 = 逆向文档 §16）：
/// dump 里的 struct 在 interop 里分两类生成 —— 可 blittable 的才是真 C# struct，**另一批被生成了 class**
/// （2026-10-04 19:40 全量普查的数：dump 侧 3520 个 struct 声明 ＝ 1596 个真 struct + 1214 个 class 化 + 531 个查不到 + 170 个同名不同种；
///   `Fragment`/`MeshStructure`/`PenetrationFragment`/`DamageModelHit` 全在 class 化那批；`MaterialInfo`/`ObjectInfo`/`SampleHit`/
///   `SampleSettings`/`PenetratorInfo` 不在，照旧直读 —— 逐条判定抄 `tools\dump-struct-kinds.txt`）。
/// 对 class 化的那批有两处系统性错位：
/// ① **字段访问器少加 0x10**：生成体是 `(IntPtr)Il2CppObjectBaseToPtrNotNull(this) + il2cpp_field_get_offset(f)`，
///   而导出 `il2cpp_field_get_offset`（RVA 0x489760）反汇编就是 `movsxd rax,[rcx+0x18]; ret` ⇒ 给的是**数据相对**偏移（不含对象头），
///   装箱结构体的载荷在 `指针+0x10`（`il2cpp_object_unbox` RVA 0x40B4C0 = `lea rax,[rcx+0x10]`）
///   ⇒ 每个字段都往前错 0x10 字节：**标量是错值，引用槽是野指针**（`.Length` / `GetComponentInParent` 直接 AV，`try/catch` 抓不到这种损坏状态异常）。
/// ② **`out <class 化结构>` 只留 8 字节槽**：`PenetrationSimulation.GetFragment(int, out PenetrationFragment)` 的生成体是
///   `stackalloc IntPtr[2]` + 槽里放 `&num2`（`nint num2 = 0`）⇒ 被调方按 `PenetrationFragment` 真实大小（0x48）往这儿写 = **踩栈**。
///   `GetFinalOriginalPenetrator(out …)` 同一条雷 ⇒ 这两个调用**一个都不留**：`FinalPenetrator` 与破片字段全部自己按偏移算。
/// ✅ **方法/属性调用是好的**：生成体走 `il2cpp_runtime_invoke(m, il2cpp_object_unbox(this), …)` ⇒ 先 unbox 再进 AOT。
/// ✅ **数组定址也是好的**（`tools\decomp\Il2CppReferenceArray`1.cs`）：`T` 是值类型时静态构造取 `il2cpp_class_value_size` 当步长
///   ⇒ `fragments[i]`/`meshes[i]` 的**下标推进没错**（步长与 AOT 的 0x50/0x58 一致），错的只有元素自己的字段。
///   数组本体布局：**长度在 `+0x18`、数据在 `+0x20`**（`Il2CppArrayBase.ArrayStartPointer` 的生成式与 AOT 里 `cmp eax,[r9+0x18]` 双向对上）。
/// ⇒ 所以读法：从 `PenetrationSimulation`（**class**，字段偏移可信）拿数组指针，自己按 `0x20 + i*0x50` 定 `Fragment` 元素、
///   按 `0x20 + i*0x58` 定 `MeshStructure` 元素；官方 `HitTransform` 用 `meshes[hitStructureIndex].transform` **重建**
///   （0x1D7A390 反汇编：`hitStructureIndex == -1` 才置空，否则取的就是这一格 ⇒ 重建值与官方值同一个数）；
///   对外那份 `PenetrationFragment` 从头到尾不读。
/// ⚠️ 这套读法把**原生偏移写死在代码里** ⇒ 游戏改版挪字段就会静默读出错数（不会崩，因为定址只用 `Pointer`+常量）。
///
/// ❌ **部件身份只有 `HitTransform` 这一个出口，别再找第二条路接回判定链**：`ParentIndex` 是**父破片**下标
/// （官方签名 `PenetrationSimulation.NewFragment(ref Fragment parent, short parentIndex)` 把两者配成一对；spall 是一棵破片树，
/// 2026-10-04 15:50 反汇编那轮判死的）；`PathIndex`（byte）配 `DamageModelRules.MaxPathStepCount = 255`，是路径步数，也不是对象表。


internal static class HitSim
{
    /// `EntityID`/`HasPart` ＝ 官方认出的着弹部件（`meshes[hitStructureIndex].transform → VehicleObject`）；
    /// `Travel` ＝ 末次原弹头在板内走的距离，后效伤害按它决定往哪一层施加。
    internal sealed record Info(int EntityID, bool HasPart, float Travel);


    static VehicleObject? ObjectOf(Transform t)
    {
        if (t == null) return null;
        try { return t.GetComponentInParent<VehicleObject>(); }
        catch (SystemException) { return null; }
    }

    /// 部件 → 装甲材质 id（`PlateStructure.armourTechID`）。非装甲部件（轮、履带、炮管）返回 `"(非装甲)"`。
    internal static string TechOf(VehicleObject vo)
    {
        try
        {
            var comps = vo.Components;
            if (comps != null)
                foreach (var c in DesignEditor.Each(comps))
                    if (c?.TryCast<Plate>() is { } plate) return plate.armourTechID ?? "(null)";
        }
        catch (Exception) { }
        return "(非装甲)";
    }

    /// **官方弹道采样**（只读、由我们主动调用，不是挂点）。
    /// `IReadOnlyCompoundStructure.Sample(Vector3, Vector3, float, in SampleSettings, out SampleHit)` 是接口 Slot 3
    /// （dump.cs:912264），`CompoundStructure` 同时实现 `IDamageModelStructure` 与 `IReadOnlyCompoundStructure`，
    /// 所以在**已经证明活的**命中接缝（`HitDamageModel` 后贴）里 `TryCast` 过去直接调就行 —— 不需要给 `Sample` 自己挂 patch，
    /// 也就绕开了「离线签名安全 ≠ 运行期可达」这一整类坑（`DamageModelHit.Simulate` 就是这么被判成死缝的，见类头注释）。
    /// 返回的是游戏自己的口径：`Thickness`（实测厚）、`LOSThickness`（视线厚度）、`EffectiveThickness`（含倾角/转正后的有效厚）、
    /// `SurfaceIncidenceAngle`（**它算的入射角，转正已经扣过**）、`Flags`（`Overmatch=1` 超口径未击穿区）。
    internal readonly record struct SampleReadout(float Thickness, float Lost, float Effective, float Incidence, bool Overmatch);

    internal static Info? FromLastSimulation(IDamageModelStructure? model)
    {
        var compound = Cast<Sprocket.DamageModelling.CompoundStructure>(model);
        if (compound == null) return null;
        PenetrationSimulation? sim;
        try { sim = compound.LastSimulation; }
        catch (Exception) { return null; }
        return Read(sim);
    }

    /// 接口的 C# 视图**不隐式实现**（interop 的 `PenetrationSimulation` 不能直接当 `IPenetrationSimulation` 用），
    /// 反过来 `TryCast` 到具体类是 IL2CPP 的 `isinst`：类型不对给 null。需要具体类是因为所有原生读数
    /// 都从 `PenetrationSimulation` 的字段（0x80 `fragments`、0x88 `fragCount`、0x30 `meshes`…）拿。
    /// `TryCast` 在互转失败时会抛，这里统一吞成 null —— 读不到就是不读，绝不拿半个类型去定偏移。
    static T? Cast<T>(Il2CppObjectBase? obj) where T : Il2CppObjectBase
    {
        if (obj == null) return null;
        try { return obj.TryCast<T>(); }
        catch (Exception) { return null; }
    }


    /// **原生布局常量**（全部离线实测，来源写在类头与 逆向 §16）。
    /// 数组：长度在 `+0x18`、数据在 `+0x20`（`Il2CppArrayBase.ArrayStartPointer` 的生成式 = `Pointer + 头 + 长度槽`，
    /// AOT 侧 `cmp eax,[r9+0x18]` 与 `mov r15,[rcx+rsi+0x20]` 两边对上）。
    /// 步长：`Fragment` 0x50（`GetFragment` 0x1D7B570 里 `lea rcx,[rax+rax*4]; shl rcx,4`）、
    /// `MeshStructure` 0x58（`ToPenetrationFragment` 0x1D7A390 里 `imul rcx,rcx,0x58`）。
    /// 字段偏移取自 dump.cs 的 `Fragment`(873813–873827) / `MeshStructure`(874055+) 字段表，与 AOT 读到的位置逐条核对过。
    static class N
    {
        internal const int ArrLen = 0x18, ArrData = 0x20;
        internal const int FragStride = 0x50, MeshStride = 0x58;
        // Fragment（数据相对）：板内行程 / 命中的板下标（＝官方 HitTransform 的出处）/ 旗标（`OriginalPenetrator=1`）
        internal const int F_Travel = 0x0C, F_Structure = 0x42, F_Flags = 0x48;
        // MeshStructure（数据相对；`transform` 就是官方 `HitTransform` 的出处）
        internal const int M_Transform = 0x00;

        internal static int Len(IntPtr arr) => arr == IntPtr.Zero ? 0 : Marshal.ReadInt32(arr + ArrLen);
        internal static IntPtr Elem(IntPtr arr, long index, int stride) => arr + (int)(ArrData + index * stride);
        internal static float F32(IntPtr e, int off) => BitConverter.Int32BitsToSingle(Marshal.ReadInt32(e + off));
        internal static short I16(IntPtr e, int off) => Marshal.ReadInt16(e + off);
        internal static ushort U16(IntPtr e, int off) => unchecked((ushort)Marshal.ReadInt16(e + off));
        internal static IntPtr Ptr(IntPtr e, int off) => Marshal.ReadIntPtr(e + off);
    }

    /// 一枚破片的**原生视图**：只有元素起始地址（= 数组 `Pointer + 0x20 + i*0x50`），字段一律按 `N` 的偏移自取。
    /// 之所以不叫 `PenetrationFragment`：对外那份在 interop 里读不了（类头 ①），而 `ToPenetrationFragment` 证明
    /// 它除了 `HitTransform`（＝`meshes[hitStructureIndex].transform`）之外没有内层拿不到的信息。
    readonly record struct Frag(IntPtr Data)
    {
        internal float Travel => N.F32(Data, N.F_Travel);
        internal short Structure => N.I16(Data, N.F_Structure);
        internal ushort Flags => N.U16(Data, N.F_Flags);
    }

    /// **末次原弹头**：复刻 `GetFinalOriginalPenetrator`（RVA 0x1D7B630）的机器码 —— 从 `fragCount-1` **往下**找第一个
    /// `flags` 含 `OriginalPenetrator(1)` 的元素，下标同时过 `fragments` 的长度界（官方那边是 `jae` 跳过）。
    /// 不叫它那个方法的原因：`out PenetrationFragment` 只有 8 字节槽，被调方要写 0x48 ⇒ 踩栈。
    static Frag? FinalPenetrator(IntPtr arr, int poolLen, int fragCount)
    {
        if (arr == IntPtr.Zero) return null;
        int n = Math.Min(fragCount, poolLen);
        for (int i = n - 1; i >= 0; i--)
        {
            var f = new Frag(N.Elem(arr, i, N.FragStride));
            if ((f.Flags & (ushort)FragmentFlag.OriginalPenetrator) != 0) return f;
        }
        return null;
    }

    /// **官方部件身份**：`meshes[hitStructureIndex].transform → VehicleObject`。
    /// 反汇编 0x1D7A390 里 `hitStructureIndex == -1(0xffff)` 才把 `HitTransform` 置空，否则取的就是这一格
    /// （`imul rcx,rcx,0x58` + `mov r15,[rcx+rsi+0x20]` → `mov [r12+0x40], r15`）⇒ 这里重建出来的就是官方那一份，不是"另一种猜法"。
    /// 走原生读取而不用 `meshes[i].transform`：`MeshStructure` 是 class 化结构，字段访问器偏 −0x10 ⇒ 会拿到 vtable 当指针用（AV，抓不到）。
    static VehicleObject? OfficialPart(IntPtr meshes, int meshesLen, short structureIndex)
    {
        if (meshes == IntPtr.Zero || structureIndex < 0 || structureIndex >= meshesLen) return null;
        IntPtr t = N.Ptr(N.Elem(meshes, structureIndex, N.MeshStride), N.M_Transform);
        if (t == IntPtr.Zero) return null;
        return ObjectOf(new Transform(t));
    }

    /// 数组原生指针 + 界：`Pointer` 是 `Il2CppObjectBase` 的属性（拿的是真数组对象地址），
    /// `Length` 走 `il2cpp_array_length`；两者取小当界，越界一格都不读（万一原生 `+0x18` 与接口读出的不一致，宁可少读）。
    static IntPtr Arr(Il2CppArrayBase? a, out int len)
    {
        IntPtr p = a?.Pointer ?? IntPtr.Zero;
        len = p == IntPtr.Zero ? 0 : Math.Min(N.Len(p), a!.Length);
        return p;
    }

    /// 把游戏这次命中跑完的那份模拟读成 `Info`（全部按原生偏移自取）。
    /// 弹头那一格认不出部件时，退一步在破片表里找**第一个认得出部件的**（spall 破片同样带 `hitStructureIndex`）。
    static Info? Read(PenetrationSimulation? sim)
    {
        if (sim == null) return null;
        try
        {
            IntPtr fragArr = Arr(sim.fragments, out int poolLen);
            IntPtr meshArr = Arr(sim.meshes, out int meshLen);
            int n = Math.Min(sim.fragCount, poolLen);
            var fin = FinalPenetrator(fragArr, poolLen, sim.fragCount);
            VehicleObject? hitObject = fin == null ? null : OfficialPart(meshArr, meshLen, fin.Value.Structure);
            for (int i = 0; hitObject == null && i < n; i++)
                hitObject = OfficialPart(meshArr, meshLen, new Frag(N.Elem(fragArr, i, N.FragStride)).Structure);
            // 弹头和整张破片表都没认出一个部件 ⇒ 这份读数没有可用信息，交给调用方兜底
            if (hitObject == null && fin == null) return null;
            return new Info(hitObject?.EntityID ?? -1, hitObject != null, fin?.Travel ?? 0f);
        }
        catch (Exception ex)
        {
            Plugin.ModLog.LogWarning($"TOOL_108 官方模拟读数失败（这一发不改判定）：{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    internal static bool Sample(IDamageModelStructure? model, Vector3 point, Vector3 direction,
                                float diameter, float mass, float speed, float maxNormalization, out SampleReadout readout)
    {
        readout = default;
        var compound = model?.TryCast<IReadOnlyCompoundStructure>();
        if (compound == null) return false;
        try
        {
            var settings = new SampleSettings(diameter, mass, speed, maxNormalization);
            // interop 把游戏的 `in SampleSettings` 摊平成 `ref`（实测编译口径），传的是**我们自己的局部变量**，
            // 所以这里不存在"写回别人的结构体"那一类风险 —— 判死的 AV 雷区是**补丁参数**里的写回 byref，不是自己调用时传引用。
            // 起点往后退一点：着弹点正好在表面上，从表面起算容易被自己的板判成 0。
            var origin = point - direction.normalized * 0.05f;
            if (!compound.Sample(origin, direction.normalized, 3f, ref settings, out var hit)) return false;
            readout = new SampleReadout(hit.Thickness, hit.LOSThickness, hit.EffectiveThickness,
                                        hit.SurfaceIncidenceAngle, (hit.Flags & SampleFlags.Overmatch) != 0);
            return true;
        }
        catch (Exception) { return false; }
    }

    /// **官方几何的第二层板**（间隙装甲判定）。从着弹点沿弹道往前 `skip` 米（越过第一层）再打一条射线，
    /// `maxGap` 内撞到东西就返回它的距离。走 `IDamageModelStructure.Raycast` ⇒ 用的是伤害模型自己的板，
    /// 不吃"有些板没有 Unity 碰撞体"那个亏（实跑见过 5~7 米的假距离）。
    internal static bool SecondSurface(IDamageModelStructure? model, Vector3 point, Vector3 direction,
                                       float skip, float maxGap, out float distance, out string info)
    {
        distance = -1f; info = "";
        if (model == null) return false;
        try
        {
            var dir = direction.normalized;
            var origin = point + dir * Mathf.Max(0.001f, skip);
            if (!model.Raycast(origin, dir, maxGap, out var hit)) return false;
            distance = hit.Distance;
            info = $"法线=({hit.Normal.x:0.##},{hit.Normal.y:0.##},{hit.Normal.z:0.##})";
            return true;
        }
        catch (Exception ex)
        {
            info = $"官方射线失败 {ex.GetType().Name}";
            return false;
        }
    }
}
