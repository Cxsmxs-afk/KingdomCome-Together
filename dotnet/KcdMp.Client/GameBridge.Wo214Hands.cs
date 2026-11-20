// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Collections.Concurrent;
using System.Globalization;
using KcdMp.Wire;

namespace KcdMp.Client;

// WO-214 F (figure_hands, both roles): the partner's figure holds the weapon the partner holds.
//   The field: the joiner drew; the host's figure of him ran its draw ("CombatViz: ghost 1 drew weapon") and showed no weapon. The
//   figure's weapon came only from the outfit packets and Human.DrawWeapon(), which draws the figure's active weapon set -- and nothing
//   ever compared the figure's hand with the partner's. After an inventory screen the joiner's outfit packet listed one class of
//   thirteen and the host's converge took the rest off the figure.
//   Each player's own hand rides to the partner at every draw, sheathe and change and repeats at the body heartbeat cadence on
//   the WO-203 side channel (W203FigureHand, "v1|drawn|class"). The partner's figure is made to hold that class (the mod's
//   KCD2MP_W214FigureHand: the game's own equip into its weapon set, then the draw; DrawFromInventory last), and the converge never
//   takes off the class its partner holds drawn.
// Log: WO214-HAND own ... -> sent (the sender), WO214-HAND figure kcd2mp_<id> ... (the receiver; the mod's own line says how).
public partial class GameBridge
{
    private const string BowHandEmptyCounter = "bow_hand_empty_while_drawn";
    // Retry at the former WO-214 hand cadence, after Lua's draw/holster checks can finish.
    private const int Wo214GameRequestIntervalMs = 10_000;
    private readonly record struct FigureBody(uint Entity, int Generation, uint LuaBirth);
    private readonly ConcurrentDictionary<string, (uint Entity, uint Birth)> _w214FigureBirths = new();
    private readonly string _w214OutfitAgent = Guid.NewGuid().ToString("N");
    private sealed record FigureHand(bool Drawn, Guid? Cls, long AtMs, long ChangedAtMs, long RequestedAtMs,
        P6Context Context, long PeerSerial, FigureBody Body, long Revision, long? RangedMode = null, uint? SourceToken = null);
    private sealed record OwnBow(P6Context Context, long Mode, BowHandProof Proof);
    private sealed record OwnHand(P6Context Context, bool Drawn, Guid? Cls, long Revision);
    private sealed record HandRepeat(OwnHand Hand, long PeerSerial, Guid? Cls, long Attempt);
    private readonly ConcurrentDictionary<byte, FigureHand> _w214FigureHand = new();
    private readonly ConcurrentDictionary<byte, FigureHand> _w214LastHeld = new();
    private sealed record EngineHand(FigureHand Desired, string Incarnation, long Connection, Wo214NativeHands Actual);
    private readonly ConcurrentDictionary<byte, EngineHand> _w214EngineHands = new();
    private readonly record struct FigureHandReceipt(Stream Stream, long PeerSerial, uint Token, FigureBody Body,
        Guid? DrawnClass, uint? PreviousStateToken);
    private readonly ConcurrentDictionary<byte, FigureHandReceipt> _w214HandAtRead = new();
    private long _w214FigureHandRevision, _w214HandStaleBody;
    private readonly object _w214OwnHandGate = new();
    private OwnBow? _w214OwnBow;
    private OwnHand? _w214OwnHand;
    private long _w214OwnHandRevision, _w214OwnHandHeartbeatAtMs;
    private readonly ConcurrentDictionary<byte, HandRepeat> _w214HandRepeat = new();
    private long _w214HandSendAttempt;
    private long _w214OwnHandChangedAtMs = long.MinValue;
    private long _w214HandSent, _w214HandIn, _w214HandOff, _w214HandOk, _w214HandFixed, _w214HandFailed, _w214HandKept, _w214PreDraw;

