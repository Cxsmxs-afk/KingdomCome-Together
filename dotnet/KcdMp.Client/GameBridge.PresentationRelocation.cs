// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.Globalization;
using KcdMp.Wire;

namespace KcdMp.Client;

public partial class GameBridge
{
    private const byte RelocationRequest = 0x3E, RelocationReply = 0x81;

    private void NotePresentationRelocationAtRead(PresentationSession session, int type, byte[] payload, long arrival, uint stamp)
    {
        if (type is not (Protocol.Name or Protocol.Disconnect or Protocol.PlayerRespawnedDown or Protocol.W164Down) ||
            payload.Length == 0 || !ReferenceEquals(Volatile.Read(ref _presentationSession), session)) return;
        byte peer = payload[0];
        if (peer == _myGhostId) return;
        if (type == Protocol.Name && payload.Length >= 2)
        {
            session.Relocations.PeerNamed(peer);
            return;
        }
        if (type == Protocol.Disconnect && payload.Length == 1)
        {
            session.Relocations.PeerLeft(peer);
            return;
        }
        if (type == Protocol.PlayerRespawnedDown)
        {
            if (payload.Length == Protocol.PlayerRespawnedDownPayloadLen &&
                TryRelocationScope(peer, requireLive: false, out var wake) && stamp == wake.World)
                session.Relocations.Respawn(peer, wake, PresentationClock.QpcToMs(arrival), payload[13],
                    new(ReadFloat(payload, 1), ReadFloat(payload, 5), ReadFloat(payload, 9)));
            return;
        }

        const int bodyOffset = 1 + Protocol.JoinHeaderLen;
        if (!W137Joiner || !_hostModeKnown || peer != _hostModeFrom ||
            payload.Length <= bodyOffset || payload[1] != _myGhostId || payload[bodyOffset] != Protocol.W164Travel ||
            !LootMsg.TryDecode(payload.AsSpan(bodyOffset), out var message) ||
            !TryRelocationScope(peer, requireLive: false, out var travel) || stamp != travel.World) return;
        session.Relocations.HostTravel(peer, travel, PresentationClock.QpcToMs(arrival), message.Text);
    }

    private void NotePresentationGhostSpawn(string id, uint entity)
    {
        if (!byte.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out byte peer) || peer == _myGhostId ||
            Volatile.Read(ref _presentationSession) is not { } session ||
            !TryRelocationScope(peer, requireLive: false, out var scope) || scope.Entity != entity) return;
        session.Relocations.BodySpawned(peer, scope, PresentationClock.NowMs());
    }

    private bool TryRelocationScope(byte peer, bool requireLive, out RemoteRelocationScope scope)
    {
        scope = default;
        var lifecycle = P1Lifecycle.Current;
        uint world = P5LoadedWorld;
        if (world == 0 || world != _w206.World || !lifecycle.Confirmed || !lifecycle.GameplaySent ||
            lifecycle.Generation == 0 || _p1Loading || _w140Separate || _leaveInProgress ||
            (requireLive && !IsLivePeer(peer))) return false;
        string id = peer.ToString(CultureInfo.InvariantCulture);
        if (!_ghostEntityIds.TryGetValue(id, out uint entity) || entity == 0) return false;
        scope = new(world, _w206.Join, lifecycle.Generation, Interlocked.Read(ref _p1Serial), _combat.EdgeConnectionRevision,
            entity, GhostGeneration(id), W137Host, _hostModeKnown ? _hostModeFrom : (byte?)null);
        return true;
    }

    private void OfferPresentationRelocation(GhostSample pose, long arrival, PresentationFrame presentation, long connection)
    {
        if (pose.IsStale || pose.IsRiding || presentation.Session is not { } session ||
            !session.Relocations.HasPending(pose.GhostId) || !W221On("rp_controller") ||
            !ReferenceEquals(Volatile.Read(ref _presentationSession), session) ||
            !NpcNativeBound("kcd2mp_" + pose.GhostId.ToString(CultureInfo.InvariantCulture)) ||
            !TryRelocationScope(pose.GhostId, requireLive: true, out var scope) || scope.Pipe != connection) return;
        long received = PresentationClock.QpcToMs(arrival);
        long capture = received - Math.Max(0, (long)presentation.HostAtArrivalMs - (long)presentation.SampleMs);
        if (session.Relocations.Take(pose.GhostId, scope, received, capture, PresentationClock.NowMs(),
            new(pose.X, pose.Y, pose.Z)) is { } approval)
            _ = AuthorizePresentationRelocationAsync(session, approval, scope, presentation);
    }

    private async Task AuthorizePresentationRelocationAsync(PresentationSession session, RemoteRelocationApproval approval,
        RemoteRelocationScope scope, PresentationFrame presentation)
    {
        bool Current() => PresentationClock.NowMs() <= approval.Deadline && W221On("rp_controller") &&
            PresentationReady(presentation, out long connection) && connection == scope.Pipe &&
            NpcNativeBound("kcd2mp_" + approval.Peer.ToString(CultureInfo.InvariantCulture)) &&
            TryRelocationScope(approval.Peer, requireLive: true, out var current) && current == scope &&
            session.Relocations.IsCurrent(approval);
        try
        {
            var result = await _combat.EdgeRequestAsync(RelocationRequest, approval.Build(), RelocationReply, EdgeLane.Realtime,
                session.Lifetime, connectionRevision: scope.Pipe, beforeWrite: Current);
            if (result.Outcome == EdgeOutcome.Completed && result.Payload is [1, 0, 0])
                Console.WriteLine($"MP-RP-RELOCATION peer={approval.Peer} reason={approval.Reason.ToString().ToLowerInvariant()} authorized=1");
        }
        catch (OperationCanceledException) { }
    }
}

