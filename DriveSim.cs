namespace SprocketTools;

/// The game's tracked drivetrain, step for step (Sprocket 0.2.55.5, read from its jobs): engine (EngineUpdateJob), gearbox
/// (TransmissionUpdateJob), main clutch and split gear (TwinTransmission.PhysicsUpdateJob: 200 substeps a physics step, 5
/// coupling iterations each), each side's sprocket, belt tension and belt-ground traction (PowertrainUpdateJob), and the
/// hull on level ground. The driver (launch, gear changes, rev matching) does what the game's driver does at full
/// throttle. Checked against recorded drives (BepInEx\SprocketTools-drives): top speed within 0.1%, each gear's acceleration
/// within 3%. Pure maths, no game types.
public sealed class DriveSim
{
    /// Speeds in rad/s, torques in N·m. The torque curve is the game's (Torque.Template scaled by max speed and torque).
    public sealed record Engine(float MaxTorque, float Inertia, float RevLimit, float Friction, float OperatingSpeed, float IdleSpeed,
                                float[] CurveSpeed, float[] CurveTorque)
    {
        /// Every engine's torque curve: these points times (max speed, max torque) (TorqueCurve's static template).
        static readonly float[] TemplateSpeed = { 0, 0.05f, 0.1f, 0.2f, 0.4f, 0.6f, 0.8f, 1, 1.2f };
        static readonly float[] TemplateTorque = { 0, 0.05f, 0.3f, 0.65f, 0.85f, 0.95f, 1, 0.8f, 0 };

        public static Engine Of(float maxTorque, float maxSpeed, float inertia, float revLimit, float friction, float operatingSpeed, float idleSpeed) =>
            new(maxTorque, inertia, revLimit, friction, operatingSpeed, idleSpeed,
                TemplateSpeed.Select(x => x * maxSpeed).ToArray(), TemplateTorque.Select(y => y * maxTorque).ToArray());

        /// Torque at full throttle, under the rev limiter (its last 50 rpm fade the fuel out and brake).
        public float FullThrottle(float w)
        {
            float start = RevLimit - LimiterBand, fuel = 1, brake = 0;
            if (w > start)
            {
                float x = (w - start) / (RevLimit - start);
                fuel = 1 - x * x * x * x;
                brake = (w - start) * Friction * 5 * x * x;
            }
            return Sample(CurveSpeed, CurveTorque, w) * fuel - brake;
        }
    }

    /// One side's drive: the track's settings as TrackAssembly.CreateTrackController makes them.
    public sealed record Sprocket(float FinalDrive, float Bending, float Viscous, float Inertia, float Friction, float Radius);

    /// RollingResistance (c0) and RollingPerSpeed2 (cv): the hull's rolling resistance is c0 N (1 + cv v²); a share of it
    /// (RollingTransfer) also loads the belts. The clutch and gearbox settings are the twin transmission's.
    public sealed record Vehicle(Engine Engine, float[] Gears, float Disengage, float Engage, float ClutchStiffness, float ClutchCapacity,
                                 float UpshiftSpeed, Sprocket Sprocket, float Mass, float RollingResistance, float RollingPerSpeed2,
                                 float RollingTransfer = 0.2f, float FixedDeltaTime = 0.01f, float Gravity = 9.81f);

    /// The torque curve sampled the game's way: monotone cubic (Fritsch-Carlson with Brodlie weights), held flat outside.
    public static float Sample(float[] x, float[] y, float at)
    {
        int n = x.Length;
        if (at <= x[0]) return y[0];
        if (at >= x[n - 1]) return y[n - 1];
        int lo = 0, hi = n - 1;
        while (hi - lo > 1) { int mid = (lo + hi) >> 1; if (at >= x[mid]) lo = mid; else hi = mid; }
        int k = lo;
        float h = x[k + 1] - x[k], t = (at - x[k]) / h;
        float Slope(int i)
        {
            if (i == 0) return (y[1] - y[0]) / (x[1] - x[0]);
            if (i == n - 1) return (y[n - 1] - y[n - 2]) / (x[n - 1] - x[n - 2]);
            float h0 = x[i] - x[i - 1], h1 = x[i + 1] - x[i];
            float d0 = (y[i] - y[i - 1]) / h0, d1 = (y[i + 1] - y[i]) / h1;
            if (d0 * d1 <= 0) return 0;
            float w1 = 2 * h1 + h0, w2 = h1 + 2 * h0;
            return (w1 + w2) / (w1 / d0 + w2 / d1);
        }
        float m0 = Slope(k), m1 = Slope(k + 1), t2 = t * t, t3 = t2 * t;
        return (2 * t3 - 3 * t2 + 1) * y[k] + (t3 - 2 * t2 + t) * h * m0 + (-2 * t3 + 3 * t2) * y[k + 1] + (t3 - t2) * h * m1;
    }

