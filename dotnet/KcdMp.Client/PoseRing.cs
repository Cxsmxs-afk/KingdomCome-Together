// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.IO.MemoryMappedFiles;

namespace KcdMp.Client;

/// <summary>
/// The DLL's pose ring (<c>Local\KCD2MP.PoseRing.1</c>). Layout is
/// <c>native/KCDMP/pose_ring.h</c>. A missing map or a different version
/// means the Lua console still carries poses.
/// </summary>
public static class PoseRing
{
    public const uint Magic = 0x33524E50;
    public const uint Version = 1;
    public const int Capacity = 64;
    public const int HeaderBytes = 24;
    public const int SlotBytes = 52;
    const string MapName = @"Local\KCD2MP.PoseRing.1";

    public static bool VersionMatches { get; private set; }
    public static string Reason { get; private set; } = "not-opened";

    public static void Probe()
    {
        if (!OperatingSystem.IsWindows()) { VersionMatches = false; Reason = "not-windows"; return; }
        try
        {
            using var map = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.Read);
            using var view = map.CreateViewAccessor(0, HeaderBytes + (long)Capacity * SlotBytes, MemoryMappedFileAccess.Read);
            uint magic = view.ReadUInt32(0);
            uint version = view.ReadUInt32(4);
            uint capacity = view.ReadUInt32(8);
            if (magic != Magic || version != Version || capacity != Capacity)
            {
                VersionMatches = false;
                Reason = $"version-{version}";
                return;
            }
            VersionMatches = true;
            Reason = "ok";
        }
        catch (FileNotFoundException)
        {
            VersionMatches = false;
            Reason = "missing";
        }
        catch
        {
            VersionMatches = false;
            Reason = "unreadable";
        }
    }

    /// <summary>The newest player sample, or false when the ring is not this build.</summary>
    public static bool TryPlayer(out float x, out float y, out float z, out float rot, out byte flags)
        => TryPlayer(out x, out y, out z, out rot, out flags, out _);

    /// <summary>The newest player sample. <paramref name="hostMs"/> is the DLL frame counter, not a wall clock.</summary>
    public static bool TryPlayer(out float x, out float y, out float z, out float rot, out byte flags, out uint hostMs)
    {
        x = y = z = rot = 0; flags = 0; hostMs = 0;
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var map = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.Read);
            using var view = map.CreateViewAccessor(0, HeaderBytes + (long)Capacity * SlotBytes, MemoryMappedFileAccess.Read);
            if (view.ReadUInt32(0) != Magic || view.ReadUInt32(4) != Version) return false;
            uint end = view.ReadUInt32(12);
            uint start = end > Capacity ? end - Capacity : 0;
            bool found = false;
            uint best = 0;
            for (uint i = start; i < end; i++)
            {
                long at = HeaderBytes + (long)(i % Capacity) * SlotBytes;
                uint seq = view.ReadUInt32(at);
                if ((seq & 1) != 0) continue;
                byte kind = view.ReadByte(at + 24);
                if (kind != 1) continue;
                uint host = view.ReadUInt32(at + 4);
                float px = view.ReadSingle(at + 8);
                float py = view.ReadSingle(at + 12);
                float pz = view.ReadSingle(at + 16);
                float pr = view.ReadSingle(at + 20);
                byte fl = view.ReadByte(at + 25);
                if (view.ReadUInt32(at) != seq) continue;
                x = px; y = py; z = pz; rot = pr; flags = fl; hostMs = host;
                best = i; found = true;
                _ = best;
            }
            return found;
        }
        catch { return false; }
    }
}
