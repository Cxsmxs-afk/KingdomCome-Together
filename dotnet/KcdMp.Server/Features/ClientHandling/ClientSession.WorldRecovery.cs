// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Globalization;
using System.Text;
using KcdMp.Wire;

namespace KcdMp.Server.Features.ClientHandling;

public partial class ClientSession
{
    private const long WorldDigestIntervalMs = 10_000, WorldRepairIntervalMs = 350;
    private sealed class RecoverySection(WorldRecoveryDigest digest, byte[][] rows)
    {
        public WorldRecoveryDigest Digest = digest;
        public WorldRecoveryDigest? Previous;
        public byte[][] Rows = rows;
        public bool Advertised;
        public long NextRequestAt, ExpiresAt = long.MaxValue;
        public int NextRow = -1;
    }

    private volatile bool _linkWorldRecovery;
    private readonly List<RecoverySection> _worldRecovery = [];
    private long _worldDigestAt;

    private void ClearWorldRecoveryLocked()
    {
        _worldRecovery.Clear();
        _worldDigestAt = 0;
    }

    // This image is the ordered baseline that this recipient specifically
    // acknowledged. The mutable observation cache is not an applied image.
    private void AnchorWorldRecoveryLocked(WorldBaselineSection section, byte[][] rows)
    {
        if (!_linkWorldRecovery ||
            !WorldRecoveryWire.TryComputeHash(_worldSyncScope, _worldSnapshot, section, rows, out var hash)) return;
        _worldRecovery.Add(new(new(_worldSyncScope, _worldSnapshot, section, 1, hash), rows));
    }

    private bool HandleWorldRepair(byte[] body)
    {
        if (!_linkWorldRecovery || !WorldRecoveryWire.TryReadRequest(body, out var request)) return false;
        lock (_worldSyncGate)
        {
            if (!_worldBaselineApplied || request.Expected.Scope != _worldSyncScope ||
                request.Expected.SnapshotId != _worldSnapshot) return false;
            var area = _worldRecovery.Find(section => section.Digest.Section == request.Expected.Section);
            if (area is null) return false;
            long now = Environment.TickCount64;
            if (area.Previous == request.Expected && now < area.NextRequestAt) return true;
            if (!area.Advertised || area.Digest != request.Expected) return false;
            if (area.NextRow >= 0 || now < area.NextRequestAt) return true;
            area.NextRequestAt = now + WorldRepairIntervalMs;
            if (!RefreshWorldRecoveryLocked(area, now))
            {
                _logger.Debug("MP-WORLD-RECOVERY relay world={World} gen={Gen} snapshot={Snapshot} section={Section} target={Target} result=current-source-unavailable",
                    _worldSyncScope.HostWorld, _worldSyncScope.Generation, _worldSnapshot, area.Digest.Section, Id);
                return true;
            }
            area.NextRow = 0;
            _logger.Information("MP-WORLD-RECOVERY relay world={World} gen={Gen} snapshot={Snapshot} section={Section} revision={Revision} target={Target} result=repair-requested",
                _worldSyncScope.HostWorld, _worldSyncScope.Generation, _worldSnapshot, area.Digest.Section, area.Digest.Revision, Id);
            return true;
        }
    }

