// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Reflection;
using KcdMp.Wire;

namespace KcdMp.Client.Tests;

public class NativeNpcLeaseTests
{
    private const BindingFlags InstancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string Npc = "kcd2mp_7";
    private static FieldInfo Field(string name) => typeof(GameBridge).GetField(name, InstancePrivate)!;

    private sealed class Rig : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(10));
        private readonly GameBridge _bridge = new(new ClientConfig());
        public readonly NamedPipeServerStream Server;
        public readonly CombatPipe Pipe;
        public readonly List<string> Lua;
        public ConcurrentDictionary<string, byte> Bound => (ConcurrentDictionary<string, byte>)Field("_nativeBound").GetValue(_bridge)!;
        public Task Queue => ((Task)Field("_nativeBindChain").GetValue(_bridge)!).WaitAsync(_stop.Token);
        public long Chained => (long)Field("_nativeBindChained").GetValue(_bridge)!;

        public Rig(bool legacyTwin = false)
        {
            string pipeName = "kcdmp-lease-" + Guid.NewGuid().ToString("N");
            Server = new(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            Pipe = new(pipeName) { BatchOn = false };
            var transport = DispatchProxy.Create<IGameTransport, QuestItemMirrorTests.CaptureTransport>();
            Lua = ((QuestItemMirrorTests.CaptureTransport)(object)transport).Lua;
            Field("_transport").SetValue(_bridge, transport);
            var switches = (ConcurrentDictionary<string, bool>)Field("_w209On").GetValue(_bridge)!;
            switches["native_bind_order"] = !legacyTwin;
        }

        public async Task Connect()
        {
            await ((CombatPipe)Field("_combat").GetValue(_bridge)!).DisposeAsync();
            Field("_combat").SetValue(_bridge, Pipe);
            await Accept();
        }

        public async Task Reconnect()
        {
            Server.Disconnect();
            await Accept();
        }

        private async Task Accept()
        {
            var accept = Server.WaitForConnectionAsync(_stop.Token);
            Assert.True(await Pipe.EnsureConnectedAsync(_stop.Token));
            await accept;
        }

        public void Event(string arg) => typeof(GameBridge).GetMethod("OnGameEvent", InstancePrivate)!
            .Invoke(_bridge, ["npc_native", Npc + " " + arg]);

        public async Task<byte[]> Read(byte type)
        {
            var header = new byte[3];
            await Server.ReadExactlyAsync(header, _stop.Token);
            Assert.Equal(type, header[0]);
            var body = new byte[BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(1))];
            await Server.ReadExactlyAsync(body, _stop.Token);
            return body;
        }

        public async Task ReadBind(bool on)
        {
            byte[] body = await Read(0x11);
            Assert.Equal(NativeNpcCodec.BuildBind(on, on ? 0x2Au : 0, 0, on ? 1 : 0, on ? 2 : 0, on ? 3 : 0,
                on ? (ushort)75 : (ushort)0, Npc), body);
        }

        public Task Answer(byte type, params byte[] body)
        {
            byte[] frame = [type, (byte)body.Length, 0, .. body];
            return Server.WriteAsync(frame, _stop.Token).AsTask();
        }

        public async ValueTask DisposeAsync()
        {
            await Pipe.DisposeAsync();
            Server.Dispose();
            _stop.Dispose();
        }
    }

    [Fact]
    public async Task Leased_steps_are_chained_even_with_the_old_order_twin()
    {
        await using var rig = new Rig(legacyTwin: true);
        await rig.Connect();
        rig.Event("on 2A ? 1 2 3 75 41");
        rig.Event("off lease 41");
        rig.Event("on 2A ? 1 2 3 75 42");
        Assert.Equal(3, rig.Chained);

        await rig.ReadBind(true);
        await rig.Answer(0x81, 1, 254, 0);
        await rig.ReadBind(false);
        Assert.Contains("ok\", 41)", Assert.Single(rig.Lua));
        await rig.Answer(0x81, 1, 255, 0);
        await rig.ReadBind(true);
        Assert.Equal(2, rig.Lua.Count);
        Assert.Contains("ok\", 41, true)", rig.Lua[1]);
        await rig.Answer(0x81, 1, 0, 0);
        await rig.Queue;
        Assert.Collection(rig.Lua,
            lua => Assert.Contains("\"kcd2mp_7\", 1, \"ok\", 41)", lua),
            lua => Assert.Contains("\"kcd2mp_7\", 1, \"ok\", 41, true)", lua),
            lua => Assert.Contains("\"kcd2mp_7\", 1, \"ok\", 42)", lua));
        Assert.Contains(Npc, rig.Bound.Keys);
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("inconsistent")]
    [InlineData("native-refusal")]
    [InlineData("disconnected")]
    public async Task An_unconfirmed_release_keeps_native_ownership_and_sends_no_Lua_ack(string fault)
    {
        await using var rig = new Rig();
        await rig.Connect();
        rig.Bound[Npc] = 1;
        rig.Event("off lease 41");
        await rig.ReadBind(false);
        switch (fault)
        {
            case "truncated": await rig.Answer(0x81, 1, 8); break;
            case "inconsistent": await rig.Answer(0x81, 1, 8, (byte)NativeNpcReason.PipeClosed); break;
            case "native-refusal": await rig.Answer(0x81, 0, 8, (byte)NativeNpcReason.Disarmed); break;
            case "disconnected": rig.Server.Dispose(); break;
        }
        await rig.Queue;
        Assert.Empty(rig.Lua);
        Assert.Contains(Npc, rig.Bound.Keys);
    }

    [Fact]
    public async Task A_late_unrelated_success_after_the_lifecycle_lane_cannot_acknowledge_release()
    {
        await using var rig = new Rig(legacyTwin: true);
        await rig.Connect();
        rig.Event("on 2A ? 1 2 3 75 41");
        await rig.ReadBind(true);
        await rig.Answer(0x81, 1, 70, 0);
        await rig.Queue;

        var control = rig.Pipe.Wo216LifecycleAsync(Protocol.Wo216LifecycleQuery, 0);
        byte[] query = await rig.Read(0x2B);
        uint requestId = BinaryPrimitives.ReadUInt32LittleEndian(query.AsSpan(10));
        await rig.Answer(0xAC, Wo216LifecycleCodecTests.Ack(requestId, 71));
        Assert.NotNull(await control);

        using var stopHold = new CancellationTokenSource();
        var hold = rig.Pipe.NpcHoldAsync(Npc, 100, stopHold.Token);
        await rig.Read(0x12);
        stopHold.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hold);

        rig.Event("off lease 41");
        await rig.ReadBind(false);
        await rig.Answer(0x81, 1, 72, 0); // late success for the hold
        await rig.Answer(0x81, 0, 73, (byte)NativeNpcReason.OwnerBusy); // actual release refused
        await rig.Queue;
        Assert.Single(rig.Lua);
        Assert.Contains(Npc, rig.Bound.Keys);
        Assert.Equal(1, rig.Pipe.StaleRepliesDropped);

        rig.Event("off lease 41");
        await rig.ReadBind(false);
        await rig.Answer(0x81, 1, 74, 0);
        await rig.Queue;
        Assert.Contains("\"kcd2mp_7\", 1, \"ok\", 41, true)", rig.Lua[1]);
        Assert.DoesNotContain(Npc, rig.Bound.Keys);
    }

    [Fact]
    public async Task A_first_request_without_a_reply_requires_a_fresh_pipe_before_acknowledging_release()
    {
        await using var rig = new Rig();
        await rig.Connect();
        rig.Bound[Npc] = 1;
        using var stopHold = new CancellationTokenSource();
        var hold = rig.Pipe.NpcHoldAsync(Npc, 100, stopHold.Token);
        await rig.Read(0x12);
        stopHold.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hold);

        rig.Event("off lease 41");
        await rig.Queue;
        Assert.False(rig.Pipe.IsConnected);
        Assert.Empty(rig.Lua);
        Assert.Contains(Npc, rig.Bound.Keys);

        await rig.Reconnect();
        rig.Event("off lease 41");
        await rig.ReadBind(false);
        await rig.Answer(0x81, 1, 18, 0);
        await rig.Queue;
        Assert.Contains("\"kcd2mp_7\", 1, \"ok\", 41, true)", Assert.Single(rig.Lua));
        Assert.DoesNotContain(Npc, rig.Bound.Keys);
    }

    [Fact]
    public async Task Legacy_events_keep_their_three_argument_ack_and_unbind_without_a_lease()
    {
        await using var rig = new Rig();
        await rig.Connect();
        rig.Event("on 2A ? 1 2 3 75");
        await rig.ReadBind(true);
        await rig.Answer(0x81, 1, 1, 0);
        await rig.Queue;
        string ack = Assert.Single(rig.Lua);
        Assert.Contains("\"kcd2mp_7\", 1, \"ok\") end;", ack);
        Assert.Matches(@"KCD2MP_W214ReportGhostId\(""7"", ""[0-9a-f]{32}"", ""0000002A""\)", ack);

        rig.Event("off cleanup");
        await rig.ReadBind(false);
        await rig.Answer(0x81, 1, 2, 0);
        await rig.Queue;
        Assert.Single(rig.Lua);
        Assert.DoesNotContain(Npc, rig.Bound.Keys);
    }
}