    private void Wo214ObserveOwnRangedHand(P6Context context, long mode, RangedCapture row, RangedWeaponKind kind, Guid cls)
    {
        var proof = new BowHandProof(cls, unchecked((long)row.CapturedTick));
        lock (_w214OwnHandGate)
        {
            if (!proof.Fresh(Environment.TickCount64) || proof.CapturedAt <= _w214OwnHandChangedAtMs ||
                !P6Current(context) || !W201("figure_ranged") || mode != Interlocked.Read(ref _p6VisualModeSerial) ||
                (_w214OwnBow is { } last && last.Proof.CapturedAt > proof.CapturedAt)) return;
            if (kind == RangedWeaponKind.Crossbow && cls != Guid.Empty)
            {
                Wo214ClearOwnBow("class-changed", proof.CapturedAt);
                if (_w214OwnHand is { Drawn: true, Cls: Guid held } && held != cls)
                    _w214OwnHand = null;
                return;
            }
            if (kind != RangedWeaponKind.Bow) return;
            if (cls == Guid.Empty)
            {
                if (W214On("figure_hands") && BowHandProof.IsDrawnPhase(row.Phase))
                {
                    T3Increment(BowHandEmptyCounter);
                    Console.WriteLine("MP-LANE1-BOW out cls=- source=native result=refused");
                    _w214OwnBow ??= new(context, mode, proof);
                }
                return;
            }
            _w214OwnBow = new(context, mode, proof);
            if (BowHandProof.IsDrawnPhase(row.Phase) &&
                (_w214OwnHand is not { Drawn: true } own || !ReferenceEquals(own.Context, context) ||
                 (own.Cls.HasValue && own.Cls != cls)))
            {
                _w214OwnHand = new(context, true, null, ++_w214OwnHandRevision);
                _w214OwnHandHeartbeatAtMs = Environment.TickCount64;
            }
        }
    }

    // Callers hold the hand gate so revocation and capture ordering use the same clock.
    private void Wo214ClearOwnBow(string reason, long atMs)
    {
        if (_w214OwnBow?.Proof.ItemClass is Guid cls && cls != Guid.Empty)
        {
            Console.WriteLine(FormattableString.Invariant($"MP-LANE1-BOW reset reason={reason} at_ms={atMs}"));
            if (_w214OwnHand is { Drawn: true } hand && (hand.Cls is null || hand.Cls == cls))
                _w214OwnHand = null;
        }
        _w214OwnBow = null;
        _w214OwnHandChangedAtMs = Math.Max(_w214OwnHandChangedAtMs, atMs);
    }

    private void Wo214ResetOwnBow(string reason, long atMs)
    {
        lock (_w214OwnHandGate)
        {
            Wo214ClearOwnBow(reason, atMs);
            if (reason == "session") { _w214OwnHand = null; _w214HandRepeat.Clear(); }
        }
    }

    /// <summary>This player's own hand (the mod's "w214_hand" event, "&lt;0|1&gt; &lt;class|-&gt;"): to the partner(s).</summary>
    private void Wo214OnOwnHand(string? arg)
    {
        if (!W214On("figure_hands")) return;
        if (!Wo214Rules.TryParseOwnHand(arg, out bool drawn, out Guid? cls)) { Console.WriteLine($"MP-W214 hand refused: '{arg}'"); return; }
        if (P6GetContext() is not { } context) return;
        if (!drawn || cls == Guid.Empty) cls = null;
        OwnHand hand;
        bool livenessOnly = false;
        long now;
        lock (_w214OwnHandGate)
        {
            now = Environment.TickCount64;
            if (!P6Current(context)) return;
            if (!drawn || (cls.HasValue && cls != _w214OwnBow?.Proof.ItemClass))
                Wo214ClearOwnBow(drawn ? "class-changed" : "sheathed", now);
            if (drawn && cls is null && _w214OwnHand is { Drawn: true, Cls: not null } last &&
                ReferenceEquals(last.Context, context)) livenessOnly = Wo214OwnHandProof(last) is null;
            else if (_w214OwnHand is not { } previous || !ReferenceEquals(previous.Context, context) ||
                     previous.Drawn != drawn || previous.Cls != cls)
                _w214OwnHand = new(context, drawn, cls, ++_w214OwnHandRevision);
            hand = _w214OwnHand;
            if (!livenessOnly) _w214OwnHandHeartbeatAtMs = now;
        }
        Wo214SendOwnHand(hand, false, livenessOnly);
    }

    private void Wo214HandHeartbeat(P6Context context)
    {
        if (!W214On("figure_hands")) return;
        OwnHand hand;
        long now;
        lock (_w214OwnHandGate)
        {
            now = Environment.TickCount64;
            if (_w214OwnHand is not { } current || !ReferenceEquals(current.Context, context) ||
                now - _w214OwnHandHeartbeatAtMs < Protocol.BodyState2HeartbeatMs) return;
            if (current.Drawn && current.Cls is null &&
                (Wo214OwnHandProof(current) is not { } proof || proof.Proof.ItemClass == Guid.Empty)) return;
            hand = current;
            _w214OwnHandHeartbeatAtMs = now;
        }
        Wo214SendOwnHand(hand, true);
    }

