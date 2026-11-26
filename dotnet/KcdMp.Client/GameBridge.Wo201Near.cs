// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Buffers.Binary;

namespace KcdMp.Client;

// WO-201 V.2 nearest_player_triggers (the host): a quest's distance trigger on "the player" measures from the nearer of the host's Henry
// and up to three bound partner figures. "Came near" fires when any of them does (DLL op 82, native/KCDMP/wo201_near.cpp).
// The agent checks the roster once a second and tells the DLL when it or the WO-204 quiet window changes.
// During a join or load the DLL answers with the host's own Henry. WO-204 Act I rebuilt the DLL's half
// (nearest_player_v2): the node is read, never written.
public partial class GameBridge
{
    private (uint First, uint Second, uint Third) _w201NearSent;
    private bool _w201NearQuietSent;
    private bool _w201NearRefused;

    private bool Wo201NearQuiet => Wo123HostJoinActive || _where == GameWhere.Loading;

    private void Wo201NearTick(long n)
    {
        if (!_w201Connected || n % 4 != 0 || _w201NearRefused) return;
        Span<uint> figures = stackalloc uint[3];
        figures.Clear();
        if (W137Host && W201("nearest_player_triggers"))
        {
            int count = 0, limit = W221On("escort_nearest_all") ? 3 : 1;
            foreach (var (_, eid) in _ghostEntityIds)
            {
                if (eid == 0 || figures[..count].Contains(eid)) continue;
                figures[count++] = eid;
                if (count == limit) break;
            }
            figures[..count].Sort();
        }
        var roster = (figures[0], figures[1], figures[2]);
        var quiet = roster.Item1 != 0 && Wo201NearQuiet;
        if (roster == _w201NearSent && quiet == _w201NearQuietSent) return;
        _w201NearSent = roster;
        _w201NearQuietSent = quiet;
        _ = Wo201NearSendAsync(roster, quiet);
    }

    private async Task Wo201NearSendAsync((uint First, uint Second, uint Third) roster, bool quiet = false)
    {
        bool active = roster.First != 0;
        var a = new byte[roster.Second != 0 ? 14 : 6];
        a[0] = (byte)(active ? 1 : 0);
        BinaryPrimitives.WriteUInt32LittleEndian(a.AsSpan(1), roster.First);
        a[5] = (byte)(quiet ? 1 : 0);
        if (a.Length == 14)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(a.AsSpan(6), roster.Second);
            BinaryPrimitives.WriteUInt32LittleEndian(a.AsSpan(10), roster.Third);
        }
        var r = await _combat.Wo163Async(Protocol.Wo201OpNear, a);
        if (r is { Ok: true } rr && rr.Payload.Length >= 9)
            Console.WriteLine($"WO201-NEAR figures={(active ? 1 + (roster.Second != 0 ? 1 : 0) + (roster.Third != 0 ? 1 : 0) : 0)} quiet={quiet} armed={rr.Payload[0]} overrides={BinaryPrimitives.ReadUInt32LittleEndian(rr.Payload.AsSpan(1))}");
        else
        {
            if (active) _w201NearRefused = true;   // not resolved here: one line, the triggers stay the host's Henry's this session
            Console.WriteLine($"WO201-NEAR refused by the DLL ({(r is { } x ? x.Reason : 255)}) -- the player's distance triggers stay the host's Henry's");
        }
    }

    private void Wo201NearReset()
    {
        if (_w201NearSent.First != 0) _ = Wo201NearSendAsync(default);
        _w201NearSent = default;
        _w201NearQuietSent = false;
    }
}
