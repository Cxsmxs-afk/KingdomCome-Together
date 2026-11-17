using System.Diagnostics;
using System.Net;
using System.Text.Json;
using KcdMp.Client;
using KcdMp.Server;
using KcdMp.Wire;

namespace KcdMp.Sim;

/// <summary>
/// Headless smoothness check. The relay is the real KcdMp.Server. Each player
/// uses the real handshake, the real LinkScheduler (newest state, drop limit)
/// and the real BatchQueue (the console queue behind the 9 s stalls).
/// There is no game, so animation and ExecuteString time are a model, not a measurement.
/// </summary>
public static class Program
{
    const int DurationMs = 5000;
    const int TickMs = 50;
    const int NpcCount = 40;
    const double ApplyMsPerStatement = 35; // field: the console often answers in about this

    public static async Task<int> Main(string[] args)
    {
        string outDir = "tools/sim/out";
        string label = "this-tree";
        string timeline = "legacy";
        bool loadOnly = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--out" && i + 1 < args.Length) outDir = args[++i];
            else if (args[i] == "--label" && i + 1 < args.Length) label = args[++i];
            else if (args[i] == "--timeline" && i + 1 < args.Length) timeline = args[++i];
            else if (args[i] == "--load-only") loadOnly = true;
        }
        Directory.CreateDirectory(outDir);
        var commit = (Environment.GetEnvironmentVariable("SIM_COMMIT") ?? "unknown").Trim();
        var results = new List<RunResult>();
        if (!loadOnly)
            foreach (var players in new[] { 2, 3, 4 })
                foreach (var profile in Profile.All)
                    results.Add(await RunOne(players, profile, label, commit));

        var opt = new JsonSerializerOptions { WriteIndented = true };
        string jsonPath = Path.Combine(outDir, "sim.json");
        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(results, opt));
        string mdPath = Path.Combine(outDir, "sim.md");
        var load = new List<LoadResult>();
        if (timeline == "both")
        {
            load.AddRange(Continuous.RunAll("legacy"));
            load.AddRange(Continuous.RunAll("fixed"));
        }
        else load.AddRange(Continuous.RunAll(timeline));
        await File.WriteAllTextAsync(Path.Combine(outDir, "sim-load.json"), JsonSerializer.Serialize(load, opt));
        var queued = QueueLoad.RunAll();
        var feel = Feel.RunAll();
        await File.WriteAllTextAsync(mdPath, Report.Markdown(label, commit, results) + Report.Load(load) + Report.Queue(queued) + Report.Feel(feel));
        int gated = results.Count(r => r.Gate);
        int failed = results.Count(r => r.Gate && !r.Pass);
        // Phase 3: a flooded console freezes the figure for seconds. The ring's
        // pose age stays the link plus the fixed delay, under a second.
        bool ringFlat = queued.Where(r => r.Timeline == "ring").All(r => r.WireBytes < 800);
        bool consoleTrails = queued.Where(r => r.Timeline == "console").All(r => r.WireBytes > 2000);
        bool feelOk = feel.All(r => r.Pass);
        Console.WriteLine($"sim {label}: {gated - failed}/{gated} gated runs passed. timeline={timeline}. queue ringFlat={ringFlat} consoleTrails={consoleTrails} feel={feelOk}. {mdPath}");
        return failed == 0 && ringFlat && consoleTrails && feelOk ? 0 : 1;
    }

    static async Task<RunResult> RunOne(int players, Profile profile, string label, string commit)
    {
        int port = FreePort();
        int http = FreePort();
        await using var app = KcdMp.Server.Program.CreateApp(
        [
            "--port", port.ToString(),
            "--Urls", $"http://127.0.0.1:{http}",
            "--Serilog:MinimumLevel", "Warning",
        ]);
        await app.StartAsync();
        await WaitPort(port);
        var cpu0 = Process.GetCurrentProcess().TotalProcessorTime;
        var wall0 = Stopwatch.StartNew();
        await using var proxy = new DelayProxy(port, profile);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var bots = new Bot[players];
        for (int i = 0; i < players; i++)
            bots[i] = await Bot.Connect(proxy.Port, i == 0 ? "host" : "p" + i, i == 0, cts.Token);

        var send = Task.WhenAll(bots.Select(b => b.SendLoop(DurationMs, players, cts.Token)));
        var recv = Task.WhenAll(bots.Select(b => b.RecvLoop(cts.Token)));
        await send;
        cts.Cancel();
        try { await recv; } catch { }

        double wall = wall0.Elapsed.TotalMilliseconds;
        double cpu = (Process.GetCurrentProcess().TotalProcessorTime - cpu0).TotalMilliseconds / Math.Max(wall, 1) * 100;
        var r = Bot.Collect(bots, players, profile.Name, label, commit, cpu, proxy.Dropped, proxy.Forwarded);
        foreach (var b in bots) b.Dispose();
        await app.StopAsync();
        return r;
    }

    static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    static async Task WaitPort(int port)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var c = new System.Net.Sockets.TcpClient();
                await c.ConnectAsync(IPAddress.Loopback, port);
                return;
            }
            catch { await Task.Delay(40); }
        }
        throw new TimeoutException("relay did not listen");
    }
}

