// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using KcdMp.Wire;

namespace KcdMp.Client.Tests;

public sealed partial class JoinCalendarIntegrationTests
{
    [Fact]
    public async Task Guest_in_its_own_world_does_not_claim_after_an_announced_host_leaves()
    {
        using var fixture = new SaveFixture();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = stop.Token;
        await using var relay = await ReplayRelay.StartAsync(ct);
        await using var host = await ReplayPeer.ConnectAsync(relay.Port, "announced-host", ct);
        await using var peer = await ReplayPeer.ConnectAsync(relay.Port, "guest", ct);
        var link = new JoinWire(peer.Wire, await peer.DrainAsync(ct), dropFirstSnapshot: false);
        await using var pipe = new JoinCalendarTestPipe();
        await using var engine = new JoinCalendarTestEngine(Path.Combine(fixture.Root, "guest.log"), fixture.GuestSaves, null, pipe.ObserveGameplayStarted);
        var guest = CreateBridge(peer.Id, false, link, pipe.Pipe, engine.Transport, fixture.GuestSaves, ct, relay.Port);
        Set(guest, "_sharedWorld", true);
        Set(guest, "_w144ConnectedUtc", DateTime.UtcNow.AddMinutes(-1));
        var receive = InvokeAsync(guest, "ReceiveLoopAsync", link, ct);
        try
        {
            await host.SendAsync(JoinStatusCodec.Build(peer.Id, 0, Protocol.JoinStateSession, Protocol.JoinReasonId("shared-world"), 0), ct);
            engine.Emit("wo124_where", "world");
            await WaitAsync(() => Get<bool>(guest, "_hostModeKnown") && Get<object>(guest, "_where").ToString() == "World",
                "the host's mode and the guest's private world", ct);
            Assert.False((bool)Call(guest, "Wo127ClaimsHost")!);

            await host.DisposeAsync();
            await WaitAsync(() => !Get<bool>(guest, "_hostModeKnown"), "the guest to consume the host disconnect", ct);
            bool claims = (bool)Call(guest, "Wo127ClaimsHost")!;
            Console.WriteLine($"F1 guest-private-world=1 host-disconnected=1 claim={(claims ? 1 : 0)}");
            Assert.False(claims);
        }
        finally
        {
            await stop.CancelAsync();
            try { await receive; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task Five_fresh_host_runs_prompt_the_same_guest_process_to_leave_and_explicitly_rejoin()
    {
        using var fixture = new SaveFixture();
        byte[] ownSave = File.ReadAllBytes(fixture.GuestOwnSave);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var ct = stop.Token;
        await using var relay = await ReplayRelay.StartAsync(ct);
        await using var initial = await ReplayPeer.ConnectAsync(relay.Port, "restart-host", ct);
        await using var peer = await ReplayPeer.ConnectAsync(relay.Port, "restart-guest", ct);
        await using var pipe = new JoinCalendarTestPipe();
        await using var engine = new JoinCalendarTestEngine(Path.Combine(fixture.Root, "guest-restarts.log"), fixture.GuestSaves, null, pipe.ObserveGameplayStarted);
        var link = new JoinWire(peer.Wire, await peer.DrainAsync(ct), dropFirstSnapshot: false);
        var guest = CreateBridge(peer.Id, false, link, pipe.Pipe, engine.Transport, fixture.GuestSaves, ct, relay.Port);
        Set(guest, "_w184SaveOnLeave", false);
        Set(guest, "_autoRequests", 99);
        engine.Emit("wo124_where", "menu");
        Call(guest, "Wo125OnLauncherChoice", "bring");
        var readGuest = InvokeAsync(guest, "ReceiveLoopAsync", link, ct);
        var runs = new HashSet<Guid>();
        try
        {
            for (int generation = 0; generation <= 5; generation++)
            {
                await using var hostPeer = generation == 0 ? initial : await ReplayPeer.ConnectAsync(relay.Port, "restart-host", ct);
                var hostLink = new JoinWire(hostPeer.Wire, await hostPeer.DrainAsync(ct), dropFirstSnapshot: false);
                await using var hostPipe = new JoinCalendarTestPipe();
                await using var hostEngine = new JoinCalendarTestEngine(Path.Combine(fixture.Root, $"restart-host-{generation}.log"),
                    fixture.HostSaves, fixture.MakeHostSave, hostPipe.ObserveGameplayStarted);
                var host = CreateBridge(hostPeer.Id, true, hostLink, hostPipe.Pipe, hostEngine.Transport, fixture.HostSaves, ct, relay.Port);
                Assert.True(runs.Add(Get<Guid>(host, "_worldReconnectInstance")));
                hostEngine.SaveWritten = path => Call(host, "OnSaveFileEvent", path);
                hostEngine.Emit("wo124_where", "world");
                Call(host, "Wo125HostIdentify", fixture.HostLoadedSave, true, "fixture starting world");
                using var hostStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var readHost = InvokeAsync(host, "ReceiveLoopAsync", hostLink, hostStop.Token);
                try
                {
                    await WaitAsync(() => Get<bool>(host, "_combatRoleApplyComplete") && Get<bool>(guest, "_combatRoleApplyComplete") &&
                            Get<WorldLoadSequence>(host, "_worldLoad").Current.Phase == WorldLoadPhase.Play,
                        "the host's connection and physically loaded world", ct);

                    byte[] position = new byte[Protocol.PositionPayloadLen];
                    position[16] = Protocol.PositionFlagHostClaim;
                    await hostPeer.SendAsync(ReplayPeer.Frame(Protocol.Position, position), ct);
                    if (generation != 0) Assert.False(Get<bool>(host, "_isDamageAuthority"));
                    await InvokeAsync(host, "Wo206AnnounceAsync", "fixture-restart", (byte?)peer.Id);
                    await WaitAsync(() => Get<bool>(host, "_isDamageAuthority"), "the privately proven host role", ct);
                    await InvokeAsync(host, "Wo124HostModeTickAsync");
                    await InvokeAsync(host, "Wo206AnnounceAsync", "fixture-ready", (byte?)peer.Id);
                    await WaitAsync(() => Get<bool>(guest, "_hostModeKnown"), "the new host's mode", ct);

                    if (generation == 0)
                    {
                        string ticketPath = (string)Call(host, "HostRestartTicketPath")!;
                        await WaitAsync(() => File.Exists(ticketPath), "the first host's persisted relay proof", ct);
                    }
                    else
                    {
                        await WaitAsync(() => Get<string>(guest, "_joinUiState") == "host-restarted", "the restart prompt", ct);
                        Assert.Equal(generation, engine.JoinLoadsIssued);
                        Assert.Equal(generation - 1, engine.OwnLoadsIssued);
                        Assert.True(guest.HostAbsentPause);
                        Assert.True(Property<bool>(guest, "LinkHeld"));
                        Assert.True(Get<bool>(guest, "_joinedWorld"));
                        Assert.True((bool)Call(guest, "Wo122WantLock")!);
                        Assert.False(Get<bool>(guest, "_isDamageAuthority"));
                        engine.Emit("join_request", "");
                        Assert.Equal(generation, engine.JoinLoadsIssued);
                        engine.Emit("w211_leave", "menu");
                        await WaitForAbandonedArrivalAsync(guest, engine, ct);
                        Assert.Equal(generation, engine.OwnLoadsIssued);
                        Assert.Equal(generation, engine.JoinLoadsIssued);
                        Assert.False(guest.HostAbsentPause);
                        Assert.Equal(ownSave, File.ReadAllBytes(fixture.GuestOwnSave));
                        await InvokeAsync(host, "Wo206AnnounceAsync", "fixture-after-leave", (byte?)peer.Id);
                    }

                    await WaitAsync(() => Get<Wo206Epochs>(guest, "_w206").World != 0, "the host world epoch", ct);
                    engine.Emit("join_request", "");
                    await engine.WaitForJoinLoadAsync(ct);
                    await WaitAsync(() => Get<bool>(guest, "_joinedWorld") && hostLink.ReadyReceived.Count == 1 &&
                        Get<object?>(host, "_hostJoin") is null, "the normal calendar, load and Ready pipeline", ct);
                    Assert.Equal(generation + 1, engine.JoinLoadsIssued);
                    Assert.False(Get<bool>(guest, "_isDamageAuthority"));
                    Assert.Equal(ownSave, File.ReadAllBytes(fixture.GuestOwnSave));
                    Console.WriteLine($"F2-GUEST cycle={generation} join_loads={engine.JoinLoadsIssued} own_loads={engine.OwnLoadsIssued} own_bytes_unchanged=1");
                    if (generation == 5) break;
                    hostPeer.FinishSending();
                    await WaitAsync(() => guest.HostAbsentPause && Property<bool>(guest, "LinkHeld") &&
                        !Get<bool>(guest, "_hostModeKnown"), "the original host world to pause again", ct);
                }
                finally
                {
                    await hostStop.CancelAsync();
                    await InvokeAsync(host, "Wo123OnDisconnectAsync");
                    try { await readHost; } catch (OperationCanceledException) { }
                }
            }
            Assert.Equal(6, runs.Count);
            Assert.Equal(5, engine.OwnLoadsIssued);
        }
        finally
        {
            Call(guest, "LoadSequenceOnExit");
            await stop.CancelAsync();
            await InvokeAsync(guest, "Wo123OnDisconnectAsync");
            try { await readGuest; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public void Host_restart_ticket_material_is_excluded_from_session_recording()
    {
        string token = Convert.ToHexString(new byte[HostRestartWire.TicketBytes]);
        byte[] proof = new LootMsg(Protocol.W203HostRestartProof, 0, HostRestartWire.Proof(new(1, Guid.NewGuid()),
            new byte[HostRestartWire.TicketBytes])).BuildUp(Protocol.W203Up, Protocol.JoinTargetHost);
        byte[] ticket = new LootMsg(Protocol.W203HostRestartTicket, 0, token).BuildUp(Protocol.W203Up, 1);
        Assert.False(TestRecordingOptions.AllowsPacket(proof));
        Assert.False(TestRecordingOptions.AllowsFrame(Protocol.W203Down, new byte[] { Protocol.JoinTargetHost }.Concat(ticket.AsSpan(3).ToArray()).ToArray()));
    }
}
