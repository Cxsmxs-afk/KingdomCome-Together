// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using KcdMp.Wire;

namespace KcdMp.Client;

public partial class GameBridge
{
    private sealed record T3SideValue(byte Source, float? Rain = null, string? Profile = null, ushort Blend = 0,
        HostCalendarSample? Calendar = null, long CalendarGeneration = 0);
    // Both weather encodings share one latest slot; an older profile must not overwrite a newer full sample.
    private const byte T3Clock = 0, T3Weather = 1, T3WeatherProfile = T3Weather;
    private readonly T3SideChannelQueue<T3SideValue> _t3SideChannels = new();
    private readonly AsyncLocal<T3SideChannelQueue<T3SideValue>.Lease?> _t3SideAmbient = new();
    private bool T3SideWorldWritable => _where == GameWhere.World && !_ownLoadExpected && !_loadGraceOwn &&
        !_w211Holding && (_isDamageAuthority || (_joinedWorld && _jj is null));
    private T3SideChannelQueue<T3SideValue>.Lease T3SideLease() => _t3SideAmbient.Value ?? _t3SideChannels.Capture(_w206.World);
    private sealed class T3SideContext : IDisposable
    {
        private readonly GameBridge _owner;
        private readonly T3SideChannelQueue<T3SideValue>.Lease? _previous;
        public T3SideContext(GameBridge owner, T3SideChannelQueue<T3SideValue>.Lease lease)
        { _owner = owner; _previous = owner._t3SideAmbient.Value; owner._t3SideAmbient.Value = lease; }
        public void Dispose() => _owner._t3SideAmbient.Value = _previous;
    }
    private bool T3SideReady => _t3SideChannels.Ready(T3SideLease(), Environment.TickCount64) && T3SideWorldWritable;
    private void T3SideGuard(T3SideChannelQueue<T3SideValue>.Lease lease, bool requireReady = false)
    {
        ProcessorFrameScope.ThrowIfAbandoned();
        if (!_t3SideChannels.IsCurrent(lease) || lease.World != _w206.World || (requireReady && !T3SideReady))
            throw new OperationCanceledException("side channel belongs to an abandoned world/life", lease.Token);
    }
    private void T3SideChannelsInvalidate(string why) => _t3SideChannels.Invalidate(_w206.World, preserveGameplay: why == "role-change");
    private void T3SideChannelsGameplayStarted() => _t3SideChannels.GameplayStarted(_w206.World, Environment.TickCount64);
    private bool T3SideSource(byte source) => !_isDamageAuthority && source == (_jj?.Host ?? (_hostModeKnown ? _hostModeFrom : (byte)0xFF));

    /// <summary>The reader does no HTTP/native work. Clock bootstrap is cached immediately; only following is deferred.</summary>
    private bool T3TryDeferSideChannel(InFrame frame)
    {
        if (!T3SideChannelFrame.TryDecode(frame.Type, frame.Payload, WeatherBlendSeconds, out var message)) return false;
        var lease = T3SideLease();
        if (frame.Stamp != _w206.World || !T3SideSource(message.Source)) return true;
        if (message.Kind == Protocol.W164Clock)
        {
            if (CacheHostCalendar(message.Source, message.Text) is not { } clock) return true;
            _t3SideChannels.Put(T3Clock, new(message.Source, Calendar: clock.Sample, CalendarGeneration: clock.Generation), lease);
        }
        else
        {
            if (!config.WeatherSyncEnabled) return true;
            _t3SideChannels.Put(T3Weather, new(message.Source, message.Rain, message.Profile, message.Blend), lease);
        }
        T3Increment("deferred_sidechannel");
        return true;
    }

    private async Task T3DrainSideChannels(CancellationToken ct)
    {
        using var caller = ProcessorFrameScope.Link(ct);
        foreach (var work in _t3SideChannels.Take(_w206.World, Environment.TickCount64, T3SideWorldWritable))
        {
            using var context = new T3SideContext(this, work.Lease);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ProcessorFrameScope.CurrentToken, work.Lease.Token);
            using var scope = ProcessorFrameScope.Link(linked.Token);
            T3SideGuard(work.Lease, true);
            var value = work.Value;
            if (work.Kind == T3Clock && value.Calendar is { } calendar)
            {
                if (!HostCalendarOwned) await ResumeHostCalendarAsync(calendar, value.CalendarGeneration);
                T3SideGuard(work.Lease, true);
                if (HostCalendarOwned) await UpdateHostCalendarAsync(calendar, value.CalendarGeneration);
            }
            else if (work.Kind == T3Weather && value.Rain is float rain)
                await Wo166OnHostWeatherAsync(rain, value.Profile);
            else if (value.Profile is string profile)
            {
                await Wo151OnHostWeatherAsync(profile);
                T3SideGuard(work.Lease, true);
                await T3ApplyWeatherProfileAsync(profile, value.Blend);
            }
            T3SideGuard(work.Lease, true);
        }
    }

    private async Task T3ApplyWeatherProfileAsync(string profile, ushort blendSec)
    {
        ProcessorFrameScope.ThrowIfAbandoned();
        var lease = T3SideLease();
        T3SideGuard(lease);
        if (!WeatherNamePattern.IsMatch(profile)) return;
        if (!T3SideReady)
        {
            _t3SideChannels.Put(T3WeatherProfile, new(0, Profile: profile, Blend: blendSec), lease);
            T3Increment("deferred_sidechannel"); return;
        }
        if (profile == _lastAppliedWeatherProfile) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ProcessorFrameScope.CurrentToken, lease.Token);
        using var scope = ProcessorFrameScope.Link(linked.Token);
        T3SideGuard(lease, true);
        await GameCalls.ExecuteNowAsync(string.Create(CultureInfo.InvariantCulture,
            $"if KCD2MP_ApplyWeather then KCD2MP_ApplyWeather(\"{profile}\",{blendSec}) end"), linked.Token);
        T3SideGuard(lease, true);
        _lastAppliedWeatherProfile = profile;
        Console.WriteLine($"[weather] applied profile '{profile}' blend={blendSec}");
    }
}
