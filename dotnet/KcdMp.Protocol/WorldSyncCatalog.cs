// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Globalization;
using System.Text;

namespace KcdMp.Wire;

public static class WorldSyncCatalog
{
    public static bool TryUnwrap(byte[] packet, out byte type, out ReadOnlySpan<byte> body)
    {
        type = 0;
        body = default;
        if (packet.Length < 4) return false;
        type = packet[0];
        body = packet.AsSpan(3);
        if (type is not (Protocol.PresentationActionDown or Protocol.PresentationPoseDown)) return true;
        if (!PresentationTimeWire.TryRead(type, body, out _, out byte original, out int offset)) return false;
        type = original;
        body = body[offset..];
        return true;
    }

    public static WorldEventChannel? EventChannel(int type, ReadOnlySpan<byte> body)
    {
        // Each listed kind has a world receiver with a positive application
        // receipt. Control traffic, frame samples and requests stay on their
        // normal routes.
        if (type == Protocol.DeathMarkerDown) return WorldEventChannel.Corpse;
        if (body.Length <= 1 + Protocol.JoinHeaderLen) return null;
        byte kind = body[1 + Protocol.JoinHeaderLen];
        if (type == Protocol.ConvDown && kind == Protocol.ConvVote) return WorldEventChannel.Dialog;
        if (type == Protocol.LootHostDown)
        {
            if (kind is Protocol.LootHostItemResult or Protocol.LootHostItemGone) return WorldEventChannel.Loot;
            if (kind is Protocol.LootHostCorpses or Protocol.LootHostTakeVerdict) return WorldEventChannel.Corpse;
        }
        return null;
    }

    public static bool TryBaseline(int type, ReadOnlySpan<byte> body, byte authority, out WorldBaselineSection section, out string key)
    {
        section = default;
        key = "";
        if (body.Length < 2) return false;
        if (type is Protocol.Ghost or Protocol.PlayerStateDown)
        {
            section = WorldBaselineSection.Body;
            key = FormattableString.Invariant($"{type:D3}/{body[0]:D3}");
            return true;
        }
        if (body[0] != authority) return false;
        if (type == Protocol.NpcStateDown)
        {
            int len = body[1];
            if (len == 0 || body.Length != 2 + len + Protocol.NpcStateFixedTail) return false;
            section = WorldBaselineSection.Npc;
            key = Encoding.UTF8.GetString(body.Slice(2, len));
            return true;
        }
        if (type == Protocol.WeatherDown) { section = WorldBaselineSection.Weather; key = "profile"; return true; }
        if (body.Length <= 1 + Protocol.JoinHeaderLen || !LootMsg.TryDecode(body[(1 + Protocol.JoinHeaderLen)..], out var row)) return false;
        if (type == Protocol.W164Down && row.Kind == Protocol.W164Clock)
        { section = WorldBaselineSection.Clock; key = "calendar"; return true; }
        if (type == Protocol.W164Down && row.Kind == Protocol.W164Weather)
        { section = WorldBaselineSection.Weather; key = "rain"; return true; }
        if (type == Protocol.LootHostDown && row.Kind == Protocol.LootHostCorpses)
        { section = WorldBaselineSection.Corpse; key = "census"; return true; }
        if (type == Protocol.LootHostDown && row.Kind == Protocol.LootHostLedger &&
            TryLedgerPart(row.Text, out int part, out _))
        { section = WorldBaselineSection.Loot; key = part.ToString("D4", CultureInfo.InvariantCulture); return true; }
        return false;
    }

    public static bool TryLedgerPart(string text, out int part, out int count)
    {
        part = count = 0;
        ReadOnlySpan<char> remaining = text.AsSpan().TrimStart();
        int first = remaining.IndexOf(' ');
        if (first <= 0 || !int.TryParse(remaining[..first], NumberStyles.None, CultureInfo.InvariantCulture, out part)) return false;
        remaining = remaining[(first + 1)..].TrimStart();
        int second = remaining.IndexOf(' ');
        return int.TryParse(second < 0 ? remaining : remaining[..second], NumberStyles.None, CultureInfo.InvariantCulture, out count) &&
            part >= 1 && count >= part && count <= 512;
    }
}
