// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Globalization;
using KcdMp.Wire;

namespace KcdMp.Client;

public partial class GameBridge
{
    private bool WorldBaselineCurrent(in InFrame frame, CancellationToken ct) =>
        !ct.IsCancellationRequested && frame.LoadScoped && frame.Stamp != 0 && frame.Stamp == _w206.World &&
        LoadSequenceWritable && LoadSequenceFrameCurrent(frame) && _where == GameWhere.World;

    private static bool TryWorldLootFrame(ReadOnlySpan<byte> payload, out byte source, out LootMsg message)
    {
        message = default;
        return Protocol.TrySplitJoinDown(payload, out source, out _, out _, out var body) &&
            LootMsg.TryDecode(body, out message);
    }

    private async Task<bool> ApplyWorldBaselineAsync(WorldBaselineChunk item, InFrame frame, CancellationToken ct)
    {
        if (item.Scope.HostWorld != frame.Stamp || item.Scope.Generation != frame.LoadGeneration ||
            !WorldBaselineCurrent(frame, ct)) return false;
        if (item.Payload.Length == 0) return true; // the authority has no rows for this section
        switch (item.Section)
        {
            case WorldBaselineSection.Body when frame.Type == Protocol.Ghost &&
                    PositionCodec.TryDecodeGhost(frame.Payload, out var ghost) && !ghost.IsStale:
                if (!WorldBaselinePose.Finite(ghost.X, ghost.Y, ghost.Z, ghost.RotZ)) return false;
                FeedNativeAtRead(frame.Type, frame.Payload, frame.Arrival);
                if (!await ApplyWorldStateFrameAsync(frame, ct)) return false;
                return await ConfirmWorldBodyAsync(frame, "ghost", ghost.GhostId.ToString(CultureInfo.InvariantCulture),
                    ghost.X, ghost.Y, ghost.Z, ghost.RotZ, null, null, null, ct);
            case WorldBaselineSection.Body when frame.Type == Protocol.PlayerStateDown &&
                    frame.Payload.Length == Protocol.PlayerStateDownPayloadLen:
                return await ApplyWorldVitalsAsync(frame, ct);
            case WorldBaselineSection.Npc when frame.Type == Protocol.NpcStateDown &&
                    NpcCompact.TryReadDown(frame.Payload, out byte source, out string name, out var npc) && NpcNamePattern.IsMatch(name):
                if (!WorldBaselinePose.Finite(npc.X, npc.Y, npc.Z, npc.Rot, npc.Hp)) return false;
                // The legacy state row can contain a one-shot swing cue. A
                // sampled baseline represents the body only.
                if ((npc.Flags & 0x08) != 0)
                    frame = frame with { Payload = NpcCompact.BuildDown(source, name, npc with { Flags = (byte)(npc.Flags & ~0x08) }) };
                FeedNativeAtRead(frame.Type, frame.Payload, frame.Arrival);
                if (!await ApplyWorldStateFrameAsync(frame, ct)) return false;
                return await ConfirmWorldBodyAsync(frame, "npc", name, npc.X, npc.Y, npc.Z, npc.Rot,
                    (npc.Flags & Protocol.NpcStateFlagDead) != 0, (npc.Flags & Protocol.NpcStateFlagUnconscious) != 0,
                    npc.Hp >= 0 ? npc.Hp : null, ct);
            case WorldBaselineSection.Corpse:
                return await ApplyWorldCorpsesAsync(frame, ct);
            case WorldBaselineSection.Loot:
                return await ConfirmWorldLedgerAsync(item, frame, ct);
            case WorldBaselineSection.Clock when T3SideChannelFrame.TryDecode(frame.Type, frame.Payload, WeatherBlendSeconds, out var clock) &&
                    clock.Kind == Protocol.W164Clock && T3SideSource(clock.Source):
                return await ApplyWorldClockAsync(frame, clock, ct);
            case WorldBaselineSection.Weather when T3SideChannelFrame.TryDecode(frame.Type, frame.Payload, WeatherBlendSeconds, out var weather) &&
                    weather.Kind == Protocol.W164Weather && T3SideSource(weather.Source):
                return await ApplyWorldWeatherAsync(frame, weather, ct);
            default:
                return false;
        }
    }

    private async Task<bool> ApplyWorldStateFrameAsync(InFrame frame, CancellationToken ct)
    {
        if (!WorldBaselineCurrent(frame, ct)) return false;
        var result = await ProcessorFrameScope.RunAsync(token => ProcessFrameCoreAsync(frame, token, null), 1800, ct);
        return result.Status == ProcessorFrameScope.Outcome.Completed && WorldBaselineCurrent(frame, ct);
    }

