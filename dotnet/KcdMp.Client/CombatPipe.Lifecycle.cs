// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Concurrent;
using System.IO.Pipes;
using KcdMp.Wire;

namespace KcdMp.Client;

public sealed partial class CombatPipe
{
    private sealed record LifecycleWait(byte Op, NamedPipeClientStream Pipe, TaskCompletionSource<byte[]?> Reply);
    private readonly ConcurrentDictionary<uint, LifecycleWait> _lifecycleWaits = new();
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly object _connectionState = new();
    private int _disposed;
    private int _lifecycleNextId;
    private static readonly TimeSpan LifecycleDeadline = TimeSpan.FromSeconds(1);

    /// <summary>P1 control must reach the reader even while a main-thread batch is unanswered.
    /// It owns neither the normal request gate nor the batch pump; only physical frame writes are serialized.</summary>
    public async Task<Wo216LifecycleReply?> Wo216LifecycleAsync(byte phase, ulong generation, CancellationToken ct = default)
    {
        if (phase > Protocol.Wo216LifecycleQuery) throw new ArgumentOutOfRangeException(nameof(phase));
        byte[]? body = await SendControlAsync(Protocol.Wo216OpLifecycle, id => Wo216LifecycleCodec.BuildArgs(phase, generation, id), ct);
        return body is not null && Wo216LifecycleCodec.TryReadReply(body, out var reply) ? reply : null;
    }

    private async Task<byte[]?> SendControlAsync(byte op, Func<uint, byte[]> buildArgs, CancellationToken ct)
    {
        using var scope = ProcessorFrameScope.Link(ct);
        ct = scope.Token;
        ct.ThrowIfCancellationRequested();
        ProcessorFrameScope.ThrowIfAbandoned();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(LifecycleDeadline);
        uint requestId = 0;
        LifecycleWait? waiting = null;
        try
        {
            if (!await EnsureConnectedAsync(deadline.Token)) return null;
            var connection = _pipe;
            if (connection?.IsConnected != true) return null;
            waiting = new(op, connection, new(TaskCreationOptions.RunContinuationsAsynchronously));
            do { requestId = unchecked((uint)Interlocked.Increment(ref _lifecycleNextId)); }
            while (requestId == 0 || !_lifecycleWaits.TryAdd(requestId, waiting));
            byte[] payload = [op, .. buildArgs(requestId)];
            await _writeLock.WaitAsync(deadline.Token);
            try
            {
                deadline.Token.ThrowIfCancellationRequested();
                ProcessorFrameScope.ThrowIfAbandoned();
                // A reconnect while this request waited for the writer must not
                // move its old generation onto the replacement pipe.
                if (!ReferenceEquals(_pipe, connection) || !connection.IsConnected) return null;
                await WriteFrameLockedAsync(Wo163, payload, deadline.Token, connection);
            }
            finally { _writeLock.Release(); }
            var reply = await waiting.Reply.Task.WaitAsync(deadline.Token);
            ct.ThrowIfCancellationRequested();
            ProcessorFrameScope.ThrowIfAbandoned();
            return ReferenceEquals(_pipe, connection) ? reply : null;
        }
        catch (OperationCanceledException)
        {
            // Cancelling the writer wait or reply wait never resets the pipe.
            // WriteFrameLockedAsync alone closes a stream if physical framing may be partial.
            ct.ThrowIfCancellationRequested();
            ProcessorFrameScope.ThrowIfAbandoned();
            return null; // our own bounded control deadline
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            return null;
        }
        finally
        {
            if (waiting is not null)
                ((ICollection<KeyValuePair<uint, LifecycleWait>>)_lifecycleWaits).Remove(new(requestId, waiting));
        }
    }

    // Every lifecycle reply stays out of the normal shared reply channel, including
    // expired IDs and malformed ACKs. Otherwise a late control ACK can masquerade
    // as a later unrelated Wo163 response.
    private bool TryLifecycleReply(NamedPipeClientStream? connection, byte type, byte[] body)
    {
        if (type != Wo163Reply || body.Length < 3) return false;
        byte op = body[2];
        uint requestId;
        if (op == Protocol.Wo216OpLifecycle)
            requestId = Wo216LifecycleCodec.TryReadReply(body, out var reply) ? reply.RequestId : 0;
        else if (op == LoadSequenceOp)
            requestId = LoadSequenceNativeReply.TryRead(body, out var load) ? load.RequestId : 0;
        else return false;
        if (requestId != 0 && _lifecycleWaits.TryGetValue(requestId, out var waiting) && waiting.Op == op &&
            ReferenceEquals(waiting.Pipe, connection)) waiting.Reply.TrySetResult(body);
        return true;
    }

    private void OnDropLifecycle(NamedPipeClientStream? connection)
    {
        foreach (var (id, waiting) in _lifecycleWaits)
        {
            if (!ReferenceEquals(waiting.Pipe, connection)) continue;
            if (((ICollection<KeyValuePair<uint, LifecycleWait>>)_lifecycleWaits).Remove(new(id, waiting)))
                waiting.Reply.TrySetResult(null);
        }
    }
}
