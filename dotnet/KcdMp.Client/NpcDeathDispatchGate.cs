// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
namespace KcdMp.Client;

/// <summary>One host death announcement per body life, shared by the DLL and Lua observers.</summary>
public sealed class NpcDeathDispatchGate
{
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _sentOrPending = new(StringComparer.Ordinal);
    private readonly HashSet<string> _deadSamples = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _aliases = new(StringComparer.Ordinal);
    private long _ticket;

    public static bool MayAnnounce(bool roleApplied, bool authority) => roleApplied && authority;

    public bool TryBegin(string npc, bool roleApplied, bool authority, out long ticket, string? alias = null)
    {
        lock (_gate)
        {
            ticket = 0;
            if (!MayAnnounce(roleApplied, authority)) return false;
            if (alias is not null) _aliases[npc] = alias;
            if (_sentOrPending.ContainsKey(npc) || (alias is not null && _sentOrPending.ContainsKey(alias))) return false;
            ticket = ++_ticket;
            _sentOrPending[npc] = ticket;
            if (alias is not null) _sentOrPending[alias] = ticket;
            return true;
        }
    }

    public void Complete(string npc, long ticket, bool succeeded)
    {
        lock (_gate)
        {
            // A late completion belongs to its old world/attempt, never a new one.
            if (!succeeded)
                foreach (string key in _sentOrPending.Where(kv => kv.Value == ticket).Select(kv => kv.Key).ToArray())
                    _sentOrPending.Remove(key);
        }
    }

    public void NoteState(string npc, bool dead)
    {
        lock (_gate)
        {
            if (dead) _deadSamples.Add(npc);
            else if (_deadSamples.Remove(npc))
            {
                _sentOrPending.Remove(npc);
                if (_aliases.TryGetValue(npc, out string? alias)) _sentOrPending.Remove(alias);
            }
        }
    }

    public void Reset() { lock (_gate) { _sentOrPending.Clear(); _deadSamples.Clear(); _aliases.Clear(); } }
}