    private async Task<bool> ApplyWorldClockAsync(InFrame frame, T3SideChannelFrame clock, CancellationToken ct)
    {
        if (CacheHostCalendar(clock.Source, clock.Text) is not { } state) return false;
        bool corrected = false;
        var result = await ProcessorFrameScope.RunAsync(async _ =>
        {
            corrected = await UpdateHostCalendarAsync(state.Sample, state.Generation);
        }, 1800, ct);
        if (result.Status != ProcessorFrameScope.Outcome.Completed || !corrected || !WorldBaselineCurrent(frame, ct))
            return false;
        if (await _combat.CalendarSnapshotAsync(ct) is not { } observation || !WorldBaselineCurrent(frame, ct))
            return false;
        long target = state.Sample.TargetMs(Environment.TickCount64);
        return Math.Abs(observation.WorldMs - target) <= 2000 && Math.Abs(observation.Ratio - state.Sample.Ratio) <= 0.025;
    }

    private async Task<bool> StageWorldBaselineAsync(IReadOnlyList<WorldBaselineChunk> items, CancellationToken ct)
    {
        foreach (var chunk in items)
            if (chunk.Section == WorldBaselineSection.Loot && chunk.Payload.Length == 0) return true;
        var parts = new SortedDictionary<int, List<Wo134Rules.Entry>>();
        var texts = new SortedDictionary<int, string>();
        int total = 0;
        (WorldSyncScope Scope, uint Snapshot) key = default;
        foreach (var chunk in items.Where(i => i.Section == WorldBaselineSection.Loot))
        {
            ct.ThrowIfCancellationRequested();
            if (chunk.Payload.Length < 2 || chunk.Payload[0] != Protocol.LootHostDown ||
                !TryWorldLootFrame(chunk.Payload.AsSpan(1), out byte source, out var row) ||
                !T3SideSource(source) || row.Kind != Protocol.LootHostLedger ||
                Wo134Rules.ParseLedgerPart(row.Text) is not { } part || !parts.TryAdd(part.Part, part.Rows) ||
                (total != 0 && total != part.N)) return false;
            total = part.N;
            texts.Add(part.Part, row.Text);
            key = (chunk.Scope, chunk.SnapshotId);
        }
        if (parts.Count == 0 || parts.Count != total || key.Scope.HostWorld != _w206.World ||
            key.Scope.Generation != _worldLoad.Current.Generation || !LoadSequenceWritable) return false;
        lock (_w134Gate)
        {
            if (_w134BaselineLedger is { } previous)
            {
                // Chest reconciliation is relative to the loaded inventory.
                // Reuse the original epoch/serial only for those exact deltas.
                if (previous.Scope != key.Scope || (previous != key && !WorldRecoveryEnabled) ||
                    _w134BaselineLedgerParts is not { } alreadyStaged ||
                    !alreadyStaged.SequenceEqual(texts.Values, StringComparer.Ordinal)) return false;
                _w134BaselineLedger = key;
                return true;
            }
            if (_w134ApplyTag is null || _w134BaselineLedgerWork is not null) return false;
            var ledger = new Wo134Rules.Ledger();
            foreach (var part in parts.Values) ledger.Entries.AddRange(part);
            _w134BaselineLedger = key;
            _w134BaselineLedgerParts = texts.Values.ToArray();
            _w134RxLedger = ledger;
            _w134RxStamp = key.Scope.HostWorld;
        }
        await Wo134ApplyIfReadyAsync(timeout: false);
        return true;
    }