sealed class Bot : IDisposable
{
    public bool Host { get; }
    public int Index { get; }
    readonly System.Net.Sockets.TcpClient _tcp;
    readonly Stream _stream;
    readonly LinkScheduler _sched = new();
    readonly BatchQueue _queue = new();
    readonly List<double> _netLag = new();
    readonly List<(uint Ms, float X, float Y, float Speed)> _truth = new();
    readonly List<(byte Id, uint Ms, float X, float Y)> _got = new();
    readonly object _gate = new();
    int _bytesOut, _bytesIn, _queueMax, _stallStateDelay = -1;
    long _edgesShed, _staleDropped;

    Bot(System.Net.Sockets.TcpClient tcp, Stream stream, bool host, int index)
    {
        _tcp = tcp; _stream = stream; Host = host; Index = index;
    }

    public static async Task<Bot> Connect(int port, string name, bool host, CancellationToken ct)
    {
        var tcp = new System.Net.Sockets.TcpClient { NoDelay = true };
        await tcp.ConnectAsync(IPAddress.Loopback, port, ct);
        var s = tcp.GetStream();
        await s.WriteAsync(RelayConnector.BuildHandshake(name, ReleaseVersionInfo.Wire), ct);
        var (type, _) = await RelayConnector.ReadFrameAsync(s, ct);
        if (type != Protocol.Ack) throw new InvalidOperationException($"handshake {type}");
        return new Bot(tcp, s, host, host ? 0 : name[^1] - '0');
    }

    public async Task SendLoop(int durationMs, int players, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        bool seeded = false;
        while (clock.ElapsedMilliseconds < durationMs && !ct.IsCancellationRequested)
        {
            uint ms = (uint)Environment.TickCount64;
            double t = clock.Elapsed.TotalSeconds;
            var (x, y, speed, riding) = PathAt(t, Index);
            lock (_gate) _truth.Add((ms, x, y, speed));
            _sched.Enqueue(PositionCodec.BuildPosition(x, y, 200f, 0.2f, riding, false, null, ms));
            if (Host && (int)(t * 20) % 2 == 0) SendNpcs(ms, t);
            if (((int)(t * 10)) % 20 == Index) _sched.Enqueue(Story("vote"));
            if (t is >= 3.0 and < 3.2) for (int i = 0; i < 4; i++) _sched.Enqueue(Story("hit" + i));
            while (_sched.TryTake(int.MaxValue, -1, out var frame, out _))
            {
                await _stream.WriteAsync(frame, ct);
                _bytesOut += frame.Length;
            }
            // A quest catch-up already sitting in the console queue, 9 s old.
            if (!seeded && t >= 2.0)
            {
                seeded = true;
                _stallStateDelay = -1;
                long old = Environment.TickCount64 - 9000;
                lock (_gate)
                    for (int i = 0; i < 80; i++)
                        _queue.Add(Edge("e" + i, old));
            }
            DrainGame(Environment.TickCount64, t is >= 2.0 and < 3.5, afterSeed: seeded);
            await Task.Delay(50, ct);
        }
        DrainGame(Environment.TickCount64, false, afterSeed: true);
        _staleDropped = _sched.StaleDropped;
        _edgesShed = ShedOf(_queue);
    }

