// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.IO.Pipes;
using KcdMp.Wire;

namespace KcdMp.Client.Tests;

/// <summary>The native side of the calendar join fixture. GameBridge owns all relay frames and load decisions.</summary>
internal sealed class JoinCalendarTestPipe : IAsyncDisposable
{
    public const long HostWorldMs = 572_161_000;
    public const double HostRatio = 15;

    private const byte Pong = 0x83, Result = 0x81, Wo140Reply = 0xA4, Wo151Reply = 0xAB, Wo163Reply = 0xAC;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _serverLoop;
    private readonly bool _failCalendarArm;
    private readonly bool _failCalendarVerify;
    private readonly bool _refuseHold;
    private readonly Func<long>? _nativeClock;
    private int _refusedRenewals;
    private int _renewalAttempts;
    private int _pauseStarts;
    private long _pauseDeadline;
    private readonly TaskCompletionSource _arm = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _verify = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _calendar = new();
    private long? _armedWorldMs, _loadedWorldMs, _verifiedWorldMs;
    private int _snapshotWhileHeld;
    private int _armRequests;
    private bool _held;
    private byte _loadPhase, _loadFlags, _lifecyclePhase;
    private ulong _loadGeneration, _lifecycleGeneration;

    public CombatPipe Pipe { get; }
    public Action? BeforeCalendarArm { get; set; }
    public Action<string>? RequestScopedSnapshot { get; set; }
    public Task ArmObserved => _arm.Task;
    public int ArmRequests => Volatile.Read(ref _armRequests);
    public Task VerifyObserved => _verify.Task;
    public Task HoldObserved => _hold.Task;
    public Task ReleaseObserved => _release.Task;
    public int RenewalAttempts => Volatile.Read(ref _renewalAttempts);
    public int PauseStarts => Volatile.Read(ref _pauseStarts);
    public bool NativePauseEffective => Volatile.Read(ref _held) &&
        (_nativeClock is null || _nativeClock() <= Interlocked.Read(ref _pauseDeadline));
    public bool SnapshotWhileHeld => Volatile.Read(ref _snapshotWhileHeld) != 0;
    public long? ArmedWorldMs { get { lock (_calendar) return _armedWorldMs; } }
    public long? VerifiedWorldMs { get { lock (_calendar) return _verifiedWorldMs; } }

    public JoinCalendarTestPipe(bool failCalendarArm = false, bool failCalendarVerify = false, bool refuseHold = false,
        Func<long>? nativeClock = null, int refusedRenewals = 0)
    {
        _failCalendarArm = failCalendarArm;
        _failCalendarVerify = failCalendarVerify;
        _refuseHold = refuseHold;
        _nativeClock = nativeClock;
        _refusedRenewals = refusedRenewals;
        string name = $"h20-calendar-{Guid.NewGuid():N}";
        Pipe = new CombatPipe(name);
        _serverLoop = ServeAsync(name, _stop.Token);
    }

    // Called by the load boundary before publishing the guest's physical Gameplay marker in kcd.log.
    public void ObserveGameplayStarted()
    {
        lock (_calendar)
        {
            _loadedWorldMs = _armedWorldMs;
        }
    }

    public void ObserveMainMenu()
    {
        lock (_calendar) _loadedWorldMs = null;
    }