    private OwnBow? Wo214OwnHandProof(OwnHand hand) =>
        hand.Drawn && _w214OwnBow is { } bow && ReferenceEquals(bow.Context, hand.Context) &&
        Wo214OwnBowCurrent(bow) && (hand.Cls is null || hand.Cls == bow.Proof.ItemClass) ? bow : null;

    private void Wo214SendOwnHand(OwnHand hand, bool repeat, bool livenessOnly = false)
    {
        OwnBow? measured;
        long selectedAt;
        lock (_w214OwnHandGate)
        {
            if (!ReferenceEquals(_w214OwnHand, hand)) return;
            selectedAt = Environment.TickCount64;
            measured = Wo214OwnHandProof(hand);
            if (measured is { Proof.ItemClass: var item } && item == Guid.Empty)
            {
                if (measured.Proof.Fresh(selectedAt)) return;
                measured = null;
            }
        }
        Guid? cls = measured?.Proof.ItemClass ?? (livenessOnly ? null : hand.Cls);
        byte host = _jj?.Host ?? (_hostModeKnown ? _hostModeFrom : Protocol.JoinTargetHost);
        var peers = W137Host ? Wo134Peers() : W137Joiner && host != Protocol.JoinTargetHost ? [host] : new List<byte>();
        string value = Wo214Rules.HandText(hand.Drawn, cls);
        int targets = 0;
        foreach (byte peer in peers)
        {
            if (!_p5Peers.TryGetValue(peer, out var binding) || !Wo214PeerCurrent(peer, binding.Serial)) continue;
            byte target = W137Host ? peer : Protocol.JoinTargetHost;
            HandRepeat? queued = null;
            if (repeat)
            {
                if (_w214HandRepeat.TryGetValue(peer, out var pending) && ReferenceEquals(pending.Hand, hand) &&
                    pending.PeerSerial == binding.Serial && pending.Cls == cls) continue;
                _w214HandRepeat[peer] = queued = new(hand, binding.Serial, cls, Interlocked.Increment(ref _w214HandSendAttempt));
            }
            targets++;
            _ = Wo214SendHandAsync(peer, target, binding.Serial, value, hand, measured, selectedAt, queued);
        }
        long k = Interlocked.Increment(ref _w214HandSent);
        if (k <= 200 || k % 50 == 0)
            Console.WriteLine($"WO214-HAND own drawn={(hand.Drawn ? 1 : 0)} cls={(cls is Guid g ? g.ToString("D") : "-")} -> sent to {targets} [n={k}]");
    }

    private bool Wo214OwnBowCurrent(OwnBow bow) =>
        P6Current(bow.Context) && W201("figure_ranged") && bow.Mode == Interlocked.Read(ref _p6VisualModeSerial);

    private bool Wo214PeerCurrent(byte peer, long serial) => !_peers.IsRemoved(peer) && P6PeerCurrent(peer, serial);

    private async Task Wo214SendHandAsync(byte peer, byte target, long peerSerial, string text, OwnHand hand,
        OwnBow? bow, long selectedAt, HandRepeat? repeat)
    {
        void ReleaseRepeat()
        {
            if (repeat is not null) _w214HandRepeat.TryRemove(KeyValuePair.Create(peer, repeat));
        }
        bool Current()
        {
            lock (_w214OwnHandGate)
            {
                var currentProof = Wo214OwnHandProof(hand);
                if (currentProof is { Proof.ItemClass: var item } && item == Guid.Empty &&
                    !currentProof.Proof.Fresh(Environment.TickCount64)) currentProof = null;
                bool current = ReferenceEquals(_w214OwnHand, hand) && ReferenceEquals(currentProof, bow) &&
                    Wo214PeerCurrent(peer, peerSerial) && W214On("figure_hands") &&
                    (repeat is null || ReferenceEquals(_w214HandRepeat.GetValueOrDefault(peer), repeat));
                if (!current) ReleaseRepeat();
                return current;
            }
        }
        var packet = new LootMsg(Protocol.W203FigureHand, Interlocked.Increment(ref _w137Tok), text).BuildUp(Protocol.W203Up, target);
        void Written()
        {
            ReleaseRepeat();
            if (bow is not null)
            {
                T3Increment(BowHandEmptyCounter, 0);
                Console.WriteLine(FormattableString.Invariant($"MP-LANE1-BOW out cls={bow.Proof.ItemClass:D} source=wo214 age_ms={selectedAt - bow.Proof.CapturedAt} capture_ms={bow.Proof.CapturedAt}"));
            }
            else if (!hand.Drawn)
                Console.WriteLine(FormattableString.Invariant($"MP-LANE1-BOW out cls=- source=wo214 drawn=0 repeat={(repeat is null ? 0 : 1)} revision={hand.Revision}"));
        }
        if (!await P6WriteAsync(hand.Context, packet, Current, hand.Context.Cancellation, Written)) ReleaseRepeat();
    }