    void SendNpcs(uint ms, double t)
    {
        for (int n = 0; n < 40; n++)
        {
            if (n % 2 == 1 && ((int)(t * 10) % 4) != 0) continue; // idle heartbeats less often
            float x = 1000 + (n % 8) * 4f + (float)(Math.Sin(t + n) * (n % 2 == 0 ? 1.2 : 0.05));
            float y = 1000 + (n / 8) * 4f;
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
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(o + Protocol.NpcStateSeqOffset), (ushort)(t * 10));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(o + Protocol.NpcStateSenderMsOffset), ms);
            var frame = new byte[3 + p.Length];
            frame[0] = Protocol.NpcStateUp;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(1), (ushort)p.Length);
            p.CopyTo(frame, 3);
            _sched.Enqueue(frame);
        }
    }

    public async Task RecvLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var (type, body) = await RelayConnector.ReadFrameAsync(_stream, ct);
                _bytesIn += 3 + body.Length;
                long now = Environment.TickCount64;
                if (type == Protocol.Ghost && PositionCodec.TryDecodeGhost(body, out var g) && g.SenderMs != 0)
                {
                    lock (_gate)
                    {
                        _netLag.Add(Unchecked(now, g.SenderMs));
                        _got.Add((g.GhostId, g.SenderMs, g.X, g.Y));
                    }
                    lock (_gate) _queue.Add(Level("KCD2MP_UpdateGhost(\"g\",1,0,0,0,false)", now));
                }
                else if (type == Protocol.NpcStateDown)
                { lock (_gate) _queue.Add(Level("if KCD2MP_ApplyNpcState then KCD2MP_ApplyNpcState(\"n\",1,0,0,0,100,0,0,1,1) end", now)); }
                else if (type is Protocol.StoryBeatDown or Protocol.StoryBeatUp)
                { lock (_gate) _queue.Add(Edge("toast-" + now, now)); }
            }
        }
        catch { }
    }

    void DrainGame(long now, bool stalled, bool afterSeed)
    {
        lock (_gate)
        {
            if (_queue.Count > _queueMax) _queueMax = _queue.Count;
            if (stalled) return;
            var batch = _queue.TakeBatch(100_000, now);
            if (batch.Count == 0 || !afterSeed || _stallStateDelay >= 0) return;
            int before = 0;
            bool state = false;
            foreach (var e in batch)
            {
                if (e.Kind == BatchKind.Level) { state = true; break; }
                before++;
            }
            if (!state) return;
            // How long the newest state waited behind one-shots, in the console's own time.
            _stallStateDelay = before * 35;
        }
    }

    static (float X, float Y, float Speed, bool Riding) PathAt(double t, int who)
    {
        float speed = t < 1.5 ? 1.4f : t < 3.0 ? 3.0f : t < 4.0 ? 6.0f : 8.0f;
        bool riding = t >= 4.0;
        float x = (float)(who * 30 + speed * t);
        float y = (float)(who * 8 + Math.Sin(t) * 0.4);
        return (x, y, speed, riding);
    }

    static byte[] Story(string text)
    {
        var b = System.Text.Encoding.UTF8.GetBytes(text);
        var p = new byte[2 + b.Length];
        p[0] = 1; p[1] = (byte)b.Length; b.CopyTo(p, 2);
        var f = new byte[3 + p.Length];
        f[0] = Protocol.StoryBeatUp;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(1), (ushort)p.Length);
        p.CopyTo(f, 3);
        return f;
    }

    static BatchQueue.Entry Level(string lua, long at) =>
        new() { Lua = lua, Text = lua, Kind = BatchKind.Level, Key = lua, Encoded = 40, EnqueuedMs = at };

    static BatchQueue.Entry Edge(string id, long at) =>
        new()
        {
            Lua = "if KCD2MP_ShowNativeToast then KCD2MP_ShowNativeToast(\"" + id + "\") end",
            Text = id, Kind = BatchKind.Edge, Encoded = 40, EnqueuedMs = at, OnceId = id,
        };

    static long ShedOf(BatchQueue q)
    {
        var prop = q.GetType().GetProperty("EdgesShed");
        return prop?.GetValue(q) is long n ? n : 0;
    }

    static double Unchecked(long now, uint then)
    {
        long d = (long)(uint)now - then;
        if (d < 0) d += 1L << 32;
        return d;
    }

    public static RunResult Collect(Bot[] bots, int players, string profile, string label, string commit, double cpu, long dropped, long forwarded)
    {
        var lags = bots.SelectMany(b => b._netLag).OrderBy(x => x).ToList();
        int snaps = 0, discont = 0;
        double extrap = 0;
        foreach (var b in bots)
        {
            foreach (var group in b._got.GroupBy(s => s.Id))
            {
                var got = group.OrderBy(s => s.Ms).ToList();
                float prevV = 0; bool haveV = false; uint prevMs = 0; float px = 0, py = 0;
                foreach (var s in got)
                {
                    if (prevMs != 0)
                    {
                        double dt = Math.Max(0.001, (Unchecked(s.Ms, prevMs)) / 1000.0);
                        double dist = Math.Sqrt((s.X - px) * (s.X - px) + (s.Y - py) * (s.Y - py));
                        float v = (float)(dist / dt);
                        if (dist > 5) snaps++;
                        if (haveV && Math.Abs(v - prevV) > 4) discont++;
                        prevV = v; haveV = true;
                    }
                    prevMs = s.Ms; px = s.X; py = s.Y;
                }
            }
        }
        int stateDelay = bots.Select(b => b._stallStateDelay).DefaultIfEmpty(-1).Max();
        int qmax = bots.Max(b => b._queueMax);
        long shed = bots.Sum(b => b._edgesShed);
        long stale = bots.Sum(b => b._staleDropped);
        int bytes = bots.Sum(b => b._bytesOut + b._bytesIn);
        double seconds = ProgramDurationSeconds;
        var r = new RunResult
        {
            Label = label, Commit = commit, Players = players, Profile = profile,
            Samples = lags.Count,
            P50 = Pct(lags, 0.50), P95 = Pct(lags, 0.95), P99 = Pct(lags, 0.99), Max = lags.Count == 0 ? -1 : lags[^1],
            QueueMax = qmax, StateDelayMs = stateDelay, EdgesShed = shed, StaleDropped = stale,
            Snaps = snaps, VelocityBreaks = discont, ExtrapMs = extrap,
            BytesPerClient = players == 0 ? 0 : bytes / players / Math.Max(seconds, 0.001),
            RelayCpuPercent = cpu, ProxyDropped = dropped, ProxyForwarded = forwarded,
        };
        r.Pass = Judge(r, out bool gate, out string why);
        r.Gate = gate; r.Why = why;
        return r;
    }

    const double ProgramDurationSeconds = 5;

    static bool Judge(RunResult r, out bool gate, out string why)
    {
        bool calm = r.Profile is "good" or "typical";
        gate = calm;
        if (!calm) { why = "reported, not gated"; return true; }
        bool strict = r.Players == 2;
        double p99 = strict ? (r.Profile == "good" ? 400 : 900) : (r.Profile == "good" ? 700 : 1500);
        double max = strict ? 2000 : 4000;
        int state = strict ? 500 : 1500;
        if (r.Samples < 5) { why = "too few samples"; return false; }
        if (r.P99 > p99) { why = $"p99 {r.P99:0} > {p99:0}"; return false; }
        if (r.Max > max) { why = $"max {r.Max:0} > {max:0}"; return false; }
        if (r.StateDelayMs > state) { why = $"console state waited {r.StateDelayMs} ms behind one-shots"; return false; }
        why = "ok";
        return true;
    }

    static double Pct(List<double> s, double q)
    {
        if (s.Count == 0) return -1;
        double i = (s.Count - 1) * q;
        int lo = (int)i;
        int hi = Math.Min(s.Count - 1, lo + 1);
        return s[lo] + (s[hi] - s[lo]) * (i - lo);
    }

    public void Dispose() { try { _tcp.Dispose(); } catch { } }
}

