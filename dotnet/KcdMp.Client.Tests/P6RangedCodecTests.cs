// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using KcdMp.Wire;

namespace KcdMp.Client.Tests;

public sealed class P6RangedCodecTests
{
    private static Guid G(int n) => new(n, 0, 0, new byte[8]);
    private static RangedCapture Capture(byte phase = 3) => new(phase, 1, 3, uint.MaxValue, 0x1122334455667788, Guid.Empty, G(9));
    private static byte[] Poll(params RangedCapture[] rows)
    {
        byte[] p = new byte[18 + rows.Length * 48]; p[0] = 2; p[1] = (byte)rows.Length;
        BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(2), 123); BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(10), 456);
        for (int i = 0; i < rows.Length; i++)
        {
            var r = rows[i]; var b = p.AsSpan(18 + i * 48, 48); b[0] = r.Phase; b[1] = r.Source; b[2] = r.KnownMask;
            BinaryPrimitives.WriteUInt32LittleEndian(b[4..], r.Sequence); BinaryPrimitives.WriteUInt64LittleEndian(b[8..], r.CapturedTick);
            r.Left.TryWriteBytes(b[16..]); r.Right.TryWriteBytes(b[32..]);
        }
        return p;
    }
    private static MeasuredMissileRequest Request() => new(new(uint.MaxValue, ulong.MaxValue, 12, 34), "npc_bandit_01", 12.5f, 3.25f);
    private static byte[] Evidence()
    {
        byte[] b = new byte[61]; G(77).TryWriteBytes(b); BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(16), 3.25f);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(20), 12.5f); b[25] = 1; b[27] = 1; b[28] = 7;
        for (int i = 0; i < 4; i++) BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(29 + 8 * i), (ulong)(100 + i));
        return b;
    }
    private static byte[] Ack(MeasuredMissileKey key, byte source, MeasuredMissileStatus status, byte steps = 1)
    {
        byte[] b = new byte[40]; b[0] = 1; b[1] = (byte)status; b[2] = steps; b[3] = source;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), key.World); BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(8), key.SourceGeneration);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(16), key.Epoch); BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(24), key.ContactId);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(32), 12.5f); BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(36), 3.25f); return b;
    }
    private sealed class Pak : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kct-p6-" + Guid.NewGuid().ToString("N") + ".pak");
        public Pak(string xml)
        {
            using var z = ZipFile.Open(Path, ZipArchiveMode.Create);
            using (var w = new StreamWriter(z.CreateEntry("Libs/Tables/item/item.xml").Open(), Encoding.UTF8)) w.Write("<Database>" + xml + "</Database>");
            using var ignored = new StreamWriter(z.CreateEntry("Libs/other/item.xml").Open(), Encoding.UTF8);
            ignored.Write($"<Database><MissileWeapon Id=\"{G(999):D}\" Class=\"9\"/></Database>");
        }
        public void Dispose() => File.Delete(Path);
    }
    private static string Weapon(int id, string element, int cls, string model = "irrelevant") =>
        $"<{element} Id=\"{G(id):D}\" Class=\"{cls}\" Model=\"{model}\"/>";
    private static string Alias(int id, int source) => $"<ItemAlias Id=\"{G(id):D}\" SourceItemId=\"{G(source):D}\"/>";

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public void Native_poll_preserves_every_actual_phase_both_classes_and_original_capture_time(byte phase)
    {
        var r = Capture(phase) with { Left = G(10), Right = G(9) }; var p = Poll(r);
        Assert.Equal(66, p.Length); Assert.True(RangedCaptureCodec.TryDecode(p, out var read));
        Assert.Equal(123UL, read.Generation); Assert.Equal(456UL, read.Epoch); Assert.Equal(r, Assert.Single(read.Rows));
        Assert.Equal(0x88, p[18 + 8]); Assert.Equal(0x11, p[18 + 15]); Assert.Equal(10, p[18 + 16]); Assert.Equal(9, p[18 + 32]);
    }
    [Fact]
    public void Native_queue_page_cap_is_ten_complete_rows_inside_the_existing_512_byte_reply()
    {
        var p = Poll(Enumerable.Range(0, 10).Select(i => Capture((byte)(i % 7)) with { Sequence = (uint)(i + 1) }).ToArray());
        Assert.Equal(498, p.Length); Assert.True(p.Length + 4 <= 512); Assert.True(RangedCaptureCodec.TryDecode(p, out var reply));
        Assert.Equal(Enumerable.Range(1, 10).Select(i => (uint)i), reply.Rows.Select(r => r.Sequence));
        Assert.True(RangedCaptureCodec.TryDecode(Poll(), out var empty)); Assert.Empty(empty.Rows);
        Assert.False(RangedCaptureCodec.TryDecode(Poll(Enumerable.Repeat(Capture(), 11).ToArray()), out _));
    }
    [Fact]
    public void Native_poll_rejects_truncation_extra_bytes_invalid_status_padding_and_unproven_class_bytes()
    {
        var p = Poll(Capture());
        for (int n = 0; n < p.Length; n++) Assert.False(RangedCaptureCodec.TryDecode(p.AsSpan(0, n), out _));
        Assert.False(RangedCaptureCodec.TryDecode([.. p, 0], out _));
        foreach (int at in new[] { 0, 1, 18, 19, 20, 21 })
        { var bad = p.ToArray(); bad[at] = 255; Assert.False(RangedCaptureCodec.TryDecode(bad, out _)); }
        foreach (int at in new[] { 2, 10 }) { var bad = p.ToArray(); bad.AsSpan(at, 8).Clear(); Assert.False(RangedCaptureCodec.TryDecode(bad, out _)); }
        Assert.False(RangedCaptureCodec.TryDecode(Poll(Capture() with { KnownMask = 1 }), out _)); // right class lacks right-hand proof
        Assert.False(RangedCaptureCodec.TryDecode(Poll(Capture() with { KnownMask = 2, Left = G(10) }), out _));
        Assert.True(RangedCaptureCodec.TryDecode(Poll(Capture() with { Source = 0, KnownMask = 0, Right = Guid.Empty }), out var unknown));
        Assert.Equal((byte)0, Assert.Single(unknown.Rows).KnownMask);
    }
    [Fact]
    public void Poll_request_is_explicit_version_on_flag_and_full_width_generation()
    {
        var on = RangedCaptureCodec.Request(true, ulong.MaxValue); Assert.Equal(10, on.Length); Assert.Equal(new byte[] { 2, 1 }, on[..2]);
        Assert.Equal(ulong.MaxValue, BinaryPrimitives.ReadUInt64LittleEndian(on.AsSpan(2)));
        Assert.Equal(0, RangedCaptureCodec.Request(false, 1)[1]);
    }
    [Theory]
    [InlineData(RangedWeaponKind.Bow)] [InlineData(RangedWeaponKind.Crossbow)] [InlineData(RangedWeaponKind.Unknown)]
    public void Ranged_v2_carries_kind_class_source_world_and_sender_identity_without_model_inference(RangedWeaponKind kind)
    {
        var step = new RangedStepV2(uint.MaxValue, ulong.MaxValue, 12, uint.MaxValue, 3, kind, kind == RangedWeaponKind.Unknown ? Guid.Empty : G(9), 2000, kind == RangedWeaponKind.Unknown ? (byte)0 : (byte)1);
        Assert.True(RangedStepV2.TryDecode(step.Encode(), out var read)); Assert.Equal(step, read);
        Assert.True(step.Encode().Length < 256);
    }
    [Fact]
    public void Ranged_v2_refuses_legacy_unknown_enums_invalid_identity_age_and_known_type_without_current_class()
    {
        var step = new RangedStepV2(7, 8, 9, 10, 3, RangedWeaponKind.Bow, G(9), 12, 1);
        var text = step.Encode(); Assert.False(RangedStepV2.TryDecode("v1|3", out _)); Assert.False(RangedStepV2.TryDecode(text + "|extra", out _));
        foreach (var (at, value) in new (int, string)[] { (0,"v3"),(1,"0"),(1,"-1"),(1," 7"),(2,"0"),(3,"0"),(5,"7"),(6,"3"),(7,"broken"),(7,Guid.Empty.ToString("D")),(8,"2001"),(9,"0"),(9,"2") })
        { var fields = text.Split('|'); fields[at] = value; Assert.False(RangedStepV2.TryDecode(string.Join('|', fields), out _)); }
    }
    [Fact]
    public void Installed_missile_rows_and_aliases_decide_kind_even_when_models_lie()
    {
        using var pak = new Pak(Weapon(9,"MissileWeapon",9,"crossbow.cgf") + Weapon(10,"MissileWeapon",10,"bow.cgf") + Weapon(14,"MissileWeapon",14) + Weapon(15,"MissileWeapon",15) +
            Weapon(13,"MissileWeapon",13,"bow.cgf") + Weapon(1,"Ammo",9,"bow.cgf") + Weapon(2,"MeleeWeapon",9,"bow.cgf") + Alias(90,9) + Alias(91,90));
        var c = RangedItemCatalog.Read(pak.Path);
        Assert.Equal(RangedWeaponKind.Bow, c.Kind(G(9))); foreach (int id in new[] { 10,14,15 }) Assert.Equal(RangedWeaponKind.Crossbow, c.Kind(G(id)));
        Assert.Equal(RangedWeaponKind.Bow, c.Kind(G(90))); Assert.Equal(RangedWeaponKind.Bow, c.Kind(G(91)));
        foreach (int id in new[] { 1,2,13,999,12345 }) Assert.Equal(RangedWeaponKind.Unknown, c.Kind(G(id)));
    }
    [Fact]
    public void Missing_cyclic_and_conflicting_item_or_alias_definitions_stay_unknown()
    {
        using var pak = new Pak(Weapon(9,"MissileWeapon",9)+Weapon(10,"MissileWeapon",10)+Alias(80,81)+Alias(81,80)+Alias(82,404)+
            Weapon(50,"MissileWeapon",9)+Weapon(50,"MissileWeapon",10)+Alias(60,9)+Alias(60,10));
        var c = RangedItemCatalog.Read(pak.Path);
        foreach (int id in new[] { 80,81,82,50,60 }) Assert.Equal(RangedWeaponKind.Unknown, c.Kind(G(id)));
    }
    [Fact]
    public void Classification_requires_both_known_hands_and_exactly_one_supported_missile_item()
    {
        using var pak = new Pak(Weapon(9,"MissileWeapon",9)+Weapon(10,"MissileWeapon",10)+Weapon(13,"MissileWeapon",13)+Weapon(1,"Ammo",9));
        var c = RangedItemCatalog.Read(pak.Path);var bow = Capture();
        Assert.Equal((RangedWeaponKind.Bow,G(9)), c.Classify(bow));
        Assert.Equal((RangedWeaponKind.Bow,G(9)), c.Classify(bow with { Left = G(9), Right = G(1) }));
        Assert.Equal((RangedWeaponKind.Crossbow,G(10)), c.Classify(bow with { Left = G(10), Right = Guid.Empty }));
        foreach (var unknown in new[] { bow with { Source = 0 }, bow with { KnownMask = 0 }, bow with { KnownMask = 1 }, bow with { KnownMask = 2 },
            bow with { Left = G(404) }, bow with { Left = G(10) }, bow with { Left = G(9) }, bow with { Right = G(13) }, bow with { Right = Guid.Empty } })
            Assert.Equal(RangedWeaponKind.Unknown, c.Classify(unknown).Kind);
    }
    [Fact]
    public void Evidence_extension_preserves_old_judge_offset_and_demands_known_attributed_measured_contact()
    {
        var p = Evidence();Assert.True(MeasuredMissileEvidence.TryDecode(p, out var e));Assert.Equal(new MeasuredMissileEvidence(100,101,102,103),e);
        Assert.Equal(0,p[26]); // existing WO210 judge remains byte26, extension starts byte27
        for (int n=0;n<p.Length;n++) Assert.False(MeasuredMissileEvidence.TryDecode(p.AsSpan(0,n),out _));
        Assert.False(MeasuredMissileEvidence.TryDecode([..p,0],out _));
        foreach(int at in new[]{25,26,27,28}){var bad=p.ToArray();bad[at]=255;Assert.False(MeasuredMissileEvidence.TryDecode(bad,out _));}
        foreach(int at in new[]{29,37,45,53}){var bad=p.ToArray();bad.AsSpan(at,8).Clear();Assert.False(MeasuredMissileEvidence.TryDecode(bad,out _));}
        foreach(float value in new[]{float.NaN,float.PositiveInfinity,-1f,1000f}){var bad=p.ToArray();BinaryPrimitives.WriteSingleLittleEndian(bad.AsSpan(20),value);Assert.False(MeasuredMissileEvidence.TryDecode(bad,out _));}
        p.AsSpan(16,8).Clear();Assert.False(MeasuredMissileEvidence.TryDecode(p,out _));
    }
    [Fact]
    public void Measured_request_and_result_text_are_culture_invariant_and_keep_full_receipt_key()
    {
        var saved=CultureInfo.CurrentCulture;try{CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("de-DE");var r=Request();
            Assert.True(MeasuredMissileRequest.TryDecode(r.Encode(),out var read));Assert.Equal(r,read);
            var result=new MeasuredMissileResult(r.Key,MeasuredMissileStatus.Accepted,1,12.5f,3.25f);
            Assert.True(MeasuredMissileResult.TryDecode(result.Encode(),out var parsed));Assert.Equal(result.Key,parsed.Key);Assert.Equal(result.Status,parsed.Status);Assert.Equal(12.5f,parsed.Health);Assert.Equal(3.25f,parsed.Stamina);
        }finally{CultureInfo.CurrentCulture=saved;}
    }
    [Fact]
    public void Measured_text_refuses_nonfinite_zero_delta_invalid_npc_and_incomplete_receipt()
    {
        var text=Request().Encode();
        foreach(var (at,value) in new (int,string)[]{(0,"v1"),(1,"0"),(2,"0"),(3,"0"),(4,"0"),(5,"3"),(6,"a|b"),(6,"npc name"),(7,"NaN"),(7,"Infinity"),(7,"-1"),(8,"1000")})
        {var f=text.Split('|');f[at]=value;Assert.False(MeasuredMissileRequest.TryDecode(string.Join('|',f),out _));}
        Assert.False(MeasuredMissileRequest.TryDecode((Request() with {Health=0,Stamina=0}).Encode(),out _));
        Assert.False(MeasuredMissileResult.TryDecode(new MeasuredMissileResult(Request().Key,(MeasuredMissileStatus)9,0,0,0).Encode(),out _));
    }
    [Theory]
    [InlineData(0)] [InlineData(15)]
    public void Native_receipt_extension_is_distinct_from_unchanged_melee_layout(byte source)
    {
        var r=Request();var victim=G(77);const uint avatar=0x12345678;const ulong host=0x8877665544332211,deadline=0x1122334455667788;
        var b=MeasuredMissileCodec.BuildArgs(source,r,victim,avatar,host,deadline);int tail=38+r.Npc.Length;
        Assert.Equal(tail+50,b.Length);Assert.Equal(255,b[0]);Assert.Equal(255,b[1]);Assert.Equal(128,b[7]);
        Assert.Equal(victim,new Guid(b.AsSpan(8,16)));Assert.Equal(3.25f,BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(24)));Assert.Equal(12.5f,BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(28)));
        Assert.Equal(avatar,BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(33)));Assert.Equal(r.Npc,Encoding.ASCII.GetString(b,38,r.Npc.Length));
        Assert.Equal("P6M1",Encoding.ASCII.GetString(b,tail,4));Assert.Equal(source,b[tail+4]);Assert.Equal(7,b[tail+5]);
        Assert.Equal(r.Key.World,BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(tail+6)));Assert.Equal(host,BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(tail+10)));
        Assert.Equal(r.Key.SourceGeneration,BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(tail+18)));Assert.Equal(r.Key.Epoch,BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(tail+26)));
        Assert.Equal(r.Key.ContactId,BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(tail+34)));Assert.Equal(deadline,BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(tail+42)));
    }
    [Fact]
    public void Invalid_receipt_never_gets_native_write_arguments()
    {
        var r=Request();
        foreach(byte source in new byte[]{16,255})Assert.Throws<ArgumentException>(()=>MeasuredMissileCodec.BuildArgs(source,r,G(77),1,1,1));
        Assert.Throws<ArgumentException>(()=>MeasuredMissileCodec.BuildArgs(0,r,Guid.Empty,1,1,1));
        Assert.Throws<ArgumentException>(()=>MeasuredMissileCodec.BuildArgs(0,r,G(77),0,1,1));
        Assert.Throws<ArgumentException>(()=>MeasuredMissileCodec.BuildArgs(0,r,G(77),1,0,1));
        Assert.Throws<ArgumentException>(()=>MeasuredMissileCodec.BuildArgs(0,r,G(77),1,1,0));
        Assert.Throws<ArgumentException>(()=>MeasuredMissileCodec.BuildArgs(0,r with {Health=float.NaN},G(77),1,1,1));
    }
    [Theory]
    [InlineData(MeasuredMissileStatus.Accepted)] [InlineData(MeasuredMissileStatus.Scope)] [InlineData(MeasuredMissileStatus.Victim)]
    [InlineData(MeasuredMissileStatus.Arranged)] [InlineData(MeasuredMissileStatus.Invalid)] [InlineData(MeasuredMissileStatus.Uncertain)]
    [InlineData(MeasuredMissileStatus.Capacity)] [InlineData(MeasuredMissileStatus.Conflict)] [InlineData(MeasuredMissileStatus.Expired)]
    public void Native_reply_accepts_only_its_exact_owner_and_receipt_identity(MeasuredMissileStatus status)
    {
        var key=Request().Key;var p=Ack(key,0,status);Assert.True(MeasuredMissileCodec.TryReadReply(p,0,key,out var read));
        Assert.Equal(key,read.Key);Assert.Equal(status,read.Status);Assert.Equal(12.5f,read.Health);
        Assert.False(MeasuredMissileCodec.TryReadReply(p,1,key,out _));
        foreach(var wrong in new[]{key with {World=7},key with {SourceGeneration=1},key with {Epoch=1},key with {ContactId=1}})Assert.False(MeasuredMissileCodec.TryReadReply(p,0,wrong,out _));
    }
    [Fact]
    public void Native_reply_is_not_success_without_write_step_or_with_unknown_version_size_or_values()
    {
        var key=Request().Key;var p=Ack(key,0,MeasuredMissileStatus.Accepted,0);Assert.False(MeasuredMissileCodec.TryReadReply(p,0,key,out _));
        p=Ack(key,0,MeasuredMissileStatus.Accepted);
        for(int n=0;n<p.Length;n++)Assert.False(MeasuredMissileCodec.TryReadReply(p.AsSpan(0,n),0,key,out _));
        Assert.False(MeasuredMissileCodec.TryReadReply([..p,0],0,key,out _));
        foreach(int at in new[]{0,1}){var bad=p.ToArray();bad[at]=255;Assert.False(MeasuredMissileCodec.TryReadReply(bad,0,key,out _));}
        foreach(int at in new[]{32,36}){var bad=p.ToArray();BinaryPrimitives.WriteSingleLittleEndian(bad.AsSpan(at),float.NaN);Assert.False(MeasuredMissileCodec.TryReadReply(bad,0,key,out _));}
    }
}