    private void Wo214NoteHandAtRead(byte[] payload)
    {
        if (_p6ReceiveStream is not { } stream ||
            !Protocol.TrySplitJoinDown(payload, out byte src, out _, out _, out var body) ||
            !LootMsg.TryDecode(body, out var message) || message.Kind != Protocol.W203FigureHand ||
            !Wo214Rules.TryParseHand(message.Text, out bool drawn, out Guid? cls) ||
            (drawn && (cls is null || cls == Guid.Empty)) ||
            !_p5Peers.TryGetValue(src, out var peer) || !P6PeerCurrent(src, peer.Serial)) return;
        var bodyAtRead = Wo214FigureBody(src);
        Guid? drawnClass = drawn ? cls : null;
        uint? previousStateToken = null;
        if (_w214HandAtRead.TryGetValue(src, out var old) && ReferenceEquals(old.Stream, stream) &&
            old.PeerSerial == peer.Serial && old.Body == bodyAtRead)
            previousStateToken = old.DrawnClass == drawnClass ? old.PreviousStateToken : old.Token;
        _w214HandAtRead[src] = new(stream, peer.Serial, message.Tok, bodyAtRead, drawnClass, previousStateToken);
    }

    private bool Wo214RangedReceiptCurrent(byte src, P6Context context, long peerSerial, FigureBody body, uint token, Guid cls) =>
        !_w214HandAtRead.TryGetValue(src, out var receipt) || !ReferenceEquals(receipt.Stream, context.Stream) ||
        receipt.PeerSerial != peerSerial || receipt.Body != body ||
        unchecked((int)(receipt.Token - token)) <= 0 ||
        (receipt.DrawnClass == cls && (receipt.PreviousStateToken is not uint previous || unchecked((int)(previous - token)) <= 0));

    private bool Wo214HandReceiptChanged(byte src, FigureHand hand) =>
        hand.SourceToken is uint token && _w214HandAtRead.TryGetValue(src, out var receipt) &&
        ReferenceEquals(receipt.Stream, hand.Context.Stream) && receipt.PeerSerial == hand.PeerSerial &&
        receipt.Body == hand.Body && unchecked((int)(receipt.Token - token)) > 0 &&
        (receipt.DrawnClass != (hand.Drawn ? hand.Cls : null) ||
         (receipt.PreviousStateToken is uint previous && unchecked((int)(previous - token)) > 0));

