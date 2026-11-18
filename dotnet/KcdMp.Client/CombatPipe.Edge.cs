// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using KcdMp.Wire;

namespace KcdMp.Client;

public enum EdgeLane : byte { Realtime = 1, Bulk = 2 }

public enum EdgeOutcome : byte
{
    Completed = 0,
    Busy = 1,
    Unsupported = 2,
    Superseded = 3,
    Invalid = 4,
    Unavailable = 250,
    Ambiguous = 251
}

/// <summary>Completed carries the existing nested reply. Busy is an explicit native non-admission.</summary>
public readonly record struct EdgePipeReply(EdgeOutcome Outcome, byte[] Payload);

public sealed partial class CombatPipe
{
    private const byte EdgeFrame = 0x38, EdgeReply = 0xB8, EdgeRawPose = 0x39, EdgeCapabilityOp = 255;
    private const byte EdgeLatest = 3;
    private const byte EdgeFaulted = 5, EdgeBusyAccepted = 6;
    private const int EdgeHeaderSize = 10, EdgeReplyHeaderSize = 6, EdgeMaxPayload = 4096;
    private const int EdgeMaxPending = 512, EdgeRealtimeBurst = 3;
    private readonly object _edgeLock = new();
    private readonly SemaphoreSlim _edgeProbeGate = new(1, 1);
    private readonly LinkedList<EdgeWork> _edgeRt = new(), _edgeBulk = new();
    private readonly Dictionary<EdgeStateKey, EdgeWork> _edgeLatest = new();
    private readonly Dictionary<uint, EdgeWork> _edgePending = new();
    private NamedPipeClientStream? _edgeProbedPipe, _edgeSupportedPipe;
    private Task? _edgeWriter;
    private uint _edgeNextId;
    private int _edgeRtTurn;
    private long _edgeBusyReplies, _edgeStateSkipped, _edgeConnectionRevision;
    private volatile bool _edgeTransportOn = true;

    public bool EdgeTransportOn { get => _edgeTransportOn; set => _edgeTransportOn = value; }
    public bool EdgeActive => EdgeTransportOn && Volatile.Read(ref _edgeSupportedPipe) is { IsConnected: true } pipe &&
        ReferenceEquals(pipe, _pipe);
    /// <summary>Matched native busy replies, including progress for requests that remain accepted.</summary>
    public long EdgeBusyReplies => Interlocked.Read(ref _edgeBusyReplies);
    public long EdgeStateSkipped => Interlocked.Read(ref _edgeStateSkipped);
    public long EdgeConnectionRevision => Interlocked.Read(ref _edgeConnectionRevision);

    private readonly record struct EdgeStateKey(byte Type, uint PeerOrKey, string? Body = null, bool State2 = false);

    private sealed class EdgeWork(NamedPipeClientStream pipe, byte lane, byte type, byte want, uint key, byte[] payload,
        CancellationToken cancellation, EdgeStateKey? stateKey, long arrival)
    {
        public readonly NamedPipeClientStream Pipe = pipe;
        public readonly byte Lane = lane, Type = type, Want = want;
        public readonly uint Key = key;
        public readonly byte[] Payload = payload;
        public readonly CancellationToken Cancellation = cancellation;
        public readonly EdgeStateKey? StateKey = stateKey;
        public readonly long Arrival = arrival;
        public readonly TaskCompletionSource<EdgePipeReply> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public uint Id;
        public LinkedListNode<EdgeWork>? QueueNode;
        public bool Writing;
    }