    private bool RefreshWorldRecoveryLocked(RecoverySection area, long now)
    {
        var section = area.Digest.Section;
        if (section == WorldBaselineSection.Loot) return true; // committed ledger rows are relative, never rerun/refreshed

        long freshFor = section switch
        {
            WorldBaselineSection.Body or WorldBaselineSection.Npc => 2_000,
            WorldBaselineSection.Clock => 9_000,
            _ => long.MaxValue, // weather profile and corpse census are lasting state
        };
        var rows = _worldImage.Where(row => row.Key.Section == section && RecoveryRowCurrent(row.Value, now, freshFor))
            .OrderBy(row => section == WorldBaselineSection.Weather ? row.Value.ReceivedAt : 0)
            .ThenBy(row => section == WorldBaselineSection.Weather ? row.Value.ArrivalOrder : 0)
            .ThenBy(row => row.Key.Key, StringComparer.Ordinal).Select(row => row.Value).ToArray();
        if (section == WorldBaselineSection.Weather) rows = MergeRecoveryWeather(rows);
        // Missing live samples are not proof that the original body/clock should be cleared.
        if (rows.Length == 0)
            return area.Rows is [{ Length: 0 }];
        if (section == WorldBaselineSection.Body && area.Rows.Any(row => row.Length != 0 && row[0] == Protocol.Ghost) &&
            !rows.Any(row => row.Payload[0] == Protocol.Ghost)) return false;
        byte[][] payloads = rows.Select(row => row.Payload).ToArray();
        int count = payloads.Length, bytes = payloads.Sum(row => row.Length);
        foreach (var other in _worldRecovery)
        {
            if (ReferenceEquals(other, area)) continue;
            count += other.Rows.Length;
            bytes += other.Rows.Sum(row => row.Length);
        }
        if (count > WorldCacheMaxRows + 6 || bytes > WorldCacheMaxBytes ||
            !WorldRecoveryWire.TryComputeHash(_worldSyncScope, _worldSnapshot, section, payloads, out var hash)) return false;
        area.ExpiresAt = freshFor == long.MaxValue ? long.MaxValue :
            rows.Where(row => row.Payload[0] != Protocol.PlayerStateDown).Select(row => row.ReceivedAt + freshFor).DefaultIfEmpty(long.MaxValue).Min();
        if (hash == area.Digest.Hash) return true;
        if (area.Digest.Revision == ulong.MaxValue) return false;
        area.Previous = area.Digest;
        area.Digest = area.Digest with { Revision = area.Digest.Revision + 1, Hash = hash };
        area.Advertised = false;
        area.Rows = payloads;
        return true;
    }

    private static WorldImageRow[] MergeRecoveryWeather(WorldImageRow[] rows)
    {
        if (rows is not [var older, var newest]) return rows;
        if (ReadWeatherParts(newest, out _, out string? latestProfile) && latestProfile is not null)
            return [newest]; // one current packet covers both the profile and rain
        if (newest.Payload[0] == Protocol.WeatherDown &&
            ReadWeatherParts(older, out float rain, out string? oldProfile) && oldProfile is not null)
        {
            // The latest native profile supersedes the profile component of
            // the older W164 sample; that sample still owns the latest rain.
            byte[] text = Encoding.ASCII.GetBytes(FormattableString.Invariant($"{rain:R} -"));
            return [older with { Payload = [.. older.Payload.AsSpan(0, 2 + Protocol.JoinHeaderLen + Protocol.LootFixedLen), .. text] }, newest];
        }
        return rows;
    }

    private static bool ReadWeatherParts(WorldImageRow row, out float rain, out string? profile)
    {
        rain = 0;
        profile = null;
        return row.Payload[0] == Protocol.W164Down &&
            LootMsg.TryDecode(row.Payload.AsSpan(2 + Protocol.JoinHeaderLen), out var message) &&
            message.Kind == Protocol.W164Weather && W164Text.TryParseWeather(message.Text, out rain, out profile);
    }

