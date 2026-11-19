// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using KcdMp.Wire;

namespace KcdMp.Client.Tests;

[CollectionDefinition("Friendly-fire receipts", DisableParallelization = true)]
public sealed class FriendlyFireReceiptCollection { }

[Collection("Friendly-fire receipts")]
public sealed class FriendlyFireReceiptIntegrationTests
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

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
            Assert.Equal(34, request.Length);
            Assert.Equal(payload[2..10], request[..8]);
            Assert.Equal((byte)2, request[9]);
            Assert.Equal(payload[PlayerHitV8.DownLen..], request[10..26]);
            Assert.Equal((ulong)ingress, BinaryPrimitives.ReadUInt64LittleEndian(request.AsSpan(26)));
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
            var field = typeof(GameBridge).GetField(fieldName, Instance)!;
            if (fieldName == "_myGhostId") field.SetValue(bridge, (byte)3);
            else field.SetValue(bridge, (long)field.GetValue(bridge)! + 1);
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
        Assert.Equal(first[PlayerHitV8.DownLen..], (await ReadHitAsync(server, ct))[10..26]);
        await server.WriteAsync(new byte[] { 0x81, 3, 0, 0, 1, 22 }, ct);
        await pending.WaitAsync(ct);

        await DeliverAsync(bridge, first, ct).WaitAsync(TimeSpan.FromSeconds(1), ct);
        await DeliverAsync(bridge, first[..PlayerHitV8.DownLen], ct).WaitAsync(TimeSpan.FromSeconds(1), ct);

        var relocated = Contact(1, 44);
        var next = DeliverAsync(bridge, relocated, ct);
        Assert.Equal(relocated[PlayerHitV8.DownLen..], (await ReadHitAsync(server, ct))[10..26]);
        await server.WriteAsync(new byte[] { 0x81, 3, 0, 1, 2, 24 }, ct);
        await next.WaitAsync(ct);

        await DeliverAsync(bridge, Contact(18, 43), ct).WaitAsync(TimeSpan.FromSeconds(1), ct);
        var separate = Contact(2, 44);
        var other = DeliverAsync(bridge, separate, ct);
        Assert.Equal(separate[PlayerHitV8.DownLen..], (await ReadHitAsync(server, ct))[10..26]);
        await server.WriteAsync(new byte[] { 0x81, 3, 0, 1, 3, 24 }, ct);
        await other.WaitAsync(ct);
    }

    private static GameBridge BridgeOn(CombatPipe pipe)
    {
        var bridge = new GameBridge(new ClientConfig());
        typeof(GameBridge).GetField("_combat", Instance)!.SetValue(bridge, pipe);
        typeof(GameBridge).GetField("_myGhostId", Instance)!.SetValue(bridge, (byte)1);
        typeof(GameBridge).GetField("_ffSession", Instance)!.SetValue(bridge, true);
        typeof(GameBridge).GetField("_w213Caps", Instance)!.SetValue(bridge, Protocol.LinkCapPvpContact);
        var session = typeof(GameBridge).GetNestedType("PvpContactSession", BindingFlags.NonPublic)!;
        var stream = new MemoryStream();
        typeof(GameBridge).GetField("_wo121ContactSession", Instance)!.SetValue(bridge,
            Activator.CreateInstance(session, Instance, null, [stream], null));
        return bridge;
    }

    private static byte[] Contact(ulong sequence, ulong incarnation)
    {
        var up = new PlayerHitV8(1, 10, 7, 0, 0).BuildUp(sequence, 99);
        var down = new byte[PlayerHitV8.IdentifiedDownLen];
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
                 ingressTick ?? Environment.TickCount64], null));
        return (Task)typeof(GameBridge).GetMethod("ProcessFrameCoreAsync", Instance)!.Invoke(bridge, [frame, ct, null])!;
    }

    private static async Task<byte[]> ReadHitAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        var header = new byte[3];
        await server.ReadExactlyAsync(header, ct);
        Assert.Equal(0x1A, header[0]);
        int length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(1));
        Assert.True(length is 10 or 34);
        var request = new byte[length];
        await server.ReadExactlyAsync(request, ct);
        return request;
    }
}