    /// <summary>Ask an installed DLL once per stream. An unanswered probe can be retried.</summary>
    public async Task<bool> ProbeEdgeAsync(CancellationToken ct = default)
    {
        if (!EdgeTransportOn) return false;
        await _edgeProbeGate.WaitAsync(ct);
        try
        {
            if (!EdgeTransportOn || !await EnsureConnectedAsync(ct) || CaptureSource() is not { } source) return false;
            if (ReferenceEquals(Volatile.Read(ref _edgeProbedPipe), source.Pipe)) return EdgeActive;
            var (body, _) = await SendAndAwaitAsync(Wo163, [EdgeCapabilityOp], Wo163Reply, ct, requiredPipe: source.Pipe);
            lock (_edgeLock)
            {
                if (!ReferenceEquals(source.Pipe, _pipe) || body is not { Length: >= 4 } || body[2] != EdgeCapabilityOp)
                    return false;
                bool supported = body is [1, _, EdgeCapabilityOp, 0, 1];
                Volatile.Write(ref _edgeSupportedPipe, supported ? source.Pipe : null);
                Volatile.Write(ref _edgeProbedPipe, source.Pipe);
                return supported && EdgeTransportOn;
            }
        }
        finally { _edgeProbeGate.Release(); }
    }

    public async Task<EdgePipeReply> EdgeRequestAsync(byte type, byte[] payload, byte expectedReplyType, EdgeLane lane,
        CancellationToken ct = default, long? connectionRevision = null)
    {
        using var scope = ProcessorFrameScope.Link(ct);
        ct = scope.Token;
        ct.ThrowIfCancellationRequested();
        ProcessorFrameScope.ThrowIfAbandoned();
        if (lane is not (EdgeLane.Realtime or EdgeLane.Bulk) || payload.Length > EdgeMaxPayload || expectedReplyType == 0 ||
            (type is >= Wo131 and <= Wo163 && payload.Length == 0))
            return EmptyEdge(EdgeOutcome.Invalid);
        if (!EdgeActive) return EmptyEdge(EdgeOutcome.Unavailable);
        return await QueueEdgeAsync(type, payload.ToArray(), expectedReplyType, (byte)lane, 0, ct, connectionRevision);
    }

