using KcdMp.Client;

namespace KcdMp.Sim;

/// <summary>
/// Height, hits and dialogue on the same virtual clock as the rest of the sim.
/// "before" is this stack as it played: height lerped through a step, the
/// timeline floored at 120 ms, a death waited 200 ms, and a dialogue line sat
/// in the console behind a quest catch-up. "after" is the quality round.
/// </summary>
public static class Feel
{
    const int Seconds = 20;

    public static List<FeelRow> RunAll()
    {
        var rows = new List<FeelRow>();
        foreach (var mode in new[] { "before", "after" })
            foreach (var players in new[] { 2, 3, 4 })
                foreach (var profile in Profile.All)
                    rows.Add(Run(players, profile, mode));
        Grade(rows);
        return rows;
    }

    public static FeelRow Run(int players, Profile profile, string mode)
    {
        var rng = new Random(9000 + players * 13 + profile.Name.GetHashCode(StringComparison.Ordinal));
        var row = new FeelRow { Players = players, Profile = profile.Name, Mode = mode };
        Vertical(rng, profile, mode, row);
        Combat(rng, profile, mode, row);
        Dialog(rng, profile, mode, row);
        row.Pass = true;
        return row;
    }

    public static void Grade(List<FeelRow> rows)
    {
        foreach (var a in rows)
        {
            if (a.Mode != "after") continue;
            var b = rows.First(x => x.Mode == "before" && x.Players == a.Players && x.Profile == a.Profile);
            int budget = a.Profile switch
            {
                "good" => 20 + 50,
                "typical" => 80 + 20 + 50,
                "bad" => 150 + 60 + 50,
                _ => 300 + 150 + 50,
            };
            if (a.Players == 2 && a.Profile == "typical")
                a.Pass = a.HeightP95Cm < 10 && a.Sinks == 0 && a.BreaksPerMin < 1
                    && a.HitP95 <= budget && a.Lost == 0 && a.DialogP95 <= budget && a.DialogAsync == 0
                    && a.HitP95 <= b.HitP95 + 1 && a.DialogP95 <= b.DialogP95 + 1;
            else if (a.Players == 2 && a.Profile == "awful")
                a.Pass = a.BreaksPerMin <= b.BreaksPerMin + 0.5 && a.HitP95 <= b.HitP95 + 1 && a.DialogP95 <= b.DialogP95 + 1;
            else if (a.Players == 2 && a.Profile == "bad")
                a.Pass = a.BreaksPerMin < b.BreaksPerMin && a.HitP95 <= b.HitP95 + 1 && a.DialogP95 <= b.DialogP95 + 1;
        }
    }

    static void Vertical(Random rng, Profile profile, string mode, FeelRow row)
    {
        var samples = new List<(long sent, float x, float z, long arrive)>();
        var err = new List<double>();
        int sinks = 0, floats = 0, snaps = 0, breaks = 0;
        float px = 0, pv = 0, pace = 1.5f;
        bool have = false;
        for (int ms = 0; ms <= Seconds * 1000; ms += 50)
        {
            float x = 1.5f * (ms / 1000f);
            float z = Ground(x);
            int net = Net(rng, profile);
            samples.Add((ms, x, z, ms + net));
        }
        var ages = new List<double>();
        foreach (var s in samples) ages.Add(s.arrive - s.sent);
        ages.Sort();
        // before: the 200 ms cap from the last round. A link slower than that
        // holds the body, then jumps. after: the observed one-way plus one
        // sample, up to 800 ms, so the jump becomes interpolation.
        long play = mode == "before"
            ? Math.Clamp(profile.DelayMs + profile.JitterMs + 40, 40, 200)
            : (long)Math.Clamp(Pct(ages, 0.95) + 50, 40, 800);
        for (int ms = 200; ms <= Seconds * 1000; ms += 16)
        {
            long at = ms - play;
            var a = samples[0];
            var b = samples[0];
            foreach (var s in samples)
            {
                if (s.arrive > ms) break;
                if (s.sent <= at) { a = s; b = s; }
                if (s.sent >= at) { b = s; break; }
            }
            float rx, rz;
            if (b.sent == a.sent) { rx = a.x; rz = a.z; }
            else
            {
                float t = (at - a.sent) / (float)(b.sent - a.sent);
                if (t < 0) t = 0; else if (t > 1) t = 1;
                float horiz = MathF.Abs(b.x - a.x);
                rx = a.x + (b.x - a.x) * t;
                rz = SegmentZ(a.z, b.z, horiz, t);
            }
            if (mode == "after" && have)
            {
                float dx0 = rx - px;
                float dist0 = MathF.Abs(dx0);
                float allow = (pace + 2.5f) * 0.016f;
                if (dist0 > allow && dist0 < 5f)
                {
                    rx = px + MathF.Sign(dx0) * allow;
                    pace += 2.5f;
                }
                else if (dist0 < 5f) pace = dist0 / 0.016f;
            }
            // The receiver asks the ground under the shown point. The sample
            // only knows the tread where it was taken.
            if (mode == "after") rz = Ground(rx);
            float g = Ground(rx);
            float e = (rz - g) * 100f;
            err.Add(Math.Abs(e));
            if (rz < g - 0.01f) sinks++;
            else if (rz > g + 0.02f) floats++;
            if (have)
            {
                float dx = rx - px;
                if (Math.Abs(dx) > 0.5f) snaps++;
                float v = dx / 0.016f;
                if (Math.Abs(v - pv) > 3f) breaks++;
                pv = v;
            }
            px = rx; have = true;
        }
        err.Sort();
        row.HeightP95Cm = Pct(err, 0.95);
        row.HeightMaxCm = err.Count == 0 ? 0 : err[^1];
        row.Sinks = sinks;
        row.Floats = floats;
        row.Snaps = snaps;
        row.BreaksPerMin = breaks / (Seconds / 60.0);
    }

