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
}
