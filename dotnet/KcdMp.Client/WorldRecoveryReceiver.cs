// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
namespace KcdMp.Client;

// The receipt projection contains exact authoritative rows confirmed by the
// baseline installer. Poses and clocks can legitimately progress after it.
internal sealed class WorldRecoveryReceiver
{
    internal enum DigestResult { Ignore, Match, Mismatch, Repeat, Reanchored, TimedOut }
    internal enum PartResult { Ignore, Waiting, Ready, Rejected }

    private const long RepairDeadlineMs = 30_000;
    private readonly Dictionary<WorldBaselineSection, Area> _areas = new();
    private int _corrupted;
    private int _bufferedBytes;

    private sealed class Area(WorldRecoveryHash receipt)
    {
        public WorldRecoveryHash Receipt = receipt;
        public WorldRecoveryDigest? Last;
        public PendingRepair? Repair;
        public bool Rejected;
        public long ReadbackVersion;
        public int BadReadbacks, Reading;
    }

    private sealed class PendingRepair(long now)
    {
        public readonly long StartedAt = now;
        public byte[]?[]? Parts;
        public int Filled, Bytes;
    }

    public void Install(IReadOnlyList<WorldBaselineChunk> complete)
    {
        foreach (var group in complete.GroupBy(chunk => chunk.Section))
        {
            var first = group.First();
            if (WorldRecoveryWire.TryComputeHash(first.Scope, first.SnapshotId, first.Section,
                    group.OrderBy(chunk => chunk.Index).Select(chunk => chunk.Payload).ToArray(), out var receipt))
                _areas.Add(first.Section, new(receipt));
        }
    }

    public bool TestCorrupt(WorldBaselineSection section) =>
        Interlocked.CompareExchange(ref _corrupted, (int)section, 0) == 0;

    public DigestResult Observe(WorldRecoveryDigest digest, long now)
    {
        if (!_areas.TryGetValue(digest.Section, out var area)) return DigestResult.Ignore;
        if (area.Last is { } last && (digest.Revision < last.Revision ||
                (digest.Revision == last.Revision && digest.Hash != last.Hash)))
            return DigestResult.Ignore;
        if (area.Last is { } previous && digest.Revision > previous.Revision)
        {
            // A replacement image is allowed only as the response to this
            // area's current request. It inherits the repair deadline.
            if (area.Repair is not { } prior) return DigestResult.Ignore;
            Release(area);
            area.Last = digest;
            if (now - prior.StartedAt >= RepairDeadlineMs)
            {
                Reject(area);
                return DigestResult.TimedOut;
            }
            area.Repair = new(prior.StartedAt);
            return DigestResult.Reanchored;
        }
        area.Last ??= digest;
        var local = area.Receipt;
        if (Volatile.Read(ref _corrupted) == (int)digest.Section) local = local with { A = local.A ^ 1 };
        if (local == digest.Hash && Volatile.Read(ref area.BadReadbacks) < 2)
            return DigestResult.Match;
        if (area.Rejected) return DigestResult.Ignore;
        if (area.Repair is { } inProgress)
        {
            if (now - inProgress.StartedAt < RepairDeadlineMs) return DigestResult.Repeat;
            Reject(area);
            return DigestResult.TimedOut;
        }
        area.Repair = new(now);
        return DigestResult.Mismatch;
    }

    public PartResult Accept(WorldRecoveryChunk chunk, out WorldBaselineChunk[]? complete)
    {
        complete = null;
        var digest = chunk.Digest;
        if (!_areas.TryGetValue(digest.Section, out var area) || area.Last != digest || area.Repair is not { } pending)
            return PartResult.Ignore;
        pending.Parts ??= new byte[chunk.Count][];
        if (pending.Parts.Length != chunk.Count)
            return Reject(area);
        if (pending.Parts[chunk.Index] is { } existing)
            return existing.AsSpan().SequenceEqual(chunk.Payload) ? PartResult.Waiting : Reject(area);
        if (chunk.Payload.Length > WorldRecoveryWire.MaxSectionBytes - _bufferedBytes) return Reject(area);
        pending.Parts[chunk.Index] = chunk.Payload;
        _bufferedBytes += chunk.Payload.Length;
        pending.Bytes += chunk.Payload.Length;
        if (++pending.Filled != pending.Parts.Length) return PartResult.Waiting;
        var payloads = pending.Parts.Select(part => part!).ToArray();
        if (!WorldRecoveryWire.TryComputeHash(digest.Scope, digest.SnapshotId, digest.Section, payloads, out var hash) ||
            hash != digest.Hash) return Reject(area);
        complete = new WorldBaselineChunk[payloads.Length];
        for (ushort index = 0; index < payloads.Length; index++)
            complete[index] = new(digest.Scope, digest.SnapshotId, WorldSyncWire.BaselineVersion,
                digest.Section, index, chunk.Count, payloads[index]);
        return PartResult.Ready;
    }

    public void Finish(WorldRecoveryDigest digest, bool confirmed)
    {
        var area = _areas[digest.Section];
        if (!confirmed) { Reject(area); return; }
        area.Receipt = digest.Hash;
        lock (area)
        {
            area.ReadbackVersion++;
            Volatile.Write(ref area.BadReadbacks, 0);
        }
        Interlocked.CompareExchange(ref _corrupted, 0, (int)digest.Section);
        Release(area);
    }

    public bool BeginReadback(WorldBaselineSection section) => _areas.TryGetValue(section, out var area) &&
        Interlocked.CompareExchange(ref area.Reading, 1, 0) == 0;
    public ulong ReadbackRevision(WorldBaselineSection section) => (ulong)Interlocked.Read(ref _areas[section].ReadbackVersion);
    public void EndReadback(WorldBaselineSection section) => Volatile.Write(ref _areas[section].Reading, 0);
    public void NoteReadback(WorldBaselineSection section, ulong revision, bool known, bool drift)
    {
        var area = _areas[section];
        lock (area)
        {
            if ((ulong)area.ReadbackVersion != revision) return;
            int count = !known || !drift ? 0 : Math.Min(area.BadReadbacks + 1, 2);
            Volatile.Write(ref area.BadReadbacks, count);
        }
    }

    private PartResult Reject(Area area)
    {
        area.Rejected = true;
        Release(area);
        return PartResult.Rejected;
    }

    private void Release(Area area)
    {
        if (area.Repair is { } pending) _bufferedBytes -= pending.Bytes;
        area.Repair = null;
    }
}
