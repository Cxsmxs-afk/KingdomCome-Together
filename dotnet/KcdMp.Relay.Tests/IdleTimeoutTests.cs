// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Buffers.Binary;
using System.Threading.Channels;
using KcdMp.Server.Features.ClientHandling;
using KcdMp.Wire;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KcdMp.Relay.Tests;

// A silent loss has no FIN or RST. Exercise the relay's real TCP read, timeout,
// disconnect broadcast, and authority decision with independent idle deadlines.
public class IdleTimeoutTests
{
    private static readonly TimeSpan Idle = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Silent_host_is_dropped_and_the_survivor_stays_without_damage_authority()
    {
        var clock = new IdleClock();
        await using var app = RelayListeners.CreateApp(clock, "--Tcp:IdleTimeoutMs", "300");
        int tcpPort = (await RelayListeners.StartAsync(app)).Port;
        var clients = app.Services.GetRequiredService<ClientHandler>();

        await using (var a = await Peer.ConnectAsync(tcpPort, "alpha"))
        {
            var hostIdle = await clock.NextTimerAsync();
            await hostIdle.WaitForArmAsync();
            await using var b = await Peer.ConnectAsync(tcpPort, "bravo");
            var guestIdle = await clock.NextTimerAsync();
            await guestIdle.WaitForArmAsync();

            var roleA = await a.ReadUntilAsync(Protocol.CombatRole, Watchdog);
            var roleB = await b.ReadUntilAsync(Protocol.CombatRole, Watchdog);
            Assert.Equal(1, roleA[0]);
            Assert.Equal(0, roleB[0]);

            byte[] claim = Frame(Protocol.Position, new byte[Protocol.PositionPayloadLen]);
            claim[3 + 16] = Protocol.PositionFlagHostClaim;
            await a.SendRawAsync(claim);
            await hostIdle.WaitForArmAsync();
            string epoch = $"v1|142|2|resume|r1|{Guid.NewGuid():N}|81";
            await a.SendRawAsync(new LootMsg(Protocol.W203Epoch, 0, epoch).BuildUp(Protocol.W203Up, b.Id));
            Assert.Equal(a.Id, (await b.ReadUntilAsync(Protocol.W203Down, Watchdog))[0]);
            await hostIdle.WaitForArmAsync();
            Assert.Equal("alpha", clients.DamageAuthority?.Name);

            // Alpha stays silent. Advance only after the relay has consumed the
            // guest's heartbeat and armed a new idle deadline on that connection.
            clock.Advance(200);
            await PingAsync(b, 1);
            await guestIdle.WaitForArmAsync();
            clock.Advance(99);
            Assert.Equal(2, clients.ReadyClientCount);
            clock.Advance(1);

            // Read the actual disconnect before the resulting role notice, so a
            // previously queued "not the host" frame cannot satisfy the test.
            var disconnected = await b.ReadUntilAsync(Protocol.Disconnect, Watchdog);
            Assert.Equal(a.Id, disconnected[0]);
            var newRole = await b.ReadUntilAsync(Protocol.CombatRole, Watchdog);
            Assert.Equal(0, newRole[0]);
            await PingAsync(b, 2);
            Assert.Equal(1, clients.ReadyClientCount);
            Assert.Null(clients.DamageAuthority);
            Assert.Equal(142u, clients.AbsentHost?.WorldEpoch);
        }

        await app.StopAsync();
    }

    private static async Task PingAsync(Peer peer, long stamp)
    {
        var body = new byte[Protocol.PingPayloadLen];
        BinaryPrimitives.WriteInt64LittleEndian(body, stamp);
        await peer.SendRawAsync(Frame(Protocol.Ping, body));
        Assert.Equal(body, await peer.ReadUntilAsync(Protocol.Pong, Watchdog));
    }

    private static byte[] Frame(byte type, byte[] body)
    {
        var packet = new byte[3 + body.Length];
        packet[0] = type;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(1), (ushort)body.Length);
        body.CopyTo(packet, 3);
        return packet;
    }

    private sealed class IdleClock : TimeProvider
    {
        private readonly List<Deadline> _timers = [];
        private readonly Channel<Deadline> _created = Channel.CreateUnbounded<Deadline>();
        private TimeSpan _now;

        public async Task<Deadline> NextTimerAsync()
        {
            using var deadline = new CancellationTokenSource(Watchdog);
            return await _created.Reader.ReadAsync(deadline.Token);
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(Timeout.InfiniteTimeSpan, dueTime);
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            var timer = new Deadline(this, callback, state);
            lock (_timers) _timers.Add(timer);
            Assert.True(_created.Writer.TryWrite(timer));
            return timer;
        }

        public void Advance(int milliseconds)
        {
            lock (_timers)
            {
                _now += TimeSpan.FromMilliseconds(milliseconds);
                foreach (var timer in _timers) timer.FireIfDue();
            }
        }

        public sealed class Deadline(IdleClock clock, TimerCallback callback, object? state) : ITimer
        {
            private readonly Channel<TimeSpan> _arms = Channel.CreateUnbounded<TimeSpan>();
            private TimeSpan? _due;
            private bool _disposed;

            public async Task WaitForArmAsync()
            {
                using var deadline = new CancellationTokenSource(Watchdog);
                Assert.Equal(Idle, await _arms.Reader.ReadAsync(deadline.Token));
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Assert.Equal(Timeout.InfiniteTimeSpan, period);
                lock (clock._timers)
                {
                    if (_disposed) return false;
                    _due = clock._now + dueTime;
                    Assert.True(_arms.Writer.TryWrite(dueTime));
                    return true;
                }
            }

            // Called with the clock locked so disposal cannot race the callback.
            public void FireIfDue()
            {
                if (_due is not TimeSpan due || due > clock._now) return;
                _due = null;
                callback(state);
            }

            public void Dispose()
            {
                lock (clock._timers)
                {
                    _disposed = true;
                    _due = null;
                }
            }

            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
