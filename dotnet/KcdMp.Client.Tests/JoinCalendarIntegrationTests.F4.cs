// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using KcdMp.Wire;

namespace KcdMp.Client.Tests;

public sealed partial class JoinCalendarIntegrationTests
{
    private enum F4Probe { TransferAbort, NoOwnExit, HostAbort }

    [Theory]
    [InlineData("A", "B")]
    [InlineData("B", "A")]
    public Task Player_cancel_retires_five_started_transfers_before_any_load_and_the_next_join_succeeds(string host, string guest) =>
        RunAsync(host, guest, dropFirstSnapshot: false, f4: F4Probe.TransferAbort);

    [Fact]
    public Task Five_leaves_without_an_own_save_use_the_exact_mpexit_receipt_and_release_the_file() =>
        RunAsync("A", "B", dropFirstSnapshot: false, f4: F4Probe.NoOwnExit);

    [Fact]
    public Task Five_host_aborts_during_transfer_clear_the_attempt_and_the_guest_can_join_again() =>
        RunAsync("A", "B", dropFirstSnapshot: false, f4: F4Probe.HostAbort);

    private static byte[] F4LargeHostSave(int sequence)
    {
        var (host, _) = WhsSaveTests.Pair();
        byte[] payload = new byte[100_000];
        new Random(17151).NextBytes(payload);
        host.SaveTime = 1_790_000_000 + sequence;
        host.World = $"<Roots><!--{Convert.ToBase64String(payload)}--></Roots>";
        return WhsSaveTests.File(host);
    }

    private static Task TestF4LoopAsync(F4Probe probe, SaveFixture fixture, GameBridge host, GameBridge guest,
        JoinWire hostLink, JoinWire guestLink, JoinCalendarTestPipe guestPipe, JoinCalendarTestEngine hostEngine,
        JoinCalendarTestEngine engine, CancellationToken ct) => probe switch
        {
            F4Probe.TransferAbort => TestF4TransfersAsync(fixture, host, guest, hostLink, guestLink, guestPipe, engine, engine, ct),
            F4Probe.HostAbort => TestF4TransfersAsync(fixture, host, guest, hostLink, guestLink, guestPipe, hostEngine, engine, ct),
            F4Probe.NoOwnExit => TestF4MenuExitAsync(fixture, host, guest, hostLink, guestPipe, engine, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(probe))
        };

