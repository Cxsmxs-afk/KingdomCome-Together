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
    { Interlocked.Exchange(ref _p6VisualModeAtTick, Environment.TickCount64); Interlocked.Increment(ref _p6VisualModeSerial); }
    private readonly SemaphoreSlim _p6PollGate = new(1, 1);
    private RangedItemCatalog? _p6Catalog;
    private Task<RangedItemCatalog?>? _p6CatalogTask;
    private long _p6CatalogRetryMs, _w201RangedRetryAtMs;
    private int _w201RangedRefusals;
    private int _p6CaptureWasOn;
    private long _w201RangedOut, _w201RangedIn, _w201RangedShown;
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
        if (type == Protocol.ConvDown || (type == Protocol.W164Down && payload.Length > 6 && payload[6] == Protocol.W164SceneBus))
            Interlocked.Increment(ref _p6VisualSerial);
    }
    private void P6Invalidate()
    {
        lock (_p6StateGate)
        {
            var old = _p6Context; _p6Context = null; _p6RemoteSteps.Clear();
            if (old is not null) _ = old.Stop.CancelAsync(); // retained token must still cancel queued writer rows
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
        if (!W201("figure_ranged"))
        {
            if (Interlocked.Exchange(ref _p6CaptureWasOn, 0) == 1 && P6GetContext() is { } offContext) _ = P6CaptureOffAsync(offContext);
            return; // presentation off never disables real contact evidence/Host acceptance
        }
        Interlocked.Exchange(ref _p6CaptureWasOn, 1);
        if (_localDowned || Environment.TickCount64 < _w201RangedRetryAtMs || P6GetContext() is not { } context) return;
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
                    if (weapon.Kind == RangedWeaponKind.Unknown) T3Increment("ranged_capture_kind_unknown");
                    int written = 0;
                    void AfterWrite()
                    {
                        if (Interlocked.Exchange(ref written, 1) != 0) return;
                        T3Increment("ranged_out"); Interlocked.Increment(ref _w201RangedOut);
                        Console.WriteLine($"WO201-RANGED out {Wo201Rules.RangedPhaseName(row.Phase)} weapon={weapon.Kind} item={weapon.ItemClass:D} seq={row.Sequence} epoch={reply.Epoch}");
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
    private async Task<bool> P6SendAsync(P6Context context, byte target, byte kind, string text, Func<bool> extra,
        CancellationToken ct, Action? afterWrite = null, Func<string>? textAtWrite = null, uint presentationSenderMs = 0)
    {
        var lease = ProcessorFrameScope.Capture();
        bool Current() => !ct.IsCancellationRequested && lease?.IsAbandoned != true && P6Current(context) && extra();
        if (!Current()) return false;
        uint token = Interlocked.Increment(ref _w137Tok);
        byte[] Build(string value)
        {
            var packet = new LootMsg(kind, token, value).BuildUp(Protocol.W164Up, target);
            return (_w213Caps & Protocol.LinkCapPresentationCombat) == 0 ? packet :
                PresentationTimeWire.StampCombatUp(packet, presentationSenderMs);
        }
        byte[] packet = Build(text);
        void Written() { T3Increment("ranged_peer_tx"); afterWrite?.Invoke(); }
        try
        {
            if (_w213Writer is { } writer && ReferenceEquals(context.Stream, _w213Stream)) writer.Enqueue(packet, Current, Written, textAtWrite is null ? null : () => Build(textAtWrite()));
            else
            {
                using var frame = ProcessorFrameScope.Link(ct); using var linked = CancellationTokenSource.CreateLinkedTokenSource(frame.Token, context.Cancellation);
                await _tcpWriteLock.WaitAsync(linked.Token);
                try { if (!Current()) return false; if (textAtWrite is not null) packet = Build(textAtWrite()); if (!Current()) return false; await context.Stream.WriteAsync(packet, linked.Token); Written(); }
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
    private static bool P6Frame(int type, byte[] payload) => P6Message(type, payload, out _, out _);
    // Consumes only these kinds before generic catchup/hold logic, preserving the original reader receipts.
    private async Task<bool> P6TryProcessFrameAsync(InFrame frame, CancellationToken ct)
    {
        if (!P6Message(frame.Type, frame.Payload, out byte src, out var message)) return false;
        if (!frame.P6Ready || P6GetContext() is not { } context || frame.P6Generation != context.Generation || frame.P6Serial != context.Serial ||
            frame.P6Life != context.Life || !P6PeerCurrent(src, frame.P6PeerSerial)) { T3Increment("ranged_stale"); return true; }
        if (message.Kind == Protocol.W164RangedStep) await Wo201OnRangedStepAsync(src, message.Text, frame, context, ct);
        else if (message.Kind == Protocol.W164MeasuredMissile) await P6OnMissileRequestAsync(src, message.Text, frame, context, ct);
        else P6OnMissileResult(src, message.Text, context);
        return true;
    }
    private async Task Wo201OnRangedStepAsync(byte src, string text, InFrame frame, P6Context context, CancellationToken ct)
    {
        if (!W201("figure_ranged") || frame.P6ModeSerial != Interlocked.Read(ref _p6VisualModeSerial)) return;
        if (!RangedStepV2.TryDecode(text, out var step)) { T3Increment("ranged_malformed"); return; }
        if (frame.Stamp == 0 || frame.Stamp != step.World || context.World != step.World ||
            Stopwatch.GetElapsedTime(frame.Arrival).TotalMilliseconds + step.AgeMs > Protocol.RangedCaptureMaxAgeMs) { T3Increment("ranged_stale"); return; }
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
        // No guessed Aim/Charge animation and no broad BodyStop that could abort a scene.
        string fragment = Wo201Rules.RangedFragment(step.Phase);
        if (fragment.Length == 0) { T3Increment("ranged_phase_unrenderable"); return; }
        // The old goatsfoot/medium reload is not proof for all crossbow subclasses.
        if (step.Kind == RangedWeaponKind.Crossbow && step.Phase == 4) { T3Increment("ranged_phase_unrenderable"); return; }
        string tags = step.Kind == RangedWeaponKind.Bow ? "l_bow" : "l_crossbow";
        bool Current() => P6Current(context) && W201("figure_ranged") && P6PeerCurrent(src, frame.P6PeerSerial) &&
            frame.P6ModeSerial == Interlocked.Read(ref _p6VisualModeSerial) &&
            frame.P6VisualSerial == Interlocked.Read(ref _p6VisualSerial) && (!_w179Remote.TryGetValue(src, out var scene) || (scene.Ended && !scene.CopyPlaying && scene.Landing != 1)) &&
            _p6RemoteSteps.TryGetValue(src, out var latest) && latest == (frame.P6PeerSerial, step.Generation, step.Epoch, step.Sequence) &&
            Stopwatch.GetElapsedTime(frame.Arrival).TotalMilliseconds + step.AgeMs <= Protocol.RangedCaptureMaxAgeMs;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, context.Cancellation, _p5Peers[src].Token);
        if (frame.Presentation.SampleMs != 0 && W221On("rp_combat") &&
            _ghostEntityIds.TryGetValue(src.ToString(System.Globalization.CultureInfo.InvariantCulture), out uint eid))
        {
            var data = new byte[18];
            data[0] = (byte)step.Kind;
            data[1] = step.Phase;
            step.ItemClass.TryWriteBytes(data.AsSpan(2));
            var admission = await TrySubmitPresentationCombatAsync(src, eid, frame.Presentation,
                unchecked((ushort)step.Sequence), 0x82, data, linked.Token, beforeWrite: Current);
            if (admission != PresentationCombatAdmission.Fallback)
            {
                string result = admission == PresentationCombatAdmission.Accepted ? "accepted" : "suppressed";
                Console.WriteLine(FormattableString.Invariant($"MP-RP-RANGED-IN peer={src} eid=0x{eid:X} weapon={step.Kind} phase={step.Phase} seq={step.Sequence} event_host_ms={frame.Presentation.SampleMs} result={result}"));
                return;
            }
        }
        var id = await _combat.RangedOneShotAsync(Wo141Rules.AvatarName(src), fragment, tags, Current, linked.Token);
        if (id is >= 0 && Current()) { Interlocked.Increment(ref _w201RangedShown); if (step.Kind == RangedWeaponKind.Bow) T3Increment("ranged_bow_played"); }
        else T3Increment("ranged_animation_refused");
    }
    private string Wo201RangedStats() => $"ranged_out={Interlocked.Read(ref _w201RangedOut)} ranged_in={Interlocked.Read(ref _w201RangedIn)} ranged_shown={Interlocked.Read(ref _w201RangedShown)}";
}
