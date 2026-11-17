// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace KcdMp.Setup.Tests;

public class FacialFallbackTests
{
    private const string Male = "Animations/Mannequin/ADB/kcd_male_database.adb";
    private const string Female = "Animations/Mannequin/ADB/wh_female_database.adb";
    // Synthetic schema fixture, never a distributed game asset. Includes a transition rest layer.
    private const string Adb = """
        <AnimDB FragDef="actions.xml" TagDef="tags.xml">
          <SubADBs><SubADB File="unrelated.adb" Tags="other"/></SubADBs>
          <FragmentList><idle><Fragment Tags="sleep+male" BlendOutDuration="0.3">
            <AnimLayer><Blend ExitTime="2.5" StartTime="0.1" Duration="0.2"/><Animation name="fa_idle_neutral_05" flags="Loop"/></AnimLayer>
            <AnimLayer><Blend ExitTime="0" StartTime="0" Duration="0"/><Animation name="walk_valid"/></AnimLayer>
            <AnimLayer><Blend ExitTime="0" StartTime="0" Duration="0"/><Animation name="fa_idle_sleep_valid"/></AnimLayer>
            <ProcLayer><Blend Duration="0.4"/><Procedural type="KeepMe"><ProceduralParams amount="7"/></Procedural></ProcLayer>
          </Fragment></idle></FragmentList>
          <FragmentBlendList><Blend from="idle" to="talk"><Variant from="" to=""><Fragment selectTime="1" enterTime="2">
            <AnimLayer><Blend ExitTime="3" StartTime="4" Duration="5"/><Animation name="fa_idle_neutral_05"/></AnimLayer>
          </Fragment></Variant></Blend></FragmentBlendList>
        </AnimDB>
        """;
    private static HashSet<string> Invalid => new(StringComparer.Ordinal) { "fa_idle_neutral_05" };
    private static string Warning(string alias = "fa_idle_neutral_05", string db = Male, string scope = "FacialExpression", string sex = "male") =>
        $"[Warning] Validator: Invalid anim ref '{alias}' on scope '{scope}' in database '{db}'. Skeleton '_objects/characters/humans/{sex}/skeleton/{sex}.chr'";

    [Fact]
    public void Removes_only_proven_clip_nodes_preserving_tags_timing_procedurals_and_other_clips()
    {
        var (bytes, count) = FacialFallback.RemoveInvalidClips(Encoding.UTF8.GetBytes(Adb), Invalid);
        Assert.Equal(2, count);
        var expected = XDocument.Parse(Adb, LoadOptions.PreserveWhitespace);
        expected.Descendants("Animation").Where(e => (string?)e.Attribute("name") == "fa_idle_neutral_05").Remove();
        var actual = XDocument.Parse(Encoding.UTF8.GetString(bytes), LoadOptions.PreserveWhitespace);
        Assert.True(XNode.DeepEquals(expected, actual));
        Assert.Equal(2, actual.Descendants("Animation").Count());
        Assert.Equal(2, actual.Descendants("AnimLayer").Count(e => !e.Elements("Animation").Any()));
        var again = FacialFallback.RemoveInvalidClips(bytes, Invalid);
        Assert.Equal(0, again.Removed);
        Assert.Equal(bytes, again.Xml);
    }

