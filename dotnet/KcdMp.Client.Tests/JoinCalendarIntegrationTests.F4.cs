// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using KcdMp.Wire;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace KcdMp.Client.Tests;

[CollectionDefinition("Join calendar resources", DisableParallelization = true)]
public sealed class JoinCalendarResourcesCollection { }

[Collection("Join calendar resources")]
public sealed class JoinCalendarResourceTests(ITestOutputHelper output)
{
    [Fact]
    public Task Five_leaves_without_an_own_save_recover_a_missing_terminal_from_the_live_menu() =>
        JoinCalendarIntegrationTests.RunF4MenuProbeAsync(output);

    [Fact]
    public Task An_answer_from_the_live_menu_cannot_confirm_a_superseded_exit_load() =>
        JoinCalendarIntegrationTests.RunF4MenuProbeAsync(output, supersedeLoad: true);

    [Fact]
    public async Task Five_leaves_with_one_retained_timer_each_fail_the_same_process_timer_budget()
    {
        var failure = await Assert.ThrowsAsync<TrueException>(() =>
            JoinCalendarIntegrationTests.RunF4MenuProbeAsync(output, retainTimers: true));
        Assert.Contains("F4 runtime timer budget", failure.Message);
        output.WriteLine("F4-TIMER-TWIN scenario=mpexit-retained-timer retained=5 result=budget-rejected");
    }

    [Fact]
    public async Task Processor_deadlines_are_attributed_and_cancelled_while_an_unowned_short_timer_remains()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var trace = new F4ProcessorDeadlines();
        using var clock = new HeldDeadlineDisposal(stop.Token);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unowned = Task.Delay(TimeSpan.FromMilliseconds(1950), new ManualFrameTime(), stop.Token);
        Task<ProcessorFrameScope.Result>[] frames =
        [
            ProcessorFrameScope.RunAsync(_ => release.Task, 2000, stop.Token, clock),
            ProcessorFrameScope.RunAsync(_ => release.Task, 2000, stop.Token, clock)
        ];
        try
        {
            Task<bool>[] deadlines = trace.Pending();
            Assert.Equal(frames.Length, deadlines.Length);
            Assert.DoesNotContain(unowned, deadlines);
            release.SetResult();
            await clock.Disposing.WaitAsync(stop.Token);
            // Task.Delay reports cancellation before calling ITimer.Dispose.
            // Both timers still belong to live scopes until disposal returns.
            Assert.Equal(frames.Length, trace.Pending().Length);
            clock.Release();
            var finished = await Task.WhenAll(frames);
            Assert.All(finished, result => Assert.Equal(ProcessorFrameScope.Outcome.Completed, result.Status));
            Assert.All(await Task.WhenAll(deadlines), cancelled => Assert.True(cancelled));
            Assert.False(unowned.IsCompleted);
            output.WriteLine($"F4-TIMER-ORIGIN cancelled={deadlines.Length} unowned_active=1 owner=ProcessorFrameScope");
        }
        finally
        {
            clock.Release();
            release.TrySetResult();
            await Task.WhenAll(frames);
            await stop.CancelAsync();
        }
    }

    private sealed class HeldDeadlineDisposal(CancellationToken stop) : TimeProvider, IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        private readonly TaskCompletionSource _disposing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationToken _stop = stop;
        private int _entered;

        public Task Disposing => _disposing.Task;
        public void Release() => _release.Set();
        public void Dispose() => _release.Dispose();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            new TestTimer(this, TimeProvider.System.CreateTimer(callback, state, dueTime, period));

        private sealed class TestTimer(HeldDeadlineDisposal owner, ITimer timer) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => timer.Change(dueTime, period);

            public void Dispose()
            {
                if (Interlocked.Increment(ref owner._entered) == 2) owner._disposing.SetResult();
                try { owner._release.Wait(owner._stop); }
                finally { timer.Dispose(); }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}

public sealed partial class JoinCalendarIntegrationTests
{
    private enum F4Probe { TransferAbort, NoOwnExit, NoOwnExitWithMenuProbe, SupersededMenuProbe, HostAbort, MissingOwnSave, ActiveGuest, TransportLost, HostReturn, RetainedTimer }

    internal static Task RunF4MenuProbeAsync(ITestOutputHelper output, bool supersedeLoad = false, bool retainTimers = false) =>
        RunAsync("A", "B", dropFirstSnapshot: false, f4: supersedeLoad ? F4Probe.SupersededMenuProbe
            : retainTimers ? F4Probe.RetainedTimer : F4Probe.NoOwnExitWithMenuProbe,
            f4Output: output);

    [Theory]
    [InlineData("A", "B")]
    [InlineData("B", "A")]
    public Task Player_cancel_retires_five_started_transfers_before_any_load_and_the_next_join_succeeds(string host, string guest) =>
        RunAsync(host, guest, dropFirstSnapshot: false, f4: F4Probe.TransferAbort, f4Output: testOutput);

    [Fact]
    public Task Five_leaves_without_an_own_save_use_the_exact_mpexit_receipt_and_release_the_file() =>
        RunAsync("A", "B", dropFirstSnapshot: false, f4: F4Probe.NoOwnExit, f4Output: testOutput);

    [Fact]
    public Task Five_host_aborts_during_transfer_clear_the_attempt_and_the_guest_can_join_again() =>
        RunAsync("A", "B", dropFirstSnapshot: false, f4: F4Probe.HostAbort, f4Output: testOutput);

    [Fact]
    public Task Five_missing_own_saves_refuse_a_join_then_accept_the_next_attempt_after_restoring_the_save() =>
        RunAsync("A", "B", dropFirstSnapshot: false, f4: F4Probe.MissingOwnSave, f4Output: testOutput);

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public Task Two_active_guests_repeat_join_and_private_return_with_overlapping_departures(int players) =>
        RunAsync(players == 3 ? "A" : "B", players == 3 ? "B" : "A", dropFirstSnapshot: false,
            extraPeers: players - 2, f4: F4Probe.ActiveGuest, f4Output: testOutput);

