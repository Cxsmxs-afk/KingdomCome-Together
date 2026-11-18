// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;

namespace KcdMp.Client.Tests;

public sealed class SessionRecordingTests
{
    private static byte[] Encode(params SessionRecord[] records)
    {
        using var output = new MemoryStream();
        SessionRecording.Write(output, records);
        return output.ToArray();
    }

    private static IReadOnlyList<SessionRecord> Decode(byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        return SessionRecording.Read(input);
    }

    [Fact]
    public void Binary_format_round_trips_simultaneous_rows_and_the_exact_relay_frame()
    {
        SessionRecord[] source =
        [
            ReplayGameTransport.Call(1, 7, nameof(IGameTransport.ReadPlayerStateAsync)),
            new(1, SessionRecordKind.RelayReceived, [0x33, 2, 0, 0xE1, 0xF2]),
            ReplayGameTransport.Result(23, 7),
            new(45, SessionRecordKind.GameEvent, "[\"gameplay\",\"ready\"]"u8.ToArray())
        ];
        byte[] encoded = Encode(source);
        Assert.Equal("KCDREC\r\n\x01\x00"u8.ToArray(), encoded[..10]);
        var decoded = Decode(encoded);
        Assert.Equal(source.Length, decoded.Count);
        for (int i = 0; i < source.Length; i++)
        {
            Assert.Equal(source[i].OffsetMs, decoded[i].OffsetMs);
            Assert.Equal(source[i].Kind, decoded[i].Kind);
            Assert.Equal(source[i].Payload, decoded[i].Payload);
        }
    }

    [Fact]
    public void Malformed_and_incomplete_recordings_are_rejected_before_replay()
    {
        byte[] encoded = Encode(new SessionRecord(5, SessionRecordKind.RelaySent, [0x33, 1, 0, 0x7A]));
        Assert.Throws<InvalidDataException>(() => Decode(encoded[..^1]));
        Assert.Throws<InvalidDataException>(() => Decode([.. encoded, 1]));

        byte[] version = (byte[])encoded.Clone();
        version[8] = 2;
        Assert.Throws<InvalidDataException>(() => Decode(version));
        byte[] length = (byte[])encoded.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(length.AsSpan(19), uint.MaxValue);
        Assert.Throws<InvalidDataException>(() => Decode(length));
        byte[] relay = (byte[])encoded.Clone();
        relay[24] = 2;
        Assert.Throws<InvalidDataException>(() => Decode(relay));
        Assert.Throws<InvalidDataException>(() => Encode(new(5, SessionRecordKind.GameEvent, [1]), new(4, SessionRecordKind.GameEvent, [1])));
    }

    [Fact]
    public async Task Replay_validates_arguments_and_releases_interleaved_outcomes_by_call_id()
    {
        var id = Guid.Parse("0584a8fe-42ac-44ac-a148-9d6838d5b23d");
        SessionRecord[] entries =
        [
            ReplayGameTransport.Call(0, 2, nameof(IGameTransport.ReadGhostSoulGuidAsync), "kcd2mp_2"),
            ReplayGameTransport.Call(5, 8, nameof(IGameTransport.ExecuteNowAsync), "KCD2MP_Synthetic('Grüße')"),
            ReplayGameTransport.Result(6, 8),
            ReplayGameTransport.Call(7, 9, nameof(IGameTransport.ReadPlayerStateAsync)),
            ReplayGameTransport.Result(9, 2, id),
            ReplayGameTransport.Result(10, 9, new PlayerState(10, 20, 30, 1, true, null, 4, false, null))
        ];
        await using var replay = new ReplayGameTransport(Decode(Encode(entries)));
        var soul = replay.ReadGhostSoulGuidAsync("kcd2mp_2");
        Assert.False(soul.IsCompleted);
        await replay.ExecuteNowAsync("KCD2MP_Synthetic('Grüße')");
        Assert.False(soul.IsCompleted);
        PlayerState? observed = await replay.ReadPlayerStateAsync();
        Assert.Equal(id, await soul);
        Assert.Equal(new PlayerState(10, 20, 30, 1, true, null, 4, false, null), observed);
        replay.VerifyComplete();
        Assert.Equal(new[] { 2, 8, 9 }, replay.ObservedCalls.Select(row => row.Id));

        await using var changed = new ReplayGameTransport(entries);
        var mismatch = await Assert.ThrowsAsync<InvalidDataException>(() => changed.ReadGhostSoulGuidAsync("kcd2mp_9"));
        Assert.DoesNotContain("kcd2mp_9", mismatch.Message);
        Assert.Throws<InvalidDataException>(changed.VerifyComplete);
    }

