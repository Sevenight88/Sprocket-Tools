namespace SprocketTools;

/// 变速箱「平滑」按钮的挡位间距：一挡和最高挡不动，中间各挡放在让本车最快到最高速 90% 的位置。
/// 游戏自带 Smooth 把中间各挡按等比排；这里试的间距从等比到向高挡收窄。纯数学，不碰游戏类型。
public static class GearSpacing
{
    /// 1 是游戏自带的等比；大于 1 时越往高挡间距越小，小于 1 时越大。
    static readonly float[] Exponents = { 0.8f, 1, 1.25f, 1.5f, 1.75f, 2, 2.5f, 3 };

    /// 从 first 到 top 共 count 挡（至少 3 挡），按 1 - (1 - t)^p（t = 0..1）分配对数速比。
    public static float[] Spread(float first, float top, int count, float p)
    {
        var gears = new float[count];
        for (int i = 0; i < count; i++)
            gears[i] = first * MathF.Pow(top / first, 1 - MathF.Pow(1 - i / (float)(count - 1), p));
        gears[^1] = top;
        return gears;
    }

    /// 游戏最终使用的传动比（TransmissionBlueprint.GetRatios）：有副变速箱时每挡还要乘副箱速比，
    /// 再全部从低到高排序。
    public static float[] Resulting(float[] gears, float auxiliary) => MathF.Abs(auxiliary - 1) < 1e-5f ? gears.ToArray()
        : gears.SelectMany(g => new[] { g, g * auxiliary }).OrderByDescending(g => g).ToArray();

    /// 找到的间距（Exponent 1 即游戏等比）与从静止到 Target（m/s）的时间，和等比的时间作对照。
    public readonly record struct Choice(float[] Gears, float Exponent, float Seconds, float EvenSeconds, float Target);

    /// 用各间距替换本车挡位后，选最快达到最高速 90% 的一组；除非另一组至少快 1%，否则用等比。
    /// 等比状态下车辆根本动不了则返回 null。
    public static Choice? Fastest(DriveSim.Vehicle vehicle, float first, float top, int count, float auxiliary)
    {
        DriveSim.Vehicle With(float p) => vehicle with { Gears = Resulting(Spread(first, top, count, p), auxiliary) };
        var even = With(1);
        float target = 0.9f * even.Gears.Max(r => DriveSim.TopSpeed(even, r).Speed);
        if (target <= 0) return null;
        var seconds = new float[Exponents.Length];
        Parallel.For(0, Exponents.Length, k => seconds[k] = TimeTo(With(Exponents[k]), target));
        int equal = Array.IndexOf(Exponents, 1f), best = Array.IndexOf(seconds, seconds.Min());
        if (float.IsPositiveInfinity(seconds[equal])) return null;
        if (seconds[best] > 0.99f * seconds[equal]) best = equal;
        return new(Spread(first, top, count, Exponents[best]), Exponents[best], seconds[best], seconds[equal], target);
    }

    /// 全油门从静止到 speed（m/s）的秒数；到不了则 +∞。
    public static float TimeTo(DriveSim.Vehicle vehicle, float speed)
    {
        var run = DriveSim.Accelerate(vehicle);
        return run.Count > 0 && run[^1].Speed >= speed ? run[^1].Time : float.PositiveInfinity;
    }
}
