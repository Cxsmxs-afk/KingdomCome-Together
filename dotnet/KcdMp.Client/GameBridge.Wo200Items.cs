// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Globalization;
using KcdMp.Wire;

namespace KcdMp.Client;

/// <summary>
/// WO-200 I.3 (qitem_mirror): quest items go to both players, and the host's counts decide. In this game a quest item exists exactly
/// while its AddQuestItem effect is active, and every such effect takes IsActive from a quest value (433 of 433 in the data); the engine
/// refuses to create, delete or move a quest item any other way. The item therefore follows the host through the quest-state mirror,
/// and this part measures it: the classes are the game's own IsQuestItem rows (Tables.pak, WO-124's list); the host's script reports its
/// Henry's count of each when it changes (and a full baseline, including zeros, every 60 s); the joiner's compares (agreed / differs; "pending" while his own
/// replay may still be confirmed, at most 5 minutes). Only the player's inventory is counted (the stash is the host's).
/// </summary>
public sealed partial class GameBridge
{
    private volatile bool _w200QPushed;
    private readonly SemaphoreSlim _w200QClassGate = new(1, 1);
    private string _w200QPeerSet = "";
    private long _w200QClassLife = -1;
    private long _w200QSent, _w200QIn, _w200LastOwnReplayMs;

    /// <summary>The quest-item classes handed to the game's script once per connection (both roles); chunks below the console ceiling.</summary>
    private async Task Wo200PushQuestClassesAsync()
    {
        if (_w200QPushed && Interlocked.Read(ref _w200QClassLife) == Interlocked.Read(ref _npcDeathLife)) return;
        await _w200QClassGate.WaitAsync();
        long life = Interlocked.Read(ref _npcDeathLife);
        bool Current() => _w200Connected && _where == GameWhere.World && !Wo136Holding && life == Interlocked.Read(ref _npcDeathLife) && (W137Host || W137Joiner);
        try
        {
            if ((_w200QPushed && Interlocked.Read(ref _w200QClassLife) == life) || !Current()) return;
            _w200QPushed = false;
            _w200QPushed = await QuestItemMirror.PublishClassesAsync(() =>
            {
                string? tables = TablesPakPath();
                if (tables is null) return Task.FromResult<IReadOnlyList<string>?>(null);
                if (_questClasses is null || _questClasses.Count == 0) _questClasses = WhsSave.QuestClasses(tables);
                return Task.FromResult<IReadOnlyList<string>?>(_questClasses.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList());
            }, lua => _transport.ExecuteNowAsync(lua), Current);
            if (_w200QPushed) Interlocked.Exchange(ref _w200QClassLife, life);
            if (_w200QPushed) Console.WriteLine($"WO200-QITEM {_questClasses?.Count ?? 0} quest-item classes given to the game's script");
            else Console.WriteLine("WO200-QITEM classes not ready -- initialization will retry");
        }
        catch (Exception ex) { Console.WriteLine($"WO200-QITEM class initialization not complete ({ex.GetType().Name}: {ex.Message}) -- will retry"); }
        finally { _w200QClassGate.Release(); }
    }

    /// <summary>Every 3 s while a session runs: the host's script reports its changes; the joiner's notes its own (the protection).</summary>
    private async Task Wo200QItemsTickAsync()
    {
        if (!W200("qitem_mirror")) return;
        bool host = W137Host && Wo134Peers().Any();
        if (!host && !W137Joiner) return;
        await Wo200PushQuestClassesAsync();
        if (!_w200QPushed) return;
        string peers = host ? string.Join(",", Wo134Peers().OrderBy(p => p)) : "";
        bool baseline = host && peers != _w200QPeerSet;
        await ExecLuaAsync($"if KCD2MP_W200QItemsTick then KCD2MP_W200QItemsTick({B(host)}, {B(baseline)}) end");
        _w200QPeerSet = peers;
    }

    /// <summary>The host's script: "class:count class:count ..." -- each to every joiner as an item count (QuestHost kind 8).</summary>
    private async Task Wo200OnHostQItemsAsync(string? arg)
    {
        if (!W137Host || !W200("qitem_mirror") || _where != GameWhere.World || Wo136Holding) return;
        var peers = Wo134Peers().ToList();
        if (peers.Count == 0) return;
        long life = Interlocked.Read(ref _npcDeathLife);
        int sent = await QuestItemMirror.ForwardAsync(arg, peers, async (peer, text) =>
        {
            if (!W137Host || life != Interlocked.Read(ref _npcDeathLife)) return false;
            try
            {
                await WriteJoinAsync(new LootMsg(Protocol.QuestHostItemCount, 0, text).BuildUp(Protocol.QuestHostUp, peer));
                Interlocked.Increment(ref _w137Out);
                return true;
            }
            catch (Exception ex) { Console.WriteLine($"WO200-QITEM count not sent to ghost {peer}: {ex.GetType().Name}: {ex.Message}"); return false; }
        });
        Interlocked.Add(ref _w200QSent, sent);
    }

    /// <summary>The joiner: the host's count of one quest-item class; his own replay sent in the last 5 minutes protects his own changes.</summary>
    private void Wo200OnHostItemCount(string text)
    {
        if (!W137Joiner || !W200("qitem_mirror")) return;
        if (!Wo200Rules.TryParseItemCountText(text, out int n, out Guid cls) || n > Wo200Rules.QCountMax) { Wo137Veto("item-count-malformed"); return; }
        Interlocked.Increment(ref _w200QIn);
        long last = Interlocked.Read(ref _w200LastOwnReplayMs);
        bool pending = last != 0 && W200Now() - last < Wo200Rules.SuppressTtl.TotalMilliseconds;
        _ = ExecLuaAsync(FormattableString.Invariant($"if KCD2MP_W200QItemHost then KCD2MP_W200QItemHost(\"{cls:D}\", {n}, {(pending ? 1 : 0)}) end"));
    }

    private string Wo200ItemsStats() => FormattableString.Invariant($"qitems_sent={_w200QSent} qitems_in={_w200QIn}");
}
