// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using MoonSharp.Interpreter;

namespace KcdMp.Client.Tests;

public sealed class H4OutfitEngineTests
{
    private const string Bow = "10000000-0000-0000-0000-000000000009";
    private const string Sword = "10000000-0000-0000-0000-000000000001";
    private const string Armor = "10000000-0000-0000-0000-000000000002";
    private const string Birth = "B0B218";
    private const string Revision = "0000000000000001";

    [Theory]
    [InlineData("equip", "hand")]
    [InlineData("unequip", "hand")]
    [InlineData("atomic", "hand")]
    [InlineData("preset", "hand")]
    [InlineData("equip", "body")]
    [InlineData("unequip", "body")]
    [InlineData("atomic", "body")]
    [InlineData("preset", "body")]
    [InlineData("unequip", "revision")]
    public void Delayed_outfit_changes_zero_items_and_current_outfit_converges(string operation, string boundary)
    {
        var game = new OutfitGame();
        string birth = boundary == "body" ? "B0B219" : Birth;
        string revision = boundary == "body" ? Revision : "0000000000000002";
        game.Scope(birth, revision, boundary == "hand");

        game.AssertRefused(operation, Birth, Revision);
        Assert.Equal(0d, game.Mutations);
        Assert.Equal(boundary == "hand" ? Bow : null, game.Hand);

        game.Scope(birth, revision);
        game.AssertApplied(operation, birth, revision);
    }

    [Theory]
    [InlineData("equip")]
    [InlineData("unequip")]
    [InlineData("atomic")]
    [InlineData("preset")]
    public void Unreadable_second_hand_defers_without_mutating(string operation)
    {
        var game = new OutfitGame();
        game.Set("unreadable = true");
        game.AssertRefused(operation, Birth, Revision);
        Assert.Equal(0d, game.Mutations);
        game.Set("unreadable = false");
        game.AssertApplied(operation, Birth, Revision);
    }

    [Theory]
    [InlineData("equip")]
    [InlineData("unequip")]
    [InlineData("atomic")]
    [InlineData("preset")]
    public void Dispatched_combat_draw_defers_the_previous_holstered_outfit_until_confirmed_empty(string operation)
    {
        var game = new OutfitGame();
        game.Set("KCD2MP_GhostCombat('1', 0); assert(KCD2MP.ghostWeaponDrawn['1'])");
        Assert.Null(game.Hand);
        game.AssertRefused(operation, Birth, Revision);
        Assert.Equal(0d, game.Mutations);

        game.Set("KCD2MP_GhostCombat('1', 1); assert(not KCD2MP.ghostWeaponDrawn['1'])");
        const string revision = "0000000000000002";
        game.Scope(Birth, revision);
        game.AssertApplied(operation, Birth, revision);
    }

    [Theory]
    [InlineData("equip", "create", 1)]
    [InlineData("atomic", "equip", 2)]
    public void Scope_is_rechecked_after_each_mutating_step(string operation, string transition, int accepted)
    {
        var game = new OutfitGame();
        game.Set($"changeDuring = '{transition}'; inventory['{Sword}'] = nil");
        game.Apply(operation, Birth, Revision);
        Assert.Equal(accepted, game.Mutations);
        Assert.Equal(Bow, game.Hand);

        const string revision = "0000000000000003";
        game.Scope(Birth, revision);
        game.Apply(operation, Birth, revision);
        Assert.Equal(Sword, game.Weapon);
    }

    private sealed class OutfitGame
    {
        private readonly Script _lua = new(CoreModules.Preset_HardSandbox | CoreModules.ErrorHandling);
        private static readonly string Source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Lua", "kdcmp.lua"))
            .ReplaceLineEndings("\n");