    private async Task ServeAsync(string name, CancellationToken ct)
    {
        try
        {
            using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            while (!ct.IsCancellationRequested)
            {
                await server.WaitForConnectionAsync(ct);
                var header = new byte[3];
                byte seq = 0;
                // The production client can close between writes. An incomplete
                // message belongs to that connection and has no valid reply.
                while (await server.ReadAtLeastAsync(header, header.Length, false, ct) == header.Length)
                {
                    var payload = new byte[BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(1))];
                    if (await server.ReadAtLeastAsync(payload, payload.Length, false, ct) != payload.Length)
                        break;
                    var (type, body) = Reply(header[0], seq++, payload);
                    byte[] frame = [type, (byte)body.Length, (byte)(body.Length >> 8), .. body];
                    await server.WriteAsync(frame, ct);
                    await server.FlushAsync(ct);
                }
                server.Disconnect();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (IOException) when (ct.IsCancellationRequested) { }
    }

    private (byte Type, byte[] Body) Reply(byte type, byte seq, byte[] request) => type switch
    {
        0x03 => (Pong, []),
        // Session, respawn and combat setup have no readback beyond these Result ACKs.
        0x0C or 0x0D or 0x16 or 0x18 or 0x20 or 0x2C => (Result, [1, seq, 0]),
        0x1D when request.Length >= 3 && request.Length == 3 + request[2] => (0x8D, [1, seq, 1, 0, 0, 1, 0, request[1], request[1], 0, 0, 0]),
        0x1E => (0x8E, [1, seq, 1, 1, 1]),
        0x26 when request is [9] => (Wo140Reply, CalendarSnapshot(seq)),
        0x26 when request is [10, ..] && request.Length == 18 => (Wo140Reply, CalendarFollow(seq, request)),
        0x2A when request is [8, _, _, _] => (Wo151Reply, JoinHold(seq, request)),
        0x2B when request.Length > 0 => (Wo163Reply, Wo163(seq, request)),
        _ => (Result, [0, seq, (byte)PipeReason.UnknownCommand]),
    };

    private byte[] CalendarSnapshot(byte seq)
    {
        if (_held) Volatile.Write(ref _snapshotWhileHeld, 1);
        byte[] body = ReplyBody(seq, 9, 17);
        body[4] = 1;
        BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(5), HostWorldMs);
        BinaryPrimitives.WriteDoubleLittleEndian(body.AsSpan(13), _held ? 0 : HostRatio);
        return body;
    }

