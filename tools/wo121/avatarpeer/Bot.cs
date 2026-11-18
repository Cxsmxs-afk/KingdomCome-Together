// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using KcdMp.Client;
using KcdMp.Wire;

static partial class P
{
    private const string BotEnv = "KCDMP_TEST_BOT";
    private static readonly Guid BotSwingRow = Guid.Parse("3e27732b-b152-4a27-97d2-1ebba717a588");
    private static readonly HashSet<string> BotVerbs = new(StringComparer.Ordinal)
    {
        "stand", "move", "curve", "strafe", "state", "attack", "block_impulse", "hit",
        "vitals", "death", "respawned", "ranged", "appearance", "bow_equip"
    };
    private static readonly string[] BotOptions = ["--test-bot", "--self-test", "--origin", "--bow-class", "--tables-pak", "--duration", "--wire-version"];
    private static readonly string[] BotValueOptions =
        ["--scenario", "--host", "--port", "--name", "--origin", "--bow-class", "--tables-pak", "--duration", "--wire-version"];

    private sealed record BotPlan(IReadOnlyList<Step> Steps, double End);
    private sealed record BotContext(byte Host, uint World, (float X, float Y, float Z)? Origin);
    private sealed record BotOptionsParsed(string? Scenario, IPAddress Address, int Port, string Name, TimeSpan Duration,
        (float X, float Y, float Z)? Origin, string? Tables, Guid Bow, string WireVersion);
    private sealed class BotRoleException() : IOException("Start the host before the T4 joiner.");

    private static uint PlannedHostMs(uint start, double seconds) => unchecked(start + (uint)(seconds * 1000));
    private static int ScheduledAgeMs(uint planned, uint actual) => Math.Max(0, unchecked((int)(actual - planned)));