    private async Task<bool> ConfirmWorldLedgerAsync(WorldBaselineChunk item, InFrame frame, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 5 && WorldBaselineCurrent(frame, ct); attempt++)
        {
            LedgerWork? work = null;
            lock (_w134Gate)
            {
                if (_w134BaselineLedger != (item.Scope, item.SnapshotId)) return false;
                if (_w134BaselineLedgerWork is { ReceiptEpoch: > 0, Calls: { } calls } sent && sent.Next == calls.Count)
                    work = sent;
            }
            if (work is not null)
            {
                string args = FormattableString.Invariant($"\"{frame.LoadGeneration}\",{work.ReceiptEpoch},{work.Generation},{work.Rows.Count}");
                string reading = await AskModAsync("KCD2MP_S3ReadLedger", 800, args, ct);
                if (WorldBaselineCurrent(frame, ct) && Wo134LedgerCurrent(work) && reading == "applied") return true;
            }
            await Task.Delay(150, ct);
        }
        return false;
    }

    private async Task<bool> ApplyWorldVitalsAsync(InFrame frame, CancellationToken ct)
    {
        float health = ReadFloat(frame.Payload, 1), stamina = ReadFloat(frame.Payload, 5);
        if (!float.IsFinite(health) || !float.IsFinite(stamina)) return false;
        byte source = frame.Payload[0], flags = frame.Payload[9];
        if (!await ApplyWorldStateFrameAsync(frame, ct)) return false;
        string reading = await AskModAsync("KCD2MP_S3ReadVitals", 800,
            FormattableString.Invariant($"\"{frame.LoadGeneration}\",\"{source}\""), ct);
        return WorldBaselineCurrent(frame, ct) && reading == FormattableString.Invariant($"{health:F1} {stamina:F1} {flags}");
    }

    private async Task<bool> ApplyWorldEventAsync(WorldEventFrame item, InFrame frame, CancellationToken ct)
    {
        bool Current() => item.Scope.HostWorld == frame.Stamp && item.Scope.Generation == frame.LoadGeneration &&
            WorldBaselineCurrent(frame, ct);
        if (!Current()) return false;
        if (item.Channel == WorldEventChannel.Corpse && frame.Type == Protocol.DeathMarkerDown)
            return frame.Payload.Length == Protocol.DeathMarkerDownLen &&
                DeathMarkerCodec.TryRead(frame.Payload.AsSpan(1), out var marker) &&
                await P5ReceiveMarkerAsync(frame.Payload[0], marker, frame.Stamp, frame.MarkerPeerSerial, ct) && Current();
        if (!TryWorldLootFrame(frame.Payload, out byte source, out var message)) return false;
        if (item.Channel == WorldEventChannel.Dialog && frame.Type == Protocol.ConvDown && message.Kind == Protocol.ConvVote)
            return await ApplyConfirmedDialogVoteAsync(source, message.Text, Current, ct, item.Id, item.SnapshotId) && Current();
        if (frame.Type != Protocol.LootHostDown || !T3SideSource(source)) return false;
        if (item.Channel == WorldEventChannel.Corpse && message.Kind == Protocol.LootHostCorpses)
            return await ApplyWorldCorpsesAsync(frame, ct) && Current();
        string[] fields = message.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string scope = FormattableString.Invariant($"\"{frame.LoadGeneration}\",\"{frame.LootGeneration}\"");
        string key = FormattableString.Invariant($"\"{item.SnapshotId}:{item.Id}\"");
        string function, args;
        if (item.Channel == WorldEventChannel.Loot && message.Kind == Protocol.LootHostItemResult &&
            fields.Length == 5 && Wo136Rules.IsItemVerdict(fields[0]) &&
            Wo134Rules.TryClass(fields[1], out var resultClass) && Wo134Rules.TryCoord(fields[2], out float rx) &&
            Wo134Rules.TryCoord(fields[3], out float ry) && Wo134Rules.TryCoord(fields[4], out float rz))
        {
            function = "KCD2MP_S3ApplyItemResult";
            args = FormattableString.Invariant($"{scope},\"{message.Tok}\",\"{fields[0]}\",\"{resultClass:D}\",{rx:R},{ry:R},{rz:R}");
        }
        else if (item.Channel == WorldEventChannel.Loot && message.Kind == Protocol.LootHostItemGone &&
            fields.Length == 4 && Wo134Rules.TryClass(fields[0], out var itemClass) &&
            Wo134Rules.TryCoord(fields[1], out float x) && Wo134Rules.TryCoord(fields[2], out float y) &&
            Wo134Rules.TryCoord(fields[3], out float z))
        {
            function = "KCD2MP_S3ApplyItemGone";
            args = FormattableString.Invariant($"{scope},{key},\"{itemClass:D}\",{x:R},{y:R},{z:R}");
        }
        else if (item.Channel == WorldEventChannel.Corpse && message.Kind == Protocol.LootHostTakeVerdict &&
            fields.Length == 5 && fields[0] is "grant" or "refuse" && Wo134Rules.BodyName.IsMatch(fields[1]) &&
            Wo217LootRules.ItemId.IsMatch(fields[2]) && Wo217LootRules.Owner.IsMatch(fields[3]) && fields[4] is "0" or "1")
        {
            function = "KCD2MP_S3ApplyCorpseTake";
            args = FormattableString.Invariant($"{scope},\"{message.Tok}\",\"{fields[0]}\",\"{fields[1]}\",\"{fields[2]}\",\"{fields[3]}\",{fields[4]}");
        }
        else return false;
        var result = await AskModAsync(function, 900, args, ct);
        return Current() && result == "applied";
    }

    private async Task<bool> ConfirmWorldBodyAsync(InFrame frame, string kind, string name, float x, float y, float z,
        float? yaw, bool? dead, bool? unconscious, float? hp, CancellationToken ct)
    {
        string args = FormattableString.Invariant($"\"{frame.LoadGeneration}\",\"{kind}\",\"{EscapeLua(name)}\"");
        for (int attempt = 0; attempt < 4 && WorldBaselineCurrent(frame, ct); attempt++)
        {
            string reading = await AskModAsync("KCD2MP_S3ReadBody", 800, args, ct);
            if (WorldBaselineCurrent(frame, ct) &&
                WorldBaselinePose.Confirms(reading, x, y, z, yaw, dead, unconscious, hp)) return true;
            if (attempt < 3) await Task.Delay(125, ct);
        }
        return false;
    }

    private async Task<bool> ApplyWorldCorpsesAsync(InFrame frame, CancellationToken ct)
    {
        if (frame.Type != Protocol.LootHostDown ||
            !TryWorldLootFrame(frame.Payload, out byte source, out var message) ||
            !T3SideSource(source) || message.Kind != Protocol.LootHostCorpses ||
            Wo217LootRules.ParseCorpses(message.Text) is not { } corpses || !W217On("encounter_world")) return false;
        await Wo217OnCorpsesAsync(source, corpses, frame);
        foreach (var corpse in corpses)
        {
            if (!await ConfirmWorldBodyAsync(frame, "npc", corpse.Name, corpse.X, corpse.Y, corpse.Z, null, true, null, null, ct))
                return false;
        }
        return WorldBaselineCurrent(frame, ct);
    }

    private async Task<bool> ApplyWorldWeatherAsync(InFrame frame, T3SideChannelFrame weather, CancellationToken ct)
    {
        if (!config.WeatherSyncEnabled ||
            (weather.Profile is { } badProfile && !WeatherNamePattern.IsMatch(badProfile)) ||
            (weather.Rain is float rain && (!float.IsFinite(rain) || rain < 0 || rain > 1)) ||
            (weather.Profile is null && weather.Rain is null)) return false;
        string? profile = weather.Profile;
        uint originalCount = 0;
        if (profile is not null)
        {
            if (await _combat.Wo151WeatherReadAsync(ct) is not { } before || !WorldBaselineCurrent(frame, ct)) return false;
            originalCount = before.Count;
            if (!await _combat.Wo151WeatherGateAsync(profile, ct) || !WorldBaselineCurrent(frame, ct)) return false;
        }
        string luaRain = weather.Rain?.ToString("R", CultureInfo.InvariantCulture) ?? "nil";
        await GameCalls.ExecuteNowAsync(FormattableString.Invariant(
            $"if KCD2MP_S3SetWeather then KCD2MP_S3SetWeather(\"{frame.LoadGeneration}\", \"{profile ?? ""}\", {luaRain}) end"), ct);
        for (int attempt = 0; attempt < 6 && WorldBaselineCurrent(frame, ct); attempt++)
        {
            bool confirmedProfile = profile is null ||
                (await _combat.Wo151WeatherReadAsync(ct) is { } read && read.Count != originalCount && read.Name == profile);
            if (confirmedProfile && WorldBaselineCurrent(frame, ct))
            {
                if (weather.Rain is float expected)
                {
                    string reading = await AskModAsync("KCD2MP_S3ReadRain", 800,
                        FormattableString.Invariant($"\"{frame.LoadGeneration}\""), ct);
                    if (!float.TryParse(reading, CultureInfo.InvariantCulture, out float localRain) ||
                        !float.IsFinite(localRain) || MathF.Abs(localRain - expected) > 0.08f)
                    {
                        await Task.Delay(200, ct);
                        continue;
                    }
                }
                if (!WorldBaselineCurrent(frame, ct)) return false;
                if (profile is not null)
                {
                    _w151WeatherGate = _lastAppliedWeatherProfile = profile;
                    LoadSequenceWeather(profile, "applied");
                }
                return true;
            }
            await Task.Delay(200, ct);
        }
        return false;
    }
}

