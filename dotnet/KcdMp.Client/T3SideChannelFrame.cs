// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using KcdMp.Wire;

namespace KcdMp.Client;

public readonly record struct T3SideChannelFrame(byte Source, byte Kind, string Text, float? Rain, string? Profile, ushort Blend)
{
    public static bool TryDecode(int type, byte[] payload, ushort defaultBlend, out T3SideChannelFrame frame)
    {
        frame = default;
        if (type == Protocol.W164Down)
        {
            if (!Protocol.TrySplitJoinDown(payload, out byte source, out _, out _, out var body) || !LootMsg.TryDecode(body, out var msg)) return false;
            if (msg.Kind == Protocol.W164Clock) { frame = new(source, msg.Kind, msg.Text, null, null, 0); return true; }
            if (msg.Kind != Protocol.W164Weather || !W164Text.TryParseWeather(msg.Text, out float rain, out string? profile)) return false;
            frame = new(source, msg.Kind, msg.Text, rain, profile, defaultBlend); return true;
        }
        if (type != Protocol.WeatherDown || payload.Length < 4 || payload[1] > Protocol.MaxWeatherNameLen || payload.Length != 4 + payload[1]) return false;
        string name = Encoding.UTF8.GetString(payload, 2, payload[1]);
        if (!Regex.IsMatch(name, @"^[A-Za-z0-9_]+$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))) return false;
        frame = new(payload[0], Protocol.W164Weather, "", null, name, BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(2 + payload[1])));
        return true;
    }
}
