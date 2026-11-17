// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KcdMp.Client;

// WO-134: world items -- the pure half (docs/WO-134-findings.md). Everything a
// peer sends is checked here before it reaches Lua: item classes are GUIDs,
// body names are engine names, container names are the level's own
// ("stash[Chest/chest3_<guid>]"), numbers are invariant decimals in range.
public static class Wo134Rules
{
    public static readonly Regex BodyName = new("^[A-Za-z0-9_]{1,80}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    // Level names nest ("stash[profession/seller/shop_inside11:profession/seller/shop_base2[...]:Chest/chest3[...]]_<guid>", 190+ chars).
    public static readonly Regex ContainerName = new(@"^[A-Za-z0-9_\[\]/.:\-]{1,300}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public const int MaxBodyItems = 80;
    public const int BodyPartItems = 10;

    public static bool TryClass(string s, out Guid g) => Guid.TryParseExact(s, "D", out g);

    public static bool TryAmount(string s, out int n) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n >= 1 && n <= 100000;

    public static bool TryHealth(string s, out float h) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out h) && float.IsFinite(h) && h >= 0f && h <= 1.5f;

    public static bool TryCoord(string s, out float v) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && float.IsFinite(v) && Math.Abs(v) < 100000f;

    public static string F(float v) => v.ToString("0.####", CultureInfo.InvariantCulture);
    public static string F3(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    public readonly record struct Item(Guid Cls, int Amt, float Hp, bool Worn);

    /// <summary>"cls:amt:hp[:w],..." or "-" (empty). Null when any entry is malformed or there are too many.</summary>
    public static List<Item>? ParseItems(string s)
    {
        var o = new List<Item>();
        if (s == "-") return o;
        var parts = s.Split(',');
        if (parts.Length > MaxBodyItems) return null;
        foreach (var p in parts)
        {
            var f = p.Split(':');
            if (f.Length is < 3 or > 4 || !TryClass(f[0], out var g) || !TryAmount(f[1], out int n) || !TryHealth(f[2], out float h)) return null;
            if (f.Length == 4 && f[3] != "w" && f[3] != "1") return null;
            o.Add(new Item(g, n, h, f.Length == 4));
        }
        return o;
    }

    public static string FormatItems(IReadOnlyList<Item> items) =>
        items.Count == 0 ? "-" : string.Join(",", items.Select(i => $"{i.Cls:D}:{i.Amt}:{F(i.Hp)}{(i.Worn ? ":w" : "")}"));

    /// <summary>Marks the items the host body wears: one item per worn class.</summary>
    public static List<Item> MarkWorn(IReadOnlyList<Item> items, IReadOnlyCollection<Guid>? worn)
    {
        var left = worn is null ? new HashSet<Guid>() : new HashSet<Guid>(worn);
        var o = new List<Item>(items.Count);
        foreach (var i in items)
            o.Add(left.Remove(i.Cls) ? i with { Worn = true } : i with { Worn = false });
        return o;
    }

    /// <summary>A Lua table literal of items, for KCD2MP_W134BodyState: {{"cls",amt,hp,true},...}.</summary>
    public static string LuaItems(IReadOnlyList<Item> items)
    {
        var sb = new StringBuilder("{");
        foreach (var i in items)
            sb.Append(CultureInfo.InvariantCulture, $"{{\"{i.Cls:D}\",{i.Amt},{F(i.Hp)},{(i.Worn ? "true" : "false")}}},");
        return sb.Append('}').ToString();
    }

    // ------------------------------------------------------------------ chests

    public sealed class Entry
    {
        public string C { get; set; } = "";     // container (entity name)
        public string Cls { get; set; } = "";   // item class
        public int N { get; set; }              // > 0 taken out, < 0 put in
        public float Hp { get; set; } = 1f;
        public long T { get; set; }             // world time (s) when it happened
        public int R { get; set; }              // the container's restock period (days), 0 = never
    }

    // A patch is identified by its item class and centimetre position, never the
    // transient PickableArea entity name. Herb records remain local to a player;
    // the host's chest delta protocol intentionally does not transmit them.
    public sealed record HerbEntry(string Cls, float X, float Y, float Z, int N, long T)
    {
        public string Key => string.Create(CultureInfo.InvariantCulture, $"{Cls}/{X:F2}/{Y:F2}/{Z:F2}");
    }
    public static bool ValidHerb(HerbEntry e) => TryClass(e.Cls, out _) && float.IsFinite(e.X) && float.IsFinite(e.Y) && float.IsFinite(e.Z)
        && Math.Abs(e.X) < 100000 && Math.Abs(e.Y) < 100000 && Math.Abs(e.Z) < 100000 && e.N > 0 && e.N <= 100000 && e.T >= 0;
    public static HerbEntry? ParseHerbEvent(string text)
    {
        var f = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (f.Length != 6 || !TryCoord(f[1], out var x) || !TryCoord(f[2], out var y) || !TryCoord(f[3], out var z)
            || !TryAmount(f[4], out var n) || !long.TryParse(f[5], NumberStyles.None, CultureInfo.InvariantCulture, out var t)) return null;
        var e = new HerbEntry(f[0].ToLowerInvariant(), x, y, z, n, t);
        return ValidHerb(e) ? e : null;
    }
    public static List<string> HerbCalls(IReadOnlyList<HerbEntry> rows, long epoch)
    {
        var calls = new List<string> { $"if KCD2MP_W134HerbsReset then KCD2MP_W134HerbsReset({epoch}) end" };
        foreach (var batch in rows.Chunk(8))
        {
            var values = batch.Select(e => string.Create(CultureInfo.InvariantCulture,
                $"{{\"{e.Cls}\",{F(e.X)},{F(e.Y)},{F(e.Z)},{e.N},{e.T}}}"));
            calls.Add("if KCD2MP_W134HerbsRestore then KCD2MP_W134HerbsRestore({" + string.Join(',', values)
                + "}," + epoch.ToString(CultureInfo.InvariantCulture) + ") end");
        }
        calls.Add($"if KCD2MP_W134HerbsReady then KCD2MP_W134HerbsReady({epoch}) end");
        return calls;
    }

    /// <summary>A chest ledger: what one player took out of (or put into) which container, in one world.</summary>
    public sealed class Ledger
    {
        public const int MaxEntries = 4000;
        public List<Entry> Entries { get; set; } = [];
        public List<HerbEntry> Herbs { get; set; } = [];

        public void AddHerb(HerbEntry e)
        {
            if (!ValidHerb(e)) return;
            Herbs.RemoveAll(x => x.Key == e.Key);
            Herbs.Add(e);
            if (Herbs.Count > MaxEntries) Herbs.RemoveRange(0, Herbs.Count - MaxEntries);
        }

        public void Add(Entry e)
        {
            // Consecutive changes to one container/class/restock merge (a take and a
            // put back cancel out) within one game hour; the newest time is kept.
            var last = Entries.Count > 0 ? Entries[^1] : null;
            if (last is not null && last.C == e.C && last.Cls == e.Cls && last.R == e.R && Math.Abs(e.T - last.T) <= 3600)
            {
                last.N += e.N;
                last.T = Math.Max(last.T, e.T);
                if (last.N == 0) Entries.RemoveAt(Entries.Count - 1);
            }
            else if (e.N != 0) Entries.Add(e);
            if (Entries.Count > MaxEntries) Entries.RemoveRange(0, Entries.Count - MaxEntries);
        }

        /// <summary>Entries the engine would have restocked by <paramref name="worldT"/> are dropped (never those with restock 0).</summary>
        public int PruneExpired(long worldT)
        {
            int before = Entries.Count;
            Entries.RemoveAll(e => e.R > 0 && worldT - e.T > (long)e.R * 86400L);
            return before - Entries.Count;
        }

        public string ToJson() => JsonSerializer.Serialize(this);

        public static Ledger FromJson(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new Ledger();
            try
            {
                var l = JsonSerializer.Deserialize<Ledger>(json) ?? new Ledger();
                l.Entries ??= []; l.Herbs ??= [];
                l.Entries.RemoveAll(e => e is null || !Valid(e));
                l.Herbs.RemoveAll(e => e is null || !ValidHerb(e));
                return l;
            }
            catch (JsonException) { return new Ledger(); }
        }

        public Ledger Clone() => FromJson(ToJson());
    }

    public static bool Valid(Entry e) =>
        ContainerName.IsMatch(e.C) && TryClass(e.Cls, out _) && e.N != 0 && Math.Abs(e.N) <= 100000
        && float.IsFinite(e.Hp) && e.Hp >= 0 && e.Hp <= 1.5f && e.T >= 0 && e.R >= 0 && e.R <= 65535;

    /// <summary>"container cls n hp worldT restockDays" from the mod's w134_chest event.</summary>
    public static Entry? ParseChestEvent(string arg)
    {
        var f = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (f.Length != 6) return null;
        if (!int.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
            || !TryHealth(f[3], out float hp)
            || !long.TryParse(f[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out long t)
            || !int.TryParse(f[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out int r)) return null;
        var e = new Entry { C = f[0], Cls = f[1].ToLowerInvariant(), N = n, Hp = hp, T = t, R = r };
        return Valid(e) ? e : null;
    }

    /// <summary>
    /// After a join: the rows that make the joiner's containers what they would be
    /// if only HE had looted. The host's takes are put back (+n), its puts taken
    /// out (-n); the joiner's own takes are taken out (-n), its puts put back (+n).
    /// Each row keeps its own time and restock period: the mod leaves an expired
    /// one to the engine's restock.
    /// </summary>
    public static List<Entry> JoinRows(Ledger host, Ledger joiner)
    {
        var o = new List<Entry>();
        foreach (var e in host.Entries) o.Add(new Entry { C = e.C, Cls = e.Cls, N = e.N, Hp = e.Hp, T = e.T, R = e.R });
        foreach (var e in joiner.Entries) o.Add(new Entry { C = e.C, Cls = e.Cls, N = -e.N, Hp = e.Hp, T = e.T, R = e.R });
        return o;
    }

    public static string RowText(Entry e) =>
        $"{e.C}|{e.Cls}|{e.N.ToString(CultureInfo.InvariantCulture)}|{F(e.Hp)}|{e.T.ToString(CultureInfo.InvariantCulture)}|{e.R.ToString(CultureInfo.InvariantCulture)}";

    public static Entry? ParseRow(string s)
    {
        var f = s.Split('|');
        if (f.Length != 6) return null;
        return ParseChestEvent(string.Join(' ', f));
    }

    /// <summary>The ledger as LootHost Ledger texts ("part nparts rows"), each within the wire's text limit.</summary>
    public static List<string> LedgerParts(Ledger l, int max = KcdMp.Wire.Protocol.LootTextMax)
    {
        var chunks = new List<List<string>>();
        var cur = new List<string>();
        int len = 0;
        foreach (var e in l.Entries)
        {
            var r = RowText(e);
            if (cur.Count > 0 && len + r.Length + 1 > max - 16) { chunks.Add(cur); cur = []; len = 0; }
            cur.Add(r); len += r.Length + 1;
        }
        if (cur.Count > 0 || chunks.Count == 0) chunks.Add(cur);
        int n = chunks.Count;
        return chunks.Select((c, i) => $"{i + 1} {n} {(c.Count == 0 ? "-" : string.Join(';', c))}").ToList();
    }

    /// <summary>One ledger part's rows; null when malformed.</summary>
    public static (int Part, int N, List<Entry> Rows)? ParseLedgerPart(string text)
    {
        var f = text.Split(' ', 3);
        if (f.Length != 3 || !int.TryParse(f[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int p)
            || !int.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || p < 1 || n < 1 || p > n || n > 500) return null;
        var rows = new List<Entry>();
        if (f[2] != "-")
            foreach (var r in f[2].Split(';'))
            {
                var e = ParseRow(r);
                if (e is null) return null;
                rows.Add(e);
            }
        return (p, n, rows);
    }

    /// <summary>
    /// KCD2MP_W134ChestApply calls, each a Lua table literal of rows, packed so no
    /// call runs past the console's limit (WO-110: ExecuteString truncates past
    /// ~2,100 encoded characters) -- container names reach 190+ characters.
    /// </summary>
    public static List<string> ApplyCalls(IReadOnlyList<Entry> rows, int maxEncoded = 1800, long? epoch = null, long? serial = null)
    {
        const string pre = "if KCD2MP_W134ChestApply then KCD2MP_W134ChestApply({";
        string Post(int part) => epoch is long stamp ? "}," + stamp.ToString(CultureInfo.InvariantCulture)
            + (serial is long id ? "," + id.ToString(CultureInfo.InvariantCulture) + "," + part.ToString(CultureInfo.InvariantCulture) : "") + ") end" : "}) end";
        var o = new List<string>();
        var sb = new StringBuilder(pre);
        int n = 0;
        foreach (var e in rows)
        {
            string r = string.Create(CultureInfo.InvariantCulture, $"{{\"{e.C}\",\"{e.Cls}\",{e.N},{F(e.Hp)},{e.T},{e.R}}},");
            if (n > 0 && (n >= 8 || Uri.EscapeDataString("#" + sb + r + Post(o.Count + 1)).Length > maxEncoded))
            {
                o.Add(sb.Append(Post(o.Count + 1)).ToString());
                sb = new StringBuilder(pre); n = 0;
            }
            sb.Append(r); n++;
        }
        if (n > 0 || o.Count == 0) o.Add(sb.Append(Post(o.Count + 1)).ToString());
        return o;
    }
}

/// <summary>WO-134: the host's chest ledgers, one per world: the running one and a copy paired with every host save.</summary>
public sealed class ChestLedgerStore
{
    public const int KeepPairs = 100;
    private readonly string _root;
    private readonly object _gate = new();
    private static readonly Regex TagRx = new("^[0-9a-f]{6,16}$", RegexOptions.Compiled);
    private static readonly Regex Md5Rx = new("^[0-9a-f]{32}$", RegexOptions.Compiled);

    public ChestLedgerStore(string root) { _root = root; }

    public static string DefaultRoot() => Path.Combine(Path.GetDirectoryName(HenryStore.DefaultRoot())!, "chests");

    private string Dir(string tag)
    {
        if (!TagRx.IsMatch(tag)) throw new ArgumentException("not a world tag: " + tag);
        return Path.Combine(_root, tag);
    }

    public Wo134Rules.Ledger Current(string tag)
    {
        lock (_gate)
        {
            var p = Path.Combine(Dir(tag), "current.json");
            return Wo134Rules.Ledger.FromJson(File.Exists(p) ? File.ReadAllText(p) : null);
        }
    }

    public void SaveCurrent(string tag, Wo134Rules.Ledger l) => Write(Path.Combine(Dir(tag), "current.json"), l.ToJson());

    /// <summary>A host world save: the running ledger is what that save's world lacks.</summary>
    public void Pair(string tag, string md5, Wo134Rules.Ledger l)
    {
        md5 = md5.ToLowerInvariant();
        if (!Md5Rx.IsMatch(md5)) throw new ArgumentException("md5");
        Write(Path.Combine(Dir(tag), $"pair-{md5}.json"), l.ToJson());
        lock (_gate)
        {
            var old = Directory.EnumerateFiles(Dir(tag), "pair-*.json").Select(f => new FileInfo(f))
                               .OrderByDescending(f => f.LastWriteTimeUtc).Skip(KeepPairs).ToList();
            foreach (var f in old) try { f.Delete(); } catch (IOException) { }
        }
    }

    /// <summary>The ledger paired with a host save, or null.</summary>
    public Wo134Rules.Ledger? Paired(string tag, string md5)
    {
        lock (_gate)
        {
            var p = Path.Combine(Dir(tag), $"pair-{md5.ToLowerInvariant()}.json");
            return File.Exists(p) ? Wo134Rules.Ledger.FromJson(File.ReadAllText(p)) : null;
        }
    }

    private void Write(string path, string json)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path + ".part", json);
            File.Move(path + ".part", path, overwrite: true);
        }
    }
}