    private byte[] CalendarFollow(byte seq, ReadOnlySpan<byte> request)
    {
        byte[] body = ReplyBody(seq, 10, 17);
        long worldMs = BinaryPrimitives.ReadInt64LittleEndian(request[2..]);
        lock (_calendar)
        {
            switch (request[1])
            {
                case 0:
                    _armedWorldMs = _loadedWorldMs = null;
                    break;
                case 1:
                    body[4] = 1;
                    BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(13), worldMs);
                    break;
                case 2:
                    Interlocked.Increment(ref _armRequests);
                    BeforeCalendarArm?.Invoke();
                    // The real mode 2 accepts a new latch in an already loaded world.
                    // The old Gameplay receipt cannot verify that newly armed latch.
                    if (!_failCalendarArm && worldMs is >= 0 and <= HostCalendar.MaxWorldMs)
                    {
                        _armedWorldMs = worldMs;
                        _loadedWorldMs = null;
                        body[4] = 2;
                    }
                    _arm.TrySetResult();
                    break;
                case 3:
                    if (!_failCalendarVerify && _loadedWorldMs is { } loaded)
                    {
                        _verifiedWorldMs = loaded;
                        BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(5), loaded);
                        BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(13), loaded);
                        body[4] = 1;
                    }
                    _verify.TrySetResult();
                    break;
            }
        }
        return body;
    }

    private byte[] JoinHold(byte seq, ReadOnlySpan<byte> request)
    {
        bool held = request[1] != 0;
        if (held && _nativeClock is not null)
        {
            Interlocked.Increment(ref _renewalAttempts);
            if (_refusedRenewals > 0)
            {
                --_refusedRenewals;
                return [0, seq, 8, 0, (byte)(NativePauseEffective ? 1 : 0)];
            }
        }
        SetHold(held, BinaryPrimitives.ReadUInt16LittleEndian(request[2..]));
        return [1, seq, 8, 0, (byte)(held ? 1 : 0)];
    }

    private void SetHold(bool held, ushort seconds)
    {
        if (held && _nativeClock is not null)
        {
            if (!NativePauseEffective) Interlocked.Increment(ref _pauseStarts);
            Interlocked.Exchange(ref _pauseDeadline, _nativeClock() + seconds * 1000L);
        }
        if (_held == held) return;
        Volatile.Write(ref _held, held);
        if (held) _hold.TrySetResult();
        else _release.TrySetResult();
    }

    private byte[] Wo163(byte seq, ReadOnlySpan<byte> request) => request[0] switch
    {
        CombatPipe.LoadSequenceOp when request.Length == 17 => LoadSequence(seq, request),
        Protocol.Wo216OpLifecycle when request.Length == 1 + Wo216LifecycleCodec.RequestArgsLength => Lifecycle(seq, request),
        Protocol.DeathMarkerControlOp when request.Length == 45 => DeathMarker(seq, request),
        Protocol.Wo216OpConfig when request.Length == 14 => [1, seq, request[0], 0],
        Protocol.Wo216OpBrains when request.Length == 1 => [1, seq, request[0], 0, .. "decision_gate=armed story_gate=armed"u8],
        Protocol.Wo200OpConfig when request.Length == 7 => [1, seq, request[0], 0, request[1], request[2]],
        Protocol.Wo184OpSaveState when request.Length == 1 => [1, seq, request[0], 0, 1, 0, 0, 1],
        Protocol.H21OpSnapshot when request.Length == 26 => ScopedSnapshot(seq, request[1..]),
        _ => [0, seq, request[0], (byte)PipeReason.UnknownCommand],
    };

    private byte[] ScopedSnapshot(byte seq, ReadOnlySpan<byte> body)
    {
        bool phase = _loadPhase is (byte)WorldLoadPhase.Settle or (byte)WorldLoadPhase.Play ||
            _loadPhase == (byte)WorldLoadPhase.Exit && body[24] == 1;
        if (!phase || _loadFlags != 0 || _loadGeneration != BinaryPrimitives.ReadUInt64LittleEndian(body[16..]))
            return [0, seq, Protocol.H21OpSnapshot, 20];
        RequestScopedSnapshot?.Invoke("kct_h21_" + Convert.ToHexString(body[..16]).ToLowerInvariant());
        return [1, seq, Protocol.H21OpSnapshot, 0, 1];
    }

    private byte[] LoadSequence(byte seq, ReadOnlySpan<byte> request)
    {
        if (request[1] != CombatPipe.LoadSequenceQuery)
        {
            _loadPhase = request[1];
            _loadFlags = request[2];
            _loadGeneration = BinaryPrimitives.ReadUInt64LittleEndian(request[5..]);
            SetHold((_loadFlags & 3) != 0 && !_refuseHold, BinaryPrimitives.ReadUInt16LittleEndian(request[3..]));
        }
        byte[] body = ReplyBody(seq, request[0], 14);
        body[4] = _loadPhase;
        body[5] = (byte)(_loadFlags | (((_loadFlags & 3) == 0 || NativePauseEffective) ? 0x80 : 0));
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(6), _loadGeneration);
        request[13..17].CopyTo(body.AsSpan(14));
        return body;
    }

    private byte[] Lifecycle(byte seq, ReadOnlySpan<byte> request)
    {
        if (request[1] != Protocol.Wo216LifecycleQuery)
        {
            _lifecyclePhase = request[1];
            _lifecycleGeneration = BinaryPrimitives.ReadUInt64LittleEndian(request[2..]);
        }
        byte[] body = ReplyBody(seq, request[0], Wo216LifecycleCodec.AckPayloadLength);
        body[4] = _lifecyclePhase;
        body[5] = (byte)(_lifecyclePhase == Protocol.Wo216LifecycleDisabled ? 0 : 1);
        body[6] = (byte)(_lifecyclePhase == Protocol.Wo216LifecycleGameplay ? 1 : 0);
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(7), _lifecycleGeneration);
        request[10..14].CopyTo(body.AsSpan(19));
        return body;
    }

    private static byte[] DeathMarker(byte seq, ReadOnlySpan<byte> request)
    {
        byte[] body = ReplyBody(seq, request[0], 16);
        body[4] = 1;
        body[5] = request[2];
        request[5..17].CopyTo(body.AsSpan(8));
        return body;
    }

    private static byte[] ReplyBody(byte seq, byte op, int payloadLength)
    {
        var body = new byte[4 + payloadLength];
        body[0] = 1;
        body[1] = seq;
        body[2] = op;
        return body;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try
        {
            await Pipe.DisposeAsync();
            await _serverLoop;
        }
        finally { _stop.Dispose(); }
    }
}
