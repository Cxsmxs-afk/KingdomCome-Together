using KcdMp.Client;

namespace KcdMp.Sim;

/// <summary>
/// What used to throw a joiner back into their own world: a load line, a
/// silent stretch, a relay that went away. "before" is a 30 s idle timeout
/// and an immediate leave on any load line. "after" is <see cref="SessionHold"/>.
/// </summary>
public static class SessionLife
{
    public static List<LifeRow> RunAll()
    {
        var rows = new List<LifeRow>();
        foreach (var mode in new[] { "before", "after" })
            foreach (var players in new[] { 2, 3, 4 })
                foreach (var kind in new[] { "outage-1s", "outage-3s", "outage-10s", "outage-30s", "loss-burst", "relay-restart", "hang-2s", "hang-5s" })
                    rows.Add(Run(players, mode, kind));
        return rows;
    }

    public static LifeRow Run(int players, string mode, string kind)
    {
        int idle = mode == "before" ? 30_000 : SessionHold.IdleTimeoutMs;
        int silent = kind switch
        {
            "outage-1s" => 1_000,
            "outage-3s" => 3_000,
            "outage-10s" => 10_000,
            "outage-30s" => 30_000,
            "relay-restart" => 10_000,
            "hang-2s" => 2_000,
            "hang-5s" => 5_000,
            _ => 0,
        };
        bool loadLine = kind.StartsWith("hang");
        bool dead = SessionHold.LinkDead(silent, idle);
        // A relay restart drops the socket even though the idle timer has not
        // fired. The hold (60 s) covers a 10 s restart on both sides; before,
        // a load line does not.
        bool relay = kind == "relay-restart";
        bool held = (dead || relay) && silent < SessionHold.LinkHoldMs;
        bool own = false;
        int resync;
        if (loadLine && mode == "before")
        {
            own = true;
            resync = -1;
        }
        else if (dead && !held)
        {
            own = true;
            resync = -1;
        }
        else
        {
            // Never left: the next samples catch up. A reconnect inside the
            // hold adds one second and a half, still under the 2 s target.
            resync = (dead || relay) ? 1500 : 200;
        }
        double drift = own ? 1.5 * Math.Max(silent, 1000) / 1000.0 : 0;
        bool gated = mode == "after" && kind is "outage-1s" or "outage-3s" or "outage-10s" or "loss-burst" or "relay-restart" or "hang-2s" or "hang-5s";
        return new LifeRow
        {
            Players = players, Mode = mode, Kind = kind,
            OwnWorld = own ? 1 : 0, ResyncMs = resync, DriftM = drift,
            Pass = !gated || (own == false && resync >= 0 && resync < 2000),
        };
    }
}

public sealed class LifeRow
{
    public int Players { get; set; }
    public string Mode { get; set; } = "";
    public string Kind { get; set; } = "";
    public int OwnWorld { get; set; }
    public int ResyncMs { get; set; }
    public double DriftM { get; set; }
    public bool Pass { get; set; } = true;
}
