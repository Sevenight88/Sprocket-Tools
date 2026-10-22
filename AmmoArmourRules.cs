using Sprocket.DamageModelling;
using UnityEngine;

namespace SprocketTools;

/// 「甲弹对抗」自己的参数表与裁定 —— 弹六型 × 甲七类，二战~冷战现实口径。
///
/// **单一事实来源**：本表同时供
///   - 命中现场（`Ballistics.AfterHitReadTarget`）算转正/跳弹/挡弹/后效；
///   - 弹药架面板（`AmmoType`）与装甲板面板（`ArmorMaterials`）显示；
///   - 开机自检（`Plugin.Load`）打印「倍率→常数」映射。
/// 面板与战斗同源：改这里，三处一起变，不另抄一份数。
///
/// 逐维落地位置（改数前先看这一维到底生不生效）：
/// - **穿深倍率** → 建弹时写 `PenetratorConstant`（`AmmoType.PenetratorFactor`，cfg `Ammo type` 节）。本表不存穿深倍率，
///   因为它走 cfg、且常数是分母（穿深×factor ⇐ 常数÷factor^(1/1.43)，见 `Ballistics.Constant`）。
/// - **转正 Norm / 跳弹 Ricochet / 后效 PostPen** → 命中现场读本表（`ShouldRicochet` / `PostPen`）。
/// - **距离衰减 Falloff** → 建弹时写 `dragConstant`（cfg `BallisticsApcrDrag` 等）；本表 Falloff 是设计推荐值备查。
/// - **弹×甲交互（Vs*）** → 命中现场按打中的那块板的装甲类查 `Vs(ammo, armourClass)`，≤`StopThreshold` 则挡下。
/// - **装甲本身的密度/rhaFactor** → 材质 json（`Materials\*.json`），游戏伤害模型自己读；本表只做面板摘要。
internal static class AmmoArmourRules
{
    internal enum PenType { Kinetic, Chemical }

    // ============================================================
    // 弹：六型
    // ============================================================
    //   Norm      转正角(°)。被帽弹(APCBC)大，APCR 为 0（硬芯不转正，大角度吃亏）。
    //   Ricochet  跳弹临界角(°)：入射角转正后 ≥ 此角 ⇒ 跳弹。HE/HEAT 接近 180（基本不跳）。
    //   PostPen   穿透后后效系数（×cfg PostPenScale）。APHE 最强（核心价值），APCR 最弱。
    //   Falloff   距离衰减阻力因子推荐值（1=不额外加阻；越小衰减越快，APCR 最快）。
    //   VsFha / VsComposite / VsEra / VsSpaced / VsSheetMetal
    //             该弹对各类装甲的穿深系数（1.0=对 RHA 满穿；<1 被削弱，>1 占便宜）。rha/cast 恒为 1.0（基准）。
    //   NoRicochet 被帽(APCBC)大角度免跳弹标记。
    internal sealed record Ammo(string Code, PenType Pen, float Norm, float Ricochet,
                                float PostPen, float Falloff,
                                float VsFha, float VsComposite, float VsEra, float VsSpaced, float VsSheetMetal,
                                bool NoRicochet = false);

    internal static readonly Ammo[] Ammos =
    {
        //                  code      类型      Norm Rico  PostPen Fall  FHA  Comp ERA  Spaced Sheet noRic
        new("AP",     PenType.Kinetic,  1f, 65f, 0.35f, 0.85f, 0.90f, 0.80f, 0.88f, 0.95f, 1.00f),
        new("APHE",   PenType.Kinetic,  2f, 65f, 1.00f, 1.00f, 0.90f, 0.85f, 0.88f, 0.95f, 1.00f),
        new("APCBC",  PenType.Kinetic,  5f, 70f, 0.65f, 0.95f, 0.85f, 0.85f, 0.88f, 0.95f, 1.00f, NoRicochet: true),
        new("APCR",   PenType.Kinetic,  0f, 60f, 0.25f, 0.70f, 1.15f, 0.75f, 0.85f, 0.95f, 1.00f),
        new("HEAT",   PenType.Chemical, 0f, 80f, 0.60f, 1.00f, 1.00f, 0.35f, 0.20f, 0.50f, 1.00f),
        new("HE",     PenType.Chemical, 0f, 180f,0.10f, 1.00f, 1.00f, 0.10f, 0.00f, 0.50f, 1.50f),
    };

    // ============================================================
    // 甲：七类（面板摘要；密度/rhaFactor 真值在 Materials\*.json）
    // ============================================================
    internal sealed record Armour(string Code, string Display, float RhaFactor, float Density, bool SingleUse, string Note);

    internal static readonly Armour[] Armours =
    {
        new("rha",       "轧制均质 RHA", 1.00f, 7850f, false, "基准"),
        new("cast",      "铸钢",         0.92f, 7350f, false, "略弱于 RHA"),
        new("fha",       "表面硬化 FHA", 1.10f, 7850f, false, "对早期 AP/APCBC 有优势，大口径 APCR 易崩裂"),
        new("composite", "复合装甲",     1.40f, 5600f, false, "抗化学能极强、对动能也有优势，密度低，战后"),
        new("era",       "爆反 ERA",     1.15f, 2600f, true,  "抗化学能极强、一代对动能弱，一次性"),
        new("spaced",    "间隙/格栅",    1.00f, 3200f, false, "提前引爆/削弱化学能，对动能几乎无效，轻"),
        new("sheetMetal","结构钢",       0.40f, 7850f, false, "极弱，HE/破片/外部件对其有效"),
    };