    [Theory]
    [InlineData("<Unknown/>")]
    [InlineData("<AnimDB FragDef='a' TagDef='t'><FragmentList/><Unexpected/></AnimDB>")]
    [InlineData("<AnimDB FragDef='a' TagDef='t'><FragmentList><id><Unknown/></id></FragmentList></AnimDB>")]
    [InlineData("<AnimDB FragDef='a' TagDef='t'><FragmentList><id><Fragment><AnimLayer><Clip name='fa_idle_neutral_05'/></AnimLayer></Fragment></id></FragmentList></AnimDB>")]
    [InlineData("<AnimDB xmlns='unknown' FragDef='a' TagDef='t'><FragmentList/></AnimDB>")]
    [InlineData("<!DOCTYPE AnimDB [<!ENTITY x 'external'>]><AnimDB FragDef='a' TagDef='t'><FragmentList/></AnimDB>")]
    public void Unknown_schema_is_rejected_without_mutating_input(string xml)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(xml), before = bytes.ToArray();
        Assert.ThrowsAny<Exception>(() => FacialFallback.RemoveInvalidClips(bytes, Invalid));
        Assert.Equal(before, bytes);
    }

    [Fact]
    public void Validator_requires_exact_scope_database_skeleton_and_facial_alias_and_deduplicates()
    {
        string text = string.Join('\n', Warning(), Warning(), Warning(scope: "Body"), Warning(db: "other.adb"),
            Warning(sex: "female"), Warning(alias: "walk_valid"), Warning("fa_sleep", Female, sex: "female"));
        Assert.Equal(new[] { (Male, "fa_idle_neutral_05"), (Female, "fa_sleep") }, FacialFallback.Observed(text).ToArray());
    }

    [Fact]
    public void Existing_log_is_only_baseline_then_new_same_source_run_builds_local_override_and_keeps_source_bytes()
    {
        using var f = new Installation();
        byte[] original = File.ReadAllBytes(f.Source);
        f.Log(f.Now.AddDays(-1), Warning());
        var first = f.Prepare();
        Assert.True(first.Ok); Assert.Equal("baseline", first.Action); Assert.False(File.Exists(f.Pak));
        f.Log(f.Now.AddSeconds(10), string.Join('\n', Enumerable.Repeat(Warning(), 1000)));
        var generated = f.Prepare(20);
        Assert.True(generated.Ok); Assert.Equal("generated", generated.Action);
        Assert.Equal(1, generated.Databases); Assert.Equal(2, generated.References);
        using (var zip = ZipFile.OpenRead(f.Pak))
        {
            Assert.Equal(2, zip.Entries.Count);
            Assert.NotNull(zip.GetEntry(Male)); Assert.Null(zip.GetEntry(Female));
            using var r = new StreamReader(zip.GetEntry(Male)!.Open());
            var xml = XDocument.Parse(r.ReadToEnd());
            Assert.DoesNotContain(xml.Descendants("Animation"), e => (string?)e.Attribute("name") == "fa_idle_neutral_05");
            Assert.Contains(xml.Descendants("Animation"), e => (string?)e.Attribute("name") == "fa_idle_sleep_valid");
        }
        Assert.Equal(original, File.ReadAllBytes(f.Source));
        byte[] pak = File.ReadAllBytes(f.Pak); var stamp = File.GetLastWriteTimeUtc(f.Pak);
        f.Log(f.Now.AddSeconds(30), "no facial warnings in the patched run");
        var retained = f.Prepare(40);
        Assert.Equal("unchanged", retained.Action); Assert.Equal(pak, File.ReadAllBytes(f.Pak));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(f.Pak)); // no churn once the warning is fixed
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Asset_update_removes_stale_override_and_requires_new_run_evidence(bool facialOnly)
    {
        using var f = new Installation(); f.Generate();
        if (facialOnly)
        {
            string path = Path.Combine(f.Root, "Data", "Facials", "new.fsq");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "new facial asset");
        }
        else f.SourceAdb(Adb.Replace("sleep+male", "updated-tag"));
        var r = f.Prepare(30);
        Assert.True(r.Ok); Assert.Equal("removed", r.Action); Assert.False(File.Exists(f.Pak));
        Assert.Equal("baseline", f.Prepare(40).Action); // old warning never reactivates the delta
        f.Log(f.Now.AddSeconds(50), Warning()); Assert.Equal("generated", f.Prepare(60).Action);
    }

    [Fact]
    public void Unknown_current_schema_disables_override_and_preserves_original_archive()
    {
        using var f = new Installation(); f.SourceAdb(Adb.Replace("<FragmentList>", "<Unexpected/><FragmentList>"));
        var before = File.ReadAllBytes(f.Source); f.Prepare(); f.Log(f.Now.AddSeconds(10), Warning());
        var r = f.Prepare(20);
        Assert.False(r.Ok); Assert.Equal("disabled", r.Action); Assert.False(File.Exists(f.Pak));
        Assert.Equal(before, File.ReadAllBytes(f.Source));
    }

    [Fact]
    public void Game_running_never_changes_state_or_assets()
    {
        using var f = new Installation(); f.Generate();
        var pak = File.ReadAllBytes(f.Pak); var state = File.ReadAllBytes(f.State);
        f.SourceAdb(Adb.Replace("sleep+male", "updated-tag"));
        var r = FacialFallback.Prepare(f.Root, f.Mod, true, f.Now.AddSeconds(30));
        Assert.Equal("skipped", r.Action); Assert.Equal(pak, File.ReadAllBytes(f.Pak)); Assert.Equal(state, File.ReadAllBytes(f.State));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Unrecognized_output_or_state_is_never_overwritten_or_removed(bool pak)
    {
        using var f = new Installation(); string path = pak ? f.Pak : f.State;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "another owner's file");
        var r = f.Prepare(); Assert.False(r.Ok); Assert.Equal("skipped", r.Action); Assert.Equal("another owner's file", File.ReadAllText(path));
    }

    [Fact]
    public void Unrecognized_provenance_disables_only_the_marked_owned_patch()
    {
        using var f = new Installation(); f.Generate(); File.WriteAllText(f.State, "another owner's file");
        var r = f.Prepare(30); Assert.False(r.Ok); Assert.False(File.Exists(f.Pak));
        Assert.Equal("another owner's file", File.ReadAllText(f.State));
    }

    [Fact]
    public void Windows_locked_stale_override_blocks_start_if_it_cannot_be_removed()
    {
        if (!OperatingSystem.IsWindows()) return; // Windows deletion/share semantics are the product contract.
        using var f = new Installation(); f.Generate(); f.SourceAdb(Adb.Replace("sleep+male", "updated-tag"));
        using var locked = new FileStream(f.Pak, FileMode.Open, FileAccess.Read, FileShare.Read);
        var r = f.Prepare(30); Assert.False(r.Ok); Assert.True(r.BlocksLaunch); Assert.Equal("blocked", r.Action);
    }

    [Fact]
    public void Another_mod_database_override_disables_our_delta_instead_of_clobbering_it()
    {
        using var f = new Installation(); f.Generate();
        string other = Path.Combine(f.Root, "Mods", "other", "Data", "other.pak"); Directory.CreateDirectory(Path.GetDirectoryName(other)!);
        using (var zip = ZipFile.Open(other, ZipArchiveMode.Create)) { using var w = new StreamWriter(zip.CreateEntry(Male).Open()); w.Write(Adb); }
        byte[] original = File.ReadAllBytes(other);
        Assert.Equal("removed", f.Prepare(30).Action); Assert.False(File.Exists(f.Pak));
        f.Log(f.Now.AddSeconds(40), Warning());
        Assert.False(f.Prepare(50).Ok); Assert.False(File.Exists(f.Pak)); Assert.Equal(original, File.ReadAllBytes(other));
    }

    [Fact]
    public void Another_mod_asset_repair_invalidates_old_evidence_even_without_a_database_override()
    {
        using var f = new Installation(); f.Generate();
        string asset = Path.Combine(f.Root, "Mods", "repair", "Data", "Animations", "face.caf");
        Directory.CreateDirectory(Path.GetDirectoryName(asset)!); File.WriteAllText(asset, "repaired asset");
        Assert.Equal("removed", f.Prepare(30).Action); Assert.False(File.Exists(f.Pak));
        Assert.Equal("baseline", f.Prepare(40).Action);
    }

    [Fact]
    public void Log_without_a_new_run_header_cannot_authorize_a_patch()
    {
        using var f = new Installation(); f.Prepare();
        File.WriteAllText(Path.Combine(f.Root, "kcd.log"), Warning());
        File.SetLastWriteTimeUtc(Path.Combine(f.Root, "kcd.log"), f.Now.AddSeconds(10));
        Assert.Equal("baseline", f.Prepare(20).Action); Assert.False(File.Exists(f.Pak));
    }

    private sealed class Installation : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "kcd-facial-" + Guid.NewGuid().ToString("N"));
        public string Mod => Path.Combine(Root, "Mods", "kdcmp");
        public string Source => Path.Combine(Root, "Data", "Animations.pak");
        public string Pak => Path.Combine(Mod, "Data", FacialFallback.PakName);
        public string State => Path.Combine(Mod, FacialFallback.StateName);
        public DateTime Now { get; } = new(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);
        public Installation() { Directory.CreateDirectory(Path.Combine(Mod, "Data")); SourceAdb(Adb); }
        public void SourceAdb(string xml)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Source)!); if (File.Exists(Source)) File.Delete(Source);
            using var zip = ZipFile.Open(Source, ZipArchiveMode.Create);
            foreach (string db in new[] { Male, Female }) { using var w = new StreamWriter(zip.CreateEntry(db).Open()); w.Write(xml); }
        }
        public void Log(DateTime start, string text)
        {
            string path = Path.Combine(Root, "kcd.log");
            File.WriteAllText(path, "Log Started at " + start.ToLocalTime().ToString("yyyy-MM-dd  HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + "\n" + text);
            File.SetLastWriteTimeUtc(path, start.AddSeconds(1));
        }
        public FacialFallback.Outcome Prepare(int seconds = 0) => FacialFallback.Prepare(Root, Mod, false, Now.AddSeconds(seconds));
        public void Generate() { Prepare(); Log(Now.AddSeconds(10), Warning()); Assert.Equal("generated", Prepare(20).Action); }
        public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
    }
}
