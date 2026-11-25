// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Reflection;
using KcdMp.Wire;
using Xunit.Abstractions;

namespace KcdMp.Client.Tests;

[Collection("Friendly-fire receipts")]
public sealed class FriendlyFireTransportTimingTests(ITestOutputHelper output)
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Only_a_successful_transport_write_has_a_completion_sample(bool scheduled, bool succeeds)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var ct = deadline.Token;
        using var wire = new WriteBarrier(succeeds);
        using var link = new RelayLink(wire, "direct", wire, () => true);
        var bridge = new GameBridge(new ClientConfig());
        var writePacket = typeof(GameBridge).GetMethod("WritePacketAsync", Instance)!;
        var start = typeof(GameBridge).GetMethod("Wo213StartWriter", Instance)!;
        var stop = typeof(GameBridge).GetMethod("Wo213StopWriterAsync", Instance)!;
        Task Send(byte[] packet) => (Task)writePacket.Invoke(bridge, [wire, packet, ct])!;

        var before = Console.Out;
        using var logs = new StringWriter();
        Console.SetOut(logs);
        try
        {
            if (scheduled) start.Invoke(bridge, [link, wire]);
            var sending = Send(new PlayerHitV8(2, 3, 5, 0, 0).BuildUp(1, 7, 9));
            await wire.Started.Task.WaitAsync(ct);
            output.WriteLine($"pending={Samples().Length} scheduled={scheduled} succeeds={succeeds}");
            foreach (string sample in Samples()) output.WriteLine(sample);
            Assert.Empty(Samples());

            wire.Release.TrySetResult();
            if (scheduled)
            {
                await sending.WaitAsync(ct);
                if (succeeds)
                {
                    await Send(Protocol.BuildLinkHelloUp(Protocol.LinkCapPvpContact)).WaitAsync(ct);
                    await wire.TrailingPacket.Task.WaitAsync(ct);
                }
                else await wire.Closed.Task.WaitAsync(ct);
            }
            else if (succeeds) await sending.WaitAsync(ct);
            else await Assert.ThrowsAsync<IOException>(() => sending.WaitAsync(ct));

            string[] samples = Samples();
            foreach (string sample in samples) output.WriteLine(sample);
            Assert.Equal(succeeds ? 2 : 1, samples.Length);
            Assert.Contains("stage=client-write-start ", samples[0], StringComparison.Ordinal);
            Assert.All(samples, row => Assert.Contains("contact_seq=1 recipient_life=9 ", row, StringComparison.Ordinal));
            Assert.Equal(succeeds ? 1 : 0, samples.Count(row => row.Contains("stage=client-write-complete ", StringComparison.Ordinal)));
        }
        finally
        {
            wire.Release.TrySetResult();
            try { if (scheduled) await (Task)stop.Invoke(bridge, null)!; }
            finally { Console.SetOut(before); }
        }

        string[] Samples() => logs.ToString().Split('\n').Where(line => line.StartsWith("MP-LANE2-FF-TIME ", StringComparison.Ordinal)).ToArray();
    }

    private sealed class WriteBarrier(bool succeeds) : MemoryStream
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource TrailingPacket { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> packet, CancellationToken ct = default)
        {
            if (packet.Span[0] == Protocol.PlayerHitV8Up)
            {
                Started.SetResult();
                await Release.Task.WaitAsync(ct);
                if (!succeeds) throw new IOException("controlled write failure");
            }
            else if (Started.Task.IsCompleted) TrailingPacket.TrySetResult();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Closed.TrySetResult();
            base.Dispose(disposing);
        }
    }
}
