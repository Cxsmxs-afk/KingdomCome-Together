// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Threading.Channels;
using KcdMp.Wire;

namespace KcdMp.Client.Tests;

internal static class ReplayScenarioWorldCases
{
    private const uint World = 7;
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Type InFrame = typeof(GameBridge).GetNestedType("InFrame", BindingFlags.NonPublic)!;

    public static Task<string?> VerifyAsync(ReplayScenarioContext context) => context.Name switch
    {
        "weather-during-load" => WeatherAsync(context),
        "pickup-under-load" => PickupAsync(context),
        "wolf-pack-fight" => WolfPackAsync(context),
        "death-respawn" => RespawnAsync(context),
        _ => throw new ArgumentException("Unknown world replay", nameof(context))
    };

    private static GameBridge Bridge(ReplayScenarioContext context)
    {
        var bridge = new GameBridge(new ClientConfig { WeatherSyncEnabled = true });
        Set(bridge, "_transport", context.Game);
        Set(bridge, "_combatRoleApplied", true);
        Set(bridge, "_hostAuthority", true);
        Set(bridge, "_joinedWorld", true);
        Set(bridge, "_sharedWorld", true);
        Set(bridge, "_hostModeKnown", true);
        Set(bridge, "_hostModeFrom", context.Sender);
        Set(bridge, "_hostSharedWorld", true);
        var where = typeof(GameBridge).GetField("_where", Private)!;
        where.SetValue(bridge, Enum.Parse(where.FieldType, "World"));
        var epochs = (Wo206Epochs)Field(bridge, "_w206");
        epochs.LearnWorld(World, context.Sender);
        epochs.NoteAck(context.Sender, World);
        return bridge;
    }

    private static object Frame(ReplayPacket packet) => Activator.CreateInstance(InFrame,
        [(int)packet.Type, packet.Payload, Stopwatch.GetTimestamp(), World])!;

    private static object Field(GameBridge bridge, string name) => typeof(GameBridge).GetField(name, Private)!.GetValue(bridge)!;
    private static void Set(GameBridge bridge, string name, object value) => typeof(GameBridge).GetField(name, Private)!.SetValue(bridge, value);
    private static object Invoke(GameBridge bridge, string name, params object?[] args) => typeof(GameBridge).GetMethod(name, Private)!.Invoke(bridge, args)!;

    private static async Task<string?> WeatherAsync(ReplayScenarioContext context)
    {
        var bridge = Bridge(context);
        await using var pipe = (CombatPipe)Field(bridge, "_combat");
        Assert.False(await context.Game.IsGameReadyAsync(context.Cancellation));
        Set(bridge, "_ownLoadExpected", true);
        var weather = context.Packets.Where(packet => packet.Type == Protocol.WeatherDown).ToArray();
        Assert.Equal(2, weather.Length);
        foreach (var packet in weather)
        {
            Assert.True(T3SideChannelFrame.TryDecode(packet.Type, packet.Payload, 7, out var value));
            Assert.Equal(context.Sender, value.Source);
            if (context.OldPath)
            {
                // The previous direct writer ran at receipt while load owned the world.
                await context.Game.ExecuteNowAsync(FormattableString.Invariant(
                    $"if KCD2MP_ApplyWeather then KCD2MP_ApplyWeather(\"{value.Profile}\",{value.Blend}) end"), context.Cancellation);
            }
            else Assert.True((bool)Invoke(bridge, "T3TryDeferSideChannel", Frame(packet)));
        }
        await (Task)Invoke(bridge, "T3DrainSideChannels", context.Cancellation);
        int early = context.Game.ObservedCalls.Count(call => call.Operation == nameof(IGameTransport.ExecuteNowAsync));
        Assert.True(await context.Game.IsGameReadyAsync(context.Cancellation));
        Set(bridge, "_ownLoadExpected", false);
        var sideChannels = Field(bridge, "_t3SideChannels");
        sideChannels.GetType().GetMethod("GameplayStarted")!.Invoke(sideChannels, [World, Environment.TickCount64 - 5000]);
        // The recording represents a preconfigured native weather gate. The
        // actual side-channel worker still validates phase and applies the Lua.
        Set(bridge, "_w151WeatherGate", "t2_rain");
        await (Task)Invoke(bridge, "T3DrainSideChannels", context.Cancellation);
        if (early != 0) return "weather wrote while the game was loading";
        return context.Game.ObservedCalls.Count(call => call.Operation == nameof(IGameTransport.ExecuteNowAsync)) == 1
            ? null : "weather did not apply exactly once after settle";
    }