    const float LimiterBand = 5.235988f; // 50 rpm in rad/s: the rev limiter's and idle governor's band
    const float MinimumSpeed = 5.235988f;
    const int Substeps = 200, Iterations = 5;

    // ---------- top speed: where the drive settles in a gear ----------

    public readonly record struct Settled(float Speed, float EngineSpeed, float Slip);

    /// Where the vehicle settles in one gear at full throttle on level ground: the hull's rolling resistance met by
    /// belt-ground traction (its slip), the belts' share of rolling resistance, belt bending and sprocket drag, the split
    /// gear's friction and the clutch's slip, against the engine under its rev limiter. Solved directly (bisection on the
    /// speed), the same balance the step-by-step drive comes to. Speed 0: this gear can't move the vehicle.
    public static Settled TopSpeed(Vehicle v, float ratio)
    {
        var p = v.Sprocket;
        float normal = v.Mass * v.Gravity / 2;
        // Engine speed needed for a hull speed, and the torque that takes; NaN where the belts can't grip enough.
        (float W, float Torque) Need(float speed)
        {
            float rolling = v.RollingResistance * normal * (1 + v.RollingPerSpeed2 * speed * speed) * MathF.Tanh(2 * speed);
            float grip = rolling / (p.Friction * normal);
            if (grip >= 1) return (float.NaN, float.NaN);
            float measure = MathF.Atanh(grip) / 2;
            float blend = Math.Clamp(2 * speed, 0, 1);
            float slip = measure / ((1 / (speed + 0.5f) - 2) * blend + 2);
            float sprocket = (speed + slip) / p.Radius;
            float atSprocket = p.Viscous * sprocket + p.Bending * MathF.Tanh(0.1f * sprocket) + (1 + v.RollingTransfer) * rolling * p.Radius;
            float split = sprocket * p.FinalDrive * ratio;
            float clutch = 0.1f * split + 2 * atSprocket / (p.FinalDrive * ratio);
            return (split + clutch / v.ClutchStiffness, clutch);
        }
        float Spare(float speed)
        {
            var (w, torque) = Need(speed);
            return float.IsNaN(w) || torque > v.ClutchCapacity ? -1 : v.Engine.FullThrottle(w) - torque;
        }
        // The fastest it could go in this gear: the engine at its rev limit with no slip.
        float hi = v.Engine.RevLimit / (p.FinalDrive * ratio) * p.Radius, lo = 0;
        const int Scan = 400;
        for (int i = Scan; i > 0; i--) // the top of the range where the engine still has torque to spare
        {
            float s = hi * i / Scan;
            if (Spare(s) >= 0) { lo = s; hi = hi * (i + 1) / Scan; break; }
            if (i == 1) return default;
        }
        for (int i = 0; i < 40; i++) { float mid = (lo + hi) / 2; if (Spare(mid) >= 0) lo = mid; else hi = mid; }
        var (engine, _) = Need(lo);
        return new(lo, engine, engine / (p.FinalDrive * ratio) * p.Radius - lo);
    }

    // ---------- step by step ----------

    readonly Vehicle v;
    readonly float tensionFollow; // the belt tension's smoothing (τ 0.05 s) per substep
    public float Time, Speed, EngineSpeed, MainClutch, Throttle = 1;
    public int Gear, ActiveGear, GearState = 3; // gearbox: 0 neutral, 1 disengaging, 2 engaging, 3 engaged
    float gearTimer, engage = 1, activeRatio, splitSpeed, splitAngle, clutchReturn;
    readonly float[] splitSide = new float[2], inputSpeed = new float[2], splitTorque = new float[2];
    readonly Side[] sides = { new(), new() };

    sealed class Side { public float Omega, TensionDiff, Traction; }

    public DriveSim(Vehicle vehicle)
    {
        v = vehicle;
        EngineSpeed = vehicle.Engine.IdleSpeed;
        activeRatio = vehicle.Gears[0];
        float sub = vehicle.FixedDeltaTime / Substeps;
        tensionFollow = 1 - MathF.Exp(-sub / 0.05f);
    }

    public float SprocketSpeed => (sides[0].Omega + sides[1].Omega) / 2;
    float NormalForce => v.Mass * v.Gravity / 2;

