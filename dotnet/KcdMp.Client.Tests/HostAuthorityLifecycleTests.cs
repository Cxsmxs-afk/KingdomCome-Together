// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using Xunit;

namespace KcdMp.Client.Tests;

public class HostAuthorityLifecycleTests
{
    [Fact]
    public async Task Query_Load_role2_reconfirm_Gameplay_waits_native_five_seconds_and_is_idempotent()
    {
        var native = new Native(); var agent = new HostAuthorityLifecycle(native.Send);
        Assert.True(await agent.PendingAsync(true, true, default)); ulong generation = agent.Current.Generation;
        Assert.Equal(18ul, generation); Assert.False(native.Active);
        Assert.True(native.Role(2, generation));
        Assert.True(await agent.PendingAsync(true, false, default)); // ACK immediately before the actual load command
        Assert.True(await agent.GameplayAsync(generation, default));
        native.Now = 4999; Assert.True(await agent.RefreshAsync(default)); Assert.False(agent.Current.Active);
        Assert.True(native.Role(2, generation)); Assert.True(await agent.GameplayAsync(generation, default));
        native.Now = 5000; Assert.True(await agent.RefreshAsync(default)); Assert.True(agent.Current.Active);
        Assert.Equal(5000u, agent.Current.GameplayAgeMs);
    }

    [Fact]
    public async Task No_Gameplay_never_activates_even_after_a_long_load()
    {
        var native = new Native(); var agent = new HostAuthorityLifecycle(native.Send);
        Assert.True(await agent.PendingAsync(true, true, default)); native.Role(2, agent.Current.Generation);
        native.Now = 300_000; Assert.True(await agent.RefreshAsync(default)); Assert.False(agent.Current.Active);
    }

    [Fact]
    public async Task Reload_invalidates_old_Gameplay_and_old_queued_role_changes()
    {
        var native = new Native(); var agent = new HostAuthorityLifecycle(native.Send);
        await agent.PendingAsync(true, true, default); ulong old = agent.Current.Generation;
        native.Role(2, old); await agent.GameplayAsync(old, default); native.Now = 10_000; await agent.RefreshAsync(default);
        Assert.True(agent.Current.Active);
        Assert.True(await agent.PendingAsync(true, true, default));
        Assert.False(native.Active); Assert.False(native.Role(2, old)); Assert.False(await agent.GameplayAsync(old, default));
        Assert.False(agent.Current.GameplaySent); Assert.True(agent.Current.Generation > old);
    }

    [Fact]
    public async Task Off_without_hooks_can_join_legacy_but_only_after_explicit_role_transition()
    {
        var native = new Native { Hooks = false }; var agent = new HostAuthorityLifecycle(native.Send);
        Assert.True(await agent.PendingAsync(false, true, default)); ulong generation = agent.Current.Generation;
        Assert.False(native.Active); Assert.False(native.Legacy);
        Assert.True(native.Role(0, generation)); Assert.False(native.Legacy);
        Assert.True(native.Role(2, generation)); Assert.True(native.Legacy);
        Assert.False(await agent.GameplayAsync(generation, default));
        Assert.True(await agent.PendingAsync(false, false, default)); Assert.True(native.Legacy); // duplicate Disabled is idempotent
        Assert.True(await agent.PendingAsync(false, true, default)); Assert.False(native.Legacy); // new load clears it off-main
        Assert.False(native.Role(2, generation)); // stale queued main-thread role2 cannot relatch the new load
    }

    [Fact]
    public async Task Agent_restart_queries_native_generation_before_new_load()
    {
        var native = new Native { Generation = 9000 }; var agent = new HostAuthorityLifecycle(native.Send);
        Assert.Equal(9000ul, await agent.KnownGenerationAsync(default));
        Assert.True(await agent.PendingAsync(true, true, default)); Assert.Equal(9001ul, agent.Current.Generation);
    }

    [Fact]
    public async Task Disabled_reset_followed_by_user_on_resume_confirms_Load_before_role2_so_legacy_never_starts()
    {
        var native = new Native(); var agent = new HostAuthorityLifecycle(native.Send);
        await agent.PendingAsync(true, true, default); native.Role(2, agent.Current.Generation);
        await agent.GameplayAsync(agent.Current.Generation, default); native.Now = 10_000;
        await agent.PendingAsync(false, true, default); native.Role(0, agent.Current.Generation);
        Assert.True(await agent.PendingAsync(true, true, default));
        Assert.True(native.Role(2, agent.Current.Generation)); Assert.False(native.Legacy); Assert.False(native.Active);
        Assert.True(await agent.GameplayAsync(agent.Current.Generation, default));
        native.Now = 14999; await agent.RefreshAsync(default); Assert.False(agent.Current.Active);
        native.Now = 15000; await agent.RefreshAsync(default); Assert.True(agent.Current.Active); Assert.False(native.Legacy);
    }

