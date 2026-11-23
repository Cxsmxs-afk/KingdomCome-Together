// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
namespace KcdMp.Client.Tests;

public sealed partial class JoinCalendarIntegrationTests
{
    private static async Task TestPlayerJoinLoopAsync(SaveFixture fixture, GameBridge host, GameBridge guest,
        JoinWire hostLink, JoinWire guestLink, JoinCalendarTestEngine engine, CancellationToken ct)
    {
        byte[] own = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(fixture.GuestOwnSave));
        Set(guest, "_w184SaveOnLeave", false);
        Set(guest, "_autoRequests", 99);
        Set(guest, "_needsMenuTold", true);
        Set(guest, "_w202RejoinFails", 3);
        engine.Emit("wo124_where", "world");
        for (int cycle = 1; cycle <= 5; cycle++)
        {
            engine.DeferJoinFileRead = true;
            engine.Emit("join_request", "");
            string file = await engine.WaitForJoinLoadAsync(ct);
            object attempt = Get<object>(guest, "_jj");
            uint joinId = GetVisible<uint>(attempt, "JoinId");
            byte[] placed = File.ReadAllBytes(file);
            int readyBefore = guestLink.ReadySent.Count;
            engine.Emit("join_cancel", "");
            await WaitAsync(() => Get<object?>(guest, "_jj") is not { } active ||
                GetVisible<string>(active, "Phase") == "orphaned", "player cancellation to retire Ready", ct);
            Console.WriteLine($"W8-PLAYER-CANCEL cycle={cycle} join=0x{joinId:x8} active={Get<object?>(guest, "_jj") is not null} ready={guestLink.ReadySent.Count - readyBefore}");
            Assert.Same(attempt, Get<object?>(guest, "_jj"));
            engine.Emit("join_cancel", "");
            Assert.Equal(placed, File.ReadAllBytes(file));
            Assert.Equal(cycle * 2 - 2, engine.OwnLoadsIssued);
            engine.CompleteJoinLoad();
            await WaitForAbandonedArrivalAsync(guest, engine, ct);
            await WaitAsync(() => Get<object?>(host, "_hostJoin") is null, "host after player abort", ct);
            Assert.DoesNotContain(joinId, guestLink.ReadySent);
            Assert.False(File.Exists(file));
            Assert.Equal(own, System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(fixture.GuestOwnSave)));

            engine.DeferJoinFileRead = false;
            Assert.Equal("World", Get<object>(guest, "_where").ToString());
            Assert.False(Property<bool>(guest, "OwnLoadExpected"));
            Assert.False(Property<bool>(guest, "LinkHeld"));
            engine.Emit("join_request", "");
            await engine.WaitForJoinLoadAsync(ct);
            await WaitAsync(() => hostLink.ReadyReceived.Count == readyBefore + 1 &&
                Get<bool>(guest, "_joinedWorld") && Get<object?>(host, "_hostJoin") is null,
                "explicit rejoin from the same private world", ct);
            Assert.Equal(0, Get<int>(guest, "_autoRequests"));
            Assert.False(Get<bool>(guest, "_needsMenuTold"));
            Assert.Equal(0, Get<int>(guest, "_w202RejoinFails"));
            Assert.False(Get<bool>(guest, "_rejoinPending"));
            engine.Emit("w211_leave", "menu");
            await WaitAsync(() => !Get<bool>(guest, "_joinedWorld") && Get<bool>(guest, "_leaveInProgress"),
                "departure before the private load", ct);
            Assert.True((bool)Call(guest, "Wo122WantLock")!);
            await WaitForAbandonedArrivalAsync(guest, engine, ct);
            Assert.False((bool)Call(guest, "Wo122WantLock")!);
            Assert.Equal(cycle * 2, engine.OwnLoadsIssued);
            Assert.False(Get<bool>(guest, "_isDamageAuthority"));
            Assert.Null(Get<object?>(guest, "_pinnedHomeLoad"));
            Assert.Null(Get<object?>(guest, "_joinRx"));
            Assert.Null(Get<object?>(guest, "_joinPreparation"));
            Assert.Empty(Directory.EnumerateFiles(fixture.GuestSaves, "mp*.whs", SearchOption.AllDirectories));
            Assert.Empty(Directory.EnumerateFiles(WorldReceiver.DefaultStagingDir(), "world-*"));
            Assert.Equal(own, System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(fixture.GuestOwnSave)));
            Console.WriteLine($"W8-PLAYER-REJOIN cycle={cycle} join_loads={engine.JoinLoadsIssued} own_loads={engine.OwnLoadsIssued} pending=0 temporary_saves=0 own_save_unchanged=1");
        }
    }
}
