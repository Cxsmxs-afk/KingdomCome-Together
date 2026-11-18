// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using KcdMp.Wire;

namespace KcdMp.Client;

public partial class GameBridge
{
    private const byte PresentationConfigType = 0x3D, PresentationActionType = 0x3B, PresentationResult = 0x81;
    private readonly record struct PresentationFrame(PresentationSession? Session, ulong Epoch, ulong SampleMs, ulong HostAtArrivalMs);
    private sealed class PresentationSession(Stream relay)
    {
        public readonly Stream Relay = relay;
        public readonly PresentationClockClient Clock = new();
        public ulong Epoch { get; private set; }

        public bool AcceptEpoch(ulong epoch, out bool changed)
        {
            lock (Clock)
            {
                changed = Epoch != 0 && Epoch != epoch;
                if (epoch == 0 || epoch < Epoch) return false;
                Epoch = epoch;
                return true;
            }
        }

        public bool IsEpoch(ulong epoch)
        {
            lock (Clock) return Epoch == epoch;
        }
    }

    private sealed record PresentationConfig(long Revision, long Connection, bool Enabled);
    private readonly SemaphoreSlim _presentationConfigGate = new(1, 1);
    private PresentationSession? _presentationSession;
    private PresentationConfig? _presentationNativeConfig;
    private long _presentationRevision;
    private volatile bool _presentationOn = true;

    private void PresentationSetMode(string arg)
    {
        if (arg is not ("on" or "off")) return;
        _presentationOn = arg == "on";
        PresentationResetNative();
        Console.WriteLine($"MP-RP-TIMELINE requested={arg}");
    }

    private void PresentationResetNative(CancellationToken ct = default)
    {
        Interlocked.Increment(ref _presentationRevision);
        _ = PresentationConfigureAsync(ct);
    }

    private async Task PresentationConfigureAsync(CancellationToken ct = default)
    {
        if (!_combat.EdgeActive || !_presentationConfigGate.Wait(0)) return;
        try
        {
            long revision = Interlocked.Read(ref _presentationRevision), connection = _combat.EdgeConnectionRevision;
            bool enabled = _presentationOn;
            if (Volatile.Read(ref _presentationNativeConfig) is { } old && old.Revision == revision && old.Connection == connection)
                return;
            var result = await _combat.EdgeRequestAsync(PresentationConfigType, [enabled ? (byte)1 : (byte)0],
                PresentationResult, EdgeLane.Realtime, ct, connectionRevision: connection);
            if (result.Outcome == EdgeOutcome.Completed && result.Payload is [1, 0, 0] &&
                revision == Interlocked.Read(ref _presentationRevision) && connection == _combat.EdgeConnectionRevision)
            {
                Volatile.Write(ref _presentationNativeConfig, new PresentationConfig(revision, connection, enabled));
                Console.WriteLine($"MP-RP-TIMELINE configured={(enabled ? "on" : "off")}");
            }
        }
        catch (OperationCanceledException) { }
        finally { _presentationConfigGate.Release(); }
    }

    private bool PresentationReady(PresentationFrame stamp, out long connection)
    {
        connection = _combat.EdgeConnectionRevision;
        return _presentationOn && stamp.SampleMs != 0 && stamp.HostAtArrivalMs != 0 && _combat.EdgeActive &&
            stamp.Session is { } session && ReferenceEquals(Volatile.Read(ref _presentationSession), session) && session.IsEpoch(stamp.Epoch) &&
            Volatile.Read(ref _presentationNativeConfig) is { Enabled: true } config && config.Connection == connection &&
            config.Revision == Interlocked.Read(ref _presentationRevision);
    }

    private async Task PresentationClockLoopAsync(PresentationSession session, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if ((_w213Caps & Protocol.LinkCapPresentationTime) != 0)
                    await WritePacketAsync(session.Relay, session.Clock.Begin(PresentationClock.NowMs()), ct);
                await Task.Delay(PresentationClock.ProbeIntervalMs, ct);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException) { }
    }

    private async Task PresentationSendClockConfirmationAsync(PresentationSession session, byte[] frame, CancellationToken ct)
    {
        try { await WritePacketAsync(session.Relay, frame, ct); }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException) { }
    }

    private bool PresentationReadFrame(PresentationSession session, ref int type, ref byte[] payload, long arrival,
        out PresentationFrame presentation, CancellationToken ct)
    {
        presentation = default;
        long localArrival = PresentationClock.QpcToMs(arrival);
        if (type == Protocol.PresentationClockDown)
        {
            if (session.Clock.TryComplete(payload, localArrival, out var confirmation))
                _ = PresentationSendClockConfirmationAsync(session, confirmation, ct);
            return false;
        }
        if (type is not (Protocol.PresentationPoseDown or Protocol.PresentationActionDown)) return true;
        if (!PresentationTimeWire.TryRead(type, payload, out var stamp, out byte inner, out int offset) ||
            !session.AcceptEpoch(stamp.Epoch, out bool changed)) return false;
        if (changed) PresentationResetNative(ct);
        type = inner;
        payload = payload[offset..];
        if (session.Clock.TryHostAt(localArrival, stamp.RelayToHostMs, out ulong projectedHost))
            presentation = new PresentationFrame(session, stamp.Epoch, stamp.HostMs, projectedHost);
        return true;
    }

    private async Task<bool> TrySchedulePresentationRowAsync(InboundAction action, uint eid, string spec,
        PresentationFrame stamp, CancellationToken ct)
    {
        if (action.Phase != ActionPhase.Commit || action.Kind is not (ActionKind.Attack or ActionKind.BlockImpulse or ActionKind.Dodge) ||
            !PresentationReady(stamp, out long connection) || !NpcNativeBound("kcd2mp_" + action.SourceGhostId)) return false;
        int specBytes = Encoding.UTF8.GetByteCount(spec);
        if (specBytes is <= 0 or > 191) return false;
        var body = new byte[17 + specBytes];
        body[0] = action.SourceGhostId;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(1), eid);
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(5), stamp.SampleMs);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(13), action.Seq);
        body[15] = (byte)action.Kind;
        body[16] = (byte)action.Phase;
        Encoding.UTF8.GetBytes(spec, body.AsSpan(17));
        var result = await _combat.EdgeRequestAsync(PresentationActionType, body, PresentationResult,
            EdgeLane.Realtime, ct, connectionRevision: connection);
        if (result.Outcome is EdgeOutcome.Busy or EdgeOutcome.Unsupported or EdgeOutcome.Unavailable ||
            (result.Outcome == EdgeOutcome.Completed && result.Payload is [0, 0, 1]))
            return !PresentationReady(stamp, out var current) || current != connection;
        bool accepted = result.Outcome == EdgeOutcome.Completed && result.Payload is [1, 0, 0];
        uint sourceMs = action.Payload.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(action.Payload) : 0;
        string status = accepted ? "accepted" : result.Outcome == EdgeOutcome.Ambiguous ? "ambiguous" : "refused";
        Console.WriteLine(FormattableString.Invariant($"MP-RP-TIMELINE-IN peer={action.SourceGhostId} kind={(byte)action.Kind} phase={(byte)action.Phase} seq={action.Seq} source_ms={sourceMs} event_host_ms={stamp.SampleMs} result={status}"));
        // Only an explicit non-admission can use the older swing inbox.
        return true;
    }
}
