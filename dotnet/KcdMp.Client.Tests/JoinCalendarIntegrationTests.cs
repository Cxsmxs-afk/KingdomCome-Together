// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
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
        RunAsync("A", "B", dropFirstSnapshot: true, delaySnapshot: true);

    private static async Task RunAsync(string hostName, string guestName, bool dropFirstSnapshot, bool delaySnapshot = false)
    {
        using var fixture = new SaveFixture();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var ct = stop.Token;
        await using var relay = await ReplayRelay.StartAsync(ct);
        await using var hostPeer = await ReplayPeer.ConnectAsync(relay.Port, hostName, ct);
        await using var guestPeer = await ReplayPeer.ConnectAsync(relay.Port, guestName, ct);
        Assert.NotEqual(hostPeer.Id, guestPeer.Id);
        byte[] hello = Protocol.BuildLinkHelloUp(Protocol.LinkCapWorldSync);
        await hostPeer.SendAsync(hello, ct);
        await guestPeer.SendAsync(hello, ct);

        var hostLink = new JoinWire(hostPeer.Wire, await hostPeer.DrainAsync(ct), dropFirstSnapshot: false);
        var guestLink = new JoinWire(guestPeer.Wire, await guestPeer.DrainAsync(ct), dropFirstSnapshot)
        {
            WithholdFrozenSnapshots = delaySnapshot
        };
        await using var hostPipe = new JoinCalendarTestPipe();
        await using var guestPipe = new JoinCalendarTestPipe();
        await using var hostGame = new JoinCalendarTestEngine(Path.Combine(fixture.Root, "host.log"), fixture.HostSaves, fixture.HostSave, hostPipe.ObserveGameplayStarted);
        await using var guestGame = new JoinCalendarTestEngine(Path.Combine(fixture.Root, "guest.log"), fixture.GuestSaves, null, guestPipe.ObserveGameplayStarted);
        var host = CreateBridge(hostPeer.Id, true, hostLink, hostPipe.Pipe, hostGame.Transport, fixture.HostSaves, ct);
        var guest = CreateBridge(guestPeer.Id, false, guestLink, guestPipe.Pipe, guestGame.Transport, fixture.GuestSaves, ct);
        hostGame.SaveWritten = path => Call(host, "OnSaveFileEvent", path);
        guestGame.Emit("wo124_where", "menu");
        hostGame.Emit("wo124_where", "world");
        Call(host, "Wo125HostIdentify", fixture.HostLoadedSave, true, "fixture starting world");

        Task hostRead = InvokeAsync(host, "ReceiveLoopAsync", hostLink, ct);
        Task guestRead = InvokeAsync(guest, "ReceiveLoopAsync", guestLink, ct);
        try
        {
            await WaitAsync(() => Get<bool>(host, "_combatRoleApplyComplete") && Get<bool>(guest, "_combatRoleApplyComplete"),
                "both relay-assigned combat roles applied", ct);
            Assert.True(Get<bool>(host, "_isDamageAuthority"));
            Assert.False(Get<bool>(guest, "_isDamageAuthority"));
            await WaitAsync(() => (Get<ushort>(guest, "_w213Caps") & Protocol.LinkCapWorldSync) != 0
                    && ((List<byte>)Call(host, "LivePartners")!).Contains(guestPeer.Id),
                "the relay's world-sync capability and live guest", ct);
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

            var beforeOffer = new TaskCompletionSource<(long ClockIn, HostCalendarSample? Bootstrap)>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            guestLink.BeforeFirstOffer = () => beforeOffer.TrySetResult(
                (Get<long>(guest, "_w200ClockIn"), Get<HostCalendarSample?>(guest, "_hostCalendarBootstrap")));
            await InvokeAsync(guest, "SendJoinRequestAsync");
            uint joinId = Get<uint>(guest, "_joinOutId");
            Assert.NotEqual(0u, joinId);
            if (delaySnapshot) await HoldPastOldCalendarDeadlineAsync(host, guest, hostGame, guestGame, hostPipe, guestLink, ct);

            string resumeReason = await hostGame.Resumed.WaitAsync(ct);
            var (clockAtOffer, bootstrapAtOffer) = await beforeOffer.Task.WaitAsync(ct);
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
            Assert.True(Get<long>(guest, "_w200ClockIn") >= 1);
            if (dropFirstSnapshot)
            {
                Assert.True(guestLink.DroppedFirstSnapshot);
                Assert.True(guestLink.FrozenSnapshots.Count >= 2);
                Assert.All(guestLink.FrozenSnapshots, snapshot => Assert.Equal(guestPipe.ArmedWorldMs, snapshot.WorldMs));
            }
            else
            {
                Assert.True(clockAtOffer >= 1);
                Assert.True(bootstrapAtOffer?.ReadyForJoin(Environment.TickCount64, joinId));
            }

            AssertReady(await hostLink.Ready.WaitAsync(ct), guestPeer.Id, joinId);
            await WaitAsync(() => Get<object?>(host, "_hostJoin") is null && Get<string>(guest, "_joinUiState") == "in",
                "the host finished the join and the guest entered the world", ct);
            Assert.True(Get<bool>(guest, "_joinedWorld"));
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
            File.WriteAllBytes(Path.Combine(GuestSaves, "playline0", "save001.whs"), WhsSaveTests.File(guest));
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
        private readonly TaskCompletionSource<byte[]> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private ReadOnlyMemory<byte> _remaining;
        private int _dropped;

        public readonly ConcurrentQueue<HostCalendarSample> FrozenSnapshots = new();
        public bool DroppedFirstSnapshot => Volatile.Read(ref _dropped) != 0;
        public volatile bool WithholdFrozenSnapshots;
        public Task<byte[]> Ready => _ready.Task;
        public Action? BeforeFirstOffer { get; set; }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (buffer.IsEmpty) return 0;
            while (_remaining.IsEmpty)
            {
                if (!_initial.TryDequeue(out var packet))
                {
                    byte[] header = new byte[3];
                    int read = await wire.ReadAsync(header.AsMemory(0, 1), ct);
                    if (read == 0) return 0;
                    await wire.ReadExactlyAsync(header.AsMemory(1), ct);
                    byte[] body = new byte[BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(1))];
                    await wire.ReadExactlyAsync(body, ct);
                    packet = new ReplayPacket(header[0], body);
                }
                if (packet.Type == Protocol.JoinerReadyDown) _ready.TrySetResult(packet.Payload);
                if (packet.Type == Protocol.WorldOfferDown)
                {
                    BeforeFirstOffer?.Invoke();
                    BeforeFirstOffer = null;
                }
                if (packet.Type == Protocol.W164Down && FrozenClock(packet.Payload) is { } sample)
                {
                    FrozenSnapshots.Enqueue(sample);
                    bool firstDrop = dropFirstSnapshot && Interlocked.Exchange(ref _dropped, 1) == 0;
                    if (firstDrop || WithholdFrozenSnapshots) continue;
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

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => wire.WriteAsync(buffer, ct);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => wire.WriteAsync(buffer, offset, count, ct);
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
