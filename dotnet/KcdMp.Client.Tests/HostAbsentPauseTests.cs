// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Reflection;
using System.Security.Cryptography;
using KcdMp.Wire;

namespace KcdMp.Client.Tests;

public sealed class HostAbsentPauseTests
{
    private const BindingFlags Instance = BindingFlags.NonPublic | BindingFlags.Instance;

    [Fact]
    public void A_joined_world_pauses_and_a_join_in_progress_aborts()
    {
        Assert.Equal(HostAbsentRules.GoneAction.Pause, HostAbsentRules.OnHostGone(joinedWorld: true, joinPreparing: false, joinLoading: false));
        Assert.Equal(HostAbsentRules.GoneAction.AbortJoin, HostAbsentRules.OnHostGone(joinedWorld: true, joinPreparing: true, joinLoading: false));
        Assert.Equal(HostAbsentRules.GoneAction.AbortJoin, HostAbsentRules.OnHostGone(joinedWorld: false, joinPreparing: false, joinLoading: true));
        Assert.Equal(HostAbsentRules.GoneAction.Ignore, HostAbsentRules.OnHostGone(joinedWorld: false, joinPreparing: false, joinLoading: false));
        Assert.False(HostAbsentRules.MayWritePersonalSave(hostAbsent: true));
    }

    [Fact]
    public void Only_the_same_player_and_world_resume()
    {
        var left = new HostLease("tcp:127.0.0.1", "the-host", 142, Guid.ParseExact("452560e1066a4794a05071e8c539ecf4", "N"));
        Assert.True(HostAbsentRules.SameReturn(left, left.IdentityKey, left.Name, 142, left.RunId));
        Assert.False(HostAbsentRules.SameReturn(left, left.IdentityKey, "joiner-1", 142, left.RunId));
        Assert.False(HostAbsentRules.SameReturn(left, left.IdentityKey, left.Name, 99, left.RunId));
        Assert.False(HostAbsentRules.SameReturn(left, left.IdentityKey, left.Name, 142, Guid.NewGuid()));
        Assert.False(HostAbsentRules.SameReturn(left, left.IdentityKey, left.Name, 0, left.RunId));
    }

