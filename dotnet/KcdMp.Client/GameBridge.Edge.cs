// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace KcdMp.Client;

public partial class GameBridge
{
    private enum EdgeMode { Auto, Inproc, Http }
    private const byte EdgeGenericRequest = 0x2B, EdgeGenericReply = 0xAC, EdgeConsoleOp = 254;
    private const byte EdgeLanesRequest = 0x3A, EdgeLanesReply = 0xBA;
    private const int EdgeConsoleMaxBytes = 1022;
    private sealed record EdgeConsoleProbe(long Connection, string Nonce, TaskCompletionSource<bool> Echo);

    private volatile EdgeMode _edgeMode;
    private EdgeConsoleProbe? _edgeConsoleProbe;
    private long _edgeLuaRevision = -1;
    private long _edgeInprocFallbacks, _edgeHttpCallsRt, _edgeFramesAdmitted;
    private int _edgeProcAwaitsPerFrameMax = 0;

    private bool EdgeLuaReady => _combat.EdgeActive && Interlocked.Read(ref _edgeLuaRevision) == _combat.EdgeConnectionRevision;

    private void EdgeOnGameEvent(string name, string arg)
    {
        if (name == "edge_probe")
        {
            var probe = Volatile.Read(ref _edgeConsoleProbe);
            if (probe is not null && probe.Connection == _combat.EdgeConnectionRevision &&
                arg.Equals(probe.Nonce, StringComparison.OrdinalIgnoreCase))
                probe.Echo.TrySetResult(true);
            return;
        }
        if (name == "edge_lanes")
        {
            var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (Environment.GetEnvironmentVariable("KCDMP_TEST_LANES") == "1" && parts.Length == 2 &&
                uint.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var nonce) && nonce > 0 &&
                uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var frames) && frames is >= 1 and <= 600)
                _ = EdgeStartLanesAsync(nonce, frames);
            return;
        }
        var mode = arg switch { "auto" => EdgeMode.Auto, "inproc" => EdgeMode.Inproc, "http" => EdgeMode.Http, _ => (EdgeMode?)null };
        if (mode is null) return;
        _edgeMode = mode.Value;
        _combat.EdgeTransportOn = mode != EdgeMode.Http;
        if (mode == EdgeMode.Http) Interlocked.Exchange(ref _edgeLuaRevision, -1);
        else _ = EdgeProbeConsoleAsync();
        Console.WriteLine($"MP-EDGE mode={arg}");
    }

    private async Task EdgeProbeConsoleAsync(CancellationToken ct = default)
    {
        if (_edgeMode == EdgeMode.Http || EdgeLuaReady || Volatile.Read(ref _edgeConsoleProbe) is not null) return;
        if (!_combat.EdgeActive && !await _combat.ProbeEdgeAsync(ct)) return;
        if (_edgeMode == EdgeMode.Http || EdgeLuaReady || !_combat.EdgeActive) return;
        var nativeNonce = RandomNumberGenerator.GetBytes(8);
        long connection = _combat.EdgeConnectionRevision;
        var probe = new EdgeConsoleProbe(connection, Convert.ToHexString(nativeNonce), new(TaskCreationOptions.RunContinuationsAsynchronously));
        if (Interlocked.CompareExchange(ref _edgeConsoleProbe, probe, null) is not null) return;
        try
        {
            var message = new byte[10];
            message[0] = EdgeConsoleOp;
            nativeNonce.CopyTo(message, 2);
            var result = await _combat.EdgeRequestAsync(EdgeGenericRequest, message, EdgeGenericReply, EdgeLane.Bulk, ct,
                connectionRevision: connection);
            if (EdgeConsoleResult(result) != 1) return;
            await probe.Echo.Task.WaitAsync(TimeSpan.FromSeconds(2), ct);
            if (_edgeMode != EdgeMode.Http && _combat.EdgeConnectionRevision == connection)
            {
                Interlocked.Exchange(ref _edgeLuaRevision, connection);
                Console.WriteLine("MP-EDGE inproc_selftest=pass");
            }
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { }
        finally { Interlocked.CompareExchange(ref _edgeConsoleProbe, null, probe); }
    }

    private static byte EdgeConsoleResult(EdgePipeReply reply) =>
        reply.Outcome == EdgeOutcome.Completed && reply.Payload is [1, 0, EdgeConsoleOp, 0, var code] ? code : (byte)0;

    private async Task EdgeStartLanesAsync(uint nonce, uint frames)
    {
        if (_edgeMode == EdgeMode.Http || !_combat.EdgeActive) return;
        var message = new byte[9];
        message[0] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(1), nonce);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(5), frames);
        bool started = false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            EdgePipeReply reply;
            do
            {
                reply = await _combat.EdgeRequestAsync(EdgeLanesRequest, message, EdgeLanesReply, EdgeLane.Bulk, timeout.Token);
                if (reply.Outcome != EdgeOutcome.Busy) break;
                await Task.Delay(16, timeout.Token);
            } while (_edgeMode != EdgeMode.Http);
            started = reply.Outcome == EdgeOutcome.Completed && reply.Payload is [1, 0, ..] && reply.Payload.Length == 10 &&
                BinaryPrimitives.ReadUInt32LittleEndian(reply.Payload.AsSpan(2)) == nonce &&
                BinaryPrimitives.ReadUInt32LittleEndian(reply.Payload.AsSpan(6)) == frames;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
        Console.WriteLine(FormattableString.Invariant($"MP-LANES agent nonce={nonce} frames={frames} started={(started ? 1 : 0)}"));
    }

    private Task EdgeExecLuaAsync(string lua, EdgeLane lane)
    {
        var recording = Volatile.Read(ref _recordingTransport);
        return recording is null ? ExecuteEdgeLuaAsync(lua, lane) :
            recording.ObserveExecuteAsync(lua, () => ExecuteEdgeLuaAsync(lua, lane));
    }

    private async Task ExecuteEdgeLuaAsync(string lua, EdgeLane lane)
    {
        using var scope = ProcessorFrameScope.Link(default);
        var ct = scope.Token;
        // Position and action data have their own native ingress. Lua calls
        // retain ordered game/setup work such as spawning and policy updates.
        if (_edgeMode != EdgeMode.Http)
        {
            int size = Encoding.UTF8.GetByteCount(lua);
            if (EdgeLuaReady && size is > 0 and <= EdgeConsoleMaxBytes)
            {
                long connection = Interlocked.Read(ref _edgeLuaRevision);
                var body = new byte[2 + size];
                body[0] = EdgeConsoleOp;
                body[1] = 1;
                Encoding.UTF8.GetBytes(lua, body.AsSpan(2));
                EdgePipeReply result;
                do
                {
                    result = await _combat.EdgeRequestAsync(EdgeGenericRequest, body, EdgeGenericReply, lane, ct,
                        connectionRevision: connection);
                    if (result.Outcome != EdgeOutcome.Busy || _edgeMode != EdgeMode.Inproc) break;
                    await Task.Delay(8, ct);
                } while (_combat.EdgeConnectionRevision == connection);
                byte code = EdgeConsoleResult(result);
                if (code == 1) return;
                if (result.Outcome == EdgeOutcome.Completed)
                    Interlocked.CompareExchange(ref _edgeLuaRevision, -1, connection);
                if (result.Outcome == EdgeOutcome.Ambiguous || code == 2 ||
                    (result.Outcome == EdgeOutcome.Completed && result.Payload is not [1, 0, EdgeConsoleOp, 0, 0]))
                    throw new InvalidOperationException("The native Lua request has no confirmed outcome.");
            }
            if (_edgeMode == EdgeMode.Inproc) throw new InvalidOperationException("The native Lua request was not admitted.");
            if (_combat.EdgeActive) Interlocked.Increment(ref _edgeInprocFallbacks);
        }
        if (lane == EdgeLane.Realtime) Interlocked.Increment(ref _edgeHttpCallsRt);
        await _transport.ExecuteAsync(lua, ct);
    }

    private bool EdgeForwardPose(int type, byte[] payload, long arrival) =>
        _nativeWriteOn && _edgeMode != EdgeMode.Http && _combat.PostEdgePose((byte)type, payload, arrival);

    // The frame processor owns dispatch only. An ordered worker retains
    // legacy game operations and their world/peer lifecycle checks.
    private static int EdgeDispatchAvailable<T>(ChannelReader<T> source, ChannelWriter<T> worker)
    {
        int count = 0;
        while (source.TryRead(out var frame))
        {
            if (!worker.TryWrite(frame)) throw new InvalidOperationException("The frame worker stopped accepting work.");
            count++;
        }
        return count;
    }

    private async Task EdgeDispatchFramesAsync(ChannelReader<InFrame> source, CancellationToken ct)
    {
        var work = Channel.CreateUnbounded<InFrame>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var worker = ExecuteGameFramesAsync(work.Reader, ct);
        try
        {
            while (await source.WaitToReadAsync(ct))
                Interlocked.Add(ref _edgeFramesAdmitted, EdgeDispatchAvailable(source, work.Writer));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            work.Writer.TryComplete();
            try { await worker; }
            finally
            {
                EdgeDrainAbandonedFrames(work.Reader);
                EdgeDrainAbandonedFrames(source);
            }
        }
    }

    private void EdgeDrainAbandonedFrames(ChannelReader<InFrame> queue)
    {
        while (queue.TryRead(out var waiting))
            if (Wo202Resolve(waiting) is { } remaining && remaining.Type != FlushTickType)
                Wo207Trace.Dequeued(remaining.Type);
    }
}
