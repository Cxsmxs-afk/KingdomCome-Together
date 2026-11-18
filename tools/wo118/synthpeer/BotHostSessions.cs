// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.

// Relay ghost IDs are recycled. A newly announced Name and a Disconnect both
// retire a connection before any work queued under that connection can bind Ready.
sealed class BotHostSessions
{
    public sealed class Peer(byte id)
    {
        private readonly CancellationTokenSource disconnected = new();
        public byte Id { get; } = id;
        public CancellationToken Disconnected => disconnected.Token;
        public void Retire() => disconnected.Cancel();
    }

    public sealed record Join(Peer Peer, uint Id);
    private readonly object gate = new();
    private readonly Dictionary<byte, Peer> peers = new();
    private Join? pending;
    private Join? ready;

    public void Announce(byte id) => Update(id, new Peer(id));
    public void Disconnect(byte id) => Update(id, null);

    private void Update(byte id, Peer? next)
    {
        Peer? previous;
        lock (gate)
        {
            peers.Remove(id, out previous);
            if (pending?.Peer == previous) pending = null;
            if (ready?.Peer == previous) ready = null;
            if (next is not null) peers.Add(id, next);
        }
        previous?.Retire();
    }

    public Peer? Find(byte id)
    {
        lock (gate) return peers.GetValueOrDefault(id);
    }

    public Join? Begin(Peer peer, uint id)
    {
        lock (gate)
        {
            if (peers.GetValueOrDefault(peer.Id) != peer) return null;
            ready = null;
            return pending = new Join(peer, id);
        }
    }

    public bool MarkReady(Join join)
    {
        lock (gate)
        {
            if (pending != join || peers.GetValueOrDefault(join.Peer.Id) != join.Peer) return false;
            pending = null;
            ready = join;
            return true;
        }
    }

    public Join? ReadyFor(Peer? peer)
    {
        lock (gate) return ready is not null && ready.Peer == peer ? ready : null;
    }
}
