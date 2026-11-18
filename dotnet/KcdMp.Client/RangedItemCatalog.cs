// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.IO.Compression;
using System.Xml.Linq;
using KcdMp.Wire;

namespace KcdMp.Client;

/// <summary>Actual item Class=N, including bounded ItemAlias chains. Model names and equipped sets are not hand evidence.</summary>
public sealed class RangedItemCatalog
{
    private readonly Dictionary<Guid, (bool Missile, int Class)> _items;
    private RangedItemCatalog(Dictionary<Guid, (bool, int)> items) => _items = items;
    public static RangedItemCatalog Read(string pak)
    {
        var items = new Dictionary<Guid, (bool, int)>(); var aliases = new Dictionary<Guid, Guid>(); var ambiguous = new HashSet<Guid>();
        using var zip = ZipFile.OpenRead(pak);
        foreach (var entry in zip.Entries)
        {
            string name = entry.FullName.ToLowerInvariant();
            if (!name.StartsWith("libs/tables/item/item", StringComparison.Ordinal) || !name.EndsWith(".xml", StringComparison.Ordinal)) continue;
            using var stream = entry.Open(); var doc = XDocument.Load(stream);
            foreach (var el in doc.Descendants())
            {
                if (!Guid.TryParse(el.Attribute("Id")?.Value, out Guid id)) continue;
                if (el.Name.LocalName == "ItemAlias")
                {
                    if (Guid.TryParse(el.Attribute("SourceItemId")?.Value, out Guid source))
                    { if (aliases.TryGetValue(id, out var prior) && prior != source) ambiguous.Add(id); aliases[id] = source; }
                    else ambiguous.Add(id);
                    continue;
                }
                bool missile = el.Name.LocalName == "MissileWeapon";
                int cls = int.TryParse(el.Attribute("Class")?.Value, out int c) ? c : -1;
                var value = (missile, cls);
                if (items.TryGetValue(id, out var previous) && previous != value) ambiguous.Add(id);
                else items[id] = value;
            }
        }
        foreach (Guid id in aliases.Keys) if (items.ContainsKey(id)) ambiguous.Add(id);
        foreach (var (alias, source) in aliases)
        {
            var seen = new HashSet<Guid> { alias }; Guid current = source;
            for (int hop = 0; hop < 8 && seen.Add(current); hop++)
            {
                if (ambiguous.Contains(current)) { ambiguous.Add(alias); break; }
                if (items.TryGetValue(current, out var value)) { if (!items.ContainsKey(alias)) items[alias] = value; else ambiguous.Add(alias); break; }
                if (!aliases.TryGetValue(current, out current)) break;
            }
        }
        foreach (Guid id in ambiguous) items[id] = (true, -1);
        return new(items);
    }
    public RangedWeaponKind Kind(Guid id) => _items.TryGetValue(id, out var item) && item.Missile ? item.Class switch
    { 9 => RangedWeaponKind.Bow, 10 or 14 or 15 => RangedWeaponKind.Crossbow, _ => RangedWeaponKind.Unknown } : RangedWeaponKind.Unknown;
    public (RangedWeaponKind Kind, Guid ItemClass) Classify(RangedCapture capture)
    {
        if (capture.Source != 1 || capture.KnownMask != 3) return default;
        Guid chosen = Guid.Empty;
        foreach (Guid id in new[] { capture.Left, capture.Right })
        {
            if (id == Guid.Empty) continue;
            if (!_items.TryGetValue(id, out var item)) return default;
            if (!item.Missile) continue;
            if (chosen != Guid.Empty || Kind(id) == RangedWeaponKind.Unknown) return default;
            chosen = id;
        }
        return (Kind(chosen), chosen);
    }
}