    /// <summary>The partner's hand (a W203 FigureHand row from <paramref name="src"/>): his figure here is made to hold it.</summary>
    private void Wo214OnFigureHand(byte src, LootMsg m)
    {
        if (!Wo214Rules.TryParseHand(m.Text, out bool drawn, out Guid? cls)) { Interlocked.Increment(ref _w203SideMalformed); return; }
        Interlocked.Increment(ref _w214HandIn);
        if (!W214On("figure_hands")) { Interlocked.Increment(ref _w214HandOff); return; }
        if (!_p5Peers.TryGetValue(src, out var peer) || !Wo214PeerCurrent(src, peer.Serial) ||
            P6GetContext() is not { } context) return;
        if (!drawn || cls == Guid.Empty) cls = null;
        bool livenessOnly = drawn && cls is null;
        var body = Wo214FigureBody(src);
        if (_w214HandAtRead.TryGetValue(src, out var receipt) &&
            (!ReferenceEquals(receipt.Stream, context.Stream) || receipt.PeerSerial != peer.Serial || receipt.Body != body ||
             (livenessOnly ? unchecked((int)(m.Tok - receipt.Token)) < 0 : receipt.Token != m.Tok)))
        {
            if (receipt.Body != body)
            {
                long count = Interlocked.Increment(ref _w214HandStaleBody);
                if (count <= 12 || count % 128 == 0)
                    Console.WriteLine(FormattableString.Invariant(
                        $"MP-LANE1-BOW in peer={src} body_generation={body.Generation} result=hand-rejected reason=old-body n={count}"));
            }
            return;
        }
        long now = Environment.TickCount64;
        if (_w214FigureHand.TryGetValue(src, out var hand) && Wo214HandCurrent(src, hand))
        {
            if (livenessOnly || (drawn == hand.Drawn && cls == hand.Cls && !Wo214HandReceiptChanged(src, hand)))
            {
                var heard = hand with { AtMs = now, SourceToken = livenessOnly ? hand.SourceToken : m.Tok };
                if (!_w214FigureHand.TryUpdate(src, heard, hand)) return;
                if (!livenessOnly && now - hand.RequestedAtMs >= Wo214GameRequestIntervalMs)
                    _ = Wo214ApplyWireHandAsync(src, heard with { RequestedAtMs = now }, peer.Token);
                return;
            }
        }
        if (livenessOnly) return;
        _ = Wo214ApplyWireHandAsync(src, new(drawn, cls, now, now, now, context, peer.Serial, body,
            Interlocked.Increment(ref _w214FigureHandRevision), SourceToken: m.Tok), peer.Token);
    }