sealed class RunResult
{
    public string Label { get; set; } = "";
    public string Commit { get; set; } = "";
    public int Players { get; set; }
    public string Profile { get; set; } = "";
    public int Samples { get; set; }
    public double P50 { get; set; }
    public double P95 { get; set; }
    public double P99 { get; set; }
    public double Max { get; set; }
    public int QueueMax { get; set; }
    public int StateDelayMs { get; set; }
    public long EdgesShed { get; set; }
    public long StaleDropped { get; set; }
    public int Snaps { get; set; }
    public int VelocityBreaks { get; set; }
    public double ExtrapMs { get; set; }
    public double BytesPerClient { get; set; }
    public double RelayCpuPercent { get; set; }
    public long ProxyDropped { get; set; }
    public long ProxyForwarded { get; set; }
    public bool Gate { get; set; }
    public bool Pass { get; set; }
    public string Why { get; set; } = "";
}

static class Report
{
    public static string Markdown(string label, string commit, List<RunResult> rows)
    {
        int gated = rows.Count(r => r.Gate);
        int ok = rows.Count(r => r.Gate && r.Pass);
        var worst = rows.Where(r => r.Gate).OrderByDescending(r => r.Max).FirstOrDefault();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# Simulator {label}");
        sb.AppendLine();
        sb.AppendLine($"Kurz: {ok} von {gated} gewerteten Läufen bestanden (gute und typische Leitung).");
        if (worst != null)
            sb.AppendLine($"Größte gewertete Latenz: {worst.Max:0} ms bei {worst.Players} Spielern, Profil {worst.Profile}. Eine 9000-ms-Spitze wäre hier sichtbar.");
        sb.AppendLine("Der Konsolen-Rückstand ist die Wartezeit des neuesten Stands hinter alten Einmal-Befehlen, gerechnet mit 35 ms pro Befehl. Das ist das Modell der Spielkonsole, nicht eine Messung im Spiel.");
        sb.AppendLine();
        sb.AppendLine($"Commit `{commit}`.");
        sb.AppendLine();
        sb.AppendLine("| Spieler | Profil | p50 | p95 | p99 | max | Queue | Stand hinter Einmal | verworfen | Snaps | Brüche | Byte/s | Wertung |");
        sb.AppendLine("|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|");
        foreach (var r in rows)
        {
            string mark = !r.Gate ? "nur Bericht" : r.Pass ? "bestanden" : "durchgefallen: " + r.Why;
            sb.AppendLine($"| {r.Players} | {r.Profile} | {r.P50:0} | {r.P95:0} | {r.P99:0} | {r.Max:0} | {r.QueueMax} | {r.StateDelayMs} | {r.EdgesShed} | {r.Snaps} | {r.VelocityBreaks} | {r.BytesPerClient:0} | {mark} |");
        }
        sb.AppendLine();
        sb.AppendLine("## Was die Simulation nicht zeigt");
        sb.AppendLine();
        sb.AppendLine("- Wie eine Animation im Spiel aussieht, ob Füße rutschen, ob ein Pferd galoppiert.");
        sb.AppendLine("- Wie lange ExecuteString und die Lua-Konsole auf diesem PC wirklich brauchen. Die 35 ms sind der Wert aus den Feldsitzungen, fest eingesetzt.");
        sb.AppendLine("- Die native DLL, `body_drive`, und ob die Engine einen Körper annimmt.");
        sb.AppendLine("- Steam, NAT und eine echte Leitung. Die Verzögerung macht ein Proxy im selben Prozess.");
        sb.AppendLine("- Die CPU-Zahl ist der ganze Simulator inklusive Relay, nicht das Relay allein.");
        return sb.ToString();
    }

