// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading.Channels;
using KcdMp.Wire;

namespace KcdMp.Client.Tests;

// Uses the real offer dispatcher and staging receiver. The temporary data root
// must not race another test's process-wide data-directory override.
[Collection("T3 connection lifecycle")]
public class T3RejoinReceiptTests
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private static void Set(GameBridge b, string key, object value) => typeof(GameBridge).GetField(key, Private)!.SetValue(b, value);
    private static T Get<T>(GameBridge b, string key) => (T)typeof(GameBridge).GetField(key, Private)!.GetValue(b)!;
    private static object? Call(GameBridge b, string method, params object?[] args) => typeof(GameBridge).GetMethod(method, Private)!.Invoke(b, args);
    private sealed class Game : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") });
    }
    private static GameBridge Bridge(HttpGameTransport transport)
    {
        var b = new GameBridge(new ClientConfig()); Set(b, "_transport", transport);
        Set(b, "_sharedWorld", true); Set(b, "_combatRoleApplied", true); Set(b, "_isDamageAuthority", false);
        Set(b, "_w189Mirror", false); // no unrelated delayed profile request after Ready
        return b;
    }
    private static byte[] DownOffer(uint joinId, WorldOffer offer)
    {
        byte[] up = Protocol.BuildJoinUp(Protocol.WorldOfferUp, 2, joinId, offer.Encode());
        return [0, .. up[3..]]; // host source followed by the unchanged join header/body
    }

    private sealed class Frames
    {
        private readonly GameBridge _bridge;
        private readonly Type _type = typeof(GameBridge).GetNestedType("InFrame", BindingFlags.NonPublic)!;
        private readonly object _reader, _writer;
        public Frames(GameBridge bridge)
        {
            _bridge = bridge;
            var create = typeof(Channel).GetMethods().Single(m => m.Name == nameof(Channel.CreateUnbounded) && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
            object channel = create.MakeGenericMethod(_type).Invoke(null, null)!;
            _reader = channel.GetType().GetProperty("Reader")!.GetValue(channel)!;
            _writer = channel.GetType().GetProperty("Writer")!.GetValue(channel)!;
            Set(bridge, "_frameWriter", _writer);
        }
        public void Offer(byte[] payload, uint readerWorld)
        {
            object frame = Activator.CreateInstance(_type, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, [(int)Protocol.WorldOfferDown, payload, Stopwatch.GetTimestamp(), readerWorld], null)!;
            Assert.True((bool)Call(_bridge, "Wo207Queue", _writer, frame, (int)Protocol.WorldOfferDown)!);
        }
        public async Task Drain()
        {
            _writer.GetType().GetMethod("TryComplete")!.Invoke(_writer, [null]);
            await ((Task)Call(_bridge, "ProcessFramesAsync", _reader, CancellationToken.None)!).WaitAsync(TimeSpan.FromSeconds(4));
        }
    }

    [Theory]
    [InlineData(7u)]
    [InlineData(0u)]
    public async Task A_queued_real_offer_preserves_its_reader_epoch_when_the_current_world_has_already_advanced(uint readerWorld)
    {
        string? before = Environment.GetEnvironmentVariable("KCDMP_DATA_DIR");
        string root = Path.Combine(Path.GetTempPath(), "kcdmp-t3-receipt-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("KCDMP_DATA_DIR", root);
        WorldReceiver? receiver = null;
        try
        {
            await using var transport = new HttpGameTransport("http://localhost:1403", 5000, new Game());
            var b = Bridge(transport); const uint joinId = 0x12345678; Set(b, "_joinOutId", joinId);
            var epochs = Get<Wo206Epochs>(b, "_w206"); epochs.LearnWorld(readerWorld, 1);
            var offer = new WorldOffer(4, 4, 1, SHA256.HashData(new byte[] { 1, 2, 3, 4 }), 91, new byte[16]);
            var frames = new Frames(b); frames.Offer(DownOffer(joinId, offer), readerWorld);
            // This is the actual main-queue race: the reader already knows8
            // before the older7 offer is dispatched by ProcessFramesAsync.
            epochs.LearnWorld(8, 2);
            await frames.Drain();
            receiver = Get<WorldReceiver>(b, "_joinRx");
            Assert.NotNull(receiver); Assert.Equal(readerWorld, receiver.HostWorldEpoch);
            Assert.Equal(8u, epochs.World); Assert.Equal(joinId, receiver.JoinId); Assert.Equal(91u, receiver.Offer.WorldSavedSeq);
            Assert.True(File.Exists(receiver.PartPath)); Assert.StartsWith(root, receiver.PartPath);
            // A chunk arriving under yet another current epoch cannot refresh
            // the immutable provenance of the file this receiver is assembling.
            epochs.LearnWorld(9, 3);
            Assert.Equal(WorldReceiver.ChunkResult.Complete, receiver.Accept(0, [1, 2, 3, 4], out _));
            Assert.Equal(readerWorld, receiver.HostWorldEpoch);
        }
        finally
        {
            receiver?.Dispose();
            Environment.SetEnvironmentVariable("KCDMP_DATA_DIR", before);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(7u, 8u, false, false, true)]
    [InlineData(7u, 7u, false, false, false)]
    [InlineData(7u, 1u, false, false, false)]
    [InlineData(7u, 0u, false, false, false)]
    [InlineData(0u, 8u, false, false, false)]
    [InlineData(7u, 8u, true, false, false)]
    [InlineData(7u, 8u, false, true, false)]
    public async Task Ready_keeps_the_loaded_receipt_and_cleanup_arms_only_a_proven_newer_world_already_heard_before_ready(
        uint loadedWorld, uint currentWorld, bool leaving, bool newWorldLeavePending, bool rejoin)
    {
        await using var transport = new HttpGameTransport("http://localhost:1403", 5000, new Game());
        var b = Bridge(transport); Set(b, "_joinedWorld", true);
        var epochs = Get<Wo206Epochs>(b, "_w206"); epochs.LearnWorld(currentWorld, 99);
        // No Changed-Epoch event will occur after Ready. The completion helper
        // must compare the file's receipt with the world that was heard earlier.
        Call(b, "Wo125AfterReady", "0123456789", "bring", new WhsSave.HenryParts(), null, 91u, loadedWorld);
        Assert.Equal(loadedWorld, Get<uint>(b, "_t3JoinedHostWorld"));
        Assert.Equal(currentWorld, epochs.World); Assert.False(Get<bool>(b, "_rejoinPending"));
        Set(b, "_leaveInProgress", leaving); Set(b, "_newWorldLeavePending", newWorldLeavePending);
        Call(b, "T3AfterJoinedWorldCleanup");
        Assert.Equal(rejoin, Get<bool>(b, "_rejoinPending"));
        if (rejoin)
        {
            Assert.Equal("host-world-changed-during-join", Get<string>(b, "_t3RejoinReason"));
            Assert.True((bool)Call(b, "T3AdmitInWorldRejoin")!);
            Assert.Equal(1, Get<ConcurrentDictionary<string, long>>(b, "_t3Counters")["auto_rejoins_in_world"]);
        }
        else Assert.Equal(0, Get<ConcurrentDictionary<string, long>>(b, "_t3Counters").GetValueOrDefault("auto_rejoins_in_world"));
        Assert.Equal(loadedWorld, Get<uint>(b, "_t3JoinedHostWorld")); // arming never relabels an old loaded save
    }
}
