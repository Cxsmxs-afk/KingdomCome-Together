// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.Text;
using KcdMp.Wire;

namespace KcdMp.Client;

public partial class GameBridge
{
    private sealed record LoadWeatherArm(Stream Stream, string Profile, ulong Generation, uint World, long ExpiresAt);
    private LoadWeatherArm? _loadWeatherArm;
    private const string LoadWeatherFlag = "--test-load-sequence";

    private void LoadWeatherOnGameEvent(string arg)
    {
        if (Environment.GetEnvironmentVariable("KCDMP_TEST_LOAD_SEQUENCE") != "1" || !LoadSequenceEnabled)
            return;
        string[] args = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var before = _worldLoad.Current;
        if (args.Length != 2 || args[0] != LoadWeatherFlag || !WeatherNamePattern.IsMatch(args[1]) ||
            !_isDamageAuthority || before.Phase != WorldLoadPhase.Play || before.HostWorld == 0 || _resyncStream is not { } stream)
            return;
        Interlocked.Exchange(ref _loadWeatherArm, new(stream, args[1], before.Generation, before.HostWorld,
            Environment.TickCount64 + 300_000));
        Console.WriteLine(FormattableString.Invariant(
            $"MP-LOAD-WEATHER-TEST role=host gen={before.Generation} world={before.HostWorld} profile={args[1]} result=armed"));
    }

    // The matching ready-wait edge has an already verified transfer. A probe never selects
    // the previous world or survives a relay reconnection, even if queued behind game traffic.
    private async Task LoadWeatherSendAsync(HostJoin join)
    {
        if (Volatile.Read(ref _loadWeatherArm) is not { } arm) return;
        if (Environment.TickCount64 >= arm.ExpiresAt || !ReferenceEquals(_resyncStream, arm.Stream))
        {
            Interlocked.CompareExchange(ref _loadWeatherArm, null, arm);
            return;
        }

        var (generation, token) = LoadSequenceCapture();
        var current = _worldLoad.Current;
        if (current.Generation != generation || generation <= arm.Generation || current.HostWorld == 0 ||
            current.HostWorld == arm.World || current.HostWorld != _w206.World ||
            current.Phase is WorldLoadPhase.Menu or WorldLoadPhase.Exit)
            return;
        if (!ReferenceEquals(Interlocked.CompareExchange(ref _loadWeatherArm, null, arm), arm))
            return;

        bool Current() => LoadSequenceEnabled && !token.IsCancellationRequested && Environment.TickCount64 < arm.ExpiresAt &&
            ReferenceEquals(_resyncStream, arm.Stream) && ReferenceEquals(_hostJoin, join) && _isDamageAuthority &&
            !join.Cts.IsCancellationRequested && _worldLoad.Current.Generation == generation && _w206.World == current.HostWorld;
        if (!Current()) return;

        void Written() => Console.WriteLine(FormattableString.Invariant(
            $"MP-LOAD-WEATHER-TEST role=host gen={generation} world={current.HostWorld} peer={join.Joiner} join=0x{join.JoinId:x8} profile={arm.Profile} blend=0 result=sent"));
        byte[] packet = BuildWeatherPacket(arm.Profile, 0);
        try
        {
            if (_w213Writer is { } writer && ReferenceEquals(arm.Stream, _w213Stream))
            {
                writer.Enqueue(packet, Current, Written);
                return;
            }
            await _tcpWriteLock.WaitAsync(token);
            try
            {
                if (!Current()) return;
                await arm.Stream.WriteAsync(packet, token);
                if (_sessionRecorder is { } recorder) RecordRelayWrite(recorder, packet);
                Written();
            }
            finally { _tcpWriteLock.Release(); }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            Console.WriteLine($"MP-LOAD-WEATHER-TEST role=host result=failed reason={ex.GetType().Name}");
        }
    }

    private static byte[] BuildWeatherPacket(string profile, ushort blend)
    {
        byte[] name = Encoding.UTF8.GetBytes(profile);
        var packet = new byte[3 + 1 + name.Length + 2];
        packet[0] = Protocol.WeatherUp;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(1), (ushort)(1 + name.Length + 2));
        packet[3] = (byte)name.Length;
        name.CopyTo(packet, 4);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(4 + name.Length), blend);
        return packet;
    }
}
