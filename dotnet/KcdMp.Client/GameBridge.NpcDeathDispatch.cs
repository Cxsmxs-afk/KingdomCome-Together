// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Text;

namespace KcdMp.Client;

public partial class GameBridge
{
    private long _npcDeathLife;

    private void ResetNpcDeathDispatch()
    {
        Interlocked.Increment(ref _npcDeathLife);
        _npcDeathSent.Reset();
        _npcDeathAppliedUtc.Clear();
    }

    // An owner-death retry may bypass a completed mark only if the current
    // body actually lives again. Repeated "owner dead" messages alone cannot.
    private async Task<bool> NpcDeathBodyReadsAliveAsync(string npc)
    {
        byte[] name = Encoding.ASCII.GetBytes(npc);
        if (name.Length > 63) return false;
        var args = new byte[5 + name.Length];
        args[4] = (byte)name.Length;
        name.CopyTo(args, 5);
        try
        {
            var r = await _combat.Wo163Async(KcdMp.Wire.Protocol.Wo195OpCheck, args);
            return r is { Ok: true, Payload.Length: >= 4 } x && x.Payload[0] == 0;
        }
        catch { return false; }
    }
}