    /// One physics step: the drivetrain's 200 substeps, then the hull.
    public void Step(bool clutchEngaged, float clutchSpeed)
    {
        float dt = v.FixedDeltaTime, sub = dt / Substeps, h = sub / Iterations;
        float normal = NormalForce;
        float rolling = v.RollingResistance * normal * (1 + v.RollingPerSpeed2 * Speed * Speed) * MathF.Tanh(2 * MathF.Abs(Speed));
        for (int s = 0; s < Substeps; s++)
        {
            EngineStep(sub);
            GearboxStep(sub);
            float ratio = activeRatio, eng = engage;
            for (int k = 0; k < 2; k++) inputSpeed[k] = splitSpeed + eng * (sides[k].Omega * v.Sprocket.FinalDrive * ratio - splitSpeed);
            MainClutch = MoveTowards(MainClutch, clutchEngaged ? 1 : 0, (clutchEngaged ? clutchSpeed : 4) * sub);
            float c = MainClutch < 0.5f ? 4 * MainClutch * MainClutch * MainClutch : 4 * (MainClutch - 1) * (MainClutch - 1) * (MainClutch - 1) + 1;
            float capacity = c * v.ClutchCapacity, returned = 0;
            for (int it = 0; it < Iterations; it++)
            {
                for (int k = 0; k < 2; k++) splitSide[k] = splitAngle + eng * (splitSide[k] + inputSpeed[k] * h - splitAngle);
                float clutch = Math.Clamp(v.ClutchStiffness * (EngineSpeed - splitSpeed), -capacity, capacity);
                returned -= clutch;
                for (int k = 0; k < 2; k++) splitTorque[k] = Math.Clamp((inputSpeed[k] - splitSpeed) * 3 + (splitSide[k] - splitAngle) * 10000, -100000, 100000);
                splitSpeed += (clutch - splitSpeed * 0.1f + splitTorque[0] + splitTorque[1]) * 0.5f * h;
                splitAngle += splitSpeed * h;
            }
            clutchReturn = returned / Iterations;
            for (int k = 0; k < 2; k++) SprocketStep(sides[k], sub, -splitTorque[k] * eng * ratio, rolling, normal);
            if (MathF.Abs(splitAngle) > 2 * MathF.PI)
            {
                float wrap = splitAngle > 0 ? -2 * MathF.PI : 2 * MathF.PI;
                splitAngle += wrap; splitSide[0] += wrap; splitSide[1] += wrap;
            }
        }
        // The hull: both belts' traction less its rolling resistance.
        Speed += (sides[0].Traction + sides[1].Traction - 2 * rolling) / v.Mass * dt;
        Time += dt;
    }

    void EngineStep(float dt)
    {
        var e = v.Engine;
        float w = EngineSpeed, start = e.RevLimit - LimiterBand, fuel = Throttle, brake = 0;
        if (w > start)
        {
            float x = (w - start) / (e.RevLimit - start);
            fuel = (1 - x * x * x * x) * Throttle;
            brake = (w - start) * e.Friction * 5 * x * x;
        }
        float idleStart = e.IdleSpeed - LimiterBand;
        if (Throttle < 0.01f)
        {
            if (w > e.IdleSpeed) fuel = 0;
            else if (w <= idleStart) fuel = 1;
            else fuel = Math.Min(fuel, 1 - MathF.Pow((w - idleStart) / (e.IdleSpeed - idleStart), 2));
        }
        // Engine braking off the throttle.
        if (w > e.IdleSpeed) brake += (w - idleStart) / (e.IdleSpeed + 1 - idleStart) * MathF.Pow(1 - Throttle, 3) * w * e.Friction;
        float generated = Sample(e.CurveSpeed, e.CurveTorque, w) * fuel - brake;
        float next = w + (clutchReturn + generated) / e.Inertia * dt;
        EngineSpeed = next <= MinimumSpeed ? 0 : next;
    }

    void GearboxStep(float dt)
    {
        gearTimer += dt;
        switch (GearState)
        {
            case 0: Select(); break;
            case 3: engage = 1; if (Gear != ActiveGear) { GearState = 1; gearTimer = 0; } break;
            case 1:
                if (gearTimer < v.Disengage) engage = Math.Clamp(1 - gearTimer / v.Disengage, 0, 1);
                else Select();
                break;
            case 2:
                if (gearTimer < v.Engage) engage = Math.Min(gearTimer / v.Engage, 1);
                else { engage = 1; GearState = 3; gearTimer = 0; }
                break;
        }
        void Select() { ActiveGear = Math.Clamp(Gear, 0, v.Gears.Length - 1); activeRatio = v.Gears[ActiveGear]; GearState = 2; gearTimer = 0; engage = 0; }
    }

    void SprocketStep(Side s, float dt, float inputTorque, float rolling, float normal)
    {
        var p = v.Sprocket;
        float beltSpeed = s.Omega * p.Radius;
        // Belt-ground traction: slip against the hull's speed (on the move), or plain slip (at a standstill).
        float blend = Math.Clamp(2 * MathF.Abs(Speed), 0, 1);
        float slip = beltSpeed - Speed;
        float measure = (slip / (MathF.Abs(Speed) + 0.5f) - 2 * slip) * blend + 2 * slip;
        s.Traction = MathF.Tanh(2 * measure) * p.Friction * normal;
        // Belt tension (tight less slack) follows traction plus the belts' share of rolling resistance.
        s.TensionDiff += tensionFollow * (-v.RollingTransfer * rolling - s.Traction - s.TensionDiff);
        float resist = s.TensionDiff * p.Radius - p.Viscous * s.Omega - MathF.Tanh(s.Omega * 0.1f) * p.Bending;
        s.Omega += (resist + inputTorque * p.FinalDrive) / p.Inertia * dt;
    }