    /// <summary>Submit the unchanged relay pose payload. True routes this update exclusively through the native edge.</summary>
    public bool PostEdgePose(byte relayKind, ReadOnlySpan<byte> payload, long arrivalQpc)
    {
        if (!EdgeActive || arrivalQpc <= 0 || PoseStateKey(relayKind, payload) is not { } stateKey) return false;
        var body = new byte[9 + payload.Length];
        body[0] = relayKind;
        BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(1), arrivalQpc);
        payload.CopyTo(body.AsSpan(9));
        _ = PostEdgeStateAsync(EdgeRawPose, body, Result, (byte)EdgeLane.Realtime, key: 0, stateKey, arrivalQpc);
        return true;
    }

    // The coordinates and state fields remain untouched. A short Ghost update
    // may replace a short update, while the last explicit State2 travels too.
    private static EdgeStateKey? PoseStateKey(byte kind, ReadOnlySpan<byte> payload)
    {
        if (kind == Protocol.Ghost && Protocol.IsGhostPayloadLen(payload.Length))
        {
            bool state2 = payload.Length >= Protocol.GhostPayloadLenV8 &&
                (payload[Protocol.GhostPayloadLen - 1] & Protocol.PositionFlagBodyState2) != 0;
            return new(kind, payload[0], State2: state2);
        }
        if (kind != Protocol.NpcStateDown || payload.Length < 3) return null;
        byte length = payload[1];
        if (length is 0 or > NativeNpcCodec.MaxNameLen || payload.Length != 2 + length + Protocol.NpcStateFixedTail)
            return null;
        var name = payload.Slice(2, length);
        foreach (byte c in name)
            if (c is not (>= (byte)'0' and <= (byte)'9' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z' or (byte)'_'))
                return null;
        return new(kind, payload[0], Encoding.ASCII.GetString(name).ToLowerInvariant());
    }

    /// <summary>A whole state can use keyed replacement when independent of other bodies' state.</summary>
    public bool PostEdgeLatest(byte type, byte[] payload, byte expectedReplyType, uint key)
    {
        if (!EdgeActive || key == 0 || expectedReplyType == 0 || payload.Length > EdgeMaxPayload ||
            type is not (SetPartner or ReadBodyState or ReadLocalState)) return false;
        _ = PostEdgeStateAsync(type, payload.ToArray(), expectedReplyType, EdgeLatest, key, new(type, key));
        return true;
    }

    private async Task PostEdgeStateAsync(byte type, byte[] payload, byte expectedType, byte lane, uint key,
        EdgeStateKey stateKey, long arrival = 0)
    {
        var result = await QueueEdgeAsync(type, payload, expectedType, lane, key, CancellationToken.None, stateKey: stateKey, arrival: arrival);
        if (result.Outcome != EdgeOutcome.Completed || result.Payload is [0, ..]) Interlocked.Increment(ref _edgeStateSkipped);
    }

    private static EdgePipeReply EmptyEdge(EdgeOutcome outcome) => new(outcome, []);

    private async Task<EdgePipeReply> QueueEdgeAsync(byte type, byte[] payload, byte want, byte lane, uint key,
        CancellationToken ct, long? connectionRevision = null, EdgeStateKey? stateKey = null, long arrival = 0)
    {
        EdgeWork request;
        lock (_edgeLock)
        {
            if (!EdgeTransportOn || _edgeSupportedPipe is not { IsConnected: true } pipe || !ReferenceEquals(_pipe, pipe) ||
                (connectionRevision.HasValue && connectionRevision.Value != EdgeConnectionRevision))
                return EmptyEdge(EdgeOutcome.Unavailable);
            if (stateKey is { } latest && _edgeLatest.TryGetValue(latest, out var replaced))
            {
                if (arrival < replaced.Arrival) return EmptyEdge(EdgeOutcome.Superseded);
                FinishEdge(replaced, EmptyEdge(EdgeOutcome.Superseded));
            }
            if (_edgePending.Count >= EdgeMaxPending) return EmptyEdge(EdgeOutcome.Unavailable);

            request = new(pipe, lane, type, want, key, payload, ct, stateKey, arrival);
            do { request.Id = unchecked(++_edgeNextId); }
            while (request.Id == 0 || _edgePending.ContainsKey(request.Id));
            _edgePending.Add(request.Id, request);
            request.QueueNode = (lane == (byte)EdgeLane.Bulk ? _edgeBulk : _edgeRt).AddLast(request);
            if (stateKey is { } state) _edgeLatest[state] = request;
            _edgeWriter ??= RunDetached(WriteEdgeQueueAsync);
        }

        try { return await request.Completion.Task.WaitAsync(ReplyDeadline, ct); }
        catch (TimeoutException)
        {
            lock (_edgeLock)
            {
                FinishEdge(request, EmptyEdge(request.Writing ? EdgeOutcome.Ambiguous : EdgeOutcome.Unavailable));
                return request.Completion.Task.GetAwaiter().GetResult();
            }
        }
        catch (OperationCanceledException)
        {
            lock (_edgeLock)
                FinishEdge(request, EmptyEdge(request.Writing ? EdgeOutcome.Ambiguous : EdgeOutcome.Unavailable));
            throw;
        }
    }

    // Caller owns _edgeLock. A state replacement keeps all other work ordered.
    private void UnqueueEdge(EdgeWork work, bool keepLatest = false)
    {
        if (work.QueueNode is { List: { } queue } node)
        {
            queue.Remove(node);
            work.QueueNode = null;
        }
        if (!keepLatest && work.StateKey is { } state && _edgeLatest.TryGetValue(state, out var tracked) &&
            ReferenceEquals(tracked, work))
            _edgeLatest.Remove(state);
    }

    private void FinishEdge(EdgeWork work, EdgePipeReply reply)
    {
        UnqueueEdge(work);
        if (_edgePending.Remove(work.Id)) work.Completion.TrySetResult(reply);
    }

    private async Task WriteEdgeQueueAsync()
    {
        while (true)
        {
            EdgeWork work;
            lock (_edgeLock)
            {
                bool bulk = _edgeBulk.Count > 0 && (_edgeRt.Count == 0 || _edgeRtTurn >= EdgeRealtimeBurst);
                var next = bulk ? _edgeBulk.First : _edgeRt.First;
                if (next is null) { _edgeWriter = null; _edgeRtTurn = 0; return; }
                work = next.Value;
                UnqueueEdge(work, keepLatest: true);
            }
            try
            {
                using var deadline = new CancellationTokenSource(ReplyDeadline);
                await _writeLock.WaitAsync(deadline.Token);
                try
                {
                    lock (_edgeLock)
                    {
                        if (!_edgePending.ContainsKey(work.Id)) continue;
                        if (!EdgeTransportOn || !ReferenceEquals(_pipe, work.Pipe) || work.Cancellation.IsCancellationRequested)
                        {
                            FinishEdge(work, EmptyEdge(EdgeOutcome.Unavailable));
                            continue;
                        }
                        UnqueueEdge(work);
                        _edgeRtTurn = work.Lane == (byte)EdgeLane.Bulk ? 0 : Math.Min(EdgeRealtimeBurst, _edgeRtTurn + 1);
                        work.Writing = true;
                    }
                    var body = new byte[EdgeHeaderSize + work.Payload.Length];
                    BinaryPrimitives.WriteUInt32LittleEndian(body, work.Id);
                    body[4] = work.Lane;
                    body[5] = work.Type;
                    BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(6), work.Key);
                    work.Payload.CopyTo(body, EdgeHeaderSize);
                    await WriteFrameLockedAsync(EdgeFrame, body, deadline.Token, work.Pipe);
                }
                finally { _writeLock.Release(); }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                lock (_edgeLock)
                    FinishEdge(work, EmptyEdge(work.Writing ? EdgeOutcome.Ambiguous : EdgeOutcome.Unavailable));
            }
        }
    }

    private bool TryEdgeReply(NamedPipeClientStream pipe, byte type, byte[] body)
    {
        if (type != EdgeReply) return false;
        if (body.Length < EdgeReplyHeaderSize) return true;
        uint id = BinaryPrimitives.ReadUInt32LittleEndian(body);
        lock (_edgeLock)
        {
            if (!_edgePending.TryGetValue(id, out var work) || !work.Writing || !ReferenceEquals(work.Pipe, pipe))
                return true;
            var outcome = body[4] == EdgeFaulted ? EdgeOutcome.Ambiguous : (EdgeOutcome)body[4];
            if (outcome == EdgeOutcome.Completed)
            {
                if (body[5] != work.Want) return true;
                var payload = body.AsSpan(EdgeReplyHeaderSize);
                if (work.Want == Pong ? payload.Length != 0 : payload.Length < 2 || payload[0] > 1 || payload[1] != 0)
                    return true;
                if (work.Want == Result && payload.Length != 3) return true;
                if (work.Type is >= Wo131 and <= Wo163 &&
                    (payload.Length < 4 || payload[2] != work.Payload[0]))
                    return true;
                FinishEdge(work, new(outcome, body[EdgeReplyHeaderSize..]));
                return true;
            }
            if (body[4] is < (byte)EdgeOutcome.Busy or > EdgeBusyAccepted || body[5] != 0 || body.Length != EdgeReplyHeaderSize)
                return true;
            if (body[4] is (byte)EdgeOutcome.Busy or EdgeBusyAccepted) Interlocked.Increment(ref _edgeBusyReplies);
            // The DLL accepted this work. Keep its ID and original deadline until the terminal reply.
            if (body[4] == EdgeBusyAccepted) return true;
            FinishEdge(work, EmptyEdge(outcome));
        }
        return true;
    }

    private void OnDropEdge(NamedPipeClientStream? pipe)
    {
        lock (_edgeLock)
        {
            if (pipe is not null) Interlocked.Increment(ref _edgeConnectionRevision);
            if (ReferenceEquals(_edgeProbedPipe, pipe)) Volatile.Write(ref _edgeProbedPipe, null);
            if (ReferenceEquals(_edgeSupportedPipe, pipe)) Volatile.Write(ref _edgeSupportedPipe, null);
            foreach (var work in _edgePending.Values.Where(work => ReferenceEquals(work.Pipe, pipe)).ToArray())
                FinishEdge(work, EmptyEdge(work.Writing ? EdgeOutcome.Ambiguous : EdgeOutcome.Unavailable));
        }
    }
}
