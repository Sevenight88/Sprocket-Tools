using System.Runtime.InteropServices;
using BepInEx.Configuration;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Sprocket;
using Sprocket.Vehicles;
using Sprocket.Vehicles.Cannons;
using Sprocket.Vehicles.Control;
using Sprocket.Vehicles.Turrets;
using Sprocket.Vehicles.Weapons;
using UnityEngine;

namespace SprocketTools;

/// 火炮垂直稳定器（单稳）与双向稳定器（双稳）。**实验性**，只发瞄准指令，不改任何游戏数据。
///
/// 开关在设计器里那格面板（见 `StabilizerPanel.cs`），存在火炮自己的 `CannonInstanceBlueprint.Flags`
/// 空位上（bit2=垂稳、bit4=双稳）：那块 `flags` 随设计存档走（`CannonInstanceBlueprint` dump.cs:864727，
/// 字段 `Flags`@0x38，`WeaponFlags` 是 sbyte 且官方只定义了 `Main=1`），战斗里从 `cannon.InstanceBlueprint`
/// 直读，中间没有翻译层。2026-10-06 实跑三次证实这条存盘链路通了：面板勾上写 2／6，战斗里读回 6。
///
/// 接缝与原理：
/// - 瞄准路都实现 `IAimable.AimAtDirection(Vector3 世界方向, SetAimFlags)`：俯仰＝`LayingDriveBehaviour`
///   （dump.cs:870592，形参名 `worldDirection`；它自己就带 `elevationInfo`@0x40 与 `azimuthInfo`@0x54，
///   也就是**一个对象管两轴**），方位＝`TurretBehaviour`（dump.cs:876897，形参名 `value`）。
///   反汇编确认该方法**只把方向存进 `targetDirection`（+0x34）并置 moving 标志**，真正动炮的是游戏每帧调的
///   `MoveToTarget`（870679）。后挂在原方法之后跑，所以我们这次写是**当帧最后一次写**。
/// - **实测：游戏每帧都在调接缝**（08:23 那场 30 秒 3657 次 ≈ 两条 × 每帧一次）。所以第一版「宽限期后再接管」
///   永远轮不到（日志 `发指令 2 帧｜外部瞄准指令 903 次`）；从第二版起改成在同一接缝上**滤波改写游戏这一帧要的
///   方向**：把世界方向拆成俯仰／方位两个角，只对要稳的那一轴做低通（`Aim smoothing seconds`），角度差超过
///   `Aim capture degrees` 直接放行（炮手大幅转向不拽）。
/// - 垂稳（bit2）＝只滤俯仰轴；双稳（bit4）＝两轴都滤。面板勾双稳会顺带打开垂稳。
///
/// 四版各自的死因（全部有日志实证，别再回退到这些写法）：
/// 1. 三条挂点挤一个 try ⇒ 一条炸掉整段静默死亡；且「宽限后接管」撞不上每帧的外部指令。
/// 2. `Scan()` 每次 `lanes.Clear()` ＋一条驱动没登记就上每帧重扫 ⇒ 滤波器每次重新落锁到游戏当前方向，
///    等于不改（`落锁 俯仰=-0.78° 方位=1.94°` 同一组数字连打三遍，`本条改写 -0.03°`）。
/// 3. 3a 版把「俯仰机制指针」和「方位机制指针」当两条路登记——**这门炮两者是同一个对象**（08:44 实测
///    `俯仰=LayingDriveBehaviour@0x1A68BC402D0 方位=同一个@0x1A68BC402D0(不是 TurretBehaviour)`），
///    第二次登记把第一次顶掉 ⇒ 本场 `落锁` 0 次。现在一条驱动按**两个轴**登记（`pitch`／`yaw` 旗标取并集），
///    同一个对象被两条接缝各登记一次也只有一份状态。
/// 4. 3b 版（08:50 实跑，用户颠簸试验：炮完全跟着车体起伏）**登记到的对象根本不是动炮的那一条**：
///    炮声明的机制是 `LayingDriveBehaviour@0x1EEFFC497E0`，每帧被调的却是 `@0x1EEFFC49F30`
///    （`收到 983 次游戏指令｜陌生驱动 496 次`，实测行 `落地校验 0/0` ⇒ 我们一帧都没写进去）；
///    方位轴更是 `本车炮塔回旋驱动 0 条`。根因是这两个类**不是 MonoBehaviour**：`LayingDriveBehaviour`
///    （dump.cs:870592）与 `TurretBehaviour`（dump.cs:876897）的直接基类是 System.Object、只实现接口，
///    所以 `GetComponentsInChildren<TurretBehaviour>` 必定返回空数组。真正挂在车上、能按车枚举的是两个
///    `VehicleComponent`（VehiclePart : MonoBehaviour，dump.cs:723379）：`LayingDrive`（dump.cs:870346，
///    属性 `Behaviour`  backed by 字段@0xE8）与 `TraverseMotor`（dump.cs:877163，`Behaviour`@0xD8）。
///    3c 起驱动对象就从这两个组件身上取。
///
/// 3d 的探针把争议结了（09:2x 实跑，用户原话：**「明显抬高约 4° 生效的，跟着车体一起晃」**）：
/// ① 我们写的对象**就是**他眼睛看见的那根炮管——`trunnions` 没认错、接管也没认错（`落地校验 760/760`），
///    「四层变换里哪一层才是看见的炮管」这个问题不必再查了。
/// ② 病在**权限**，不在方向。同一份日志：`游戏要`（俯仰）整场钉在 −0.51°，同期 `车根` 从 −5.83° 晃到 +4.35°，
///    而 `本条改写` 只有 0.00° ⇒ **游戏送进接缝的世界方向本来就是稳的**，给一个本来就稳的数做低通＝什么都没改。
///    这就是前三版「完全没效果」的真正原因（不是没挂上，是挂上的那条本来就没有颠簸可滤）。
/// ③ 炮管跟着晃发生在**下一层**：炮实际能转多快由每帧的
///    `MoveToTarget(float deltaTime, float speedMultiplier, AimFlags flags)`（dump.cs:870679／877008）决定。
///    反汇编 `UpdateAxis`（RVA 0x1DE29C0，两条指令就写完）：`maxSpeed = speedMultiplier × RotationAxisInfo.MaxSpeed`
///    （字段 0x20 ← `info.MaxSpeed`@0x4），`maxAcceleration = (info.MotorTorque + mass·centreOfMass·gravity)/momentOfInertia`
///    （→0x18），`maxDeceleration = (info.MotorTorque − mass·centreOfMass·gravity)/momentOfInertia`（→0x1C，本方字段
///    `mass`@0x68／`centreOfMass`@0x6C／`momentOfInertia`@0x70）。**极速只有 2°/s 的驱动追得上 5°/s 的车体才算稳**。
///    两条 `MoveToTarget` 开头都有 `if (speedMultiplier <= 0) return`（0x1DE1FBF／0x1E1091C 的 comiss），
///    所以这个倍率只能乘、不能置 0。方位侧另有两个公共属性 `MotorTorqueMultiplier`/`MaxTraverseRate`（876922-876925）
///    可直接改写（扭矩权限），本版没动它们。
///
/// 3e＝**把稳定器做成陀螺仪＝额外的转向权限**：前挂上面两条 `MoveToTarget`，只对「本车 + 勾了稳定器」的那条驱动
/// 把 `speedMultiplier` 乘上 `Stabilizer lay authority`（1＝不加权限＝现状）。方向语义、滤波、行程极限、物理全是
/// 游戏自己的，我们只给它顶住车体的能力——就是用户要的那套：「火炮本身移动只能 2°每秒，但有这套机制颠簸 5°每秒依旧能稳」。
/// 一次实跑要读三样东西判成败：`驱动权限 加N 次`（挂点真被调到没有，0 次＝钩错方法）、
/// `游戏给的倍率 lo~hi`（看清基线；lo 若为 0 说明有路径真的传 0 进来，那我们的乘法等于没加）、
/// `车体峰速率 x°/s`（颠簸到底几度每秒，配 `摇架` 那列才知道权限够不够）。
/// 那一轮的结案读法（`Probe barrel offset degrees` 已随探针退场）：炮管肉眼抬起约 4° ⇒ 我们写的确实是这根炮管
/// （`trunnions` 没认错），但它跟着车体晃 ⇒ 病在驱动权限不够，不在方向语义。
///
/// 3e 实跑（09:4x，两场）结论：**稳态已经钉住**——`车根 −11.99°` 时 `摇架 0.36°`（隔离 12.35°），
/// `游戏给的 1.00~1.00｜挂点被调 9082 次｜真加 9082 次` ⇒ 权限每帧都吃到、顶到 6 倍极速。
/// 但 15 行 `实测` 里剧烈颠簸那一瞬仍有残差（`摇架 −10.97°` 对 `锁定 0.35°`，同期车体峰速率 15.5°/s；另有 2~3° 的几行）。
/// 3f＝查这个残差撞的是什么：`UpdateAxis` 里 **speedMultiplier 只乘 MaxSpeed（→0x20）**，
/// `maxAcceleration／maxDeceleration`（→0x18／0x1C）只由 `MotorTorque`@0x0 与 mass·centreOfMass·gravity／惯量算，
/// 完全不乘权限 ⇒ ×6 只抬高上限速度，**炮身掉头的快慢没变**。两条办法分开验：
/// ① 只读探针：新增 `峰速率 炮身/车体` 两列（同一窗口里各自量到的最大 °/s）与 `驱动参数 力矩(原)/极速/质量/力臂/惯量`，
///    回旋那条新增 `回旋上限 加速/刹车/极速/力矩倍率`（`TurretBehaviour` 的 0x10/0x14/0x18/0x30，全是公共可写属性背后）。
///    炮身峰速率 ≈ 6×极速 ⇒ 权限吃到了、残差是加速度；炮身峰速率 ≈ 1×极速 ⇒ 权限根本没作用到极速上，病还在别处。
///    回旋那四列若在两次读数间自己变 ⇒ 游戏每帧重算，写属性会被顶掉（那就只能走别的路）。
/// ② 新旋钮 `Stabilizer lay torque multiplier`（默认 1＝关）：>1 时把这条俯仰驱动对象的
///    `elevationInfo.MotorTorque`（0x40）放大，让游戏自己把加速度算大。写的是运行时对象字段，原值存 `lane.torqueOrig`（3j 起改成按偏移存 `lane.origs`），
///    停稳／掉条目／旋钮回 1 都写回；不进蓝图、不动存档、只碰已认领为本车且勾了垂稳的那条。方位侧等①的数字再决定。
///
/// 3g 实跑（10:0x，权限 ×20 场）把「摇架钉住」这个前提打掉了：剧烈颠簸的窗口里 `摇架 摇架父级 支座 车根` **四层同值**
/// （`−6.07 / −4.55 / −4.55 / −4.55`、`6.44 / 7.09 / 7.09 / 7.11`，同期 `锁定 −0.19`），用户 10:0x 的判词也是
/// 「炮管本身在晃」。而 `峰速率 炮身 ≈ 车体`（14.8/13.3、25.4/23.4、11.1/11.7）——这一列本来就废：
/// 摇架是世界父级带着转的，它的 °/s 里含车体速率，两个数相等只说明**轴没相对支座转出多少**，量不出轴自己在追什么。
/// 3g 顺手加的姿态／变换链两行也没抓到位（`3g 链 trunnions <- turretRing <- New Vehicle`，父级就是车体，没有第二层管子）。
///
/// 3h＝改量**轴自己**：`get_TargetAngleDeviation`（RVA 0x1DE19A0）两轴各取 `state[0x14] − state[0x00]` 的大者
/// （state 由 `0x1D39080(handle, 0)` 解析，元素长 0x38；3i 反汇编定下方向：`+0x00`＝已施加到摇架 localRotation 的角，
/// `+0x14`＝本帧由世界方向投影出的目标角）⇒ 这是**不含车体**的轴内偏差（实测会带负号，量级和 `我算偏差` 一致）。
/// 配上 `我算偏差 =|锁定 − 摇架|`（同一件事的世界算法），一次跑就裁决：
/// ① `游戏 ≈ 0` 而 `我算` 好几度 ⇒ 这条驱动自认为已经在靶上 ⇒ 它追的目标根本不是我们给的世界方向（车体系／陈旧角）
///    ⇒ 权限、力矩全都无效（正好解释 3e/3f/3g 三版「开不开一个样子」），要改的是它写入轴目标那一步；
/// ② `游戏 ≈ 我算` ⇒ 它确实在追我们给的世界方向、只是追不上 ⇒ 病在速率／加速度，`Stabilizer lay authority`
///    与 `Stabilizer lay torque multiplier` 这条路成立。
/// 同时补读 `行程 Min@0x48~Max@0x4C`（若 `摇架 − 支座` 顶在行程端点＝俯仰被限住，那是第三种病）、
/// 四个轴状态属性（`ElevationLocked`／`AzimuthLocked`／`TargetAchievable`／`HasTarget`）与原生单字节旗标
/// `moving@0x30／aimSetFlags@0x31／hasAppliedTarget@0x32／disposed@0x33`。属性全走 interop 的真实方法（非接口桩），逐帧一次、全裹 try。
///
/// 3h 实跑（10:4x，权限 ×20 场）当场把上面两种可能裁决掉了：`偏差 游戏 6.35 我算 6.28`、`峰 13.83 对 13.85`
/// —— 两列**同值**（游戏那列带符号，绝对值一致）⇒ ① 轴确实在追我们给的世界方向（“车体系／陈旧目标”这个猜测死了），
/// ② 病是**追不上**：一个 2 秒窗口里误差能涨到 13.9°，同期 `车体峰速率` 22.7°/s。
/// 3i＝顺着这条结论把 `MoveToTarget`（RVA 0x1DE1F50，整段到 0x1DE2431）读完，真正的权限口在这里：
/// ① 世界方向→轴角是**每帧重投影**的（0x1DE1FF3 起：foundation 的 `InverseTransformDirection` × `zeroLocalRot`，
///    交给 0x30954c0 拆轴、`atan(y/z)×57.29578` 写进轴状态 `+0x14`）⇒ “把世界方向稳住”这件事游戏自己一直在做；
/// ② 每帧步长 `state[+0x20] = 第三浮点参 × healthFraction × MaxSpeed`：俯仰在 0x1DE2421 用 `elevationInfo.MaxSpeed`@0x44、
///    方位在 0x1DE2349 用 `azimuthInfo.MaxSpeed`@0x58（`RotationAxisInfo` 内联：力矩@+0x0 极速@+0x4 Min@+0x8 Max@+0xC）；
///    力矩只进 `state[+0x18]/[+0x1C] = (力矩 ∓ mass·centreOfMass·gravity)／惯量`＝加速度，不乘任何倍率；
/// ③ 而 `speedMultiplier`（第 2 个浮点形参，实测游戏永远传 1.00）**只在开头那一句 `if (speedMultiplier <= 0) return`
///    （0x1DE1FBF）被用到一次**，之后再没参与计算 ⇒ 3e／3f／3g 三版乘它，是乘在一段死代码上——
///    这就是「×6 和 ×20 都开不开一个样子」的根因。（`UpdateAxis`（RVA 0x1DE29C0）确实把 speedMultiplier 乘进 MaxSpeed，
///    但 `find_callers` 扫到 0 个调用者＝没被走到，3f 的推断错在这里。）
/// ⇒ 权限必须写在**活的口**上：`Stabilizer lay authority` 从 3i 起改成按倍率放大写回 `elevationInfo.MaxSpeed`@0x44
/// （炮塔回旋＝`TurretBehaviour.maxTraverseRate`@0x18），力矩旋钮继续管 0x40；`speedMultiplier` 从此**只读不写**，
/// 读它只为盯住游戏哪天改了口径。两者都在 `Boost` 每帧写、停稳／丢条目／旋钮回 1 写回原值（运行时对象字段，不进蓝图与存档）。
///
/// 3i 实跑（11:0x，权限 ×20、力矩 ×1）＝**数字上垂稳已经钉住**（肉眼验收还没走完，别当已结案）：
/// `极速 180.00（原 9.00）`挂得住（不是每帧重算），
/// 车体峰速率 29.7°/s 的窗口里 `我算俯仰 0.07°`；同场还顺手证伪了 3h 的另一种担心——`游戏传的 speedMultiplier 1.00~1.00`
/// 全程恒定，乘它确实等于什么都没乘。残差全部落在**双稳**（方位）侧，日志里两类：
/// ① 好几窗 `俯仰我算已到位⇒游戏那列是方位轴贡献`（`偏差 游戏 39.50 我算俯仰 0.13`）⇒ 方位轴没跟上；
/// ② 车体峰速率 20~70°/s 的窗里方位我算能到 12~16°。根因两条，都是 3i 自己留的口子：
///    回旋只抬了**极速**（`极速 384.00（原 19.20）`），**加速度两位没碰**（`加速 19.52 刹车 97.59`）；
///    摇架自带的方位轴（`azimuthInfo`：力矩@0x54／极速@0x58）在车体 mounted 炮上是**真正的方位执行机构**，3i 一行没写。
/// 3j＝补齐这两条并把三处仪器缺陷一次修掉（本版全是“把已定的机制做全＋把读数做诚实”，没有新猜的路）：
/// ① 权限写满七个字段：俯仰 0x40／0x44，摇架方位 0x54／0x58（`lane.yaw` 才写），炮塔 0x18 极速＋**0x10／0x14 加速度**；
///    加速度走**力矩旋钮**（`Stabilizer lay torque multiplier`，默认 1＝关，测的时候要抬到 ~6）。
///    原值改按偏移存 `lane.origs`（3i 用单个 `speedOrig` 同时兜 0x44 与 0x18，两轴一起稳会互相踩原值）。
///    炮塔没写官方入口 `MotorTorqueMultiplier`@0x30 而直接写它算出来的两位：倍率字段只在车被评估时喂这两位，
///    写它这一帧看不见效果；3i 已证这几位不是每帧重算，写值＋每帧重写安全。
/// ② 归位窗：刚接管 3 秒内**不记峰值**（实测进场第一窗 `偏差 峰 170~260°` 是出生／炮归位，却把判词一路顶成
///    “在追但追不上”）；读数行明写“归位窗”。
/// ③ 陌生驱动计数按归属拆成“自己车／别家”，并且**自己的车不等 `ScanEvery`**（0.25 秒下限立刻重扫）——
///    这场实测出现过“在自己车变换树上、却报‘没认领’”的驱动，接缝跑在认车前头，原判词是自相矛盾的假失败。
/// ④ 方位单独一列：`我算方位 =|锁定方位 − 摇架世界方位|`（与俯仰同口径），把游戏那列“两轴取大”拆开归因；
///    再加一列 `驱动在追 俯仰／方位`＝原生 `targetDirection`@0x34 的读数（纯 Marshal 读，不碰属性桩），
///    它和 `锁定` 不一致就是“我们写的方向没走到这台驱动”，一致才轮到谈权限。
/// 顺带官方对号：3h/3i 靠反汇编推出来的 0x38 字节轴状态就是 `RotationAxisPhysics`（TypeDefIndex 11052），
/// dump.cs 直接给了名字——`angle@0x0／velocity@0x4／acceleration@0x8／min@0xC／max@0x10／target@0x14／
/// maxAcceleration@0x18／maxDeceleration@0x1C／maxSpeed@0x20／apply@0x28／extrapolationDeltaTime@0x30／
/// flags@0x34／result@0x35／axisFlags@0x36`。⇒ 0x20 那一位**是轴的 maxSpeed**（每帧被改写成“本帧允许走的量”），
/// 3h 猜的“副角／选择子”其实是 velocity 与 extrapolationDeltaTime（后坐力外推项）。中心积分器＝
/// `RotationAxisPhysicsRegister.Update`（RVA 0x1D38D90，fixedDeltaTime=0.01，两条轴共用一张表）。
///
/// 4L＝**把重心从「追得上」挪到「抗得住」**（2026-10-06 拿第三方「火控单元」的双稳写法作对照比出来的，只借思路不借代码）：
/// 它那套稳定器一行控制环都没有，只在 `LayingDrive.Initiate` 上一次性把**蓝图轴**的 `TorqueMultiplier` 抬到 180、
/// `Ratio` 钳到 ≤8；按官方换算（极速=180/比率、力矩=比率×125×力矩倍率）＝「极速 22.5°/s、力矩 ×180」——
/// **极速保守、力矩给足**。我们到 3k 为止正好反着：`lay authority` 开到 ×20（=180°/s），
/// `lay torque multiplier` 一直是默认 1＝没开过。§25 的鬼畜就是这么来的：只有上限速度、没有刹车能力的伺服
/// 会绕着目标自振并撞行程端点（摇架 std 11° vs 车体 3.4°，单帧最大跳 24.9°）。本版的三条实质改动：
/// ① **只抬不压 ＋ 地板与封顶两道夹**：极速那几位（俯仰 0x44／摇架方位 0x58／炮塔 0x18）改成
///    `clamp(原值 × Stabilizer lay authority, Stabilizer lay rate floor deg/s, Stabilizer lay rate cap deg/s)`，
///    最后再兜一条「不低于原值」⇒ 玩家自己造的快炮不会被压低（地板不做天花板、封顶也不压原值）。
///    为什么**地板**是主角：高低机出厂只有 1.4~1.6°/s（=180/比率），×4 也才 6°/s，而实测车体俯仰峰速率到过
///    29.7°/s ⇒ 光乘倍率永远追不上颠簸，看上去就是「跟着车体一起晃」。地板（默认 30°/s）才是把「炮本身 2°/s、
///    颠簸 5°/s 也要钉住」这条判据写进代码的地方。封顶（默认 60°/s）是 §25 的闸门：老 cfg 里残留的 ×20
///    会被它夹住，倍率写几都拆不成自由振动。两个数都是**绝对 °/s**，不随原值漂。
/// ② **力矩按车体参数反解成「角加速度地板」**：新旋钮 `Stabilizer min lay acceleration deg/s2`（默认 250）。
///    俯仰两轴能写的字段是 `MotorTorque`（0x40／0x54），单位是力矩不是加速度 ⇒ 拿游戏自己的式子反解：
///    `需要的力矩 = 惯量×角加速度 + |质量×9.81×力臂|`（惯量@0x70／质量@0x68／力臂@0x6C，3f 反汇编
///    `maxAcceleration=(力矩∓质量·力臂·重力)/惯量` 时定下的同一批字段），再取 `max(原值, 反解值)`；取 `+|…|`
///    是为了让**刹车**那一侧也至少到 250。炮塔那两位本来就是加速度（0x10／0x14）⇒ 直接 `max(原值, 250)`。
///    250°/s² 的口径＝能在 0.12 秒里从 0 顶到 30°/s、也能 0.12 秒刹住，这就是「陀螺仪」和「追得快的伺服」的差别。
///    读数行同时打**反解出来的目标力矩**和**写进去之后的实际读数**：单位读错一眼看得出来（`力矩` 那列和
///    `惯量×250` 差好几个数量级＝0x68/0x70 的语义我搞错了，那要先修读数，别调参）。
/// ③ **权限从每帧热路径搬出去**：两条 `MoveToTarget` 前挂从此只做计数与采样，**不再每帧往原生偏移写**。
///    写入只在三个时刻：`LayingDrive.Initiate`（RVA 0x1DDE880）／`TraverseMotor.Initiate`（0x1E11E80）后挂
///    （车被重新评估、掉血重建都会走）＋ 认车重扫末尾（≤1 秒一次）＋ 归属认领成功那一刻。
///    3i 已经证过这几位不是每帧重算（`极速 180.00（原 9.00）`挂得住），
///    所以搬出去没有正确性代价；好处是热路径少七次 Marshal 写，按我们自己的 AV 判据少一整类每帧裸写风险。
///    两个 `Initiate` 挂点**只认声明在该组件自己身上的那个**（`declaredOnly`）——解析到基类 `VehicleComponent.Initiate`
///    就等于给场上每个部件每帧挂一次，那是我们见过的最贵的错法。
///
/// 明确**没借**的三件（它的糟粕）：① 它写 `LayingDriveBlueprint.Axis`——dump.cs:870719 那一行是
/// `[Serializable] public class LayingDriveBlueprint.Axis : ISerializable`，而且 `LayingDriveBlueprintContainer : ScriptableObject`
/// 只持有**一个** `blueprint` ⇒ 改的是全游戏共享的资产对象，还会烤进存档（这场实测它往 Autosave 里烤的 180/8，
/// 我们上轮才洗回 112.3/1）。我们继续只写运行时对象字段。② 它不分成逐炮开关，敌车与 AI 的炮一起被抬。
/// ③ 它的挂点用 `GetMethods().FirstOrDefault(m => m.Name == "Initiate")` 按名字取第一个重载，不看签名。
/// 它「在主菜单版本号那行后面追加『双稳已失效』」的思路好，但落地不干净：dump 里搜不到版本标签的专用字段
/// （`GameVersion` 只有 `VehicleBlueprintHeader`@728909 那一个是图纸头），只能全局劫持 `TMP_Text.set_text`，
/// 而汉化 DLL 后挂同一个 setter 时是把文本**整体换成译文** ⇒ 最后一个写的人赢，后缀会被吃掉。
/// 所以「失效可见性」这味药改放在设计器那一格面板里（`StabilizerPanel` 的状态行：几条挂点没接上、
/// 游戏版本对不对得上已验证版、接管几条驱动、上一次重写写进了几条）。
///
/// 滤波这一路（`Aim smoothing seconds`）默认**关**（0）：3d 的探针已经结过案——游戏送进接缝的世界方向本来就是稳的
/// （`游戏要` 整场钉在 −0.51°，同期车根 −5.83°→+4.35°），给它做低通＝什么都没改，只是在 §25 那种高权限下
/// 多一个会跟原生重投影互相顶的写入者。旋钮留着（想试「软锁定」再抬上去），但本版起它不是机制的主体，
/// 主体是①②给这台伺服的抗扰能力。
///
/// 归属判定（3c）：先扫**全部** `ControlType.Player` 的车（3b 只取第一台，是个缺陷）；每辆车用
/// `GetComponentsInChildren<LayingDrive>`／`<TraverseMotor>` 取 `.Behaviour` 登记进轴，并把该车变换树上
/// **每一个** Transform 指针记进 `ownTransforms`。陌生驱动顺着它的 `trunnions` 往上找父级，命中这张表才算本车
/// （`LayingDriveBehaviour.trunnions`@0x10 是公共字段，能读；`TurretBehaviour.transform`@0x38 是私有的，
/// 所以方位接缝只报数不认领）；不在表里就只报一次名。
/// **不做「看见陌生驱动就无条件接管」**：同一条接缝上 20 秒内出现过三个不同指针（`0x1A9427BE080`／
/// `0x1A942B03080`／`0x1A937802500`），那是 AI 与敌军的车——认错就把 NPC 的炮也稳了。
/// 车上有多个炮塔／多门炮时它们都会跟着稳（日志逐条报组件 id）。
/// **已知粗糙处**（等机制验通再收）：车上一门炮勾了稳定器时，本车**所有**俯仰／回旋驱动都会稳
/// （旗标取的是整车的并集），没勾的那门也会被带上；要按炮分档得把 `LayingDrive.VehicleObject` 和炮对上。
///
/// 读数（每 2 秒最多一行，`Log stabilizer` 开着才打；3e 起**只要接缝被调到就打**，不再要求本帧真改写过）：
/// 汇总行给 `收到/改写` 次数、`陌生驱动（自己车／别家）`、`本条改写`、`探针偏移`、`驱动权限 极速×K 力矩×T（挂点被调／真写／游戏传的倍率区间）`；
/// `实测·俯仰` 取被调到最多的两条俯仰驱动，给 `游戏要 俯仰／方位`｜`锁定 俯仰／方位`｜`驱动在追 俯仰／方位`（3j：原生 targetDirection，
/// 和 `锁定` 不一致＝我们写的方向没走到这台驱动）｜`摇架`／`摇架父级`／`支座`／`车根`（从炮身一路量到车体的四层世界俯仰）｜
/// `写后回读`／`落地校验 对上/总次`｜`加权限 N 次`｜`峰速率 炮身/车体`（3g：两列同量级时无信息，摇架被车体带着转）｜
/// `偏差 游戏两轴取大／我算俯仰／我算方位 + 判据`｜`轴状态 + 四个原生旗标`｜`俯仰驱动 力矩(原)/极速(原)/行程`｜
/// `方位驱动 力矩(原)/极速(原)/行程`（3j 新增：车体 mounted 炮的方位执行机构就在这四位）｜`车体参数 质量/力臂/惯量`｜`写入倍率`。
/// 稳住了的样子＝`支座`／`车根` 随颠簸起伏而 `摇架` 贴着 `锁定` 不动，肉眼炮管钉住；
/// `摇架` 仍贴着 `车根` 走（3g 实测就是这样）⇒ 看 `偏差` 三列裁决：游戏≈0 而我算好几度＝驱动根本没在追这个世界方向
/// （加权限／加力矩都不会有用）；两列相当＝在追但追不上（俯仰那列对不上抬 `lay authority`，一直顶不住再抬
/// `lay torque multiplier`＝加速度；方位那列对不上就是 3j 新写的 0x54/0x58 与炮塔 0x10/0x14 的事）；
/// `摇架 − 支座` 顶在 `行程` 端点＝被行程限住，属第三种病。刚接管几秒的窗会标 `归位窗`，峰值不计，别当“没颠簸”。
/// `实测·回旋` 取被调到的炮塔驱动，给 `加速(原)/刹车(原)/极速(原)/力矩倍率` ＋两个写入倍率（`力矩倍率` 那一位本身没单位，
/// 别把它读成 °/s²）。`加权限 0 次` ⇒ 这两条 `MoveToTarget` 根本没被调到（钩错了方法）。
/// 注意 `写后回读` 取的是**上一次写完时的读数**，和同一行里“此刻”的 `锁定` 可能差一帧，别拿它当同帧对照。
/// 跨帧只为打实测读数留着驱动引用，读数全裹 try。
///
/// 战斗侧挂点不进 `features[]`（进了就会被 AtBoot 逻辑推迟到首次进设计器，纯战斗里就失效了）；开机没接上的由
/// `RetryPending`（`DesignEditor.Update` 每帧带来）按 1 秒退避重试——同一份二进制一轮全接上、另一轮开局就失败。
internal static class Stabilizer
{
    internal const byte VerticalBit = 2;   // 面板「垂直稳定器」
    internal const byte FullBit = 4;       // 面板「双向稳定器」

