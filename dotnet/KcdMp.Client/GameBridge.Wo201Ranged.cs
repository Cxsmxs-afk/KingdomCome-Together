// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Concurrent;
using System.Diagnostics;
using KcdMp.Wire;
namespace KcdMp.Client;

// P6: immutable native capture -> reliable typed sender fact -> animation only.
// No active-hand HTTP reads, remote-prop inference or synthetic projectiles.
public partial class GameBridge
{
    private sealed class P6Context(Stream stream, uint world, ulong generation, long serial, long life, CancellationToken session, CancellationToken reader)
    {
        public readonly Stream Stream = stream;
        public readonly uint World = world;
        public readonly ulong Generation = generation;
        public readonly long Serial = serial, Life = life;
        public readonly CancellationTokenSource Stop = CancellationTokenSource.CreateLinkedTokenSource(session, reader);
        public CancellationToken Cancellation => Stop.Token;
    }
    private readonly object _p6StateGate = new();
    private P6Context? _p6Context;
    private Stream? _p6ReceiveStream;
    private CancellationToken _p6ReceiveToken;
    private long _p6VisualSerial;
    private long _p6VisualModeSerial, _p6VisualModeAtTick;
    private void P6VisualModeChanged()
    {
        long now = Environment.TickCount64;
        Interlocked.Exchange(ref _p6VisualModeAtTick, now);
        Interlocked.Increment(ref _p6VisualModeSerial);
        Wo214ResetOwnBow("mode", now);
    }
    private readonly SemaphoreSlim _p6PollGate = new(1, 1);
    private RangedItemCatalog? _p6Catalog;
    private Task<RangedItemCatalog?>? _p6CatalogTask;
    private long _p6CatalogRetryMs, _w201RangedRetryAtMs;
    private int _w201RangedRefusals;
    private int _p6CaptureWasOn;
    private long _w201RangedOut, _w201RangedIn, _w201RangedDispatched, _w201RangedRefused;
    private readonly ConcurrentDictionary<byte, (long Peer, ulong Gen, ulong Epoch, uint Seq)> _p6RemoteSteps = new();