    [Fact]
    public async Task A_late_pending_ACK_cannot_confirm_a_newer_load()
    {
        var first = new TaskCompletionSource<HostAuthorityLifecycle.Ack?>(TaskCreationOptions.RunContinuationsAsynchronously);
        int loads = 0;
        var agent = new HostAuthorityLifecycle((phase, generation, _) =>
        {
            if (phase == HostAuthorityLifecycle.Query) return Task.FromResult<HostAuthorityLifecycle.Ack?>(new(true, 1, true, false, 10, 0));
            if (++loads == 1) return first.Task;
            return Task.FromResult<HostAuthorityLifecycle.Ack?>(new(true, phase, true, false, generation, 0));
        });
        var old = agent.PendingAsync(true, true, default); ulong oldGeneration = agent.Current.Generation;
        Assert.True(await agent.PendingAsync(true, true, default)); ulong current = agent.Current.Generation;
        first.SetResult(new(true, 1, true, false, oldGeneration, 0));
        Assert.False(await old); Assert.Equal(current, agent.Current.Generation); Assert.True(agent.Current.Confirmed);
    }

    [Fact]
    public async Task Cancelled_pending_cannot_claim_confirmation_when_ACK_arrives_late()
    {
        var reply = new TaskCompletionSource<HostAuthorityLifecycle.Ack?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource();
        var agent = new HostAuthorityLifecycle((phase, _, _) => phase == HostAuthorityLifecycle.Query
            ? Task.FromResult<HostAuthorityLifecycle.Ack?>(new(true, 1, true, false, 0, 0)) : reply.Task);
        var pending = agent.PendingAsync(true, true, stop.Token); stop.Cancel();
        reply.SetResult(new(true, 1, true, false, agent.Current.Generation, 0));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(agent.Current.Confirmed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task One_lost_pending_ACK_retries_same_generation_and_only_replays_real_Gameplay(bool gameplayObserved)
    {
        var native = new Native(); int pendingCalls = 0;
        var agent = new HostAuthorityLifecycle(async (phase, generation, ct) =>
        {
            var ack = await native.Send(phase, generation, ct);
            return phase == HostAuthorityLifecycle.Load && ++pendingCalls == 1 ? null : ack;
        });
        Assert.False(await agent.PendingAsync(true, true, default)); ulong generation = agent.Current.Generation;
        native.Role(2, generation);
        Assert.True(await agent.RetryAsync(gameplayObserved, default));
        Assert.Equal(generation, agent.Current.Generation); Assert.Equal(gameplayObserved, agent.Current.GameplaySent);
        native.Now = 5000; await agent.RefreshAsync(default); Assert.Equal(gameplayObserved, agent.Current.Active);
        if (gameplayObserved)
        {
            native.Now = 6000; Assert.True(await agent.RetryAsync(true, default));
            await agent.RefreshAsync(default); Assert.Equal(6000u, agent.Current.GameplayAgeMs);
        }
    }

    [Fact]
    public async Task Native_reconnect_phase_loss_recovers_with_new_generation_and_stored_Gameplay()
    {
        var native = new Native(); var agent = new HostAuthorityLifecycle(native.Send);
        await agent.PendingAsync(true, true, default); ulong old = agent.Current.Generation;
        native.Role(2, old); await agent.GameplayAsync(old, default);
        native.LoseGameplay();
        Assert.False(await agent.RefreshAsync(default)); Assert.False(agent.Current.Confirmed);
        Assert.True(await agent.RetryAsync(true, default)); Assert.True(agent.Current.Generation > old);
        native.Role(2, agent.Current.Generation); native.Now = 5000;
        await agent.RefreshAsync(default); Assert.True(agent.Current.Active);
    }

    [Fact]
    public async Task A_lost_initial_query_retries_the_requested_mode_instead_of_staying_generation_zero()
    {
        var native = new Native(); bool firstQuery = true;
        var agent = new HostAuthorityLifecycle((phase, generation, ct) =>
        {
            if (phase == HostAuthorityLifecycle.Query && firstQuery) { firstQuery = false; return Task.FromResult<HostAuthorityLifecycle.Ack?>(null); }
            return native.Send(phase, generation, ct);
        });
        Assert.False(await agent.PendingAsync(true, true, default)); Assert.Equal(0ul, agent.Current.Generation);
        Assert.True(await agent.RetryAsync(true, default)); Assert.True(agent.Current.Enabled); Assert.True(agent.Current.GameplaySent);
        native.Role(2, agent.Current.Generation); native.Now = 5000; await agent.RefreshAsync(default); Assert.True(agent.Current.Active);
    }

    [Fact]
    public async Task A_later_off_request_wins_even_when_initial_query_kept_the_old_on_request_waiting()
    {
        var seed = new TaskCompletionSource<HostAuthorityLifecycle.Ack?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var native = new Native(); int queries = 0;
        var agent = new HostAuthorityLifecycle((phase, generation, ct) =>
            phase == HostAuthorityLifecycle.Query && Interlocked.Increment(ref queries) == 1 ? seed.Task : native.Send(phase, generation, ct));
        var oldOn = agent.PendingAsync(true, true, default);
        var newOff = agent.PendingAsync(false, true, default);
        seed.SetResult(new(true, HostAuthorityLifecycle.Load, true, false, native.Generation, 0));
        Assert.False(await oldOn); Assert.True(await newOff); Assert.False(agent.Current.Enabled);
        Assert.True(native.Role(0, agent.Current.Generation)); Assert.True(native.Role(2, agent.Current.Generation)); Assert.True(native.Legacy);
    }

    [Fact]
    public void Actual_engine_timeout_is_an_event_and_mod_text_or_settle_is_not()
    {
        var tail = new LogTailGameTransport(new HttpGameTransport("http://127.0.0.1:1"), "unused.log");
        int timeouts = 0; tail.PostLoadReconstructionTimeout += () => timeouts++;
        Feed(tail, "Gameplay started");
        Feed(tail, "[KCD2-MP] P1 synthetic settle timeout");
        Feed(tail, "[KCD2-MP] Loading screen timeouted while still in post load reconstruction!");
        Assert.Equal(0, timeouts);
        Feed(tail, "Loading screen timeouted while still in post load reconstruction!");
        Assert.Equal(1, timeouts);
    }

    private delegate void SpanLine(ReadOnlySpan<char> line);
    private static void Feed(LogTailGameTransport tail, string text)
    {
        var method = typeof(LogTailGameTransport).GetMethod("ProcessLine", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        ((SpanLine)Delegate.CreateDelegate(typeof(SpanLine), tail, method))(text.AsSpan());
    }

    // Deterministic model of the native op168 contract. Native tests cover the actual rules implementation.
    private sealed class Native
    {
        public ulong Generation = 17;
        public long Now;
        public bool Hooks = true, Legacy;
        private byte _phase = HostAuthorityLifecycle.Load, _role;
        private bool _enabled = true, _loadAccepted;
        private long _gameplay = -1;
        public bool Active => Hooks && _enabled && _role == 2 && _phase == HostAuthorityLifecycle.Gameplay && _gameplay >= 0 && Now - _gameplay >= 5000;
        public void LoseGameplay() { _phase = HostAuthorityLifecycle.Load; _gameplay = -1; _loadAccepted = false; }
        public bool Role(byte role, ulong generation)
        {
            if (generation != Generation || (role == 2 && _enabled && !Hooks)) return false;
            if (role == 2 && _role != 2 && !_enabled) Legacy = true;
            _role = role; return true;
        }
        public Task<HostAuthorityLifecycle.Ack?> Send(byte phase, ulong generation, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); bool accepted = true;
            if (phase is HostAuthorityLifecycle.Load or HostAuthorityLifecycle.Disabled)
            {
                if (generation > Generation)
                {
                    Generation = generation; _phase = phase; _enabled = phase != HostAuthorityLifecycle.Disabled;
                    _gameplay = -1; Legacy = false; _loadAccepted = phase == HostAuthorityLifecycle.Load;
                }
                else accepted = generation == Generation && phase == _phase;
            }
            else if (phase == HostAuthorityLifecycle.Gameplay)
            {
                accepted = generation == Generation && _enabled && _loadAccepted;
                if (accepted && _phase != phase) { _phase = phase; _gameplay = Now; }
            }
            uint age = _gameplay < 0 ? 0 : (uint)(Now - _gameplay);
            return Task.FromResult<HostAuthorityLifecycle.Ack?>(new(accepted, _phase, _enabled, Active, Generation, age));
        }
    }
}