    /// 这套机制最后一次**逐条对着反汇编与实机日志验过**的游戏版本。对不上不当门拦截（那是玩家自己取舍的事），
    /// 只在日志与设计器面板的状态行上标一句「未验证」，免得玩家以为「没效果＝我车造错了」。
    internal const string TestedGameVersion = "0.2.55.5";

    const float Gravity = 9.81f;           // 反解力矩用的重力加速度（3f：`质量·力臂·gravity` 那一项）

    const float ScanEvery = 1f;            // 陌生驱动触发的重扫最多每秒一次
    const float RefreshEvery = 5f;         // 就算没有陌生驱动，也每 5 秒重扫一次（车上加装／拆件后重新认车）
    const float ReportEvery = 2f;          // 汇总/实测读数每 2 秒一行
    const int KeepScans = 3;               // 连续 3 次重扫没再被登记到的驱动才丢（换车／炮被拆）
    const float SettleWindow = 3f;         // 3j：刚接管的驱动这几秒内**不记峰值**（出生／进场归位的几百度摆动不是颠簸）
    const float OwnRetryEvery = 0.25f;     // 3j：接缝跑在认车前头时的立刻重扫下限（自己车上的驱动，比 ScanEvery 积极）

    // ---- 配置项（Register 里 Bind）----
    internal static ConfigEntry<bool>? Enabled;
    internal static ConfigEntry<float>? Smoothing;
    internal static ConfigEntry<float>? Capture;
    internal static ConfigEntry<float>? Authority;
    internal static ConfigEntry<float>? Torque;
    internal static ConfigEntry<float>? RateFloor;   // 极速地板（4L：稳定器要能压住车体的峰速率）
    internal static ConfigEntry<float>? RateCap;     // 极速封顶（4L：防 §25 那种「没边的极速＝鬼畜」）
    internal static ConfigEntry<float>? AccelFloor;  // 角加速度地板（4L：抗扰的本体）
    internal static ConfigEntry<bool>? Log;
    internal static bool versionMismatch;    // 当前游戏版本 ≠ TestedGameVersion（只标不拦）
    internal static string gameVersion = "";
    static int assertRuns, assertWrites;     // 面板状态行：重写了多少次、其中写进放大值的次数
    static string lastAssertLine = "";