    private static bool RecoveryRowCurrent(WorldImageRow row, long now, long freshFor)
    {
        byte[] payload = row.Payload;
        byte type = payload[0];
        // Vitals are a changed-state record and may be older than the position
        // stream. A stale-marked pose is never an absolute correction.
        if (type != Protocol.PlayerStateDown && now - row.ReceivedAt >= freshFor) return false;
        if (type == Protocol.Ghost)
            return Protocol.IsGhostPayloadLen(payload.Length - 1) &&
                (payload[18] & Protocol.PositionFlagStale) == 0 &&
                float.IsFinite(BitConverter.ToSingle(payload, 2)) && float.IsFinite(BitConverter.ToSingle(payload, 6)) &&
                float.IsFinite(BitConverter.ToSingle(payload, 10)) && float.IsFinite(BitConverter.ToSingle(payload, 14));
        if (type == Protocol.NpcStateDown)
            return NpcCompact.TryReadDown(payload.AsSpan(1), out _, out _, out var state) &&
                float.IsFinite(state.X) && float.IsFinite(state.Y) && float.IsFinite(state.Z) &&
                float.IsFinite(state.Rot) && float.IsFinite(state.Hp);
        if (type == Protocol.PlayerStateDown)
            return payload.Length == Protocol.PlayerStateDownPayloadLen + 1 &&
                float.IsFinite(BitConverter.ToSingle(payload, 2)) && float.IsFinite(BitConverter.ToSingle(payload, 6));
        if (type == Protocol.WeatherDown)
            return payload.Length >= 6 && payload[2] > 0 && payload[2] <= Protocol.MaxWeatherNameLen &&
                payload.Length == payload[2] + 5 && payload[3..(3 + payload[2])].All(b => char.IsAsciiLetterOrDigit((char)b) || b == '_');
        if (type == Protocol.W164Down)
        {
            if (!LootMsg.TryDecode(payload.AsSpan(2 + Protocol.JoinHeaderLen), out var message)) return false;
            return message.Kind == Protocol.W164Weather
                ? W164Text.TryParseWeather(message.Text, out _, out _)
                : message.Kind == Protocol.W164Clock && ValidRecoveryClock(message.Text);
        }
        return true;
    }

    private static long WorldObservationTime(byte[] packet)
    {
        long now = Environment.TickCount64;
        if (packet[0] != Protocol.PresentationPoseDown ||
            !PresentationTimeWire.TryRead(packet[0], packet.AsSpan(3), out var stamp, out _, out _)) return now;
        // A calibrated, queued old sample cannot become current just because
        // the relay has now observed it.
        double age = (double)PresentationClock.NowMs() + stamp.RelayToHostMs - stamp.HostMs;
        return now - (age < -1000 ? 3000 : (long)Math.Clamp(age, 0, int.MaxValue));
    }

    private static bool ValidRecoveryClock(string text)
    {
        var fields = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length == 6 && fields[0] == "b1" &&
            long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out long world) && world is >= 0 and <= (long)uint.MaxValue * 1000 &&
            int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out int ratio) && ratio is > 0 and <= 15000 &&
            long.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out long relayMs) && relayMs >= 0 &&
            (fields[4] == "0" || fields[4] == "1" && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - relayMs is >= -1000 and <= 9000) &&
            uint.TryParse(fields[5], NumberStyles.None, CultureInfo.InvariantCulture, out uint joinId) && joinId == 0;
    }

    private void SendWorldRecoveryLocked(long now)
    {
        if (!_linkWorldRecovery || !_worldBaselineApplied) return;
        if (now >= _worldDigestAt)
        {
            _worldDigestAt = now + WorldDigestIntervalMs;
            foreach (var area in _worldRecovery)
                if (EnqueueWire(WorldRecoveryWire.BuildDigest(area.Digest))) area.Advertised = true;
        }

        int budget = 12;
        foreach (var area in _worldRecovery)
        {
            if (budget == 0) break;
            if (area.NextRow < 0) continue;
            if (now >= area.ExpiresAt) { area.NextRow = -1; continue; }
            if (!area.Advertised)
            {
                if (!EnqueueWire(WorldRecoveryWire.BuildDigest(area.Digest))) return;
                area.Advertised = true;
            }
            // Keep an active repair bounded without taking the other areas'
            // turns or altering the ordered event window.
            for (int sent = 0; sent < 4 && budget > 0 && area.NextRow >= 0; sent++)
            {
                if (_sched is not null && _sched.EventBytes > 64 * 1024) return;
                ushort row = checked((ushort)area.NextRow);
                if (!EnqueueWire(WorldRecoveryWire.BuildChunk(new(area.Digest, row, checked((ushort)area.Rows.Length), area.Rows[row]))))
                    return;
                budget--;
                if (++area.NextRow >= area.Rows.Length) area.NextRow = -1;
            }
        }
    }
}