    public static string Load(List<LoadResult> rows)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine();
        sb.AppendLine("## Dauerlast, 10 Minuten, virtuelle Uhr");
        sb.AppendLine();
        sb.AppendLine("40 NPCs und die Spieler, Profile typical/bad/awful, zehn Minuten auf einer virtuellen Uhr. Ein Konsolen-Aufruf kostet 35 ms und nimmt jeden Stand mit, der in 1900 Zeichen passt, wie der echte Flusher. Queue ist das Alter des ältesten wartenden Stands. Snaps sind Sprünge über 5 m. Brüche sind Geschwindigkeitssprünge der Spieler über 3 m/s.");
        sb.AppendLine();
        sb.AppendLine("| Spieler | Profil | Zeitlinie | Queue p95 | Queue max | Snaps/min | davon Spieler | davon NPC | Brüche/min | Extrapoliert ms |");
        sb.AppendLine("|---:|---|---|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var r in rows)
            sb.AppendLine($"| {r.Players} | {r.Profile} | {r.Timeline} | {r.QueueP95:0} | {r.QueueMax:0} | {r.SnapsPerMin:0.0} | {r.PlayerSnapsPerMin:0.0} | {r.NpcSnapsPerMin:0.0} | {r.BreaksPerMin:0.0} | {r.ExtrapMs:0} |");
        return sb.ToString();
    }

