// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
namespace KcdMp.Client;

/// <summary>Bounded latest-only side channels. A load/role/life change cancels both queued and in-flight work.</summary>
public sealed class T3SideChannelQueue<T>
{
    public const int SettleMs = 5000;
    public readonly record struct Lease(uint World, long Life, CancellationToken Token);
    public readonly record struct Work(byte Kind, T Value, Lease Lease);
    private readonly object _gate = new();
    private readonly Dictionary<byte, Work> _latest = new();
    private uint _world;
    private byte _lastKind = byte.MaxValue;
    private long _life, _readyAt = long.MaxValue;
    private CancellationTokenSource _cancel = new();

    public Lease Capture(uint world)
    {
        lock (_gate) { if (_world != world) Reset(world); return new(_world, _life, _cancel.Token); }
    }
    public void Invalidate(uint world, bool preserveGameplay = false)
    {
        lock (_gate) { long ready = _readyAt; bool same = _world == world; Reset(world); if (preserveGameplay && same) _readyAt = ready; }
    }
    private void Reset(uint world)
    {
        var old = _cancel; _cancel = new();
        _ = old.CancelAsync().ContinueWith(t => { _ = t.Exception; old.Dispose(); }, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        _world = world; _life++; _readyAt = long.MaxValue; _lastKind = byte.MaxValue; _latest.Clear();
    }
    public void GameplayStarted(uint world, long nowMs)
    {
        lock (_gate) { if (_world != world) Reset(world); _readyAt = nowMs + SettleMs; }
    }
    public bool IsCurrent(Lease lease)
    {
        lock (_gate) return lease.World == _world && lease.Life == _life && !lease.Token.IsCancellationRequested;
    }
    public bool Ready(Lease lease, long nowMs)
    {
        lock (_gate) return IsCurrent(lease) && nowMs >= _readyAt;
    }
    public bool Put(byte kind, T value, Lease lease)
    {
        lock (_gate)
        {
            if (!IsCurrent(lease)) return false;
            _latest[kind] = new(kind, value, lease); return true;
        }
    }
    public Work[] Take(uint world, long nowMs, bool worldWritable)
    {
        lock (_gate)
        {
            if (!worldWritable || world != _world || nowMs < _readyAt) return [];
            // One bounded worker turn: a hung clock must not discard the pending weather with it.
            if (_latest.Count == 0) return [];
            byte kind = _latest.Keys.Where(k => k > _lastKind).OrderBy(k => k).DefaultIfEmpty(_latest.Keys.Min()).First();
            var work = _latest[kind]; _latest.Remove(kind); _lastKind = kind; return [work];
        }
    }
    public int Count { get { lock (_gate) return _latest.Count; } }
}