    /// 一条瞄准驱动的稳定状态。键是驱动对象的原生指针。**一条驱动可以同时管两个轴**（实测这门炮就是），
    /// 所以旗标按轴记（`pitch`／`yaw`），不按“这是俯仰路还是方位路”记。锁角必须跨重扫活着，否则低通没有记忆。
    sealed class Lane
    {
        internal bool pitch, yaw;                // 这一条驱动要稳俯仰轴／方位轴（并集）
        internal bool declaredPitch, declaredYaw;// 本次重扫「重新声明」到的轴：炮塔勾掉了要能当场失效
        internal float lockedPitch, lockedYaw, lastTime;
        internal bool hasLock;
        internal int stamp;                      // 最近一次被重扫声明到的代号
        internal float lastDeltaDeg;             // 最后一次改写量，只给日志看
        internal int writeChecks, writeFails;    // 「写完立刻回读」的对上／对不上计数
        internal float lastWrittenPitch;         // 上一次改写后回读到的俯仰，给实测行用
        internal int hits;                       // 这条驱动被接缝调到过多少次（用来认出哪一条真的在动炮）
        internal float incomingPitch;            // 游戏最近一帧要的俯仰（度），和 `锁定` 对照才看得出有没有在压颠簸
        internal float incomingYaw;              // 3j：同一帧游戏要的方位（度），双稳的归因要看这一列
        internal int boosts;                     // 这条驱动的 MoveToTarget 被调到过多少次（0＝权限挂点没接上/没被调）
        internal int applied;                    // 里头真加过权限的次数（`Stabilizer lay authority` <= 1 时只数 boosts）
        internal float baseMin = float.MaxValue, baseMax = float.MinValue;   // 游戏自己传进来的 speedMultiplier 区间
        internal float hullPeakRate;             // 上一个读数窗口里量到的车体最大俯仰速率（度/秒）＝颠簸有多猛
        internal float hullLast, hullLastTime;
        internal bool hasHullSample;
        internal float barrelPeakRate;           // 同期炮身（摇架）最大俯仰速率＝我们实际追得多快
        internal float barrelLast, barrelLastTime;
        internal bool hasBarrelSample;
        /// 3j：被我们按倍率改写过的原生 float 字段 ⇒ 改写前的原值，键是该字段在这个驱动对象上的偏移。
        /// 俯仰轴 0x40 力矩／0x44 极速，摇架自带的方位轴 0x54 力矩／0x58 极速，炮塔 0x10 加速／0x14 刹车／0x18 极速。
        /// 「写过才要还原」这件事由“键在不在表里”表达，不再一个字段配一个 NaN 哨兵（3i 的 `speedOrig` 一个变量
        /// 同时被俯仰 0x44 和回旋 0x18 复用，两条轴一起稳就会互相踩原值——这就是 3j 换表的直接原因）。
        internal readonly Dictionary<int, float> origs = new();
        internal float devLast = float.NaN, devPeak;      // 3h：游戏自认的离靶偏差（两轴取大，不含车体）当前／窗口峰值
        internal float devOwnLast = float.NaN, devOwnPeak; // 同一窗口里 `我算偏差 =|锁定 − 摇架|`（世界算法）
        internal float devAzLast = float.NaN, devAzPeak = float.NaN; // 3j：方位轴同口径偏差＝把游戏那列「两轴取大」拆开归因（NaN＝这窗没读到，判词要用它分辨“没读”和“读到 0”）
        internal float claimedAt = float.MinValue;         // 3j：这一条被接管那一刻的 Time.time，归位窗从它起算
        internal bool inSettle;                            // 3j：上一次采样还在归位窗里（只给日志标注用）
        internal float gamePitchDeg = float.NaN, gameYawDeg = float.NaN;   // 3j：驱动身上 `targetDirection`@0x34 的原生读数（度）＝它此刻真的在追哪个世界方向
        internal bool elevLocked, azLocked, achievable, hasTarget;   // 3h：同一次采样里的四个轴状态
        internal bool adopted;                   // 靠变换归属认领下来的（不是炮声明的机制），重扫时按那辆车的旗标续期
        internal OwnRoot? own;                   // 认领时用的那台本方车
        internal string part = "";               // 归属组件的 ComponentID，只给日志看
        internal LayingDriveBehaviour? laying;   // 只为打实测读数留着（读数全裹 try）
        internal string typeName = "";
        internal float torqueTargetPitch = float.NaN, torqueTargetYaw = float.NaN;   // 4L：`MotorTorque`（0x40／0x54）按车体参数反解出来的目标力矩，读数行拿它和写后实际值对（单位读错当场看得出）
    }

    static readonly Dictionary<IntPtr, Lane> lanes = new();
    static readonly HashSet<IntPtr> reportedUnknown = new();   // 每个陌生指针只报一次名

    /// 本方车（`ControlType.Player`）的变换根 ⇒ 该车旗标并集。用来判陌生驱动的归属：
    /// 根在这个表里才是我们自己的车，否则是 AI／敌军（同一条接缝上确实出现过敌军车的指针）。
    sealed class OwnRoot
    {
        internal string name = "";
        internal bool vertical, full;
        internal int cannons;
        internal bool cannonsScanned;  // 3j：这次重扫真的把这台车的炮枚举完了（没读完⇒旗标还不可信，接缝要立刻催一次重扫）
        internal int transforms;
        internal int gen;            // 最近一次重扫里这台车出现的代号（Prune 用它给认领条目续期）
        internal Transform? hull;    // 这台车的变换根＝肉眼看得见的那层车体，实测行的 `车根` 列读它
    }
    static readonly Dictionary<IntPtr, OwnRoot> playerRoots = new();

    /// 本方车变换树里的**每一个** Transform 指针 ⇒ 它所属的车。认陌生驱动时顺着它的摇架往上找父级，
    /// 命中这张表就算本车（只用变换根会漏：炮塔件／动态件的根可能和车体根不是一个）。09:0x 实测
    /// `GetComponentsInChildren<TurretBehaviour>` 那类枚举不可靠，归属就改按“这条驱动的变换在不在这台车上”判。
    static readonly Dictionary<IntPtr, OwnRoot> ownTransforms = new();

    static int generation;
    static float lastScan = -1000f, nextSummary, nextReport, nextRetry;
    static int sawCalls, wroteCalls, unknownCalls;
    static int ownUnclaimedCalls, foreignUnclaimedCalls;   // 3j：把 `陌生驱动 N 次` 拆开——自己车上「该认还没认」的不算失败
    static float lastOwnRetry = -1000f;                    // 3j：立刻重扫（接缝先于认车）的节流时刻
    static string lastScanLine = "";       // 重扫每秒可能跑好几次，内容没变就不重复打
    static bool wasInDesigner;
    static Harmony? harmony;

    // 我们在后挂里重发的那一次不能再当成“游戏发的”，否则无限递归。
    [ThreadStatic] static bool selfCall;

    /// 由 `Plugin.Load` 在 `new Harmony(...)` 之后调用一次：Bind 配置＋接六条挂点（逐条，失败进 pending）——
    /// 两条瞄准接缝（后挂）＋两条 `MoveToTarget`（前挂，只做计数与采样）＋两条 `Initiate`（后挂，重写权限）。
    public static void Init(ConfigFile config, Harmony instance)
    {
        Register(config);
        harmony = instance;
        TryPatch(typeof(LayingDriveBehaviour), nameof(LayingDriveBehaviour.AimAtDirection), nameof(OnAimLaying), "俯仰接缝");
        TryPatch(typeof(TurretBehaviour), nameof(TurretBehaviour.AimAtDirection), nameof(OnAimTurret), "方位接缝");
        TryPatch(typeof(LayingDriveBehaviour), nameof(LayingDriveBehaviour.MoveToTarget), nameof(OnMoveLaying), "俯仰驱动采样", true);
        TryPatch(typeof(TurretBehaviour), nameof(TurretBehaviour.MoveToTarget), nameof(OnMoveTurret), "回旋驱动采样", true);
        // 两条重建钩子：车被 Initiate（进场、加装改装、掉血重评估）之后，游戏会拿蓝图重算运行时参数，
        // 我们的写入要跟着补一次。`declaredOnly`＝只认同名方法里声明在组件自己身上的那个，见 TryPatch 的注释。
        TryPatch(typeof(LayingDrive), "Initiate", nameof(OnInitiate), "俯仰驱动重建", false, true);
        TryPatch(typeof(TraverseMotor), "Initiate", nameof(OnInitiate), "回旋驱动重建", false, true);
        ReportPatchState(false);
    }

    static string SafeVersion()
    {
        try { return UnityEngine.Application.version ?? ""; }
        catch (Exception) { return ""; }
    }

    sealed class Attempt
    {
        internal System.Reflection.MethodInfo? Target;
        internal readonly Type GameType;
        internal readonly string Method, Handler, Label;
        internal readonly bool Prefix, DeclaredOnly;
        internal string Error;
        internal Attempt(Type gameType, string method, string handler, string label, string error,
            System.Reflection.MethodInfo? target, bool prefix, bool declaredOnly)
        { GameType = gameType; Method = method; Handler = handler; Label = label; Error = error; Target = target; Prefix = prefix; DeclaredOnly = declaredOnly; }
    }
    static readonly List<Attempt> pending = new();

    /// `declaredOnly=true` 时**只接受声明在这个类型自己身上的方法**，绝不退回基类。
    /// 为什么需要这条：`Initiate` 是 `VehicleComponent` 的虚方法（slot 12），场上一切部件都有它——
    /// 一旦解析退到基类，就等于给每个部件的每次初始化都挂一次我们的后挂，那是最贵的一种错法。
    static void TryPatch(Type gameType, string method, string handler, string label, bool prefix = false, bool declaredOnly = false)
    {
        System.Reflection.MethodInfo? target = null;
        try
        {
            target = AccessTools.DeclaredMethod(gameType, method);
            if (target == null)
            {
                if (declaredOnly) throw new MissingMethodException($"{gameType.Name} 自己没有声明 {method}（不退到基类，避免给所有部件挂点）");
                target = AccessTools.Method(gameType, method) ?? throw new MissingMethodException($"{gameType.Name}.{method} 不在游戏里");
            }
            PatchOne(harmony!, target, handler, prefix);
            Plugin.ModLog.LogDebug($"TOOL_STAB 挂点接上：{label}（{gameType.Name}.{method}）");
        }
        catch (Exception ex)
        {
            var real = ex.InnerException ?? ex;
            pending.Add(new Attempt(gameType, method, handler, label, real.GetType().Name + ": " + real.Message, target, prefix, declaredOnly));
            Plugin.ModLog.LogError($"TOOL_STAB 挂点 {label} 没接上：{real.GetType().Name}: {real.Message}（每秒重试）");
        }
    }

    static void PatchOne(Harmony instance, System.Reflection.MethodInfo target, string handler, bool prefix)
    {
        var processor = instance.CreateProcessor(target);
        var method = new HarmonyMethod(typeof(Stabilizer), handler);
        if (prefix) processor.AddPrefix(method); else processor.AddPostfix(method);
        processor.Patch();
    }

    static void ReportPatchState(bool quietWhenAttached)
    {
        if (pending.Count == 0)
        {
            if (!quietWhenAttached) Plugin.ModLog.LogDebug("TOOL_STAB 六条挂点都接上了");
            return;
        }
        Plugin.ModLog.LogWarning($"TOOL_STAB 还差 {pending.Count} 条挂点：{string.Join("、", pending.Select(p => p.Label + "（" + p.Error + "）"))} ⇒ " +
            "缺权限挂点＝顶不住颠簸，缺接缝＝滤波不工作，缺重建钩子＝改装／掉血后写入会退回原值，正在每秒重试");
    }

    /// 设计器面板的状态行（第三方那套「版本号后面挂『双稳已失效』」的替代，见文件头 4L 末段：
    /// 版本标签没有专用字段，只能全局劫持文本 setter，而汉化 DLL 后挂同一个 setter 会把后缀整体吃掉）。
    /// 只做读数，不改任何东西。
    internal static string MechanismStatus()
    {
        var taken = lanes.Count(kv => kv.Value.pitch || kv.Value.yaw);
        var ver = versionMismatch ? $"未验证：这套机制在 {TestedGameVersion} 上验的，当前 {gameVersion}" : $"游戏版本 {gameVersion}（已验证）";
        var pend = pending.Count == 0 ? "挂点 6/6" : $"! 挂点差 {pending.Count} 条（{string.Join("、", pending.Select(p => p.Label))}）";
        return $"{pend}｜{ver}｜总开关{(Enabled?.Value == true ? "开" : "关")}" +
            $"｜接管 {taken} 条驱动｜权限重写 {assertRuns} 次（写进 {assertWrites} 条）" +
            $"｜极速 {Fmt(RateFloor?.Value ?? 0f)}~{Fmt(RateCap?.Value ?? 0f)}°/s（原值×{Fmt(Authority?.Value ?? 1f)}，只抬不压）、" +
            $"最小角加速度 {Fmt(AccelFloor?.Value ?? 0f)}°/s²";
    }

    /// 开机没接上的挂点，由 `DesignEditor.Update` 每帧带来这里，按 1 秒退避重试（挂上就报一条）。
    public static void RetryPending()
    {
        if (pending.Count == 0 || Time.unscaledTime < nextRetry) return;
        nextRetry = Time.unscaledTime + 1f;
        if (harmony == null) return;
        for (int i = pending.Count - 1; i >= 0; i--)
        {
            var a = pending[i];
            try
            {
                var target = a.Target ?? AccessTools.DeclaredMethod(a.GameType, a.Method);
                if (target == null)
                {
                    if (!a.DeclaredOnly)
                    {
                        target = AccessTools.Method(a.GameType, a.Method);
                        if (target == null) continue;
                    }
                    else
                    {
                        // 声明在这个组件上的方法没了（游戏改名／并到基类）：不退到基类，当场放弃并说清楚，
                        // 免得每秒空转、还让人以为「再等等就挂上了」。
                        pending.RemoveAt(i);
                        Plugin.ModLog.LogWarning($"TOOL_STAB 挂点 {a.Label} 放弃重试：{a.GameType.Name} 上已经没有自己声明的 {a.Method} " +
                            "⇒ 权限重建改由认车重扫（≤1 秒一次）兜着，进战斗先看读数行的极速有没有抬起来");
                        continue;
                    }
                }
                PatchOne(harmony, target, a.Handler, a.Prefix);
                pending.RemoveAt(i);
                Plugin.ModLog.LogInfo($"TOOL_STAB 挂点重试成功：{a.Label}（{a.GameType.Name}.{a.Method}）");
            }
            catch (Exception ex)
            {
                var real = ex.InnerException ?? ex;
                a.Error = real.GetType().Name + ": " + real.Message;
                a.Target = null;   // 也许失败在解析上，重试时重新解析
            }
        }
        ReportPatchState(true);
    }

