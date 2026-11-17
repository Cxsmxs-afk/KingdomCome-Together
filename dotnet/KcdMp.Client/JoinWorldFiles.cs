// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
namespace KcdMp.Client;

/// <summary>A verified world survives failed attempts. Only join=in or the player's cancel authorizes removal.
/// Authorization still protects it from sweeps until the engine has finished reading it and cleanup succeeds.</summary>
public sealed class JoinWorldFiles<T> where T : class
{
    private sealed record Entry(T Owner, bool Released);
    private readonly Dictionary<string, Entry> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    public int Count { get { lock (_gate) return _files.Count; } }

    public void Keep(string path, T owner)
    {
        lock (_gate) _files[Path.GetFullPath(path)] = new(owner, false);
    }

    public bool Protects(string path)
    {
        lock (_gate) return _files.ContainsKey(Path.GetFullPath(path));
    }

    public bool MayRemove(string path, T owner)
    {
        lock (_gate) return _files.TryGetValue(Path.GetFullPath(path), out var e) && ReferenceEquals(e.Owner, owner) && e.Released;
    }

    public T[] End(bool joined, bool cancelled)
    {
        lock (_gate)
        {
            if (!joined && !cancelled) return [];
            var owners = _files.Values.Select(e => e.Owner).Distinct().ToArray();
            foreach (var path in _files.Keys.ToArray()) _files[path] = _files[path] with { Released = true };
            return owners;
        }
    }

    public void Cancel(T owner)
    {
        lock (_gate)
            foreach (var path in _files.Where(p => ReferenceEquals(p.Value.Owner, owner)).Select(p => p.Key).ToArray())
                _files[path] = _files[path] with { Released = true };
    }

    /// <summary>A stale cleanup cannot delete a newer lease of the same path. Failed deletion keeps sweep protection.</summary>
    public bool TryDelete(string path, T owner)
    {
        lock (_gate)
        {
            string full = Path.GetFullPath(path);
            if (!_files.TryGetValue(full, out var e) || !ReferenceEquals(e.Owner, owner) || !e.Released) return false;
            File.Delete(full);
            _files.Remove(full);
            return true;
        }
    }
}

/// <summary>Retry discovery of the same placed file, never another transfer or splice. A late rescan is followed by
/// cheap find-only requests; only a responsive list still missing the file may request another full rescan.</summary>
public static class JoinSaveList
{
    public const int MaxAttempts = 12;
    public static async Task<CombatPipe.SaveListReport?> WaitAsync(
        Func<byte, CancellationToken, Task<CombatPipe.SaveListReport?>> query,
        Func<bool> active,
        CancellationToken ct = default,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        delay ??= Task.Delay;
        byte op = 1;
        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (!active()) return null;
            var report = await query(op, ct);
            if (!active()) return null;
            ct.ThrowIfCancellationRequested();
            if (report is { Listed: true }) return report;
            op = report is not null && attempt % 3 == 2 ? (byte)1 : (byte)3;
            if (attempt + 1 < MaxAttempts) await delay(TimeSpan.FromSeconds(1), ct);
        }
        return null;
    }
}
