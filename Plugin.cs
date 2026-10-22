using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Sprocket.UI;
using UnityEngine;
using UnityEngine.Events;

// SprocketClipboard（部件剪贴板，单独一个 mod）要用这里已经验过的设计编辑管线和蓝图 id 规则，免得两处各写一份、日后走样。
[assembly: InternalsVisibleTo("SprocketClipboard")]

namespace SprocketTools;

/// SprocketTools: small editor improvements, each one a section in the game's own inspector panels.
[BepInPlugin("local.sprocket.tools", "SprocketTools", "1.3.0")]
public sealed class Plugin : BasePlugin
{
    internal const string AuthorEn = "七夜已逝";
    internal const string AuthorZh = "七夜已逝";
    internal static ManualLogSource ModLog = null!;
    internal static ConfigEntry<string>? Folded;
    internal static ConfigEntry<bool>? ShowHotkeys;
    internal static ConfigEntry<bool>? PartMassMarkers;
    internal static ConfigEntry<bool>? DrawingNoWireframe;
    internal static ConfigEntry<bool>? DrawingSeeThroughOutline;
    internal static ConfigEntry<bool>? DrawingColourOutline;
    internal static ConfigEntry<float>? DrawingIntensity;
    internal static ConfigEntry<bool>? DrawingElevation, DrawingTraverse, DrawingBlue, DrawingTurretTraverse;
    internal static ConfigEntry<bool>? DrawingCreaseNormals;
    internal static ConfigEntry<bool>? DrawingSeamFilter;
    internal static ConfigEntry<bool>? DrawingAntiAliasing;
    internal static ConfigEntry<bool>? DrawingDevMode;
    internal static ConfigEntry<bool>? DrawingGrid;
    internal static ConfigEntry<float>? DrawingGridIntensity;
    internal static ConfigEntry<string>? DrawingHiddenGunLimits;
    internal static ConfigEntry<float>? ExplodeSpread, FlashlightPercent, FullbrightPercent;
    internal static ConfigEntry<int>? BackupsKept;
    internal static ConfigEntry<float>? RotationSnap;
    internal static ConfigEntry<bool>? MirrorMerge;
    internal static ConfigEntry<string>? ShortcutsPosition;
    internal static ConfigEntry<int>? CardSideOffset, CardElevation, CardAimHeight, CardWidth, CardHeight, CardLightingRecipe;
    internal static ConfigEntry<bool>? CardLeftSide, CardWhiteBackground, CardFlatLighting;
    internal static ConfigEntry<float>? CardFieldOfView, CardFraming, CardFullbrightPercent;
    internal static ConfigEntry<float>? AmmoApFactor, AmmoApheFactor, AmmoApcbcFactor, AmmoApcrFactor, AmmoHeatFactor;
    internal static ConfigEntry<float>? AmmoAphePower, AmmoHePower, AmmoFuzeDelay;
    internal static ConfigEntry<bool>? BallisticsEnable, BallisticsArmourStop;
    internal static ConfigEntry<bool>? EraFxOn;
    internal static ConfigEntry<float>? EraFxScale;
    internal static ConfigEntry<float>? SpacedGap, SpacedPenalty;
    internal static ConfigEntry<bool>? EraDisappears;
    internal static ConfigEntry<string>? EraTrigger;
    internal static ConfigEntry<float>? PostPenScale;
    internal static ConfigEntry<string>? PostPenMode;
    internal static ConfigEntry<bool>? ApcbcNoRicochet;
    internal static ConfigEntry<bool>? HeatStaticPen;
    internal static ConfigEntry<float>? HeatJetSpeed;
    internal static ConfigEntry<bool>? TrackSegments;
    internal static ConfigEntry<float>? TrackSegmentDamage;
    internal static ConfigEntry<float>? BallisticsApcrDrag, BallisticsHeatDrag, BallisticsApheCharge, BallisticsHeCharge;
    internal static ConfigEntry<bool>? ArmourMaterials;
    internal static ConfigEntry<bool>? GlassTransparent;
    internal static ConfigEntry<string>? GlassLookMode;
    internal static ConfigEntry<bool>? LightSwitch;
    internal static ConfigEntry<string>? PhotoResolution, PhotoMethod;
    public override void Load()
    {
        var loadWatch = Stopwatch.StartNew();
        ModLog = Log;
        ModLog.LogInfo($"Author: {AuthorZh}");
        DrawingNoWireframe = Config.Bind("Drawing sheet", "No wireframe", false, "F9 彩色图与透视导出中省略线框叠加层。单独的纯线条图纸不受影响。");
        DrawingSeeThroughOutline = Config.Bind("Drawing sheet", "See-through outline only", true, "透视内部视图只保留载具轮廓剪影，不再绘制网格边线。“取消线框”时该轮廓也一并关闭。");
        DrawingColourOutline = Config.Bind("Drawing sheet", "Colour outline only", true, "彩色图纸只画可见的部件轮廓与锐边，不含网格三角化线。“取消线框”时这些轮廓线也一并关闭。");
        DrawingIntensity = Config.Bind("Drawing sheet", "Wireframe intensity percent", 100f, "F9 图纸上几何线稿的浓度，0 至 100。涂装、尺寸标注与文字保持原有浓度。");
        DrawingCreaseNormals = Config.Bind("Drawing sheet", "Creases from shading normals", false, "F9 线稿改按网格自己烘好的顶点法线判断硬边，而不是按每个三角形的几何法线：光滑着色的圆弧面（炮塔弧面、翼子板圆角）不再画出分面棱，真硬边照旧。默认关闭。");
        DrawingSeamFilter = Config.Bind("Drawing sheet", "Hide plate joints", false, "F9 线稿隐去装甲板对拼的拼接缝：一条开边的另一侧若被另一块网格以同高同向的面接住，就不画。车体外轮廓与折棱不受影响；与板面齐平的舱盖边线也会一并消失。默认关闭。");
        DrawingAntiAliasing = Config.Bind("Drawing sheet", "Line anti-aliasing", false, "F9 线条图（和由它生成的蓝图纸）按两倍分辨率重画再折回：每个像素按线条盖住的比例上墨，斜边与圆弧上的阶梯换成浓淡。四个视图都要重画一遍，出图明显更慢；只影响线条图，彩色图与透视图的描边不变。默认关闭。");
        DrawingDevMode = Config.Bind("Drawing sheet", "Developer mode", false, "开发者模式：F9 除了常规三张图，再补出同槽位的对照图，名字带“-开发者图纸”后缀——“名称-开发者图纸（彩色图）／（透视图）”仍用背景那层品红供色（常规版已改成黑底供色，这两张用来看品红晕开的那条边），开着抗锯齿时另出“名称-开发者图纸”即未做抗锯齿的线条图；并在日志给出品红像素数、两趟颜色差异、半色像素占比与补拍耗时。默认关闭。");
        DrawingElevation = Config.Bind("Drawing sheet", "Gun elevation and depression", false, "侧视图中用虚线标出瞄准驱动上下极限处的火炮位置。不改动载具。");
        DrawingTraverse = Config.Bind("Drawing sheet", "Gun traverse", false, "顶视图中用虚线标出瞄准驱动左右极限处的火炮位置（含炮廓车型）。不改动载具。");
        DrawingTurretTraverse = Config.Bind("Drawing sheet", "Turret rotation", false, "顶视图中按方向机的最小/最大角度标出炮塔旋转。整周旋转画成 360 度圆弧。");
        DrawingBlue = Config.Bind("Drawing sheet", "Blue blueprint", false, "另外保存一张蓝底白线图纸，尺寸与火炮活动选项相同。");
        DrawingGrid = Config.Bind("Drawing sheet", "Blueprint grid", false, "仅给蓝底图纸添加淡色方格背景。小格按绘图比例为 0.25 米，每米一条较深的线。");
        DrawingGridIntensity = Config.Bind("Drawing sheet", "Grid intensity percent", 20f, "蓝底图纸背景网格的浓度，0 至 100。载具线条与文字绘制在其上。");
        DrawingHiddenGunLimits = Config.Bind("Drawing sheet", "Hidden gun limits", "[]", "仅从火炮活动极限标注中排除的火炮。由火炮面板中的“该炮极限标注”管理。按设计名和火炮 ID 存储；重命名设计后会另起一份选择。");
        Folded = Config.Bind("Panels", "Folded sections", "", "在编辑器面板中折叠起来的 SprocketTools 小节，用 | 分隔");
        ShowHotkeys = Config.Bind("Panels", "Show hotkeys box", true, "手工结构面板旁边的快捷键面板（编辑器中按 F1 显示或隐藏）");
        ExplodeSpread = Config.Bind("Panels", "Exploded view spread", 0.5f, "爆炸视图（F2）把部件移开的距离，单位为米（F3 / F4 调整）");
        FlashlightPercent = Config.Bind("Panels", "Flashlight brightness", 80f, "手电筒（F6）照射位置的亮度，按太阳光强度的百分比计");
        FullbrightPercent = Config.Bind("Panels", "Fullbright brightness", 25f, "全亮（F7）的 14 个灯各自的亮度，按太阳光强度的百分比计");
        PartMassMarkers = Config.Bind("Panels", "Part mass markers", true, "部件质心标记：右下角视图筛选开启质心（COM）时，每个部件画出自己的蓝色质心菱形。关掉只留整车质心。");
        RotationSnap = Config.Bind("Editor", "Rotation snap (degrees)", 0f, "游戏旋转捕捉开启时，以本值（度）作为转动步进，代替游戏自带的（7.5 可作出 48 边圆，5 作出 72 边）。填 0 保持游戏步进。");
        MirrorMerge = Config.Bind("Editor", "Mirror merge", true, "编辑器镜像开启时，合并（M）会连同另一侧的镜像点一起合并。");
        BackupsKept = Config.Bind("Backups", "Backups kept", 50, "保留多少份设计备份（BepInEx\\SprocketToolsBackups，每次修改一份）；超出时先删最旧的。填 0 表示全部保留。");
        ShortcutsPosition = Config.Bind("Panels", "Shortcuts box position", "", "快捷键框被拖到的位置（左上角，单位像素），留空表示跟在部件面板旁边。");
        Keybinds.Load(Config);
        ModOptions.Register();
        // The F10 photo card (War Thunder profile view). Sections and keys stay English (they live in the cfg file);
        // the Card prefix keeps these apart from the drawing sheet's own entries.
        CardSideOffset = Config.Bind("View", "Side offset degrees", 60,
            "机位的侧偏角度，从正侧视起算（0 = 正侧视，90 = 正对车头）。对着参考图量出来定的是 60。");
        CardElevation = Config.Bind("View", "Elevation degrees", 0,
            "俯角，度数。正数=从上往下看，0=平视，负数=轻微仰视。对着参考图量出来在 0 到 -4 之间；" +
            "画面的高低感用下面那项视线高度调，别动俯角。");
        CardAimHeight = Config.Bind("View", "Aim height percent", 40,
            "视线对准车高的百分之几（0=地面、50=正中、100=车顶），相机离地高度跟着这个点走。" +
            "对准正中会把相机抬到车体上沿之上、读着像俯拍；参考图那种平视感在 40 上下，可以自己扫这个值。");
        CardLeftSide = Config.Bind("View", "Camera on the left", false,
            "相机放到车的左侧（画面里车头朝左）。车左右不对称时只能拍到一侧，默认拍右侧。");
        CardFieldOfView = Config.Bind("View", "Field of view", 20f, "竖直视场角，度。参考图量出来是 20。");
        CardFraming = Config.Bind("View", "Framing", 0.92f, "车的投影占画面多满（0 到 1）。0.92 表示留 8% 的边。");
        CardWidth = Config.Bind("Picture", "Width pixels", 4096, "出图宽度（像素）。资料卡的画布是 2:1，不受屏幕分辨率限制。4096 下一张约 90 MB 显存/内存峰值，连续出图时更吃内存。");
        CardHeight = Config.Bind("Picture", "Height pixels", 2048, "出图高度（像素）。");
        CardWhiteBackground = Config.Bind("Picture", "White background", true,
            "白底（开）还是透明底（关）。两种都要先把车后面的品红抠掉，抠不干净会在边上留一丝粉。");
        CardFlatLighting = Config.Bind("Picture", "Flat lighting", false,
            "平光：开=关掉所有投影并在车周围铺 14 盏补光（每个面一样亮，像泥模）；关=照游戏自己的太阳和阴影拍（有明暗、有投影）。资料卡要的是关。");
        CardFullbrightPercent = Config.Bind("Picture", "Fullbright brightness", 25f,
            "平光（上面那项开着才用）铺的 14 盏补光灯各自的亮度，按日照度的百分比算。");
        CardLightingRecipe = Config.Bind("Picture", "Lighting recipe", 5,
            "按 F10 正常出图时用哪组打光：0=不加（就用游戏自己的太阳和阴影，等于以前那张）、" +
            "1 SSGI、2 天反、3 全开（镜面反射/SSGI/天空反射/贴花全留）、4 天光补、5 轮位补（轮拱两盏补光，默认）、" +
            "6 提亮（曝光 +0.7EV）、7 冷调（4500K）。觉得黑了就试 5 或 6，想原样就填 0。");
        // 弹种预设（弹药架面板的「弹种」小节）。节名与键名保持英文：它们写在 cfg 文件里。
        // 注意方向：这些是**穿深倍率**（>1 = 更强）。游戏那根"穿甲常数"是**分母**，越小越强，
        // 所以落成常数时是除下去（原版可自证：60mm=2000 / 80mm=1900 / 90mm=1800 / 122mm=1800）。
        AmmoApFactor = Config.Bind("Ammo type", "AP factor", 1f,
            "AP 的穿深倍率（以同口径槽最强的常数为基准）。1 = 照抄原版强度。");
        AmmoApheFactor = Config.Bind("Ammo type", "APHE factor", 0.95f,
            "APHE 的穿深倍率（略低于 AP：装药占了弹体质量，穿深略逊）。0.95 = 现实标定。");
        AmmoApcbcFactor = Config.Bind("Ammo type", "APCBC factor", 1.05f,
            "APCBC 的穿深倍率。");
        AmmoApcrFactor = Config.Bind("Ammo type", "APCR factor", 1.55f,
            "APCR 的穿深倍率（垂直穿深高）。");
        AmmoHeatFactor = Config.Bind("Ammo type", "HEAT factor", 2f,
            "HEAT 的穿深倍率。HEAT 不带装药块（带就必然起爆），只做穿深。");
        // 面板写的是**官方原值**（全库唯一取值 1000）；实际威力由下面 Ammo ballistics 一节的运行时倍率统一缩放，
        // 这样**已经建好的弹**也能一起改，不必回去逐个重套弹种。
        AmmoAphePower = Config.Bind("Ammo type", "APHE explosive power", 1000f,
            "APHE 装药强度（写进设计的值，官方唯一取值 1000）。实际威力看 Ammo ballistics 的 APHE charge factor。");
        AmmoHePower = Config.Bind("Ammo type", "HE explosive power", 1000f,
            "HE 装药强度（写进设计的值，官方唯一取值 1000）。实际威力看 Ammo ballistics 的 HE charge factor。");
        AmmoFuzeDelay = Config.Bind("Ammo type", "Fuze delay", 0f,
            "带装药弹（APHE/HE）的引信延迟（官方全 0）。单位未实测，要试就从小数往上加。");
        // #108 的弹种对抗层（节名与键名保持英文：它们写在 cfg 文件里）。改的是战斗里的运行时对象，不写进设计 json。
        BallisticsEnable = Config.Bind("Ammo ballistics", "Enable ammo type ballistics", true,
            "按弹种改战斗数据（阻力/装药/穿甲常数），只动我们在弹药架面板标过弹种的弹。关掉＝与原版逐字节一致。");
        BallisticsArmourStop = Config.Bind("Ammo ballistics", "Armour class stops rounds", true,
            "复合/爆反按弹种挡弹：命中复合或爆反时，按该弹种的 vsComposite / vsEra 倍率裁定（HEAT、HE 会被挡下，动能弹只是变弱）。爆反一次性。");
        SpacedGap = Config.Bind("Ammo ballistics", "Spaced armour gap", 0.35f,
            "间隙装甲判定距离（米）：着弹点沿弹道往后这个距离内若还有另一块装甲板，就算间隙结构。0 = 不判间隙。");
        SpacedPenalty = Config.Bind("Ammo ballistics", "Spaced armour penalty", 0.5f,
            "间隙结构对**化学能弹**（HEAT/HE）的穿深惩罚倍率。报告只说\"会被削弱\"没给数，0.5 是先给的起点；1 = 不惩罚。");
        EraTrigger = Config.Bind("Ammo ballistics", "ERA trigger", "AnyHit",
            "爆反什么时候起爆（消耗这块板 + 放特效 + 按下面的开关让它消失）。" +
            "AnyHit = 任何弹**撞上就炸**，不管这发穿没穿、挡没挡（Miss 不算，擦弹跳飞也算）。" +
            "OnlyWhenStopped = 旧口径：只有这一发真被这块爆反挡下才炸（想对照物理上\"长杆弹不一定触发起爆盒\"就切这儿）。");
        EraDisappears = Config.Bind("Ammo ballistics", "ERA disappears when hit", true,
            "爆反起爆后**炸掉消失**（纯视觉）：只关掉这块板自己的渲染器，不写 HealthFraction，" +
            "所以不进部件损毁、不沾修理费与结算；蓝图与存档零改动 ⇒ 回设计界面这块板照常存在。起爆时机见 ERA trigger。");
        EraFxOn = Config.Bind("Ammo ballistics", "ERA explosion effect", true,
            "爆反起爆那一刻在弹孔放一发**自造爆炸特效**（会膨胀的发光球＋点光闪，不依赖任何美术资产）。" +
            "关掉＝只有板子消失，没有动画。尺寸由这块板自己的大小定，见 ERA explosion effect scale。");
        EraFxScale = Config.Bind("Ammo ballistics", "ERA explosion effect scale", 1f,
            "爆炸特效尺寸倍率：基准 = 这块爆反板渲染器包围盒的最大边长（米），乘这个数。" +
            "觉得爆炸偏小就往上加（1.5、2），盖住整车了就往下压（0.5）。0.05~8 之外一律夹住。");
        PostPenScale = Config.Bind("Ammo ballistics", "Post-penetration damage scale", 15f,
            "后效伤害倍率（报告最后一列）：**只在穿透之后**补一份伤害 = 该弹种的后效系数 × 这个数（走游戏自己的 IDamageApplier.HealthDelta）。0 = 关掉。不穿透时一律不加伤害。" +
            "注意：官方本来就会逐破片对部件结算一份伤害（ApplyFragmentHits→HealthDelta），这份是叠加在官方之上的额外一份；" +
            "总后效 = 官方逐片 + 本项，15 是旧假设下的数，知道双份后应往下压。");
        PostPenMode = Config.Bind("Ammo ballistics", "Post-penetration mode", "Stack",
            new ConfigDescription("后效叠加模式：Stack=在官方逐件伤害上叠加我们这份（默认，当前行为）；Off=完全不加我们这份；" +
                "Replace=想只走我们这份、抑制官方逐件伤害（⚠️离线未实现：抑制官方需挂 ApplyFragmentHits prefix，属判死的 AV 雷区，选了会退化为 Stack 并打警告）。",
                new AcceptableValueList<string>("Stack", "Replace", "Off")));
        ApcbcNoRicochet = Config.Bind("Ammo ballistics", "APCBC no ricochet", true,
            "APCBC 被帽弹免跳弹：即使按入射角+转正算出\"该跳\"（≥跳弹角），被帽弹在大角度下也不跳弹 —— 不置 Deflected，" +
                "反而清掉游戏自己判的跳弹位，让它穿透。关掉则按跳弹角正常跳弹。");
        HeatStaticPen = Config.Bind("Ammo ballistics", "HEAT static penetration", true,
            "⚠️实验性功能，未实机验证：HEAT 静破甲。HEAT 射流穿深不应随弹丸着速/射程衰减，但官方公式是速度相关的。" +
                "开启后在命中后贴里用固定射流速度（见 HEAT jet speed）重算穿深、与官方采样的有效厚度比较，覆盖穿透判定。" +
                "高风险：覆盖结果后官方逐件伤害是否仍走原路径未验证。");
        HeatJetSpeed = Config.Bind("Ammo ballistics", "HEAT jet speed", 900f,
            "HEAT 静破甲用的固定射流等效速度（m/s）。要对着零距离 HEAT 穿深标定：使重算穿深≈官方零距离穿深。仅 HEAT static penetration 开着时生效。");
        TrackSegments = Config.Bind("Ammo ballistics", "Track segment damage", true,
            "履带分段毁伤：每条履带按长度切成前/中/后三段各自记伤害，打哪段伤哪段；**三段全废才真的断整条**，中途只压血量。修的是「打前面也断后面」那个手感。");
        TrackSegmentDamage = Config.Bind("Ammo ballistics", "Track segment damage per hit", 0.5f,
            "每一发穿透命中给该履带段加多少伤害（1 = 一段报废）。没穿透的命中只算 30%。三段全废 ⇒ 置 Broken。");
        BallisticsApcrDrag = Config.Bind("Ammo ballistics", "APCR drag factor", 1f,
            "APCR 阻力倍率（做出随距离掉穿深）。1 = 不动，数值要实扫。");
        BallisticsHeatDrag = Config.Bind("Ammo ballistics", "HEAT drag factor", 1f,
            "HEAT 阻力倍率。1 = 不动，数值要实扫。");
        BallisticsApheCharge = Config.Bind("Ammo ballistics", "APHE charge factor", 0.01f,
            "APHE 装药倍率。官方 1000 实测会「不穿透也把成员炸死」；用户实机逐步定的数：0.05（50）→ 0.02（20）→ **0.01（实际 10）**。");
        BallisticsHeCharge = Config.Bind("Ammo ballistics", "HE charge factor", 0.05f,
            "HE 装药倍率。官方 1000 实测过大；用户实机定：0.3（300）→ 0.1（100）→ **0.05（实际 50）**。");
        // #105 的正式功能：七条自造装甲材质接进游戏自己的科技表，装甲板面板里给一个「装甲材质」下拉。
        ArmourMaterials = Config.Bind("Armour material", "Enable custom materials", true,
            "自造装甲材质：把内嵌的七条材质并进游戏科技表，装甲板面板多出「装甲材质」小节。倍率以 RHA = 1.0。关掉只剩原版四条。");
        GlassTransparent = Config.Bind("Armour material", "Glass looks transparent", true,
            "观察窗玻璃的外观改造总开关（改哪条路看下面 `Glass look mode`）。只动选了这条材质的板自己的材质副本，不影响别的板。");
        GlassLookMode = Config.Bind("Armour material", "Glass look mode", "gray",
            "观察窗玻璃改成什么样：`gray`＝涂成玻璃灰（默认，实机定：透明那条路走不通，退而求其次）；" +
            "`transparent`＝换成 HDRP/Lit 并开透明（2026-10-06 12:09 实跑过：材质确实换成功、日志报 3/3 个渲染器，画面仍然不透明 " +
            "⇒ 本游戏 build 的 HDRP/Lit 没带透明变体，shader 变体被裁了，留着这个值只为以后再试）；" +
            "`off`＝完全不改外观。" +
            "为什么属性路线（只把 _SurfaceType 写成 1）注定无效：车板用的是 Shader Graphs/VehicleLit，" +
            "Surface Type 是**建图时的编译选项**，打包出去只编译了不透明 pass，运行时无 pass 可切；" +
            "而且实测这块板的 `_BaseColour` 是 RGBA(0,0,0,0) 却照画不误 ⇒ 那个属性多半没进图（真进了整车会是黑的），改它也没用。");
        // #106 的正式功能：按 L 开 / 关车灯。
        PhotoResolution = Config.Bind("Photo", "Resolution", "屏幕", "F8 照片尺寸：屏幕（原样）、2K、4K、6K、8K。");
        PhotoMethod = Config.Bind("Photo", "Capture method", "渲染", "非屏幕尺寸的拍法：渲染（按高分辨率重画，线条最准但需要显卡支持）、放大（放大屏幕截图，最省显存）。");
        LightSwitch = Config.Bind("Vehicle lights", "L toggles lights", true,
            "按 L 开 / 关车灯：直接驱动头灯/尾灯部件模型底下已有的光源（头灯白色聚光、尾灯红色点光）。");
        // #106 的车灯探针（`Probe vehicle lights` / `Probe lights power test`）**已退役**：
        // 车灯做完了（见 `VehicleLightSwitch.cs`），而 `TOOL_LIGHT module` 每场要打 91 行纯噪声。
        // 源码归档在 `_归档\退役源码-20261004\VehicleLightProbe.cs`，要用再拿回来。
        AddComponent<DesignEditor>();
        long setupMs = loadWatch.ElapsedMilliseconds;
        var harmony = new Harmony("local.sprocket.tools");
        gameHooks = harmony; // 推迟的那批挂点由 AttachDesignerHooks 接手
        // 传统弹种/运行时换弹已迁到 ArmorDawn（AdAmmoType/AdAmmoLoad），工具包不再挂这两条。
        // 垂稳/双稳：手动挂瞄准接缝（不能进 features[]，否则被 AtBoot 逻辑推迟到设计器、战斗里失效）。
        Stabilizer.Init(Config, harmony);
        CannonPool.Init(Config);
        PenetrationLimits.Init(Config);
        // 第二项曾是纯探针小节的「要不要往游戏身上接挂点」判据（开关全关就一条都不挂）。探针全部退场后这条死路一起删了。
        var features = new Type[]
        {
            typeof(InspectorSection),
            typeof(ArmorMaterials), typeof(GlassLook), // 类名和同名字段撞了，写全命名空间
            typeof(VehicleLightSwitch),
            typeof(MassMarkers), typeof(TurretDriveRepair), typeof(TurretMotorLifecycle), typeof(ShapeTools),
            typeof(RestoreSection), typeof(HoleQuality), typeof(MeshTools), typeof(MergeFaces),
            typeof(Hotkeys), typeof(TurretCopy), typeof(ExplodedView), typeof(GunLength),
            typeof(GearSpeeds), typeof(StabilizerPanel), typeof(PartPaint), typeof(PartPaint.SavedPaintLoad),
            typeof(ImageAddresses), typeof(DrawingSheet.NoHover), typeof(DrawingSettings), typeof(RotationSnap),
            typeof(MirrorMerge), typeof(PanelFit), typeof(ObjTransfer),
            typeof(EraFx), typeof(CannonPool), typeof(PenetrationLimits), typeof(ModOptions),
        };
        var timings = new List<(string name, long ms)>();
        foreach (var feature in features)
        {
            var watch = Stopwatch.StartNew();
            // 一个挂点一个挂点地接：游戏更新改掉某个方法时，只有用到它的那一条挂点失效，同一个小节的其他挂点照旧（见 Mod Options > 兼容性）。
            Hooks.Attach(harmony, feature);
            watch.Stop();
            timings.Add((feature.Name, watch.ElapsedMilliseconds));
        }
        // 发动机面板的类在 0.2.56 换了程序集，编译期写死只认一个版本 ⇒ 挂点手工接在 EngineEditor 运行时找到的类上
        // （找不到就只记这两条失败，小节自禁用，见 Ui.Guard）。这两条目标只在设计器里被调到，照旧攒给首次进设计器。
        Hooks.AttachPostfix(harmony, nameof(GearSpeeds), EngineEditor.Type, "CombustionEngineComponentEditor", "OnComponentRebuilt",
            AccessTools.Method(typeof(GearSpeeds), nameof(GearSpeeds.EngineChanged)));
        Hooks.AttachPostfix(harmony, nameof(GearSpeeds), EngineEditor.Type, "CombustionEngineComponentEditor", "OnGUI",
            AccessTools.Method(typeof(GearSpeeds), nameof(GearSpeeds.InEngine)));
        var slowest = timings.OrderByDescending(t => t.ms).Take(5).Select(t => $"{t.name} {t.ms}ms");
        Log.LogDebug($"[timing] SprocketTools load: total {loadWatch.ElapsedMilliseconds}ms｜配置与组件 {setupMs}ms｜挂点 {loadWatch.ElapsedMilliseconds - setupMs}ms（{Hooks.Report.Count} 条，另 {Hooks.Pending} 条推迟到首次进设计器）｜最慢 {string.Join(", ", slowest)}");
        if (Hooks.Failures.Any()) Log.LogWarning($"TOOL_HOOKS {Hooks.Summary()}");
        else Log.LogInfo($"TOOL_HOOKS {Hooks.Summary()}");
        Log.LogInfo("SprocketTools 1.3.0 loaded（Ctrl+K 打开 Mod Options）");
    }

