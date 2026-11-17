// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace KcdMp.Setup;

/// <summary>
/// A local, prelaunch delta from this installation's own ADB and a subsequent game's validator
/// evidence. Never writes a retail archive or invents a replacement animation. Not a runtime log filter.
/// </summary>
public static class FacialFallback
{
    public const string PakName = "kdcmp_facial_fallback.pak";
    public const string StateName = "kdcmp_facial_fallback.state.xml";
    private const string Marker = "_kcdmp/facial-fallback.xml";
    private const string Owner = "KingdomComeTogether.FacialFallback";
    private const int MaxXml = 32 * 1024 * 1024;
    private const int MaxLog = 64 * 1024 * 1024;
    private static readonly Dictionary<string, string> Databases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Animations/Mannequin/ADB/kcd_male_database.adb"] = "_objects/characters/humans/male/skeleton/male.chr",
        ["Animations/Mannequin/ADB/wh_female_database.adb"] = "_objects/characters/humans/female/skeleton/female.chr"
    };
    private static readonly Regex Warning = new(
        @"Validator: Invalid anim ref '(?<alias>fa_[A-Za-z0-9_]+)' on scope 'FacialExpression' in database '(?<db>[^']+)'\. Skeleton '(?<skeleton>[^']+)'",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex RunStart = new(@"^Log Started at (?<time>\d{4}-\d{2}-\d{2}  \d{2}:\d{2}:\d{2})\s*$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public sealed record Outcome(bool Ok, string Action, int Databases, int References, string Detail, bool BlocksLaunch = false);
    private sealed record State(string Installation, string Fingerprint, DateTime PreparedUtc, string LogHash,
        Dictionary<string, HashSet<string>> Refs);

    public static Outcome Prepare(string gameRoot, string modDir, bool gameRunning) =>
        Prepare(gameRoot, modDir, gameRunning, DateTime.UtcNow);

    internal static Outcome Prepare(string gameRoot, string modDir, bool gameRunning, DateTime utcNow)
    {
        if (gameRunning) return new(true, "skipped", 0, 0, "game is running; assets left untouched");
        string pak = Path.Combine(modDir, "Data", PakName), statePath = Path.Combine(modDir, StateName);
        try
        {
            if (!Directory.Exists(modDir)) return new(false, "skipped", 0, 0, "mod folder is absent");
            // A name alone does not authorize overwriting another mod's file.
            bool foreignPak = File.Exists(pak) && !OwnedPak(pak);
            if (foreignPak || (File.Exists(statePath) && !OwnedXml(statePath)))
            {
                if (!foreignPak) DeleteOwnedPak(pak); // a foreign state cannot certify our old override
                return new(false, "skipped", 0, 0, "output name is occupied by an unrecognized file");
            }

            string installation = Hash(Encoding.UTF8.GetBytes(Path.GetFullPath(gameRoot).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant()));
            var source = ReadDatabases(gameRoot);
            string fingerprint = Fingerprint(gameRoot, modDir, source);
            State? old = File.Exists(statePath) ? ReadState(statePath) : null;
            var log = ReadLog(gameRoot);
            bool sameSource = old is not null && old.Installation == installation && old.Fingerprint == fingerprint;
            var refs = sameSource ? old!.Refs : new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            // A preexisting log cannot prove which asset version produced a warning. The first launch
            // establishes provenance; only a new run after that launch may contribute new references.
            bool freshRun = sameSource && log.Text is not null && log.Hash != old!.LogHash &&
                log.WrittenUtc >= old.PreparedUtc && LogStartedUtc(log.Text) is { } started &&
                started >= old.PreparedUtc.AddSeconds(-2) && started <= utcNow.AddSeconds(2);
            if (freshRun)
                foreach (var (db, alias) in Observed(log.Text!))
                {
                    if (!refs.TryGetValue(db, out var names)) refs[db] = names = new(StringComparer.Ordinal);
                    if (names.Count < 256) names.Add(alias);
                }

            var patched = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
            int removed = 0;
            foreach (var (db, names) in refs)
            {
                if (!source.TryGetValue(db, out var bytes)) throw new InvalidDataException("observed database is absent");
                var (xml, count) = RemoveInvalidClips(bytes, names);
                if (count == 0) continue;
                if (OtherModOverrides(gameRoot, pak, db)) throw new InvalidDataException("another mod overrides the observed database");
                patched[db] = xml;
                removed += count;
            }
            // Check archive/Facials metadata again after parsing, before publishing the override.
            if (Fingerprint(gameRoot, modDir, ReadDatabases(gameRoot)) != fingerprint)
                throw new IOException("assets changed during preparation");
            bool changed = false;
            if (patched.Count == 0) changed = DeleteOwnedPak(pak);
            else
            {
                patched[Marker] = XmlBytes(new XDocument(new XElement("FacialFallback", new XAttribute("Owner", Owner),
                    new XAttribute("Version", 1), new XAttribute("Fingerprint", fingerprint))));
                changed = WritePak(pak, patched);
            }
            WriteState(statePath, new(installation, fingerprint, utcNow, log.Hash, refs));
            string action = patched.Count == 0 ? (changed ? "removed" : "baseline") : changed ? "generated" : "unchanged";
            return new(true, action, patched.Count == 0 ? 0 : patched.Count - 1, removed,
                !sameSource ? "new asset version; awaiting a subsequent game's validator evidence" :
                freshRun ? "current run evidence evaluated; unrelated clips retained" : "retained only evidence proven for this asset version");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or XmlException or ArgumentException or FormatException or RegexMatchTimeoutException)
        {
            // A stale delta must not outlive the source/schema proof. Only our marked files may be removed.
            try { DeleteOwnedPak(pak); if (OwnedXml(statePath)) File.Delete(statePath); } catch { }
            if (File.Exists(pak) && OwnedPak(pak))
                return new(false, "blocked", 0, 0, "unverified owned override could not be removed; close the game and check file access", true);
            return new(false, "disabled", 0, 0, $"validation failed ({ex.GetType().Name}); no facial fallback certified");
        }
    }

    internal static IEnumerable<(string Database, string Alias)> Observed(string text)
    {
        var seen = new HashSet<(string, string)>();
        foreach (Match m in Warning.Matches(text))
        {
            string db = m.Groups["db"].Value.Replace('\\', '/');
            if (!Databases.TryGetValue(db, out var skeleton) ||
                !string.Equals(skeleton, m.Groups["skeleton"].Value.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)) continue;
            string canonical = Databases.Keys.Single(k => string.Equals(k, db, StringComparison.OrdinalIgnoreCase));
            if (seen.Add((canonical, m.Groups["alias"].Value))) yield return (canonical, m.Groups["alias"].Value);
        }
    }

    /// <summary>Official ADB structure: keep Fragment, tags, Blend/timing, ProcLayer and every other clip.
    /// Removing a proven invalid Animation leaves the existing rest layer; it does not guess a neutral alias.</summary>
    internal static (byte[] Xml, int Removed) RemoveInvalidClips(byte[] bytes, IReadOnlySet<string> invalid)
    {
        var doc = ReadXml(bytes);
        var root = doc.Root;
        if (root?.Name != "AnimDB" || string.IsNullOrWhiteSpace((string?)root.Attribute("FragDef")) ||
            string.IsNullOrWhiteSpace((string?)root.Attribute("TagDef")) || root.Elements("FragmentList").Count() != 1 ||
            root.Elements().Any(e => e.Name != "SubADBs" && e.Name != "FragmentList" && e.Name != "FragmentBlendList") ||
            root.Descendants().Any(e => e.Name.Namespace != XNamespace.None)) throw new InvalidDataException("unknown ADB schema");
        var fragments = root.Element("FragmentList")!.Elements().SelectMany(id =>
        {
            if (id.Elements().Any(e => e.Name != "Fragment")) throw new InvalidDataException("unknown fragment schema");
            return id.Elements();
        }).ToList();
        foreach (var blends in root.Elements("FragmentBlendList"))
            foreach (var blend in blends.Elements())
            {
                if (blend.Name != "Blend" || blend.Elements().Any(e => e.Name != "Variant")) throw new InvalidDataException("unknown blend schema");
                foreach (var variant in blend.Elements())
                {
                    if (variant.Elements().Any(e => e.Name != "Fragment")) throw new InvalidDataException("unknown variant schema");
                    fragments.AddRange(variant.Elements());
                }
            }
        foreach (var f in fragments)
        {
            if (f.Elements().Any(e => e.Name != "AnimLayer" && e.Name != "ProcLayer")) throw new InvalidDataException("unknown layer schema");
            foreach (var layer in f.Elements("AnimLayer"))
                if (layer.Elements().Any(e => e.Name != "Blend" && e.Name != "Animation") ||
                    layer.Elements("Animation").Any(e => e.HasElements || string.IsNullOrWhiteSpace((string?)e.Attribute("name"))))
                    throw new InvalidDataException("unknown animation schema");
        }
        var animations = fragments.SelectMany(f => f.Elements("AnimLayer")).SelectMany(l => l.Elements("Animation")).ToList();
        // A matching alias anywhere outside the checked animation structure cannot safely be rewritten.
        if (root.Descendants("Animation").Except(animations).Any(e => invalid.Contains((string?)e.Attribute("name") ?? "")))
            throw new InvalidDataException("animation outside checked layers");
        var remove = animations.Where(e => invalid.Contains((string)e.Attribute("name")!)).ToList();
        foreach (var e in remove) e.Remove();
        return (XmlBytes(doc), remove.Count);
    }

    private static Dictionary<string, byte[]> ReadDatabases(string root)
    {
        using var zip = ZipFile.OpenRead(Path.Combine(root, "Data", "Animations.pak"));
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (string db in Databases.Keys)
        {
            var entries = zip.Entries.Where(e => string.Equals(e.FullName.Replace('\\', '/'), db, StringComparison.OrdinalIgnoreCase)).ToList();
            if (entries.Count != 1 || entries[0].Length > MaxXml) throw new InvalidDataException("missing or ambiguous database");
            using var stream = entries[0].Open(); using var memory = new MemoryStream(); stream.CopyTo(memory);
            result[db] = memory.ToArray();
        }
        return result;
    }

    private static string Fingerprint(string root, string modDir, Dictionary<string, byte[]> source)
    {
        var b = new StringBuilder();
        foreach (var (path, bytes) in source.OrderBy(x => x.Key, StringComparer.Ordinal)) b.Append(path).Append('|').Append(Hash(bytes)).Append('\n');
        // The alias/skeleton assets can change even if the database text did not.
        foreach (string path in Directory.EnumerateFiles(Path.Combine(root, "Data"), "*", SearchOption.TopDirectoryOnly)
                     .Where(p => Path.GetExtension(p).Equals(".pak", StringComparison.OrdinalIgnoreCase)).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            Stamp(path);
        string facials = Path.Combine(root, "Data", "Facials");
        if (Directory.Exists(facials))
            foreach (string path in Directory.EnumerateFiles(facials, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
                         .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)) Stamp(path);
        // Another mod may repair an animation alias without changing a base archive. Discard the old
        // evidence then too. Exclude only our output/provenance and the proven config-only keys pak.
        string mods = Path.Combine(root, "Mods");
        var generated = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFullPath(Path.Combine(modDir, "Data", PakName)), Path.GetFullPath(Path.Combine(modDir, StateName)),
            Path.GetFullPath(Path.Combine(modDir, "Data", "kdcmp_keys.pak"))
        };
        if (Directory.Exists(mods))
            foreach (string path in Directory.EnumerateFiles(mods, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
                         .Where(p => !generated.Contains(Path.GetFullPath(p))).OrderBy(p => p, StringComparer.OrdinalIgnoreCase)) Stamp(path);
        return Hash(Encoding.UTF8.GetBytes(b.ToString()));
        void Stamp(string path) { var f = new FileInfo(path); b.Append(Path.GetRelativePath(root, path)).Append('|').Append(f.Length).Append('|').Append(f.LastWriteTimeUtc.Ticks).Append('\n'); }
    }

    private static (string? Text, string Hash, DateTime WrittenUtc) ReadLog(string root)
    {
        string path = Path.Combine(root, "kcd.log"); var f = new FileInfo(path);
        if (!f.Exists || f.Length > MaxLog) return (null, "", DateTime.MinValue);
        byte[] bytes = File.ReadAllBytes(path);
        return (Encoding.UTF8.GetString(bytes), Hash(bytes), f.LastWriteTimeUtc);
    }

    private static DateTime? LogStartedUtc(string text)
    {
        var m = RunStart.Match(text);
        return m.Success && DateTime.TryParseExact(m.Groups["time"].Value, "yyyy-MM-dd  HH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal, out var time) ? time.ToUniversalTime() : null;
    }

    private static bool OtherModOverrides(string root, string ownPak, string database)
    {
        string mods = Path.Combine(root, "Mods"); if (!Directory.Exists(mods)) return false;
        foreach (string file in Directory.EnumerateFiles(mods, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            if (Path.GetFullPath(file).Equals(Path.GetFullPath(ownPak), StringComparison.OrdinalIgnoreCase)) continue;
            if (file.Replace('\\', '/').EndsWith("/" + database, StringComparison.OrdinalIgnoreCase)) return true;
            if (!Path.GetExtension(file).Equals(".pak", StringComparison.OrdinalIgnoreCase)) continue;
            using var zip = ZipFile.OpenRead(file);
            if (zip.Entries.Any(e => e.FullName.Replace('\\', '/').Equals(database, StringComparison.OrdinalIgnoreCase))) return true;
        }
        return false;
    }

    private static XDocument ReadXml(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxXml });
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }
    private static byte[] XmlBytes(XDocument doc)
    {
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false })) doc.Save(writer);
        return stream.ToArray();
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static bool IsOwned(XDocument doc) => doc.Root?.Name == "FacialFallback" && (string?)doc.Root.Attribute("Owner") == Owner && (string?)doc.Root.Attribute("Version") == "1";
    private static bool OwnedXml(string path) { try { return new FileInfo(path).Length < MaxXml && IsOwned(ReadXml(File.ReadAllBytes(path))); } catch { return false; } }
    private static bool OwnedPak(string path)
    {
        try { using var zip = ZipFile.OpenRead(path); var e = zip.GetEntry(Marker); if (e is null || e.Length > MaxXml) return false;
            using var s = e.Open(); using var m = new MemoryStream(); s.CopyTo(m); return IsOwned(ReadXml(m.ToArray())); } catch { return false; }
    }
    private static bool DeleteOwnedPak(string path) { if (!File.Exists(path) || !OwnedPak(path)) return false; File.Delete(path); return true; }

    private static State ReadState(string path)
    {
        var root = ReadXml(File.ReadAllBytes(path)).Root!;
        var refs = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in root.Elements("Ref"))
        {
            string db = (string?)e.Attribute("Database") ?? "", alias = (string?)e.Attribute("Alias") ?? "";
            if (!Databases.ContainsKey(db) || !Regex.IsMatch(alias, "^fa_[A-Za-z0-9_]+$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))) throw new InvalidDataException("unknown persisted reference");
            if (!refs.TryGetValue(db, out var set)) refs[db] = set = new(StringComparer.Ordinal);
            if (set.Count >= 256) throw new InvalidDataException("too many persisted references"); set.Add(alias);
        }
        return new((string?)root.Attribute("Installation") ?? "", (string?)root.Attribute("Fingerprint") ?? "",
            DateTime.Parse((string?)root.Attribute("PreparedUtc") ?? "", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            (string?)root.Attribute("LogHash") ?? "", refs);
    }
    private static void WriteState(string path, State s)
    {
        var root = new XElement("FacialFallback", new XAttribute("Owner", Owner), new XAttribute("Version", 1),
            new XAttribute("Installation", s.Installation), new XAttribute("Fingerprint", s.Fingerprint),
            new XAttribute("PreparedUtc", s.PreparedUtc.ToString("O", CultureInfo.InvariantCulture)), new XAttribute("LogHash", s.LogHash));
        foreach (var (db, aliases) in s.Refs.OrderBy(e => e.Key, StringComparer.Ordinal))
            foreach (var alias in aliases.OrderBy(a => a, StringComparer.Ordinal)) root.Add(new XElement("Ref", new XAttribute("Database", db), new XAttribute("Alias", alias)));
        AtomicWrite(path, p => File.WriteAllBytes(p, XmlBytes(new XDocument(root))));
    }
    private static bool WritePak(string path, SortedDictionary<string, byte[]> files)
    {
        if (File.Exists(path))
        {
            using var existing = ZipFile.OpenRead(path);
            if (existing.Entries.Count == files.Count && files.All(f => { var e = existing.GetEntry(f.Key); if (e is null || e.Length != f.Value.Length) return false;
                    using var s = e.Open(); using var m = new MemoryStream(); s.CopyTo(m); return m.ToArray().AsSpan().SequenceEqual(f.Value); })) return false;
        }
        AtomicWrite(path, temp => { using var zip = ZipFile.Open(temp, ZipArchiveMode.Create);
            foreach (var (name, bytes) in files) { var e = zip.CreateEntry(name, CompressionLevel.Optimal); e.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero); using var s = e.Open(); s.Write(bytes); } });
        return true;
    }
    private static void AtomicWrite(string path, Action<string> write)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { write(temp); File.Move(temp, path, true); } finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