    /// 全部配置项集中在这里 Bind（节名 Stabilizer，键名英文写进 cfg）。
    public static void Register(ConfigFile config)
    {
        gameVersion = SafeVersion();
        versionMismatch = !string.IsNullOrEmpty(gameVersion) && gameVersion != TestedGameVersion;
        if (versionMismatch)
            Plugin.ModLog.LogInfo($"TOOL_STAB 偏移表基于 {TestedGameVersion}，当前 {gameVersion}：按兼容模式加载（效果不对再回退）");
        Enabled = config.Bind("Stabilizer", "Enable stabilizer", true,
            "火炮稳定器总开关。关=完全不生效（面板上的勾照旧能勾，只是战斗里不改写瞄准方向）。");
        Smoothing = config.Bind("Stabilizer", "Aim smoothing seconds", 0f,
            "【4L 起默认 0＝关】对游戏递进来的世界方向做低通的时间常数（秒）。3d 的探针结案：游戏送进接缝的方向本来就是稳的" +
            "（车根 −5.83°→+4.35° 的同期它整场钉在 −0.51°），所以滤波不是稳定器的主体，抬权限才是。" +
            "留着这条是想试「软锁定」手感（>0 会让炮手主动瞄准带一点滞后）；0＝完全不滤、不往接缝里回写。");
        Capture = config.Bind("Stabilizer", "Aim capture degrees", 25f,
            "角度差超过这个值就认定炮手在大幅转向，直接放行不拽（度）。只在上面的滤波开着时才有意义。");
        Authority = config.Bind("Stabilizer", "Stabilizer lay authority", 4f,
            "【极速的倍率，4L 起被下面的地板／封顶两道夹住】勾了稳定器之后，把每条驱动的**极速**按这个倍率抬高：" +
            "俯仰写 `elevationInfo.MaxSpeed`（驱动对象 0x44），摇架自带的方位写 `azimuthInfo.MaxSpeed`（0x58，3j 补），" +
            "炮塔回旋写 `TurretBehaviour.maxTraverseRate`（0x18）。写进去的值还要过 `Stabilizer lay rate floor/cap deg/s` 两道夹，" +
            "并且永不低于这条驱动自己的原值（只抬不压）。改的是运行时对象字段，不进蓝图与存档。" +
            "为什么不是乘游戏的 `speedMultiplier`：3i 反汇编证明那个参数在 `MoveToTarget` 里只被 `<=0 就返回` 用到一次，" +
            "根本不参与极速计算（实测游戏永远传 1.00），乘它必然没感觉。");
        RateFloor = config.Bind("Stabilizer", "Stabilizer lay rate floor deg/s", 30f,
            "【极速的地板，4L 新增；0＝不设地板】写法＝`clamp(原值×权限倍率, 地板, 封顶)`，仍永不低于原值。" +
            "为什么必须有地板：高低机原值只有 1.4~1.6°/s（=180/比率），×4 也只有 6°/s，而实测车体俯仰峰速率到过 29.7°/s ⇒ " +
            "车体抬 5°/s 时这条轴最多追 6°/s，看着就是「跟着车体一起晃」。这正是你定的判据：炮本身只能 2°/s，" +
            "颠簸 5°/s 也要钉住 ⇒ 稳定器必须把这根轴的**可用速率**买到比颠簸更快，而不是只乘一下。" +
            "30°/s 是照着实测最猛的车体峰速率取的；觉得跟手了就往下调，觉得炮管乱窜先往下调这个数。");
        RateCap = config.Bind("Stabilizer", "Stabilizer lay rate cap deg/s", 60f,
            "【极速的封顶，4L 新增；0＝不封顶】上一版 ×20 把 19.2°/s 抬成 384°/s，这条轴唯一的阻尼就是它自己的极速，" +
            "于是绕着我们的锁定方向自振、还撞行程端点（§25 的「鬼畜」）。封顶是那道事的闸门：" +
            "倍率再猛也撞不到比这里更高的值，老 cfg 里残留的 ×20 也会被这一位夹住。" +
            "60°/s＝地板的两倍，够它「追得上还有余量拽回来」；想要更硬的手感再往上调。");
        AccelFloor = config.Bind("Stabilizer", "Stabilizer min lay acceleration deg/s2", 250f,
            "【抗扰的本体，4L 新增；0＝关】稳定器真正靠的是**能把炮刹住**，不是能把炮转快。" +
            "口径：能在 0.12 秒里从 0 顶到 30°/s、也能 0.12 秒刹住。两类字段分开写：" +
            "俯仰 0x40／摇架方位 0x54 是 `MotorTorque`（单位是力矩）⇒ 用游戏自己的式子反解 " +
            "`需要的力矩 = 惯量(0x70)×角加速度 + |质量(0x68)×9.81×力臂(0x6C)|`，取 `+|…|` 让刹车那侧也够；" +
            "炮塔 0x10 加速／0x14 刹车本来就是角加速度 ⇒ 直接写。全部 `max(原值, 目标)`，只抬不压，" +
            "写的是运行时对象字段、停稳／掉条目会还原，不进蓝图与存档。" +
            "3i 的账就是这么欠下的：当时只抬了极速（`极速 384.00（原 19.20）`），`加速 19.52 刹车 97.59` 一位没碰，" +
            "方位残差 12~16°（19.52°/s² 从 0 顶到 30°/s 要 1.5 秒，永远慢半拍）。" +
            "读数行会打反解目标与写后实际值：俯仰那两位大概率显示「没改」，因为它们的原力矩／惯量已经上万 ⇒ " +
            "本来就不缺加速度，真缺的是炮塔那两位。对不上先怀疑 0x68/0x70 的语义再怀疑数值。");
        Torque = config.Bind("Stabilizer", "Stabilizer lay torque multiplier", 1f,
            "【额外的力矩倍率，默认 1＝不乘】在 `Stabilizer min lay acceleration deg/s2` 反解出来的目标之上再乘一遍" +
            "（两者都只抬不压）。这条就是第三方那套「力矩 ×180」的入口：如果他们那版在你车上确实更稳，" +
            "把这个数从 1 抬到 20~180 就是我们的等价做法，而且不用碰蓝图。平时留 1。");
        Log = config.Bind("Stabilizer", "Log stabilizer", false,
            "把挂点状态、每门炮读到的旗标值与两条驱动的运行时原生类名、每 2 秒一条改写统计与实测读数（摇架/车体俯仰）记进日志（TOOL_STAB 前缀）。");
    }

    // ================= 挂点 =================

    // 俯仰接缝。形参名照 dump.cs 写（`worldDirection`）：Harmony 按名字给后挂的参数对号入座。
    [HarmonyPostfix]
    static void OnAimLaying(LayingDriveBehaviour __instance, Vector3 worldDirection, SetAimFlags flags)
    {
        if (selfCall) return;
        if (!Filter(__instance, true, worldDirection, out var dir, out var lane)) return;
        selfCall = true;
        try
        {
            __instance.AimAtDirection(dir, flags);
            Verify(lane, dir, __instance.TargetDirection);   // 写完立刻回读：对不上就是这一帧被游戏改回去了
        }
        catch (Exception ex) { Plugin.ModLog.LogWarning($"TOOL_STAB 俯仰接缝改写失败：{ex.Message}"); }
        finally { selfCall = false; }
    }

    // 方位接缝。dump.cs:876999 这里形参叫 `value`，不是 `worldDirection`——照 dump 写。
    [HarmonyPostfix]
    static void OnAimTurret(TurretBehaviour __instance, Vector3 value, SetAimFlags flags)
    {
        if (selfCall) return;
        if (!Filter(__instance, false, value, out var dir, out var lane)) return;
        selfCall = true;
        try
        {
            __instance.AimAtDirection(dir, flags);
            Verify(lane, dir, __instance.TargetDirection);
        }
        catch (Exception ex) { Plugin.ModLog.LogWarning($"TOOL_STAB 方位接缝改写失败：{ex.Message}"); }
        finally { selfCall = false; }
    }

    // ================= 驱动权限（稳定器的本体：给这条驱动更多的转向能力） =================

    /// 俯仰驱动每帧的步进。**4L 起这条前挂只做采样与计数**（权限写入搬到 `Initiate` 后挂／认车重扫／归属认领，
    /// 见 `Reassert`）。采的读数是：游戏自己传进来的
    /// `speedMultiplier` 区间、车体与炮身的峰速率、轴状态四旗标、驱动身上 `targetDirection` 的真实朝向。
    /// 不动行程极限、不动物理、不动目标 ⇒ 主动瞄准／损伤减速／俯仰限位全都还在，只是它现在追得上车体了。
    /// `speedMultiplier` 这个形参只**读不写**（3i 证明游戏只拿它做 `<=0 就返回` 的门，乘它没有任何效果，见文件头 ④）。
    [HarmonyPrefix]
    static void OnMoveLaying(LayingDriveBehaviour __instance, ref float speedMultiplier) => Boost(__instance, ref speedMultiplier, true);

    /// 炮塔回旋驱动的同一件事（`TurretBehaviour.MoveToTarget`，RVA 0x1E108B0）。这一路的字段是
    /// `maxTraverseRate`@0x18 极速、`maxAcceleration`@0x10／`maxBrakeAcceleration`@0x14 角加速度——
    /// 3i 实测后两位在双稳的窗里根本没被碰过（`加速 19.52 刹车 97.59`）而方位残差 12~16°，4L 由 `Reassert` 补写它们。
    [HarmonyPrefix]
    static void OnMoveTurret(TurretBehaviour __instance, ref float speedMultiplier) => Boost(__instance, ref speedMultiplier, false);

    /// 热路径（每条驱动每帧一次）：整段裹 try，读数失败就当作“这次没读到”，绝不能把游戏的驱动带崩。
    /// `laying=true` ⇒ 这条驱动带 `elevationInfo`／`azimuthInfo` 两轴，任一轴要稳就算接管；`false` ⇒ 只看方位轴。
    /// `laying=true` ⇒ 这条驱动带 `elevationInfo`／`azimuthInfo` 两轴，任一轴要稳就加；`false` ⇒ 只看方位轴。
    static void Boost(Il2CppObjectBase? driveObj, ref float speedMultiplier, bool laying)
    {
        try
        {
            if (Enabled?.Value != true || driveObj == null) return;
            if (DesignEditor.Instance?.IsReady == true) return;         // 设计器里不抢炮
            IntPtr drive = driveObj.Pointer;
            if (drive == IntPtr.Zero || !lanes.TryGetValue(drive, out var lane)) return;
            if (laying ? !(lane.pitch || lane.yaw) : !lane.yaw) return;
            lane.boosts++;
            if (!float.IsNaN(speedMultiplier))
            {
                if (speedMultiplier < lane.baseMin) lane.baseMin = speedMultiplier;
                if (speedMultiplier > lane.baseMax) lane.baseMax = speedMultiplier;
            }
            if (laying) { SampleRates(lane); SampleAxis(driveObj as LayingDriveBehaviour, lane, drive); }
        }
        catch (Exception ex)
        {
            if (boostWarned) return;
            boostWarned = true;
            Plugin.ModLog.LogWarning($"TOOL_STAB 驱动权限挂点出错（只报一次，之后静默跳过）：{ex.GetType().Name}: {ex.Message}");
        }
    }
    static bool boostWarned;

    /// 量车体与炮身各自的俯仰速率（度/秒）。09:4x 的争议：权限 ×6 之后**稳态钉得很死**（车体 −12°、摇架 0.36°），
    /// 但剧烈颠簸那一瞬 `摇架` 会离 `锁定` 两三度甚至更多。两种可能必须分开：
    /// ① 权限没吃到极速 ⇒ `炮身峰速率` 会停在没加权限时的水平；
    /// ② 极速够了、撞的是**加速度**上限——`UpdateAxis`（RVA 0x1DE29C0）里 `speedMultiplier` 只乘 `MaxSpeed`
    ///    （`[rdx+0x20] = speedMultiplier × [r8+4]`），而 `maxAcceleration／maxDeceleration` 完全由
    ///    `[r8+0]=MotorTorque` 与 mass@0x68／centreOfMass@0x6C／momentOfInertia@0x70 算出来，**不吃 speedMultiplier**
    ///    ⇒ 光乘权限只抬高上限速度，掉头快慢没变。峰速率两列一比就知道是哪一种。
    /// 峰值存 lane 上，`Readout` 打完一行就清零（读数窗口≈2 秒）。
    /// **3g 修正认识**：`炮身峰速率` 这一列被车体污染了——摇架是父级（车体）带着转的，它的 °/s 含车体速率，
    /// 所以「炮身 ≈ 车体」只能说明轴没相对支座转出多少，量不到轴自己在追什么 ⇒ 3h 改读 `TargetAngleDeviation`。
    /// **3j 归位窗**：刚接管的 `SettleWindow` 秒内**不记峰值**（实测进场第一窗 `偏差 峰 170~260°` 是出生／炮归位，
    /// 不是颠簸，却把 `DevVerdict` 一路顶成「在追但追不上」）。采样状态照旧更新，只关峰值累积，
    /// 免得窗口一过第一帧的 dt 跨了几秒把速率算成零头。
    static void SampleRates(Lane lane)
    {
        var laying = lane.laying;
        if (laying == null) return;
        float now = Time.time, hull = PitchOf(laying.foundation), barrel = PitchOf(laying.trunnions);
        bool settle = InSettle(lane, now);
        if (lane.hasHullSample && !float.IsNaN(hull))
        {
            float dt = now - lane.hullLastTime;
            if (dt > 1e-4f)
            {
                float rate = MathF.Abs(hull - lane.hullLast) / dt;
                if (!settle && rate > lane.hullPeakRate) lane.hullPeakRate = rate;
            }
        }
        if (lane.hasBarrelSample && !float.IsNaN(barrel))
        {
            float dt = now - lane.barrelLastTime;
            if (dt > 1e-4f)
            {
                float rate = MathF.Abs(barrel - lane.barrelLast) / dt;
                if (!settle && rate > lane.barrelPeakRate) lane.barrelPeakRate = rate;
            }
        }
        if (!float.IsNaN(hull)) { lane.hullLast = hull; lane.hullLastTime = now; lane.hasHullSample = true; }
        if (!float.IsNaN(barrel)) { lane.barrelLast = barrel; lane.barrelLastTime = now; lane.hasBarrelSample = true; }
    }

    /// 这条驱动还在「刚接管」的归位窗里吗？（顺带把 `inSettle` 标给日志，读数行要打出这一句，别让空峰值装成“没颠簸”）
    static bool InSettle(Lane lane, float now)
    {
        lane.inSettle = now - lane.claimedAt < SettleWindow;
        return lane.inSettle;
    }