    /// 只在 Load 里用得上（推迟的那批挂点要在进设计器那帧才接，得留个 Harmony 实例）。
    static Harmony? gameHooks;

    /// #79 A：设计器一出现就把攒下的挂点一次接完。这一下约一秒多，落在进设计器的开场，而不是每次开机。
    internal static void AttachDesignerHooks()
    {
        if (gameHooks == null || Hooks.Pending == 0) return;
        var watch = Stopwatch.StartNew();
        int attached = Hooks.AttachDeferred(gameHooks);
        ModLog.LogDebug($"[timing] SprocketTools designer hooks: {attached} 条接上用了 {watch.ElapsedMilliseconds}ms（首次进设计器的一次性开销，之后不再有此停顿）");
        if (Hooks.Failures.Any()) ModLog.LogWarning($"TOOL_HOOKS {Hooks.Summary()}");
    }
}

internal static class Ui
{
    static UnityEngine.Font? cjk;
    static bool cjkTried;
    static readonly string[] CjkNames = { "Microsoft YaHei", "Microsoft JhengHei", "SimHei", "SimSun" };

    /// The few things drawn with IMGUI (the hotkeys box, the measurements, the status bar) write with Unity's built-in
    /// font, which has no Chinese glyphs. Point the styles at a Windows font that has them, before any of that is drawn.
    internal static void CjkFont()
    {
        if (cjkTried) return;
        cjkTried = true;
        foreach (var name in CjkNames)
        {
            cjk = DynamicFont(name);
            if (cjk == null) continue;
            Plugin.ModLog.LogInfo($"TOOL_FONT IMGUI using {name}");
            break;
        }
        if (cjk == null) { Plugin.ModLog.LogWarning("TOOL_FONT no Windows font with Chinese glyphs found; IMGUI text stays as the built-in font"); return; }
        var skin = GUI.skin;
        skin.font = cjk;
        skin.label.font = skin.box.font = skin.button.font = skin.window.font = cjk;
    }

