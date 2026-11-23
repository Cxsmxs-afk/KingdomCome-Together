// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
namespace KcdMp.Client;

/// <summary>
/// WO-127: the HOST CLAIM bit on this agent's Position packets
/// (Protocol.PositionFlagHostClaim, ProtocolWo127.cs). The relay makes a
/// claimant the authority whatever order people connected in, which closes
/// WO-124's carry-forward (a remote relay used to hand authority to the first
/// agent to connect) and keeps it with the host when joiners arrive over Steam.
///
/// This agent claims when:
///   * the launcher started the relay for it (--hosting), or
///   * mp_shared_world is on and this game is in a world of its own -- latched
///     from that moment so a load (the host reloading, _where = Loading) never
///     drops the claim mid-session; cleared when the toggle goes off or this
///     machine becomes a joiner (a join running, or in the host's world).
/// A guest can wait in a private world. Hearing a different host's session
/// mode fixes that role for the life of this relay link.
/// </summary>
public partial class GameBridge
{
    private bool _hostClaimLatched;
    private DateTime _w144ConnectedUtc = DateTime.MinValue;
    private volatile bool _foreignHostHeard;

    private void Wo127NoteHostMode(byte source)
    {
        if (source != _myGhostId) _foreignHostHeard = true;
    }

    private void Wo127BeginRelayLink()
    {
        _foreignHostHeard = false;
        _hostClaimLatched = false;
        _w144ConnectedUtc = DateTime.UtcNow;
    }

    private bool? _hostClaimLogged;

    private bool Wo127ClaimsHost()
    {
        bool claim;
        if (config.IsHosting) claim = true;
        else if (!_sharedWorld || _joinedWorld || _jj is not null || _foreignHostHeard) { _hostClaimLatched = false; claim = false; }
        else
        {
            bool inOwnWorld = _where == GameWhere.World || (_where == GameWhere.Unknown && !_startedAtMenu);
            // WO-144 5: the latch waits for the host's word first -- a host announces its session mode
            // to every new connection within a second or two; a joiner that hears it never claims
            if (inOwnWorld && Wo144Rules.ClaimGraceOver(_w144ConnectedUtc, DateTime.UtcNow)) _hostClaimLatched = true;
            claim = _hostClaimLatched;
        }
        if (_hostClaimLogged != claim)
        {
            _hostClaimLogged = claim;
            Console.WriteLine($"MP-HOST-CLAIM {(claim ? "on" : "off")} (hosting={(config.IsHosting ? 1 : 0)} shared_world={(_sharedWorld ? 1 : 0)} joined={(_joinedWorld ? 1 : 0)} where={_where})");
        }
        return claim;
    }
}
