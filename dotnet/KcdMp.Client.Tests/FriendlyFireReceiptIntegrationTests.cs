// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Threading.Channels;
using KcdMp.Wire;

namespace KcdMp.Client.Tests;

[CollectionDefinition("Friendly-fire receipts", DisableParallelization = true)]
public sealed class FriendlyFireReceiptCollection { }

[Collection("Friendly-fire receipts")]
public sealed class FriendlyFireReceiptIntegrationTests
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    [Fact]
    public async Task Pvp_notice_already_read_by_pipe_does_not_follow_the_same_entity_into_a_new_life()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = stop.Token;
        string name = "ff-" + Guid.NewGuid().ToString("N")[..16];
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var pipe = new CombatPipe(name);
        var listen = server.WaitForConnectionAsync(ct);
        Assert.True(await pipe.EnsureConnectedAsync(ct));
        await listen;

        using var wire = new HeldPeerStream();
        var bridge = BridgeOn(pipe, wire);
        typeof(GameBridge).GetField("_t3StatsNextMs", Instance)!.SetValue(bridge, long.MaxValue);
        typeof(GameBridge).GetMethod("Wo121OnConnect", Instance)!.Invoke(bridge, [wire, ct]);
        const byte victim = 2;
        const uint entity = 222;
        const ulong incarnation = 43, formerLife = 1001, currentLife = 1002;
        var entities = (System.Collections.Concurrent.ConcurrentDictionary<string, uint>)
            typeof(GameBridge).GetField("_ghostEntityIds", Instance)!.GetValue(bridge)!;
        var versions = (System.Collections.Concurrent.ConcurrentDictionary<byte, string>)
            typeof(GameBridge).GetField("_ghostReleaseVersions", Instance)!.GetValue(bridge)!;
        entities[victim.ToString()] = entity;

        var workerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workerRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workerDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pipe.OnLocalAction = _ =>
        {
            if (workerEntered.TrySetResult()) return workerRelease.Task.WaitAsync(ct);
            workerDrained.TrySetResult();
            return Task.CompletedTask;
        };
        var receive = (Task)typeof(GameBridge).GetMethod("ReceiveLoopAsync", Instance)!.Invoke(bridge, [wire, ct])!;

        async Task UpdateRosterAsync(ulong revision, ulong life, byte marker)
        {
            byte[] roster = new byte[Protocol.PvpPeerLifeLen];
            roster[0] = victim;
            BinaryPrimitives.WriteUInt64LittleEndian(roster.AsSpan(1), incarnation);
            new PvpLifeState(revision, life).Write(roster.AsSpan(Protocol.PvpPeerLen));
            wire.Feed(Protocol.PvpPeerDown, roster);
            wire.Feed(Protocol.ReleaseVersion, [victim, marker]);
            while (!versions.TryGetValue(victim, out var version) || version != ((char)marker).ToString())
                await Task.Delay(5, ct);
        }

        ValueTask SendHitAsync(float damage, ulong id)
        {
            ulong tick = (ulong)Environment.TickCount64;
            byte[] hit = NativeContact(entity, 0, damage, tick, tick, FixtureLife, id);
            BinaryPrimitives.WriteSingleLittleEndian(hit.AsSpan(7), 0);
            hit[16] = 0;
            return server.WriteAsync(hit, ct);
        }

        var targetAtRead = new TaskCompletionSource<GameBridge.PvpHitTarget?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var capture = pipe.CapturePvpHitTarget!;
        pipe.CapturePvpHitTarget = eid =>
        {
            var captured = capture(eid);
            targetAtRead.TrySetResult(captured);
            return captured;
        };

        byte[] barrier = [0x96, 27, 0, .. new byte[27]];
        try
        {
            await NativeLifeAsync(pipe, server, FixtureLife, ct);
            await UpdateRosterAsync(1, formerLife, (byte)'a');
            await server.WriteAsync(barrier, ct);
            await workerEntered.Task.WaitAsync(ct);
            await SendHitAsync(5, 1);
            Assert.Equal(formerLife, (await targetAtRead.Task.WaitAsync(ct))?.Life);
            await UpdateRosterAsync(2, currentLife, (byte)'b');
            await Task.Delay(2, ct);
            await SendHitAsync(7, 2);
            await server.WriteAsync(barrier, ct);
            workerRelease.TrySetResult();
            await workerDrained.Task.WaitAsync(ct);

            var sends = wire.UpstreamHits.ToArray();
            var evidence = sends.Select(frame => $"hp={BinaryPrimitives.ReadSingleLittleEndian(frame.AsSpan(8))} " +
                $"life={BinaryPrimitives.ReadUInt64LittleEndian(frame.AsSpan(3 + PlayerHitV8.IdentifiedUpLen))}");
            Assert.True(sends.Length == 1, $"Expected only the fresh hit. Relay received [{string.Join(", ", evidence)}].");
            byte[] frame = Assert.Single(sends);
            Assert.Equal(new PlayerHitV8(victim, 0, 7, 0, 0).BuildUp(1, incarnation, currentLife), frame);
        }
        finally
        {
            workerRelease.TrySetResult();
            stop.Cancel();
            await receive;
        }
    }

    [Fact]
    public async Task Contact_held_outside_reader_before_respawn_never_reaches_native_after_respawn_on_same_stream()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = stop.Token;
        string name = "ff-" + Guid.NewGuid().ToString("N")[..16];
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var pipe = new CombatPipe(name);
        var listen = server.WaitForConnectionAsync(ct);
        Assert.True(await pipe.EnsureConnectedAsync(ct));
        await listen;

        using var wire = new HeldPeerStream();
        var bridge = BridgeOn(pipe, wire);
        typeof(GameBridge).GetField("_t3StatsNextMs", Instance)!.SetValue(bridge, long.MaxValue);
        const ulong formerLife = 1001, currentLife = 1002;
        await NativeLifeAsync(pipe, server, formerLife, ct);
        var old = Contact(17, 43, formerLife);
        var ready = Contact(18, 43, currentLife);
        var receive = (Task)typeof(GameBridge).GetMethod("ReceiveLoopAsync", Instance)!.Invoke(bridge, [wire, ct])!;
        try
        {
            await wire.ReaderWaiting.Task.WaitAsync(ct);
            var down = typeof(GameBridge).GetMethod("OnLocalDownedAsync", Instance)!;
            await (Task)down.Invoke(bridge, [true, Protocol.RespawnReasonKnockdown])!;
            await (Task)down.Invoke(bridge, [false, Protocol.RespawnReasonKnockdown])!;
            await NativeLifeAsync(pipe, server, currentLife, ct);
            Assert.Equal(0, wire.BytesRead);
            wire.Feed(Protocol.PlayerHitV8Down, old);
            wire.Feed(Protocol.PlayerHitV8Down, ready);
            wire.Feed(Protocol.PlayerHitV8Down, ready);
            wire.Feed(Protocol.PlayerHitV8Down, Contact(19, 43, currentLife));
            wire.Feed(Protocol.ReleaseVersion, [2, (byte)'v']);
            float hp = 95, st = 85;

            byte[] first = await ReadHitAsync(server, ct);
            Assert.Equal(18UL, BinaryPrimitives.ReadUInt64LittleEndian(first.AsSpan(10)));
            Assert.Equal(currentLife, BinaryPrimitives.ReadUInt64LittleEndian(first.AsSpan(34)));
            Assert.Equal((95f, 85f), (hp, st));
            hp -= BinaryPrimitives.ReadSingleLittleEndian(first.AsSpan(4));
            st -= BinaryPrimitives.ReadSingleLittleEndian(first);
            await server.WriteAsync(new byte[] { 0x81, 3, 0, 1, 1, 24 }, ct);
            Assert.Equal((88f, 75f), (hp, st));

            byte[] protectedHit = await ReadHitAsync(server, ct);
            Assert.Equal(19UL, BinaryPrimitives.ReadUInt64LittleEndian(protectedHit.AsSpan(10)));
            Assert.Equal(currentLife, BinaryPrimitives.ReadUInt64LittleEndian(protectedHit.AsSpan(34)));
            await server.WriteAsync(new byte[] { 0x81, 3, 0, 1, 2, 21 }, ct);
            var processed = (System.Collections.Concurrent.ConcurrentDictionary<byte, string>)
                typeof(GameBridge).GetField("_ghostReleaseVersions", Instance)!.GetValue(bridge)!;
            while (!processed.ContainsKey(2)) await Task.Delay(5, ct);
            Assert.Equal((88f, 75f), (hp, st));
        }
        finally
        {
            stop.Cancel();
            await receive;
        }
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData(PlayerHitV8.FlagMissile)]
    public async Task Native_contact_held_before_callback_cannot_adopt_new_target_life(byte flags)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = stop.Token;
        string name = "ff-source-" + Guid.NewGuid().ToString("N")[..16];
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var pipe = new CombatPipe(name);
        var listen = server.WaitForConnectionAsync(ct);
        Assert.True(await pipe.EnsureConnectedAsync(ct));
        await listen;

        using var wire = new HeldPeerStream();
        var bridge = BridgeOn(pipe, wire);
        typeof(GameBridge).GetField("_t3StatsNextMs", Instance)!.SetValue(bridge, long.MaxValue);
        typeof(GameBridge).GetMethod("Wo121OnConnect", Instance)!.Invoke(bridge, [wire, ct]);
        const byte ghost = 2;
        const uint eid = 0x2345;
        const ulong incarnation = 43, formerLife = 1001, currentLife = 1002, sourceLife = 9001;
        var entities = (ConcurrentDictionary<string, uint>)typeof(GameBridge).GetField("_ghostEntityIds", Instance)!.GetValue(bridge)!;
        entities["2"] = eid;
        var markers = (ConcurrentDictionary<byte, string>)typeof(GameBridge).GetField("_ghostReleaseVersions", Instance)!.GetValue(bridge)!;
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unblock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldProcessed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var freshProcessed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Let real pipe events queue without replacing the combat callback under test.
        pipe.OnLocalDowned = (_, marker) =>
        {
            if (marker == 1)
            {
                blocked.TrySetResult();
                return unblock.Task.WaitAsync(ct);
            }
            (marker == 2 ? oldProcessed : freshProcessed).TrySetResult();
            return Task.CompletedTask;
        };
        string targetName = "ff-target-" + Guid.NewGuid().ToString("N")[..16];
        using var targetServer = new NamedPipeServerStream(targetName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var targetPipe = new CombatPipe(targetName);
        var targetListen = targetServer.WaitForConnectionAsync(ct);
        Assert.True(await targetPipe.EnsureConnectedAsync(ct));
        await targetListen;
        using var targetWire = new HeldPeerStream();
        var targetBridge = BridgeOn(targetPipe, targetWire, ownGhost: ghost);
        typeof(GameBridge).GetField("_t3StatsNextMs", Instance)!.SetValue(targetBridge, long.MaxValue);
        var targetMarkers = (ConcurrentDictionary<byte, string>)typeof(GameBridge).GetField("_ghostReleaseVersions", Instance)!.GetValue(targetBridge)!;
        var reader = typeof(GameBridge).GetMethod("ReceiveLoopAsync", Instance)!;
        var receive = (Task)reader.Invoke(bridge, [wire, ct])!;
        var targetReceive = (Task)reader.Invoke(targetBridge, [targetWire, ct])!;
        try
        {
            await wire.ReaderWaiting.Task.WaitAsync(ct);
            await targetWire.ReaderWaiting.Task.WaitAsync(ct);
            await NativeLifeAsync(pipe, server, sourceLife, ct);
            await NativeLifeAsync(targetPipe, targetServer, currentLife, ct);
            await AnnounceTargetAsync(1, formerLife, 'a');
            await server.WriteAsync(new byte[] { 0x91, 2, 0, 1, 1 }, ct);
            await blocked.Task.WaitAsync(ct);
            ulong captured = (ulong)Environment.TickCount64;
            // Exercise the former encoding against the unmodified baseline and ensure it cannot bypass the new fence.
            await server.WriteAsync(NativeContact(eid, flags, health: 7), ct);
            while ((ulong)Environment.TickCount64 <= captured) await Task.Delay(1, ct);
            await AnnounceTargetAsync(2, currentLife, 'b');
            Assert.Equal(eid, entities["2"]);
            Assert.Equal(sourceLife, pipe.PvpNativeLife);
            // The original impact preceded publication. The measurement and callback can finish after it.
            await server.WriteAsync(NativeContact(eid, flags, health: 7, captured,
                (ulong)Environment.TickCount64, sourceLife, measurement: 17), ct);
            await server.WriteAsync(new byte[] { 0x91, 2, 0, 0, 2 }, ct);
            unblock.SetResult();
            await oldProcessed.Task.WaitAsync(ct);
            var stale = wire.UpstreamHits.ToArray();
            var firstApply = ReadHitAsync(targetServer, ct);
            foreach (byte[] premature in stale) ForwardToTarget(premature);
            Assert.True(stale.Length == 0, stale.Length == 0 ? "" :
                $"Old native contact was sent on the same ghost/body as {stale.Length} new upstream hit(s), " +
                $"first recipient life={BinaryPrimitives.ReadUInt64LittleEndian(stale[0].AsSpan(3 + PlayerHitV8.IdentifiedUpLen))}.");
            await AssertNoApplyBeforeMarkerAsync(firstApply, 'a');

            ulong publishedBy = (ulong)Environment.TickCount64;
            while ((ulong)Environment.TickCount64 <= publishedBy) await Task.Delay(1, ct);
            ulong freshTick = (ulong)Environment.TickCount64;
            byte[] fresh = NativeContact(eid, flags, health: 13, freshTick, freshTick, sourceLife, measurement: 18);
            await server.WriteAsync(fresh, ct);
            await server.WriteAsync(fresh, ct);
            await server.WriteAsync(new byte[] { 0x91, 2, 0, 0, 3 }, ct);
            await freshProcessed.Task.WaitAsync(ct);
            byte[] packet = Assert.Single(wire.UpstreamHits);
            Assert.Equal((byte)2, packet[3]);
            Assert.Equal(13f, BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(8)));
            Assert.Equal(flags, packet[12]);
            Assert.Equal(currentLife, BinaryPrimitives.ReadUInt64LittleEndian(packet.AsSpan(3 + PlayerHitV8.IdentifiedUpLen)));

            ForwardToTarget(packet);
            ForwardToTarget(packet);
            byte[] apply = await firstApply;
            Assert.Equal(42, apply.Length);
            Assert.Equal(13f, BinaryPrimitives.ReadSingleLittleEndian(apply.AsSpan(4)));
            Assert.Equal(flags, (byte)(apply[8] & 0x7F));
            Assert.Equal(currentLife, BinaryPrimitives.ReadUInt64LittleEndian(apply.AsSpan(34)));
            await targetServer.WriteAsync(new byte[] { 0x81, 3, 0, 1, 1, 24 }, ct);
            await AssertNoApplyBeforeMarkerAsync(ReadHitAsync(targetServer, ct), 'b');
        }
        finally
        {
            stop.Cancel();
            unblock.TrySetResult();
            await Task.WhenAll(receive, targetReceive);
        }

        // Match the relay's source prefix and authenticated-incarnation rewrite.
        void ForwardToTarget(byte[] upstream)
        {
            var down = new byte[PlayerHitV8.LifeDownLen];
            down[0] = 1;
            upstream.AsSpan(3).CopyTo(down.AsSpan(1));
            BinaryPrimitives.WriteUInt64LittleEndian(down.AsSpan(PlayerHitV8.DownLen + sizeof(ulong)), 79);
            targetWire.Feed(Protocol.PlayerHitV8Down, down);
        }

        async Task AssertNoApplyBeforeMarkerAsync(Task<byte[]> apply, char marker)
        {
            targetWire.Feed(Protocol.ReleaseVersion, [1, (byte)marker]);
            while (targetMarkers.GetValueOrDefault((byte)1) != marker.ToString() && !apply.IsCompleted)
                await Task.Delay(5, ct);
            Assert.False(apply.IsCompleted, $"The target sent an unexpected native Apply before marker {marker}.");
        }

        async Task AnnounceTargetAsync(ulong revision, ulong proof, char marker)
        {
            var roster = new byte[Protocol.PvpPeerLifeLen];
            roster[0] = ghost;
            BinaryPrimitives.WriteUInt64LittleEndian(roster.AsSpan(1), incarnation);
            new PvpLifeState(revision, proof).Write(roster.AsSpan(Protocol.PvpPeerLen));
            wire.Feed(Protocol.PvpPeerDown, roster);
            wire.Feed(Protocol.ReleaseVersion, [ghost, (byte)marker]);
            while (markers.GetValueOrDefault(ghost) != marker.ToString()) await Task.Delay(5, ct);
        }
    }

    private static byte[] NativeContact(uint eid, byte flags, float health, ulong captured = 0,
        ulong measured = 0, ulong sourceLife = 0, ulong measurement = 0)
    {
        var frame = new byte[captured == 0 ? 17 : 49];
        frame[0] = 0x97;
        frame[1] = (byte)(frame.Length - 3);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(3), eid);
        BinaryPrimitives.WriteSingleLittleEndian(frame.AsSpan(7), 10f);
        BinaryPrimitives.WriteSingleLittleEndian(frame.AsSpan(11), health);
        frame[15] = flags;
        frame[16] = 3;
        if (captured != 0)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(17), captured);
            BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(25), measured);
            BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(33), sourceLife);
            BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(41), measurement);
        }
        return frame;
    }

    [Fact]
    public async Task Full_contact_notice_queue_leaves_pipe_replies_and_later_notices_live()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = stop.Token;
        string name = "ff-queue-" + Guid.NewGuid().ToString("N")[..16];
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var pipe = new CombatPipe(name);
        var listen = server.WaitForConnectionAsync(ct);
        Assert.True(await pipe.EnsureConnectedAsync(ct));
        await listen;

        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unblock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = new ConcurrentQueue<ulong>();
        pipe.OnLocalDowned = (_, marker) =>
        {
            if (marker == 1)
            {
                blocked.SetResult();
                return unblock.Task.WaitAsync(ct);
            }
            (marker == 2 ? drained : recovered).SetResult();
            return Task.CompletedTask;
        };
        pipe.OnPvpHit = hit =>
        {
            delivered.Enqueue(hit.MeasurementId);
            return Task.CompletedTask;
        };

        const ulong burst = 96, sourceLife = 9001;
        try
        {
            await server.WriteAsync(new byte[] { 0x91, 2, 0, 1, 1 }, ct);
            await blocked.Task.WaitAsync(ct);
            for (ulong id = 1; id <= burst; ++id)
                await server.WriteAsync(NativeContact(0x2345, 0, 7f, 1, 2, sourceLife, id), ct);

            var pong = pipe.PingAsync(ct);
            var ping = new byte[3];
            await server.ReadExactlyAsync(ping, ct);
            Assert.Equal(new byte[] { 0x03, 0, 0 }, ping);
            await server.WriteAsync(new byte[] { 0x83, 0, 0 }, ct);
            Assert.True(await pong.WaitAsync(ct));
            Assert.Empty(delivered);

            await server.WriteAsync(new byte[] { 0x91, 2, 0, 0, 2 }, ct);
            unblock.SetResult();
            await drained.Task.WaitAsync(ct);
            var accepted = Enumerable.Range(1, 64).Select(id => (ulong)id).ToArray();
            Assert.Equal(accepted, delivered.ToArray());

            ulong now = (ulong)Environment.TickCount64;
            await server.WriteAsync(NativeContact(0x2345, 0, 13f, now, now, sourceLife, burst + 1), ct);
            await server.WriteAsync(new byte[] { 0x91, 2, 0, 0, 3 }, ct);
            await recovered.Task.WaitAsync(ct);
            Assert.Equal(accepted.Append(burst + 1), delivered.ToArray());
        }
        finally { unblock.TrySetResult(); }
    }

    [Theory]
    [InlineData((byte)1, (byte)21, "protected_consumed")]
    [InlineData((byte)0, (byte)22, "dispatch_unconfirmed")]
    [InlineData((byte)0, (byte)17, "dispatch_unconfirmed")]
    [InlineData((byte)0, (byte)20, "recipient_retired")]
    [InlineData((byte)0, (byte)23, "recipient_retired")]
    [InlineData((byte)1, (byte)24, "observed")]
    [InlineData((byte)1, (byte)0, "acknowledged")]
    [InlineData((byte)0, (byte)0, "failed")]
    public async Task Agent_distinguishes_consumption_from_a_damage_acknowledgement(byte ok, byte reason, string expected)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = stop.Token;
        string name = "ff-" + Guid.NewGuid().ToString("N")[..16];
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var pipe = new CombatPipe(name);
        var listen = server.WaitForConnectionAsync(ct);
        Assert.True(await pipe.EnsureConnectedAsync(ct));
        await listen;

        var bridge = BridgeOn(pipe);
        var payload = Contact(7, 43);
        using var output = new StringWriter();
        var original = Console.Out;
        Console.SetOut(output);
        try
        {
            long ingress = Environment.TickCount64;
            var work = DeliverAsync(bridge, payload, ct, ingress);
            var request = await ReadHitAsync(server, ct);
            Assert.Equal(42, request.Length);
            Assert.Equal(payload[2..10], request[..8]);
            Assert.Equal((byte)2, request[9]);
            Assert.Equal(payload[PlayerHitV8.DownLen..PlayerHitV8.IdentifiedDownLen], request[10..26]);
            Assert.Equal((ulong)ingress, BinaryPrimitives.ReadUInt64LittleEndian(request.AsSpan(26)));
            Assert.Equal(FixtureLife, BinaryPrimitives.ReadUInt64LittleEndian(request.AsSpan(34)));
            await server.WriteAsync(new byte[] { 0x81, 3, 0, ok, 1, reason }, ct);
            await work.WaitAsync(ct);
        }
        finally { Console.SetOut(original); }

        var line = Assert.Single(output.ToString().Split('\n'), line => line.StartsWith("MP-FF dir=in attacker=2 victim=1 ", StringComparison.Ordinal));
        Assert.Contains($" session=on result={expected} ", line);
        Assert.DoesNotContain("result=applied", line);
    }

    [Theory]
    [InlineData("_myGhostId")]
    [InlineData("_p1Serial")]
    [InlineData("_t3InputLifeRevision")]
    [InlineData("_pvpNativeLife")]
    public async Task Recipient_change_while_waiting_for_writer_suppresses_the_old_request(string fieldName)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = stop.Token;
        string name = "ff-" + Guid.NewGuid().ToString("N")[..16];
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var pipe = new CombatPipe(name);
        var listen = server.WaitForConnectionAsync(ct);
        Assert.True(await pipe.EnsureConnectedAsync(ct));
        await listen;

        var bridge = BridgeOn(pipe);
        var writer = (SemaphoreSlim)typeof(CombatPipe).GetField("_writeLock", Instance)!.GetValue(pipe)!;
        await writer.WaitAsync(ct);
        Task pending;
        try
        {
            pending = DeliverAsync(bridge, Contact(7, 43), ct);
            Assert.False(pending.IsCompleted);
            if (fieldName == "_pvpNativeLife") await NativeLifeAsync(pipe, server, FixtureLife + 1, ct);
            else
            {
                var field = typeof(GameBridge).GetField(fieldName, Instance)!;
                if (fieldName == "_myGhostId") field.SetValue(bridge, (byte)3);
                else field.SetValue(bridge, (long)field.GetValue(bridge)! + 1);
            }
        }
        finally { writer.Release(); }

        var read = ReadHitAsync(server, ct);
        if (await Task.WhenAny(pending, read) == read)
        {
            await read;
            await server.WriteAsync(new byte[] { 0x81, 3, 0, 1, 1, 0 }, ct);
            await pending.WaitAsync(ct);
            Assert.Fail("The former recipient's damage request reached the physical pipe.");
        }
        await pending.WaitAsync(ct);

        var next = pipe.ApplyPvpHitAsync(11, 13, 0, 2, ct);
        var actual = await read;
        Assert.Equal(13f, BinaryPrimitives.ReadSingleLittleEndian(actual.AsSpan(4)));
        await server.WriteAsync(new byte[] { 0x81, 3, 0, 1, 1, 0 }, ct);
        Assert.True((await next.WaitAsync(ct)).Ok);
    }

    [Fact]
    public async Task Duplicate_and_wrong_sequence_receipts_never_complete_the_next_contact()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = stop.Token;
        string name = "ff-" + Guid.NewGuid().ToString("N")[..16];
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var pipe = new CombatPipe(name);
        var listen = server.WaitForConnectionAsync(ct);
        Assert.True(await pipe.EnsureConnectedAsync(ct));
        await listen;

        var first = pipe.ApplyPvpHitAsync(11, 13, 0, 2, ct);
        await ReadHitAsync(server, ct);
        await server.WriteAsync(new byte[] { 0x81, 3, 0, 0, 1, 22 }, ct);
        Assert.Equal((PipeReason)22, (await first.WaitAsync(ct)).Reason);

        var next = pipe.ApplyPvpHitAsync(11, 13, 0, 2, ct);
        await ReadHitAsync(server, ct);
        await server.WriteAsync(new byte[] {
            0x81, 3, 0, 1, 1, 24,  // a late positive reply for the first command
            0x81, 3, 0, 1, 3, 24,  // a positive reply on the wrong sequence
            0x81, 3, 0, 1, 2, 21,  // the second command was protected
        }, ct);
        var actual = await next.WaitAsync(ct);
        Assert.Equal(new PipeResult(true, (PipeReason)21), actual);
        Assert.Equal(2L, pipe.StaleRepliesDropped);
    }

    [Fact]
    public async Task Identified_contact_is_spent_before_a_lost_result_and_a_new_source_can_restart_at_one()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = stop.Token;
        string name = "ff-" + Guid.NewGuid().ToString("N")[..16];
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var pipe = new CombatPipe(name);
        var listen = server.WaitForConnectionAsync(ct);
        Assert.True(await pipe.EnsureConnectedAsync(ct));
        await listen;
        var bridge = BridgeOn(pipe);

        var first = Contact(17, 43);
        var pending = DeliverAsync(bridge, first, ct);
        Assert.Equal(first[PlayerHitV8.DownLen..PlayerHitV8.IdentifiedDownLen], (await ReadHitAsync(server, ct))[10..26]);
        await server.WriteAsync(new byte[] { 0x81, 3, 0, 0, 1, 22 }, ct);
        await pending.WaitAsync(ct);

        await DeliverAsync(bridge, first, ct).WaitAsync(TimeSpan.FromSeconds(1), ct);
        await DeliverAsync(bridge, first[..PlayerHitV8.DownLen], ct).WaitAsync(TimeSpan.FromSeconds(1), ct);

        var relocated = Contact(1, 44);
        var next = DeliverAsync(bridge, relocated, ct);
        Assert.Equal(relocated[PlayerHitV8.DownLen..PlayerHitV8.IdentifiedDownLen], (await ReadHitAsync(server, ct))[10..26]);
        await server.WriteAsync(new byte[] { 0x81, 3, 0, 1, 2, 24 }, ct);
        await next.WaitAsync(ct);

        await DeliverAsync(bridge, Contact(18, 43), ct).WaitAsync(TimeSpan.FromSeconds(1), ct);
        var separate = Contact(2, 44);
        var other = DeliverAsync(bridge, separate, ct);
        Assert.Equal(separate[PlayerHitV8.DownLen..PlayerHitV8.IdentifiedDownLen], (await ReadHitAsync(server, ct))[10..26]);
        await server.WriteAsync(new byte[] { 0x81, 3, 0, 1, 3, 24 }, ct);
        await other.WaitAsync(ct);
    }

    private const ulong FixtureLife = 41;

    private static GameBridge BridgeOn(CombatPipe pipe, Stream? stream = null, byte ownGhost = 1)
    {
        var bridge = new GameBridge(new ClientConfig());
        typeof(GameBridge).GetField("_combat", Instance)!.SetValue(bridge, pipe);
        typeof(GameBridge).GetField("_myGhostId", Instance)!.SetValue(bridge, ownGhost);
        typeof(GameBridge).GetField("_ffSession", Instance)!.SetValue(bridge, true);
        typeof(GameBridge).GetField("_w213Caps", Instance)!.SetValue(bridge, (ushort)(Protocol.LinkCapPvpContact | Protocol.LinkCapPvpLife));
        typeof(CombatPipe).GetField("_pvpNativeLife", Instance)!.SetValue(pipe, FixtureLife);
        var session = typeof(GameBridge).GetNestedType("PvpContactSession", BindingFlags.NonPublic)!;
        typeof(GameBridge).GetField("_wo121ContactSession", Instance)!.SetValue(bridge,
            Activator.CreateInstance(session, Instance, null, [stream ?? Stream.Null], null));
        return bridge;
    }

    private static byte[] Contact(ulong sequence, ulong incarnation, ulong proof = FixtureLife)
    {
        var up = new PlayerHitV8(1, 10, 7, 0, 0).BuildUp(sequence, 99, proof);
        var down = new byte[PlayerHitV8.LifeDownLen];
        down[0] = 2;
        up.AsSpan(3).CopyTo(down.AsSpan(1));
        BinaryPrimitives.WriteUInt64LittleEndian(down.AsSpan(PlayerHitV8.DownLen + sizeof(ulong)), incarnation);
        return down;
    }

    private static Task DeliverAsync(GameBridge bridge, byte[] payload, CancellationToken ct, long? ingressTick = null)
    {
        var frameType = typeof(GameBridge).GetNestedType("InFrame", BindingFlags.NonPublic)!;
        var frame = Activator.CreateInstance(frameType, Instance, null,
            [(int)Protocol.PlayerHitV8Down, payload, Stopwatch.GetTimestamp(), 0u], null)!;
        frameType.GetProperty("PvpContact", Instance)!.SetValue(frame,
            Activator.CreateInstance(typeof(GameBridge).GetNestedType("PvpFrameScope", BindingFlags.NonPublic)!, Instance, null,
                [typeof(GameBridge).GetField("_wo121ContactSession", Instance)!.GetValue(bridge),
                 typeof(GameBridge).GetField("_p1Serial", Instance)!.GetValue(bridge),
                 typeof(GameBridge).GetField("_t3InputLifeRevision", Instance)!.GetValue(bridge),
                 ingressTick ?? Environment.TickCount64,
                 ((CombatPipe)typeof(GameBridge).GetField("_combat", Instance)!.GetValue(bridge)!).PvpNativeLife], null));
        return (Task)typeof(GameBridge).GetMethod("ProcessFrameCoreAsync", Instance)!.Invoke(bridge, [frame, ct, null])!;
    }

    private static async Task<byte[]> ReadHitAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        var header = new byte[3];
        await server.ReadExactlyAsync(header, ct);
        Assert.Equal(0x1A, header[0]);
        int length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(1));
        Assert.True(length is 10 or 34 or 42);
        var request = new byte[length];
        await server.ReadExactlyAsync(request, ct);
        return request;
    }

    private static async Task NativeLifeAsync(CombatPipe pipe, NamedPipeServerStream server, ulong life, CancellationToken ct)
    {
        byte[] frame = [0xBC, 8, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(3), life);
        await server.WriteAsync(frame, ct);
        while (pipe.PvpNativeLife != life) await Task.Delay(5, ct);
    }

    private sealed class HeldPeerStream : Stream
    {
        private readonly Channel<byte[]> _frames = Channel.CreateUnbounded<byte[]>();
        public ConcurrentQueue<byte[]> UpstreamHits { get; } = new();
        private byte[] _frame = [];
        private int _at;
        private long _read;

        public TaskCompletionSource ReaderWaiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long BytesRead => Interlocked.Read(ref _read);
        public void Feed(byte type, byte[] body) =>
            Assert.True(_frames.Writer.TryWrite([type, (byte)body.Length, 0, .. body]));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_at == _frame.Length)
            {
                ReaderWaiting.TrySetResult();
                _frame = await _frames.Reader.ReadAsync(cancellationToken);
                _at = 0;
            }
            int length = Math.Min(buffer.Length, _frame.Length - _at);
            _frame.AsMemory(_at, length).CopyTo(buffer);
            _at += length;
            Interlocked.Add(ref _read, length);
            return length;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count)).GetAwaiter().GetResult();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!buffer.IsEmpty && buffer.Span[0] == Protocol.PlayerHitV8Up) UpstreamHits.Enqueue(buffer.ToArray());
            return ValueTask.CompletedTask;
        }
        public override void Flush() => throw new NotSupportedException();
    }
}
