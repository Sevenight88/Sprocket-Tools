namespace SprocketTools;

/// Standing to top speed on flat ground at full throttle, the way the game drives, in steps of up to 1/50 s (no game
/// code: tested offline). Each rule is read from the game's own drivetrain jobs:
/// - Engine (EngineUpdateJob): its torque curve, faded out as 1 - x^4 over the last 50 rpm below the rev limit, with
///   the engine's friction braking there too, so it settles just under the limit.
/// - Gearbox (TransmissionUpdateJob.GetIdealGear, used by both drive layouts): starts in first and shifts up once the
///   engine passes the upshift rpm, if the next gear, after the speed lost while changing, keeps the engine above 1.2 x
///   idle and still pushes harder than the resistance. Changing gear, drive fades out over the disengage time and back
///   in over the engage time.
/// - Tracks (PowertrainUpdateJob): they push on the ground no harder than their grip (friction x weight).
internal static class Acceleration
{
    internal sealed record Drivetrain(Func<float, float> TorqueAt, float[] Ratios, float FinalDrive, float Radius, float Mass,
        float EngineInertia, float SprocketInertia, float IdleRpm, float UpshiftRpm, float DisengageTime, float EngageTime, float Grip);

    const float LimiterRpm = 50; // the game's RevLimiterSpeedBuffer (5.236 rad/s)

    /// The engine's torque (N·m) at a rev count, with the game's rev limiter: throttle x (1 - x^4) over the last 50 rpm
    /// below `revLimit`, minus 5 x friction x (ω - start) x x^2 (ω in rad/s). Past the limit it only brakes.
    internal static Func<float, float> RevLimited(Func<float, float> torque, float revLimit, float friction) => rpm =>
    {
        float start = revLimit - LimiterRpm;
        if (rpm <= start) return torque(rpm);
        float x = (rpm - start) / LimiterRpm, x2 = x * x;
        return torque(rpm) * (1 - x2 * x2) - (rpm - start) * MathF.PI / 30 * friction * 5 * x2;
    };

    /// Rolling resistance and the tracks' own losses (N, all tracks together) at `v` m/s, as the game's track job counts
    /// them: rolling = c0 x weight x (1 + cv x v²) x tanh(2v); at each sprocket (ω = v / radius) viscous drag x ω plus
    /// bending resistance x tanh(0.1 ω), as a push on the ground.
    internal static float TrackLosses(float v, float weight, float c0, float cv, float viscous, float bending, int tracks, float radius)
    {
        float rolling = c0 * weight * (1 + cv * v * v) * MathF.Tanh(2 * MathF.Abs(v));
        float w = v / radius;
        return rolling + tracks * (viscous * w + bending * MathF.Tanh(0.1f * w)) / radius;
    }

    /// `resist` the rolling resistance, track losses and drag (N) at a speed (m/s). Stops at `top` (the tracks' speed
    /// limit), when nothing pushes harder than the resistance, or after 600 s.
    internal static (float Seconds, float Reached, int Shifts) Run(Drivetrain d, Func<float, float> resist, float top)
    {
        float v = 0, t = 0;
        int gear = 0, shifts = 0;
        float Gearing(int i) => d.Ratios[i] * d.FinalDrive / d.Radius;          // engine rad/s per m/s
        float Rpm(int i, float speed) => speed * Gearing(i) * 30 / MathF.PI;
        // The push of gear `i` at the ground (engaged by `share`), no more than the tracks can grip.
        float Push(int i, float speed, float share) =>
            Math.Min(d.TorqueAt(Math.Max(Rpm(i, speed), d.IdleRpm)) * Gearing(i) * share, d.Grip);
        float Accel(int i, float share) =>
            (Push(i, v, share) - resist(v)) / (d.Mass + d.EngineInertia * Gearing(i) * Gearing(i) * share + d.SprocketInertia / (d.Radius * d.Radius));
        // Up to 1/50 s, but short enough that the engine changes by no more than 5 rpm: in a low gear it would otherwise
        // jump right over the 50 rpm between the upshift point and the rev limit.
        float Step(int i, float share, float most)
        {
            float a = Accel(i, share);
            float dt = Math.Min(Math.Max(5 / Math.Max(1e-6f, MathF.Abs(a) * Gearing(i) * 30 / MathF.PI), 1e-4f), Math.Min(0.02f, most));
            v = Math.Max(0, v + a * dt);
            t += dt;
            return dt;
        }

        while (v < top * 0.995f && t < 600)
        {
            if (gear < d.Ratios.Length - 1 && Rpm(gear, v) > d.UpshiftRpm && Push(gear, v, 1) > resist(v))
            {
                // The game's check: after the change, is the next gear still worth it? It guesses the speed lost as the
                // resistance torque at the gearbox (F r / final drive) over mass x radius², which is the real coasting
                // loss / final drive², so it barely expects any (with the real loss, low first gears never shifted).
                float after = Math.Max(0, v - resist(v) / d.Mass * (d.DisengageTime + d.EngageTime) / (d.FinalDrive * d.FinalDrive));
                if (Rpm(gear + 1, after) > 1.2f * d.IdleRpm && Push(gear + 1, after, 1) > resist(after))
                    for (float s = 0; s < d.DisengageTime;)
                    {
                        float dt = Step(gear, 1 - s / d.DisengageTime, d.DisengageTime - s);
                        if (dt <= 0 || s + dt <= s) break;
                        s += dt;
                    }
                    gear++;
                    shifts++;
                    for (float s = 0; s < d.EngageTime;)
                    {
                        float dt = Step(gear, s / d.EngageTime, d.EngageTime - s);
                        if (dt <= 0 || s + dt <= s) break;
                        s += dt;
                    }
                    continue;
            }
            if (Accel(gear, 1) <= 1e-3f) break;                                  // it can't go any faster
            Step(gear, 1, 0.02f);
        }
        return (t, v, shifts);
    }
}
