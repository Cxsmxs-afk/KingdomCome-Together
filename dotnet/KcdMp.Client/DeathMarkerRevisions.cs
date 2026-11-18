// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using KcdMp.Wire;
namespace KcdMp.Client;

/// <summary>Remove tombstones survive snapshot replay. Applied is separate so a cancelled native write can be retried.</summary>
public sealed class DeathMarkerRevisions
{
    private sealed record Entry(DeathMarkerFact Fact, bool Applied);
    private readonly Dictionary<(byte Owner, uint World, ulong Id), Entry> _entries = new();
    private readonly object _gate = new();
    public bool Observe(byte owner, DeathMarkerFact fact)
    {
        lock (_gate)
        {
            var key = (owner, fact.World, fact.Id);
            if (_entries.TryGetValue(key, out var old))
            {
                if (old.Fact.Revision > fact.Revision || (old.Fact.Revision == fact.Revision && (old.Applied || old.Fact != fact))) return false;
            }
            else if (_entries.Count >= 4096) return false;
            _entries[key] = new(fact, false); return true;
        }
    }
    public DeathMarkerFact? Latest(byte owner, uint world, ulong id)
    { lock (_gate) return _entries.TryGetValue((owner, world, id), out var e) ? e.Fact : null; }
    public bool Current(byte owner, DeathMarkerFact fact)
    { lock (_gate) return _entries.TryGetValue((owner, fact.World, fact.Id), out var e) && e.Fact == fact; }
    public void Applied(byte owner, DeathMarkerFact fact)
    { lock (_gate) if (Current(owner, fact)) _entries[(owner, fact.World, fact.Id)] = new(fact, true); }
    public void Clear(byte owner = 255)
    {
        lock (_gate)
            foreach (var k in _entries.Keys.Where(k => owner == 255 || k.Owner == owner).ToArray()) _entries.Remove(k);
    }
}
