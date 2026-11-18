// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Text.Json;
using KcdMp.Wire;

namespace KcdMp.Client;

public partial class GameBridge
{
    private SessionRecorder? _sessionRecorder;
    private RecordingGameTransport? _recordingTransport;

    internal void RecordSession(SessionRecorder? recorder) => _sessionRecorder = recorder;

    // Leave _transport concrete for the log tail subscriptions and HTTP lifecycle.
    private IGameTransport GameCalls => Volatile.Read(ref _recordingTransport) ?? _transport;

    private void UpdateRecordingTransport()
    {
        if (_sessionRecorder is { } recorder)
            Volatile.Write(ref _recordingTransport, new RecordingGameTransport(_transport, recorder, TestRecordingOptions.AllowsLuaEffect));
    }

    private void RecordGameEvent(string name, string arg)
    {
        if (_sessionRecorder is { } recorder && arg.Length <= 4096)
            recorder.TryAppend(SessionRecordKind.GameEvent, JsonSerializer.SerializeToUtf8Bytes(new[] { name, arg }));
    }

    private void RecordLoadStarted() => RecordGameEvent("load_started", "");
    private void RecordGameplayStarted() => RecordGameEvent("gameplay_started", "");
    private void RecordMainMenuShown() => RecordGameEvent("main_menu", "");

    private void RecordRelayRead(byte[] header, byte[] payload)
    {
        if (_sessionRecorder is not { } recorder || !TestRecordingOptions.AllowsFrame(header[0], payload)) return;
        byte[] packet = [.. header, .. payload];
        recorder.TryAppend(SessionRecordKind.RelayReceived, packet);
    }

    private static void RecordRelayWrite(SessionRecorder recorder, ReadOnlySpan<byte> packet)
    {
        if (TestRecordingOptions.AllowsPacket(packet)) recorder.TryAppend(SessionRecordKind.RelaySent, packet);
    }

    private sealed class RecordingRelayPath(ILinkPath inner, SessionRecorder recorder) : ILinkPath
    {
        public string Transport => inner.Transport;
        public bool CanSendUnreliable => inner.CanSendUnreliable;
        public bool StateOwnLane => inner.StateOwnLane;
        public LinkPathStatus Status() => inner.Status();

        public async ValueTask SendAsync(ReadOnlyMemory<byte> frame, bool state, CancellationToken ct)
        {
            await inner.SendAsync(frame, state, ct);
            RecordRelayWrite(recorder, frame.Span);
        }
    }
}