    private static async Task TestF4TransfersAsync(SaveFixture fixture, GameBridge host, GameBridge guest,
        JoinWire hostLink, JoinWire guestLink, JoinCalendarTestPipe guestPipe, JoinCalendarTestEngine canceller,
        JoinCalendarTestEngine engine, CancellationToken ct)
    {
        byte[] ownBefore = SHA256.HashData(File.ReadAllBytes(fixture.GuestOwnSave));
        var before = F4Resources(guest, guestPipe);
        var cancelled = new HashSet<uint>();
        bool hostCancels = !ReferenceEquals(canceller, engine);
        var incomingAbort = hostCancels ? guestLink.Aborts : hostLink.Aborts;
        guestLink.DropIncoming = packet => packet.Type == Protocol.WorldChunkDown &&
            Protocol.TrySplitJoinDown(packet.Payload, out _, out _, out _, out var body) &&
            BinaryPrimitives.ReadUInt32LittleEndian(body) != 0;
        try
        {
            for (int i = 1; i <= 5; i++)
            {
                engine.Emit("join_request", "");
                await WaitAsync(() => Get<WorldReceiver?>(guest, "_joinRx")?.Next == 1,
                    "one chunk of the actual TCP world transfer to be staged", ct);
                var receiving = Get<WorldReceiver>(guest, "_joinRx");
                Assert.True(cancelled.Add(receiving.JoinId));
                Assert.InRange(receiving.Received, 1, receiving.Offer.Size - 1);
                Assert.True(File.Exists(receiving.PartPath));
                Assert.Equal(0, engine.JoinLoadsIssued);
                Assert.Equal(0, guestPipe.ArmRequests);

                canceller.Emit("join_cancel", "");
                await WaitAsync(() => incomingAbort.Contains(receiving.JoinId) && Get<object?>(host, "_hostJoin") is null &&
                    Get<string>(guest, "_joinUiState") == "aborted" && !File.Exists(receiving.PartPath),
                    "the player's cancellation to reach the other agent and remove its partial transfer", ct);
                Assert.Null(Get<object?>(guest, "_joinRx"));
                Assert.Null(Get<object?>(guest, "_joinPreparation"));
                Assert.Null(Get<object?>(guest, "_jj"));
                Assert.Null(Get<object?>(guest, "_joinCalendarRequest"));
                Assert.Equal(0u, Get<uint>(guest, "_joinOutId"));
                Assert.False(File.Exists(receiving.FinalPath));
                Assert.Empty(guestLink.ReadySent);
                Assert.Empty(Directory.EnumerateFiles(fixture.GuestSaves, "mp*.whs", SearchOption.AllDirectories));
                Assert.Equal(ownBefore, SHA256.HashData(File.ReadAllBytes(fixture.GuestOwnSave)));
                Console.WriteLine($"F4-TRANSFER canceller={(hostCancels ? "host" : "guest")} cycle={i} join=0x{receiving.JoinId:x8} staged={receiving.Received}/{receiving.Offer.Size} load=0 ready=0 staging_exists=0 own_sha256_equal=1");
            }
        }
        finally { guestLink.DropIncoming = null; }
        AssertF4Resources("transfer", before, guest, guestPipe);

        engine.Emit("join_request", "");
        await engine.WaitForJoinLoadAsync(ct);
        await WaitAsync(() => Get<bool>(guest, "_joinedWorld") && Get<object?>(host, "_hostJoin") is null,
            "the next world load and Ready on the same guest reader", ct);
        uint ready = Assert.Single(guestLink.ReadySent);
        Assert.DoesNotContain(ready, cancelled);
        Assert.Equal(ready, Assert.Single(hostLink.ReadyReceived));
        Assert.Equal(1, engine.JoinLoadsIssued);
        Assert.Equal(ownBefore, SHA256.HashData(File.ReadAllBytes(fixture.GuestOwnSave)));
        Console.WriteLine($"F4-TRANSFER rejoin=0x{ready:x8} load=1 ready=1 own_sha256_equal=1");
    }

