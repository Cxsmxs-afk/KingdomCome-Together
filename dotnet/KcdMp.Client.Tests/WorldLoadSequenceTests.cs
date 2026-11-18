// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
namespace KcdMp.Client.Tests;

public sealed class WorldLoadSequenceTests
{
    [Fact]
    public void The_join_offer_owns_its_generation_through_load_reconstruction_settle_and_play()
    {
        long now = 10;
        var state = new WorldLoadSequence(() => now);
        Assert.Equal(WorldLoadPhase.Menu, state.Current.Phase);
        Assert.False(state.IsCurrent(0));

        now = 15;
        Assert.True(state.TryBegin(0, WorldLoadStart.Offer, out var offer, hostWorld: 7, offerId: 91));
        Assert.Equal(1ul, offer.Generation);
        Assert.Equal(WorldLoadPhase.Offer, offer.Phase);
        Assert.True(state.IsCurrent(offer.Generation));
        Assert.False(state.CanApply(offer.Generation));
        Assert.False(state.TryAdvance(offer.Generation, WorldLoadPhase.Play));

        now = 25;
        Assert.True(state.TryBegin(offer.Generation, WorldLoadStart.Offer, out var repeated, hostWorld: 7, offerId: 91));
        Assert.Equal(offer.Generation, repeated.Generation);
        Assert.Equal(10, repeated.ElapsedMs);
        Assert.True(state.TryAdvance(offer.Generation, WorldLoadPhase.Offer, WorldLoadPhase.Load));
        Assert.True(state.TryBegin(offer.Generation, WorldLoadStart.Offer, hostWorld: 7, offerId: 91));
        Assert.Equal(WorldLoadPhase.Load, state.Current.Phase);

        now = 125;
        Assert.True(state.TryAdvance(offer.Generation, WorldLoadPhase.Load, WorldLoadPhase.Reconstruct));
        Assert.False(state.TryAdvance(offer.Generation, WorldLoadPhase.Load, WorldLoadPhase.Reconstruct));
        now = 155;
        Assert.True(state.TryAdvance(offer.Generation, WorldLoadPhase.Reconstruct, WorldLoadPhase.Settle));
        Assert.False(state.CanApply(offer.Generation));
        now = 255;
        Assert.True(state.TryAdvance(offer.Generation, WorldLoadPhase.Settle, WorldLoadPhase.Play));
        Assert.True(state.CanApply(offer.Generation));
        Assert.False(state.TryAbort(offer.Generation));

        Assert.Equal(5, state.PhaseMilliseconds(WorldLoadPhase.Menu));
        Assert.Equal(10, state.PhaseMilliseconds(WorldLoadPhase.Offer));
        Assert.Equal(100, state.PhaseMilliseconds(WorldLoadPhase.Load));
        Assert.Equal(30, state.PhaseMilliseconds(WorldLoadPhase.Reconstruct));
        Assert.Equal(100, state.PhaseMilliseconds(WorldLoadPhase.Settle));
        Assert.Equal(offer.Generation, state.Current.Generation);
        Assert.Equal(7u, state.Current.HostWorld);

        now = 260;
        Assert.True(state.TryAdvance(offer.Generation, WorldLoadPhase.Play, WorldLoadPhase.Exit));
        Assert.False(state.IsCurrent(offer.Generation));
        Assert.False(state.CanApply(offer.Generation));
        now = 264;
        Assert.True(state.TryAdvance(offer.Generation, WorldLoadPhase.Exit, WorldLoadPhase.Menu));
        Assert.Equal(5, state.PhaseMilliseconds(WorldLoadPhase.Play));
        Assert.Equal(4, state.PhaseMilliseconds(WorldLoadPhase.Exit));
        Assert.Equal(0u, state.Current.HostWorld);
    }

    [Fact]
    public void The_host_uses_the_same_phases_after_a_direct_local_load_and_duplicate_engine_edges_do_not_restart_it()
    {
        long now = 100;
        var state = new WorldLoadSequence(() => now);
        Assert.True(state.TryBegin(0, WorldLoadStart.LocalLoad, out var load));
        Assert.Equal(WorldLoadPhase.Load, load.Phase);
        Assert.True(state.TrySetHostWorld(load.Generation, 4));
        Assert.False(state.TrySetHostWorld(load.Generation, 5));
        now = 178;
        Assert.True(state.TryBegin(load.Generation, WorldLoadStart.LocalLoad, out var duplicate, hostWorld: 4));
        Assert.Equal(load.Generation, duplicate.Generation);
        Assert.Equal(78, duplicate.ElapsedMs);
        Assert.True(state.TryAdvance(load.Generation, WorldLoadPhase.Reconstruct));
        Assert.True(state.TryAdvance(load.Generation, WorldLoadPhase.Settle));
        Assert.True(state.TryAdvance(load.Generation, WorldLoadPhase.Play));
        Assert.True(state.CanApply(load.Generation));

        Assert.True(state.TryBegin(load.Generation, WorldLoadStart.LocalLoad, out var replacement, hostWorld: 5));
        Assert.Equal(2ul, replacement.Generation);
        Assert.False(state.IsCurrent(load.Generation));
        Assert.True(state.IsCurrent(replacement.Generation));
        Assert.False(state.CanApply(replacement.Generation));
        Assert.False(state.TryAdvance(load.Generation, WorldLoadPhase.Reconstruct));
    }

