// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Reflection;
using KcdMp.Wire;

namespace KcdMp.Client.Tests;

public sealed class Lane2KnockoutTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refused_knockout_does_not_issue_a_recovery(bool changedBody)
    {
        await using var fixture = await PresentationPipe.CreateAsync();
        var down = fixture.Present(down: true);
        await fixture.ReplyToKnockoutAsync(expectedDown: true, [0, 0, 3]);
        await down;
        if (changedBody) fixture.Rebind();
        await fixture.AssertNoWriteAsync(fixture.Present(down: false));
    }

    [Fact]
    public async Task A_death_vitals_edge_does_not_wake_the_knocked_out_body()
    {
        await using var fixture = await PresentationPipe.CreateAsync();
        var down = fixture.Present(down: true);
        await fixture.ReplyToKnockoutAsync(expectedDown: true, [1, 0, 0]);
        await down;

        fixture.NoteDeathVitals();
        await fixture.AssertNoWriteAsync(fixture.Present(down: false));
    }

    [Fact]
    public async Task Native_unavailable_does_not_switch_to_legacy_guard_commands()
    {
        await using var fixture = await PresentationPipe.CreateAsync();
        var down = fixture.Present(down: true);
        await fixture.ReplyToKnockoutAsync(expectedDown: true, [0, 0, 1]);
        await fixture.AssertNoWriteAsync(down);
        await fixture.AssertNoWriteAsync(fixture.Present(down: false));
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    public async Task Accepted_or_ambiguous_knockout_keeps_one_typed_recovery_path(bool host, bool ambiguous, bool unrelatedJoin)
    {
        await using var fixture = await PresentationPipe.CreateAsync(host);
        var down = fixture.Present(down: true);
        await fixture.ReplyToKnockoutAsync(expectedDown: true, ambiguous ? null : [1, 0, 0],
            beforeReply: unrelatedJoin ? fixture.StartOtherPeerJoin : null);
        await down;

        if (unrelatedJoin) fixture.StartOtherPeerJoin();
        var wake = fixture.Present(down: false);
        await fixture.ReplyToKnockoutAsync(expectedDown: false, [1, 0, 0]);
        await wake;
        await fixture.AssertNoWriteAsync(fixture.Present(down: false, retryWake: true));
    }

    private sealed class PresentationPipe(IAsyncDisposable connection, object harness, GameBridge bridge, object stamp,
        NamedPipeServerStream server, CancellationToken token) : IAsyncDisposable
    {
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private const byte Peer = 2;

        public static async Task<PresentationPipe> CreateAsync(bool host = true)
        {
            // Exercise the same scoped GameBridge and edge pipe used by the production-row tests.
            var connectionType = typeof(EdgePipeTests).GetNestedType("Connection", BindingFlags.NonPublic)!;
            var harnessType = typeof(EdgePipeTests).GetNestedType("PresentationCombatHarness", BindingFlags.NonPublic)!;
            var connection = (IAsyncDisposable)Activator.CreateInstance(connectionType)!;
            try
            {
                await (Task)connectionType.GetMethod("Connect")!.Invoke(connection, [true])!;
                var create = (Task)harnessType.GetMethod("CreateAsync")!.Invoke(null, [connection])!;
                await create;
                var harness = create.GetType().GetProperty("Result")!.GetValue(create)!;
                var bridge = (GameBridge)harnessType.GetFields(Instance).Single(f => f.FieldType == typeof(GameBridge)).GetValue(harness)!;
                var stamp = harnessType.GetFields(Instance).Single(f => f.FieldType == typeof(object)).GetValue(harness)!;
                var server = (NamedPipeServerStream)connectionType.GetField("Server")!.GetValue(connection)!;
                var token = (CancellationToken)connectionType.GetProperty("Token")!.GetValue(connection)!;
                void SetField(string name, object value) => typeof(GameBridge).GetField(name, Instance)!.SetValue(bridge, value);
                SetField("_wo121Ct", token);
                if (!host)
                {
                    var epochs = (Wo206Epochs)typeof(GameBridge).GetField("_w206", Instance)!.GetValue(bridge)!;
                    SetField("_isDamageAuthority", false);
                    SetField("_hostAuthority", true);
                    SetField("_joinedWorld", true);
                    SetField("_t3JoinedHostWorld", epochs.World);
                    SetField("_hostModeKnown", true);
                    SetField("_hostSharedWorld", true);
                    SetField("_hostModeFrom", Peer);
                }
                return new(connection, harness, bridge, stamp, server, token);
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }

        public Task Present(bool down, bool retryWake = false) => (Task)typeof(GameBridge)
            .GetMethod("PresentKnockoutAsync", Instance)!.Invoke(bridge, [Peer, down, stamp, retryWake])!;

        public void Rebind() => harness.GetType().GetMethod("Rebind")!.Invoke(harness, null);

        public void StartOtherPeerJoin() => ((Wo206Epochs)typeof(GameBridge).GetField("_w206", Instance)!
            .GetValue(bridge)!).StartJoin(3);

        public void NoteDeathVitals() => ((ConcurrentDictionary<byte, byte>)typeof(GameBridge)
            .GetField("_w132PeerFlags", Instance)!.GetValue(bridge)!)[Peer] = Protocol.PlayerStateFlagUnconscious;

        public async Task ReplyToKnockoutAsync(bool expectedDown, byte[]? receipt, Action? beforeReply = null)
        {
            var request = await (Task<byte[]>)connection.GetType().GetMethod("Read")!.Invoke(connection, [(byte)0x38])!;
            Assert.Equal(0x40, request[5]);
            Assert.Equal(new byte[] { 0x81, 1, expectedDown ? (byte)1 : (byte)0 }, request[25..]);
            // Native EdgeFaulted (5) means that a write could already have reached an engine owner.
            var status = receipt is null ? (EdgeOutcome)5 : EdgeOutcome.Completed;
            beforeReply?.Invoke();
            await (Task)connection.GetType().GetMethod("Respond")!.Invoke(connection,
                [BinaryPrimitives.ReadUInt32LittleEndian(request), status, receipt is null ? (byte)0 : (byte)0x81, receipt])!;
        }

        public async Task AssertNoWriteAsync(Task operation)
        {
            using var watch = CancellationTokenSource.CreateLinkedTokenSource(token);
            var pending = server.ReadAsync(new byte[1], watch.Token).AsTask();
            try
            {
                Assert.Same(operation, await Task.WhenAny(operation, pending).WaitAsync(token));
                await operation;
                Assert.False(pending.IsCompletedSuccessfully);
            }
            finally { await watch.CancelAsync(); }
        }

        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }
}