    /// Font.CreateDynamicFontFromOSFont has no IL2CPP binding in this game (its call resolves to a Font constructor
    /// the interop assembly doesn't carry), so the internal call it would have made is made directly.
    static UnityEngine.Font? DynamicFont(string name)
    {
        try
        {
            var type = typeof(UnityEngine.Font);
            var font = type.GetConstructor(Type.EmptyTypes)?.Invoke(null) as UnityEngine.Font;
            var create = type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name == "Internal_CreateDynamicFont" && m.GetParameters().Length == 3);
            if (font == null || create == null) return null;
            create.Invoke(null, new object[] { font, new Il2CppStringArray(new[] { name }), 16 });
            return font.Pointer == IntPtr.Zero ? null : font;
        }
        catch (Exception ex) { Plugin.ModLog.LogInfo($"TOOL_FONT {name}: {ex.Message}"); return null; }
    }

    /// 游戏的原生部件编辑器会共用同一份 inspector 布局（例如炮塔环和炮塔架）。我们的后挂要么在同一帧里
    /// 被下一个原生编辑器接着画：最后一个下拉/折叠必须替游戏关掉，否则它读到的是没闭合的状态。
    internal static void Inspector(string feature, IGUILayout layout, Action draw) => Guard(feature, () =>
    {
        try { draw(); }
        finally { layout.EndAllDropdowns(); }
    });

    /// An exception thrown back into the game's inspector drawing could take the game down; log it instead.
    internal static void Guard(string feature, Action draw)
    {
        if (missing.Contains(feature)) return;
        try { draw(); }
        catch (Exception ex) when (ex is FileNotFoundException or TypeLoadException or MissingMemberException)
        {
            // 这个版本的游戏没有这段代码要的东西（DLL 是给另一个版本编的）：它永远好不了，就别每帧试。
            // 上游 1.8.6/1.8.7 在 0.2.55.5 上「速度与加速」每帧抛 FileNotFound，面板还被拖累。
            missing.Add(feature);
            Plugin.ModLog.LogWarning($"{feature} is off: this Sprocket version doesn't have what it needs ({ex.GetType().Name}: {ex.Message})");
        }
        catch (Exception ex)
        {
            // The same error again (a GUI drawn every frame) is counted, not logged again: no wall of red.
            string text = ex.ToString();
            if (lastErrors.TryGetValue(feature, out var last) && last.Text == text)
            {
                lastErrors[feature] = (text, ++last.Count);
                // 计数行也带上异常名：完整那条早就滚出日志了，截图里只有这一行能看出是什么错。
                if (last.Count % 1000 == 0)
                    Plugin.ModLog.LogError($"{feature}: the same error again, {last.Count} times so far ({ex.GetType().Name}: {ex.Message}; full error at the first time in LogOutput.log)");
                return;
            }
            lastErrors[feature] = (text, 1);
            Plugin.ModLog.LogError($"{feature}: {ex}");
        }
    }

    static readonly Dictionary<string, (string Text, int Count)> lastErrors = new();
    static readonly HashSet<string> missing = new();

    internal static UnityAction Callback(Action action) => DelegateSupport.ConvertDelegate<UnityAction>(action)!;

    internal static Il2CppSystem.Action<float> FloatCallback(Action<float> action) =>
        DelegateSupport.ConvertDelegate<Il2CppSystem.Action<float>>(action)!;

    internal static Il2CppSystem.Action<bool> BoolCallback(Action<bool> action) =>
        DelegateSupport.ConvertDelegate<Il2CppSystem.Action<bool>>(action)!;

    internal static UnityAction<int> IntCallback(Action<int> action) =>
        DelegateSupport.ConvertDelegate<UnityAction<int>>(action)!;

    static HashSet<string>? closed;

    /// Starts a top-level section the player can fold away (the game's own dropdown). It stays folded, across restarts
    /// too (saved in the plugin's config file).
    /// 折叠状态存在配置里，用的键就是这个标题串。按键现在可以改了，标题里就不该再写死「（F9）」；
    /// 改过名的那一版用 legacy 传进来一起认，改名不会把玩家已有的折叠状态弄丢。
    internal static void Section(IGUILayout layout, string title, string? legacy = null)
    {
        closed ??= (Plugin.Folded?.Value ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        layout.EndAllDropdowns();
        bool folded = closed.Contains(title) || (legacy != null && closed.Contains(legacy));
        layout.BeginDropdown(title, !folded, BoolCallback(open =>
        {
            bool changed = open ? closed.Remove(title) : !closed.Contains(title) && closed.Add(title);
            if (legacy != null && closed.Remove(legacy)) changed = true;
            if (!changed) return;
            if (Plugin.Folded != null) Plugin.Folded.Value = string.Join("|", closed);
        }));
    }
}