    [Fact]
    public void The_pause_does_not_touch_a_personal_save_and_a_different_epoch_does_not_resume()
    {
        string path = Path.Combine(Path.GetTempPath(), "kct-host-absent-" + Guid.NewGuid().ToString("N") + ".whs");
        byte[] original = [1, 2, 3, 4, 5, 6, 7, 8];
        File.WriteAllBytes(path, original);
        try
        {
            var bridge = new GameBridge(new ClientConfig());
            var epochs = (Wo206Epochs)typeof(GameBridge).GetField("_w206", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(bridge)!;
            Guid run = Guid.ParseExact("452560e1066a4794a05071e8c539ecf4", "N");
            Assert.True(epochs.LearnWorld(142, 2));
            bridge.HostAbsentOnEpoch(142, run);
            bridge.EnterHostAbsentPause("host-left");
            Assert.True(bridge.HostAbsentPause);
            Assert.Contains("host-absent", bridge.JoinStatusJson());
            Assert.Equal(Convert.ToHexString(SHA256.HashData(original)), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));

            Assert.False(bridge.HostAbsentCanHearEpoch(99, run));
            Assert.True(bridge.HostAbsentPause);
            Assert.False(bridge.HostAbsentCanHearEpoch(142, Guid.NewGuid()));
            Assert.True(bridge.HostAbsentPause);
            Assert.True(bridge.HostAbsentCanHearEpoch(142, run));
            Assert.True(bridge.HostAbsentPause);
            Assert.True(bridge.CompleteHostAbsentResume(142, run));
            Assert.False(bridge.HostAbsentPause);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(original)), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Returning_agent_announces_the_retained_world_before_relay_authority()
    {
        var bridge = new GameBridge(new ClientConfig { IsHosting = true });
        using var relay = new MemoryStream();
        using var lifetime = new CancellationTokenSource();
        Set(bridge, "_loadSequenceRuntime", true);
        Set(bridge, "_sharedWorld", true);
        Set(bridge, "_combatRoleApplied", true);
        Set(bridge, "_isDamageAuthority", true);
        Set(bridge, "_wo122Stream", relay);
        var world = Get<Wo206Epochs>(bridge, "_w206");
        var sequence = Get<WorldLoadSequence>(bridge, "_worldLoad");
        Assert.True(world.LearnWorld(142, 2));
        Assert.True(sequence.TryBegin(0, WorldLoadStart.AdoptLoadedWorld, 142));
        Invoke(bridge, "WorldReconnectHostLost", lifetime.Token);
        Set(bridge, "_isDamageAuthority", false);

        await (Task)Invoke(bridge, "Wo206AnnounceAsync", "peer", (byte?)7)!;

        var bytes = relay.ToArray();
        Assert.NotEmpty(bytes);
        Assert.Equal(Protocol.W203Up, bytes[0]);
        Assert.Equal(7, bytes[3]);
        Assert.True(LootMsg.TryDecode(bytes.AsSpan(3 + Protocol.JoinHeaderLen), out var proof));
        Assert.Equal(Protocol.W203Epoch, proof.Kind);
        Assert.True(Wo206Rules.TryParseEpoch(proof.Text, out uint epoch, out _, out _));
        Assert.Equal(142u, epoch);
        Assert.True(HostAbsentRules.TryReadRun(proof.Text, out Guid run));
        Assert.Equal(Get<Guid>(bridge, "_worldReconnectInstance"), run);
        Assert.False(Get<bool>(bridge, "_isDamageAuthority"));
        lifetime.Cancel();
    }

    [Fact]
    public void Paused_joiner_keeps_the_save_lock_even_when_the_host_status_is_unknown()
    {
        var bridge = new GameBridge(new ClientConfig());
        Set(bridge, "_joinedWorld", true);
        Set(bridge, "_sharedWorld", false);
        Set(bridge, "_wo122Connected", true);
        Set(bridge, "_combatRoleApplied", true);
        bridge.EnterHostAbsentPause("host-left");

        Assert.Equal(true, Invoke(bridge, "Wo122WantLock"));
    }

    [Fact]
    public void A_different_epoch_from_the_relay_cannot_change_the_held_world()
    {
        var bridge = new GameBridge(new ClientConfig());
        var epochs = Get<Wo206Epochs>(bridge, "_w206");
        Assert.True(epochs.LearnWorld(142, 2));
        var run = Guid.NewGuid();
        bridge.HostAbsentOnEpoch(142, run);
        bridge.EnterHostAbsentPause("host-left");
        Set(bridge, "_myGhostId", (byte)5);
        string text = WorldReconnectFacts.EpochProof(Wo206Rules.EpochText(143, 2, "tick"), run, 9);
        byte[] frame = new LootMsg(Protocol.W203Epoch, 0, text).BuildUp(Protocol.W203Up, 5);

        Assert.Equal(true, Invoke(bridge, "Wo206TakeAtRead", (int)Protocol.W203Down, (byte[])[7, .. frame.AsSpan(3)]));

        Assert.Equal(142u, epochs.World);
        Assert.True(bridge.HostAbsentPause);
    }

    [Fact]
    public async Task One_refused_native_renewal_keeps_the_same_pause_beyond_two_intervals()
    {
        long now = 0;
        await using var native = new JoinCalendarTestPipe(nativeClock: () => Interlocked.Read(ref now), refusedRenewals: 1,
            controlTime: new ManualFrameTime());
        await using var engine = new JoinCalendarTestEngine(Path.Combine(Path.GetTempPath(), $"kct-host-hold-{Guid.NewGuid():N}.log"),
            Path.GetTempPath(), null, native.ObserveGameplayStarted);
        var bridge = new GameBridge(new ClientConfig());
        Set(bridge, "_combat", native.Pipe);
        Set(bridge, "_transport", engine.Transport);
        Set(bridge, "_loadSequenceRuntime", true);
        Set(bridge, "_joinedWorld", true);
        var sequence = Get<WorldLoadSequence>(bridge, "_worldLoad");
        Assert.True(sequence.TryBegin(0, WorldLoadStart.AdoptLoadedWorld, 142));
        bridge.EnterHostAbsentPause("host-left");
        Assert.Equal(true, Invoke(bridge, "LoadSequenceTryHoldLink", false));
        Assert.True(await (Task<bool>)Invoke(bridge, "LoadSequenceSyncNativeAsync", true)!);
        Assert.True(native.NativePauseEffective);

        long serial = Get<long>(bridge, "_w211HoldSerial");
        long due = 40_000;
        foreach (long tick in new long[] { 41_000, 46_000, 80_001, 90_000, 101_000 })
        {
            Interlocked.Exchange(ref now, tick);
            Assert.True(native.NativePauseEffective, $"the native pause expired at {tick} ms before the scheduled renew at {due} ms");
            due = await (ValueTask<long>)Invoke(bridge, "Wo211AdvanceHostAbsentHoldAsync", serial, tick, due)!;
        }
        Assert.Equal(3, native.RenewalAttempts);
        Assert.Equal(1, native.PauseStarts);

        Assert.Equal(true, Invoke(bridge, "LoadSequenceTryResumeLink", 142u, true, 0ul, null!));
        Assert.True(await (Task<bool>)Invoke(bridge, "LoadSequenceSyncNativeAsync", false)!);
        await native.ReleaseObserved.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(native.NativePauseEffective);
        Console.WriteLine("host_pause_duration_ms=101000 renew_attempts=3 renew_refused=1 pause_starts=1 released=1");
    }

    private static T Get<T>(GameBridge bridge, string field) => (T)typeof(GameBridge).GetField(field, Instance)!.GetValue(bridge)!;
    private static void Set(GameBridge bridge, string field, object value) => typeof(GameBridge).GetField(field, Instance)!.SetValue(bridge, value);
    private static object? Invoke(GameBridge bridge, string method, params object[] args) => typeof(GameBridge).GetMethod(method, Instance)!.Invoke(bridge, args);
}
