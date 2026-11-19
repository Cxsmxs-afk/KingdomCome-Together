// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.Collections;
using System.Text;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Reflection;
using KcdMp.Wire;

namespace KcdMp.Client.Tests;

[CollectionDefinition("Join calendar integration", DisableParallelization = true)]
public sealed class JoinCalendarIntegrationCollection { }

[Collection("Join calendar integration")]
public sealed class JoinCalendarIntegrationTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private enum JoinProbe { None, Delay, Scope, HostLossBeforeOffer, HostLossWhileWaiting, MissingHold, MenuProgress }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task A_hosts_and_B_joins_from_the_main_menu(bool dropFirstSnapshot) =>
        RunAsync("A", "B", dropFirstSnapshot);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task B_hosts_and_A_joins_from_the_main_menu(bool dropFirstSnapshot) =>
        RunAsync("B", "A", dropFirstSnapshot);

    [Fact]
    public Task A_delayed_calendar_can_arrive_after_five_seconds_while_the_host_still_waits() =>
        RunAsync("A", "B", dropFirstSnapshot: true, JoinProbe.Delay);

    [Fact]
    public Task Authenticated_stale_calendar_cannot_arm_the_latch_and_duplicates_arm_it_once() =>
        RunAsync("A", "B", dropFirstSnapshot: false, JoinProbe.Scope);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Host_loss_retires_the_join_before_the_guest_can_load(bool beforeOffer) =>
        RunAsync("A", "B", dropFirstSnapshot: false,
            beforeOffer ? JoinProbe.HostLossBeforeOffer : JoinProbe.HostLossWhileWaiting);

    [Fact]
    public Task Host_does_not_offer_a_world_the_native_pipe_never_confirmed_as_held() =>
        RunAsync("A", "B", dropFirstSnapshot: false, JoinProbe.MissingHold);

    [Fact]
    public Task Menu_progress_cannot_renew_the_host_hold_before_the_calendar_receipt() =>
        RunAsync("A", "B", dropFirstSnapshot: false, JoinProbe.MenuProgress);

    [Theory]
    [InlineData("A", false)]
    [InlineData("A", true)]
    [InlineData("B", false)]
    [InlineData("B", true)]
    public Task A_retired_join_cannot_cross_the_engine_HTTP_boundary(string host, bool hostLoss) =>
        RunBoundaryAsync(host, "dispatch", hostLoss);

    [Theory]
    [InlineData("A", false)]
    [InlineData("A", true)]
    [InlineData("B", false)]
    [InlineData("B", true)]
    public Task A_retired_join_cannot_send_an_already_queued_ready(string host, bool hostLoss) =>
        RunBoundaryAsync(host, "ready", hostLoss);

    [Theory]
    [InlineData("A", false)]
    [InlineData("A", true)]
    [InlineData("B", false)]
    [InlineData("B", true)]
    public Task A_late_engine_read_keeps_the_file_safe_and_cannot_make_an_aborted_join_ready(string host, bool hostLoss) =>
        RunBoundaryAsync(host, "late-read", hostLoss);

    [Fact]
    public Task Guest_transport_loss_after_load_returns_to_the_own_world_only_after_the_engine_finishes() =>
        RunAsync("A", "B", dropFirstSnapshot: false, boundary: new("late-read", HostLoss: false, GuestLoss: true));

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public Task Targeted_join_loads_with_other_real_relay_peers_present(int players) =>
        RunAsync(players == 3 ? "A" : "B", players == 3 ? "B" : "A", dropFirstSnapshot: false, extraPeers: players - 2);

    private sealed record BoundaryProbe(string Edge, bool HostLoss, bool GuestLoss = false);

    private static Task RunBoundaryAsync(string host, string edge, bool hostLoss) =>
        RunAsync(host, host == "A" ? "B" : "A", dropFirstSnapshot: false, boundary: new(edge, hostLoss));

    private static async Task RunAsync(string hostName, string guestName, bool dropFirstSnapshot, JoinProbe probe = JoinProbe.None,
        BoundaryProbe? boundary = null, int extraPeers = 0)
    {
        using var fixture = new SaveFixture();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(boundary is null ? 25 : 40));
        var ct = stop.Token;
        await using var relay = await ReplayRelay.StartAsync(ct);
        await using var hostPeer = await ReplayPeer.ConnectAsync(relay.Port, hostName, ct);
        await using var guestPeer = await ReplayPeer.ConnectAsync(relay.Port, guestName, ct);
        await using var observerC = extraPeers >= 1 ? await ReplayPeer.ConnectAsync(relay.Port, "C", ct) : null;
        await using var observerD = extraPeers >= 2 ? await ReplayPeer.ConnectAsync(relay.Port, "D", ct) : null;
        Assert.NotEqual(hostPeer.Id, guestPeer.Id);
        byte[] hello = Protocol.BuildLinkHelloUp(Protocol.LinkCapWorldSync);
        await hostPeer.SendAsync(hello, ct);
        await guestPeer.SendAsync(hello, ct);

        var hostLink = new JoinWire(hostPeer.Wire, await hostPeer.DrainAsync(ct), dropFirstSnapshot: false);
        var guestLink = new JoinWire(guestPeer.Wire, await guestPeer.DrainAsync(ct), dropFirstSnapshot)
        {
            WithholdFrozenSnapshots = probe is JoinProbe.Delay or JoinProbe.MenuProgress or JoinProbe.HostLossWhileWaiting,
            WithholdWorldTransfer = probe == JoinProbe.HostLossBeforeOffer
        };
        await using var hostPipe = new JoinCalendarTestPipe(refuseHold: probe == JoinProbe.MissingHold);
        await using var guestPipe = new JoinCalendarTestPipe();
        await using var hostGame = new JoinCalendarTestEngine(Path.Combine(fixture.Root, "host.log"), fixture.HostSaves, fixture.HostSave, hostPipe.ObserveGameplayStarted);
        await using var guestGame = new JoinCalendarTestEngine(Path.Combine(fixture.Root, "guest.log"), fixture.GuestSaves, null, guestPipe.ObserveGameplayStarted);
        var host = CreateBridge(hostPeer.Id, true, hostLink, hostPipe.Pipe, hostGame.Transport, fixture.HostSaves, ct);
        var guest = CreateBridge(guestPeer.Id, false, guestLink, guestPipe.Pipe, guestGame.Transport, fixture.GuestSaves, ct);
        guestGame.Transport.MainMenuShown += guestPipe.ObserveMainMenu;
        guestGame.DeferJoinFileRead = boundary?.Edge == "late-read";
        guestGame.DeferSaveLockReply = boundary?.Edge == "ready";
        hostGame.SaveWritten = path => Call(host, "OnSaveFileEvent", path);
        guestGame.Emit("wo124_where", "menu");
        hostGame.Emit("wo124_where", "world");
        Call(host, "Wo125HostIdentify", fixture.HostLoadedSave, true, "fixture starting world");

        Task hostRead = Task.CompletedTask;
        Task guestRead = InvokeAsync(guest, "ReceiveLoopAsync", guestLink, ct);
        try
        {
            // Complete the guest's authenticated role first. Starting the host reader announces the world
            // to the guest as soon as it handles the peer name.
            await WaitAsync(() => Get<bool>(guest, "_combatRoleApplyComplete"), "the guest's relay-assigned combat role", ct);
            Assert.False(Get<bool>(guest, "_isDamageAuthority"));
            hostRead = InvokeAsync(host, "ReceiveLoopAsync", hostLink, ct);
            await WaitAsync(() => Get<bool>(host, "_combatRoleApplyComplete"), "the host's relay-assigned combat role", ct);
            Assert.True(Get<bool>(host, "_isDamageAuthority"));
            await WaitAsync(() => (Get<ushort>(guest, "_w213Caps") & Protocol.LinkCapWorldSync) != 0
                    && ((List<byte>)Call(host, "LivePartners")!).Count == 1 + extraPeers,
                "the relay's world-sync capability and all live peers", ct);
            await InvokeAsync(host, "Wo124HostModeTickAsync");
            await InvokeAsync(host, "Wo206AnnounceAsync", "fixture", (byte?)guestPeer.Id);
            await WaitAsync(() => Get<bool>(guest, "_hostModeKnown") && Get<Wo206Epochs>(guest, "_w206").World != 0,
                "guest learned the host world and session mode", ct);
            Assert.Equal(hostPeer.Id, Get<byte>(guest, "_hostModeFrom"));
            Call(guest, "Wo125OnLauncherChoice", "bring");
            Assert.True(Property<bool>(guest, "LoadSequenceEnabled"));
            Assert.True(Property<bool>(guest, "WorldSyncEffective"));
            Assert.False(Property<bool>(guest, "WorldSyncReady"));
            Assert.Equal(WorldLoadPhase.Menu, Get<WorldLoadSequence>(guest, "_worldLoad").Current.Phase);

            var atOffer = new TaskCompletionSource<(long ClockIn, HostCalendarSample? Bootstrap)>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            guestLink.BeforeFirstOffer = () => atOffer.TrySetResult(
                (Get<long>(guest, "_w200ClockIn"), Get<HostCalendarSample?>(guest, "_hostCalendarBootstrap")));
            if (probe == JoinProbe.Scope) guestLink.ProbeCalendarEpoch = Get<Wo206Epochs>(guest, "_w206").World;
            if (boundary is not null)
            {
                await CheckAbortBoundaryAsync(boundary, fixture, host, guest, hostPeer, guestPeer, hostLink, guestLink,
                    hostGame, guestGame, guestRead, ct);
                return;
            }
            await InvokeAsync(guest, "SendJoinRequestAsync");
            uint joinId = Get<uint>(guest, "_joinOutId");
            Assert.NotEqual(0u, joinId);
            if (probe is JoinProbe.HostLossBeforeOffer or JoinProbe.HostLossWhileWaiting)
            {
                if (probe == JoinProbe.HostLossBeforeOffer) await atOffer.Task.WaitAsync(ct);
                else await WaitForCalendarAsync(guest, ct);
                hostPeer.FinishSending();
                await WaitAsync(() => !Get<bool>(guest, "_hostModeKnown"), "the real relay's host departure", ct);
                Assert.Equal(0u, Get<uint>(guest, "_joinOutId"));
                Assert.Null(Get<HostCalendarSample?>(guest, "_hostCalendarBootstrap"));
                Assert.False(guestPipe.ArmObserved.IsCompleted);
                Assert.False(guestGame.LoadIssued.IsCompleted);
                Assert.False(hostLink.Ready.IsCompleted);
                return;
            }
            if (probe == JoinProbe.MissingHold)
            {
                Assert.Equal("clock-unavailable", await hostGame.Resumed.WaitAsync(ct));
                Assert.False(atOffer.Task.IsCompleted);
                Assert.Empty(guestLink.FrozenSnapshots);
                Assert.False(guestPipe.ArmObserved.IsCompleted);
                return;
            }
            if (probe == JoinProbe.MenuProgress)
            {
                await WaitForCalendarAsync(guest, ct);
                Call(guest, "Wo204Tick", 0L);
                var hostDrops = Get<ConcurrentDictionary<string, long>>(host, "_joinCalendarDrops");
                await WaitAsync(() => hostDrops.ContainsKey("progress-before-calendar-receipt") ||
                    Get<long>(host, "_w204JoinProgressIn") > 0, "the host's handling of the menu progress", ct);
                Assert.Equal(0, Get<long>(host, "_w204JoinProgressIn"));
                guestLink.WithholdFrozenSnapshots = false;
            }
            if (probe == JoinProbe.Delay)
                await HoldPastOldCalendarDeadlineAsync(host, guest, hostGame, guestGame, hostPipe, guestLink, ct);

            string resumeReason = await hostGame.Resumed.WaitAsync(ct);
            var (clockAtOffer, bootstrapAtOffer) = await atOffer.Task.WaitAsync(ct);
            Assert.NotEmpty(guestLink.FrozenSnapshots);
            Assert.All(guestLink.FrozenSnapshots, snapshot => Assert.Equal(joinId, snapshot.JoinId));
            Assert.True(resumeReason == "ready",
                $"Host resumed with '{resumeReason}' after receiving {guestLink.FrozenSnapshots.Count} frozen calendar frame(s); " +
                $"guest clock_in before the WorldOffer was {clockAtOffer}, final clock_in is {Get<long>(guest, "_w200ClockIn")}, " +
                $"guest final UI phase is '{Get<string>(guest, "_joinUiState")}'.");
            Assert.True(guestPipe.ArmObserved.IsCompletedSuccessfully);
            Assert.True(guestGame.LoadIssued.IsCompletedSuccessfully);
            Assert.True(guestPipe.VerifyObserved.IsCompletedSuccessfully);
            Assert.True(hostPipe.HoldObserved.IsCompletedSuccessfully);
            Assert.True(hostPipe.ReleaseObserved.IsCompletedSuccessfully);
            Assert.True(hostPipe.SnapshotWhileHeld);
            Assert.Equal(guestLink.FrozenSnapshots.First().WorldMs, guestPipe.ArmedWorldMs);
            Assert.Equal(guestPipe.ArmedWorldMs, guestPipe.VerifiedWorldMs);
            Assert.Equal(1, guestPipe.ArmRequests);
            Assert.True(Get<long>(guest, "_w200ClockIn") >= 1);
            if (probe == JoinProbe.Scope)
            {
                Assert.Equal(1, clockAtOffer);
                var drops = Get<ConcurrentDictionary<string, long>>(guest, "_joinCalendarDrops");
                Assert.True(drops.ContainsKey("different-world"));
                Assert.True(drops.ContainsKey("inactive-attempt"));
            }
            if (dropFirstSnapshot)
            {
                Assert.True(guestLink.DroppedFirstSnapshot);
                Assert.True(guestLink.FrozenSnapshots.Count >= 2);
                Assert.All(guestLink.FrozenSnapshots, snapshot => Assert.Equal(guestPipe.ArmedWorldMs, snapshot.WorldMs));
            }
            else if (probe != JoinProbe.MenuProgress)
            {
                Assert.True(clockAtOffer >= 1);
                Assert.True(bootstrapAtOffer?.ReadyForJoin(Environment.TickCount64, joinId));
            }

            AssertReady(await hostLink.Ready.WaitAsync(ct), guestPeer.Id, joinId);
            await WaitAsync(() => Get<object?>(host, "_hostJoin") is null && Get<string>(guest, "_joinUiState") == "in",
                "the host finished the join and the guest entered the world", ct);
            await guestGame.JoinedNotification.WaitAsync(ct);
            Assert.True(Get<bool>(guest, "_joinedWorld"));
            foreach (var observer in new[] { observerC, observerD }.OfType<ReplayPeer>())
            {
                var observed = await observer.DrainAsync(ct);
                Assert.DoesNotContain(observed, packet => packet.Type is Protocol.WorldOfferDown or Protocol.WorldChunkDown or Protocol.JoinerReadyDown);
            }
        }
        finally
        {
            stop.Cancel();
            await InvokeAsync(host, "Wo123OnDisconnectAsync");
            await InvokeAsync(guest, "Wo123OnDisconnectAsync");
            try { await Task.WhenAll(hostRead, guestRead).WaitAsync(TimeSpan.FromSeconds(5)); }
            finally
            {
                Get<FileSystemWatcher?>(host, "_savesWatcher")?.Dispose();
                Get<FileSystemWatcher?>(guest, "_savesWatcher")?.Dispose();
            }
        }
    }

    private static async Task CheckAbortBoundaryAsync(BoundaryProbe probe, SaveFixture fixture,
        GameBridge host, GameBridge guest, ReplayPeer hostPeer, ReplayPeer guestPeer, JoinWire hostLink, JoinWire guestLink,
        JoinCalendarTestEngine hostGame, JoinCalendarTestEngine guestGame, Task guestRead, CancellationToken ct)
    {
        byte[] ownBefore = File.ReadAllBytes(fixture.GuestOwnSave);
        using var dispatch = probe.Edge == "dispatch" ? new LoadPhaseBarrier() : null;
        LinkWriter? writer = probe.Edge == "ready" ? new(new LinkScheduler(new LinkSchedOptions { Budget = false }),
            new StreamLinkPath(guestLink)) : null;
        using var writerStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (writer is not null)
        {
            Set(guest, "_w213Stream", guestLink);
            Set(guest, "_w213Writer", writer);
        }
        var writerTask = writer?.RunAsync(writerStop.Token) ?? Task.CompletedTask;
        try
        {
            await InvokeAsync(guest, "SendJoinRequestAsync");
            uint joinId = Get<uint>(guest, "_joinOutId");
            Assert.NotEqual(0u, joinId);
            if (dispatch is not null)
            {
                await dispatch.Arrived.WaitAsync(ct);
                await AbortThroughRelayAsync(probe, guest, hostPeer, guestPeer, guestLink, joinId, ct, waitForDuplicate: false);
                dispatch.Release();
                if (!probe.HostLoss)
                    await WaitAsync(() => guestLink.Aborts.Count(id => id == joinId) >= 2, "the duplicate authenticated abort", ct);
                await WaitAsync(() => guestGame.JoinLoadsIssued != 0 || Get<object?>(guest, "_jj") is null,
                    "the retired load worker to reach its HTTP decision", ct);
                Assert.Equal(0, guestGame.JoinLoadsIssued);
            }
            else if (writer is not null)
            {
                await guestGame.WaitForSaveLockAsync(ct);
                var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var flushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                byte[] before = PingMarker(2001), after = PingMarker(2002);
                guestLink.BeforeWrite = async (frame, token) =>
                {
                    if (frame.Span.SequenceEqual(before))
                    {
                        blocked.TrySetResult();
                        await release.Task.WaitAsync(token);
                    }
                    if (frame.Span.SequenceEqual(after)) flushed.TrySetResult();
                };
                try
                {
                    writer.Enqueue(before);
                    await blocked.Task.WaitAsync(ct);
                    guestGame.ReleaseSaveLock();
                    await WaitAsync(() => HasQueuedReady(writer.Scheduler, joinId), "the Ready to queue while TCP is held", ct);
                    await AbortThroughRelayAsync(probe, guest, hostPeer, guestPeer, guestLink, joinId, ct);
                    writer.Enqueue(after);
                    release.TrySetResult();
                    await flushed.Task.WaitAsync(ct);
                    Assert.DoesNotContain(joinId, guestLink.ReadySent);
                }
                finally { release.TrySetResult(); }
            }
            else
            {
                string file = await guestGame.WaitForJoinLoadAsync(ct);
                byte[] placed = File.ReadAllBytes(file);
                if (probe.GuestLoss)
                {
                    guestPeer.FinishSending();
                    await guestRead.WaitAsync(ct);
                    Set(guest, "_wo122Ct", new CancellationToken(canceled: true));
                    guestGame.Transport.SaveLoadAccepted -= Handler<Action<string>>(guest, "Wo124OnSaveLoadAccepted");
                    guestGame.Transport.LoadStarted -= Handler<Action>(guest, "Wo124OnLoadStarted");
                    guestGame.Transport.LoadReconstructionStarted -= Handler<Action>(guest, "LoadSequenceOnReconstruction");
                    guestGame.Transport.GameplayStarted -= Handler<Action>(guest, "Wo124OnGameplayStarted");
                    guestGame.Transport.LoadFailedToMenu -= Handler<Action>(guest, "Wo124OnLoadFailedToMenu");
                    await InvokeAsync(guest, "Wo123OnDisconnectAsync");
                    await InvokeAsync(guest, "Wo124OnDisconnectAsync");
                }
                else await AbortThroughRelayAsync(probe, guest, hostPeer, guestPeer, guestLink, joinId, ct);
                await WaitAsync(() => Get<object?>(guest, "_jj") is not { } join ||
                    GetVisible<string>(join, "Phase") != "loading" ||
                    GetVisible<TaskCompletionSource<bool>>(join, "Abandoned").Task.IsCompleted,
                    "the submitted join to be retired before the engine reads it", ct);
                Assert.Equal(placed, File.ReadAllBytes(file));
                Assert.Equal(0, guestGame.JoinFilesRead);
                Assert.Equal(0, guestGame.OwnLoadsIssued);
                Assert.Empty(guestLink.ReadySent);
                guestGame.CompleteJoinLoad();
                Assert.Equal(1, guestGame.JoinFilesRead);
                await guestGame.WaitForOwnLoadAsync(ct);
                await WaitAsync(() => Get<object?>(guest, "_jj") is null && !Get<bool>(guest, "_joinedWorld") &&
                    !Get<bool>(guest, "_leaveInProgress"),
                    "the abandoned arrival to return to the guest's own world", ct);
                if (!probe.HostLoss && !probe.GuestLoss)
                    await RejoinAfterAbortAsync(fixture, host, guest, guestPeer.Id, joinId, hostGame, guestGame, hostLink,
                        guestLink, ct);
            }
            Assert.DoesNotContain(joinId, guestLink.ReadySent);
            Assert.DoesNotContain(joinId, hostLink.ReadyReceived);
            Assert.Equal(ownBefore, File.ReadAllBytes(fixture.GuestOwnSave));
        }
        finally
        {
            dispatch?.Release();
            writerStop.Cancel();
            await writerTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task AbortThroughRelayAsync(BoundaryProbe probe, GameBridge guest, ReplayPeer hostPeer,
        ReplayPeer guestPeer, JoinWire guestLink, uint joinId, CancellationToken ct, bool waitForDuplicate = true)
    {
        Assert.NotNull(Get<object?>(guest, "_joinCalendarRequest"));
        if (probe.HostLoss) hostPeer.FinishSending();
        else
        {
            // The authenticated host's actual TCP stream emits the abort twice before the guest can continue.
            byte[] frame = WorldReceiver.BuildAbort(guestPeer.Id, joinId, Protocol.JoinAbortHostCancel);
            await hostPeer.SendAsync(frame, ct);
            await hostPeer.SendAsync(frame, ct);
        }
        await WaitAsync(() => Get<object?>(guest, "_joinCalendarRequest") is null &&
            (probe.HostLoss ? guestLink.Disconnected.Contains(hostPeer.Id) : guestLink.Aborts.Count(id => id == joinId) >= (waitForDuplicate ? 2 : 1)),
            "the real reader to retire the host's join", ct);
    }

    private static async Task RejoinAfterAbortAsync(SaveFixture fixture, GameBridge host, GameBridge guest,
        byte guestId, uint abortedJoinId, JoinCalendarTestEngine hostGame, JoinCalendarTestEngine guestGame,
        JoinWire hostLink, JoinWire guestLink, CancellationToken ct)
    {
        hostGame.Emit("join_cancel", $"{abortedJoinId:x8}");
        await WaitAsync(() => Get<object?>(host, "_hostJoin") is null, "the host to finish cancelling the first join", ct);
        guestGame.DeferJoinFileRead = false;
        guestGame.Feed("PlayVideoOnly 'main_menu_trosecko'");
        guestGame.Emit("wo124_where", "menu");
        var (freshHost, _) = WhsSaveTests.Pair();
        freshHost.SaveTime = 1_790_000_001;
        hostGame.NextHostSave = () => WhsSaveTests.File(freshHost);
        await InvokeAsync(guest, "SendJoinRequestAsync");
        uint rejoinId = Get<uint>(guest, "_joinOutId");
        Assert.NotEqual(0u, rejoinId);
        Assert.NotEqual(abortedJoinId, rejoinId);
        await WaitAsync(() => hostLink.ReadyReceived.Contains(rejoinId) && Get<string>(guest, "_joinUiState") == "in" &&
            Get<object?>(host, "_hostJoin") is null, "a new request to join and the host to accept its Ready", ct);
        await guestGame.JoinedNotification.WaitAsync(ct);
        Assert.Equal(1, guestLink.ReadySent.Count(id => id == rejoinId));
        Assert.Equal(1, hostLink.ReadyReceived.Count(id => id == rejoinId));
        Assert.Equal(2, guestGame.JoinLoadsIssued);
        Assert.Equal(1, guestGame.OwnLoadsIssued);
        Assert.True(File.Exists(fixture.HostLoadedSave));
    }

    private static byte[] PingMarker(long value)
    {
        byte[] body = new byte[Protocol.PingPayloadLen];
        BinaryPrimitives.WriteInt64LittleEndian(body, value);
        return ReplayPeer.Frame(Protocol.Ping, body);
    }

    private static bool HasQueuedReady(LinkScheduler scheduler, uint joinId)
    {
        lock (Get<object>(scheduler, "_lock"))
            foreach (object entry in Get<IEnumerable>(scheduler, "_events"))
            {
                byte[] frame = GetVisible<byte[]>(entry, "Item1");
                if (frame[0] == Protocol.JoinerReadyUp && ReadyUpId(frame) == joinId) return true;
            }
        return false;
    }

    private static uint ReadyUpId(ReadOnlySpan<byte> frame) => BinaryPrimitives.ReadUInt32LittleEndian(frame[4..]);

    private static T GetVisible<T>(object instance, string field) =>
        (T)instance.GetType().GetField(field)!.GetValue(instance)!;

    /// <summary>The shipped Load transition is logged after preparing the command and before HTTP submission.</summary>
    private sealed class LoadPhaseBarrier : TextWriter
    {
        private readonly TextWriter _previous = Console.Out;
        private readonly ManualResetEventSlim _continue = new();
        private readonly TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public LoadPhaseBarrier() => Console.SetOut(this);
        public override Encoding Encoding => _previous.Encoding;
        public Task Arrived => _arrived.Task;
        public void Release() => _continue.Set();
        public override void WriteLine(string? value)
        {
            if (value is not null && value.StartsWith("MP-LOAD-SEQUENCE role=joiner ", StringComparison.Ordinal) &&
                value.EndsWith(" phase=Load link_hold=0", StringComparison.Ordinal) && _arrived.TrySetResult())
                _continue.Wait(TimeSpan.FromSeconds(15));
            _previous.WriteLine(value);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Release();
                Console.SetOut(_previous);
            }
            base.Dispose(disposing);
        }
    }

    private static Task WaitForCalendarAsync(GameBridge guest, CancellationToken ct) =>
        WaitAsync(() => Get<TaskCompletionSource?>(guest, "_hostCalendarChanged") is not null,
            "calendar wait after the complete world transfer", ct);

    private static async Task HoldPastOldCalendarDeadlineAsync(GameBridge host, GameBridge guest,
        JoinCalendarTestEngine hostGame, JoinCalendarTestEngine guestGame, JoinCalendarTestPipe hostPipe,
        JoinWire guestWire, CancellationToken ct)
    {
        await WaitAsync(
            () => Get<string>(guest, "_joinUiMessage") == "Waiting for your host's world clock..." || hostGame.Resumed.IsCompleted,
            "the guest to reach the calendar wait after receiving the save", ct);
        Assert.False(hostGame.Resumed.IsCompleted, "The host resumed while the guest had no calendar.");

        var pastOldDeadline = Task.Delay(TimeSpan.FromSeconds(6), ct);
        Assert.Same(pastOldDeadline, await Task.WhenAny(pastOldDeadline, hostGame.Resumed, guestGame.LoadIssued));
        await pastOldDeadline;
        Assert.True(hostPipe.HoldObserved.IsCompletedSuccessfully);
        Assert.False(hostPipe.ReleaseObserved.IsCompleted);
        Assert.Equal(0, Get<long>(guest, "_w200ClockIn"));
        Assert.True(guestWire.FrozenSnapshots.Count > 1);
        var currentJoin = Get<object?>(host, "_hostJoin");
        Assert.NotNull(currentJoin);
        Assert.Equal("waiting-ready", currentJoin.GetType().GetField("Phase")!.GetValue(currentJoin));
        guestWire.WithholdFrozenSnapshots = false;
    }

    private static void AssertReady(byte[] payload, byte guest, uint joinId)
    {
        Assert.True(Protocol.TrySplitJoinDown(payload, out byte source, out _, out uint readyId, out var body));
        Assert.Equal(guest, source);
        Assert.Equal(joinId, readyId);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(body));
    }

    private static GameBridge CreateBridge(byte id, bool hosting, Stream relay, CombatPipe pipe,
        LogTailGameTransport game, string saves, CancellationToken ct)
    {
        var bridge = new GameBridge(new ClientConfig());
        Set(bridge, "_myGhostId", id);
        Set(bridge, "_combat", pipe);
        Set(bridge, "_transport", game);
        Set(bridge, "_savesDir", saves);
        Set(bridge, "_wo122Stream", relay);
        Set(bridge, "_wo122Ct", ct);
        Set(bridge, "_wo122Connected", true);
        Set(bridge, "_sharedWorld", hosting);
        Set(bridge, "_loadSequenceRuntime", true);
        Set(bridge, "_w154JoinStopVideo", false);
        Field(bridge, "_edgeMode").SetValue(bridge, Enum.Parse(Field(bridge, "_edgeMode").FieldType, "Http"));
        game.GameEvent += Handler<Action<string, string>>(bridge, "OnGameEvent");
        game.SaveLoadAccepted += Handler<Action<string>>(bridge, "Wo124OnSaveLoadAccepted");
        game.LoadStarted += Handler<Action>(bridge, "Wo124OnLoadStarted");
        game.LoadReconstructionStarted += Handler<Action>(bridge, "LoadSequenceOnReconstruction");
        game.GameplayStarted += Handler<Action>(bridge, "Wo124OnGameplayStarted");
        game.MainMenuShown += Handler<Action>(bridge, "Wo124OnMainMenuShown");
        game.LoadFailedToMenu += Handler<Action>(bridge, "Wo124OnLoadFailedToMenu");
        return bridge;
    }

    private static T Handler<T>(GameBridge b, string name) where T : Delegate =>
        b.GetType().GetMethod(name, Private)!.CreateDelegate<T>(b);

    private static FieldInfo Field(object o, string name) => o.GetType().GetField(name, Private)!;
    private static T Get<T>(object o, string name) => (T)Field(o, name).GetValue(o)!;
    private static void Set(object o, string name, object? value) => Field(o, name).SetValue(o, value);
    private static T Property<T>(GameBridge b, string name) => (T)b.GetType().GetProperty(name, Private)!.GetValue(b)!;
    private static object? Call(GameBridge b, string name, params object?[] args) =>
        b.GetType().GetMethod(name, Private)!.Invoke(b, args);
    private static Task InvokeAsync(GameBridge b, string name, params object?[] args) => (Task)Call(b, name, args)!;

    private static async Task WaitAsync(Func<bool> condition, string stage, CancellationToken ct)
    {
        try
        {
            while (!condition()) await Task.Delay(10, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Assert.Fail($"Timed out waiting for {stage}.");
        }
    }

    private sealed class SaveFixture : IDisposable
    {
        private readonly Dictionary<string, string?> _environment = new();

        public string Root { get; } = Directory.CreateTempSubdirectory("kcdmp-join-calendar-").FullName;
        public string HostSaves => Path.Combine(Root, "host-saves");
        public string GuestSaves => Path.Combine(Root, "guest-saves");
        public string HostLoadedSave => Path.Combine(HostSaves, "playline0", "save000.whs");
        public string GuestOwnSave => Path.Combine(GuestSaves, "playline0", "save001.whs");
        public byte[] HostSave { get; }

        public SaveFixture()
        {
            string install = Path.Combine(Root, "install");
            Directory.CreateDirectory(Path.Combine(install, "Data"));
            using (var tables = ZipFile.Open(Path.Combine(install, "Data", "Tables.pak"), ZipArchiveMode.Create))
            {
                using var item = new StreamWriter(tables.CreateEntry("Libs/Tables/item/item__quest.xml").Open());
                string questItem = WhsSaveTests.Quest.Single().Key;
                item.Write($"""<database><MiscItem Id="{questItem}" Name="loveLetter" IsQuestItem="true"/></database>""");
            }

            var (host, guest) = WhsSaveTests.Pair();
            guest.Seed++;
            guest.SaveTime = 1_790_000_000;
            HostSave = WhsSaveTests.File(host);
            Directory.CreateDirectory(Path.Combine(HostSaves, "playline0"));
            Directory.CreateDirectory(Path.Combine(GuestSaves, "playline0"));
            File.WriteAllBytes(HostLoadedSave, HostSave);
            File.WriteAllBytes(GuestOwnSave, WhsSaveTests.File(guest));
            Assert.Single(GameBridge.ListOwnSaves(GuestSaves));
            Override("KCDMP_DATA_DIR", Root);
            Override("KCDMP_JOIN_SAVES_DIR", GuestSaves);
            Override("KCD2MP_INSTALL", install);
        }

        private void Override(string name, string value)
        {
            _environment.Add(name, Environment.GetEnvironmentVariable(name));
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            foreach (var (name, prior) in _environment) Environment.SetEnvironmentVariable(name, prior);
            Directory.Delete(Root, recursive: true);
        }
    }

    /// <summary>Observes actual relay frames and can remove one frozen clock before the guest agent reads it.</summary>
    private sealed class JoinWire(Stream wire, IReadOnlyList<ReplayPacket> initial, bool dropFirstSnapshot) : Stream
    {
        private readonly Queue<ReplayPacket> _initial = new(initial);
        private readonly Queue<ReplayPacket> _injected = new();
        private readonly TaskCompletionSource<byte[]> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private ReadOnlyMemory<byte> _remaining;
        private int _dropped;

        public readonly ConcurrentQueue<HostCalendarSample> FrozenSnapshots = new();
        public bool DroppedFirstSnapshot => Volatile.Read(ref _dropped) != 0;
        public volatile bool WithholdFrozenSnapshots;
        public bool WithholdWorldTransfer { get; init; }
        public volatile uint ProbeCalendarEpoch;
        public Task<byte[]> Ready => _ready.Task;
        public Action? BeforeFirstOffer { get; set; }
        public Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask>? BeforeWrite { get; set; }
        public readonly ConcurrentQueue<uint> Aborts = new();
        public readonly ConcurrentQueue<byte> Disconnected = new();
        public readonly ConcurrentQueue<uint> ReadyReceived = new();
        public readonly ConcurrentQueue<uint> ReadySent = new();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (buffer.IsEmpty) return 0;
            while (_remaining.IsEmpty)
            {
                bool replayed = _injected.TryDequeue(out var packet);
                if (!replayed && !_initial.TryDequeue(out packet))
                {
                    byte[] header = new byte[3];
                    int read = await wire.ReadAsync(header.AsMemory(0, 1), ct);
                    if (read == 0) return 0;
                    await wire.ReadExactlyAsync(header.AsMemory(1), ct);
                    byte[] body = new byte[BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(1))];
                    await wire.ReadExactlyAsync(body, ct);
                    packet = new ReplayPacket(header[0], body);
                }
                if (packet.Type == Protocol.JoinerReadyDown)
                {
                    _ready.TrySetResult(packet.Payload);
                    ReadyReceived.Enqueue(BinaryPrimitives.ReadUInt32LittleEndian(packet.Payload.AsSpan(2)));
                }
                if (packet.Type == Protocol.JoinAbortDown)
                    Aborts.Enqueue(BinaryPrimitives.ReadUInt32LittleEndian(packet.Payload.AsSpan(2)));
                if (packet.Type == Protocol.Disconnect && packet.Payload.Length == 1)
                    Disconnected.Enqueue(packet.Payload[0]);
                if (packet.Type == Protocol.WorldOfferDown)
                {
                    BeforeFirstOffer?.Invoke();
                    BeforeFirstOffer = null;
                }
                if (WithholdWorldTransfer && packet.Type is Protocol.WorldOfferDown or Protocol.WorldChunkDown) continue;
                if (!replayed && packet.Type == Protocol.W164Down && FrozenClock(packet.Payload) is { } sample)
                {
                    FrozenSnapshots.Enqueue(sample);
                    bool firstDrop = dropFirstSnapshot && Interlocked.Exchange(ref _dropped, 1) == 0;
                    if (firstDrop || WithholdFrozenSnapshots) continue;
                    if (ProbeCalendarEpoch != 0)
                    {
                        uint world = ProbeCalendarEpoch;
                        ProbeCalendarEpoch = 0;
                        _injected.Enqueue(CalendarWithScope(packet, sample.JoinId + 1, world, sample.WorldMs + 2000));
                        _injected.Enqueue(CalendarWithScope(packet, sample.JoinId, world == 1 ? uint.MaxValue : world - 1, sample.WorldMs + 1000));
                        _injected.Enqueue(packet);
                        _injected.Enqueue(packet);
                        continue;
                    }
                }
                _remaining = ReplayPeer.Frame(packet.Type, packet.Payload);
            }
            int size = Math.Min(buffer.Length, _remaining.Length);
            _remaining[..size].CopyTo(buffer);
            _remaining = _remaining[size..];
            return size;
        }

        private static HostCalendarSample? FrozenClock(byte[] payload)
        {
            if (!Protocol.TrySplitJoinDown(payload, out _, out _, out _, out var body)
                || !LootMsg.TryDecode(body, out var clock) || clock.Kind != Protocol.W164Clock) return null;
            return HostCalendar.TryParse(clock.Text, Environment.TickCount64, 0, false, 0, out var sample)
                && sample.IsJoinSnapshot ? sample : null;
        }

        private static ReplayPacket CalendarWithScope(ReplayPacket original, uint joinId, uint world, long time)
        {
            byte source = original.Payload[0];
            byte target = original.Payload[1];
            var row = new LootMsg(Protocol.W164Clock, world, HostCalendar.Format(time, 0, 1, false, joinId));
            byte[] framed = row.BuildUp(Protocol.W164Up, target);
            BinaryPrimitives.WriteUInt32LittleEndian(framed.AsSpan(4), joinId);
            return new ReplayPacket(Protocol.W164Down, [source, .. framed.AsSpan(3)]);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            if (BeforeWrite is { } before) await before(buffer, ct);
            await wire.WriteAsync(buffer, ct);
            if (!buffer.IsEmpty && buffer.Span[0] == Protocol.JoinerReadyUp)
                ReadySent.Enqueue(ReadyUpId(buffer.Span));
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override Task FlushAsync(CancellationToken ct) => wire.FlushAsync(ct);
        public override void Flush() => wire.Flush();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
