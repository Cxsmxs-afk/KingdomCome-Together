// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.IO.Pipes;
using System.Reflection;
using KcdMp.Wire;

namespace KcdMp.Client.Tests;

public sealed class P6CombatPipeTests
{
    private static byte[] Wire(byte type, byte[] body) => [type, (byte)body.Length, (byte)(body.Length >> 8), .. body];
    private static SemaphoreSlim Gate(CombatPipe pipe, string name) => (SemaphoreSlim)typeof(CombatPipe).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pipe)!;
    private sealed class Rig : IAsyncDisposable
    {
        public readonly NamedPipeServerStream Server;
        public readonly CombatPipe Pipe;
        private readonly Task _accept;
        private byte _seq;
        public Rig()
        {
            string name = "p6-" + Guid.NewGuid().ToString("N"); Server = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            _accept = Server.WaitForConnectionAsync(); Pipe = new(name);
        }
        public async Task Connect() { Assert.True(await Pipe.EnsureConnectedAsync()); await _accept.WaitAsync(TimeSpan.FromSeconds(3)); }
        public async Task<(byte Type, byte[] Body)> Read()
        {
            using var stop = new CancellationTokenSource(3000); byte[] h = new byte[3]; await Server.ReadExactlyAsync(h, stop.Token);
            byte[] b = new byte[BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(1))]; await Server.ReadExactlyAsync(b, stop.Token); return (h[0], b);
        }
        public async Task Reply(byte type, byte op, byte[] payload, bool ok = true, byte reason = 0)
        { await Server.WriteAsync(Wire(type, [(byte)(ok ? 1 : 0), _seq++, op, reason, .. payload])); await Server.FlushAsync(); }
        public async ValueTask DisposeAsync() { await Pipe.DisposeAsync(); await Server.DisposeAsync(); }
    }
    private static byte[] Poll(ulong gen = 123, byte phase = 3)
    {
        byte[] b = new byte[66]; b[0] = 2; b[1] = 1; BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(2), gen); BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(10), 456);
        b[18] = phase; b[19] = 1; b[20] = 3; BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(22), 55);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(26), 789); Guid.Parse("00000009-0000-0000-0000-000000000000").TryWriteBytes(b.AsSpan(50)); return b;
    }
    private static async Task GoodPoll(Rig r)
    {
        var next = r.Pipe.RangedPollAsync(true, 123, () => true); var wire = await r.Read(); Assert.Equal(0x2B, wire.Type); Assert.Equal(86, wire.Body[0]);
        await r.Reply(0xAC, 86, Poll()); Assert.NotNull(await next.WaitAsync(TimeSpan.FromSeconds(3)));
    }
    private static async Task AtWriteGate(CombatPipe pipe)
    {
        var start = Environment.TickCount64;
        while (Gate(pipe, "_gate").CurrentCount != 0 && Environment.TickCount64 - start < 2000) await Task.Delay(5);
        Assert.Equal(0, Gate(pipe, "_gate").CurrentCount);
    }
    private static MeasuredMissileRequest Request() => new(new(7, 11, 22, 33), "npc_bandit_01", 5.5f, 1.25f);
    private static byte[] Ack(MeasuredMissileKey key, byte source = 0, byte status = 0, byte steps = 1)
    {
        byte[] b = new byte[40]; b[0] = 1; b[1] = status; b[2] = steps; b[3] = source;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), key.World); BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(8), key.SourceGeneration);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(16), key.Epoch); BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(24), key.ContactId);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(32), 5.5f); BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(36), 1.25f); return b;
    }
    private static byte[] LocalHit(bool measured, bool died = false)
    {
        byte[] b = new byte[measured ? 61 : 27]; Guid.Parse("00000042-0000-0000-0000-000000000000").TryWriteBytes(b);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(16), 1.25f); BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(20), 5.5f); b[24] = died ? (byte)1 : (byte)0; b[25] = 1;
        if (measured) { b[27] = 1; b[28] = 7; for (int i = 0; i < 4; i++) BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(29 + 8 * i), (ulong)(100 + i)); }
        return b;
    }
    [Fact]
    public async Task Actual_ranged_poll_writes_explicit_86_v2_request_and_decodes_immutable_native_row()
    {
        await using var r = new Rig(); await r.Connect(); var pending = r.Pipe.RangedPollAsync(true, 123, () => true);
        var request = await r.Read(); Assert.Equal(0x2B, request.Type); Assert.Equal(new byte[] { 86, 2, 1, 123, 0, 0, 0, 0, 0, 0, 0 }, request.Body);
        await r.Reply(0xAC, 86, Poll()); var result = await pending.WaitAsync(TimeSpan.FromSeconds(3)); Assert.NotNull(result);
        Assert.Equal(123UL, result.Generation); Assert.Equal(456UL, result.Epoch); var row = Assert.Single(result.Rows);
        Assert.Equal((byte)3, row.Phase); Assert.Equal(55u, row.Sequence); Assert.Equal(789UL, row.CapturedTick); Assert.Equal(Guid.Empty, row.Left); Assert.NotEqual(Guid.Empty, row.Right);
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public async Task Poll_refuses_mismatched_generation_malformed_row_old_version_or_generic_refusal(int bad)
    {
        await using var r = new Rig(); await r.Connect(); var pending = r.Pipe.RangedPollAsync(true, 123, () => true); await r.Read(); var p = Poll();
        if (bad == 0) p = Poll(124); if (bad == 1) p[21] = 1; if (bad == 2) p[0] = 1; if (bad == 3) p = p[..^1];
        await r.Reply(0xAC, 86, p, bad != 4); Assert.Null(await pending.WaitAsync(TimeSpan.FromSeconds(3))); await GoodPoll(r);
    }
    [Fact]
    public async Task OneShot_guard_is_checked_after_actual_physical_writer_wait_and_the_following_request_survives()
    {
        await using var r = new Rig(); await r.Connect(); var writer = Gate(r.Pipe, "_writeLock"); await writer.WaitAsync(); bool current = true;
        var stale = r.Pipe.RangedOneShotAsync("kcd2mp_1", "Fire", "l_bow", () => current);
        try { await AtWriteGate(r.Pipe); current = false; } finally { writer.Release(); }
        Assert.Null(await stale.WaitAsync(TimeSpan.FromSeconds(3)));
        var live = r.Pipe.RangedOneShotAsync("kcd2mp_1", "Fire", "l_bow", () => true); var first = await r.Read();
        Assert.Equal(0x28, first.Type); Assert.Equal(4, first.Body[0]); Assert.Equal(Wo143Codec.OneShot("kcd2mp_1", "Fire", "l_bow", 0, 0), first.Body[1..]);
        await r.Reply(0xA8, 4, BitConverter.GetBytes(321)); Assert.Equal(321, await live.WaitAsync(TimeSpan.FromSeconds(3))); await GoodPoll(r);
    }
    [Fact]
    public async Task Measured_accept_uses_only_op18_extended_tail_and_requires_full_echoed_receipt()
    {
        await using var r = new Rig(); await r.Connect(); var req = Request(); var victim = Guid.NewGuid(); ulong deadline = unchecked((ulong)Environment.TickCount64) + 2000;
        var pending = r.Pipe.AcceptMeasuredMissileAsync(0, req, victim, 99, 123, deadline, () => true); var wire = await r.Read();
        Assert.Equal(0x2B, wire.Type); Assert.Equal(18, wire.Body[0]); Assert.Equal(MeasuredMissileCodec.BuildArgs(0, req, victim, 99, 123, deadline), wire.Body[1..]);
        await r.Reply(0xAC, 18, Ack(req.Key)); var actual = await pending.WaitAsync(TimeSpan.FromSeconds(3)); Assert.NotNull(actual);
        Assert.Equal(req.Key, actual.Value.Key); Assert.Equal(MeasuredMissileStatus.Accepted, actual.Value.Status); Assert.Equal((byte)1, actual.Value.Steps); Assert.Equal(req.Health, actual.Value.Health); Assert.Equal(req.Stamina, actual.Value.Stamina);
    }
    [Theory]
    [InlineData(3)] [InlineData(4)] [InlineData(8)] [InlineData(16)] [InlineData(24)]
    public async Task Receipt_owner_world_generation_epoch_and_contact_must_echo_exactly(int at)
    {
        await using var r = new Rig(); await r.Connect(); var req = Request();
        var pending = r.Pipe.AcceptMeasuredMissileAsync(0, req, Guid.NewGuid(), 99, 123, unchecked((ulong)Environment.TickCount64) + 2000, () => true); await r.Read();
        var p = Ack(req.Key); p[at] ^= 1; await r.Reply(0xAC, 18, p); Assert.Null(await pending.WaitAsync(TimeSpan.FromSeconds(3))); await GoodPoll(r);
    }
    [Fact]
    public async Task Old_DLL_BadRequest_is_explicit_Invalid_and_never_falls_back_to_0x19_raw_damage()
    {
        await using var r = new Rig(); await r.Connect(); var req = Request();
        var pending = r.Pipe.AcceptMeasuredMissileAsync(0, req, Guid.NewGuid(), 99, 123, unchecked((ulong)Environment.TickCount64) + 2000, () => true); await r.Read();
        await r.Reply(0xAC, 18, [], false, 1); var refused = await pending.WaitAsync(TimeSpan.FromSeconds(3)); Assert.NotNull(refused); Assert.Equal(MeasuredMissileStatus.Invalid, refused.Value.Status);
        await GoodPoll(r); // the very next physical frame is86, not a fallback0x19/op18
    }
    [Fact]
    public async Task Original_deadline_expiring_at_writer_prevents_apply_without_poisoning_the_next_poll()
    {
        await using var r = new Rig(); await r.Connect(); var writer = Gate(r.Pipe, "_writeLock"); await writer.WaitAsync();
        ulong deadline = unchecked((ulong)Environment.TickCount64) + 100; var req = Request();
        var old = r.Pipe.AcceptMeasuredMissileAsync(0, req, Guid.NewGuid(), 99, 123, deadline, () => true);
        try { await AtWriteGate(r.Pipe); await Task.Delay(150); Assert.True(unchecked((ulong)Environment.TickCount64) > deadline); } finally { writer.Release(); }
        Assert.Null(await old.WaitAsync(TimeSpan.FromSeconds(3))); await GoodPoll(r);
    }
    [Fact]
    public async Task Typed_contact_fires_only_typed_callback_keeps_real_Host_death_and_malformed_extension_never_becomes_legacy()
    {
        await using var r = new Rig(); var got = new TaskCompletionSource<MeasuredMissileEvidence>(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); int measured = 0, legacy = 0;
        r.Pipe.OnMeasuredMissile = (_, st, hp, evidence) => { Assert.Equal(1.25f, st); Assert.Equal(5.5f, hp); measured++; got.TrySetResult(evidence); return Task.CompletedTask; };
        r.Pipe.OnLocalHit = (_, _, _, _, _, _) => { legacy++; old.TrySetResult(); return Task.CompletedTask; }; await r.Connect();
        await r.Server.WriteAsync(Wire(0x90, LocalHit(true, true))); var evidence = await got.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(evidence.Died); Assert.Equal(new MeasuredMissileEvidence(100, 101, 102, 103) { Died = true }, evidence); Assert.Equal(0, legacy);
        var malformed = LocalHit(true); malformed[28] = 3; await r.Server.WriteAsync(Wire(0x90, malformed));
        await r.Server.WriteAsync(Wire(0x90, LocalHit(true)[..28])); // unknown partial new extension, not the old27B layout
        await r.Server.WriteAsync(Wire(0x90, LocalHit(false))); await old.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, measured); Assert.Equal(1, legacy);
    }
}