        public OutfitGame()
        {
            _lua.DoString($$"""
                KCD2MP = { ghosts = {}, ghostWeaponDrawn = {}, w136 = { PRESET_BARE = "bare" },
                    w144 = { lightStats = { made = 0, dressed = 0, dressFail = 0 } } }
                W214 = { hand = {} }
                function mp_log(line) lastLog = line end
                function KCD2MP_W214On() return true end
                KCD2MP.w136.handClass = function(e, slot) return e.human:GetItemInHand(slot) end
                local bow = "{{Bow}}"
                mutations = 0
                inventory = { [bow] = bow, ["{{Sword}}"] = "{{Sword}}" }
                weapon = bow
                avatar = { id = "000000DE", human = {}, actor = {}, inventory = {} }
                KCD2MP.ghosts['1'] = { entity = avatar }
                function scope(birth, revision, drawn)
                    local g = KCD2MP.ghosts['1']
                    g.outfitBirth, g.w214HandRevision = birth, revision
                    g.w214OutfitCuePending = nil
                    g.w214HandClass = drawn and bow or nil
                    held = drawn and bow or nil
                end
                function avatar.human:GetItemInHand(slot)
                    if slot == 1 and unreadable then error("second hand unavailable") end
                    return slot == 1 and held or nil
                end
                function avatar.human:HolsterWeapon() held = nil end
                function avatar.inventory:FindItem(class) return inventory[class] end
                local function changed(operation)
                    mutations = mutations + 1
                    if changeDuring == operation then
                        changeDuring = nil
                        scope("{{Birth}}", "0000000000000002", true)
                    end
                end
                function avatar.inventory:CreateItem(class) inventory[class] = class; changed("create") end
                function avatar.actor:EquipInventoryItem(item) weapon = item; changed("equip") end
                function avatar.actor:UnequipInventoryItem(item)
                    if weapon == item then weapon = nil end
                    if held == item then held = nil end
                    changed("unequip")
                end
                function avatar.actor:EquipClothingPreset() weapon, held = nil, nil; changed("preset") end
                scope("{{Birth}}", "{{Revision}}", false)
                """);

            LoadFunction("function KCD2MP_W136ClearPreset(", "\nend\n");
            LoadFunction("function KCD2MP_W144Equip(", "\nend\n");
            LoadFunction("function KCD2MP_W144Unequip(", "\nend\n");
            LoadFunction("function KCD2MP_W214DrawWaits(", "\n    end\n");
            LoadFunction("function KCD2MP_GhostCombat(", "\nend\n");
            LoadFunction("function KCD2MP_W202EquipAll(", "\n    end\n");
            int start = Source.IndexOf("local function w214BodyMatches(", StringComparison.Ordinal);
            Assert.True(start >= 0);
            int end = Source.IndexOf("function KCD2MP_W214OwnHandTick(", start, StringComparison.Ordinal);
            Assert.True(end > start);
            _lua.DoString(Source[start..end]);
        }

        private void LoadFunction(string marker, string ending)
        {
            int start = Source.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(start >= 0);
            int end = Source.IndexOf(ending, start + marker.Length, StringComparison.Ordinal);
            Assert.True(end > start);
            _lua.DoString(Source[start..(end + ending.Length)]);
        }

        public double Mutations => _lua.Globals.Get("mutations").Number;
        public string? Hand => _lua.Globals.Get("held").String;
        public string? Weapon => _lua.Globals.Get("weapon").String;
        private string? LastLog => _lua.Globals.Get("lastLog").String;

        public void Set(string command) => _lua.DoString(command);
        public void Scope(string birth, string revision, bool drawn = false) =>
            _lua.Call(_lua.Globals.Get("scope"), birth, revision, drawn);

        public bool Apply(string operation, string birth, string revision)
        {
            string stamp = $"'000000DE','{revision}','{birth}'";
            return _lua.DoString("return " + (operation switch
            {
                "unequip" => $"KCD2MP_W144Unequip('kcd2mp_1','{Bow}',{stamp})",
                "atomic" => $"KCD2MP_W202EquipAll('kcd2mp_1','{Armor},{Sword}',{stamp}) == 2",
                "equip" => $"KCD2MP_W144Equip('kcd2mp_1','{Sword}',{stamp})",
                "preset" => $"KCD2MP_W136ClearPreset('1','fixture',{stamp})",
                _ => throw new ArgumentOutOfRangeException(nameof(operation))
            })).Boolean;
        }

        public void AssertRefused(string operation, string birth, string revision) =>
            Assert.False(Apply(operation, birth, revision), LastLog);

        public void AssertApplied(string operation, string birth, string revision)
        {
            Assert.True(Apply(operation, birth, revision), LastLog);
            Assert.Equal(operation == "atomic" ? 3d : 1d, Mutations);
            Assert.Equal(operation is "equip" or "atomic" ? Sword : null, Weapon);
        }
    }
}