    [Theory]
    [InlineData(WorldLoadPhase.Offer)]
    [InlineData(WorldLoadPhase.Load)]
    [InlineData(WorldLoadPhase.Reconstruct)]
    [InlineData(WorldLoadPhase.Settle)]
    [InlineData(WorldLoadPhase.Exit)]
    public void A_pending_failure_retires_frames_even_before_a_replacement_attempt(WorldLoadPhase failed)
    {
        var state = new WorldLoadSequence(() => 0);
        Assert.True(state.TryBegin(0, WorldLoadStart.Offer, out var offer, hostWorld: 4, offerId: 31));
        if (failed == WorldLoadPhase.Exit) Assert.True(state.TryAdvance(offer.Generation, WorldLoadPhase.Exit));
        else
        {
            for (var phase = WorldLoadPhase.Load; phase <= failed; phase++)
                Assert.True(state.TryAdvance(offer.Generation, phase));
        }
        Assert.False(state.TryAbort(offer.Generation, WorldLoadPhase.Menu));
        Assert.True(state.TryAbort(offer.Generation, failed));
        Assert.Equal(WorldLoadPhase.Menu, state.Current.Phase);
        Assert.False(state.IsCurrent(offer.Generation));
        Assert.False(state.TryAdvance(offer.Generation, WorldLoadPhase.Load));
        Assert.True(state.TryBegin(offer.Generation, WorldLoadStart.Offer, out var replacement, hostWorld: 4, offerId: 32));
        Assert.Equal(offer.Generation + 1, replacement.Generation);
        Assert.False(state.TryAbort(offer.Generation));
        Assert.Equal(WorldLoadPhase.Offer, state.Current.Phase);
    }

    [Fact]
    public void Same_world_reconnect_unparks_play_and_a_proven_new_host_epoch_enters_a_fresh_load()
    {
        var state = new WorldLoadSequence(() => 0);
        Assert.True(state.TryBegin(0, WorldLoadStart.AdoptLoadedWorld, out var active));
        Assert.True(state.TrySetHostWorld(active.Generation, 7));
        Assert.True(state.TryHoldLink(active.Generation));
        Assert.False(state.TryHoldLink(active.Generation));
        Assert.True(state.Current.LinkHeld);
        Assert.False(state.CanApply(active.Generation));
        Assert.False(state.TryReconnect(active.Generation, 0));
        Assert.False(state.TryReconnect(active.Generation, 8));
        Assert.True(state.TryReconnect(active.Generation, 7));
        Assert.False(state.TryReconnect(active.Generation, 7));
        Assert.Equal(active.Generation, state.Current.Generation);
        Assert.False(state.Current.LinkHeld);
        Assert.True(state.CanApply(active.Generation));
        Assert.Equal(0, state.PhaseMilliseconds(WorldLoadPhase.Load));

        Assert.True(state.TryHoldLink(active.Generation));
        Assert.False(state.TryBegin(active.Generation, WorldLoadStart.HostReload, hostWorld: 6));
        Assert.True(state.TryBegin(active.Generation, WorldLoadStart.HostReload, hostWorld: 7));
        Assert.True(state.Current.LinkHeld);
        Assert.True(state.TryBegin(active.Generation, WorldLoadStart.HostReload, out var reload, hostWorld: 8));
        Assert.Equal(active.Generation + 1, reload.Generation);
        Assert.Equal(WorldLoadPhase.Load, reload.Phase);
        Assert.False(reload.LinkHeld);
        Assert.False(state.TryReleaseLink(active.Generation));
        Assert.False(state.TryReconnect(active.Generation, 7));
        Assert.False(state.IsCurrent(active.Generation));
        Assert.True(state.TryBegin(reload.Generation, WorldLoadStart.HostReload, hostWorld: 8));
        Assert.Equal(reload.Generation, state.Current.Generation);
    }

    [Fact]
    public void The_joiner_parks_a_new_host_world_before_the_offer_and_adopts_the_matching_offer_without_another_restart()
    {
        long now = 5;
        var state = new WorldLoadSequence(() => now);
        Assert.True(state.TryBegin(0, WorldLoadStart.AdoptLoadedWorld, out var old, hostWorld: 11));
        Assert.True(state.TryBegin(old.Generation, WorldLoadStart.Offer, out var observed, hostWorld: 12));
        Assert.Equal(old.Generation + 1, observed.Generation);
        Assert.False(state.IsCurrent(old.Generation));
        Assert.False(state.CanApply(observed.Generation));

        now = 11;
        Assert.True(state.TryBegin(observed.Generation, WorldLoadStart.Offer, out var actual, hostWorld: 12, offerId: 53));
        Assert.Equal(observed.Generation, actual.Generation);
        Assert.Equal(6, actual.ElapsedMs);
        Assert.True(state.TryBegin(actual.Generation, WorldLoadStart.Offer, hostWorld: 12));
        Assert.True(state.TryAdvance(actual.Generation, WorldLoadPhase.Load));
        Assert.True(state.TryBegin(actual.Generation, WorldLoadStart.Offer, hostWorld: 12, offerId: 53));
        Assert.Equal(WorldLoadPhase.Load, state.Current.Phase);
    }

