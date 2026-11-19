// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Reflection;
using KcdMp.Wire;

namespace KcdMp.Client.Tests;

public sealed class RunTransferRetirementTests
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const byte Host = 2;
    private const uint Join = 119;

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Retired_finished_transfer_deletes_only_its_staging_and_does_not_clear_a_replacement(bool replacedLink, bool duringDone)
    {
        DirectoryInfo root = Directory.CreateTempSubdirectory("kct-run-transfer-retirement-");
        try
        {
            var bridge = new GameBridge(new ClientConfig());
            using var oldLink = new TransferStream();
            using var newLink = new MemoryStream();
            var oldLease = new Lease();
            var newLease = new Lease();
            object oldRequest = Request(oldLink, oldLease);
            object newRequest = Request(newLink, newLease);
            Set(bridge, "_wo122Stream", oldLink);
            Set(bridge, "_hostModeKnown", true);
            Set(bridge, "_hostModeFrom", Host);
            Set(bridge, "_joinOutId", Join);
            Set(bridge, "_joinCalendarRequest", oldRequest);
            var epochs = (Wo206Epochs)Read(bridge, "_w206")!;
            epochs.LearnWorld(7, 1);

            byte[] save = WhsSaveTests.SyntheticSave();
            var run = new WorldRunOffer(Guid.NewGuid(), 1, 7, 2);
            var sender = new WorldSender(save, Join, 1, 42, Convert.FromHexString(WhsSave.Verify(save).Md5), run: run);
            using var transfer = new WorldReceiver(root.FullName, Join, Host, sender.Offer) { HostWorldEpoch = 7 };
            Set(bridge, "_joinRx", transfer);
            string unrelated = Path.Combine(root.FullName, "world-unrelated.whs");
            File.WriteAllText(unrelated, "unrelated bytes");
            oldLink.RetireDuring = duringDone ? Protocol.WorldDoneUp : Protocol.WorldAckUp;
            oldLink.Retire = () =>
            {
                Assert.Equal(duringDone, File.Exists(transfer.FinalPath));
                epochs.LearnWorld(8, 2);
                if (!replacedLink) return;
                // The disconnect reader retires the old request before a new
                // authenticated link can reuse the same join id.
                Invoke(bridge, "RetireJoinCalendarAtRead", Host, oldLink, Join);
                Set(bridge, "_wo122Stream", newLink);
                Set(bridge, "_joinCalendarRequest", newRequest);
                Set(bridge, "_joinOutId", Join);
            };

            byte[] last = Assert.Single(sender.TakeSendable());
            await ((Task)Invoke(bridge, "OnWorldChunkInAsync", Host, Join, last[8..], CancellationToken.None)!).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(File.Exists(transfer.PartPath));
            Assert.False(File.Exists(transfer.FinalPath), "Retired request left a finished world in staging.");
            Assert.Equal("unrelated bytes", File.ReadAllText(unrelated));
            Assert.Null(Read(bridge, "_joinRx"));
            Assert.Null(Read(bridge, "_jj"));
            Assert.Equal(0u, Read(bridge, "_joinReceivedId"));
            Assert.Equal(1, oldLease.DisposeCount);
            Assert.Equal(0, newLease.DisposeCount);
            Assert.Empty(newLink.ToArray());
            byte[] expected = duringDone ? [.. transfer.BuildAck(), .. transfer.BuildDone()] : transfer.BuildAck();
            byte[] frames = oldLink.ToArray();
            Assert.Equal(expected, frames[..expected.Length]);
            if (replacedLink)
            {
                Assert.Same(newRequest, Read(bridge, "_joinCalendarRequest"));
                Assert.Equal(Join, Read(bridge, "_joinOutId"));
                Assert.Equal(expected.Length, frames.Length);
            }
            else
            {
                Assert.Null(Read(bridge, "_joinCalendarRequest"));
                Assert.Equal(0u, Read(bridge, "_joinOutId"));
                Assert.Equal(WorldReceiver.BuildAbort(Host, Join, Protocol.JoinAbortWorldChanged), frames[expected.Length..]);
            }
        }
        finally { root.Delete(recursive: true); }
    }

    private static object Request(Stream link, Lease lease)
    {
        object chosen = New("SelectedCharacter", null, null, lease, null);
        object character = New("JoinCharacterSelection", null, null, null, null, chosen);
        object request = New("JoinCalendarRequest", Join, Host, link);
        request.GetType().GetProperty("Character", Instance)!.SetValue(request, character);
        return request;
    }

    private static object New(string name, params object?[] args) =>
        Activator.CreateInstance(typeof(GameBridge).GetNestedType(name, BindingFlags.NonPublic)!, Instance, null, args, null)!;

    private static object? Invoke(GameBridge bridge, string name, params object?[] args) =>
        typeof(GameBridge).GetMethod(name, Instance)!.Invoke(bridge, args);

    private static object? Read(GameBridge bridge, string name) => typeof(GameBridge).GetField(name, Instance)!.GetValue(bridge);

    private static void Set(GameBridge bridge, string name, object value) => typeof(GameBridge).GetField(name, Instance)!.SetValue(bridge, value);

    private sealed class Lease : IDisposable
    {
        public int DisposeCount { get; private set; }
        public void Dispose() => DisposeCount++;
    }

    private sealed class TransferStream : MemoryStream
    {
        public byte RetireDuring { get; set; }
        public Action? Retire { get; set; }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var written = base.WriteAsync(buffer, cancellationToken);
            if (buffer.Span[0] == RetireDuring) Retire?.Invoke();
            return written;
        }
    }
}