    [Fact]
    public void Replay_requires_complete_correlated_game_outcomes_and_a_valid_result_type()
    {
        var call = ReplayGameTransport.Call(0, 7, nameof(IGameTransport.IsGameReadyAsync));
        var result = ReplayGameTransport.Result(0, 7, true);
        Assert.Throws<InvalidDataException>(() => new ReplayGameTransport([call]));
        Assert.Throws<InvalidDataException>(() => new ReplayGameTransport([result, call]));
        Assert.Throws<InvalidDataException>(() => new ReplayGameTransport([call, result, result]));
        var missingOutcome = new SessionRecord(0, SessionRecordKind.GameResult, "{\"Id\":7,\"Value\":true}"u8.ToArray());
        Assert.Throws<InvalidDataException>(() => new ReplayGameTransport([call, missingOutcome]));
        Assert.Throws<InvalidDataException>(() => new ReplayGameTransport([call, ReplayGameTransport.Result(0, 7, "yes")]));
    }

    [Fact]
    public async Task Recorder_preserves_results_and_call_outcomes_and_redacts_unapproved_Lua()
    {
        string path = Path.Combine(Path.GetTempPath(), $"kcdrec-{Guid.NewGuid():N}.kcdrec");
        const string safe = "KCD2MP_Synthetic()", skipped = "private path";
        try
        {
            var source = new ReplayGameTransport(
            [
                ReplayGameTransport.Call(0, 1, nameof(IGameTransport.ReadPlayerSoulIdentityAsync)),
                ReplayGameTransport.Result(0, 1, (Guid.Empty, "synthetic")),
                ReplayGameTransport.Call(0, 2, nameof(IGameTransport.ExecuteNowAsync), skipped),
                ReplayGameTransport.Result(0, 2),
                ReplayGameTransport.Call(0, 3, nameof(IGameTransport.ExecuteNowAsync), safe),
                new(0, SessionRecordKind.GameResult, "{\"Id\":3,\"Outcome\":2,\"Value\":null}"u8.ToArray())
            ]);
            using (var recorder = SessionRecorder.Create(path))
            {
                await using var recording = new RecordingGameTransport(source, recorder, lua => lua == safe);
                var changedTransport = new RecordingGameTransport(source, recorder, lua => lua == safe);
                Assert.Equal((Guid.Empty, "synthetic"), await recording.ReadPlayerSoulIdentityAsync());
                await recording.ExecuteNowAsync(skipped);
                await Assert.ThrowsAsync<IOException>(() => changedTransport.ObserveExecuteAsync(safe, () => source.ExecuteNowAsync(safe)));
                source.VerifyComplete();
                Assert.False(recorder.Failed);
            }

            byte[] bytes = await File.ReadAllBytesAsync(path);
            Assert.DoesNotContain(skipped, System.Text.Encoding.UTF8.GetString(bytes));
            await using var replay = new ReplayGameTransport(Decode(bytes));
            Assert.Equal((Guid.Empty, "synthetic"), await replay.ReadPlayerSoulIdentityAsync());
            await Assert.ThrowsAsync<IOException>(() => replay.ExecuteNowAsync(safe));
            replay.VerifyComplete();
            Assert.Equal(new[] { 1, 2 }, replay.ObservedCalls.Select(row => row.Id));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Append_limit_stops_capture_and_an_incomplete_prefix_cannot_be_loaded()
    {
        string path = Path.Combine(Path.GetTempPath(), $"kcdrec-{Guid.NewGuid():N}.kcdrec");
        try
        {
            using (var recorder = SessionRecorder.Create(path))
            {
                Assert.Throws<IOException>(() => SessionRecorder.Create(path));
                Assert.True(recorder.TryAppend(SessionRecordKind.GameEvent, "[]"u8));
                Assert.False(recorder.TryAppend(SessionRecordKind.GameEvent, new byte[131073]));
                Assert.True(recorder.Failed);
                Assert.False(recorder.TryAppend(SessionRecordKind.GameEvent, "[]"u8));
            }
            Assert.Throws<InvalidDataException>(() => Decode(File.ReadAllBytes(path)));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Closing_while_a_game_call_is_pending_preserves_the_game_call_and_marks_the_file_incomplete()
    {
        string path = Path.Combine(Path.GetTempPath(), $"kcdrec-{Guid.NewGuid():N}.kcdrec");
        try
        {
            var source = new ReplayGameTransport(
            [
                ReplayGameTransport.Call(0, 1, nameof(IGameTransport.IsGameReadyAsync)),
                ReplayGameTransport.Call(0, 2, nameof(IGameTransport.ReadPlayerStateAsync)),
                ReplayGameTransport.Result(0, 2),
                ReplayGameTransport.Result(0, 1, true)
            ]);
            using var recorder = SessionRecorder.Create(path);
            await using var recording = new RecordingGameTransport(source, recorder);
            Task<bool> pending = recording.IsGameReadyAsync();
            Assert.False(pending.IsCompleted);
            recorder.Dispose();
            Assert.True(recorder.Failed);
            Assert.Null(await source.ReadPlayerStateAsync());
            Assert.True(await pending);
            source.VerifyComplete();
            Assert.Throws<InvalidDataException>(() => Decode(File.ReadAllBytes(path)));
        }
        finally { File.Delete(path); }
    }
}