    private static async Task TestF4MenuExitAsync(SaveFixture fixture, GameBridge host, GameBridge guest,
        JoinWire hostLink, JoinCalendarTestPipe guestPipe, JoinCalendarTestEngine engine, CancellationToken ct)
    {
        string ownSave = fixture.GuestOwnSave;
        string savedOutsideProfile = Path.Combine(fixture.Root, "own-file-test-archive");
        byte[] ownBefore = SHA256.HashData(File.ReadAllBytes(ownSave));
        Set(guest, "_w184SaveOnLeave", false);
        engine.DeferExitFileRead = true;
        var exits = new HashSet<string>();
        var before = F4Resources(guest, guestPipe);
        for (int i = 1; i <= 5; i++)
        {
            Call(guest, "Wo125OnLauncherChoice", "bring");
            engine.Emit("join_request", "");
            string incoming = await engine.WaitForJoinLoadAsync(ct);
            await WaitAsync(() => Get<bool>(guest, "_joinedWorld") && hostLink.ReadyReceived.Count == i && Get<object?>(host, "_hostJoin") is null,
                "the target join to send its confirmed Ready before leaving", ct);
            Assert.Equal(ownBefore, SHA256.HashData(File.ReadAllBytes(ownSave)));
            File.Move(ownSave, savedOutsideProfile);
            try
            {
                Assert.Empty(GameBridge.ListOwnSaves(fixture.GuestSaves));
                engine.Emit("w211_leave", "menu");
                string exit = await engine.WaitForExitLoadAsync(ct);
                Assert.True(exits.Add(exit));
                Assert.True(Get<bool>(guest, "_leaveInProgress"));
                Assert.True((bool)Call(guest, "Wo122WantLock")!);
                Assert.Equal(0, engine.OwnLoadsIssued);
                byte[] exitHash = SHA256.HashData(File.ReadAllBytes(exit));
                guest.Wo125Sweep("F4 unconfirmed exit");
                Assert.Equal(exitHash, SHA256.HashData(File.ReadAllBytes(exit)));
                engine.CompleteExitLoad(exit);
                await WaitAsync(() => !Get<bool>(guest, "_leaveInProgress") && !File.Exists(exit) &&
                    !File.Exists(incoming) && Get<object?>(guest, "_wo125ActiveExitPath") is null &&
                    Get<object>(guest, "_where").ToString() == "Menu", "confirmed mpexit and file release", ct);
                Assert.False(Property<bool>(guest, "OwnLoadExpected"));
                Assert.False(Get<bool>(guest, "_joinedWorld"));
                Assert.False((bool)Call(guest, "Wo122WantLock")!);
                Assert.Null(Get<object?>(guest, "_wo125ActiveExitPath"));
                Assert.Null(Get<object?>(guest, "_pinnedHomeLoad"));
                Assert.Empty(Directory.EnumerateFiles(fixture.GuestSaves, "mp*.whs", SearchOption.AllDirectories));
                Assert.Empty(Directory.EnumerateFiles(WorldReceiver.DefaultStagingDir(), "world-*"));
                Assert.Equal(ownBefore, SHA256.HashData(File.ReadAllBytes(savedOutsideProfile)));
                Console.WriteLine($"F4-MPEXIT cycle={i} join_loads={engine.JoinLoadsIssued} own_loads=0 terminal=menu files=0 own_sha256_equal=1");
            }
            finally { File.Move(savedOutsideProfile, ownSave); }
        }
        Assert.Equal(5, engine.JoinLoadsIssued);
        Assert.Equal(5, exits.Count);
        Assert.Equal(ownBefore, SHA256.HashData(File.ReadAllBytes(ownSave)));
        AssertF4Resources("mpexit", before, guest, guestPipe);
    }

    private readonly record struct F4Owned(int PipeConnections, int Reservations, int Timers);

    private static (int Threads, int Handles, F4Owned Owned) F4Resources(GameBridge guest, JoinCalendarTestPipe pipe)
    {
        using var process = Process.GetCurrentProcess();
        var retained = Get<object>(guest, "_retainedJoinWorlds");
        int leases = (int)retained.GetType().GetProperty("Count")!.GetValue(retained)!;
        leases += Get<object?>(guest, "_joinRx") is null ? 0 : 1;
        leases += Get<object?>(guest, "_pinnedHomeLoad") is null ? 0 : 1;
        leases += Get<object?>(guest, "_wo125ActiveExitPath") is null ? 0 : 1;
        int timers = Get<bool>(guest, "_rejoinPending") ? 1 : 0;
        return (process.Threads.Count, process.HandleCount, new(pipe.Pipe.IsConnected ? 1 : 0, leases, timers));
    }

    private static void AssertF4Resources(string scenario, (int Threads, int Handles, F4Owned Owned) before,
        GameBridge guest, JoinCalendarTestPipe pipe)
    {
        var after = F4Resources(guest, pipe);
        Console.WriteLine($"F4-RESOURCES scenario={scenario} threads={before.Threads}->{after.Threads} handles={before.Handles}->{after.Handles} " +
            $"guest_pipe={before.Owned.PipeConnections}->{after.Owned.PipeConnections} tracked_leases={before.Owned.Reservations}->{after.Owned.Reservations} " +
            $"rejoin_timer={before.Owned.Timers}->{after.Owned.Timers}");
        Assert.Equal(before.Owned, after.Owned);
    }
}