    public static string Queue(List<LoadResult> rows)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine();
        sb.AppendLine("## Quest-Last, Pose");
        sb.AppendLine();
        sb.AppendLine("Eine Minute lang füllt ein Quest-Nachzug die Konsole (eine Zeile pro Runde, schneller als sie abläuft). Nach 20 s steht das Spiel 3 s im Hintergrund und die Konsole tut nichts. `console` ist Phase 2: die Pose wartet in derselben Schlange. `ring` ist Phase 3: die Pose kommt aus dem DLL-Ring und läuft weiter. Queue ist das Alter der Quest-Zeile. Pose p95 bleibt flach; Pose max zeigt, ob die Figur in der Pause stehen bleibt.");
        sb.AppendLine();
        sb.AppendLine("| Spieler | Profil | Pfad | Queue p95 | Queue max | Pose p95 | Pose max |");
        sb.AppendLine("|---:|---|---|---:|---:|---:|---:|");
        foreach (var r in rows)
            sb.AppendLine($"| {r.Players} | {r.Profile} | {r.Timeline} | {r.QueueP95:0} | {r.QueueMax:0} | {r.ExtrapMs:0} | {r.WireBytes} |");
        return sb.ToString();
    }

    public static string Feel(List<FeelRow> rows)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine();
        sb.AppendLine("## Höhe, Treffer, Dialog");
        sb.AppendLine();
        sb.AppendLine("`before` ist die letzte Runde: höhere Trittfläche, Pose-Puffer bei 200 ms gedeckelt. `after` fragt den Boden unter dem gezeigten Punkt und lässt den Puffer dem One-Way folgen (bis 800 ms), mit einem Schrittdeckel. Treffer und Dialog bleiben auf dem direkten Weg.");
        sb.AppendLine();
        sb.AppendLine("| Spieler | Profil | Stand | Höhe p95 cm | Höhe max | Durchsinken | Ruckler/min | Treffer p95 | Tod p95 | verloren | Dialog p95 | Dialog verloren |");
        sb.AppendLine("|---:|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var r in rows)
            sb.AppendLine($"| {r.Players} | {r.Profile} | {r.Mode} | {r.HeightP95Cm:0.0} | {r.HeightMaxCm:0.0} | {r.Sinks} | {r.BreaksPerMin:0.0} | {r.HitP95:0} | {r.DeathP95:0} | {r.Lost} | {r.DialogP95:0} | {r.DialogAsync} |");
        return sb.ToString();
    }
}