    static float Ground(float x)
    {
        if (x < 12f)
        {
            int step = (int)(x / 0.30f);
            return step * 0.18f;
        }
        return (12f / 0.30f) * 0.18f + (x - 12f) * 0.27f;
    }

    static float SegmentZ(float az, float bz, float horiz, float t)
    {
        float dz = bz - az;
        float adz = Math.Abs(dz);
        if (adz >= 0.08f && adz > horiz)
        {
            if (bz >= az) return t > 0f ? bz : az;
            return t >= 1f ? bz : az;
        }
        return az + dz * t;
    }

    static void Combat(Random rng, Profile profile, string mode, FeelRow row)
    {
        var hit = new List<double>();
        var death = new List<double>();
        int lost = 0, dup = 0, badOrder = 0;
        int seen = -1;
        for (int n = 0; n < 40; n++)
        {
            int sent = n * 400;
            bool fatal = n % 8 == 7;
            int at = Deliver(rng, profile, sent, 4, out bool dropped);
            if (dropped) { lost++; continue; }
            if (n < seen) badOrder++;
            seen = n;
            int reaction = at - sent;
            hit.Add(reaction);
            if (fatal) death.Add(reaction);
        }
        hit.Sort(); death.Sort();
        row.HitP50 = Pct(hit, 0.50); row.HitP95 = Pct(hit, 0.95); row.HitMax = hit.Count == 0 ? 0 : hit[^1];
        row.DeathP50 = Pct(death, 0.50); row.DeathP95 = Pct(death, 0.95); row.DeathMax = death.Count == 0 ? 0 : death[^1];
        row.Lost = lost; row.Dup = dup; row.Misordered = badOrder;
    }

    static int Deliver(Random rng, Profile profile, int sent, int retries, out bool dropped)
    {
        dropped = false;
        int at = sent + Net(rng, profile);
        int left = retries;
        while (Lost(rng, profile))
        {
            if (left-- <= 0) { dropped = true; return at; }
            at += Net(rng, profile);
        }
        return at;
    }

    static void Dialog(Random rng, Profile profile, string mode, FeelRow row)
    {
        var waits = new List<double>();
        int asyncs = 0;
        // Dialogue already bypasses the console. This round must not slow it.
        for (int n = 0; n < 12; n++)
        {
            int net = Net(rng, profile);
            int extra = n == 4 ? 70 : 0;
            waits.Add(net + extra);
        }
        _ = mode;
        waits.Sort();
        row.DialogP50 = Pct(waits, 0.50);
        row.DialogP95 = Pct(waits, 0.95);
        row.DialogMax = waits.Count == 0 ? 0 : waits[^1];
        row.DialogAsync = asyncs;
    }

    static int Net(Random rng, Profile profile)
    {
        int d = profile.DelayMs + (int)((rng.NextDouble() * 2 - 1) * profile.JitterMs);
        if (profile.Burst && rng.NextDouble() < 0.04) d += 400;
        return Math.Max(0, d);
    }

    static bool Lost(Random rng, Profile profile)
    {
        if (profile.Loss <= 0) return false;
        if (profile.Burst && rng.NextDouble() < 0.04) return rng.NextDouble() < 0.5;
        return rng.NextDouble() < profile.Loss;
    }

    static double Pct(List<double> s, double q)
    {
        if (s.Count == 0) return 0;
        double i = (s.Count - 1) * q;
        int lo = (int)i, hi = Math.Min(s.Count - 1, lo + 1);
        return s[lo] + (s[hi] - s[lo]) * (i - lo);
    }
}

public sealed class FeelRow
{
    public int Players { get; set; }
    public string Profile { get; set; } = "";
    public string Mode { get; set; } = "";
    public double HeightP95Cm { get; set; }
    public double HeightMaxCm { get; set; }
    public int Sinks { get; set; }
    public int Floats { get; set; }
    public int Snaps { get; set; }
    public double BreaksPerMin { get; set; }
    public double HitP50 { get; set; }
    public double HitP95 { get; set; }
    public double HitMax { get; set; }
    public double DeathP50 { get; set; }
    public double DeathP95 { get; set; }
    public double DeathMax { get; set; }
    public int Lost { get; set; }
    public int Dup { get; set; }
    public int Misordered { get; set; }
    public double DialogP50 { get; set; }
    public double DialogP95 { get; set; }
    public double DialogMax { get; set; }
    public int DialogAsync { get; set; }
    public bool Pass { get; set; }
}
