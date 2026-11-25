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
    public async Task Five_leaves_with_two_retained_file_handles_each_fail_the_application_handle_budget()
    {
        var failure = await Assert.ThrowsAsync<TrueException>(() =>
            JoinCalendarIntegrationTests.RunF4MenuProbeAsync(output, retainHandles: true));
        Assert.Contains("F4 application handle budget", failure.Message);
        output.WriteLine("F4-HANDLE-TWIN scenario=mpexit-retained-handle retained=10 result=budget-rejected");
    }

    [Fact]
    public void Windows_handle_accounting_requires_proven_live_thread_ownership()
    {
        const string socketEvent = "ntdll.dll>mswsock.dll>ws2_32.dll>Interop+Winsock.WSARecv";
        const string socketThread = "ntdll.dll>ws2_32.dll>Interop+Winsock.WSARecv";
        const string regexCache = "ntdll.dll>KERNELBASE.dll>coreclr.dll+886b>System.Text.RegularExpressions.RegexCache.Add";
        const string workerStart = "ntdll.dll>KERNELBASE.dll>coreclr.dll+418df>System.Threading.Thread.StartCore";
        const string workerName = "ntdll.dll>KERNELBASE.dll>coreclr.dll+886b>System.Threading.Thread.SetThreadPoolWorkerThreadName";
        const string gateWait = "ntdll.dll>KERNELBASE.dll>coreclr.dll+886b>System.Threading.SemaphoreSlim.WaitAsync";
        F4WindowsHandleTrace.Allocation[] allocations =
        [
            new("Event", 10, socketEvent), new("Thread", 10, socketThread, TargetThread: 10),
            new("Event", 11, socketEvent), new("Thread", 12, socketThread, TargetThread: 11),
            new("Event", 13, socketEvent), new("Thread", 13, socketThread, TargetThread: 13),
            new("Event", 13, socketEvent), new("Thread", 13, socketThread, TargetThread: 13),
            new("Event", 14, socketEvent), new("Thread", 14, socketThread, TargetThread: 14),
            new("Thread", 19, socketThread, TargetThread: 19),
            new("Thread", 20, socketThread, TargetThread: 20), new("Thread", 20, socketThread, TargetThread: 20),
            new("Event", 21, socketEvent),
            new("File", 10, socketEvent),
            new("Event", 10, gateWait),
            new("Event", 10, gateWait, PersistentMonitor: "relay-0._writeSignal"),
            new("File", 10, gateWait, PersistentMonitor: "relay-0._writeSignal"),
            new("Event", 10, "Some.Application.Open", PersistentMonitor: "other-gate"),
            new("Event", 10, regexCache),
            new("Event", 15, workerStart), new("Event", 15, workerStart), new("Thread", 15, workerStart, Worker: 16),
            new("Event", 17, workerStart), new("Event", 17, workerStart), new("Thread", 17, workerStart),
            new("Event", 16, workerName), new("Event", 10, workerName)
        ];
        var live = new HashSet<long> { 10, 11, 12, 13, 16, 19, 20, 21 };
        var runtime = F4WindowsHandleTrace.CountBookkeeping(allocations, live, out var started, knownNativeWorkerStack: true);
        Assert.Equal(new F4WindowsHandleTrace.Bookkeeping(1, 1, 1, 1, 1, 1), runtime);
        Assert.Equal(9, runtime.Total);
        Assert.Equal(16, Assert.Single(started));
        Assert.Equal(new F4WindowsHandleTrace.Bookkeeping(1, 1, 1, 1, 1, 0),
            F4WindowsHandleTrace.CountBookkeeping(allocations, live, out _));
    }

    [Fact]
    public void An_unresolved_worker_stack_requires_both_native_events_and_the_live_kernel_target()
    {
        const string clr = ">coreclr.dll+416c1>coreclr.dll+413b3>coreclr.dll+408f0>unresolved";
        const string event1 = "ntdll.dll>KERNELBASE.dll>coreclr.dll+418df>coreclr.dll+4188c>coreclr.dll+417ad" + clr;
        const string event2 = "ntdll.dll>KERNELBASE.dll>coreclr.dll+418df>coreclr.dll+4189a>coreclr.dll+417ad" + clr;
        const string thread = "ntdll.dll>KERNELBASE.dll>KERNEL32.DLL>coreclr.dll+417d4" + clr;
        F4WindowsHandleTrace.Allocation[] allocations =
        [
            new("Event", 1, event1), new("Event", 1, event2), new("Thread", 1, thread, Worker: 10, TargetThread: 10),
            new("Event", 2, event1), new("Event", 2, event1), new("Thread", 2, thread, Worker: 20, TargetThread: 20),
            new("Event", 3, event1), new("Event", 3, event2), new("Thread", 3, thread, Worker: 30, TargetThread: 31),
            new("Event", 4, event1), new("Event", 4, event2), new("Thread", 4, thread, Worker: 40, TargetThread: 40),
            new("Event", 5, event1), new("Event", 5, "KcdMp.Client.App.Open"), new("Thread", 5, thread, Worker: 50, TargetThread: 50),
            new("Event", 6, event1), new("Event", 6, event2), new("Thread", 6, thread, TargetThread: 60),
            new("Event", 7, event1), new("Event", 7, event2),
            new("Thread", 7, thread.Replace("unresolved", "KcdMp.Client.App.ThreadStart"), Worker: 70, TargetThread: 70),
        ];
        var live = new HashSet<long> { 10, 20, 30, 31, 50, 60, 70 };
        var otherRuntime = F4WindowsHandleTrace.CountBookkeeping(allocations, live, out var otherWorkers);
        Assert.Equal(0, otherRuntime.Total);
        Assert.Empty(otherWorkers);

        var knownRuntime = F4WindowsHandleTrace.CountBookkeeping(allocations, live, out var started, knownNativeWorkerStack: true);
        Assert.Equal(1, knownRuntime.WorkerStarts);
        Assert.Equal(3, knownRuntime.Total);
        Assert.Equal(10, Assert.Single(started));
    }

    [Fact]
    public async Task A_baseline_gate_has_a_concrete_runtime_monitor_owner()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var release = new ManualResetEventSlim();
        using var owner = new ContendedSemaphore();
        using var trace = new F4PersistentMonitorTrace();
        trace.Track("baseline", owner);
        object monitor = Assert.IsType<System.Runtime.CompilerServices.StrongBox<bool>>(
            F4PersistentMonitorTrace.GetMonitor(owner.Gate));
        var acquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task holder = Task.Factory.StartNew(() =>
        {
            lock (monitor)
            {
                acquired.SetResult();
                release.Wait(stop.Token);
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Task waiting = Task.CompletedTask;
        try
        {
            await acquired.Task.WaitAsync(stop.Token);
            waiting = Task.Run(async () =>
            {
                await owner.Gate.WaitAsync(stop.Token);
                owner.Gate.Release();
            });
            await trace.FirstObserved.WaitAsync(stop.Token);
        }
        finally
        {
            release.Set();
            await Task.WhenAll(holder, waiting);
        }
        if (F4PersistentMonitorTrace.Supported)
        {
            var (handle, name) = Assert.Single(trace.ReadHandles());
            Assert.Equal("baseline.Gate", name);
            Dictionary<nint, string> objects = [];
            F4WindowsHandleTypes.Capture(objects);
            Assert.Equal("Event", objects.GetValueOrDefault(handle));
        }
    }

    private sealed class ContendedSemaphore : IDisposable
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public void Dispose() => Gate.Dispose();
    }

    [Fact]
    public void A_busy_pool_thread_or_an_application_thread_stays_in_the_thread_budget()
    {
        const string poolStart = "System.Threading.PortableThreadPool+WorkerThread.WorkerThreadStart";
        const string poolWait = "System.Threading.LowLevelLifoSemaphore.WaitForSignal";
        Assert.True(F4WindowsHandleTrace.IsPoolWaiting([poolWait, poolStart]));
        Assert.False(F4WindowsHandleTrace.IsPoolWaiting(
            [poolWait, "KcdMp.Client.Transport.WaitForReply", "System.Threading.ThreadPoolWorkQueue.Dispatch", poolStart]));
        Assert.False(F4WindowsHandleTrace.IsPoolWaiting([poolWait, "KcdMp.Client.Transport.ReadLoop"]));
        Assert.False(F4WindowsHandleTrace.IsPoolWaiting([poolStart]));
    }

    [Fact]
    public async Task Processor_deadlines_are_attributed_and_cancelled_while_an_unowned_short_timer_remains()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var trace = new F4ProcessorDeadlines();
        using var clock = new HeldDeadlineDisposal(stop.Token);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unowned = Task.Delay(TimeSpan.FromMilliseconds(1950), new ManualFrameTime(), stop.Token);
        TimeProvider deadlineTime = trace.Wrap(clock);
        Task<ProcessorFrameScope.Result>[] frames =
        [
            ProcessorFrameScope.RunAsync(_ => release.Task, 2000, stop.Token, deadlineTime),
            ProcessorFrameScope.RunAsync(_ => release.Task, 2000, stop.Token, deadlineTime)
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
    private enum F4Probe { TransferAbort, NoOwnExit, NoOwnExitWithMenuProbe, SupersededMenuProbe, HostAbort, MissingOwnSave, ActiveGuest, TransportLost, HostReturn, RetainedTimer, RetainedHandle }

    internal static Task RunF4MenuProbeAsync(ITestOutputHelper output, bool supersedeLoad = false,
        bool retainTimers = false, bool retainHandles = false) =>
        RunAsync("A", "B", dropFirstSnapshot: false, f4: supersedeLoad ? F4Probe.SupersededMenuProbe
            : retainTimers ? F4Probe.RetainedTimer : retainHandles ? F4Probe.RetainedHandle : F4Probe.NoOwnExitWithMenuProbe,
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
            F4Probe.NoOwnExit or F4Probe.NoOwnExitWithMenuProbe or F4Probe.SupersededMenuProbe or F4Probe.RetainedTimer or F4Probe.RetainedHandle =>
                TestF4MenuExitAsync(fixture, host, guest, hostLink, engine, ct, output, probe),
            F4Probe.MissingOwnSave => TestF4MissingOwnSaveAsync(fixture, host, guest, hostLink, guestLink, engine, ct, output),
            _ => throw new ArgumentOutOfRangeException(nameof(probe))
        };

    private static async Task TestF4TransfersAsync(SaveFixture fixture, GameBridge host, GameBridge guest,
        JoinWire hostLink, JoinWire guestLink, JoinCalendarTestPipe guestPipe, JoinCalendarTestEngine canceller,
        JoinCalendarTestEngine engine, string scenario, CancellationToken ct, ITestOutputHelper output)
    {
        byte[] ownBefore = SHA256.HashData(File.ReadAllBytes(fixture.GuestOwnSave));
        using var resources = new F4ResourceCheck(scenario, guest, output, null, host);
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

        var requested = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
        var offered = new ConcurrentQueue<uint>();
        guestLink.BeforeWrite = (packet, _) =>
        {
            if (packet.Length >= 8 && packet.Span[0] == Protocol.JoinRequestUp)
                requested.TrySetResult(BinaryPrimitives.ReadUInt32LittleEndian(packet.Span[4..]));
            return ValueTask.CompletedTask;
        };
        guestLink.DropIncoming = packet =>
        {
            if (packet.Type == Protocol.WorldOfferDown &&
                Protocol.TrySplitJoinDown(packet.Payload, out _, out _, out uint id, out _))
                offered.Enqueue(id);
            return false;
        };
        uint retryId;
        try
        {
            engine.Emit("join_request", "");
            string loadedFile = await engine.WaitForJoinLoadAsync(ct);
            Assert.True(requested.Task.IsCompletedSuccessfully);
            retryId = requested.Task.Result;
            Assert.Contains(retryId, offered);
            Assert.Equal($"mpworld{retryId:x8}.whs", Path.GetFileName(loadedFile));
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            object? hostAttempt = Get<object?>(host, "_hostJoin");
            object? guestAttempt = Get<object?>(guest, "_jj");
            uint request = requested.Task.IsCompletedSuccessfully ? requested.Task.Result : 0;
            throw new TimeoutException($"F4 rejoin scenario={scenario} request={request:x8} offer={offered.Contains(request)} " +
                $"calendars={guestLink.FrozenSnapshots.Count(sample => sample.JoinId == request)} native_arms={guestPipe.ArmRequests} " +
                $"guest_ui={Get<string>(guest, "_joinUiState")} guest_reason={Get<string>(guest, "_joinUiReason")} " +
                $"host_phase={(hostAttempt is null ? "none" : GetVisible<string>(hostAttempt, "Phase"))} " +
                $"guest_phase={(guestAttempt is null ? "none" : GetVisible<string>(guestAttempt, "Phase"))} " +
                $"out={Get<uint>(guest, "_joinOutId"):x8} eof={guestLink.EndOfStreamObserved}", ex);
        }
        finally
        {
            guestLink.BeforeWrite = null;
            guestLink.DropIncoming = null;
        }
        await WaitAsync(() => Get<bool>(guest, "_joinedWorld") && Get<object?>(host, "_hostJoin") is null,
            "the next world load and Ready on the same guest reader", ct);
        await engine.WaitForJoinedNotificationAsync(ct);
        uint ready = Assert.Single(guestLink.ReadySent);
        Assert.Equal(retryId, ready);
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
        using var resources = new F4ResourceCheck("transfer-eof", guest, output, null, host);
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
        using var resources = new F4ResourceCheck($"host-return-{players}", guest, output, null, host);
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
        using var resources = new F4ResourceCheck("missing-personal-save", guest, output, null, host);
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
        bool retainHandles = probe == F4Probe.RetainedHandle;
        string scenario = probe switch
        {
            F4Probe.NoOwnExit => "mpexit-receipt",
            F4Probe.RetainedTimer => "mpexit-retained-timer",
            F4Probe.RetainedHandle => "mpexit-retained-handle",
            _ => "mpexit"
        };
        using var resources = supersedeLoad ? null : new F4ResourceCheck(scenario, guest, output, null, host);
        int initialReceiptCycle = probe == F4Probe.NoOwnExit ? 1 : 0;
        int lastCycle = supersedeLoad ? 0 : 5 + initialReceiptCycle;
        using var outstanding = retainTimers || retainHandles ? new RetainedLeaveResources() : null;
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
                if (i > initialReceiptCycle)
                {
                    if (retainTimers) outstanding!.Add(new CancellationTokenSource(TimeSpan.FromMinutes(2)));
                    if (retainHandles)
                    {
                        string path = Path.Combine(fixture.Root, "retained-handle.dat");
                        for (int handle = 0; handle < 2; handle++)
                            outstanding!.Add(File.Open(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite));
                    }
                }
                await resources.CycleAsync(i - initialReceiptCycle, ct, host);
            }
        }
        Assert.Equal(lastCycle + 1, engine.JoinLoadsIssued);
        Assert.Equal(lastCycle + 1, exits.Count);
        Assert.Equal(ownBefore, SHA256.HashData(File.ReadAllBytes(ownSave)));
        resources?.AssertBudget();
    }

    private sealed class RetainedLeaveResources : IDisposable
    {
        private readonly List<IDisposable> _retained = [];

        public void Add(IDisposable resource) => _retained.Add(resource);

        // Release the deliberately retained resources only after the budget assertion.
        public void Dispose()
        {
            foreach (var resource in _retained) resource.Dispose();
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
        int activeCycle = 0;
        string phase = "second-peer-role";
        try
        {
            await WaitAsync(() => Get<bool>(second, "_combatRoleApplyComplete"), "the second guest's authenticated role", ct);
            Assert.False(Get<bool>(second, "_isDamageAuthority"));
            phase = "second-peer-world";
            await InvokeAsync(host, "Wo206AnnounceAsync", "fixture-second-active", (byte?)secondPeer.Id);
            await WaitAsync(() => Get<bool>(second, "_hostModeKnown") && Get<Wo206Epochs>(second, "_w206").World != 0,
                "the second guest to learn the same authenticated world", ct);
            Assert.Equal(Get<Wo206Epochs>(first, "_w206").World, Get<Wo206Epochs>(second, "_w206").World);
            Set(first, "_w184SaveOnLeave", false);
            Set(second, "_w184SaveOnLeave", false);
            firstEngine.DeferOwnFileRead = true;
            secondEngine.DeferOwnFileRead = true;
            using var resources = new F4ResourceCheck($"active-guests-{players}", first, output, null, host, second);
            for (int cycle = 1; cycle <= 6; cycle++)
            {
                activeCycle = cycle;
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

                phase = "first-guest-join";
                await JoinAsync(first, firstLink, firstEngine, "playline0/save001");
                // The host serializes its save/transfer, but both guests stay live.
                phase = "second-guest-join";
                await JoinAsync(second, secondLink, secondEngine, "playline1/save002");
                Assert.True(Get<bool>(first, "_joinedWorld"));
                phase = "overlapping-private-loads";
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
                phase = "resource-sample";
                await resources.CycleAsync(cycle - 1, ct, host, second);
            }
            phase = "resource-budget";
            resources.AssertBudget();
        }
        catch (Exception error)
        {
            output.WriteLine($"F4-ACTIVE-GUESTS failed players={players} cycle={activeCycle} phase={phase} type={error.GetType().Name} " +
                $"second_pipe={secondPipe.Completion.Status} second_pipe_drop={secondPipe.Pipe.LastConnectionDropReason}");
            throw;
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
        int ThreadPoolWorkers, long RuntimeTimers, F4Owned Owned, F4WindowsHandleTypes.Inventory WindowsHandles,
        IReadOnlyDictionary<IntPtr, string>? WindowsHandleObjects, IReadOnlyDictionary<nint, string>? PersistentMonitorHandles,
        HashSet<long>? WindowsThreadIds, Task<bool>[] FrameDeadlines);

    private static F4ResourceSample F4Resources(GameBridge guest, F4WindowsHandleTrace? windowsTrace, F4ProcessorDeadlines deadlineTrace)
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
        bool traceWindowsHandles = windowsTrace is not null;
        Dictionary<IntPtr, string>? objects = traceWindowsHandles ? new() : null;
        // A pinned live monitor keeps this same event through the inventory.
        var monitors = windowsTrace?.SampleMonitorHandles();
        var inventory = traceWindowsHandles ? F4WindowsHandleTypes.Capture(objects) : default;
        // Count after the inventory calls, so handles they initialise are present in the same observation.
        process.Refresh();
        var processThreads = process.Threads;
        var threadIds = traceWindowsHandles ? processThreads.Cast<ProcessThread>().Select(thread => (long)thread.Id).ToHashSet() : null;
        int handles = process.HandleCount;
        return new(processThreads.Count, handles, rawThreads, rawHandles, ThreadPool.ThreadCount, runtimeTimers,
            new(pipe.IsConnected ? 1 : 0, pipe.EdgeConnectionRevision, leases, timers), inventory, objects, monitors, threadIds, frameDeadlines);
    }

    private sealed class F4ResourceCheck : IDisposable
    {
        private const int TimerAllowance = 1;
        private readonly string _scenario;
        private readonly GameBridge _guest;
        private readonly ReplayRelay? _relay;
        private readonly ITestOutputHelper _output;
        private readonly AutoResetEvent _requestReady = new(false);
        private readonly F4ProcessorDeadlines _deadlineTrace = new();
        private readonly Thread _sampler;
        private readonly F4WindowsHandleTrace? _handleTrace;
        private TaskCompletionSource<F4ResourceSample>? _request;
        private bool _stopSampler;
        private int _nextCycle;
        private F4ResourceSample _before, _after;

        private readonly (GameBridge Player, TimeProvider Previous)[] _processorClocks;

        public F4ResourceCheck(string scenario, GameBridge guest, ITestOutputHelper output, ReplayRelay? relay,
            params GameBridge[] others)
        {
            _scenario = scenario;
            _guest = guest;
            _output = output;
            _relay = relay;
            _processorClocks = others.Prepend(guest).Select(player => (player, player.ProcessorTime)).ToArray();
            foreach (var (player, previous) in _processorClocks) player.ProcessorTime = _deadlineTrace.Wrap(previous);
            if (OperatingSystem.IsWindows()) _handleTrace = new(observeMonitors: relay is not null);
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
            int target = useWinsock ? active : 16;
            int warmWorkers = Math.Clamp(Math.Max(16, target), workerMin, workerMax);
            var warmed = new ConcurrentDictionary<int, byte>();
            try
            {
                Assert.True(ThreadPool.SetMinThreads(warmWorkers, ioMin));
                await WarmWorkerRoundAsync(warmWorkers, useWinsock, warmed, ct);
                _output.WriteLine($"F4-POOL-WARMUP scenario={_scenario} primed={warmed.Count} worker_min={workerMin} worker_max={workerMax} after_join_workers={active} reached={ThreadPool.ThreadCount} target={warmWorkers}");
            }
            finally
            {
                Assert.True(ThreadPool.SetMinThreads(workerMin, ioMin));
            }
        }

        private static async Task WarmWorkerRoundAsync(int workers, bool useWinsock,
            ConcurrentDictionary<int, byte> warmed, CancellationToken ct)
        {
            using var round = new ManualResetEventSlim();
            await Task.WhenAll(Enumerable.Range(0, workers).Select(_ => Task.Run(() =>
            {
                Task exchange = warmed.TryAdd(Environment.CurrentManagedThreadId, 0) && useWinsock
                    ? WarmWinsockThreadAsync(ct) : Task.CompletedTask;
                if (warmed.Count >= workers) round.Set();
                // Hold the worker only while the other workers issue their calls.
                // The I/O completes asynchronously, outside the parking gate.
                round.Wait(TimeSpan.FromMilliseconds(200), ct);
                return exchange;
            }, ct)));
        }

        private static async Task WarmWinsockThreadAsync(CancellationToken ct)
        {
            // The production NetworkStream uses overlapped WSARecv and WSASend.
            // Issue both on this worker. Blocking on either Task would ask .NET
            // to create additional workers without these Winsock handles.
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
            await write;
            Assert.Equal(1, await read);
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
                try { request.SetResult(F4Resources(_guest, _handleTrace, _deadlineTrace)); }
                catch (Exception ex) { request.SetException(ex); }
            }
        }

        public async Task CycleAsync(int cycle, CancellationToken ct, params GameBridge[] others)
        {
            Assert.Equal(_nextCycle++, cycle);
            if (cycle == 0)
            {
                if (_relay is not null && _handleTrace is { TracksMonitors: true } trace)
                {
                    foreach (var session in _relay.ReadySessions())
                        trace.TrackGates($"relay-{session.Id}", session);
                    TrackPlayer("guest", _guest);
                    for (int index = 0; index < others.Length; index++)
                        TrackPlayer($"partner-{index}", others[index]);

                    void TrackPlayer(string role, GameBridge agent)
                    {
                        trace.TrackGates(role, agent);
                        trace.TrackGates($"{role}.combat", Get<CombatPipe>(agent, "_combat"));
                        trace.TrackGates($"{role}.transport", Get<IGameTransport>(agent, "_transport"));
                    }
                }
                await PrimeWorkersAsync(ct);
            }
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
            if (sample.Owned.PipeRevision != _before.Owned.PipeRevision)
                _output.WriteLine($"F4-PIPE-REVISION scenario={_scenario} cycle={cycle} before={_before.Owned.PipeRevision} after={sample.Owned.PipeRevision} " +
                    $"reason={Get<CombatPipe>(_guest, "_combat").LastConnectionDropReason}");
            if (sample.WindowsHandles.Details is string types)
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

        public static async Task WaitForNativeTransitionAsync(GameBridge player, CancellationToken ct)
        {
            if (Get<Task<bool>?>(player, "_p1Pending") is { } authority)
                await authority.WaitAsync(ct);
            await Get<Task>(player, "_privateReturnMenuUpdate").WaitAsync(ct);
            // The observer must not contend on a group's private semaphore
            // monitors while it samples Windows resource growth.
            var publish = Get<SemaphoreSlim>(player, "_loadSequencePublish");
            var calendar = Get<SemaphoreSlim>(player, "_hostCalendarGate");
            var send = Get<SemaphoreSlim>(player, "_hostCalendarSendGate");
            var native = Get<SemaphoreSlim>(Get<CombatPipe>(player, "_combat"), "_gate");
            await WaitAsync(() => publish.CurrentCount != 0 && calendar.CurrentCount != 0 && send.CurrentCount != 0 &&
                native.CurrentCount != 0, "native publications and their frame waits to finish", ct);
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
            F4WindowsHandleTrace.Bookkeeping runtime = default;
            int idleWorkers = 0;
            bool checkThreads = after.Threads > before.Threads + 4;
            if (_handleTrace is not null && (after.Handles > before.Handles + 4 || checkThreads) &&
                before.WindowsHandleObjects is { } start && after.WindowsHandleObjects is { } end &&
                before.WindowsThreadIds is { } startThreads && after.WindowsThreadIds is { } endThreads)
            {
                var newThreads = new HashSet<long>(endThreads);
                newThreads.ExceptWith(startThreads);
                var origins = _handleTrace.DescribeNewHandles(start, end, after.PersistentMonitorHandles, newThreads, checkThreads);
                runtime = origins.Runtime;
                idleWorkers = Math.Min(origins.IdleNewWorkers, Math.Max(0, after.ThreadPoolWorkers - before.ThreadPoolWorkers));
                _output.WriteLine($"F4-HANDLE-ORIGINS scenario={_scenario} {origins.Details}");
            }
            int comparedHandles = after.Handles - runtime.Total;
            int comparedThreads = after.Threads - idleWorkers;
            _output.WriteLine($"F4-HANDLE-BUDGET scenario={_scenario} before={before.Handles} after={after.Handles} " +
                $"winsock_pairs={runtime.WinsockPairs} regex_cache_events={runtime.RegexCacheEvents} " +
                $"worker_starts={runtime.WorkerStarts} worker_name_events={runtime.WorkerNameEvents} " +
                $"winsock_thread_refs={runtime.WinsockThreadReferences} persistent_monitor_events={runtime.PersistentMonitorEvents} compared_after={comparedHandles}");
            _output.WriteLine($"F4-THREAD-BUDGET scenario={_scenario} before={before.Threads} after={after.Threads} " +
                $"idle_new_workers={idleWorkers} compared_after={comparedThreads}");
            int? startFiles = before.WindowsHandles.FileHandles;
            int? endFiles = after.WindowsHandles.FileHandles;
            if (startFiles.HasValue && endFiles.HasValue)
                _output.WriteLine($"F4-FILE-BUDGET scenario={_scenario} before={startFiles} after={endFiles}");
            Assert.True(after.RuntimeTimers <= before.RuntimeTimers + TimerAllowance,
                $"F4 runtime timer budget: scenario={_scenario} {before.RuntimeTimers}->{after.RuntimeTimers}, allowance={TimerAllowance}");
            if (_handleTrace is not null)
            {
                // A terminated runtime worker must not offset a still-open file or socket.
                Assert.True(startFiles.HasValue && endFiles.HasValue, $"Missing Windows file handle inventory for {_scenario}");
                Assert.True(endFiles <= startFiles + 4,
                    $"F4 application handle budget: scenario={_scenario} Windows file handles {startFiles}->{endFiles}, allowance=4");
            }
            Assert.True(comparedThreads >= 0 && comparedThreads <= before.Threads + 4,
                $"F4 application thread budget: scenario={_scenario} {before.Threads}->{comparedThreads}, raw={after.Threads}, allowance=4");
            Assert.True(comparedHandles >= 0 && comparedHandles <= before.Handles + 4,
                $"F4 application handle budget: scenario={_scenario} {before.Handles}->{comparedHandles}, raw={after.Handles}, allowance=4");
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
                foreach (var (player, previous) in _processorClocks) player.ProcessorTime = previous;
            }
        }
    }
}
