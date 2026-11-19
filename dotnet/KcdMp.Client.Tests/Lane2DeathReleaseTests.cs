// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Reflection;
using KcdMp.Wire;

namespace KcdMp.Client.Tests;

public sealed class Lane2DeathReleaseTests
{
    private const string Npc = "A_Test_Npc";
    private const uint CurrentEid = 0x11223344, OldEngageEid = 0x55667788;
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

    [Fact]
    public async Task An_unbound_death_is_not_treated_as_a_released_copy()
    {
        var bridge = new GameBridge(new ClientConfig());
        Task release = (Task)typeof(GameBridge).GetMethod("Wo195OnCopyDeathAsync", PrivateInstance)!
            .Invoke(bridge, [Npc, "owner-death"])!;
        await release;
        bool dead = (bool)typeof(GameBridge).GetMethod("Wo195IsDead", PrivateInstance)!.Invoke(bridge, [Npc])!;
        Assert.False(dead);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeathReleaseUsesOnlyTheCurrentSoulBinding(bool retireBeforeWrite)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        string pipeName = "lane2-release-" + Guid.NewGuid().ToString("N");
        using var wire = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var pipe = new CombatPipe(pipeName);
        Task connected = wire.WaitForConnectionAsync(deadline.Token);
        Assert.True(await pipe.EnsureConnectedAsync(deadline.Token));
        await connected;

        var bridge = new GameBridge(new ClientConfig());
        var lifecycle = new HostAuthorityLifecycle((phase, generation, _) =>
            Task.FromResult<HostAuthorityLifecycle.Ack?>(new(true, phase, false, false, generation, 0)));
        Assert.True(await lifecycle.PendingAsync(enabled: false, newLoad: false, ct: deadline.Token));
        ulong generation = lifecycle.Current.Generation;
        Assert.True(await lifecycle.GameplayAsync(generation, deadline.Token));
        Field("_combat").SetValue(bridge, pipe);
        Field("_p1Lifecycle").SetValue(bridge, lifecycle);
        Field("_where").SetValue(bridge, Enum.Parse(Field("_where").FieldType, "World"));
        var entities = (ConcurrentDictionary<string, uint>)Field("_npcEntityIds").GetValue(bridge)!;
        entities[Npc] = CurrentEid;
        var guid = Guid.NewGuid();
        var souls = (ConcurrentDictionary<string, (uint Eid, Guid Guid)>)Field("_copySoulGuid").GetValue(bridge)!;
        souls[Npc] = (CurrentEid, guid);
        var engagements = (ConcurrentDictionary<string, (DateTime At, uint Eid)>)Field("_w132Engaged").GetValue(bridge)!;
        engagements[Npc] = (DateTime.UtcNow, OldEngageEid);

        Task Release() => (Task)typeof(GameBridge).GetMethod("Wo195OnCopyDeathAsync", PrivateInstance)!
            .Invoke(bridge, [Npc, "owner-death"])!;
        bool Dead() => (bool)typeof(GameBridge).GetMethod("Wo195IsDead", PrivateInstance)!.Invoke(bridge, [Npc])!;

        if (retireBeforeWrite)
        {
            var writer = (SemaphoreSlim)typeof(CombatPipe).GetField("_writeLock", PrivateInstance)!.GetValue(pipe)!;
            Task queued;
            await writer.WaitAsync(deadline.Token);
            try
            {
                queued = Release();
                Assert.False(queued.IsCompleted);
                Assert.True(Dead());
                Field("_npcDeathLife").SetValue(bridge, 1L);
            }
            finally { writer.Release(); }
            Task bytes = wire.ReadAsync(new byte[1], deadline.Token).AsTask();
            Assert.Same(queued, await Task.WhenAny(queued, bytes).WaitAsync(deadline.Token));
            await queued.WaitAsync(deadline.Token);
            Assert.False(Dead());
            return;
        }

        Task release = Release();
        byte[] header = new byte[3];
        await wire.ReadExactlyAsync(header, deadline.Token);
        Assert.Equal(0x2B, header[0]); // Only the native release owner is asked, never a raw engagement-off op.
        byte[] body = new byte[BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(1))];
        await wire.ReadExactlyAsync(body, deadline.Token);
        Assert.Equal(29, body.Length);
        Assert.Equal(Protocol.Wo195OpRelease, body[0]);
        Assert.Equal(CurrentEid, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(1)));
        Assert.Equal(guid, new Guid(body.AsSpan(5, 16)));
        Assert.Equal(generation, BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(21)));

        // A second death route sees the same concrete binding while the first request is in flight.
        await Release().WaitAsync(deadline.Token);
        Assert.Equal(1L, Field("_w195Releases").GetValue(bridge));

        // A reply for the previous binding cannot trigger the delayed corpse path on a new EID.
        entities[Npc] = CurrentEid + 1;
        await wire.WriteAsync(new byte[] { 0xAC, 7, 0, 1, 40, Protocol.Wo195OpRelease, 0, 0, 0, 1 }, deadline.Token);
        await release.WaitAsync(deadline.Token);
        Assert.False(Dead());
    }

    [Fact]
    public async Task A_rejected_native_release_does_not_leave_the_current_body_marked_dead()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        string pipeName = "lane2-release-nack-" + Guid.NewGuid().ToString("N");
        using var wire = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var pipe = new CombatPipe(pipeName);
        Task connected = wire.WaitForConnectionAsync(deadline.Token);
        Assert.True(await pipe.EnsureConnectedAsync(deadline.Token));
        await connected;

        var bridge = new GameBridge(new ClientConfig());
        var lifecycle = new HostAuthorityLifecycle((phase, generation, _) =>
            Task.FromResult<HostAuthorityLifecycle.Ack?>(new(true, phase, false, false, generation, 0)));
        Assert.True(await lifecycle.PendingAsync(enabled: false, newLoad: false, ct: deadline.Token));
        Assert.True(await lifecycle.GameplayAsync(lifecycle.Current.Generation, deadline.Token));
        Field("_combat").SetValue(bridge, pipe);
        Field("_p1Lifecycle").SetValue(bridge, lifecycle);
        Field("_where").SetValue(bridge, Enum.Parse(Field("_where").FieldType, "World"));
        var entities = (ConcurrentDictionary<string, uint>)Field("_npcEntityIds").GetValue(bridge)!;
        entities[Npc] = CurrentEid;
        var souls = (ConcurrentDictionary<string, (uint Eid, Guid Guid)>)Field("_copySoulGuid").GetValue(bridge)!;
        souls[Npc] = (CurrentEid, Guid.NewGuid());

        Task release = (Task)typeof(GameBridge).GetMethod("Wo195OnCopyDeathAsync", PrivateInstance)!
            .Invoke(bridge, [Npc, "owner-death"])!;
        bool Dead() => (bool)typeof(GameBridge).GetMethod("Wo195IsDead", PrivateInstance)!.Invoke(bridge, [Npc])!;
        byte[] header = new byte[3];
        await wire.ReadExactlyAsync(header, deadline.Token);
        await wire.ReadExactlyAsync(new byte[BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(1))], deadline.Token);
        Assert.True(Dead());
        await wire.WriteAsync(new byte[] { 0xAC, 7, 0, 0, 40, Protocol.Wo195OpRelease, 1, 0, 0, 0 }, deadline.Token);
        await release.WaitAsync(deadline.Token);
        Assert.False(Dead());
    }

    private static FieldInfo Field(string name) => typeof(GameBridge).GetField(name, PrivateInstance)!;
}