    private static float Finite(string text) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) && float.IsFinite(value)
            ? value : throw new FormatException("Expected a finite number.");

    private static double Time(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value)
            ? value : throw new FormatException("Expected a finite time.");

    private static string BotWireVersion(string wire)
    {
        var (version, build) = BuildId.Split(wire);
        if (!string.Equals(version, BuildId.Split(ReleaseVersionInfo.Wire).Version, StringComparison.Ordinal) ||
            (build.Length != 0 && (!BuildId.IsBuildId(build) || build.Any(char.IsControl))) ||
            !string.Equals(wire, BuildId.Wire(version, build), StringComparison.Ordinal))
            throw new FormatException("Wire version must name this release and use an optional canonical build ID.");
        return wire;
    }

    private static BotOptionsParsed ParseBotOptions(string[] args)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int index = 0; index < args.Length; index++)
        {
            string key = args[index];
            if (key == "--test-bot") continue;
            if (!BotValueOptions.Contains(key) || index + 1 == args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal)
                || !fields.TryAdd(key, args[++index]))
                throw new FormatException("Unknown, duplicate or incomplete T4 option.");
        }

        string address = fields.GetValueOrDefault("--host", "127.0.0.1");
        if (!IPAddress.TryParse(address, out var loopback) || !IPAddress.IsLoopback(loopback))
            throw new FormatException("T4 requires a numeric loopback relay address.");
        if (!int.TryParse(fields.GetValueOrDefault("--port", "7778"), NumberStyles.None, CultureInfo.InvariantCulture, out int port) ||
            port is < 1 or > 65535)
            throw new FormatException("Invalid relay port.");
        string name = fields.GetValueOrDefault("--name", "t4-bot-joiner");
        if (name.Length is < 1 or > 32 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw new FormatException("Bot name requires 1–32 ASCII letters, digits, hyphens or underscores.");
        double duration = Time(fields.GetValueOrDefault("--duration", "300"));
        if (duration is < 1 or > 900) throw new FormatException("Duration must be 1–900 seconds.");

        (float X, float Y, float Z)? origin = null;
        if (fields.GetValueOrDefault("--origin") is string position)
        {
            var coordinates = position.Split(',');
            if (coordinates.Length != 3) throw new FormatException("Origin must be x,y,z.");
            origin = (Finite(coordinates[0]), Finite(coordinates[1]), Finite(coordinates[2]));
        }
        Guid bow = Guid.Empty;
        if (fields.GetValueOrDefault("--bow-class") is string item && (!Guid.TryParseExact(item, "D", out bow) || bow == Guid.Empty))
            throw new FormatException("Bow class must be a non-empty GUID.");
        return new(fields.GetValueOrDefault("--scenario"), loopback, port, name, TimeSpan.FromSeconds(duration), origin,
            fields.GetValueOrDefault("--tables-pak"), bow,
            BotWireVersion(fields.GetValueOrDefault("--wire-version", ReleaseVersionInfo.Wire)));
    }

    private static Guid FindBotBow(BotOptionsParsed options)
    {
        string? tables = options.Tables;
        if (tables is null && Environment.GetEnvironmentVariable("KCD2MP_INSTALL") is { Length: > 0 } root)
        {
            string configured = Path.Combine(root, "Data", "Tables.pak");
            if (File.Exists(configured)) tables = configured;
        }
        if (options.Bow != Guid.Empty && tables is null) return options.Bow;
        tables ??= WeaponSwingCatalog.FindTablesPak();
        if (tables is null) throw new InvalidOperationException("Set --tables-pak or --bow-class for the ranged station.");
        var catalog = RangedItemCatalog.Read(tables);
        if (options.Bow != Guid.Empty)
        {
            if (catalog.Kind(options.Bow) != RangedWeaponKind.Bow) throw new InvalidOperationException("The chosen class is not a bow in Tables.pak.");
            return options.Bow;
        }
        foreach (var key in Wo201ItemModels.Read(tables).Keys.Order(StringComparer.Ordinal))
            if (Guid.TryParse(key, out Guid item) && catalog.Kind(item) == RangedWeaponKind.Bow)
                return item;
        throw new InvalidOperationException("Tables.pak contains no usable bow class.");
    }

    private static BotPlan ReadBotPlan(IEnumerable<string> lines)
    {
        var steps = new List<Step>();
        double? end = null;
        foreach (var raw in lines)
        {
            var text = raw.Trim();
            if (text.Length == 0 || text.StartsWith('#')) continue;
            if (text.Length > 512 || steps.Count >= 4096 || end.HasValue) throw new FormatException("T4 scenario exceeds its limits or has data after end.");
            var fields = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 2 && fields[0] == "end") { end = Time(fields[1]); continue; }
            if (fields.Length < 3 || fields[0] != "at" || !BotVerbs.Contains(fields[2]))
                throw new FormatException("Invalid T4 scenario verb or time header.");
            double time = Time(fields[1]);
            string[] args = fields[2..];
            ValidateBotStep(args);
            if (time < 0 || (steps.Count > 0 && time < steps[^1].T))
                throw new FormatException("T4 scenario times must be non-negative and ordered.");
            steps.Add(new(time, args));
        }
        if (steps.Count == 0 || steps[0].T != 0 || steps[0].F[0] != "stand" ||
            end is not (> 0 and <= 600) || steps.Any(s => s.T >= end))
            throw new FormatException("T4 scenario must begin with at 0 stand and have an explicit end after all steps.");
        return new(steps, end.Value);
    }

    private static void ValidateBotStep(string[] f)
    {
        bool Count(int min, int max) => f.Length >= min && f.Length <= max;
        void Values(int begin, int end)
        {
            for (int i = begin; i < end; i++) _ = Finite(f[i]);
        }
        switch (f[0])
        {
            case "stand" when Count(5, 5): Values(1, 5); return;
            case "respawned" when Count(4, 4): Values(1, 4); return;
            case "move" when Count(4, 5):
            case "curve" when Count(5, 5):
            case "strafe" when Count(4, 4):
                Values(1, f.Length);
                if (Finite(f[1]) is > 0 and <= 20 && Finite(f[3]) is > 0 and <= 120) return;
                break;
            case "state" when Count(2, 8):
                if (f.Skip(1).All(s => s is "crouch=0" or "crouch=1" or "combat=0" or "combat=1" or
                    "block=0" or "block=1" or "locked=0" or "locked=1" or "downed=0" or "downed=1")) return;
                break;
            case "attack" when Count(2, 3):
            case "block_impulse" when Count(2, 3):
                if (Guid.TryParseExact(f[1], "D", out Guid row) && row != Guid.Empty) return;
                break;
            case "hit" when Count(3, 4):
            case "vitals" when Count(3, 4):
                if (Finite(f[1]) is >= 0 and <= 100 && Finite(f[2]) is >= 0 and <= 100 &&
                    (f.Length == 3 || (f[0] == "hit" ? f[3] == "unarmed" : f[3] is "downed" or "knockeddown"))) return;
                break;
            case "death" or "bow_equip" when Count(1, 1): return;
            case "ranged" when Count(2, 2): _ = BotRangedPhase(f[1]); return;
            case "appearance" when Count(2, 2):
                if (f[1].Split(',').All(s => Guid.TryParseExact(s, "D", out Guid id) && id != Guid.Empty)) return;
                break;
        }
        throw new FormatException("Invalid parameters for a T4 scenario step.");
    }

    private static byte BotRangedPhase(string phase) => phase switch
    {
        "charge" => 1, "aim" => 2, "fire" => 3, "reload" => 4, "uncharge" => 5,
        _ => throw new FormatException("Unknown ranged phase.")
    };

    private static BotPlan DefaultBotPlan((float X, float Y, float Z) p) => ReadBotPlan(FormattableString.Invariant(
        $"""
        at 0 stand {p.X:R} {p.Y:R} {p.Z:R} 0
        at 1 vitals 100 100
        at 2 move 1.3 0 3
        at 5 move 1.3 0 0.4 {p.Z + 0.15f:R}
        at 5.4 move 1.3 0 0.6
        at 6 move 1.3 0 0.4 {p.Z + 0.30f:R}
        at 6.4 move 1.3 0 0.6
        at 7 move 1.3 0 0.4 {p.Z + 0.45f:R}
        at 8.5 curve 2.7 0 4 1.5708
        at 13 curve 4.4 1.5708 3 3.1416
        at 17 state crouch=1
        at 18 move 0.65 3.1416 3
        at 22 state crouch=0 combat=1
        at 24 attack {BotSwingRow:D}
        at 26 state block=1
        at 28 state block=0
        at 30 hit 1 2 unarmed
        at 32 vitals 25 20 knockeddown
        at 32 state combat=0 downed=1
        at 36 vitals 70 70
        at 36 state downed=0
        at 39 vitals 0 0 downed
        at 39 state downed=1
        at 39 death
        at 43 respawned {p.X:R} {p.Y:R} {p.Z:R}
        at 43 state downed=0
        at 47 bow_equip
        at 50 ranged charge
        at 52 ranged aim
        at 54 ranged fire
        at 56 ranged reload
        end 60
        """).ReplaceLineEndings("\n").Split('\n'));

    private static byte[] BuildBotRanged(byte host, uint world, ulong epoch, uint sequence, byte phase, Guid bow, uint age) =>
        new LootMsg(Protocol.W164RangedStep, 0u,
            new RangedStepV2(world, 1, epoch, sequence, phase, RangedWeaponKind.Bow, bow, age, 1).Encode())
        .BuildUp(Protocol.W164Up, host);

    private static byte[] BotEpochAck(uint world, uint join) =>
        new LootMsg(Protocol.W203Epoch, 0u, Wo206Rules.EpochText(world, join, Wo206Rules.Ack))
            .BuildUp(Protocol.W203Up, Protocol.JoinTargetHost);

    private static async Task<BotContext> PrepareBotAsync(NetworkStream stream, byte myId, bool needsOrigin, CancellationToken token)
    {
        string staging = Directory.CreateTempSubdirectory("kcdmp-t4-").FullName;
        WorldReceiver? receiver = null;
        uint joinId = (uint)Random.Shared.Next(1, int.MaxValue);
        bool requested = false, ready = false, resumed = false;
        byte? host = null;
        var epochs = new Dictionary<byte, (uint World, uint Join)>();
        var positions = new Dictionary<byte, (float X, float Y, float Z)>();
        try
        {
            // Keep one pending frame read across heartbeat ticks, including partial headers/chunks.
            var read = ReadPacket(stream, token);
            long lastHeartbeat = Stopwatch.GetTimestamp();
            while (!token.IsCancellationRequested)
            {
                if (requested && Stopwatch.GetElapsedTime(lastHeartbeat).TotalSeconds >= 2)
                {
                    await Send(stream, PositionCodec.BuildPosition(0, 0, 0, 0, false, true, null), token);
                    lastHeartbeat = Stopwatch.GetTimestamp();
                }
                if (!read.IsCompleted && await Task.WhenAny(read, Task.Delay(100, token)) != read) continue;
                var (type, payload) = await read;
                if (type == Protocol.CombatRole && payload.Length == 1)
                {
                    if (payload[0] != 0) throw new BotRoleException();
                    if (!requested)
                    {
                        await Send(stream, WorldReceiver.BuildRequest(joinId), token);
                        requested = true;
                    }
                }
                else if (Wo206Rules.TryReadEpochFrame(type, payload, out byte source, out uint world, out uint join, out string why)
                    && world != 0 && source != myId && why != Wo206Rules.Ack)
                {
                    if (source == host && receiver is not null && receiver.HostWorldEpoch != world)
                        throw new IOException("Host changed world during the T4 join.");
                    epochs[source] = (world, join);
                    if (source == host) await Send(stream, BotEpochAck(world, join), token);
                }
                else if (type == Protocol.Ghost && PositionCodec.TryDecodeGhost(payload, out var ghost) && ghost.GhostId != myId &&
                    !ghost.IsStale && float.IsFinite(ghost.X) && float.IsFinite(ghost.Y) && float.IsFinite(ghost.Z))
                    positions[ghost.GhostId] = (ghost.X + 3, ghost.Y, ghost.Z);
                else if (Protocol.IsJoinDown(type, payload.Length) && payload[1] == myId &&
                    BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(2)) == joinId && payload[0] != myId)
                {
                    byte from = payload[0];
                    if (host is byte expected && expected != from) throw new IOException("T4 join sender changed.");
                    if (host is null && epochs.TryGetValue(from, out var known))
                        await Send(stream, BotEpochAck(known.World, known.Join), token);
                    host = from;
                    byte[] body = payload[(1 + Protocol.JoinHeaderLen)..];
                    if (type == Protocol.JoinAbortDown) throw new IOException("Host aborted the T4 join.");
                    if (type == Protocol.JoinStatusDown && JoinStatusCodec.TryDecode(body, out byte state, out byte reason, out _))
                    {
                        if (state == Protocol.JoinStateRefused) throw new IOException("Host refused the T4 join.");
                        if (state == Protocol.JoinStateResumed)
                        {
                            if (!ready || reason != Protocol.JoinReasonId("ready"))
                                throw new IOException("Host did not resume the verified T4 join.");
                            resumed = true;
                        }
                    }
                    else if (type == Protocol.WorldOfferDown)
                    {
                        if (receiver is not null || !epochs.TryGetValue(from, out var offered))
                            throw new IOException("Duplicate T4 offer or missing host world epoch.");
                        var offer = WorldOffer.TryDecode(body, out _)
                            ?? throw new IOException("Invalid T4 world offer.");
                        receiver = new WorldReceiver(staging, joinId, from, offer) { HostWorldEpoch = offered.World };
                    }
                    else if (type == Protocol.WorldChunkDown)
                    {
                        if (receiver is null || body.Length < 4) throw new IOException("World chunk before valid T4 offer.");
                        var result = receiver.Accept(BinaryPrimitives.ReadUInt32LittleEndian(body), body.AsSpan(4), out _);
                        if (result == WorldReceiver.ChunkResult.Error) throw new IOException("Invalid T4 world chunk.");
                        if (result is WorldReceiver.ChunkResult.AckDue or WorldReceiver.ChunkResult.Complete)
                            await Send(stream, receiver.BuildAck(), token);
                        if (result == WorldReceiver.ChunkResult.Complete)
                        {
                            var (ok, _, _) = receiver.Finish();
                            if (!ok) throw new IOException("T4 save verification failed.");
                            await Send(stream, receiver.BuildDone(), token);
                            await Send(stream, WorldReceiver.BuildReady(joinId, receiver.Offer.WorldSavedSeq), token);
                            ready = true;
                        }
                    }
                }

                if (resumed && receiver is not null && host is byte accepted &&
                    (!needsOrigin || positions.ContainsKey(accepted)))
                    return new(accepted, receiver.HostWorldEpoch, positions.TryGetValue(accepted, out var at) ? at : null);
                read = ReadPacket(stream, token);
            }
            token.ThrowIfCancellationRequested();
            throw new IOException("T4 join did not complete.");
        }
        catch (Exception ex) when (requested && !resumed && !token.IsCancellationRequested && ex is IOException)
        {
            try { await Send(stream, WorldReceiver.BuildAbort(Protocol.JoinTargetHost, joinId, Protocol.JoinAbortProtocol), token); }
            catch (IOException) { }
            throw;
        }
        finally
        {
            receiver?.Abort();
            receiver?.Dispose();
            Directory.Delete(staging);
        }
    }

    private static int BotSelfTest()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var plan = DefaultBotPlan((132.5f, -220, 23));
            var bow = Guid.Parse("10000000-0000-0000-0000-000000000009");
            var packet = BuildBotRanged(0, 42, 2, 7, 3, bow, 11);
            if (plan.End != 60 || plan.Steps.Count(s => s.F[0] == "curve") != 2 ||
                !new[] { "hit", "vitals", "death", "respawned", "ranged", "attack" }
                    .All(verb => plan.Steps.Any(s => s.F[0] == verb))
                || !plan.Steps.Any(s => s.F.SequenceEqual(new[] { "state", "block=1" }))
                || plan.Steps.Single(s => s.F.SequenceEqual(new[] { "ranged", "fire" })).T != 54 ||
                packet[0] != Protocol.W164Up || packet[3] != 0 ||
                !LootMsg.TryDecode(packet.AsSpan(3 + Protocol.JoinHeaderLen), out var msg) || msg.Kind != Protocol.W164RangedStep ||
                !RangedStepV2.TryDecode(msg.Text, out var step) || step is not { World: 42, Epoch: 2, Sequence: 7, Phase: 3, AgeMs: 11 }
                || step.ItemClass != bow || step.Kind != RangedWeaponKind.Bow
                || PlannedHostMs(uint.MaxValue - 499, 0.5) != 0 || ScheduledAgeMs(uint.MaxValue - 5, 10) != 16)
                throw new InvalidOperationException("T4 joiner plan or packet self-test failed.");
            try
            {
                _ = ReadBotPlan(["at 0 stand 0 0 0 0", "at 0.1 appearance mirror", "end 1"]);
                throw new InvalidOperationException("T4 plans must not depend on the host's appearance.");
            }
            catch (FormatException) { }
            string localVersion = BuildId.Split(ReleaseVersionInfo.Wire).Version;
            string testWire = BuildId.Wire(localVersion, "t4-fixture-0000000000");
            if (ParseBotOptions(["--test-bot"]).WireVersion != ReleaseVersionInfo.Wire ||
                ParseBotOptions(["--test-bot", "--wire-version", testWire]).WireVersion != testWire)
                throw new InvalidOperationException("T4 bot must send the staged wire release.");
            try
            {
                _ = BotWireVersion(localVersion + "+invalid build");
                throw new InvalidOperationException("T4 must reject invalid artifact build identifiers.");
            }
            catch (FormatException) { }
            Console.WriteLine("BOT-T4 role=joiner selftest=PASS");
            return 0;
        }
        finally { CultureInfo.CurrentCulture = original; }
    }
}
