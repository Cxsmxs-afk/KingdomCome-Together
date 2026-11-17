// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Buffers.Binary;
using KcdMp.Wire;

namespace KcdMp.Client;

public enum FaderOwner : byte { VideoLanding = 2, Wake = 3, Harness = 4 }

public static class OwnedFader
{
    private static int _next = Random.Shared.Next(1, int.MaxValue);
    public static uint NextToken()
    {
        uint token = unchecked((uint)Interlocked.Increment(ref _next));
        return token != 0 ? token : unchecked((uint)Interlocked.Increment(ref _next));
    }
    public static byte[] Command(bool fadeOut, float seconds, FaderOwner owner, uint token)
    {
        if (token == 0 || owner is not (FaderOwner.VideoLanding or FaderOwner.Wake or FaderOwner.Harness))
            throw new ArgumentException("A fader needs a known owner and nonzero lease");
        var body = new byte[10];
        body[0] = fadeOut ? (byte)1 : (byte)0;
        BitConverter.GetBytes(seconds).CopyTo(body, 1);
        body[5] = (byte)owner;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(6), token);
        return body;
    }
    public static bool Acquired(bool ok, byte[] payload) => ok && payload.Length > 0 && payload[0] == 1;
}

public partial class GameBridge
{
    private async Task<bool> OwnedFadeAsync(bool fadeOut, float seconds, FaderOwner owner, uint token)
    {
        var r = await _combat.Wo163Async(Protocol.Wo179OpFade, OwnedFader.Command(fadeOut, seconds, owner, token));
        return r is { Ok: true } response && OwnedFader.Acquired(true, response.Payload);
    }
}