    static float MoveTowards(float from, float to, float step) => MathF.Abs(to - from) <= step ? to : from + MathF.Sign(to - from) * step;

    // ---------- the driver ----------

    public readonly record struct Point(float Time, float Speed, float EngineSpeed, int Gear);

    /// Full throttle from a standstill, the way the game's driver does it: first gear engaged at idle, revved to the
    /// operating speed, clutch in; then up a gear each time the engine passes the upshift speed (if the next gear can
    /// still pull), the clutch out at 4/s while the throttle matches the engine to the sprocket in the next gear, and back
    /// in at 4/s. Stops once the speed settles (or after `seconds`).
    public static List<Point> Accelerate(Vehicle v, float seconds = 300)
    {
        var sim = new DriveSim(v) { Gear = 0, GearState = 0, Throttle = 0.02f };
        var top = v.Gears.Select(r => TopSpeed(v, r).Speed).ToArray();
        var points = new List<Point>();
        string phase = "gear";
        float dt = v.FixedDeltaTime, settledFor = 0;
        while (sim.Time < seconds)
        {
            bool clutch = false;
            float clutchSpeed = 4;
            switch (phase)
            {
                case "gear": // the gearbox engages first gear, the engine at idle
                    sim.Throttle = 0.02f;
                    if (sim.GearState == 3) phase = "rev";
                    break;
                case "rev": // throttle up (2/s), clutch open, to the engine's operating speed
                    sim.Throttle = Math.Min(1, sim.Throttle + 2 * dt);
                    if (sim.EngineSpeed >= v.Engine.OperatingSpeed) phase = "launch";
                    break;
                case "launch": // clutch in at 1/s
                    sim.Throttle = Math.Min(1, sim.Throttle + 2 * dt);
                    clutch = true; clutchSpeed = 1;
                    if (sim.MainClutch >= 1) phase = "drive";
                    break;
                case "drive":
                    sim.Throttle = Math.Min(1, sim.Throttle + 5 * dt);
                    clutch = true;
                    // GetIdealGear: up once past the upshift speed, if the next gear keeps the engine above 1.2 x idle
                    // and still pulls (its settled speed is higher).
                    if (sim.EngineSpeed > v.UpshiftSpeed && sim.GearState == 3 && sim.Gear + 1 < v.Gears.Length && top[sim.Gear + 1] > sim.Speed * 1.001f
                        && sim.SprocketSpeed * v.Sprocket.FinalDrive * v.Gears[sim.Gear + 1] > 1.2f * v.Engine.IdleSpeed)
                        phase = "out";
                    break;
                case "out":
                    sim.Throttle = RevMatch(sim, v, sim.Gear + 1);
                    if (sim.MainClutch <= 0) { sim.Gear++; phase = "change"; }
                    break;
                case "change":
                    sim.Throttle = RevMatch(sim, v, sim.Gear);
                    if (sim.GearState == 3) phase = "in";
                    break;
                case "in": // clutch in at 4/s, throttle up at 5/s
                    clutch = true;
                    sim.Throttle = Math.Min(1, sim.Throttle + 5 * dt);
                    if (sim.MainClutch >= 0.6f) phase = "drive";
                    break;
            }
            float before = sim.Speed;
            sim.Step(clutch, clutchSpeed);
            points.Add(new(sim.Time, sim.Speed, sim.EngineSpeed, sim.ActiveGear));
            // Settled: in drive, gaining less than 0.01 km/h a second for 2 s.
            settledFor = phase == "drive" && (sim.Speed - before) / dt < 0.01f / 3.6f ? settledFor + dt : 0;
            if (settledFor >= 2 || (phase == "drive" && sim.Time > 5 && sim.Speed < 0.01f)) break;
        }
        return points;
    }

    /// The game's rev matching: the engine's target is the sprocket's speed through the final drive and the gear, the
    /// throttle opening across a 100 rpm band around it.
    static float RevMatch(DriveSim sim, Vehicle v, int gear)
    {
        float target = sim.SprocketSpeed * v.Sprocket.FinalDrive * v.Gears[Math.Min(gear, v.Gears.Length - 1)];
        float diff = (target - sim.EngineSpeed) * 30 / MathF.PI;
        return diff >= 0 ? Math.Clamp((diff + 50) / 100, 0, 1) : 1 - Math.Clamp((-diff + 50) / 100, 0, 1);
    }
}