    private async Task Wo214ApplyWireHandAsync(byte src, FigureHand hand, CancellationToken peerToken)
    {
        try
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(hand.Context.Cancellation, peerToken);
            await Wo214SetFigureHandAsync(src, hand, stop.Token);
            if (!hand.Drawn && Wo214HandCurrent(src, hand) &&
                _w214FigureHand.TryGetValue(src, out var latest) && Wo214SameHandState(latest, hand))
                Console.WriteLine(FormattableString.Invariant($"MP-LANE1-BOW in peer={src} cls=- result=hand-sheathed"));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine(FormattableString.Invariant($"WO214-HAND figure kcd2mp_{src} request failed ({ex.GetType().Name})"));
        }
    }

    private FigureBody Wo214FigureBody(byte src)
    {
        string id = src.ToString(CultureInfo.InvariantCulture);
        uint entity = _ghostEntityIds.GetValueOrDefault(id);
        var birth = _w214FigureBirths.GetValueOrDefault(id);
        return new(entity, GhostGeneration(id), birth.Entity == entity ? birth.Birth : 0);
    }

    private bool Wo214NoteFigureBirth(string id, uint entity, uint birth)
    {
        if (birth == 0) return false;    // the legacy two-field report has no incarnation
        var previous = _w214FigureBirths.GetValueOrDefault(id);
        _w214FigureBirths[id] = (entity, birth);
        return previous.Entity == entity && previous.Birth != 0 && previous.Birth != birth;
    }

    private bool Wo214HandCurrent(byte src, FigureHand hand) =>
        P6Current(hand.Context) && Wo214PeerCurrent(src, hand.PeerSerial) && hand.Body == Wo214FigureBody(src) &&
        (hand.RangedMode is not long mode || (W201("figure_ranged") && mode == Interlocked.Read(ref _p6VisualModeSerial)));

    private static bool Wo214SameHandState(FigureHand? left, FigureHand? right) =>
        left?.Revision == right?.Revision;

    private void Wo214OnOtherRangedHand(byte src, Guid cls, FigureBody body, long capturedAt)
    {
        bool Replaced(FigureHand hand) => hand.Body == body && Wo214HandCurrent(src, hand) && hand.Drawn && hand.Cls is Guid held &&
            held != cls && (hand.RangedMode.HasValue || hand.ChangedAtMs <= capturedAt);
        if (_w214FigureHand.TryGetValue(src, out var hand) && Replaced(hand))
            _w214FigureHand.TryRemove(KeyValuePair.Create(src, hand));
        if (_w214LastHeld.TryGetValue(src, out var lastHeld) && Replaced(lastHeld))
            _w214LastHeld.TryRemove(KeyValuePair.Create(src, lastHeld));
    }

    private async Task Wo214OnRangedHandAsync(byte src, Guid cls, P6Context context, long mode, long peerSerial,
        FigureBody body, long capturedAt, Func<bool> current, CancellationToken ct, uint? sourceToken)
    {
        if (!W214On("figure_hands") || cls == Guid.Empty || !current() || ct.IsCancellationRequested) return;
        long now = Environment.TickCount64;
        var hand = new FigureHand(true, cls, now, now, now, context, peerSerial, body,
            Interlocked.Increment(ref _w214FigureHandRevision), mode, sourceToken);
        if (_w214FigureHand.TryGetValue(src, out var previous) && previous.Body == body && Wo214HandCurrent(src, previous))
        {
            if (previous.ChangedAtMs > capturedAt &&
                (!previous.Drawn || (previous.Cls is Guid held && held != cls))) return;
            if (previous.Drawn && previous.Cls == cls && !Wo214HandReceiptChanged(src, previous))
            {
                hand = hand with { ChangedAtMs = previous.ChangedAtMs, Revision = previous.Revision };
                if (now - previous.RequestedAtMs < Wo214GameRequestIntervalMs)
                {
                    // An unchanged observation does not reset a confirmed slot or restart a pending apply.
                    _w214FigureHand.TryUpdate(src, previous with { AtMs = now }, previous);
                    return;
                }
            }
        }
        try
        {
            await Wo214SetFigureHandAsync(src, hand, ct);
            if (current()) Console.WriteLine(FormattableString.Invariant($"MP-LANE1-BOW in peer={src} cls={cls:D} result=hand-requested"));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _w214FigureHand.TryRemove(KeyValuePair.Create(src, hand));
        }
        catch (Exception ex)
        {
            _w214FigureHand.TryRemove(KeyValuePair.Create(src, hand));
            Console.WriteLine(FormattableString.Invariant($"MP-LANE1-BOW in peer={src} cls={cls:D} result=hand-request-failed reason={ex.GetType().Name}"));
        }
    }

    private async Task Wo214SetFigureHandAsync(byte src, FigureHand hand, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!Wo214HandCurrent(src, hand))
            throw new OperationCanceledException("figure hand belongs to a different body");
        _w214FigureHand[src] = hand;
        if (hand.Drawn && hand.Cls.HasValue)
        {
            var held = _w214LastHeld.GetValueOrDefault(src);
            if (!Wo214SameHandState(held, hand) || held?.RangedMode != hand.RangedMode) _w214LastHeld[src] = hand;
        }
        string? incarnation = Wo214OutfitIncarnation(src);
        long connection = _combat.EdgeConnectionRevision;
        bool Current() => incarnation is not null && Wo214EngineHandCurrent(src, hand, incarnation, connection);
        if (!Current() || !uint.TryParse(incarnation, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint spawn)) return;
        bool oversized = false;
        if (hand.Drawn && hand.Cls is Guid gc && _swingCatalog.IsCompletedSuccessfully && _swingCatalog.Result is { } cat)
            oversized = cat.OversizedItemOf([gc]) is not null;
        byte flags = (byte)((hand.Drawn ? 1 : 0) | (oversized ? 2 : 0));
        await ApplyOutfitHandEdgeAsync(src, hand, async () =>
        {
            if (!Current()) return;
            var declared = await _combat.RequestRangedHandAsync(src, hand.Body.Entity, hand.PeerSerial, hand.Body.Generation,
                hand.Revision, hand.Cls ?? Guid.Empty, connection, Current, ct,
                new Wo214HandDeclaration(flags, spawn, hand.Context.World, hand.Context.Serial));
            if (declared is null || !Current()) return;
            Console.WriteLine($"WO214-HAND figure kcd2mp_{src} told drawn={(hand.Drawn ? 1 : 0)} cls={hand.Cls?.ToString("D") ?? "-"}{(oversized ? " (oversized)" : "")}");
        }, ct);
    }

    private bool Wo214EngineHandCurrent(byte peer, FigureHand hand, string incarnation, long connection) =>
        hand.Body.Entity != 0 && connection == _combat.EdgeConnectionRevision && W214On("figure_hands") &&
        incarnation == Wo214OutfitIncarnation(peer) && Wo214HandCurrent(peer, hand) &&
        _w214FigureHand.TryGetValue(peer, out var latest) && Wo214SameHandState(latest, hand) &&
        !Wo214HandReceiptChanged(peer, latest);

    private bool Wo214ConfirmedRangedHand(byte peer, FigureHand hand, Guid cls) =>
        _w214EngineHands.TryGetValue(peer, out var proof) && Wo214SameHandState(proof.Desired, hand) &&
        Wo214EngineHandCurrent(peer, hand, proof.Incarnation, proof.Connection) && proof.Actual.Holds(cls);

    // A Lua disposition permits a strict native readback. It does not confirm either hand.
    private void Wo214OnFigureHandResult(string? arg)
    {
        var p = (arg ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 2) return;
        long k = p[1] switch
        {
            "ok" => Interlocked.Increment(ref _w214HandOk),
            "fixed" => Interlocked.Increment(ref _w214HandFixed),
            _ => Interlocked.Increment(ref _w214HandFailed),
        };
        if (k <= 200 || k % 50 == 0) Console.WriteLine($"WO214-HAND figure kcd2mp_{p[0]} {p[1]} ({(p.Length > 2 ? p[2] : "-")}) [n={k}]");
        if (p.Length == 5 && p[1] is "ok" or "fixed" or "failed" &&
            byte.TryParse(p[0], NumberStyles.None, CultureInfo.InvariantCulture, out byte peer) &&
            _w214FigureHand.TryGetValue(peer, out var hand) && p[4] == Wo214OutfitHandScope(hand) &&
            _p5Peers.TryGetValue(peer, out var binding) && binding.Serial == hand.PeerSerial)
            _ = Wo214ObserveFigureHandAsync(peer, hand, p[3], p[4], binding.Token);
    }

    private async Task Wo214ObserveFigureHandAsync(byte peer, FigureHand hand, string incarnation, string scope, CancellationToken peerToken)
    {
        long connection = _combat.EdgeConnectionRevision;
        bool Current() => Wo214EngineHandCurrent(peer, hand, incarnation, connection);
        if (!Current()) return;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(hand.Context.Cancellation, peerToken);
        try
        {
            var result = await _combat.RequestRangedHandAsync(peer, hand.Body.Entity, hand.PeerSerial, hand.Body.Generation,
                hand.Revision, hand.Cls ?? Guid.Empty, connection, Current, stop.Token);
            if (result is not { } actual || !Current()) return;
            _w214EngineHands[peer] = new(hand, incarnation, connection, actual);
            string left = actual.Left == Guid.Empty ? "-" : actual.Left.ToString("D");
            string right = actual.Right == Guid.Empty ? "-" : actual.Right.ToString("D");
            Console.WriteLine(FormattableString.Invariant($"WO214-HAND native peer={peer} world={hand.Context.World:X8} body_generation={hand.Body.Generation} revision={hand.Revision} known={actual.Known} left={left} right={right}"));
            await _transport.ExecuteNowAsync(FormattableString.Invariant(
                $"if KCD2MP_W214NativeHandProof then KCD2MP_W214NativeHandProof(\"{peer}\", \"{incarnation}\", \"{scope}\", {actual.Known}, \"{left}\", \"{right}\") end"), stop.Token);
            if (Current() && !hand.Drawn && actual.Empty) Wo214OutfitHandConfirmed(peer, hand);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Console.WriteLine(FormattableString.Invariant($"WO214-HAND figure kcd2mp_{peer} native observation failed ({ex.GetType().Name})"));
        }
    }

    /// <summary>F: the converge's target for figure <paramref name="ghostId"/> keeps the class its partner holds drawn.</summary>
    private HashSet<Guid> Wo214KeepHeld(byte ghostId, HashSet<Guid> target)
    {
        if (!W214On("figure_hands") || !_w214FigureHand.TryGetValue(ghostId, out var h)
            || !Wo214HandCurrent(ghostId, h)) return target;
        var kept = Wo214Rules.KeepHeld(target, true, h.Drawn, h.Cls);
        if (kept.Count > target.Count)
        {
            long k = Interlocked.Increment(ref _w214HandKept);
            if (k <= 50 || k % 50 == 0)
                Console.WriteLine($"WO214-HAND figure kcd2mp_{ghostId}: its outfit packet lacks the weapon in its partner's hand ({h.Cls:D}) -- kept on the figure [n={k}]");
        }
        return kept;
    }

    /// <summary>
    /// F, at the partner's draw (his combat event, before his figure's own draw): the weapon he last held drawn is still on his figure,
    /// or it is put back first -- so that draw shows it at once (the hand message's fix comes only after his hand filled: 1.8 s on the
    /// bench). The field: his figure lost its weapon to the converge long before his next draw.
    /// </summary>
    private async Task Wo214PreDrawAsync(byte ghostId, CancellationToken ct)
    {
        if (!W214On("figure_hands") || !_w214LastHeld.TryGetValue(ghostId, out var held) || held.Cls is not Guid cls ||
            !Wo214HandCurrent(ghostId, held) || Wo214OutfitIncarnation(ghostId) is not { } incarnation) return;
        var beforeRead = _w214FigureHand.GetValueOrDefault(ghostId);
        string soul = $"kcd2mp_{ghostId}";
        var actual = await ReadGhostSetAsync(soul, ct);
        if (actual is null || actual.Contains(cls) || !W214On("figure_hands") ||
            !ReferenceEquals(_w214LastHeld.GetValueOrDefault(ghostId), held) ||
            !Wo214SameHandState(_w214FigureHand.GetValueOrDefault(ghostId), beforeRead) ||
            !Wo214HandCurrent(ghostId, held) || Wo214OutfitIncarnation(ghostId) != incarnation) return;
        var scope = new OutfitEngineScope(ghostId, incarnation, beforeRead is null ? "-" : Wo214OutfitHandScope(beforeRead),
            Interlocked.Increment(ref _outfitOrder));
        try { await OutfitMutationAsync(scope, "equip", cls.ToString("D"), ct); }
        catch (Exception ex) { Console.WriteLine($"WO214-HAND figure {soul}: its partner's weapon {cls:D} was not put back before his draw ({ex.Message})"); return; }
        if (!OutfitAttemptedInEngine(scope)) return;
        long k = Interlocked.Increment(ref _w214PreDraw);
        if (k <= 200 || k % 50 == 0)
            Console.WriteLine($"WO214-HAND figure {soul}: scoped equip for its partner's weapon {cls:D} reached the engine before draw [n={k}]");
    }

    private void Wo214HandsReset()
    {
        _w214FigureHand.Clear();
        _w214LastHeld.Clear();
        _w214EngineHands.Clear();
        _w214HandAtRead.Clear();
        Wo214ResetOwnBow("session", Environment.TickCount64);
    }

    // ---- the bench's fault (KCDMP_TEST_W214_FIGURE_STRIP=1; the host's agent only, never set by an installer) ----
    // The field's end state, made at will: the partner's figure without the weapon its partner draws. On the bench a figure whose outfit
    // packets are whole draws its weapon on today's path too (the d5 run: 10 of 10), so the field's failure needs its state: after the
    // joiner's inventory screen his outfit packet listed one class of thirteen and the converge took pieces off his figure; his next draw
    // showed nothing. Here the weapon his figure wears is taken off just before each of his draws (the REST unequip the converge uses).
    private static readonly bool W214FigureStrip = Environment.GetEnvironmentVariable("KCDMP_TEST_W214_FIGURE_STRIP") == "1";
    private long _w214Stripped;

    private async Task Wo214TestStripAsync(byte ghostId)
    {
        if (!W214FigureStrip || !W137Host) return;
        var catalog = _swingCatalog.IsCompletedSuccessfully ? _swingCatalog.Result : null;
        if (catalog is null || !_ghostAppearance.TryGetValue(ghostId, out var applied)) return;
        Guid? cls;
        lock (applied) cls = catalog.MainHandItemOf(applied.ToList());
        if (cls is not Guid c) return;
        try { await _transport.UnequipItemOnGhostAsync($"kcd2mp_{ghostId}", c); }
        catch (Exception ex) { Console.WriteLine($"WO214-TEST strip figure kcd2mp_{ghostId}: the unequip failed ({ex.Message})"); return; }
        long k = Interlocked.Increment(ref _w214Stripped);
        Console.WriteLine($"WO214-TEST strip figure kcd2mp_{ghostId}: its main-hand weapon {c:D} taken off just before its draw (the field's end state) [n={k}]");
    }

    private string Wo214HandsStats() =>
        $" hand_sent={Interlocked.Read(ref _w214HandSent)} hand_in={Interlocked.Read(ref _w214HandIn)} hand_off={Interlocked.Read(ref _w214HandOff)}"
        + $" figure_hand_ok={Interlocked.Read(ref _w214HandOk)} figure_hand_fixed={Interlocked.Read(ref _w214HandFixed)} figure_hand_failed={Interlocked.Read(ref _w214HandFailed)}"
        + $" figure_hand_kept={Interlocked.Read(ref _w214HandKept)} figure_predraw_put_back={Interlocked.Read(ref _w214PreDraw)}";
}
