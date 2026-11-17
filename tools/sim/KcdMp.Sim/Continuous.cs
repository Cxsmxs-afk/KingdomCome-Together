using KcdMp.Client;
using KcdMp.Wire;

namespace KcdMp.Sim;

/// <summary>
/// Ten minutes of players and 40 NPCs on a virtual clock. Delay, jitter and loss
/// match the proxy profiles, applied to timestamps, so the run does not take ten
/// real minutes. Each console statement costs 35 ms of that clock.
/// "legacy" extrapolates the last sample. "fixed" is phase 2: one 120 ms timeline,
/// no extrapolation, a snap only when the body is more than 0.4 s behind.
/// </summary>
public static class Continuous
{
    public const int Minutes = 10;
    const int StepMs = 5;
    const int ApplyMs = 35;
    const long FixedDelayMs = 120;
    const long SnapBehindMs = 400;

    public static List<LoadResult> RunAll(string timeline)
    {
        var rows = new List<LoadResult>();
        foreach (var players in new[] { 2, 3, 4 })
            foreach (var name in new[] { "typical", "bad", "awful" })
                rows.Add(Run(players, Profile.All.First(p => p.Name == name), timeline));
        return rows;
    }

    public static LoadResult Run(int players, Profile profile, string timeline)
    {
        var rng = new Random(1000 + players * 17 + profile.Name.GetHashCode(StringComparison.Ordinal));
        int steps = Minutes * 60_000 / StepMs;
        var sched = new LinkScheduler[players];
        var queues = new BatchQueue[players];
        var budget = new double[players];
        var pending = new List<Due>[players];
        var bodies = new Dictionary<int, Body>[players];
        for (int i = 0; i < players; i++)
        {
            sched[i] = new LinkScheduler();
            queues[i] = new BatchQueue();
            pending[i] = new List<Due>();
            bodies[i] = new Dictionary<int, Body>();
        }
        var ages = new List<double>(steps / 8);
        int playerSnaps = 0, npcSnaps = 0, breaks = 0;
        double extrapMs = 0;
        long wire = 0;

        for (int step = 0; step < steps; step++)
        {
            long now = (long)step * StepMs;
            if (now % 50 == 0)
            {
                for (int p = 0; p < players; p++)
                {
                    var (x, y, _, riding) = Path(now / 1000.0, p);
                    sched[p].Enqueue(PositionCodec.BuildPosition(x, y, 200f, 0.2f, riding, false, null, (uint)now));
                }
            }
            if (now % 100 == 0)
            {
                for (int n = 0; n < 40; n++)
                {
                    float x = 1000 + (n % 8) * 4f + (float)Math.Sin(now / 1000.0 + n) * (n % 2 == 0 ? 1.2f : 0.05f);
                    float y = 1000 + (n / 8) * 4f;
                    sched[0].Enqueue(NpcFrame(n, x, y, (uint)now));
                }
            }
            for (int p = 0; p < players; p++)
            {
                while (sched[p].TryTake(int.MaxValue, -1, out var frame, out _))
                {
                    wire += frame.Length;
                    if (Lost(rng, profile)) continue;
                    int delay = profile.DelayMs + (int)((rng.NextDouble() * 2 - 1) * profile.JitterMs);
                    if (profile.Burst && rng.NextDouble() < 0.04) delay += 400;
                    if (delay < 0) delay = 0;
                    if (!TrySample(frame, p + 1, out var sample)) continue;
                    for (int to = 0; to < players; to++)
                        if (to != p) pending[to].Add(new Due(now + delay, sample));
                }
            }
            for (int p = 0; p < players; p++)
            {
                var list = pending[p];
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    if (list[i].At > now) continue;
                    var s = list[i].Sample;
                    queues[p].Add(new BatchQueue.Entry
                    {
                        Lua = "KCD2MP_UpdateGhost(\"g\",1,0,0,0,false)",
                        Text = string.Join('|', s.Id.ToString(), s.X.ToString(System.Globalization.CultureInfo.InvariantCulture), s.Y.ToString(System.Globalization.CultureInfo.InvariantCulture), s.Sent.ToString()),
                        Kind = BatchKind.Level, Key = "lvl|" + s.Id, Encoded = 40, EnqueuedMs = list[i].At,
                    });
                    list.RemoveAt(i);
                }
                if (now % 50 == 0) ages.Add(queues[p].LagMs(now));
                budget[p] += StepMs;
                if (budget[p] >= ApplyMs && queues[p].Count > 0)
                {
                    budget[p] -= ApplyMs;
                    // One console round trip. The real flusher sends every statement that fits 1900 encoded characters.
                    var batch = queues[p].TakeBatch(1900, now);
                    foreach (var e in batch)
                    {
                    if (e.Text is null) continue;
                    var bit = e.Text.Split('|');
                    if (bit.Length < 4) continue;
                    var inv = System.Globalization.CultureInfo.InvariantCulture;
                    int who = int.Parse(bit[0]);
                    int snapHere = 0;
                    Render(bodies[p], who, long.Parse(bit[3]),
                        float.Parse(bit[1], inv), float.Parse(bit[2], inv),
                        now, timeline, ref snapHere, ref extrapMs, ref breaks);
                    if (who < 100) playerSnaps += snapHere; else npcSnaps += snapHere;
                    }
                }
            }
        }
        ages.Sort();
        return new LoadResult
        {
            Players = players, Profile = profile.Name, Timeline = timeline,
            QueueP95 = Pct(ages, 0.95), QueueMax = ages.Count == 0 ? 0 : ages[^1],
            SnapsPerMin = (playerSnaps + npcSnaps) / (double)Minutes,
            PlayerSnapsPerMin = playerSnaps / (double)Minutes,
            NpcSnapsPerMin = npcSnaps / (double)Minutes,
            BreaksPerMin = breaks / (double)Minutes,
            ExtrapMs = extrapMs, WireBytes = wire,
        };
    }

    readonly record struct Due(long At, Sample Sample);
    readonly record struct Sample(int Id, float X, float Y, long Sent);

    static bool TrySample(byte[] frame, int sender, out Sample sample)
    {
        sample = default;
        if (frame.Length < 4) return false;
        if (frame[0] == Protocol.Position && PositionCodec.TryDecodeGhost(AsGhost(frame, (byte)sender), out var g))
        {
            sample = new Sample(g.GhostId, g.X, g.Y, g.SenderMs);
            return true;
        }
        if (frame[0] == Protocol.NpcStateUp)
        {
            var p = frame.AsSpan(3);
            int nl = p[0];
            if (p.Length < 1 + nl + Protocol.NpcStateFixedTail) return false;
            int id = 100 + int.Parse(System.Text.Encoding.UTF8.GetString(p.Slice(1, nl))[4..]);
            int o = 1 + nl;
            float x = BitConverter.ToSingle(p[o..]);
            float y = BitConverter.ToSingle(p[(o + 4)..]);
            long sent = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(p[(o + Protocol.NpcStateSenderMsOffset)..]);
            sample = new Sample(id, x, y, sent);
            return true;
        }
        return false;
    }

    static void Render(Dictionary<int, Body> bodies, int id, long sent, float x, float y, long now, string timeline,
        ref int snaps, ref double extrapMs, ref int breaks)
    {
        if (!bodies.TryGetValue(id, out var b)) bodies[id] = b = new Body();
        b.Samples.Add((sent, x, y));
        if (b.Samples.Count > 16) b.Samples.RemoveAt(0);
        float rx, ry;
        var newest = b.Samples[^1];
        if (timeline == "fixed")
        {
            if (now - newest.sent > FixedDelayMs + SnapBehindMs) { rx = newest.x; ry = newest.y; }
            else
            {
                long at = now - FixedDelayMs;
                var last = b.Samples[0];
                var next = b.Samples[^1];
                foreach (var s in b.Samples)
                {
                    if (s.sent <= at) last = s;
                    if (s.sent >= at) { next = s; break; }
                }
                if (at >= newest.sent || next.sent == last.sent) { rx = last.x; ry = last.y; }
                else
                {
                    float u = (at - last.sent) / (float)(next.sent - last.sent);
                    rx = last.x + (next.x - last.x) * u;
                    ry = last.y + (next.y - last.y) * u;
                }
            }
        }
        else
        {
            long behind = Math.Max(0, now - newest.sent);
            long extra = Math.Min(behind, 60);
            extrapMs += extra;
            float vx = 0, vy = 0;
            if (b.Samples.Count >= 2)
            {
                var a = b.Samples[^2];
                float dt = Math.Max(1, newest.sent - a.sent);
                vx = (newest.x - a.x) / dt * extra;
                vy = (newest.y - a.y) / dt * extra;
            }
            rx = newest.x + vx; ry = newest.y + vy;
        }
        if (b.Have)
        {
            float dx = rx - b.X, dy = ry - b.Y;
            float dist = MathF.Sqrt(dx * dx + dy * dy);
            long dt = Math.Max(1, now - b.LastNow);
            if (dist > 5f) snaps++;
            float v = dist / (dt / 1000f);
            if (id < 100 && b.HaveV && MathF.Abs(v - b.V) > 3f) breaks++;
            b.V = v; b.HaveV = true;
        }
        b.X = rx; b.Y = ry; b.Have = true; b.LastNow = now;
    }

    static byte[] AsGhost(byte[] position, byte id)
    {
        var p = position.AsSpan(3);
        var g = new byte[1 + p.Length];
        g[0] = id;
        p.CopyTo(g.AsSpan(1));
        return g;
    }

    static byte[] NpcFrame(int n, float x, float y, uint ms)
    {
        var name = "npc_" + n.ToString("00");
        var nb = System.Text.Encoding.UTF8.GetBytes(name);
        var p = new byte[1 + nb.Length + Protocol.NpcStateFixedTail];
        p[0] = (byte)nb.Length;
        nb.CopyTo(p, 1);
        int o = 1 + nb.Length;
        BitConverter.TryWriteBytes(p.AsSpan(o), x);
        BitConverter.TryWriteBytes(p.AsSpan(o + 4), y);
        BitConverter.TryWriteBytes(p.AsSpan(o + 8), 200f);
        BitConverter.TryWriteBytes(p.AsSpan(o + 16), 100f);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(o + Protocol.NpcStateSenderMsOffset), ms);
        var frame = new byte[3 + p.Length];
        frame[0] = Protocol.NpcStateUp;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(1), (ushort)p.Length);
        p.CopyTo(frame, 3);
        return frame;
    }

    static (float X, float Y, float Speed, bool Riding) Path(double t, int who)
    {
        // Walk, run, sprint, horse. Position is the integral, so a speed change is not a teleport.
        const double cycle = 36.8; // 2*1.4 + 2*3 + 2*6 + 2*8
        double n = Math.Floor(t / 8.0);
        double phase = t - n * 8.0;
        float speed;
        double extra;
        if (phase < 2) { speed = 1.4f; extra = phase * 1.4; }
        else if (phase < 4) { speed = 3f; extra = 2.8 + (phase - 2) * 3; }
        else if (phase < 6) { speed = 6f; extra = 8.8 + (phase - 4) * 6; }
        else { speed = 8f; extra = 20.8 + (phase - 6) * 8; }
        return ((float)(who * 40 + n * cycle + extra), (float)(who * 8), speed, phase >= 6);
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

    sealed class Body
    {
        public List<(long sent, float x, float y)> Samples { get; } = new();
        public float X, Y, V;
        public long LastNow;
        public bool Have, HaveV;
    }
}

public sealed class LoadResult
{
    public int Players { get; set; }
    public string Profile { get; set; } = "";
    public string Timeline { get; set; } = "";
    public double QueueP95 { get; set; }
    public double QueueMax { get; set; }
    public double SnapsPerMin { get; set; }
    public double PlayerSnapsPerMin { get; set; }
    public double NpcSnapsPerMin { get; set; }
    public double BreaksPerMin { get; set; }
    public double ExtrapMs { get; set; }
    public long WireBytes { get; set; }
}