    [Fact]
    public Task Five_tcp_failures_during_transfer_can_retry_on_the_same_agent_and_engine() =>
        RunAsync("A", "B", dropFirstSnapshot: false, f4: F4Probe.TransportLost, f4Output: testOutput);

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public Task The_same_host_run_pauses_and_resumes_five_times_without_a_guest_load(int players) =>
        RunAsync("A", "B", dropFirstSnapshot: false, extraPeers: players - 2, f4: F4Probe.HostReturn, f4Output: testOutput);

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
        JoinCalendarTestEngine engine, string hostLabel, CancellationToken ct, ITestOutputHelper output) => probe switch
        {
            F4Probe.TransferAbort => TestF4TransfersAsync(fixture, host, guest, hostLink, guestLink, guestPipe, engine, engine,
                $"transfer-player-{hostLabel.ToLowerInvariant()}", ct, output),
            F4Probe.HostAbort => TestF4TransfersAsync(fixture, host, guest, hostLink, guestLink, guestPipe, hostEngine, engine,
                "transfer-host", ct, output),
            F4Probe.NoOwnExit or F4Probe.NoOwnExitWithMenuProbe or F4Probe.SupersededMenuProbe or F4Probe.RetainedTimer =>
                TestF4MenuExitAsync(fixture, host, guest, hostLink, engine, ct, output, probe),
            F4Probe.MissingOwnSave => TestF4MissingOwnSaveAsync(fixture, host, guest, hostLink, guestLink, engine, ct, output),
            _ => throw new ArgumentOutOfRangeException(nameof(probe))
        };

    private static async Task TestF4TransfersAsync(SaveFixture fixture, GameBridge host, GameBridge guest,
        JoinWire hostLink, JoinWire guestLink, JoinCalendarTestPipe guestPipe, JoinCalendarTestEngine canceller,
        JoinCalendarTestEngine engine, string scenario, CancellationToken ct, ITestOutputHelper output)
    {
        byte[] ownBefore = SHA256.HashData(File.ReadAllBytes(fixture.GuestOwnSave));
        using var resources = new F4ResourceCheck(scenario, guest, output);
        var cancelled = new HashSet<uint>();
        bool hostCancels = !ReferenceEquals(canceller, engine);
        var incomingAbort = hostCancels ? guestLink.Aborts : hostLink.Aborts;
        guestLink.DropIncoming = DropAfterFirstWorldChunk;
        try
        {
            for (int i = 0; i <= 5; i++)
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
                await resources.CycleAsync(i, ct, host);
            }
        }
        finally { guestLink.DropIncoming = null; }
        resources.AssertBudget();

        engine.Emit("join_request", "");
        await engine.WaitForJoinLoadAsync(ct);
        await WaitAsync(() => Get<bool>(guest, "_joinedWorld") && Get<object?>(host, "_hostJoin") is null,
            "the next world load and Ready on the same guest reader", ct);
        await engine.WaitForJoinedNotificationAsync(ct);
        uint ready = Assert.Single(guestLink.ReadySent);
        Assert.DoesNotContain(ready, cancelled);
        Assert.Equal(ready, Assert.Single(hostLink.ReadyReceived));
        Assert.Equal(1, engine.JoinLoadsIssued);
        Assert.Equal(ownBefore, SHA256.HashData(File.ReadAllBytes(fixture.GuestOwnSave)));
        Console.WriteLine($"F4-TRANSFER rejoin=0x{ready:x8} load=1 ready=1 own_sha256_equal=1");
    }

    private static bool DropAfterFirstWorldChunk(ReplayPacket packet) => packet.Type == Protocol.WorldChunkDown &&
        Protocol.TrySplitJoinDown(packet.Payload, out _, out _, out _, out var body) &&
        BinaryPrimitives.ReadUInt32LittleEndian(body) != 0;

    private static async Task TestF4TransportLossAsync(SaveFixture fixture, int port, string guestName,
        GameBridge host, GameBridge guest, JoinWire hostLink, ReplayPeer firstPeer, JoinWire firstLink, Task firstRead,
        JoinCalendarTestEngine game, CancellationToken ct, ITestOutputHelper output)
    {
        byte[] ownHash = SHA256.HashData(File.ReadAllBytes(fixture.GuestOwnSave));
        ReplayPeer peer = firstPeer;
        JoinWire link = firstLink;
        Task read = firstRead;
        var abandoned = new HashSet<uint>();
        using var resources = new F4ResourceCheck("transfer-eof", guest, output);
        try
        {
            for (int i = 0; i <= 5; i++)
            {
                link.DropIncoming = DropAfterFirstWorldChunk;
                game.Emit("join_request", "");
                await WaitAsync(() => Get<WorldReceiver?>(guest, "_joinRx")?.Next == 1,
                    "the interrupted connection's first chunk to reach the production receiver", ct);
                var transfer = Get<WorldReceiver>(guest, "_joinRx");
                Assert.True(abandoned.Add(transfer.JoinId));
                Assert.True(File.Exists(transfer.PartPath));

                peer.FinishSending();
                await read.WaitAsync(ct);
                Assert.True(link.EndOfStreamObserved);
                await InvokeAsync(guest, "Wo122OnDisconnectAsync");
                await InvokeAsync(guest, "Wo123OnDisconnectAsync");
                await InvokeAsync(guest, "Wo124OnDisconnectAsync");
                await WaitAsync(() => Get<object?>(host, "_hostJoin") is null && !((List<byte>)Call(host, "LivePartners")!).Contains(peer.Id),
                    "the host to clear the lost guest's attempt and roster entry", ct);
                Assert.Equal(0u, Get<uint>(guest, "_joinOutId"));
                Assert.Null(Get<object?>(guest, "_joinCalendarRequest"));
                Assert.Null(Get<object?>(guest, "_joinRx"));
                Assert.False(File.Exists(transfer.PartPath));
                Assert.Empty(link.ReadySent);
                Assert.Equal(0, game.JoinLoadsIssued);
                Assert.Equal(ownHash, SHA256.HashData(File.ReadAllBytes(fixture.GuestOwnSave)));
                Console.WriteLine($"F4-TCP-LOSS cycle={i} join=0x{transfer.JoinId:x8} eof=1 stage=0 load=0 ready=0 own_sha256_equal=1");

                if (!ReferenceEquals(peer, firstPeer)) await peer.DisposeAsync();
                peer = await ReplayPeer.ConnectAsync(port, guestName, ct);
                await peer.SendAsync(Protocol.BuildLinkHelloUp(Protocol.LinkCapWorldSync), ct);
                link = new JoinWire(peer.Wire, await peer.DrainAsync(ct), dropFirstSnapshot: false);
                Set(guest, "_myGhostId", peer.Id);
                Set(guest, "_wo122Stream", link);
                Set(guest, "_wo122Connected", true);
                Set(guest, "_combatRoleApplied", false);
                Set(guest, "_combatRoleApplyComplete", false);
                read = InvokeAsync(guest, "ReceiveLoopAsync", link, ct);
                await WaitAsync(() => ((List<byte>)Call(host, "LivePartners")!).Contains(peer.Id),
                    "the host to observe the new guest connection", ct);
                await InvokeAsync(host, "Wo124HostModeTickAsync");
                await WaitAsync(() => Get<bool>(guest, "_combatRoleApplyComplete") && Get<bool>(guest, "_hostModeKnown"),
                    "the new TCP connection to receive the real relay role and host mode", ct);
                Assert.False(Get<bool>(guest, "_isDamageAuthority"));
                await resources.CycleAsync(i, ct, host);
            }
            resources.AssertBudget();

            game.Emit("join_request", "");
            await game.WaitForJoinLoadAsync(ct);
            await WaitAsync(() => Get<bool>(guest, "_joinedWorld") && Get<object?>(host, "_hostJoin") is null,
                "confirmed same-process rejoin after five TCP interruptions", ct);
            await game.WaitForJoinedNotificationAsync(ct);
            uint actual = Assert.Single(link.ReadySent);
            Assert.DoesNotContain(actual, abandoned);
            Assert.Equal(actual, Assert.Single(hostLink.ReadyReceived));
            Assert.Equal(1, game.JoinLoadsIssued);
            Assert.Equal(ownHash, SHA256.HashData(File.ReadAllBytes(fixture.GuestOwnSave)));
            Console.WriteLine($"F4-TCP-LOSS rejoin=0x{actual:x8} load=1 ready=1 own_sha256_equal=1");
        }
        finally
        {
            await peer.DisposeAsync();
            await read.WaitAsync(ct);
        }
    }

    private static async Task TestF4HostReturnsAsync(SaveFixture fixture, int port, string hostName, GameBridge host, GameBridge guest,
        ReplayPeer hostPeer, Task hostRead, byte guestId, JoinCalendarTestPipe pipe, JoinCalendarTestEngine game, int players,
        CancellationToken ct, ITestOutputHelper output)
    {
        byte[] original = SHA256.HashData(File.ReadAllBytes(fixture.GuestOwnSave));
        uint world = Get<Wo206Epochs>(guest, "_w206").World;
        Get<System.Collections.Concurrent.ConcurrentDictionary<string, bool>>(guest, "_w221On")["world_recovery"] = false;
        await WaitAsync(() =>
        {
            Call(guest, "LoadSequenceTick");
            return Get<WorldLoadSequence>(guest, "_worldLoad").Current.Phase == WorldLoadPhase.Play;
        }, "the already joined world to settle", ct);
        hostPeer.FinishSending();
        await hostRead.WaitAsync(ct);
        using var resources = new F4ResourceCheck($"host-return-{players}", guest, output);
        for (int i = 0; i <= 6; i++)
        {
            await WaitAsync(() => guest.HostAbsentPause && Property<bool>(guest, "LinkHeld") && !Get<bool>(guest, "_hostModeKnown"),
                "the real host socket loss to pause the same guest world", ct);
            await WaitAsync(() => pipe.NativePauseEffective, "the native test pipe to acknowledge this pause", ct);
            Assert.True(Get<bool>(guest, "_joinedWorld"));
            Assert.True((bool)Call(guest, "Wo122WantLock")!);
            await CheckReturningHostAsync(port, hostName, host, guest, guestId, pipe, ct, observeReleased: () =>
            {
                Assert.Equal(world, Get<Wo206Epochs>(guest, "_w206").World);
                Assert.Equal(1, game.JoinLoadsIssued);
                Assert.Equal(0, game.OwnLoadsIssued);
                Assert.False(pipe.NativePauseEffective);
                Assert.Equal(original, SHA256.HashData(File.ReadAllBytes(fixture.GuestOwnSave)));
                Console.WriteLine($"F4-HOST-RETURN cycle={i} world={world} guest_join_loads=1 guest_own_loads=0 own_sha256_equal=1");
            });
            await WaitAsync(() => guest.HostAbsentPause && Property<bool>(guest, "LinkHeld") &&
                !Get<bool>(guest, "_hostModeKnown") && pipe.NativePauseEffective,
                "the returning connection to close and the next native hold to be active before sampling", ct);
            // The first complete return also starts the recurring hold and reader work.
            if (i > 0) await resources.CycleAsync(i - 1, ct, host);
        }
        resources.AssertBudget();
    }

    private static async Task TestF4MissingOwnSaveAsync(SaveFixture fixture, GameBridge host, GameBridge guest,
        JoinWire hostLink, JoinWire guestLink, JoinCalendarTestEngine engine, CancellationToken ct, ITestOutputHelper output)
    {
        string own = fixture.GuestOwnSave;
        string removed = Path.Combine(fixture.Root, "fixture-offline-save");
        byte[] hash = SHA256.HashData(File.ReadAllBytes(own));
        Set(guest, "_w184SaveOnLeave", false);
        using var resources = new F4ResourceCheck("missing-personal-save", guest, output);
        for (int i = 1; i <= 7; i++)
        {
            File.Move(own, removed);
            try
            {
                Call(guest, "Wo125OnLauncherChoice", "bring");
                await InvokeAsync(guest, "StartJoinRequestAsync", true);
                Assert.Equal("no-save", Get<string>(guest, "_joinUiState"));
                Assert.Null(Get<object?>(guest, "_joinPreparation"));
                Assert.Null(Get<object?>(guest, "_joinCalendarRequest"));
                Assert.Null(Get<object?>(host, "_hostJoin"));
                Assert.Equal(0u, Get<uint>(guest, "_joinOutId"));
                Assert.Equal(i - 1, engine.JoinLoadsIssued);
                Assert.Equal(i - 1, guestLink.ReadySent.Count);
            }
            finally { File.Move(removed, own); }

            Call(guest, "Wo125OnLauncherChoice", "bring");
            engine.Emit("join_request", "");
            await engine.WaitForJoinLoadAsync(ct);
            await WaitAsync(() => Get<bool>(guest, "_joinedWorld") && hostLink.ReadyReceived.Count == i && Get<object?>(host, "_hostJoin") is null,
                "a confirmed join after replacing the unavailable personal save", ct);
            await engine.WaitForJoinedNotificationAsync(ct);
            await InvokeAsync(guest, "LeaveSharedWorldAsync", "fixture", "return after checking the save");
            await WaitForAbandonedArrivalAsync(guest, engine, ct);
            Assert.Equal(i, engine.OwnLoadsIssued);
            Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(own)));
            Assert.Empty(Directory.EnumerateFiles(fixture.GuestSaves, "mp*.whs", SearchOption.AllDirectories));
            Console.WriteLine($"F4-MISSING-SAVE cycle={i} no_load_on_failure=1 rejoin_loads={engine.JoinLoadsIssued} ready={guestLink.ReadySent.Count} own_sha256_equal=1");
            // The first private return establishes the receiver's recurring work.
            if (i > 1) await resources.CycleAsync(i - 2, ct, host);
        }
        resources.AssertBudget();
    }

    private static async Task TestF4MenuExitAsync(SaveFixture fixture, GameBridge host, GameBridge guest,
        JoinWire hostLink, JoinCalendarTestEngine engine, CancellationToken ct, ITestOutputHelper output, F4Probe probe)
    {
        string ownSave = fixture.GuestOwnSave;
        string savedOutsideProfile = Path.Combine(fixture.Root, "own-file-test-archive");
        byte[] ownBefore = SHA256.HashData(File.ReadAllBytes(ownSave));
        Set(guest, "_w184SaveOnLeave", false);
        engine.DeferExitFileRead = true;
        var exits = new HashSet<string>();
        bool supersedeLoad = probe == F4Probe.SupersededMenuProbe;
        bool retainTimers = probe == F4Probe.RetainedTimer;
        string scenario = probe switch
        {
            F4Probe.NoOwnExit => "mpexit-receipt",
            F4Probe.RetainedTimer => "mpexit-retained-timer",
            _ => "mpexit"
        };
        using var resources = supersedeLoad ? null : new F4ResourceCheck(scenario, guest, output);
        int initialReceiptCycle = probe == F4Probe.NoOwnExit ? 1 : 0;
        int lastCycle = supersedeLoad ? 0 : 5 + initialReceiptCycle;
        using var outstanding = retainTimers ? new RetainedLeaveTimers() : null;
        // Load and return once before measuring so the initial runtime and
        // fixture setup is outside the five comparable cycles.
        for (int i = 0; i <= lastCycle; i++)
        {
            Call(guest, "Wo125OnLauncherChoice", "bring");
            engine.Emit("join_request", "");
            string incoming = await engine.WaitForJoinLoadAsync(ct);
            await WaitAsync(() => Get<bool>(guest, "_joinedWorld") && hostLink.ReadyReceived.Count == i + 1 && Get<object?>(host, "_hostJoin") is null,
                "the target join to send its confirmed Ready before leaving", ct);
            await engine.WaitForJoinedNotificationAsync(ct);
            if (i == 0)
            {
                Assert.Contains("\"State\":\"in\"", guest.JoinStatusJson());
                Set(guest, "_joinUiExpiresAtMs", Environment.TickCount64 - 1);
                Assert.Contains("\"State\":\"idle\"", guest.JoinStatusJson());
                Assert.True(Get<bool>(guest, "_joinedWorld"));
                Assert.NotNull(Get<object?>(guest, "_joinedWorldRefresh"));
            }
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
                if (probe == F4Probe.NoOwnExit) engine.CompleteExitLoad(exit);
                else if (supersedeLoad) await CheckF4SupersededMenuAsync(guest, engine, exit, output);
                else await ConfirmF4ExitFromMenuAsync(guest, engine, exit, i, ct, output);
                await WaitAsync(() => !Get<bool>(guest, "_leaveInProgress") && !File.Exists(exit) &&
                    !File.Exists(incoming) && Get<object?>(guest, "_wo125ActiveExitPath") is null &&
                    Get<object>(guest, "_where").ToString() == "Menu" && !Get<bool>(guest, "_lockHeld"),
                    "confirmed mpexit, file release and save unlock", ct);
                Assert.False(Property<bool>(guest, "OwnLoadExpected"));
                Assert.False(Get<bool>(guest, "_joinedWorld"));
                Assert.Null(Get<object?>(guest, "_joinedWorldRefresh"));
                Assert.False((bool)Call(guest, "Wo122WantLock")!);
                Assert.Null(Get<object?>(guest, "_wo125ActiveExitPath"));
                Assert.Null(Get<object?>(guest, "_pinnedHomeLoad"));
                Assert.Empty(Directory.EnumerateFiles(fixture.GuestSaves, "mp*.whs", SearchOption.AllDirectories));
                Assert.Empty(Directory.EnumerateFiles(WorldReceiver.DefaultStagingDir(), "world-*"));
                Assert.Equal(ownBefore, SHA256.HashData(File.ReadAllBytes(savedOutsideProfile)));
                Console.WriteLine($"F4-MPEXIT cycle={i} warmup={(i <= initialReceiptCycle ? 1 : 0)} join_loads={engine.JoinLoadsIssued} own_loads=0 terminal=menu files=0 own_sha256_equal=1");
            }
            finally { File.Move(savedOutsideProfile, ownSave); }
            if (resources is not null && i >= initialReceiptCycle)
            {
                if (i > initialReceiptCycle) outstanding?.Retain();
                await resources.CycleAsync(i - initialReceiptCycle, ct, host);
            }
        }
        Assert.Equal(lastCycle + 1, engine.JoinLoadsIssued);
        Assert.Equal(lastCycle + 1, exits.Count);
        Assert.Equal(ownBefore, SHA256.HashData(File.ReadAllBytes(ownSave)));
        resources?.AssertBudget();
    }

    private sealed class RetainedLeaveTimers : IDisposable
    {
        private readonly List<CancellationTokenSource> _retained = [];

        // Retain a real one-shot timer past every sample. It is released only after the test's assertion.
        public void Retain() => _retained.Add(new CancellationTokenSource(TimeSpan.FromMinutes(2)));

        public void Dispose()
        {
            foreach (var timer in _retained) timer.Dispose();
        }
    }

    private static async Task CheckF4SupersededMenuAsync(GameBridge guest, JoinCalendarTestEngine engine, string exit, ITestOutputHelper output)
    {
        var load = Get<WorldLoadSequence>(guest, "_worldLoad");
        ulong generation = load.Current.Generation;
        engine.CompleteExitLoad(exit, reportFailure: false);
        engine.BeforeNextWhereReply = () => Call(guest, "LoadSequenceBeginLoad", true);
        Set(guest, "_leaveSinceUtc", DateTime.UtcNow.AddMinutes(-6));
        await InvokeAsync(guest, "Wo124JoinerTickAsync");
        Assert.NotEqual(generation, load.Current.Generation);
        Assert.True(Property<bool>(guest, "OwnLoadExpected"));
        Assert.Equal("return-needed", Call(guest, "JoinMenuPhase"));
        Assert.True((bool)Call(guest, "Wo122WantLock")!);
        Assert.True(File.Exists(exit));
        output.WriteLine($"G2-MENU superseded={generation}->{load.Current.Generation} phase=return-needed");
        engine.Feed("Exiting to main menu because save game loading failed");
    }

    private static async Task ConfirmF4ExitFromMenuAsync(GameBridge guest, JoinCalendarTestEngine engine, string exit,
        int cycle, CancellationToken ct, ITestOutputHelper output)
    {
        int queries = engine.WhereQueries;
        bool useRetry = cycle == 0 || cycle % 2 != 0; // warm the retry path as well as the ordinary menu return
        Set(guest, "_leaveSinceUtc", DateTime.UtcNow.AddMinutes(-6));
        if (useRetry)
        {
            // A live loading reply must not settle the exit. The next player Leave retries the check.
            await InvokeAsync(guest, "Wo124JoinerTickAsync");
            Assert.Equal("return-needed", Call(guest, "JoinMenuPhase"));
            Assert.True(File.Exists(exit));
            await WaitAsync(() => engine.ReturnRetryVisible, "the local Leave retry to become available", ct);
        }

        engine.CompleteExitLoad(exit, reportFailure: false, reportRead: cycle != 2);
        Assert.True((bool)Call(guest, "Wo122WantLock")!);
        Assert.True(File.Exists(exit)); // the unsolicited menu line cannot complete this receipt
        if (useRetry)
            engine.Emit("w211_leave", "menu");
        else
        {
            await InvokeAsync(guest, "Wo124JoinerTickAsync");
            output.WriteLine($"G2-MENU cycle={cycle} queries={engine.WhereQueries - queries} phase={Call(guest, "JoinMenuPhase")}");
            Assert.NotEqual("return-needed", Call(guest, "JoinMenuPhase"));
        }
        await WaitAsync(() => !Get<bool>(guest, "_leaveInProgress") && !engine.ReturnRetryVisible,
            "the current tokened menu reply to complete the exit receipt", ct);
        Assert.Equal(queries + (useRetry ? 2 : 1), engine.WhereQueries);
        output.WriteLine($"G2-MENU cycle={cycle} terminal=lost read_log={(cycle != 2 ? 1 : 0)} fresh_queries={engine.WhereQueries - queries} locked=0");
    }

    private static async Task TestF4ActiveGuestsAsync(SaveFixture fixture, int port, GameBridge host, GameBridge first,
        JoinWire hostLink, JoinWire firstLink, JoinCalendarTestEngine firstEngine, ReplayPeer secondPeer, int players,
        CancellationToken ct, ITestOutputHelper output)
    {
        string secondSave = Path.Combine(fixture.GuestSaves, "playline1", "save002.whs");
        var (_, source) = WhsSaveTests.Pair();
        source.Seed += 7;
        source.Strength += 10;
        Directory.CreateDirectory(Path.GetDirectoryName(secondSave)!);
        File.WriteAllBytes(secondSave, WhsSaveTests.File(source));
        byte[] firstHash = SHA256.HashData(File.ReadAllBytes(fixture.GuestOwnSave));
        byte[] secondHash = SHA256.HashData(File.ReadAllBytes(secondSave));
        using var secondStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await secondPeer.SendAsync(Protocol.BuildLinkHelloUp(Protocol.LinkCapWorldSync), ct);
        var secondLink = new JoinWire(secondPeer.Wire, await secondPeer.DrainAsync(ct), dropFirstSnapshot: false);
        // Both guests can load their own saves while the engine refuses QuickSave.
        await using var secondPipe = new JoinCalendarTestPipe() { CanQuickSave = false };
        await using var secondEngine = new JoinCalendarTestEngine(Path.Combine(fixture.Root, "guest-c.log"), fixture.GuestSaves,
            null, secondPipe.ObserveGameplayStarted);
        var second = CreateBridge(secondPeer.Id, false, secondLink, secondPipe.Pipe, secondEngine.Transport, fixture.GuestSaves, secondStop.Token, port);
        secondEngine.Transport.MainMenuShown += secondPipe.ObserveMainMenu;
        secondEngine.Emit("wo124_where", "menu");
        Task secondRead = InvokeAsync(second, "ReceiveLoopAsync", secondLink, secondStop.Token);
        try
        {
            await WaitAsync(() => Get<bool>(second, "_combatRoleApplyComplete"), "the second guest's authenticated role", ct);
            Assert.False(Get<bool>(second, "_isDamageAuthority"));
            await InvokeAsync(host, "Wo206AnnounceAsync", "fixture-second-active", (byte?)secondPeer.Id);
            await WaitAsync(() => Get<bool>(second, "_hostModeKnown") && Get<Wo206Epochs>(second, "_w206").World != 0,
                "the second guest to learn the same authenticated world", ct);
            Assert.Equal(Get<Wo206Epochs>(first, "_w206").World, Get<Wo206Epochs>(second, "_w206").World);
            Set(first, "_w184SaveOnLeave", false);
            Set(second, "_w184SaveOnLeave", false);
            firstEngine.DeferOwnFileRead = true;
            secondEngine.DeferOwnFileRead = true;
            using var resources = new F4ResourceCheck($"active-guests-{players}", first, output);
            for (int cycle = 1; cycle <= 6; cycle++)
            {
                async Task JoinAsync(GameBridge guest, JoinWire link, JoinCalendarTestEngine engine, string selected)
                {
                    Call(guest, "Wo125OnConsoleChoice", selected);
                    engine.Emit("join_request", "");
                    string path = await engine.WaitForJoinLoadAsync(ct);
                    await WaitAsync(() => Get<bool>(guest, "_joinedWorld") && Get<object?>(host, "_hostJoin") is null &&
                        !File.Exists(path) && link.ReadySent.Count == cycle && hostLink.ReadyReceived.Contains(link.ReadySent.Last()),
                        "the active guest to finish its targeted join", ct);
                    await engine.WaitForJoinedNotificationAsync(ct);
                }

                await JoinAsync(first, firstLink, firstEngine, "playline0/save001");
                // The host serializes its save/transfer, but both guests stay live.
                await JoinAsync(second, secondLink, secondEngine, "playline1/save002");
                Assert.True(Get<bool>(first, "_joinedWorld"));
                Task firstLeaving = InvokeAsync(first, "LeaveSharedWorldAsync", "fixture", "return first guest");
                Task secondLeaving = InvokeAsync(second, "LeaveSharedWorldAsync", "fixture", "return second guest");
                Assert.Equal(fixture.GuestOwnSave, await firstEngine.WaitForOwnLoadAsync(ct));
                Assert.Equal(secondSave, await secondEngine.WaitForOwnLoadAsync(ct));
                Assert.True(Get<bool>(first, "_leaveInProgress"));
                Assert.True(Get<bool>(second, "_leaveInProgress"));
                firstEngine.CompleteOwnLoad();
                secondEngine.CompleteOwnLoad();
                await Task.WhenAll(firstLeaving, secondLeaving);
                await WaitAsync(() => !Get<bool>(first, "_leaveInProgress") && !Get<bool>(second, "_leaveInProgress") &&
                    !Get<bool>(first, "_joinedWorld") && !Get<bool>(second, "_joinedWorld"),
                    "both of the already observed private loads to confirm", ct);
                Assert.Equal(firstHash, SHA256.HashData(File.ReadAllBytes(fixture.GuestOwnSave)));
                Assert.Equal(secondHash, SHA256.HashData(File.ReadAllBytes(secondSave)));
                Assert.Empty(Directory.EnumerateFiles(fixture.GuestSaves, "mp*.whs", SearchOption.AllDirectories));
                Assert.Equal(2 * cycle, hostLink.ReadyReceived.Distinct().Count());
                Console.WriteLine($"F4-ACTIVE-GUESTS cycle={cycle} targeted_ready={hostLink.ReadyReceived.Count} overlapping_returns=2 own_sha256_equal=2");
                await resources.CycleAsync(cycle - 1, ct, host, second);
            }
            resources.AssertBudget();
        }
        finally
        {
            secondPipe.BeginShutdown();
            secondStop.Cancel();
            await InvokeAsync(second, "Wo123OnDisconnectAsync");
            try { await secondRead.WaitAsync(TimeSpan.FromSeconds(5)); }
            finally { Get<FileSystemWatcher?>(second, "_savesWatcher")?.Dispose(); }
        }
    }

    private readonly record struct F4Owned(int PipeConnections, long PipeRevision, int Reservations, int Timers);

    private readonly record struct F4ResourceSample(int Threads, int Handles, int RawThreads, int RawHandles,
        int ThreadPoolWorkers, long RuntimeTimers, F4Owned Owned, string? WindowsHandleTypes,
        IReadOnlyDictionary<IntPtr, string>? WindowsHandleObjects, Task<bool>[] FrameDeadlines);

    private static F4ResourceSample F4Resources(GameBridge guest, bool traceWindowsHandles, F4ProcessorDeadlines deadlineTrace)
    {
        using var process = Process.GetCurrentProcess();
        int rawThreads = process.Threads.Count;
        int rawHandles = process.HandleCount;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var retained = Get<object>(guest, "_retainedJoinWorlds");
        int leases = (int)retained.GetType().GetProperty("Count")!.GetValue(retained)!;
        leases += Get<object?>(guest, "_joinRx") is null ? 0 : 1;
        leases += Get<object?>(guest, "_pinnedHomeLoad") is null ? 0 : 1;
        leases += Get<object?>(guest, "_wo125ActiveExitPath") is null ? 0 : 1;
        int timers = Get<bool>(guest, "_rejoinPending") ? 1 : 0;
        var pipe = Get<CombatPipe>(guest, "_combat");
        var (runtimeTimers, frameDeadlines) = deadlineTrace.Sample();
        Dictionary<IntPtr, string>? objects = traceWindowsHandles ? new() : null;
        string? types = OperatingSystem.IsWindows() ? F4WindowsHandleTypes.Capture(objects) : null;
        // Count after the inventory calls, so handles they initialise are present in the same observation.
        process.Refresh();
        int threads = process.Threads.Count;
        int handles = process.HandleCount;
        return new(threads, handles, rawThreads, rawHandles, ThreadPool.ThreadCount, runtimeTimers,
            new(pipe.IsConnected ? 1 : 0, pipe.EdgeConnectionRevision, leases, timers), types, objects, frameDeadlines);
    }

    private sealed class F4ResourceCheck : IDisposable
    {
        private const int TimerAllowance = 1;
        private readonly string _scenario;
        private readonly GameBridge _guest;
        private readonly ITestOutputHelper _output;
        private readonly AutoResetEvent _requestReady = new(false);
        private readonly F4ProcessorDeadlines _deadlineTrace = new();
        private readonly Thread _sampler;
        private readonly F4WindowsHandleTrace? _handleTrace;
        private TaskCompletionSource<F4ResourceSample>? _request;
        private bool _stopSampler;
        private int _nextCycle;
        private F4ResourceSample _before, _after;

        public F4ResourceCheck(string scenario, GameBridge guest, ITestOutputHelper output)
        {
            _scenario = scenario;
            _guest = guest;
            _output = output;
            if (OperatingSystem.IsWindows()) _handleTrace = new();
            _sampler = new Thread(Sample) { IsBackground = true, Name = "F4 resource sampler" };
            _sampler.Start();
        }

        private async Task PrimeWorkersAsync(CancellationToken ct)
        {
            ThreadPool.GetMinThreads(out int workerMin, out int ioMin);
            ThreadPool.GetMaxThreads(out int workerMax, out _);
            bool useWinsock = OperatingSystem.IsWindows();
            // Prime the pool reached by the actual warmup round. A cold pool does not yet
            // contain all the workers that the concurrent guest returns leave behind.
            int active = ThreadPool.ThreadCount;
            int target = useWinsock ? active + Environment.ProcessorCount : 16;
            int warmWorkers = Math.Clamp(Math.Max(16, target), workerMin, workerMax);
            Assert.True(ThreadPool.SetMinThreads(warmWorkers, ioMin));
            try
            {
                int warmed = await WarmWorkersAsync(warmWorkers, useWinsock, ct);
                int reached = ThreadPool.ThreadCount;
                if (useWinsock && reached > warmed)
                {
                    // The warmup itself may activate more workers than the join did.
                    // Sample only after those workers have had a Winsock exchange too.
                    warmWorkers = Math.Clamp(reached, workerMin, workerMax);
                    Assert.True(ThreadPool.SetMinThreads(warmWorkers, ioMin));
                    warmed = await WarmWorkersAsync(warmWorkers, useWinsock, ct);
                }
                _output.WriteLine($"F4-POOL-WARMUP scenario={_scenario} primed={warmed} worker_min={workerMin} worker_max={workerMax} after_join_workers={active} target={warmWorkers}");
            }
            finally
            {
                Assert.True(ThreadPool.SetMinThreads(workerMin, ioMin));
            }
        }

        private static async Task<int> WarmWorkersAsync(int workers, bool useWinsock, CancellationToken ct)
        {
            var warmed = new ConcurrentDictionary<int, byte>();
            var elapsed = Stopwatch.StartNew();
            do
            {
                using var round = new ManualResetEventSlim();
                await Task.WhenAll(Enumerable.Range(0, workers).Select(_ => Task.Run(() =>
                {
                    int thread = Environment.CurrentManagedThreadId;
                    if (!warmed.ContainsKey(thread))
                    {
                        if (useWinsock) WarmWinsockThread(ct);
                        warmed.TryAdd(thread, 0);
                    }
                    if (warmed.Count >= workers) round.Set();
                    // Every worker releases its own slot even if the test host
                    // already occupies a slot or its timer cannot run yet.
                    round.Wait(TimeSpan.FromMilliseconds(200), ct);
                }, ct)));
            } while (warmed.Count < workers && elapsed.Elapsed < TimeSpan.FromSeconds(2));
            return warmed.Count;
        }

        private static void WarmWinsockThread(CancellationToken ct)
        {
            // The production NetworkStream uses overlapped WSARecv and WSASend.
            // Issue both on this worker before waiting for either completion.
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start(1);
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            using var peer = new TcpClient();
            peer.Connect(endpoint.Address, endpoint.Port);
            using var accepted = listener.AcceptTcpClient();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(1000);
            var received = new byte[1];
            var read = accepted.GetStream().ReadAsync(received, deadline.Token);
            var write = peer.GetStream().WriteAsync(new byte[] { 0x2a }, deadline.Token);
            write.AsTask().GetAwaiter().GetResult();
            Assert.Equal(1, read.AsTask().GetAwaiter().GetResult());
            Assert.Equal(0x2a, received[0]);
        }

        private void Sample()
        {
            // The GC/finalizer and Windows handle queries use one OS thread
            // for baseline and observations, even when the join resumes on different workers.
            // The event is owned for the whole fixture, including baseline. It
            // avoids measuring a lazy SemaphoreSlim monitor created by the sampler.
            while (true)
            {
                _requestReady.WaitOne();
                if (Volatile.Read(ref _stopSampler)) return;
                var request = Interlocked.Exchange(ref _request, null);
                if (request is null) continue;
                try { request.SetResult(F4Resources(_guest, _handleTrace is not null, _deadlineTrace)); }
                catch (Exception ex) { request.SetException(ex); }
            }
        }

        public async Task CycleAsync(int cycle, CancellationToken ct, params GameBridge[] others)
        {
            Assert.Equal(_nextCycle++, cycle);
            if (cycle == 0) await PrimeWorkersAsync(ct);
            var sample = await SampleAfterNativeAsync();
            if (sample.FrameDeadlines.Length > 0 &&
                (cycle == 0 || cycle == 5 && sample.RuntimeTimers > _before.RuntimeTimers + TimerAllowance))
            {
                if (cycle == 5) LogTimers("baseline", _before);
                LogTimers("observed", sample);
                // These exact timers belong to ProcessorFrameScope. Wait for
                // their release, then read the whole process count again.
                bool[] released = await Task.WhenAll(sample.FrameDeadlines).WaitAsync(ct);
                Assert.All(released, cancelled => Assert.True(cancelled,
                    "A processor frame deadline expired before its owner cancelled it."));
                long early = sample.RuntimeTimers;
                sample = await SampleAfterNativeAsync();
                _output.WriteLine($"F4-TIMER-DRAIN scenario={_scenario} cycle={cycle} owner=ProcessorFrameScope cancelled={released.Length} runtime_timers={early}->{sample.RuntimeTimers}");
                LogTimers("settled", sample);
            }
            if (cycle == 0) _before = sample;
            _after = sample;
            _output.WriteLine($"F4-RESOURCES scenario={_scenario} cycle={cycle} threads={sample.Threads} handles={sample.Handles} " +
                $"raw_threads={sample.RawThreads} raw_handles={sample.RawHandles} thread_pool_workers={sample.ThreadPoolWorkers} " +
                $"runtime_timers={sample.RuntimeTimers} guest_pipe={sample.Owned.PipeConnections} tracked_leases={sample.Owned.Reservations} " +
                $"pipe_revision={sample.Owned.PipeRevision} rejoin_timer={sample.Owned.Timers}");
            if (sample.WindowsHandleTypes is string types)
                _output.WriteLine($"F4-HANDLE-TYPES scenario={_scenario} cycle={cycle} {types}");

            async Task<F4ResourceSample> SampleAfterNativeAsync()
            {
                // A phase can be confirmed while its native operations
                // still hold physical write and reply deadlines.
                await WaitForNativeTransitionAsync(_guest, ct);
                foreach (var player in others) await WaitForNativeTransitionAsync(player, ct);
                var request = new TaskCompletionSource<F4ResourceSample>(TaskCreationOptions.RunContinuationsAsynchronously);
                ct.ThrowIfCancellationRequested();
                Assert.Null(Interlocked.CompareExchange(ref _request, request, null));
                _requestReady.Set();
                return await request.Task.WaitAsync(ct);
            }
        }

        private void LogTimers(string phase, F4ResourceSample sample) =>
            _output.WriteLine($"F4-TIMER-SAMPLE scenario={_scenario} phase={phase} count={sample.RuntimeTimers} frame_deadlines={sample.FrameDeadlines.Length} owner=ProcessorFrameScope");

        private static async Task WaitForNativeTransitionAsync(GameBridge player, CancellationToken ct)
        {
            foreach (string name in new[] { "_loadSequencePublish", "_hostCalendarGate", "_hostCalendarSendGate" })
            {
                var gate = Get<SemaphoreSlim>(player, name);
                await gate.WaitAsync(ct);
                gate.Release();
            }
            if (Get<Task<bool>?>(player, "_p1Pending") is { } authority)
                await authority.WaitAsync(ct);
            await Get<Task>(player, "_privateReturnMenuUpdate").WaitAsync(ct);
            // The phase can be published while a queued single-frame native
            // call still owns a bounded write or reply wait.
            var native = Get<SemaphoreSlim>(Get<CombatPipe>(player, "_combat"), "_gate");
            await native.WaitAsync(ct);
            native.Release();
        }

        public void AssertBudget()
        {
            Assert.Equal(6, _nextCycle);
            var before = _before;
            var after = _after;
            _output.WriteLine($"F4-RESOURCES scenario={_scenario} threads={before.Threads}->{after.Threads} handles={before.Handles}->{after.Handles} " +
                $"raw_threads={before.RawThreads}->{after.RawThreads} raw_handles={before.RawHandles}->{after.RawHandles} " +
                $"thread_pool_workers={before.ThreadPoolWorkers}->{after.ThreadPoolWorkers} runtime_timers={before.RuntimeTimers}->{after.RuntimeTimers} " +
                $"guest_pipe={before.Owned.PipeConnections}->{after.Owned.PipeConnections} tracked_leases={before.Owned.Reservations}->{after.Owned.Reservations} " +
                $"pipe_revision={before.Owned.PipeRevision}->{after.Owned.PipeRevision} rejoin_timer={before.Owned.Timers}->{after.Owned.Timers}");
            if (_handleTrace is not null && after.Handles > before.Handles + 4 &&
                before.WindowsHandleObjects is { } start && after.WindowsHandleObjects is { } end)
                _output.WriteLine($"F4-HANDLE-ORIGINS scenario={_scenario} {_handleTrace.DescribeNewHandles(start, end)}");
            Assert.True(after.RuntimeTimers <= before.RuntimeTimers + TimerAllowance,
                $"F4 runtime timer budget: scenario={_scenario} {before.RuntimeTimers}->{after.RuntimeTimers}, allowance={TimerAllowance}");
            Assert.InRange(after.Threads, 0, before.Threads + 4);
            Assert.InRange(after.Handles, 0, before.Handles + 4);
            Assert.Equal(before.Owned, after.Owned);
        }

        public void Dispose()
        {
            Volatile.Write(ref _stopSampler, true);
            _requestReady.Set();
            try
            {
                Assert.True(_sampler.Join(TimeSpan.FromSeconds(10)), "The F4 resource sampler did not stop");
                _requestReady.Dispose();
            }
            finally
            {
                _handleTrace?.Dispose();
                _deadlineTrace.Dispose();
            }
        }
    }
}