public enum RemoteRelocationReason : byte { Join = 1, Respawn = 2, Travel = 3 }

public readonly record struct RemoteRelocationScope(uint World, uint Join, ulong LoadGeneration, long LoadSerial, long Pipe,
    uint Entity, int BodyGeneration, bool IsHost, byte? Host);

public readonly record struct RemoteRelocationPoint(float X, float Y, float Z)
{
    public bool Valid => ValidAxis(X) && ValidAxis(Y) && ValidAxis(Z);
    private static bool ValidAxis(float val) => float.IsFinite(val) && Math.Abs(val) <= 100_000f;

    public bool Near(RemoteRelocationPoint other)
    {
        float dx = X - other.X, dy = Y - other.Y, dz = Z - other.Z;
        return dx * dx + dy * dy + dz * dz <= 4f;
    }
}

public readonly record struct RemoteRelocationApproval(byte Peer, uint Entity, RemoteRelocationReason Reason, RemoteRelocationPoint Point,
    long Deadline, long PeerEpoch)
{
    public byte[] Build()
    {
        byte[] payload = new byte[18];
        payload[0] = Peer;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1), Entity);
        payload[5] = (byte)Reason;
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(6), Point.X);
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(10), Point.Y);
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(14), Point.Z);
        return payload;
    }
}

/// <summary>One ledger per authenticated relay connection. Position alone can only match an existing proof.</summary>
public sealed class RemoteRelocationProofs
{
    private const int FreshMs = 2_000;
    private sealed record Evidence(RemoteRelocationReason Reason, RemoteRelocationScope Scope, long At, RemoteRelocationPoint? Target);
    private sealed record TravelRun(uint Sequence, RemoteRelocationScope Scope, long At);

    private sealed class Peer
    {
        public bool Joining = true;
        public long Epoch;
        public Evidence? Pending;
        public TravelRun? Travel;
        public uint? LastTravel;
    }

    private readonly object _gate = new();
    private readonly Dictionary<byte, Peer> _peers = new();
    private long _peerEpoch;

    public void PeerNamed(byte id)
    {
        lock (_gate)
            if (!_peers.ContainsKey(id)) _peers.Add(id, new() { Epoch = ++_peerEpoch });
    }
    public void PeerLeft(byte id) { lock (_gate) _peers.Remove(id); }
    public bool HasPending(byte id) { lock (_gate) return _peers.TryGetValue(id, out var peer) && peer.Pending is not null; }
    public bool IsCurrent(RemoteRelocationApproval approval)
    {
        lock (_gate) return _peers.TryGetValue(approval.Peer, out var peer) && peer.Epoch == approval.PeerEpoch;
    }

    public void BodySpawned(byte id, RemoteRelocationScope scope, long at)
    {
        lock (_gate)
        {
            if (!_peers.TryGetValue(id, out var peer)) return;
            peer.Epoch = ++_peerEpoch;
            peer.Pending = peer.Joining ? new(RemoteRelocationReason.Join, scope, at, null) : null;
            peer.Joining = false;
            peer.Travel = null;
        }
    }

    public void Respawn(byte id, RemoteRelocationScope scope, long at, byte reason, RemoteRelocationPoint point)
    {
        if (reason is not (Protocol.RespawnReasonDeath or Protocol.RespawnReasonExecution) || !point.Valid) return;
        lock (_gate)
        {
            if (!_peers.TryGetValue(id, out var peer)) return;
            peer.Epoch = ++_peerEpoch;
            peer.Pending = new(RemoteRelocationReason.Respawn, scope, at, point);
            peer.Travel = null;
        }
    }

    public void HostTravel(byte id, RemoteRelocationScope scope, long at, string text)
    {
        if (scope.IsHost || scope.Host != id || !W164Text.TryParseTravel(text, out char edge, out uint seq, out string arg, out _))
            return;
        lock (_gate)
        {
            if (!_peers.TryGetValue(id, out var peer)) return;
            if (edge == 's' && (peer.LastTravel is not uint last || unchecked((int)(seq - last)) > 0))
            {
                peer.Epoch = ++_peerEpoch;
                peer.Pending = null;
                peer.LastTravel = seq;
                peer.Travel = new(seq, scope, at);
            }
            else if (edge == 'e' && peer.Travel is { } run && run.Sequence == seq)
            {
                peer.Travel = null;
                if (run.Scope == scope && at >= run.At && at - run.At <= Wo167Rules.MirrorMaxMs &&
                    W164Text.TryParsePos(arg, out float x, out float y, out float z))
                    peer.Pending = new(RemoteRelocationReason.Travel, scope, at, new(x, y, z));
            }
        }
    }

    public RemoteRelocationApproval? Take(byte id, RemoteRelocationScope scope, long arrival, long capture, long now,
        RemoteRelocationPoint point)
    {
        lock (_gate)
        {
            if (!_peers.TryGetValue(id, out var peer) || peer.Pending is not { } proof) return null;
            if (proof.Scope != scope || now >= proof.At && now - proof.At > FreshMs)
            {
                peer.Pending = null;
                return null;
            }
            if (capture < proof.At || arrival < capture || arrival > now || now - arrival > FreshMs || !point.Valid ||
                (proof.Target is { } target && !target.Near(point))) return null;
            peer.Pending = null;
            return new(id, scope.Entity, proof.Reason, proof.Target ?? point, proof.At + FreshMs, peer.Epoch);
        }
    }
}