    [Fact]
    public void A_racing_restart_accepts_exactly_one_request_and_rejects_completions_from_every_prior_generation()
    {
        var state = new WorldLoadSequence(() => 1);
        Assert.True(state.TryBegin(0, WorldLoadStart.LocalLoad, out var first));
        int accepted = 0;
        Parallel.For(0, 32, _ =>
        {
            if (state.TryBegin(first.Generation, WorldLoadStart.SupersedingLoad)) Interlocked.Increment(ref accepted);
        });
        Assert.Equal(1, accepted);
        var second = state.Current;
        Assert.Equal(first.Generation + 1, second.Generation);
        Assert.False(state.TryAdvance(first.Generation, WorldLoadPhase.Reconstruct));
        Assert.False(state.TryAbort(first.Generation));
        Assert.False(state.IsCurrent(first.Generation));
        Assert.True(state.TryAdvance(second.Generation, WorldLoadPhase.Reconstruct));
    }

    [Fact]
    public void Phase_elapsed_never_moves_backward_or_resets_on_repeated_observations()
    {
        long now = 20;
        var state = new WorldLoadSequence(() => now);
        Assert.True(state.TryBegin(0, WorldLoadStart.LocalLoad, out var loading));
        now = 50;
        Assert.Equal(30, state.Current.ElapsedMs);
        now = 30;
        Assert.True(state.TryAdvance(loading.Generation, WorldLoadPhase.Load));
        Assert.Equal(30, state.Current.ElapsedMs);
        Assert.True(state.TryAdvance(loading.Generation, WorldLoadPhase.Reconstruct));
        now = 55;
        Assert.Equal(30, state.PhaseMilliseconds(WorldLoadPhase.Load));
        Assert.Equal(5, state.PhaseMilliseconds(WorldLoadPhase.Reconstruct));
    }

    [Fact]
    public void Private_load_preparation_rolls_back_only_its_exact_intent_and_carries_into_its_own_load()
    {
        long now = 100;
        var state = new WorldLoadSequence(() => now);
        Assert.True(state.TryBegin(0, WorldLoadStart.AdoptLoadedWorld, out var loaded, hostWorld: 5));
        Assert.True(state.SetOwnIntent(loaded.Generation, WorldLoadOwnIntent.SharedSaveGrace, out var grace, deadlineMs: 250));
        Assert.True(grace.OwnGraceActive);
        Assert.True(state.CanApply(loaded.Generation));

        Assert.True(state.SetOwnIntent(loaded.Generation, WorldLoadOwnIntent.ExpectedPrivate, out var first, out var beforeFirst));
        Assert.False(state.CanApply(loaded.Generation));
        Assert.True(state.TryRestoreOwnIntent(first, beforeFirst));
        Assert.True(state.CanApply(loaded.Generation));
        Assert.Equal(250, state.Current.OwnIntentDeadlineMs);
        now = 250;
        Assert.False(state.Current.OwnGraceActive);

        Assert.True(state.SetOwnIntent(loaded.Generation, WorldLoadOwnIntent.DifferentSaveGrace, out var graceUpdate, deadlineMs: 500));
        Assert.True(state.SetOwnIntent(loaded.Generation, WorldLoadOwnIntent.ExpectedPrivate, out var preparing, out var priorGrace));
        Assert.False(state.TryRestoreOwnIntent(preparing, grace));
        Assert.True(state.TryRestoreOwnIntent(preparing, priorGrace));
        Assert.Equal(graceUpdate.OwnIntentDeadlineMs, state.Current.OwnIntentDeadlineMs);
        Assert.False(state.CanApply(loaded.Generation));
        Assert.False(state.TryUpdateOwnIntent(graceUpdate, WorldLoadOwnIntent.None));
        Assert.False(state.TryAdvance(graceUpdate, WorldLoadPhase.Exit));
        Assert.True(state.TryUpdateOwnIntent(state.Current, WorldLoadOwnIntent.DifferentSaveGrace, deadlineMs: 550));
        Assert.Equal(550, state.Current.OwnIntentDeadlineMs);

        Assert.True(state.SetOwnIntent(loaded.Generation, WorldLoadOwnIntent.ExpectedPrivate, out preparing, out priorGrace));
        Assert.True(state.TryAdvance(preparing, WorldLoadPhase.Exit));
        Assert.True(state.TryBegin(loaded.Generation, WorldLoadStart.LocalLoad, out var pending));
        Assert.Equal(WorldLoadOwnIntent.ExpectedPrivate, pending.OwnIntent);
        Assert.False(state.TryRestoreOwnIntent(preparing, priorGrace));
        Assert.False(state.SetOwnIntent(loaded.Generation, WorldLoadOwnIntent.None));
        Assert.True(state.TryAbort(pending.Generation));
        Assert.Equal(WorldLoadOwnIntent.None, state.Current.OwnIntent);
    }
}