    private static async Task<string?> PickupAsync(ReplayScenarioContext context)
    {
        var bridge = Bridge(context);
        await using var pipe = (CombatPipe)Field(bridge, "_combat");
        var lifecycle = new HostAuthorityLifecycle((phase, generation, _) => Task.FromResult<HostAuthorityLifecycle.Ack?>(
            new(true, phase, false, false, generation, 0)));
        Assert.True(await lifecycle.PendingAsync(false, true, context.Cancellation));
        Assert.Equal(1UL, lifecycle.Current.Generation);
        Assert.True(await lifecycle.GameplayAsync(lifecycle.Current.Generation, context.Cancellation));
        Set(bridge, "_p1Lifecycle", lifecycle);
        var packet = Assert.Single(context.Packets, packet => packet.Type == Protocol.LootHostDown);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowFrame = ProcessorFrameScope.RunAsync(async ct =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(ct);
        }, 4000, context.Cancellation);
        try
        {
            await entered.Task.WaitAsync(context.Cancellation);
            if (context.OldPath)
            {
                // The old shared FIFO required the gameplay frame to finish first.
                release.SetResult();
                await slowFrame;
            }
            object input = Frame(packet);
            InFrame.GetProperty("LootGeneration")!.SetValue(input, lifecycle.Current.Generation);
            var open = typeof(Channel).GetMethods().Single(method => method.Name == "CreateUnbounded" && method.GetParameters().Length == 0);
            var lane = open.MakeGenericMethod(InFrame).Invoke(null, null)!;
            var writer = lane.GetType().GetProperty("Writer")!.GetValue(lane)!;
            Assert.True((bool)writer.GetType().GetMethod("TryWrite")!.Invoke(writer, [input])!);
            writer.GetType().GetMethod("TryComplete")!.Invoke(writer, [null]);
            await (Task)Invoke(bridge, "T3LootLaneAsync", lane, context.Cancellation);
            return !slowFrame.IsCompleted && context.Game.ObservedCalls.Count == 1
                ? null : "pickup result waited for the blocked gameplay frame";
        }
        finally { release.TrySetResult(); await slowFrame; }
    }

    private static async Task<string?> WolfPackAsync(ReplayScenarioContext context)
    {
        var local = await context.Game.ReadPlayerSoulIdentityAsync(context.Cancellation);
        Assert.NotNull(local.Guid);
        var guard = new NpcDamageGuard();
        guard.SetLocalPlayer(local.Guid, local.Name);
        var observations = context.Packets.Where(packet => packet.Type == Protocol.NpcDamageDown).ToArray();
        Assert.Equal(3, observations.Length);
        var at = DateTime.UnixEpoch.AddMinutes(1);
        for (int i = 0; i < observations.Length; i++)
        {
            byte[] bytes = observations[i].Payload;
            Assert.Equal(context.Sender, bytes[0]);
            int nameLength = bytes[1];
            Assert.Equal(2 + nameLength + Protocol.NpcDamageFixedTail, bytes.Length);
            string name = Encoding.UTF8.GetString(bytes, 2, nameLength);
            Guid target = name == local.Name ? local.Guid.GetValueOrDefault() : new Guid(i + 1, 0, 0, new byte[8]);
            if (guard.CheckInbound(name, target) != NpcDamageGuard.Inbound.Apply) return "NPC damage targeted the local player's soul";
            float stamina = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(2 + nameLength));
            float health = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(6 + nameLength));
            Assert.True(health > 0);
            guard.NoteInboundApplied(name, health, stamina, false, at);
            if (guard.CheckOutbound(target, name, health, stamina, false, at) == NpcDamageGuard.Outbound.Send)
                return "a credit from the wolf pack could be emitted twice";
        }
        return null;
    }

    private static async Task<string?> RespawnAsync(ReplayScenarioContext context)
    {
        var dead = await context.Game.ReadPlayerStateAsync(context.Cancellation);
        Assert.True(dead?.IsDead);
        var packet = Assert.Single(context.Packets, packet => packet.Type == Protocol.W203Down);
        var answer = ReadTeleportAnswer(packet, context.Sender);
        var snapshot = new TeleportSnapshot(answer.X, answer.Y, answer.Z, answer.Known, answer.Flags, World, 1);
        var proof = new TeleportProof(TeleportSource.Respawn, Environment.TickCount64 + 1500, World,
            () => answer.Ok && answer.World == World && snapshot.Safe);
        bool canWake = proof.Valid;
        var awake = await context.Game.ReadPlayerStateAsync(context.Cancellation);
        Assert.False(awake?.IsDead);
        Assert.True(awake?.Health > 0);
        return canWake ? null : "wake relied on a stale or unsafe teleport snapshot";
    }

    private static TeleportAnswer ReadTeleportAnswer(ReplayPacket packet, byte expectedSource)
    {
        Assert.True(Protocol.TrySplitJoinDown(packet.Payload, out byte source, out _, out _, out var body));
        Assert.Equal(expectedSource, source);
        Assert.True(LootMsg.TryDecode(body, out var message));
        Assert.Equal(Protocol.W203TpSnapshotResponse, message.Kind);
        Assert.True(TeleportSnapshotCodec.TryAnswer(message.Text, out var answer));
        return answer;
    }
}