internal static class WorldBaselinePose
{
    internal static bool Finite(params float[] values) => Array.TrueForAll(values, float.IsFinite);

    internal static bool Confirms(string reading, float x, float y, float z, float? yaw, bool? dead, bool? unconscious, float? hp)
    {
        if (!Finite(x, y, z, yaw ?? 0, hp ?? 0)) return false;
        string[] fields = reading.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 7 ||
            !TryNumber(0, out float rx) || !TryNumber(1, out float ry) || !TryNumber(2, out float rz) ||
            (rx - x) * (rx - x) + (ry - y) * (ry - y) > 2.25f || MathF.Abs(rz - z) > 1.5f) return false;
        if (yaw is float angle && (!TryNumber(3, out float radians) ||
            MathF.Abs(MathF.IEEERemainder(radians - angle, 2 * MathF.PI)) > 0.45f)) return false;
        if (dead.HasValue && fields[4] != (dead.Value ? "1" : "0")) return false;
        if (unconscious.HasValue && fields[5] != (unconscious.Value ? "1" : "0")) return false;
        return hp is null || TryNumber(6, out float health) && MathF.Abs(health - hp.Value) <= 1f;

        bool TryNumber(int at, out float value) => float.TryParse(fields[at], NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
            float.IsFinite(value);
    }
}