    /// 3h 的裁决列：**轴自己**离靶多远。`get_TargetAngleDeviation`（RVA 0x1DE19A0）取两轴各自
    /// `state[0x14] − state[0x00]` 的大者（state 由 `0x1D39080(handle, 0)` 解析，元素长 0x38；
    /// 3i 反汇编：`+0x00`＝已施加角（Apply 用它摇炮），`+0x14`＝本帧由世界方向投影出的目标角）
    /// ⇒ 这个数**不含车体**，正是 3g 那两列 `峰速率` 量不出来的东西（实测会带负号，绝对值和 `我算偏差` 同量级）。
    /// 和 `Readout` 里的 `我算偏差 =|锁定 − 摇架|` 对起来只分两种病：游戏≈0 而我算好几度 ⇒ 驱动自认已在靶上，
    /// 它追的不是我们给的世界方向；游戏≈我算 ⇒ 在追但追不上（速率／加速度）。
    /// 单位自证（3i）：活的步长在 0x1DE242B `state[+0x20] = deltaTime × healthFraction × elevationInfo.MaxSpeed`，
    /// `MaxSpeed` 就是设计器那列 1.39/1.60（度/秒）、deltaTime 是秒 ⇒ `+0x00/+0x14` 两个角按**度**读；
    /// `Readout` 另打 `行程 Min@0x48~Max@0x4C` 当第二重对照（实测 −9.20~13.10，正是俯仰行程）。
    /// **3j 官方对号**：这个 state 就是 `RotationAxisPhysics`（TypeDefIndex 11052，长 0x38），dump.cs 直接给出名字：
    /// `angle@0x0／velocity@0x4／acceleration@0x8／min@0xC／max@0x10／target@0x14／maxAcceleration@0x18／
    /// maxDeceleration@0x1C／maxSpeed@0x20／apply@0x28／extrapolationDeltaTime@0x30／flags@0x34／result@0x35／axisFlags@0x36`
    /// ⇒ 上面 `+0x20` 那一位**就是轴自己的 maxSpeed**（每帧被改写成 `原 MaxSpeed × deltaTime × healthFraction`
    /// 这个“本帧允许走的量”，所以 0x44 一放大整条轴立刻跟上），而 3h 猜的“副角／选择子”是 `velocity` 与
    /// `extrapolationDeltaTime`（ recoil 外推项），不是第二个角。中心积分器在
    /// `RotationAxisPhysicsRegister.Update`（RVA 0x1D38D90，fixedDeltaTime=0.01），两条轴共用一张表。
    /// 顺带读四个轴状态属性，并同窗口量一份**同口径**的世界偏差 `|锁定 − 摇架|`（两列一比就是判据，见 `DevVerdict`）。
    /// 逐帧一次属性调用（每帧最多一次，全裹 try，读失败就当作这次没读到）。
    static void SampleAxis(LayingDriveBehaviour? laying, Lane lane, IntPtr drive)
    {
        if (laying == null) return;
        try
        {
            float now = Time.time;
            bool settle = InSettle(lane, now);
            float dev = laying.TargetAngleDeviation;
            if (!float.IsNaN(dev))
            {
                lane.devLast = dev;
                float abs = MathF.Abs(dev);              // 这列带符号（=已施加角−目标角），比大小要取绝对值
                if (!settle && abs > lane.devPeak) lane.devPeak = abs;
            }
            lane.elevLocked = laying.ElevationLocked;
            lane.azLocked = laying.AzimuthLocked;
            lane.achievable = laying.TargetAchievable;
            lane.hasTarget = laying.HasTarget;
            float barrel = PitchOf(laying.trunnions);
            if (!float.IsNaN(barrel))
            {
                float own = MathF.Abs(lane.lockedPitch * Mathf.Rad2Deg - barrel);
                lane.devOwnLast = own;
                if (!settle && own > lane.devOwnPeak) lane.devOwnPeak = own;
            }
            // 3j：方位轴同一口径单列一列。游戏那列是**两轴取大**，以前只能猜「这一窗的大值是方位轴贡献」，
            // 现在两列并排，谁在拖一目了然（摇架世界方位 vs 我们锁定的世界方位，跟俯仰那列完全同口径）。
            float az = YawOf(laying.trunnions);
            if (!float.IsNaN(az))
            {
                float ownAz = MathF.Abs(ShortAngle(lane.lockedYaw, az * Mathf.Deg2Rad) * Mathf.Rad2Deg);
                lane.devAzLast = ownAz;
                if (!settle && (float.IsNaN(lane.devAzPeak) || ownAz > lane.devAzPeak)) lane.devAzPeak = ownAz;
            }
            // 3j：驱动此刻真正在追的世界方向（原生 `targetDirection`@0x34，x／y／z 三个 float，
            // 不叫属性、纯 Marshal 读，避免多踩一次 interop 桩）。和游戏接缝给我们的 `锁定` 一比，
            // 就知道“追不上”是权限问题还是**根本没收到我们的方向**。
            float tx = RawF(drive, 0x34), ty = RawF(drive, 0x38), tz = RawF(drive, 0x3C);
            if (!float.IsNaN(tx) && !float.IsNaN(tz) && tx * tx + tz * tz > 1e-8f)
            {
                lane.gameYawDeg = MathF.Atan2(tx, tz) * Mathf.Rad2Deg;
                lane.gamePitchDeg = float.IsNaN(ty) ? float.NaN
                    : MathF.Asin(Mathf.Clamp(ty / MathF.Sqrt(tx * tx + ty * ty + tz * tz), -1f, 1f)) * Mathf.Rad2Deg;
            }
        }
        catch (Exception) { }
    }

    /// 一层的**世界方位**（度，`atan2(forward.x, forward.z)`）；读不到返回 NaN。3j 的方位偏差列用它。
    static float YawOf(object? t)
    {
        try
        {
            var tr = t as Transform;
            if (tr == null) return float.NaN;
            var f = tr.forward;
            if (f.x * f.x + f.z * f.z < 1e-8f) return float.NaN;
            return MathF.Atan2(f.x, f.z) * Mathf.Rad2Deg;
        }
        catch (Exception) { return float.NaN; }
    }

    /// 原生单字节旗标：`moving@0x30／aimSetFlags@0x31／hasAppliedTarget@0x32／disposed@0x33`
    /// （dump.cs:870599-870602，`SetAimFlags` 的底层类型是 byte，dump.cs:686650）。
    static string Flags(IntPtr drive)
    {
        try
        {
            return $"动{Marshal.ReadByte(drive + 0x30)} 令{Marshal.ReadByte(drive + 0x31)}" +
                $" 已应用{Marshal.ReadByte(drive + 0x32)} 毁{Marshal.ReadByte(drive + 0x33)}";
        }
        catch (Exception) { return "读不到"; }
    }

    /// 把 3h 的偏差列翻成一句判词（只按数字说话，读不到就说读不到）。
    /// 3j：游戏那一列是**两轴取大**，以前只能猜“大值是哪一轴贡献的”，现在俯仰／方位两列都在参数里，直接按名字归因。
    static string DevVerdict(float gameDev, float ownDev, float ownAzDev)
    {
        if (float.IsNaN(gameDev)) return "偏差读不到";
        if (float.IsNaN(ownDev)) return "摇架读不到，只有游戏那列";
        string who = float.IsNaN(ownAzDev) ? "方位列读不到"
            : ownAzDev >= ownDev ? $"方位轴贡献（俯仰 {ownDev:F2} ≤ 方位 {ownAzDev:F2}）"
                                 : $"俯仰轴贡献（俯仰 {ownDev:F2} > 方位 {ownAzDev:F2}）";
        if (ownDev < 0.5f) return gameDev < 0.5f ? "两列都小⇒这一窗口没颠簸" : $"俯仰我算已到位⇒游戏那列是方位轴贡献（{who}）";
        if (gameDev < 0.5f) return "游戏自认到位⇒它追的不是这个世界方向";
        if (gameDev >= ownDev * 0.5f) return $"在追但追不上⇒撞速率/加速度（{who}）";
        return "游戏偏差远小过我算⇒轴目标不是世界角，看行程/旗标两列";
    }

    /// 把这条驱动**要稳的两个能力字段**按 4L 的旋钮重写一遍（3i 立骨架，3j 补齐方位轴与回旋，4L 换算法）：
    /// `极速`（俯仰 0x44／摇架自带的方位 0x58／炮塔回旋 0x18）⇒ `clamp(原值×Stabilizer lay authority, 地板, 封顶)`；
    /// `马达力矩`（俯仰 0x40／方位 0x54）⇒ 按游戏自己的加速度式子**反解**（见 `TorqueTarget`）；
    /// 炮塔的 `maxAcceleration`@0x10／`maxBrakeAcceleration`@0x14 ⇒ 本来就是角加速度，直接写地板。
    /// 三条共同底线：**只抬不压**（永不低于这条驱动自己的原值，玩家自调的大值不会被我们压小），
    /// 全部是运行时对象字段（原值按偏移存 `lane.origs`，停稳／丢条目／旋钮调低都写回去），不进蓝图、不动存档。
    /// 返回 true＝这一次至少写进过一个放大值（给 `lane.applied` 与面板计数）。
    static bool ApplyPowers(IntPtr drive, Lane lane, bool laying)
    {
        bool wrote = false;
        if (laying)
        {
            wrote |= ScaleRate(drive, 0x44, lane, lane.pitch);
            wrote |= ScaleTorque(drive, 0x40, lane, lane.pitch, true);
            wrote |= ScaleRate(drive, 0x58, lane, lane.yaw);
            wrote |= ScaleTorque(drive, 0x54, lane, lane.yaw, false);
        }
        else
        {
            wrote |= ScaleRate(drive, 0x18, lane, lane.yaw);
            wrote |= ScaleAccel(drive, 0x10, lane, lane.yaw);
            wrote |= ScaleAccel(drive, 0x14, lane, lane.yaw);
        }
        return wrote;
    }

    /// 关掉／停稳／丢条目：把这个驱动上**所有**我们碰过的字段写回原值（3j：按 `origs` 的键还原，
    /// 不再靠“这条是俯仰路还是回旋路”去猜偏移——同一条 `LayingDriveBehaviour` 两轴都稳时那是四个字段）。
    static void RestoreLimits(IntPtr drive, Lane lane)
    {
        if (lane.origs.Count == 0) return;
        foreach (int off in new List<int>(lane.origs.Keys))
        {
            WriteF(drive, off, lane.origs[off]);
            lane.origs.Remove(off);
        }
    }

    /// 这个字段**没被我们碰过时**的值：写过就以表里的原值为准（现场已经是放大值），没写过就读现场（读不到＝NaN）。
    static float BaseValue(IntPtr drive, Lane lane, int off)
        => lane.origs.TryGetValue(off, out float saved) ? saved : RawF(drive, off);

    /// 单个 float 字段的「只抬不压」写核。`target` 不高于原值（或读不到）⇒ **一个字节都不写、也不记原值**，
    /// 这样 `Orig()` 打出的「没改」才是真话、`RestoreLimits` 也只还原我们确实动过的字段；
    /// 但如果这一位上一轮写过而这次不再需要（旋钮调低／地板变小），当场还原。
    /// `want=false`＝这一轴现在不稳，还原记过的那位。返回 true＝写进了一个放大值。
    static bool WriteBoosted(IntPtr drive, Lane lane, int off, float baseValue, float target, bool want)
    {
        if (lane.origs.TryGetValue(off, out float saved)) baseValue = saved;   // 已经写过：原值以表里为准
        if (!want || float.IsNaN(baseValue) || float.IsNaN(target) || target <= baseValue + 1e-4f)
        {
            if (lane.origs.TryGetValue(off, out float orig))
            {
                WriteF(drive, off, orig);
                lane.origs.Remove(off);
            }
            return false;
        }
        lane.origs[off] = baseValue;
        WriteF(drive, off, target);
        return true;
    }

    /// 极速：`clamp(原值×倍率, 地板, 封顶)`，最后再兜一条「不低于原值」。地板和封顶都是**绝对 °/s**，
    /// 所以老 cfg 里残留的 ×20 只会撞到封顶、不会再把这条轴拆到没阻尼（§25 的鬼畜＝极速 384°/s 绕着锁定方向自振）。
    static bool ScaleRate(IntPtr drive, int off, Lane lane, bool want)
    {
        float orig = want ? BaseValue(drive, lane, off) : float.NaN;
        return WriteBoosted(drive, lane, off, orig, want ? RateTarget(orig) : float.NaN, want);
    }

    static float RateTarget(float orig)
    {
        if (float.IsNaN(orig) || orig <= 0f) return float.NaN;
        float v = orig * MathF.Max(1f, Authority?.Value ?? 1f);
        float floor = RateFloor?.Value ?? 0f, cap = RateCap?.Value ?? 0f;
        if (floor > 0f) v = MathF.Max(v, floor);
        if (cap > 0f) v = MathF.Min(v, cap);
        return v < orig ? orig : v;      // 封顶低于原值也不压——只抬不压是这套的底线
    }

    /// 俯仰 0x40／摇架方位 0x54 是 `MotorTorque`（力矩，不是角加速度）。游戏自己把它换成角加速度：
    /// `maxAcceleration = (力矩 ∓ mass·g·centreOfMass) / momentOfInertia`（∓ 一次给加速、一次给刹车；
    /// 字段 mass@0x68／centreOfMass@0x6C／momentOfInertia@0x70，dump.cs:870595-870612，3f 反汇编定死）。
    /// 所以「能在 T 秒内从 0 顶到 V 并同样快刹住」反解成 `力矩 = 惯量×角加速度 + |质量×9.81×力臂|`
    /// （取绝对值，刹车那一侧也要够）。三个读数任一读不到 ⇒ 退回纯倍率，不猜数、不写 NaN。
    /// 算完的目标存进 lane，读数行拿它跟写后实际值对：单位读错或偏移错位，那一眼就能看出来。
    static bool ScaleTorque(IntPtr drive, int off, Lane lane, bool want, bool pitchAxis)
    {
        float orig = want ? BaseValue(drive, lane, off) : float.NaN;
        float target = want ? TorqueTarget(drive, orig) : float.NaN;
        if (want) { if (pitchAxis) lane.torqueTargetPitch = target; else lane.torqueTargetYaw = target; }
        else if (pitchAxis) lane.torqueTargetPitch = float.NaN; else lane.torqueTargetYaw = float.NaN;
        return WriteBoosted(drive, lane, off, orig, target, want);
    }

    static float TorqueTarget(IntPtr drive, float orig)
    {
        if (float.IsNaN(orig) || orig <= 0f) return float.NaN;
        float byMult = orig * MathF.Max(1f, Torque?.Value ?? 1f);
        float accel = AccelFloor?.Value ?? 0f;
        if (accel <= 0f) return byMult;
        float mass = RawF(drive, 0x68), arm = RawF(drive, 0x6C), inertia = RawF(drive, 0x70);
        if (float.IsNaN(mass) || float.IsNaN(arm) || float.IsNaN(inertia) || inertia <= 1e-6f) return byMult;
        return MathF.Max(inertia * accel + MathF.Abs(mass * Gravity * arm), byMult);
    }

    /// 炮塔回旋的 0x10 加速／0x14 刹车：这两个字段游戏已经算完了，直接按角加速度地板写。
    /// 3i 实测 `加速 19.52 刹车 97.59` 就是这里——19.52°/s² 从 0 顶到 30°/s 要 1.5 秒，双稳永远慢半拍。
    /// 官方入口是 `MotorTorqueMultiplier`@0x30，但它只在车被评估时喂这两个字段，写倍率这一帧看不见效果。
    static bool ScaleAccel(IntPtr drive, int off, Lane lane, bool want)
    {
        float orig = want ? BaseValue(drive, lane, off) : float.NaN;
        return WriteBoosted(drive, lane, off, orig, want ? AccelTarget(orig) : float.NaN, want);
    }

    static float AccelTarget(float orig)
    {
        if (float.IsNaN(orig) || orig <= 0f) return float.NaN;
        return MathF.Max(orig * MathF.Max(1f, Torque?.Value ?? 1f), AccelFloor?.Value ?? 0f);
    }

    // ================= 4L：权限写入的时刻（默认不在每帧热路径上裸写） =================

    /// 这条 lane 走哪组偏移：`LayingDriveBehaviour` 是 0x40/0x44/0x54/0x58，`TurretBehaviour` 是 0x10/0x14/0x18。
    /// 光看 `lane.laying` 不够——interop 转换失败时它是空的（`Add` 那里 catch 掉只留个警告），
    /// 而 `typeName` 是从原生对象头的 klass 取的真类名，两种驱动分得开。认错偏移会踩到别人的字段，所以宁可多这一道判。
    static bool IsLaying(Lane lane) => lane.laying != null || lane.typeName.IndexOf("Laying", StringComparison.Ordinal) >= 0;