    internal static Ammo? AmmoFor(string? label) => label == null ? null : Ammos.FirstOrDefault(a => a.Code == label);
    internal static Armour ArmourFor(string cls) => Armours.FirstOrDefault(a => a.Code == cls) ?? Armours[0];

    /// **弹×甲交互倍率**：该弹打在这一装甲类上的穿深系数（1.0=对 RHA 满穿）。
    /// rha / cast / 未知 ⇒ 1.0（基准；cast 的 0.92 已由材质表 rhaFactor 处理，这里不二次惩罚）。
    internal static float Vs(Ammo a, string armourClass) => armourClass switch
    {
        "fha" => a.VsFha,
        "composite" => a.VsComposite,
        "era" => a.VsEra,
        "spaced" => a.VsSpaced,
        "sheetMetal" => a.VsSheetMetal,
        _ => 1f,
    };

    /// 材质 id → 装甲档（命中层与面板共用同一份，免得两处各写一份走岔）。
    /// 映射：爆反(explosiveReactive)=era；非爆反应变(nonExplosiveReactive/NERA)=spaced；
    /// composite=composite；faceHardened=fha；castHomogeneous=cast；sheetMetal=sheetMetal；其余≈rha。
    internal static string ClassOfTech(string? tech)
    {
        if (tech == null) return "rha";
        if (tech.IndexOf("Reactive", StringComparison.OrdinalIgnoreCase) >= 0)
            return tech.IndexOf("Explosive", StringComparison.OrdinalIgnoreCase) >= 0 ? "era" : "spaced";
        if (tech.IndexOf("composite", StringComparison.OrdinalIgnoreCase) >= 0) return "composite";
        if (tech.IndexOf("faceHardened", StringComparison.OrdinalIgnoreCase) >= 0) return "fha";
        if (tech.IndexOf("castHomogeneous", StringComparison.OrdinalIgnoreCase) >= 0) return "cast";
        if (tech.IndexOf("sheetMetal", StringComparison.OrdinalIgnoreCase) >= 0) return "sheetMetal";
        return "rha";
    }

    /// 一整车的材质清单 → 一个装甲档（取优先级最高的那个；板级判定认不准时的兜底）。
    /// 优先级：ERA > 复合 > 间隙 > FHA > 铸钢 > RHA。
    internal static string ClassOfTechs(IEnumerable<string> techs)
    {
        string best = "rha";
        foreach (var t in techs)
        {
            string c = ClassOfTech(t);
            if (c == "era") return "era";
            if (c == "composite") best = "composite";
            else if (c == "spaced" && best == "rha") best = "spaced";
            else if (c == "fha" && (best == "rha" || best == "cast")) best = "fha";
            else if (c == "cast" && best == "rha") best = "cast";
        }
        return best;
    }

    /// 面板用：一型弹的参数行。`factor` 传它的穿深倍率（来自 cfg，与写进战斗的那一个同源）。
    internal static string Describe(Ammo a, float factor) =>
        $"穿深×{factor:0.##}｜转正{a.Norm:0.#}°｜跳弹{a.Ricochet:0.#}°{(a.NoRicochet ? "（被帽免跳）" : "")}｜后效{a.PostPen:0.##}" +
        $"｜复合×{a.VsComposite:0.##}｜爆反×{a.VsEra:0.##}｜间隙×{a.VsSpaced:0.##}｜FHA×{a.VsFha:0.##}";

    /// 面板用：一种装甲的参数行。
    internal static string Describe(Armour a) =>
        $"对RHA×{a.RhaFactor:0.##}｜密度{a.Density:0}kg/m³{(a.SingleUse ? "｜一次性" : "")}｜{a.Note}";

    /// 入射角（度）。法线与弹道方向都不保证归一化，所以自己除模长；取 `|cos|` 的补角既是最稳的写法，
    /// 也绕开了"法线朝向是朝内还是朝外"这个我们没法定的事（实测两种朝向都出现过）。
    internal static float Obliquity(Vector3 normal, Vector3 direction)
    {
        float dot = normal.x * direction.x + normal.y * direction.y + normal.z * direction.z;
        float mn = Mathf.Sqrt(normal.x * normal.x + normal.y * normal.y + normal.z * normal.z);
        float md = Mathf.Sqrt(direction.x * direction.x + direction.y * direction.y + direction.z * direction.z);
        if (mn < 1e-5f || md < 1e-5f) return 0f;
        float cos = Mathf.Clamp(Mathf.Abs(dot) / (mn * md), 0f, 1f);
        return Mathf.Acos(cos) * Mathf.Rad2Deg;
    }

    /// 本弹种在这一入射角下**该不该跳弹**。转正先扣，再跟该型的跳弹角比。
    internal static bool ShouldRicochet(Ammo ammo, float obliquityDeg, out float effectiveDeg)
    {
        effectiveDeg = Mathf.Max(0f, obliquityDeg - ammo.Norm);
        return effectiveDeg >= ammo.Ricochet;
    }

    /// 把我们的跳弹判定合进游戏的 `__result`。
    /// 该跳 ⇒ 置成纯 `Deflected`（不穿）；不该跳但游戏判了 `Deflected` ⇒ 只清掉那一位，保留游戏自己的穿透结论。
    /// **不动穿透那一位** —— 厚度数据不在我们手上，改了就是无依据的猜测。
    internal static DamageModelHitResult ApplyRicochet(DamageModelHitResult game, bool shouldRicochet)
    {
        if (shouldRicochet) return DamageModelHitResult.Deflected;
        if (game.HasFlag(DamageModelHitResult.Deflected))
            return game & ~DamageModelHitResult.Deflected;
        return game;
    }
}
