// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Globalization;

namespace KcdMp.Client;

public partial class GameBridge
{
    private sealed record RecoveryWeather(WorldSyncScope Scope, string? Profile, float? Rain, long ReceivedAt);
    private RecoveryWeather? _worldRecoveryWeather;

    private void RememberRecoveryWeather(InFrame frame, T3SideChannelFrame weather)
    {
        if (!WorldRecoveryWireEffective || !frame.LoadScoped || frame.LoadGeneration == 0) return;
        Volatile.Write(ref _worldRecoveryWeather,
            new(new(frame.Stamp, frame.LoadGeneration), weather.Profile, weather.Rain, Environment.TickCount64));
    }

    private bool RecoveryReadbackEligible(WorldSyncReceiver receiver) =>
        !_isDamageAuthority && LoadSequenceWritable && T3SideWorldWritable && !_localSkipActive &&
        _jj is null && WorldSyncCurrent(receiver);

    private async Task RefreshRecoveryReadbackAsync(WorldSyncReceiver receiver, WorldBaselineSection area, CancellationToken ct)
    {
        ulong revision = receiver.Recovery.ReadbackRevision(area);
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(900);
            if (!RecoveryReadbackEligible(receiver)) return;
            bool? drift = area switch
            {
                WorldBaselineSection.Clock => await ReadRecoveryClockAsync(budget.Token),
                WorldBaselineSection.Weather => await ReadRecoveryWeatherAsync(receiver, budget.Token),
                _ => null
            };
            if (RecoveryReadbackEligible(receiver))
                receiver.Recovery.NoteReadback(area, revision, drift.HasValue, drift == true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
        {
            // An unavailable engine read is not a mismatch.
        }
        finally { receiver.Recovery.EndReadback(area); }
    }

    private async Task<bool?> ReadRecoveryClockAsync(CancellationToken ct)
    {
        HostCalendarSample? expected;
        lock (_hostCalendarLock) expected = _hostCalendar;
        long now = Environment.TickCount64;
        if (expected is not { } sample || !HostCalendarOwned || sample.Ratio <= 0 ||
            now - sample.ArrivedMs is < 0 or > 5000)
            return null;
        if (await _combat.CalendarSnapshotAsync(ct) is not { } observed) return null;
        lock (_hostCalendarLock) if (_hostCalendar != sample) return null;
        long target = sample.TargetMs(Environment.TickCount64);
        return Math.Abs(observed.WorldMs - target) > 2500 || Math.Abs(observed.Ratio - sample.Ratio) > 0.05;
    }

    private async Task<bool?> ReadRecoveryWeatherAsync(WorldSyncReceiver receiver, CancellationToken ct)
    {
        long now = Environment.TickCount64;
        var expected = Volatile.Read(ref _worldRecoveryWeather);
        if (!config.WeatherSyncEnabled || expected is null || expected.Scope != receiver.Scope ||
            now - expected.ReceivedAt is < 0 or > 10_000) return null;
        bool profileKnown = expected.Profile is not null;
        bool rainKnown = expected.Rain.HasValue;
        bool drift = false;
        if (expected.Profile is { } profile)
        {
            if (await _combat.Wo151WeatherReadAsync(ct) is not { Count: > 0 } read) return null;
            drift = profile != read.Name;
        }
        if (expected.Rain is float rain)
        {
            string reading = await AskModAsync("KCD2MP_S3ReadRain", 600,
                FormattableString.Invariant($"\"{receiver.Scope.Generation}\""), ct);
            if (!float.TryParse(reading, CultureInfo.InvariantCulture, out float localRain) || !float.IsFinite(localRain))
                return null;
            drift |= Math.Abs(localRain - rain) > 0.12;
        }
        return (profileKnown || rainKnown) && ReferenceEquals(expected, Volatile.Read(ref _worldRecoveryWeather))
            ? drift : null;
    }
}
