// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
namespace KcdMp.Client.Tests;

public sealed partial class JoinCalendarIntegrationTests
{
    [Fact]
    public Task Five_missing_return_terminals_use_one_probe_and_keep_unverified_saves_locked() =>
        RunAsync("A", "B", dropFirstSnapshot: false, JoinProbe.ReturnDeadlineLoop);

    private static async Task TestPlayerJoinLoopAsync(SaveFixture fixture, GameBridge host, GameBridge guest,
        JoinWire hostLink, JoinWire guestLink, JoinCalendarTestEngine engine, CancellationToken ct, bool returnDeadline = false)
    {
        byte[] own = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(fixture.GuestOwnSave));
        Set(guest, "_w184SaveOnLeave", false);
        Set(guest, "_autoRequests", 99);
        Set(guest, "_needsMenuTold", true);
        Set(guest, "_w202RejoinFails", 3);
        engine.Emit("wo124_where", "world");
        for (int cycle = 1; cycle <= 5; cycle++)
        {
            int readyBefore = guestLink.ReadySent.Count;
            if (!returnDeadline)
            {
                engine.DeferJoinFileRead = true;
                engine.Emit("join_request", "");
                string file = await engine.WaitForJoinLoadAsync(ct);
                object attempt = Get<object>(guest, "_jj");
                uint joinId = GetVisible<uint>(attempt, "JoinId");
                byte[] placed = File.ReadAllBytes(file);
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
            }

            engine.DeferJoinFileRead = false;
            Assert.Equal("World", Get<object>(guest, "_where").ToString());
            Assert.False(Property<bool>(guest, "OwnLoadExpected"));
            Assert.False(Property<bool>(guest, "LinkHeld"));
            engine.Emit("join_request", "");
            await engine.WaitForJoinLoadAsync(ct);
            await WaitAsync(() => hostLink.ReadyReceived.Count == readyBefore + 1 &&
                Get<bool>(guest, "_joinedWorld") && Get<object?>(host, "_hostJoin") is null,
                "explicit rejoin from the same private world", ct);
            await engine.WaitForJoinedNotificationAsync(ct);
            Assert.Equal(0, Get<int>(guest, "_autoRequests"));
            Assert.False(Get<bool>(guest, "_needsMenuTold"));
            Assert.Equal(0, Get<int>(guest, "_w202RejoinFails"));
            Assert.False(Get<bool>(guest, "_rejoinPending"));
            engine.DeferOwnFileRead = true;
            engine.Emit("w211_leave", "menu");
            Assert.Equal(fixture.GuestOwnSave, await engine.WaitForOwnLoadAsync(ct));
            Assert.False(Get<bool>(guest, "_joinedWorld"));
            Assert.True(Get<bool>(guest, "_leaveInProgress"));
            Assert.True((bool)Call(guest, "Wo122WantLock")!);
            if (returnDeadline)
            {
                int queries = engine.WhereQueries;
                if (cycle != 2) engine.ReadOwnSaveWithoutTerminal();
                else engine.Emit("wo124_where", "world"); // still the shared world, with no read of the private file
                if (cycle == 4)
                    engine.LastLoadedPathOverride = Path.Combine(fixture.GuestSaves, "playline2", Path.GetFileName(fixture.GuestOwnSave));
                Set(guest, "_leaveSinceUtc", DateTime.UtcNow.AddMinutes(-6));
                await InvokeAsync(guest, "Wo124JoinerTickAsync");
                Console.WriteLine($"F3-RETURN cycle={cycle} checks={engine.WhereQueries - queries} locked={Call(guest, "Wo122WantLock")}");
                Assert.Equal(queries + 1, engine.WhereQueries);
                await InvokeAsync(guest, "Wo124JoinerTickAsync");
                Assert.Equal(queries + 1, engine.WhereQueries);
                if (cycle % 2 == 0)
                {
                    Assert.Equal("return-needed", Call(guest, "JoinMenuPhase"));
                    Assert.True((bool)Call(guest, "Wo122WantLock")!);
                    Assert.NotNull(Get<object?>(guest, "_pinnedHomeLoad"));
                    await WaitAsync(() => engine.ReturnRetryVisible, "the local Leave retry to reach the menu", ct);
                    if (cycle == 2) engine.ReadOwnSaveWithoutTerminal();
                    engine.LastLoadedPathOverride = null;
                    engine.Emit("w211_leave", "menu");
                    await WaitAsync(() => engine.WhereQueries == queries + 2, "one explicit return recheck", ct);
                    await WaitAsync(() => !engine.ReturnRetryVisible, "the local retry to clear", ct);
                }
            }
            else engine.CompleteOwnLoad();
            await WaitForPrivateReturnAsync(guest, ct);
            engine.DeferOwnFileRead = false;
            Assert.False((bool)Call(guest, "Wo122WantLock")!);
            Assert.Equal(returnDeadline ? cycle : cycle * 2, engine.OwnLoadsIssued);
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