    /// 驱动重建后挂：`LayingDrive.Initiate`／`TraverseMotor.Initiate`（各自 Slot 12，RVA 0x1DDE880／0x1E11E80，
    /// 都是组件自己声明的 `protected override`，`TryPatch` 那里按 DeclaredOnly 挂，绝不退到 `VehicleComponent.Initiate`）。
    /// 为什么要有这两条：3i 只在 `MoveToTarget` 前挂里写，于是**炮一重建（换车／重新入场／改装）我们写的值就没了**，
    /// 而新对象要等到接缝自己跑到才会被抬——现在是重建当场就补写。
    /// 形参只接 `__instance`，再从它取 `.Behaviour`（我们登记的键是那个 behaviour 的原生指针），少一个 interop 桩就少一分 AV 风险。
    [HarmonyPostfix]
    static void OnInitiate(Il2CppObjectBase __instance)
    {
        try
        {
            if (__instance == null || Enabled?.Value != true) return;
            object? beh = __instance switch
            {
                LayingDrive laying => laying.Behaviour,
                TraverseMotor motor => motor.Behaviour,
                _ => null,
            };
            if (beh == null && !initiateKindReported)
            {
                // 递进来的包装对象不是这两个组件的包装类（interop 有时只给基类包装）⇒ 这条补写路走不通。
                // 不当失败处理：认车重扫末尾那次 `Reassert` 照样会把权限写上，只是要等 ≤1 秒。报一次名字就够。
                initiateKindReported = true;
                Plugin.ModLog.LogWarning($"TOOL_STAB 驱动重建补写拿到的是 {__instance.GetType().Name}（不是 LayingDrive／TraverseMotor 的包装类）" +
                    "⇒ 这一路不写，权限改由认车重扫（≤1 秒一次）补，机制照常");
            }
            IntPtr drive = Ptr(beh);
            if (drive == IntPtr.Zero) return;
            if (!lanes.TryGetValue(drive, out var lane)) return;    // 还没认到车：认车重扫末尾会统一补写
            AssertOne(drive, lane, IsLaying(lane));
        }
        catch (Exception ex)
        {
            // 跑在游戏的 Initiate 里，只报一次，别把进场的车刷屏
            if (!initiateWarned)
            {
                initiateWarned = true;
                Plugin.ModLog.LogWarning($"TOOL_STAB 驱动重建补写失败（只报一次）：{ex.GetType().Name}: {ex.Message}");
            }
        }
    }
    static bool initiateWarned;
    static bool initiateKindReported;   // 「后挂拿到的包装类不是这两个组件」只报一次

    /// 单条驱动的一次补写。整段裹 try：炸了当作“这次没加上”，绝不能把游戏的驱动或进场带崩。
    static void AssertOne(IntPtr drive, Lane lane, bool laying)
    {
        assertRuns++;
        try
        {
            if (!(lane.pitch || lane.yaw)) return;
            if (DesignEditor.Instance?.IsReady == true) return;      // 设计器里不动炮
            if (ApplyPowers(drive, lane, laying)) { lane.applied++; assertWrites++; }
        }
        catch (Exception) { }
    }

