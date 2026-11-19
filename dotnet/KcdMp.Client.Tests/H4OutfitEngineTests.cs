// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using MoonSharp.Interpreter;

namespace KcdMp.Client.Tests;

public sealed class H4OutfitEngineTests
{
    private const string Bow = "10000000-0000-0000-0000-000000000009";
    private const string Sword = "10000000-0000-0000-0000-000000000001";

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
        Action delayed = game.HandOff(operation, "00000001", "old", "0000000000000001");
        string body = boundary == "body" ? "00000002" : "00000001";
        game.ChangeScope(body, "new", boundary == "hand", readable: true);

        delayed();

        Assert.Equal(0d, game.Mutations);
        Assert.Equal(boundary == "hand" ? Bow : null, game.Hand);

        game.ChangeScope(body, "holstered", held: false, readable: true);
        game.HandOff(operation, body, "holstered", "0000000000000002")();

        Assert.Equal(operation == "atomic" ? 3d : 1d, game.Mutations);
        Assert.Equal(operation is "equip" or "atomic" ? Sword : null, game.Weapon);
    }

    [Theory]
    [InlineData("equip")]
    [InlineData("unequip")]
    [InlineData("atomic")]
    [InlineData("preset")]
    public void Unreadable_second_hand_defers_without_mutating(string operation)
    {
        var game = new OutfitGame();
        game.ChangeScope("00000001", "current", held: false, readable: false);
        game.HandOff(operation, "00000001", "current", "0000000000000001")();
        Assert.Equal(0d, game.Mutations);

        game.ChangeScope("00000001", "current", held: false, readable: true);
        game.HandOff(operation, "00000001", "current", "0000000000000002")();
        Assert.True(game.Mutations > 0);
    }

    [Fact]
    public void Actual_empty_hands_do_not_override_a_pending_native_hand_confirmation()
    {
        var game = new OutfitGame();
        game.Set("confirmedScope.known = 1");
        game.HandOff("preset", "00000001", "old", "0000000000000001")();
        Assert.Equal(0d, game.Mutations);
        game.Set("confirmedScope.known = 3");
        game.HandOff("preset", "00000001", "old", "0000000000000001")();
        Assert.Equal(1d, game.Mutations);
    }

    [Theory]
    [InlineData("equip", "create", 1)]
    [InlineData("atomic", "equip", 2)]
    public void Scope_is_rechecked_after_each_mutating_step(string operation, string transition, int accepted)
    {
        var game = new OutfitGame();
        game.Set($"changeDuring = '{transition}'; inventory['{Sword}'] = nil");
        if (operation == "atomic") game.Set($"inventory['{Sword}'] = '{Sword}'");
        game.HandOff(operation, "00000001", "old", "0000000000000001")();
        Assert.Equal(accepted, game.Mutations);
        Assert.Equal(Bow, game.Hand);

        game.ChangeScope("00000001", "holstered", held: false, readable: true);
        game.HandOff(operation, "00000001", "holstered", "0000000000000002")();
        Assert.Equal(Sword, game.Weapon);
    }

    private sealed class OutfitGame
    {
        private readonly Script _lua = new(CoreModules.Preset_HardSandbox | CoreModules.ErrorHandling);

        public OutfitGame()
        {
            _lua.DoString($$"""
                KCD2MP = { ghosts = {}, w136 = { PRESET_BARE = "bare" }, w144 = { stats = {}, LIGHTS = {}, lightStats = { made = 0, dressed = 0, dressFail = 0 } } }
                W214 = { hand = { } }
                function mp_log() end
                function KCD2MP_EmitEvent() end
                local bow = "{{Bow}}"
                mutations = 0
                inventory = {}
                weapon = bow
                body = {
                    id = 222, human = {}, actor = {}, inventory = {},
                }
                System = { GetEntityByName = function() return body end }
                ItemManager = { GetItem = function(item) return { class = item } end }
                inventory[bow] = bow
                function body.human:GetItemInHand(slot)
                    if slot == 1 and unreadable then error("second hand unavailable") end
                    return slot == 1 and held or nil
                end
                function body.inventory:FindItem(class) return inventory[class] end
                function body.inventory:CreateItem(class)
                    inventory[class] = class
                    mutations = mutations + 1
                    if changeDuring == 'create' then
                        changeDuring = nil
                        changeScope('00000001', 'new', true, true)
                    end
                end
                function body.actor:EquipInventoryItem(item)
                    weapon, held = item, nil
                    mutations = mutations + 1
                    if changeDuring == 'equip' then
                        changeDuring = nil
                        changeScope('00000001', 'new', true, true)
                    end
                end
                function body.actor:UnequipInventoryItem(item)
                    if weapon == item then weapon = nil end
                    if held == item then held = nil end
                    mutations = mutations + 1
                end
                function body.actor:EquipClothingPreset()
                    weapon, held = nil, nil
                    mutations = mutations + 1
                end
                function KCD2MP_W214HandScope(id) return confirmedScope end
                function changeScope(incarnation, scope, hasBow, readable)
                    KCD2MP.ghosts['1'] = { entity = body, outfitIncarnation = incarnation }
                    confirmedScope = { incarnation = incarnation, scope = scope, drawn = hasBow, known = 3, left = '-', right = hasBow and bow or '-' }
                    unreadable = not readable
                    held = hasBow and bow or nil
                end
                changeScope('00000001', 'old', false, true)
                inventory['{{Sword}}'] = '{{Sword}}'
                """);

            string source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Lua", "kdcmp.lua"));
            _lua.DoString(Function(source, "function KCD2MP_W136ClearPreset(", "\nend\n"));
            _lua.DoString(Function(source, "function KCD2MP_W144Equip(", "\nend\n"));
            _lua.DoString(Function(source, "function KCD2MP_W202EquipAll(", "\n    end\n"));
            const string start = "-- ===== H4 Outfit engine fence =====";
            int fence = source.IndexOf(start, StringComparison.Ordinal);
            if (fence >= 0)
            {
                int end = source.IndexOf("-- ===== WO-215:", fence, StringComparison.Ordinal);
                Assert.True(end > fence);
                _lua.DoString(source[fence..end]);
            }
        }

        public double Mutations => _lua.Globals.Get("mutations").Number;
        public string? Hand => _lua.Globals.Get("held").String;
        public string? Weapon => _lua.Globals.Get("weapon").String;

        public void Set(string command) => _lua.DoString(command);

        public void ChangeScope(string body, string scope, bool held, bool readable) =>
            _lua.Call(_lua.Globals.Get("changeScope"), DynValue.NewString(body), DynValue.NewString(scope),
                DynValue.NewBoolean(held), DynValue.NewBoolean(readable));

        public Action HandOff(string operation, string body, string scope, string order)
        {
            string items = operation switch
            {
                "unequip" => Bow,
                "atomic" => $"10000000-0000-0000-0000-000000000002,{Sword}",
                "equip" => Sword,
                _ => ""
            };
            string old = operation switch
            {
                "unequip" => $"body.actor:UnequipInventoryItem(body.inventory:FindItem('{Bow}'))",
                "atomic" => $"KCD2MP_W202EquipAll('kcd2mp_1', '{items}')",
                "equip" => $"KCD2MP_W144Equip('kcd2mp_1', '{items}')",
                _ => "KCD2MP_W136ClearPreset('1', 'test')"
            };
            string command = $"if KCD2MP_W214OutfitApply then KCD2MP_W214OutfitApply('1', '{body}', '{scope}', '{order}', '{operation}', '{items}') else {old} end";
            return () => _lua.DoString(command);
        }

        private static string Function(string source, string start, string end)
        {
            int first = source.IndexOf(start, StringComparison.Ordinal);
            Assert.True(first >= 0);
            int last = source.IndexOf(end, first + start.Length, StringComparison.Ordinal);
            Assert.True(last > first);
            return source[first..(last + end.Length)];
        }
    }
}
