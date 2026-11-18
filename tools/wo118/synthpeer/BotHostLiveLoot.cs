// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using KcdMp.Client;
using KcdMp.Wire;

// Test inventory only. The live Joiner's normal inventory verifies and applies the grant.
sealed class BotHostLiveLoot(IReadOnlyDictionary<string, List<Wo134Rules.Item>> fixtures)
{
    private sealed class Body(IEnumerable<Wo134Rules.Item> items)
    {
        internal readonly Dictionary<string, Wo134Rules.Item> Items = items.Select((item, index) => (Id: $"t4_{index + 1}", item))
            .ToDictionary(pair => pair.Id, pair => pair.item, StringComparer.Ordinal);
        internal int Revision = 1;
    }
    private sealed record Reservation(string Body, string Item, Wo134Rules.Item Value);

    private readonly Dictionary<string, Body> _bodies = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, Reservation> _pending = new();

    private Body? Find(string name)
    {
        if (_bodies.TryGetValue(name, out var found)) return found;
        if (!fixtures.TryGetValue(name, out var items)) return null;
        var body = new Body(items);
        _bodies.Add(name, body);
        return body;
    }

    internal IEnumerable<LootMsg> List(string name, uint token)
    {
        var body = Find(name);
        var parts = body?.Items.Where(pair => pair.Value.Amt > 0).Select(pair =>
        {
            var item = pair.Value;
            return FormattableString.Invariant($"{pair.Key}:{item.Cls:D}:{item.Amt}:{item.Hp:R}:{(item.Worn ? 1 : 0)}:0:-");
        }).Chunk(8).ToArray() ?? [];
        for (int i = 0; i < Math.Max(1, parts.Length); i++)
        {
            string items = parts.Length == 0 ? "-" : string.Join(',', parts[i]);
            yield return new(Protocol.LootHostBodyList, token,
                FormattableString.Invariant($"{name} {body?.Revision ?? 1} {i + 1} {Math.Max(1, parts.Length)} {items}"));
        }
    }

    internal LootMsg Reserve(uint token, string name, string id, Guid itemClass, int amount)
    {
        var body = Find(name);
        if (token == 0 || _pending.Count >= 128 || _pending.ContainsKey(token) ||
            body is null || !body.Items.TryGetValue(id, out var item) || item.Cls != itemClass || amount <= 0 || amount > item.Amt)
            return new(Protocol.LootHostTakeVerdict, token, $"refuse {name} {id} gone 0");
        _pending.Add(token, new(name, id, item with { Amt = amount }));
        body.Items[id] = item with { Amt = item.Amt - amount };
        ++body.Revision;
        return new(Protocol.LootHostTakeVerdict, token, $"grant {name} {id} - 0");
    }

    internal bool Settle(uint token, string name, bool applied)
    {
        if (!_pending.TryGetValue(token, out var keep) || keep.Body != name) return false;
        _pending.Remove(token);
        if (!applied) Restore(keep);
        return true;
    }

    internal void CancelPending()
    {
        foreach (var keep in _pending.Values) Restore(keep);
        _pending.Clear();
    }

    private void Restore(Reservation keep)
    {
        var body = _bodies[keep.Body];
        body.Items[keep.Item] = keep.Value with { Amt = body.Items[keep.Item].Amt + keep.Value.Amt };
        ++body.Revision;
    }
}