    /// 一个「权限重写时刻」：把当前接管的每一条驱动都补写一遍。
    /// 三个调用点——`Initiate` 后挂走 `AssertOne`（单条）、认车重扫末尾、归属认领成功。
    /// 之所以不靠每帧：3i 已证这几位不是每帧重算（写 180 挂得住），每帧裸写原生偏移只是白白多踩几百次 interop 桩。
    static void Reassert(string why)
    {
        try
        {
            if (Enabled?.Value != true) return;
            if (lanes.Count == 0) return;
            foreach (var kv in lanes)
            {
                var l = kv.Value;
                if (l.pitch || l.yaw) AssertOne(kv.Key, l, IsLaying(l));
            }
            if (Log?.Value != true) return;
            string line = $"TOOL_STAB 权限重写（{why}）：接管 {lanes.Count(kv => kv.Value.pitch || kv.Value.yaw)} 条" +
                $"｜累计重写 {assertRuns} 次、真写 {assertWrites} 次" +
                $"｜极速 {Fmt(RateFloor?.Value ?? 0f)}~{Fmt(RateCap?.Value ?? 0f)}°/s（原值×{Fmt(Authority?.Value ?? 1f)}）" +
                $"｜角加速度地板 {Fmt(AccelFloor?.Value ?? 0f)}°/s²×{Fmt(Torque?.Value ?? 1f)}";
            if (line != lastAssertLine) { lastAssertLine = line; Plugin.ModLog.LogInfo(line); }
        }
        catch (Exception ex)
        {
            Plugin.ModLog.LogWarning($"TOOL_STAB 权限重写失败（{why}）：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// 原值列（日志用）：没写过就说“没改”，别用 NaN 糊成一个数。
    static string Orig(Lane lane, int off) => lane.origs.TryGetValue(off, out float v) ? v.ToString("F2") : "没改";

    /// 只读／只写原生 float：偏移全部绑在 dump.cs 行号上（`LayingDriveBehaviour`：elevationInfo@0x40／azimuthInfo@0x54／
    /// mass@0x68／centreOfMass@0x6C／momentOfInertia@0x70，dump.cs:870595-870612；`TurretBehaviour`：
    /// maxAcceleration@0x10／maxBrakeAcceleration@0x14／maxTraverseRate@0x18／MotorTorqueMultiplier@0x30，
    /// dump.cs:876901-876910）。炸了返回 NaN／静默放弃，读的是我们自己认领的那两条驱动。
    static float RawF(IntPtr p, int off)
    {
        try { return BitConverter.Int32BitsToSingle(Marshal.ReadInt32(p + off)); }
        catch (Exception) { return float.NaN; }
    }

    static void WriteF(IntPtr p, int off, float value)
    {
        try { Marshal.WriteInt32(p + off, BitConverter.SingleToInt32Bits(value)); }
        catch (Exception) { }
    }

    static string Fmt(float v) => float.IsNaN(v) ? "读不到" : v.ToString("F2");

    /// 同上，但 NaN 说成“没算”——给那些**我们主动没算**的目标值（这一轴现在不稳，反解就没跑）。
    static string FmtOr(float v, string none) => float.IsNaN(v) ? none : v.ToString("F2");

    /// 回读 `TargetDirection` 与刚写进去的方向比角度；只统计，不重试。
    static void Verify(Lane lane, Vector3 written, Vector3 back)
    {
        lane.writeChecks++;
        try
        {
            if (Vector3.Angle(back, written) > 0.5f) lane.writeFails++;
            lane.lastWrittenPitch = MathF.Asin(Mathf.Clamp(back.normalized.y, -1f, 1f)) * Mathf.Rad2Deg;
        }
        catch (Exception) { lane.writeFails++; }
    }

    // ================= 一次瞄准指令 =================

    /// 把游戏这一帧发的世界方向按稳定器滤波。false＝不接管（这条驱动没登记、轴不在稳、还没落锁、或没什么可改的）。
    static bool Filter(Il2CppObjectBase driveObj, bool fromLayingSeam, Vector3 incoming,
        out Vector3 result, out Lane taken)
    {
        result = incoming; taken = null!;
        IntPtr drive = driveObj.Pointer;
        if (Enabled?.Value != true || drive == IntPtr.Zero) return false;
        if (DesignEditor.Instance?.IsReady == true) return false;      // 设计器里不抢炮
        if (incoming.sqrMagnitude < 1e-8f) return false;
        sawCalls++;
        if (!lanes.TryGetValue(drive, out var lane) || !(lane.pitch || lane.yaw))
        {
            var adopted = TryAdopt(driveObj, fromLayingSeam);        // 先按变换归属认：挂在本方车变换树上的驱动直接接管
            if (adopted == null)
            {
                // 3j：接缝可能跑在认车前头（这场实测就出现过「在自己车变换树上、却还没认领」的驱动）。
                // 先问归属，再决定催不催重扫：自己的车⇒不等 ScanEvery，最多 0.25 秒就重扫一次；
                // 别家（AI／敌军）⇒按老规矩节流，而且**根本不催**，免得拿别人的指针把认车跑成每帧一次。
                var ours = OwnCarOf(driveObj);
                NoteUnknown(driveObj, fromLayingSeam, ours);
                if (ours == null)
                {
                    if (Time.time - lastScan < ScanEvery) return false;
                }
                else
                {
                    if (ours.cannonsScanned && !ours.vertical && !ours.full) return false;   // 这台车确实没勾稳定器
                    if (Time.time - lastOwnRetry < OwnRetryEvery) return false;
                    lastOwnRetry = Time.time;
                }
                Scan();                                              // 幂等：保住已有锁角，只补新条目
                lanes.TryGetValue(drive, out lane);
            }
            else lane = adopted;
        }
        if (lane == null || !(lane.pitch || lane.yaw)) return false;
        if (Time.time - lastScan > RefreshEvery) Scan();             // 认车也要定期重做，别只靠陌生驱动触发
        lane.hits++;

        float pitch = MathF.Asin(Mathf.Clamp(incoming.normalized.y, -1f, 1f));
        float yaw = MathF.Atan2(incoming.x, incoming.z);
        float now = Time.time;
        float tau = Smoothing?.Value ?? 0.3f;
        // 3e：汇总/实测**只要接缝在跑就报**（旧写法要求本帧真改写过，而探针归 0 后几乎从不改写，日志会装死）。
        if (Log?.Value == true && now >= nextSummary) { nextSummary = now + ReportEvery; Summary(); Readout(); }
        lane.incomingPitch = pitch * Mathf.Rad2Deg;
        lane.incomingYaw = yaw * Mathf.Rad2Deg;        // 3j：双稳的归因要拿方位这一列和 `锁定方位` 对

        if (!lane.hasLock)
        {
            // 第一次看到这条驱动：以游戏这一帧要的方向落锁，这一帧不抢写。
            lane.lockedPitch = pitch; lane.lockedYaw = yaw; lane.lastTime = now; lane.hasLock = true;
            if (Log?.Value == true)
                Plugin.ModLog.LogInfo($"TOOL_STAB 落锁 {lane.typeName}@0x{(long)drive:X} 俯仰={pitch * Mathf.Rad2Deg:F2}° 方位={yaw * Mathf.Rad2Deg:F2}° 稳俯仰={lane.pitch} 稳方位={lane.yaw}");
            return false;
        }
        if (tau <= 0f) { lane.lockedPitch = pitch; lane.lockedYaw = yaw; lane.lastTime = now; return false; }

        float dt = Mathf.Clamp(now - lane.lastTime, 0f, 0.1f);
        lane.lastTime = now;
        float k = 1f - MathF.Exp(-dt / tau);
        float capture = (Capture?.Value ?? 25f) * Mathf.Deg2Rad;

        // 只对本次要稳的那一轴做低通，另一轴原样交回游戏这一帧要的值。
        float dPitch = lane.pitch ? ShortAngle(lane.lockedPitch, pitch) : 0f;
        float dYaw = lane.yaw ? ShortAngle(lane.lockedYaw, yaw) : 0f;
        if (lane.pitch) lane.lockedPitch = MathF.Abs(dPitch) > capture ? pitch : lane.lockedPitch + dPitch * k;
        if (lane.yaw) lane.lockedYaw = MathF.Abs(dYaw) > capture ? yaw : lane.lockedYaw + dYaw * k;
        float outPitch = lane.pitch ? lane.lockedPitch : pitch;
        float outYaw = lane.yaw ? lane.lockedYaw : yaw;
        float cp = MathF.Cos(outPitch);
        result = new Vector3(MathF.Sin(outYaw) * cp, MathF.Sin(outPitch), MathF.Cos(outYaw) * cp);

        float deltaDeg = MathF.Abs(dPitch) >= MathF.Abs(dYaw) ? dPitch * Mathf.Rad2Deg : dYaw * Mathf.Rad2Deg;
        lane.lastDeltaDeg = deltaDeg;
        lastDelta = deltaDeg;
        if (MathF.Abs(dPitch) < 0.02f && MathF.Abs(dYaw) < 0.02f) return false;   // 没什么可改的，别多发一次指令
        taken = lane;
        wroteCalls++;
        return true;
    }

    static float lastDelta;

    /// 汇总一行：接缝被调／改写次数、**权限挂点被调与真加的次数、游戏自己传进来的倍率区间**、权限设定、探针值。
    /// `挂点被调 0 次` ⇒ 两条 `MoveToTarget` 没被跑到（钩错方法或没接上）；
    /// `游戏给的 0.xx` 接近 0 ⇒ 基线本来就慢，权限得给更大；`lo=0` ⇒ 有路径真的传 0 进来（游戏那步会直接不动炮）。
    static void Summary()
    {
        int n = lanes.Count(l => l.Value.pitch || l.Value.yaw);
        int boosts = 0, applied = 0;
        float lo = float.MaxValue, hi = float.MinValue;
        foreach (var l in lanes.Values)
        {
            boosts += l.boosts; applied += l.applied;
            if (l.baseMin < lo) lo = l.baseMin;
            if (l.baseMax > hi) hi = l.baseMax;
        }
        string range = lo > hi ? "游戏传的倍率没读到" : $"游戏传的 speedMultiplier {lo:F2}~{hi:F2}";
        Plugin.ModLog.LogInfo($"TOOL_STAB 稳定中 {n} 条驱动｜收到 {sawCalls} 次游戏指令｜改写 {wroteCalls} 次" +
            $"｜陌生驱动 {unknownCalls} 次（自己车 {ownUnclaimedCalls}｜别家 {foreignUnclaimedCalls}）" +
            $"｜本条改写 {lastDelta:F2}°｜驱动权限 极速原值×{Authority?.Value ?? 1f:F1}夹在{RateFloor?.Value ?? 0f:F0}~{RateCap?.Value ?? 0f:F0}°/s" +
            $" 角加速度地板 {AccelFloor?.Value ?? 0f:F0}°/s²×{Torque?.Value ?? 1f:F1}" +
            $"（挂点被调 {boosts} 次｜重写时刻 {assertRuns} 次｜真写 {applied} 次｜{range}）");
    }

    /// 两个角差折到 (-π, π]：方位角绕 359°→1° 不能被当成 -358°。
    static float ShortAngle(float from, float to)
    {
        float d = to - from;
        while (d > MathF.PI) d -= 2f * MathF.PI;
        while (d < -MathF.PI) d += 2f * MathF.PI;
        return d;
    }

    /// 没登记过、而且归属判定也没认领的指针：每 2 秒最多报一行「哪条接缝、运行时原生类名、指针、它摇架所在的根」，
    /// 且**每个指针只报一次**。这场日志已经用它证明“不能无条件接管”：20 秒内同一条接缝上出现过三个不同指针
    /// （AI／敌军的车）；08:50 又用它证明**本车自己也有不止一条俯仰驱动**（炮声明的 `@…7E0` ≠ 每帧被调的 `@…F30`）。
    /// 3j：计数按归属拆开——「自己车上还没认到」和「别家的车」是两件不同的事，混在一个 `陌生驱动 N 次` 里
    /// 会把正常的时序（接缝先跑一步）看成几千次失败。
    static void NoteUnknown(Il2CppObjectBase driveObj, bool fromLayingSeam, OwnRoot? ours)
    {
        unknownCalls++;
        if (ours == null) foreignUnclaimedCalls++;
        else ownUnclaimedCalls++;
        if (Log?.Value != true) return;
        float now = Time.time;
        if (now < nextReport) return;
        nextReport = now + ReportEvery;
        if (!reportedUnknown.Add(driveObj.Pointer)) return;
        Plugin.ModLog.LogInfo($"TOOL_STAB 陌生驱动在{(fromLayingSeam ? "俯仰" : "方位")}接缝上：类={NativeName(driveObj)} 指针=0x{(long)driveObj.Pointer:X}" +
            Ownership(driveObj) + $"｜本方车根 {playerRoots.Count} 个｜{UnclaimedWhy(ours)}");
    }

    /// 没认领上到底因为什么（3j 把这句话写准，别再一律“没认领”）：
    /// 别家的车＝按设计不接管；自己车但那台车的炮已经枚举完且确实没勾＝不接管；
    /// 自己车但炮还没枚举完（接缝跑在认车前头）＝`Filter` 已经立刻催过重扫，这一条只是时序，不是失败。
    static string UnclaimedWhy(OwnRoot? ours)
    {
        if (ours == null) return "（不在本方车的变换树里⇒不接管，这个指针只报一次）";
        if (ours.vertical || ours.full)
            return $"（在本方车 {ours.name} 上、这台车勾了稳定器⇒接缝跑在认车前头，已立刻重扫认领）";
        if (!ours.cannonsScanned)
            return $"（在本方车 {ours.name} 上，但这台车的炮还没枚举完⇒旗标暂不可信，已立刻重扫）";
        return $"（在本方车 {ours.name} 上，但这台车没勾稳定器⇒不接管，这个指针只报一次）";
    }

    /// 3j：这条驱动挂在哪台本方车上（只有俯仰接缝读得到变换，方位接缝按“别家”走老节流）。
    static OwnRoot? OwnCarOf(Il2CppObjectBase driveObj)
    {
        try { return driveObj is LayingDriveBehaviour laying ? FindOwn(laying.trunnions) : null; }
        catch (Exception) { return null; }
    }

    /// 陌生驱动的归属读数：俯仰接缝能读（`LayingDriveBehaviour.trunnions`@0x10／`foundation`@0x18 是公共字段），
    /// 方位接缝读不到（`TurretBehaviour.transform`@0x38 是私有字段），所以后者只报类名与指针。
    static string Ownership(Il2CppObjectBase driveObj)
    {
        try
        {
            if (driveObj is not LayingDriveBehaviour laying) return "｜归属读不到（方位接缝的 transform 是私有字段）";
            IntPtr root = RootOf(laying.trunnions, out string rootName);
            float barrel = PitchOf(laying.trunnions), hull = PitchOf(laying.foundation);
            return $" 摇架={SafeName(laying.trunnions)} 根={rootName}@0x{(long)root:X}" +
                $" 摇架俯仰={barrel:F2}° 车体俯仰={hull:F2}°";   // 归属结论在 `UnclaimedWhy`，这里只报读数，别两处各下一个判词
        }
        catch (Exception ex) { return $"｜归属读数失败：{ex.GetType().Name} {ex.Message}"; }
    }

    /// 归属认领：这条驱动的摇架挂在**本方车**（`ControlType.Player`）的变换树上 ⇒ 按那辆车的旗标并集接管。
    /// 只在俯仰接缝做（方位接缝读不到变换）。认不出归属、或那辆车没勾稳定器 ⇒ null。
    static Lane? TryAdopt(Il2CppObjectBase driveObj, bool fromLayingSeam)
    {
        if (ownTransforms.Count == 0 || !fromLayingSeam) return null;
        try
        {
            if (driveObj is not LayingDriveBehaviour laying) return null;
            var own = FindOwn(laying.trunnions);
            if (own == null) return null;
            bool pitch = own.vertical, yaw = own.full;   // 这条驱动自带 azimuthInfo@0x54 ⇒ 双稳时两轴都在它身上
            if (!pitch && !yaw) return null;
            bool fresh = !lanes.ContainsKey(driveObj.Pointer);
            Add(driveObj.Pointer, pitch, yaw, NativeName(driveObj), laying, $"本方车 {own.name} 的驱动", true, own);
            own.gen = generation;
            var lane = lanes[driveObj.Pointer];
            if (fresh) AssertOne(driveObj.Pointer, lane, true);   // 4L：认领的当场就补写权限，不等下一次重扫
            if (fresh && Log?.Value == true)
                Plugin.ModLog.LogInfo($"TOOL_STAB 归车接管 {lane.typeName}@0x{(long)driveObj.Pointer:X}（摇架={SafeName(laying.trunnions)} 挂在 {own.name}，" +
                    $"这台车 垂稳={own.vertical} 双稳={own.full} ⇒ 稳俯仰={pitch} 稳方位={yaw}）");
            return lane;
        }
        catch (Exception ex)
        {
            Plugin.ModLog.LogWarning($"TOOL_STAB 归属认领失败：{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// 变换根的原生指针与名字；读不到返回 Zero／""（跨帧拿着的引用可能已经被销毁，所以全裹 try）。
    static IntPtr RootOf(Transform? t, out string name)
    {
        name = "";
        try
        {
            var r = t?.root;
            if (r == null) return IntPtr.Zero;
            name = SafeName(r);
            return r.Pointer;
        }
        catch (Exception) { return IntPtr.Zero; }
    }

    static string SafeName(Transform? t)
    {
        try { return t == null ? "无" : t.name ?? "无"; } catch (Exception) { return "读不到"; }
    }

    /// 把这台玩家车变换树上的**全部** Transform 指针记进 `ownTransforms`（重扫时整表重建，所以换车／拆件会跟着更新）。
    /// 一台车可能有几百个变换，`Scan` 最快 1 秒一次，这个代价换来的是“陌生驱动到底是不是我们自己的炮”能一次问清。
    static void FillOwnTransforms(VehicleBehaviour v, OwnRoot own)
    {
        try
        {
            var ts = v.GetComponentsInChildren<Transform>(true);
            foreach (var t in ts ?? Array.Empty<Transform>())
            {
                if (t == null) continue;
                IntPtr p = t.Pointer;
                if (p == IntPtr.Zero) continue;
                ownTransforms[p] = own;
                own.transforms++;
            }
        }
        catch (Exception ex) { Plugin.ModLog.LogWarning($"TOOL_STAB 本车变换表采集失败：{ex.Message}"); }
    }

    /// 从这条变换往上找（最多 12 级），看它属不属于某台玩家车；不属于 ⇒ null。
    static OwnRoot? FindOwn(Transform? t)
    {
        try
        {
            for (int i = 0; t != null && i < 12; i++)
            {
                if (ownTransforms.TryGetValue(t.Pointer, out var own)) return own;
                t = t.parent;
            }
        }
        catch (Exception) { }
        return null;
    }

    // ================= 认车 =================

    /// 认车（3c）：扫**全部** `ControlType.Player` 的车（3b 只取第一台是个缺陷），每辆车取
    /// ① 炮声明的两条机制（按原生指针登记，认不认得出类型都拿得到）、
    /// ② 车上所有 `LayingDrive.Behaviour`（俯仰驱动组件，dump.cs:870346）登记进俯仰轴＋双稳时的方位轴、
    /// ③ 车上所有 `TraverseMotor.Behaviour`（回旋驱动组件，dump.cs:877163）登记进方位轴，
    /// 并记下该车变换根指针给归属认领用。**幂等**：已有条目就地更新旗标、保留锁角与校验计数；
    /// 连续 `KeepScans` 次没再被登记到的才丢。
    static void Scan()
    {
        lastScan = Time.time;
        generation++;
        if (DesignEditor.Instance?.IsReady == true)
        {
            // 设计器里改的可能是另一辆车：表清空，进战斗重新落锁（这条是唯一一次“清记忆”）。
            if (!wasInDesigner) { lanes.Clear(); reportedUnknown.Clear(); playerRoots.Clear(); ownTransforms.Clear(); lastScanLine = ""; }
            wasInDesigner = true;
            return;
        }
        if (wasInDesigner && Log?.Value == true) Plugin.ModLog.LogInfo("TOOL_STAB 离开设计器：重读车上的稳定器旗标");
        wasInDesigner = false;

        // 本次重扫先当成“谁都还没被声明”：下面登记时置 declared*，Prune 里再落回 pitch/yaw——
        // 这样炮上把双稳关掉，方位轴当场就不稳了（并集只写在上一版的旗标上会关不掉）。
        foreach (var l in lanes.Values) { l.declaredPitch = false; l.declaredYaw = false; }

        try
        {
            VehicleBehaviour[] vehicles;
            try { vehicles = UnityEngine.Object.FindObjectsOfType<VehicleBehaviour>() ?? Array.Empty<VehicleBehaviour>(); }
            catch (Exception ex) { Plugin.ModLog.LogWarning($"TOOL_STAB 认车失败（找车）：{ex.Message}"); return; }

            playerRoots.Clear();
            ownTransforms.Clear();
            var text = new System.Text.StringBuilder();
            int pv = 0, on = 0, wantFull = 0, layingDrives = 0, turretDrives = 0;
            foreach (var v in vehicles)
            {
                if (v == null || v.ControlType != ControlType.Player) continue;
                pv++;
                IntPtr root = RootOf(v.transform?.root, out string rootName);
                OwnRoot? own = null;
                if (root != IntPtr.Zero)
                {
                    if (!playerRoots.TryGetValue(root, out own)) { own = new OwnRoot { name = rootName }; playerRoots[root] = own; }
                }
                if (own != null)
                {
                    own.gen = generation;
                    if (own.hull == null) { try { own.hull = v.transform?.root; } catch (Exception) { } }
                    FillOwnTransforms(v, own);
                }

                Cannon[]? cannons = null;
                try { cannons = v.GetComponentsInChildren<Cannon>(true); }
                catch (Exception ex) { text.Append($"\n  车{pv} 根={rootName}@0x{(long)root:X} 找炮失败：{ex.Message}"); continue; }
                foreach (var gun in cannons ?? Array.Empty<Cannon>())
                {
                    if (gun == null) continue;
                    byte flags;
                    try { flags = unchecked((byte)gun.InstanceBlueprint.Flags); }
                    catch { continue; }      // 蓝图没挂上（原版车）就当没这条旗标
                    bool vertical = (flags & VerticalBit) != 0, full = (flags & FullBit) != 0;
                    if (!vertical && !full) continue;
                    on++;
                    if (full) wantFull++;
                    if (own != null) { own.cannons++; own.vertical |= vertical; own.full |= full; }

                    var vertAim = gun.VerticalAimingMechanism;
                    var horizAim = gun.HorizontalAimingMechanism;
                    IntPtr vPtr = Ptr(vertAim), hPtr = Ptr(horizAim);
                    LayingDriveBehaviour? vert = null;
                    try { vert = vertAim?.TryCast<LayingDriveBehaviour>(); }
                    catch (Exception ex) { Plugin.ModLog.LogWarning($"TOOL_STAB 取俯仰驱动失败：{ex.Message}"); }

                    Add(vPtr, vertical, full, NativeName(vertAim), vert, "炮声明的俯仰机制", false, own);
                    if (hPtr != vPtr) Add(hPtr, false, full, NativeName(horizAim), null, "炮声明的方位机制", false, own);

                    text.Append($"\n  炮 vuid={(int)gun.VehicleObject.VUID} flags={flags}（原始字节，勾了应为 2／4／6）" +
                        $" 俯仰机制={NativeName(vertAim)}@0x{(long)vPtr:X}" +
                        $" 方位机制={NativeName(horizAim)}@0x{(long)hPtr:X}" +
                        (vPtr != IntPtr.Zero && vPtr == hPtr ? "（与俯仰同一个对象⇒两个轴都在这一条驱动上）" : "") +
                        (vert == null && vertical ? " ⇒ 垂稳认不出俯仰驱动" : ""));
                }

                // 3j：这台车的炮**枚举完了**（无论有没有勾），旗标从此可信——接缝拿它区分“没勾”和“还没扫到”。
                if (own != null) own.cannonsScanned = true;

                // 车上真正在跑的驱动：从持有它们的两个 VehicleComponent（ MonoBehaviour）身上取 .Behaviour。
                // 这两路是 3b 的病灶——`LayingDriveBehaviour`／`TurretBehaviour` 本身不是组件，按组件枚举必定 0 条。
                if (own != null && (own.vertical || own.full))
                {
                    int thisLaying = 0, thisTurret = 0;
                    try
                    {
                        var drives = v.GetComponentsInChildren<LayingDrive>(true);
                        foreach (var d in drives ?? Array.Empty<LayingDrive>())
                        {
                            if (d == null) continue;
                            var beh = d.Behaviour;
                            IntPtr p = Ptr(beh);
                            if (p == IntPtr.Zero) continue;
                            LayingDriveBehaviour? c = null;
                            try { c = beh?.TryCast<LayingDriveBehaviour>(); } catch (Exception) { }
                            thisLaying++;
                            Add(p, own.vertical, own.full, NativeName(beh), c, $"俯仰驱动组件 {d.ComponentID}", false, own);
                        }
                        layingDrives += thisLaying;
                    }
                    catch (Exception ex) { text.Append($"\n  车{pv} 俯仰驱动组件取失败：{ex.Message}"); }

                    try
                    {
                        var motors = v.GetComponentsInChildren<TraverseMotor>(true);
                        foreach (var m in motors ?? Array.Empty<TraverseMotor>())
                        {
                            if (m == null) continue;
                            var beh = m.Behaviour;
                            IntPtr p = Ptr(beh);
                            if (p == IntPtr.Zero) continue;
                            thisTurret++;
                            Add(p, false, own.full, NativeName(beh), null, $"回旋驱动组件 {m.ComponentID}");
                        }
                        turretDrives += thisTurret;
                    }
                    catch (Exception ex) { text.Append($"\n  车{pv} 回旋驱动组件取失败：{ex.Message}"); }

                    text.Append($"\n  车{pv} 根={rootName}@0x{(long)root:X} 炮 {cannons?.Length ?? 0} 门（勾了 {own.cannons}）" +
                        $"｜俯仰驱动组件 {thisLaying} 条｜回旋驱动组件 {thisTurret} 条｜本车变换 {own.transforms} 个");
                }
            }

            Prune();
            Reassert("认车重扫");      // 4L：权限写入的正门之一（勾上新加／换车／驱动重建后都在这里补一次）
            if (pv == 0)
            {
                if (Log?.Value == true) Plugin.ModLog.LogInfo("TOOL_STAB 认车：场上没有玩家控的车（战斗外？），这次不接管");
                return;
            }
            string line = $"TOOL_STAB 认车：玩家控的 {pv} 台｜勾了稳定器的炮 {on} 门（其中双稳 {wantFull} 门）" +
                $"｜俯仰驱动组件 {layingDrives} 条｜回旋驱动组件 {turretDrives} 条" +
                $"｜本方变换 {ownTransforms.Count} 个｜接管 {lanes.Count(l => l.Value.pitch || l.Value.yaw)} 条驱动{text}";
            if (Log?.Value == true && line != lastScanLine) { lastScanLine = line; Plugin.ModLog.LogInfo(line); }
        }
        catch (Exception ex) { Plugin.ModLog.LogWarning($"TOOL_STAB 认车失败：{ex.Message}"); }
    }

    /// 把本次重扫声明到的轴落回 `pitch`／`yaw`（勾掉了就当场不再稳），并丢掉连续 `KeepScans` 次
    /// 没再被声明到的条目（换车、炮被拆、驱动已销毁），免得拿野指针当接管对象。
    /// 靠变换根认领下来的条目（`adopted`）不靠炮声明续期：只要它的根还在本方车集合里就按那辆车的旗标续。
    static void Prune()
    {
        if (lanes.Count == 0) return;
        List<IntPtr>? stale = null;
        foreach (var kv in lanes)
        {
            var l = kv.Value;
            if (l.stamp == generation)
            {
                l.pitch = l.declaredPitch; l.yaw = l.declaredYaw;
                if (!(l.pitch || l.yaw)) RestoreLimits(kv.Key, l);   // 勾掉了：之后可能再没人调 MoveToTarget，权限当场还回去
                continue;
            }
            if (l.adopted && l.own is { } own && own.gen == generation)
            {
                l.stamp = generation;
                l.declaredPitch = l.pitch = own.vertical;
                l.declaredYaw = l.yaw = own.full;
                if (!(l.pitch || l.yaw)) RestoreLimits(kv.Key, l);
                continue;
            }
            if (generation - l.stamp > KeepScans) { (stale ??= new List<IntPtr>()).Add(kv.Key); continue; }
            l.pitch = false; l.yaw = false;   // 这一轮没被声明到（炮被拆／勾去掉）：先停稳，锁角留着等它回来
            RestoreLimits(kv.Key, l);
        }
        if (stale == null) return;
        foreach (var k in stale)
        {
            if (lanes.TryGetValue(k, out var l)) RestoreLimits(k, l);
            lanes.Remove(k); reportedUnknown.Remove(k);
        }
    }

    /// 实测读数：取**被调到最多**的两条俯仰驱动（08:50 那版挑中了 `落地校验 0/0` 的静默条目，看不出真正在动炮的那条）。
    /// 三列对照才分得清病在哪：`游戏要`＝游戏这一帧要的世界方向俯仰、`锁定`＝滤波器给的角、
    /// `摇架`＝炮身实际世界俯仰、`车体`＝底盘世界俯仰。稳定生效的样子是：`车体` 随颠簸大幅起伏，
    /// `摇架` 跟着 `锁定` 基本不动；若 `摇架` 一直贴着 `车体` 走 ⇒ 这条驱动没在动炮，或写进去没落地。
    static void Readout()
    {
        var hot = lanes.Where(kv => kv.Value.pitch && kv.Value.laying != null)
            .OrderByDescending(kv => kv.Value.hits).Take(2).ToList();
        foreach (var kv in hot)
        {
            var lane = kv.Value;
            try
            {
                float barrel = PitchOf(lane.laying!.trunnions), hull = PitchOf(lane.laying.foundation);
                if (float.IsNaN(barrel) && float.IsNaN(hull)) continue;
                // 另外两层：摇架的父级（炮塔／炮架）与这台车的变换根（肉眼看得见的那层车体）。
                // 09:2x 的争议就在这——`摇架` 明明贴着 `锁定`、`车体` 自己起伏，用户却说炮管跟着颠簸，
                // 说明这两个变换里至少有一个不是他看见的那根管子/那层车体，必须多量几层才能对齐眼睛。
                float parent = PitchOf(lane.laying.trunnions?.parent), root = PitchOf(lane.own?.hull);
                Plugin.ModLog.LogInfo($"TOOL_STAB 实测·俯仰 {lane.typeName}@0x{(long)kv.Key:X}（{lane.part}）" +
                    $"｜本条被调 {lane.hits} 次｜游戏要 俯仰 {lane.incomingPitch:F2}° 方位 {lane.incomingYaw:F2}°" +
                    $" 锁定 {lane.lockedPitch * Mathf.Rad2Deg:F2}°／{lane.lockedYaw * Mathf.Rad2Deg:F2}°" +
                    $" 驱动在追 {Fmt(lane.gamePitchDeg)}°／{Fmt(lane.gameYawDeg)}°（3j：这一列≠锁定⇒我们写的方向没走到这台驱动）" +
                    $"｜摇架 {barrel:F2}° 摇架父级 {parent:F2}° 支座 {hull:F2}° 车根 {root:F2}°" +
                    $"｜写后回读 {lane.lastWrittenPitch:F2}°｜落地校验 {lane.writeChecks - lane.writeFails}/{lane.writeChecks} 对上" +
                    $"｜权限 真写 {lane.applied} 次／挂点被调 {lane.boosts} 次 游戏给的 {Range(lane)}" +
                    $"｜峰速率 炮身 {lane.barrelPeakRate:F1} 车体 {lane.hullPeakRate:F1}°/s{(lane.inSettle ? "（归位窗内，峰值不记）" : "")}" +
                    $"｜偏差 游戏两轴取大 {Fmt(lane.devLast)}（峰 {Fmt(lane.devPeak)}）" +
                    $" 我算俯仰 {Fmt(lane.devOwnLast)}（峰 {Fmt(lane.devOwnPeak)}） 我算方位 {Fmt(lane.devAzLast)}（峰 {Fmt(lane.devAzPeak)}）" +
                    $"⇒ {DevVerdict(lane.devPeak, lane.devOwnPeak, lane.devAzPeak)}{(lane.inSettle ? "｜归位窗：刚接管几秒内不记峰值" : "")}" +
                    $"｜轴状态 俯仰{(lane.elevLocked ? "锁" : "松")} 方位{(lane.azLocked ? "锁" : "松")} 可达{(lane.achievable ? "是" : "否")} 有靶{(lane.hasTarget ? "是" : "否")} {Flags(kv.Key)}" +
                    $"｜俯仰驱动 力矩 {Fmt(RawF(kv.Key, 0x40))}（原 {Orig(lane, 0x40)}｜反解目标 {FmtOr(lane.torqueTargetPitch, "没算")}）" +
                    $" 极速 {Fmt(RawF(kv.Key, 0x44))}（原 {Orig(lane, 0x44)}｜应写 {Fmt(RateTarget(BaseValue(kv.Key, lane, 0x44)))}）" +
                    $" 行程 {Fmt(RawF(kv.Key, 0x48))}~{Fmt(RawF(kv.Key, 0x4C))}" +
                    $"｜方位驱动 力矩 {Fmt(RawF(kv.Key, 0x54))}（原 {Orig(lane, 0x54)}｜反解目标 {FmtOr(lane.torqueTargetYaw, "没算")}）" +
                    $" 极速 {Fmt(RawF(kv.Key, 0x58))}（原 {Orig(lane, 0x58)}｜应写 {Fmt(RateTarget(BaseValue(kv.Key, lane, 0x58)))}）" +
                    $" 行程 {Fmt(RawF(kv.Key, 0x5C))}~{Fmt(RawF(kv.Key, 0x60))}" +
                    $"｜车体参数 质量 {Fmt(RawF(kv.Key, 0x68))} 力臂 {Fmt(RawF(kv.Key, 0x6C))} 惯量 {Fmt(RawF(kv.Key, 0x70))}" +
                    $"｜旋钮 极速×{Fmt(Authority?.Value ?? 1f)}夹在{Fmt(RateFloor?.Value ?? 0f)}~{Fmt(RateCap?.Value ?? 0f)}°/s" +
                    $" 角加速度地板 {Fmt(AccelFloor?.Value ?? 0f)}°/s²×{Fmt(Torque?.Value ?? 1f)}" +
                    $"｜重写时刻累计 {assertRuns} 次（真写 {assertWrites}）");
                lane.hullPeakRate = 0f;      // 只对上一个读数窗口负责，别把开场那一下颠簸一直挂着
                lane.barrelPeakRate = 0f;
                lane.devPeak = 0f; lane.devOwnPeak = 0f; lane.devAzPeak = float.NaN;

                // 3g：用户 10:0x 说「肉眼看到炮管本身在晃」，可 `摇架` 那一列的俯仰明明钉住（09:4x 是 0.36 对 −11.99）。
                // 冲突只有两种解释，这一行把两者一次量掉：
                // ① 我量的这层／我写的这层**不是**他看见的那根管子 ⇒ 把摇架的祖先链和子级全列出来，谁在跟车体走一目了然；
                // ② 晃的是**别的自由度**——俯仰被钉住了，但横滚（车体侧倾，俯仰轴根本补偿不了）、
                //    方位（回旋轴是车体系的，侧倾时世界方位跟着漂）、升沉（悬挂把炮根整个抬上抬下，角度稳、位置不稳）
                //    都不在 `摇架 俯仰` 这一列里。所以这里对同一层同时量 俯仰／横滚／方位／世界Y。
                // 顺带量主相机（观瞄画面）——它若贴着车根走，说明画面本身没稳，那是另一条活。
                var trun = lane.laying.trunnions as Transform;
                var hullTr = lane.laying.foundation as Transform;
                var rootTr = lane.own?.hull as Transform;
                var cam = Camera.main;
                Plugin.ModLog.LogInfo($"TOOL_STAB 实测·姿态｜{Pose("摇架", trun)}｜{Pose("摇架父级", trun?.parent)}" +
                    $"｜{Pose("支座", hullTr)}｜{Pose("车根", rootTr)}｜{Pose("相机", cam?.transform)}");
                DumpTree(trun, rootTr);
            }
            catch (Exception ex) { Plugin.ModLog.LogWarning($"TOOL_STAB 实测读数失败：{ex.Message}"); }
        }

        // 炮塔回旋那条读不到变换（`TurretBehaviour.transform`@0x38 是私有字段），只能报计数＋它自己的三条上限
        // （maxAcceleration@0x10／maxBrakeAcceleration@0x14／maxTraverseRate@0x18，都是公共可写属性背后的字段）。
        // 若这几列每帧/每次读数都在变 ⇒ 游戏每帧重算，写进去会被顶掉；若不动 ⇒ 方位侧可以直接写这三个口。
        foreach (var kv in lanes.Where(kv => kv.Value.yaw && !IsLaying(kv.Value) && kv.Value.boosts > 0).Take(2))
        {
            var lane = kv.Value;
            Plugin.ModLog.LogInfo($"TOOL_STAB 实测·回旋 {lane.typeName}@0x{(long)kv.Key:X}（{lane.part}）" +
                $"｜接缝被调 {lane.hits} 次｜权限真写 {lane.applied} 次／挂点被调 {lane.boosts} 次 游戏给的 {Range(lane)}" +
                $"｜回旋上限 加速 {Fmt(RawF(kv.Key, 0x10))}°/s²（原 {Orig(lane, 0x10)}｜应写 {Fmt(AccelTarget(BaseValue(kv.Key, lane, 0x10)))}）" +
                $" 刹车 {Fmt(RawF(kv.Key, 0x14))}°/s²（原 {Orig(lane, 0x14)}｜应写 {Fmt(AccelTarget(BaseValue(kv.Key, lane, 0x14)))}）" +
                $" 极速 {Fmt(RawF(kv.Key, 0x18))}°/s（原 {Orig(lane, 0x18)}｜应写 {Fmt(RateTarget(BaseValue(kv.Key, lane, 0x18)))}）" +
                $" 官方力矩倍率 {Fmt(RawF(kv.Key, 0x30))}（3j：只写上面三位，这一位留给游戏自己评估时用）" +
                $"｜旋钮 极速×{Fmt(Authority?.Value ?? 1f)}夹在{Fmt(RateFloor?.Value ?? 0f)}~{Fmt(RateCap?.Value ?? 0f)}°/s" +
                $" 角加速度地板 {Fmt(AccelFloor?.Value ?? 0f)}°/s²×{Fmt(Torque?.Value ?? 1f)}");
        }
    }

    static string Range(Lane lane) => lane.baseMin > lane.baseMax ? "没读到" : $"{lane.baseMin:F2}~{lane.baseMax:F2}";

    /// 一层的完整姿态：俯仰（forward 的 y）／横滚（up 在世界 XZ 里的侧倾）／方位（forward 的水平朝向）／世界 Y。
    /// 只用于对照读数，小角度近似够用——要看的是「哪一层跟着车体走、哪一层被钉住」，不是绝对精度。
    static string Pose(string label, object? t)
    {
        try
        {
            var tr = t as Transform;
            if (tr == null) return $"{label} 无";
            var f = tr.forward;
            var u = tr.up;
            var p = tr.position;
            float pitch = f.sqrMagnitude < 1e-6f ? float.NaN : MathF.Asin(Mathf.Clamp(f.normalized.y, -1f, 1f)) * Mathf.Rad2Deg;
            float roll = MathF.Atan2(u.x, u.y) * Mathf.Rad2Deg;
            float yaw = MathF.Atan2(f.x, f.z) * Mathf.Rad2Deg;
            return $"{label} 俯仰 {pitch:F2} 横滚 {roll:F2} 方位 {yaw:F2} Y {p.y:F2}";
        }
        catch (Exception) { return $"{label} 读不到"; }
    }

    /// 摇架的祖先链（到车根）＋子级（往下两层，最多 16 条）：一次跑出「钉住的那层」和「他看见的那层」是不是同一个。
    static void DumpTree(Transform? trun, Transform? hull)
    {
        try
        {
            if (trun == null) return;
            var up = new List<string>();
            for (Transform? p = trun; p != null && up.Count < 8; p = p.parent) up.Add(p.name);
            var dn = new List<string>();
            Walk(trun, 0, dn);
            Plugin.ModLog.LogInfo($"TOOL_STAB 实测·变换链 {(up.Count == 0 ? "无" : string.Join(" <- ", up))}");
            if (dn.Count > 0) Plugin.ModLog.LogInfo($"TOOL_STAB 实测·摇架子级｜{string.Join("｜", dn)}");
            // 车根下面所有名字像炮管／炮盾／摇架的层（万一他看见的那根管子挂在别处，这里就会露出来）
            var gun = new List<string>();
            if (hull != null)
            {
                foreach (var c in hull.GetComponentsInChildren<Transform>(true))
                {
                    if (c == null) continue;
                    string nm = c.name.ToLowerInvariant();
                    if (!nm.Contains("barrel") && !nm.Contains("gun") && !nm.Contains("trunn") &&
                        !nm.Contains("mant") && !nm.Contains("recoil") && !nm.Contains("cannon")) continue;
                    gun.Add(Pose(c.name, c));
                    if (gun.Count >= 12) break;
                }
            }
            if (gun.Count > 0) Plugin.ModLog.LogInfo($"TOOL_STAB 实测·车上炮名层｜{string.Join("｜", gun)}");
        }
        catch (Exception ex) { Plugin.ModLog.LogWarning($"TOOL_STAB 变换树读数失败：{ex.Message}"); }
    }

    static void Walk(Transform t, int depth, List<string> into)
    {
        if (depth >= 2 || into.Count >= 16) return;
        int n = t.childCount;
        for (int i = 0; i < n; i++)
        {
            var c = t.GetChild(i);
            if (c == null) continue;
            into.Add(Pose(c.name, c));
            Walk(c, depth + 1, into);
        }
    }

    static float PitchOf(object? t)
    {
        try
        {
            var tr = t as Transform;
            if (tr == null) return float.NaN;
            var f = tr.forward;
            if (f.sqrMagnitude < 1e-6f) return float.NaN;
            return MathF.Asin(Mathf.Clamp(f.normalized.y, -1f, 1f)) * Mathf.Rad2Deg;
        }
        catch (Exception) { return float.NaN; }
    }

    /// 同指针的条目**就地更新**：轴旗标取并集（这次重扫的并集，落回要看 `Prune`），
    /// 锁角／滤波记忆／校验计数全部留着——这一条就是第二版失效的解药。
    static void Add(IntPtr ptr, bool pitch, bool yaw, string typeName, LayingDriveBehaviour? laying,
        string part = "", bool adopted = false, OwnRoot? own = null)
    {
        if (ptr == IntPtr.Zero || (!pitch && !yaw)) return;
        if (lanes.TryGetValue(ptr, out var old))
        {
            old.declaredPitch |= pitch; old.declaredYaw |= yaw; old.stamp = generation;
            old.pitch |= pitch; old.yaw |= yaw;   // 立刻生效（关轴靠 Prune 把 declared* 落回来，不等下一次重扫）
            if (typeName.Length > 0) old.typeName = typeName;
            if (laying != null) old.laying = laying;
            if (part.Length > 0) old.part = part;
            if (own != null) { old.adopted = adopted; old.own = own; }
            return;
        }
        lanes[ptr] = new Lane
        {
            pitch = pitch, yaw = yaw, declaredPitch = pitch, declaredYaw = yaw,
            stamp = generation, typeName = typeName, laying = laying,
            part = part, adopted = adopted, own = own,
            claimedAt = Time.time        // 3j：归位窗从这一刻起算（刚接管的几秒内峰值不记，见 `InSettle`）
        };
    }

    /// 原生指针与运行时类名。interop 给字段声明类型（`IReadOnlyAimable`）生成的包装类名没有信息量，
    /// 所以类名直接从原生对象头的 klass 取（`il2cpp_object_get_class`＋`il2cpp_class_get_name`），
    /// 给出的才是真子类名——和上游 `DriveRecorder.cs:126` 的 `GetIl2CppType()` 同一个信息来源。
    static IntPtr Ptr(object? o)
    {
        try { return o == null ? IntPtr.Zero : ((Il2CppObjectBase)o).Pointer; }
        catch { return IntPtr.Zero; }
    }

    static string NativeName(object? o)
    {
        var ptr = Ptr(o);
        if (ptr == IntPtr.Zero) return "无";
        try
        {
            var klass = IL2CPP.il2cpp_object_get_class(ptr);
            if (klass != IntPtr.Zero) return Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(klass)) ?? "?";
        }
        catch (Exception) { }
        try { return o!.GetType().Name; } catch (Exception) { return "?"; }
    }
}