    private bool P6Current(P6Context c) => !c.Stop.IsCancellationRequested && ReferenceEquals(c.Stream, _wo122Stream) &&
        ReferenceEquals(c.Stream, _p6ReceiveStream) && !_p6ReceiveToken.IsCancellationRequested &&
        c.World != 0 && c.World == P5LoadedWorld && c.Generation == P1Lifecycle.Current.Generation &&
        P1Lifecycle.Current is { Confirmed: true, GameplaySent: true } && c.Serial == Interlocked.Read(ref _p1Serial) &&
        c.Life == Interlocked.Read(ref _t3InputLifeRevision) && !_p1Loading && !_leaveInProgress && _where == GameWhere.World;
    private P6Context? P6GetContext()
    {
        lock (_p6StateGate)
        {
            if (_p6Context is { } current && P6Current(current)) return current;
            P6Invalidate();
            if (_wo122Stream is not { } stream || !ReferenceEquals(stream, _p6ReceiveStream) || _p6ReceiveToken.IsCancellationRequested || P5LoadedWorld == 0 || _p1Loading || _leaveInProgress ||
                P1Lifecycle.Current is not { Confirmed: true, GameplaySent: true, Generation: > 0 } state) return null;
            return _p6Context = new(stream, P5LoadedWorld, state.Generation, Interlocked.Read(ref _p1Serial), Interlocked.Read(ref _t3InputLifeRevision), _wo122Ct, _p6ReceiveToken);
        }
    }
    private void P6BeginReceive(Stream stream, CancellationToken ct)
    { lock (_p6StateGate) { P6Invalidate(); _p6ReceiveStream = stream; _p6ReceiveToken = ct; } }
    private void P6EndReceive(Stream stream, CancellationToken ct)
    { lock (_p6StateGate) { if (ReferenceEquals(_p6ReceiveStream, stream) && _p6ReceiveToken == ct) { _p6ReceiveStream = null; P6Invalidate(); } } }
    private void P6NoteVisualAtRead(int type, byte[] payload)
    {
        if (type == Protocol.W203Down) Wo214NoteHandAtRead(payload);
        if (type == Protocol.ConvDown || (type == Protocol.W164Down && payload.Length > 6 && payload[6] == Protocol.W164SceneBus))
            Interlocked.Increment(ref _p6VisualSerial);
    }
    private void P6Invalidate()
    {
        lock (_p6StateGate)
        {
            var old = _p6Context; _p6Context = null; _p6RemoteSteps.Clear();
            if (old is not null)
            {
                _ = old.Stop.CancelAsync(); // retained token must still cancel queued writer rows
                Wo214ResetOwnBow("session", Environment.TickCount64);
            }
        }
    }
    private async Task<RangedItemCatalog?> P6CatalogAsync()
    {
        Task<RangedItemCatalog?> task;
        lock (_p6StateGate)
        {
            if (_p6Catalog is not null) return _p6Catalog;
            if (_p6CatalogTask is null && Environment.TickCount64 >= _p6CatalogRetryMs)
            {
                _p6CatalogTask = Task.Run(() =>
                { try { string? path = TablesPakPath(); return path is null ? null : RangedItemCatalog.Read(path); } catch (Exception ex) { Console.WriteLine($"P6 item catalog unavailable: {ex.GetType().Name}"); return null; } });
            }
            if (_p6CatalogTask is null) return null;
            task = _p6CatalogTask;
        }
        var result = await task;
        lock (_p6StateGate)
        { _p6Catalog = result; if (result is null && ReferenceEquals(task, _p6CatalogTask)) { _p6CatalogTask = null; _p6CatalogRetryMs = Environment.TickCount64 + 30000; } }
        return result;
    }
    private void Wo201RangedTick()
    {
        if (!_w201Connected) { P6Invalidate(); return; }
        var context = P6GetContext();
        if (context is not null) Wo214HandHeartbeat(context);
        if (!W201("figure_ranged"))
        {
            if (Interlocked.Exchange(ref _p6CaptureWasOn, 0) == 1 && context is not null) _ = P6CaptureOffAsync(context);
            return; // presentation off never disables real contact evidence/Host acceptance
        }
        Interlocked.Exchange(ref _p6CaptureWasOn, 1);
        if (_localDowned || Environment.TickCount64 < _w201RangedRetryAtMs || context is null) return;
        _ = Wo201RangedPollAsync(context);
    }
    private async Task P6CaptureOffAsync(P6Context context)
    { try { await _combat.RangedPollAsync(false, context.Generation, () => P6Current(context) && !W201("figure_ranged"), context.Cancellation); } catch (OperationCanceledException) { } catch (IOException) { } }
    private async Task Wo201RangedPollAsync(P6Context context)
    {
        if (!await _p6PollGate.WaitAsync(0)) { T3Increment("ranged_poll_busy"); return; }
        long visualMode = Interlocked.Read(ref _p6VisualModeSerial);
        bool CurrentMode() => P6Current(context) && W201("figure_ranged") && visualMode == Interlocked.Read(ref _p6VisualModeSerial);
        try
        {
            var catalog = await P6CatalogAsync();
            for (int page = 0; page < 4 && CurrentMode(); page++)
            {
                T3Increment("ranged_poll_started");
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.Cancellation); stop.CancelAfter(1200);
                RangedPollReply? reply;
                try { reply = await _combat.RangedPollAsync(true, context.Generation, CurrentMode, stop.Token); }
                catch (OperationCanceledException) { if (P6Current(context)) T3Increment("ranged_poll_timeouts"); return; }
                if (!CurrentMode()) return;
                if (reply is null)
                { T3Increment("ranged_poll_refused"); _w201RangedRefusals++; _w201RangedRetryAtMs = Environment.TickCount64 + Math.Min(30000, 500L << Math.Min(6, _w201RangedRefusals)); return; }
                _w201RangedRefusals = 0; _w201RangedRetryAtMs = 0;
                foreach (var row in reply.Rows)
                {
                    long age = Environment.TickCount64 - unchecked((long)row.CapturedTick);
                    if (age < 0 || age > Protocol.RangedCaptureMaxAgeMs || unchecked((long)row.CapturedTick) <= Interlocked.Read(ref _p6VisualModeAtTick)) { T3Increment("ranged_stale"); continue; }
                    uint sourceMs = unchecked((uint)(PresentationClock.NowMs() - age));
                    var weapon = catalog?.Classify(row) ?? default;
                    Wo214ObserveOwnRangedHand(context, visualMode, row, weapon.Kind, weapon.ItemClass);
                    if (weapon.Kind == RangedWeaponKind.Unknown) T3Increment("ranged_capture_kind_unknown");
                    int written = 0;
                    void AfterWrite()
                    {
                        if (Interlocked.Exchange(ref written, 1) != 0) return;
                        T3Increment("ranged_out"); Interlocked.Increment(ref _w201RangedOut);
                        Console.WriteLine($"WO201-RANGED out {Wo201Rules.RangedPhaseName(row.Phase)} weapon={weapon.Kind} item={weapon.ItemClass:D} seq={row.Sequence} epoch={reply.Epoch}");
                        if (W214On("figure_hands") && weapon.Kind == RangedWeaponKind.Bow && weapon.ItemClass != Guid.Empty)
                        {
                            T3Increment(BowHandEmptyCounter, 0);
                            Console.WriteLine(FormattableString.Invariant($"MP-LANE1-BOW out cls={weapon.ItemClass:D} source=native phase={row.Phase} capture_ms={row.CapturedTick}"));
                        }
                    }
                    foreach (byte peer in Wo134Peers())
                    {
                        var step = new RangedStepV2(context.World, reply.Generation, reply.Epoch, row.Sequence, row.Phase,
                            weapon.Kind, weapon.ItemClass, (uint)age, row.Source);
                        await P6SendAsync(context, peer, Protocol.W164RangedStep, step.Encode(),
                            () => CurrentMode() && Environment.TickCount64 - unchecked((long)row.CapturedTick) <= Protocol.RangedCaptureMaxAgeMs,
                            context.Cancellation, AfterWrite, () => (step with { AgeMs = (uint)(Environment.TickCount64 - unchecked((long)row.CapturedTick)) }).Encode(),
                            presentationSenderMs: sourceMs);
                    }
                }
                if (reply.Rows.Count < RangedCaptureCodec.MaxRows) break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { T3Increment("ranged_poll_refused"); Console.WriteLine($"P6 ranged poll: {ex.GetType().Name}"); }
        finally { _p6PollGate.Release(); }
    }
    private Task<bool> P6SendAsync(P6Context context, byte target, byte kind, string text, Func<bool> extra,
        CancellationToken ct, Action? afterWrite = null, Func<string>? textAtWrite = null, uint presentationSenderMs = 0)
    {
        uint token = Interlocked.Increment(ref _w137Tok);
        byte[] Build(string value)
        {
            var packet = new LootMsg(kind, token, value).BuildUp(Protocol.W164Up, target);
            return (_w213Caps & Protocol.LinkCapPresentationCombat) == 0 ? packet :
                PresentationTimeWire.StampCombatUp(packet, presentationSenderMs);
        }
        void Written() { T3Increment("ranged_peer_tx"); afterWrite?.Invoke(); }
        return P6WriteAsync(context, Build(text), extra, ct, Written, textAtWrite is null ? null : () => Build(textAtWrite()));
    }

    private async Task<bool> P6WriteAsync(P6Context context, byte[] packet, Func<bool> extra, CancellationToken ct,
        Action written, Func<byte[]>? packetAtWrite = null)
    {
        var lease = ProcessorFrameScope.Capture();
        bool Current() => !ct.IsCancellationRequested && lease?.IsAbandoned != true && P6Current(context) && extra();
        if (!Current()) return false;
        try
        {
            if (_w213Writer is { } writer && ReferenceEquals(context.Stream, _w213Stream)) writer.Enqueue(packet, Current, written, packetAtWrite);
            else
            {
                using var frame = ProcessorFrameScope.Link(ct); using var linked = CancellationTokenSource.CreateLinkedTokenSource(frame.Token, context.Cancellation);
                await _tcpWriteLock.WaitAsync(linked.Token);
                try
                {
                    if (!Current()) return false;
                    if (packetAtWrite is not null) packet = packetAtWrite();
                    if (!Current()) return false;
                    await context.Stream.WriteAsync(packet, linked.Token);
                    if (_sessionRecorder is { } recorder) RecordRelayWrite(recorder, packet);
                    written();
                }
                finally { _tcpWriteLock.Release(); }
            }
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (IOException) { return false; }
    }
    private long P6PeerAtRead(int type, byte[] p)
    {
        if (type != Protocol.W164Down || p.Length == 0) return 0;
        return _p5Peers.TryGetValue(p[0], out var peer) && peer.Connected ? peer.Serial : 0;
    }
    private bool P6PeerCurrent(byte src, long serial) => serial != 0 && _p5Peers.TryGetValue(src, out var peer) && peer.Serial == serial && peer.Connected && !peer.Token.IsCancellationRequested;
    private static bool P6Message(int type, byte[] payload, out byte source, out LootMsg message)
    {
        source = 0; message = default;
        if (type != Protocol.W164Down || !Protocol.TrySplitJoinDown(payload, out source, out _, out _, out var body) || !LootMsg.TryDecode(body, out message)) return false;
        return message.Kind is Protocol.W164RangedStep or Protocol.W164MeasuredMissile || (message.Kind == Protocol.W164RealHit && message.Text.StartsWith("m1|", StringComparison.Ordinal));
    }
    // Consumes only these kinds before generic catchup/hold logic, preserving the original reader receipts.
    private async Task<bool> P6TryProcessFrameAsync(InFrame frame, CancellationToken ct)
    {
        if (!P6Message(frame.Type, frame.Payload, out byte src, out var message)) return false;
        if (!frame.P6Ready || P6GetContext() is not { } context || frame.P6Generation != context.Generation || frame.P6Serial != context.Serial ||
            frame.P6Life != context.Life || !P6PeerCurrent(src, frame.P6PeerSerial)) { T3Increment("ranged_stale"); return true; }
        if (message.Kind == Protocol.W164RangedStep) await Wo201OnRangedStepAsync(src, message, frame, context, ct);
        else if (message.Kind == Protocol.W164MeasuredMissile) await P6OnMissileRequestAsync(src, message.Text, frame, context, ct);
        else P6OnMissileResult(src, message.Text, context);
        return true;
    }
    private async Task Wo201OnRangedStepAsync(byte src, LootMsg message, InFrame frame, P6Context context, CancellationToken ct)
    {
        if (!W201("figure_ranged") || frame.P6ModeSerial != Interlocked.Read(ref _p6VisualModeSerial)) return;
        if (!RangedStepV2.TryDecode(message.Text, out var step)) { T3Increment("ranged_malformed"); return; }
        if (frame.Stamp == 0 || frame.Stamp != step.World || context.World != step.World ||
            Stopwatch.GetElapsedTime(frame.Arrival).TotalMilliseconds + step.AgeMs > Protocol.RangedCaptureMaxAgeMs) { T3Increment("ranged_stale"); return; }
        var body = frame.P6RangedBody;
        if (body.Entity == 0 || body != Wo214FigureBody(src))
        {
            T3Increment("ranged_stale");
            Wo201RangedRefused(src, step, body.Entity == 0 ? "no-body-at-read" : "old-body-at-read");
            return;
        }
        if (_p6RemoteSteps.TryGetValue(src, out var last) && last.Peer == frame.P6PeerSerial)
        {
            if (step.Generation < last.Gen || (step.Generation == last.Gen && step.Epoch < last.Epoch)) { T3Increment("ranged_stale"); return; }
            if (step.Generation == last.Gen && step.Epoch == last.Epoch && unchecked((int)(step.Sequence - last.Seq)) <= 0) { T3Increment("ranged_duplicate"); return; }
        }
        if (last.Gen != step.Generation || last.Epoch != step.Epoch || last.Peer != frame.P6PeerSerial) T3Increment("ranged_stream_reset");
        _p6RemoteSteps[src] = (frame.P6PeerSerial, step.Generation, step.Epoch, step.Sequence);
        T3Increment("ranged_in"); Interlocked.Increment(ref _w201RangedIn);
        var catalog = await P6CatalogAsync();
        if (!P6Current(context) || !P6PeerCurrent(src, frame.P6PeerSerial)) return;
        if (step.Kind == RangedWeaponKind.Unknown || catalog?.Kind(step.ItemClass) != step.Kind)
        { T3Increment("ranged_kind_unknown"); return; }
        bool Current() => P6Current(context) && W201("figure_ranged") && P6PeerCurrent(src, frame.P6PeerSerial) &&
            body == Wo214FigureBody(src) && Wo214RangedReceiptCurrent(src, context, frame.P6PeerSerial, body, message.Tok, step.ItemClass) &&
            frame.P6ModeSerial == Interlocked.Read(ref _p6VisualModeSerial) &&
            frame.P6VisualSerial == Interlocked.Read(ref _p6VisualSerial) && (!_w179Remote.TryGetValue(src, out var scene) || (scene.Ended && !scene.CopyPlaying && scene.Landing != 1)) &&
            _p6RemoteSteps.TryGetValue(src, out var latest) && latest == (frame.P6PeerSerial, step.Generation, step.Epoch, step.Sequence) &&
            Stopwatch.GetElapsedTime(frame.Arrival).TotalMilliseconds + step.AgeMs <= Protocol.RangedCaptureMaxAgeMs;
        if (!_p5Peers.TryGetValue(src, out var peer) || peer.Serial != frame.P6PeerSerial) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, context.Cancellation, peer.Token);
        long capturedAt = Environment.TickCount64 - step.AgeMs - (long)Stopwatch.GetElapsedTime(frame.Arrival).TotalMilliseconds;
        if (BowHandProof.IsDrawnPhase(step.Phase))
            await Wo214OnRangedHandAsync(src, step.ItemClass, context, frame.P6ModeSerial, frame.P6PeerSerial, capturedAt, Current, linked.Token);
        else if (step.Kind == RangedWeaponKind.Crossbow && !linked.IsCancellationRequested && Current())
            Wo214OnOtherRangedHand(src, step.ItemClass, capturedAt);
        // Charge and Aim still lack a verified animation entry. Crossbow reload tags vary by subclass.
        if (Wo201Rules.RangedFragment(step.Phase).Length == 0 || (step.Kind == RangedWeaponKind.Crossbow && step.Phase == 4))
        { T3Increment("ranged_phase_unrenderable"); return; }

        if (frame.Presentation.SampleMs == 0 || !W221On("rp_combat"))
        {
            Wo201RangedRefused(src, step, "typed-route-unavailable");
            return;
        }
        if (!_w214FigureHand.TryGetValue(src, out var hand) || !hand.Drawn || hand.Cls != step.ItemClass ||
            hand.Body != body || !Wo214HandCurrent(src, hand))
        {
            Wo201RangedRefused(src, step, "no-current-hand-revision");
            return;
        }

        // The hand record is intent. The typed native provider reads the actual hands before applying.
        bool CurrentHand() => Current() && W214On("figure_hands") &&
            _w214FigureHand.TryGetValue(src, out var latest) && Wo214SameHandState(latest, hand);
        var data = new byte[18];
        data[0] = (byte)step.Kind;
        data[1] = step.Phase;
        step.ItemClass.TryWriteBytes(data.AsSpan(2));
        var admission = await TrySubmitPresentationCombatAsync(src, body.Entity, frame.Presentation,
            unchecked((ushort)step.Sequence), 0x82, data, linked.Token, beforeWrite: CurrentHand);
        if (admission != PresentationCombatAdmission.Accepted)
        {
            Wo201RangedRefused(src, step, admission == PresentationCombatAdmission.Fallback ? "typed-unavailable" : "typed-suppressed");
            return;
        }
        Interlocked.Increment(ref _w201RangedDispatched);
        Console.WriteLine(FormattableString.Invariant(
            $"MP-RP-RANGED-IN peer={src} eid=0x{body.Entity:X} weapon={step.Kind} phase={step.Phase} seq={step.Sequence} event_host_ms={frame.Presentation.SampleMs} result=accepted body_generation={body.Generation} hand_rev={hand.Revision} cls={step.ItemClass:D}"));
    }

    private void Wo201RangedRefused(byte src, RangedStepV2 step, string reason)
    {
        T3Increment("ranged_animation_refused");
        long count = Interlocked.Increment(ref _w201RangedRefused);
        if (count <= 12 || count % 128 == 0)
            Console.WriteLine(FormattableString.Invariant(
                $"MP-RP-RANGED-IN peer={src} weapon={step.Kind} phase={step.Phase} seq={step.Sequence} result=refused reason={reason} n={count}"));
    }

    private string Wo201RangedStats() => $"ranged_out={Interlocked.Read(ref _w201RangedOut)} ranged_in={Interlocked.Read(ref _w201RangedIn)} ranged_dispatched={Interlocked.Read(ref _w201RangedDispatched)}";
}
