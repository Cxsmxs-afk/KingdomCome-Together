// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Buffers.Binary;
using System.Text.RegularExpressions;
using KcdMp.Wire;

namespace KcdMp.Client;

internal sealed class TestRecordingOptions(string? path)
{
    private const string Flag = "--test-record";
    private static readonly Regex LuaEffect = new(
        @"^(?:if )?KCD2MP_(?:ApplyWeather|ApplyTimeSkip|ApplyNpcState|NpcRemoteDeath|SetGhostHealth|UpdateGhost|W132AvatarHeal|W136Hold|W197FigureKnockout|W166WeatherApply|W134(?:ItemResult|ItemGone|TakeResult|Host\w+)|W217(?:BodyListChunk|TakeResult|Host\w+|Corpses)|W177(?:HostCorpseAsk|CorpseAnswer)|W113(?:PlayerRespawned|GraveAdd|GraveRemove))\b",
        RegexOptions.CultureInvariant);

    public static bool TryRead(string[] args, string? environment, out TestRecordingOptions? recording, out string[] agentArgs)
    {
        recording = null;
        agentArgs = args;
        var flags = args.Where(a => a == Flag || a.StartsWith(Flag + "=", StringComparison.Ordinal)).ToArray();
        if (flags.Length == 0) return true;
        if (environment != "1" || flags.Length != 1) return false;
        string flag = flags[0];
        string? file = flag == Flag ? null : flag[(Flag.Length + 1)..];
        if (file is not null && (!file.EndsWith(".kcdrec", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileNameWithoutExtension(file).Length == 0 || file.IndexOfAny(Path.GetInvalidPathChars()) >= 0)) return false;
        recording = new(file);
        agentArgs = args.Where(a => a != flag).ToArray();
        return true;
    }

    public SessionRecorder? Open()
    {
        try
        {
            string file;
            if (path is null)
            {
                string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (root.Length == 0) root = Path.GetTempPath();
                string dir = Path.Combine(root, "KingdomComeTogether", "Recordings");
                Directory.CreateDirectory(dir);
                file = Path.Combine(dir, FormattableString.Invariant($"session-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Environment.ProcessId}.kcdrec"));
            }
            else file = Path.GetFullPath(path);
            var recorder = SessionRecorder.Create(file);
            Console.WriteLine("MP-T2-RECORD enabled file=" + Path.GetFileName(file));
            return recorder;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Console.Error.WriteLine("MP-T2-RECORD unavailable: " + ex.GetType().Name);
            return null;
        }
    }

    public static bool AllowsLuaEffect(string lua) => lua.Length <= 4096 && LuaEffect.IsMatch(lua);

    public static bool AllowsGameEvent(string name) => name is "time_now" or "w134_open" or "w134_take" or "w134_item" or
        "w134_applied" or "w217_corpses" or "w217_corpse" or "w217_lootend" or "w177_load_quiet";

    public static bool AllowsFrame(byte type, ReadOnlySpan<byte> body)
    {
        if (type == Protocol.WorldEventDown)
            return WorldSyncWire.TryReadEvent(body, out var item) && AllowsFrame(item.Type, item.Payload);
        if (type == Protocol.WorldBaselineDown)
            return WorldSyncWire.TryReadBaseline(body, out var chunk) &&
                (chunk.Payload.Length == 0 || (chunk.Payload[0] is not (Protocol.WorldBaselineDown or Protocol.WorldEventDown or
                    Protocol.WorldSyncUp or Protocol.WorldSyncStatusDown) && AllowsFrame(chunk.Payload[0], chunk.Payload.AsSpan(1))));
        if (type == Protocol.WorldSyncUp) return WorldSyncWire.TryReadSync(body, out _);
        if (type == Protocol.WorldSyncStatusDown) return WorldSyncStatusWire.TryRead(body, out _);
        if (type is Protocol.PresentationPoseDown or Protocol.PresentationActionDown)
        {
            return PresentationTimeWire.TryRead(type, body, out _, out byte inner, out int offset) &&
                AllowsFrame(inner, body[offset..]);
        }
        if (type == Protocol.PresentationCombatUp)
        {
            return PresentationTimeWire.TryReadCombatUp(body, out byte inner, out _) &&
                AllowsFrame(inner, body[PresentationTimeWire.SubjectStampLength..]);
        }
        if (type is Protocol.W164Up or Protocol.W164Down)
        {
            int header = Protocol.JoinHeaderLen + (type == Protocol.W164Down ? 1 : 0);
            return body.Length > header && LootMsg.TryDecode(body[header..], out var msg) &&
                msg.Kind is Protocol.W164RangedStep or Protocol.W164Clock or Protocol.W164Weather or Protocol.W164CorpseAsk or Protocol.W164CorpseAnswer;
        }
        if (type == Protocol.ConvDown)
            return body.Length > 1 + Protocol.JoinHeaderLen &&
                LootMsg.TryDecode(body[(1 + Protocol.JoinHeaderLen)..], out var message) && message.Kind == Protocol.ConvVote;
        return type is Protocol.Position or Protocol.Ghost or Protocol.NpcStateUp or Protocol.NpcStateDown or
            Protocol.NpcStateQDown or Protocol.ActionUp or Protocol.ActionDown or Protocol.PlayerStateUp or Protocol.PlayerStateDown or
            Protocol.PlayerDeathUp or Protocol.PlayerDeathDown or Protocol.PlayerRespawnedUp or Protocol.PlayerRespawnedDown or
            Protocol.NpcDamageUp or Protocol.NpcDamageDown or Protocol.PlayerHitUp or Protocol.PlayerHitDown or
            Protocol.PlayerHitV8Up or Protocol.PlayerHitV8Down or Protocol.WeatherUp or Protocol.WeatherDown or
            Protocol.TimeSkipUp or Protocol.TimeSkipDown or Protocol.LootAskUp or Protocol.LootAskDown or
            Protocol.LootHostUp or Protocol.LootHostDown or Protocol.DeathMarkerUp or Protocol.DeathMarkerDown or
            Protocol.GraveAddUp or Protocol.GraveAddDown or Protocol.GraveRemoveUp or Protocol.GraveRemoveDown or
            Protocol.JoinRequestUp or Protocol.JoinRequestDown or Protocol.JoinerReadyUp or Protocol.JoinerReadyDown or
            Protocol.JoinStatusUp or Protocol.JoinStatusDown;
    }

    public static bool AllowsPacket(ReadOnlySpan<byte> packet) => packet.Length >= 3 &&
        BinaryPrimitives.ReadUInt16LittleEndian(packet[1..]) == packet.Length - 3 && AllowsFrame(packet[0], packet[3..]);
}
