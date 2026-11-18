// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Net.Sockets;
using KcdMp.Wire;

static partial class P
{
    private sealed class BotPresentationClock(NetworkStream stream)
    {
        private readonly object _sync = new();
        private readonly PresentationClockClient _clock = new();
        private readonly TaskCompletionSource<bool> _initial = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _supported;
        private bool _combat;
        private long _lastQuery = -PresentationClock.ProbeIntervalMs;

        public Task Initial => _initial.Task;
        public bool SupportsCombat
        {
            get { lock (_sync) return _combat && _initial.Task.IsCompletedSuccessfully && _initial.Task.Result; }
        }

        public Task NegotiateAsync(CancellationToken token) =>
            Send(stream, Protocol.BuildLinkHelloUp(Protocol.LinkCapPresentationTime | Protocol.LinkCapPresentationCombat), token);

        public Task RefreshAsync(CancellationToken token)
        {
            byte[] query;
            lock (_sync)
            {
                long now = PresentationClock.NowMs();
                if (!_supported || now - _lastQuery < PresentationClock.ProbeIntervalMs) return Task.CompletedTask;
                _lastQuery = now;
                query = _clock.Begin(now);
            }
            return Send(stream, query, token);
        }

        public async Task<bool> OnFrameAsync(byte type, byte[] payload, CancellationToken token)
        {
            if (type == Protocol.LinkHelloDown)
            {
                ushort caps = 0;
                bool supported = payload.Length == Protocol.LinkHelloDownLen &&
                    Protocol.TryParseLinkHello(payload, out byte version, out caps, out _, out _) &&
                    version == Protocol.LinkVersion && (caps & Protocol.LinkCapPresentationTime) != 0;
                lock (_sync)
                {
                    _supported = supported;
                    _combat = supported && (caps & Protocol.LinkCapPresentationCombat) != 0;
                }
                if (supported) await RefreshAsync(token);
                else if (_initial.TrySetResult(false))
                    Console.WriteLine("BOT-T4 role=joiner presentation_clock=legacy reason=relay");
                return true;
            }
            if (type != Protocol.PresentationClockDown) return false;

            byte[] confirmation;
            lock (_sync)
            {
                if (!_supported || !_clock.TryComplete(payload, PresentationClock.NowMs(), out confirmation)) return true;
            }
            // The relay sees this before a calibrated frame because both are
            // written under the same TCP write lock. There is no second ACK.
            await Send(stream, confirmation, token);
            if (_initial.TrySetResult(true)) Console.WriteLine("BOT-T4 role=joiner presentation_clock=ready");
            return true;
        }
    }
}
