-- Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
-- F5: full worn-set repair against actual mod functions, deterministic engine model.
NOW = 0
os.clock = function() return NOW end
LOG = {}; TIMERS = {}; ENTS = {}; ERRS = {}; TOASTS = {}; CMDS = {}; CCMDS = {}; LOCKS = {}

local function mkstub()
    return setmetatable({}, { __index = function(_, k) return function(...) return nil end end })
end
local function d2(a, b) return (a.x - b.x) ^ 2 + (a.y - b.y) ^ 2 + (a.z - b.z) ^ 2 end
System = mkstub()
System.LogAlways = function(s) LOG[#LOG + 1] = tostring(s) end
System.GetCVarValue = function() return "0" end
System.GetCVar = function() return "0" end
System.GetEntityByName = function(n) return ENTS[n] end
System.GetEntitiesInSphere = function(p, r)
    local o = {}
    for _, e in pairs(ENTS) do
        if e.GetWorldPos and d2(e:GetWorldPos(), p) <= r * r then o[#o + 1] = e end
    end
    table.sort(o, function(a, b) return a.id < b.id end)
    return o
end
System.ExecuteCommand = function(s) CMDS[#CMDS + 1] = tostring(s) end
System.RemoveEntity = function(eid) for n, e in pairs(ENTS) do if e.id == eid then ENTS[n] = nil end end end
System.AddCCommand = function(name, body, help) CCMDS[name] = true end
Script = mkstub()
Script.SetTimer = function(ms, f) TIMERS[#TIMERS + 1] = { ms = ms, f = f, at = NOW } end
Game = mkstub(); AI = mkstub(); Sound = mkstub(); Physics = mkstub(); Terrain = mkstub()
Game.AddSaveLock = function(name) LOCKS[name] = true; return true end
Game.RemoveSaveLock = function(name) LOCKS[name] = nil; return true end
UIAction = mkstub()
UIAction.CallFunction = function(panel, inst, fn, text) TOASTS[#TOASTS + 1] = tostring(text) end
WORLD_T = 1000
Calendar = { GetWorldTime = function() return WORLD_T end, SetWorldTime = function(t) WORLD_T = t end,
             GetWorldTimeRatio = function() return 15 end, SetWorldTimeRatio = function() end }

-- ---- items and inventories: the engine's move (AddItem takes the item out of the inventory that held it) ----
ITEMS = {}          -- wuid -> { class, amount, health, owner, stolen }
NEXTWUID = 100
INVS = {}
local function newItem(cls, hp, amt)
    NEXTWUID = NEXTWUID + 1
    local w = "wuid" .. NEXTWUID
    ITEMS[w] = { class = cls, amount = amt or 1, health = hp or 1, id = w }
    return w
end
ItemManager = mkstub()
ItemManager.GetItem = function(w) return ITEMS[w] end
ItemManager.GetItemOwner = function(w) return ITEMS[w] and ITEMS[w].owner or nil end
ItemManager.SetItemOwner = function(w, o, stolen) if ITEMS[w] then ITEMS[w].owner = o; ITEMS[w].stolen = stolen end end
local function mkInventory()
    local inv = { list = {} }
    INVS[#INVS + 1] = inv
    inv.GetInventoryTable = function(self) local t = {}; for i, w in ipairs(self.list) do t[i] = w end; return t end
    inv.CreateItem = function(self, cls, hp, amt) local w = newItem(cls, hp, amt); self.list[#self.list + 1] = w; return true end
    inv.AddItem = function(self, w)
        if not ITEMS[w] then error("no such item") end
        for _, other in ipairs(INVS) do
            for i, x in ipairs(other.list) do if x == w then table.remove(other.list, i); break end end
        end
        self.list[#self.list + 1] = w
    end
    inv.HasItem = function(self, w) for _, x in ipairs(self.list) do if x == w then return true end end; return false end
    inv.DeleteItem = function(self, w, n)
        for i, x in ipairs(self.list) do if x == w then table.remove(self.list, i); ITEMS[w] = nil; return true end end
        return false
    end
    inv.RemoveItem = function(self, w) for i, x in ipairs(self.list) do if x == w then table.remove(self.list, i); return w end end end
    inv.GetCountOfClass = function(self, cls) local n = 0; for _, w in ipairs(self.list) do if ITEMS[w] and ITEMS[w].class == cls then n = n + ITEMS[w].amount end end; return n end
    inv.GetId = function() return "inv" end
    return inv
end
NEXTID = 5000

player = { id = 1, class = "Player", inventory = mkInventory(), this = { id = "wuid_player" } }
player.GetName = function() return "Dude" end
player.GetWorldPos = function() return { x = 100, y = 100, z = 10 } end
player.GetWorldAngles = function() return { x = 0, y = 0, z = 0 } end
player.actor = { GetHealth = function() return 100 end, IsDead = function() return false end,
                 UnequipInventoryItem = function(_, w) UNEQUIPPED = (UNEQUIPPED or 0) + 1 end }
player.human = { IsInDialog = function() return false end }
ENTS["Dude"] = player

System.SpawnEntity = function(t)
    NEXTID = NEXTID + 1
    local e = { class = t.class, id = NEXTID, px = t.position.x, py = t.position.y, pz = t.position.z, inventory = mkInventory(), hidden = false }
    e.GetName = function() return t.name end
    e.GetWorldPos = function(self) return { x = self.px, y = self.py, z = self.pz } end
    e.Hide = function(self, v) self.hidden = v == 1 end
    e.IsHidden = function(self) return self.hidden end
    ENTS[t.name] = e
    return e
end

local rawpcall = pcall
pcall = function(f, ...)
    local r = { rawpcall(f, ...) }
    if not r[1] then ERRS[#ERRS + 1] = tostring(r[2]) end
    return table.unpack(r)
end

local function mkBody(name, x, y, z)
    NEXTID = NEXTID + 1
    local e = { class = "NPC", id = NEXTID, px = x, py = y, pz = z, dead = true, inventory = mkInventory(), equipped = {}, this = { id = "wuid_" .. name } }
    e.GetName = function() return name end
    e.GetWorldPos = function(self) return { x = self.px, y = self.py, z = self.pz } end
    e.actor = { IsDead = function() return e.dead end, IsUnconscious = function() return false end,
                EquipInventoryItem = function(_, w) e.equipped[#e.equipped + 1] = w end }
    e.human = { IsInDialog = function() return false end }
    e.Hide = function() end
    e.IsHidden = function() return false end
    ENTS[name] = e
    return e
end
XGenAIModule = mkstub()
XGenAIModule.GetEntityByWUID = function(w) for _, e in pairs(ENTS) do if e.this and e.this.id == w then return e end end end
LOOTS = {}
BasicAIActions = {}
function BasicAIActions:OnLoot(user, slot) LOOTS[#LOOTS + 1] = self:GetName() end
function BasicAIActions:OnPickpocketing(user, slot) LOOTS[#LOOTS + 1] = "pp:" .. self:GetName() end


-- @@KDCMP@@
local RESULTS = {}
local function check(name, ok) RESULTS[#RESULTS + 1] = (ok and "PASS  " or "FAIL  ") .. name end
local equipped, owned, calls = {}, {}, {}
local entity = { inventory = {}, actor = {}, dead = false }
function entity.actor:IsDead() return entity.dead end
function entity.inventory:FindItem(cls) return owned[cls] and cls or nil end
function entity.inventory:CreateItem(cls) owned[cls] = true end
function entity.actor:UnequipInventoryItem(cls) calls[#calls + 1] = 'off:' .. cls; equipped[cls] = nil end
function entity.actor:EquipInventoryItem(cls)
    calls[#calls + 1] = 'on:' .. cls
    if cls == 'outer' and (not equipped.under or equipped.extra) then return false end
    equipped[cls] = true
end
ENTS.outfit_test_npc = entity
KCD2MP.npcPuppets.outfit_test_npc = { lastPacketAt = NOW, dead = false }
owned = { under = true, outer = true, extra = true }; equipped = { outer = true, extra = true }; calls = {}
KCD2MP_W216Dress('outfit_test_npc', 'under,outer', 'extra')
check('F5 removes the wrong slot occupant before equipping the entire layered host set', calls[1] == 'off:extra' and calls[2] == 'on:under' and calls[3] == 'on:outer')
check('F5 complete outfit converges rather than retrying only the outer difference', equipped.under and equipped.outer and not equipped.extra)
equipped = { under = true, outer = true }; calls = {}
KCD2MP_W216Dress('outfit_test_npc', 'under,outer', '')
check('F5 equal class set is still fully re-equipped to repair slot drift', #calls == 2 and calls[1] == 'on:under' and calls[2] == 'on:outer')
owned = {}; equipped = {}; calls = {}
KCD2MP_W216Dress('outfit_test_npc', 'under,outer', '')
check('F5 created pieces participate in the same complete layered pass', owned.under and owned.outer and equipped.under and equipped.outer)
check('F5 unavailable copy does not masquerade as a successful repair', KCD2MP_W216Dress('absent_outfit_npc', 'under,outer', '') == false)
-- The queued audit must not become a second inventory writer after a death
-- or while the live-loot transaction owns this body.
owned = {}; equipped = {}; calls = {}; entity.dead = true
check('F5 queued old outfit never recreates looted corpse pieces', KCD2MP_W216Dress('outfit_test_npc', 'under,outer', '') == false and next(owned) == nil and #calls == 0)
entity.dead = false; KCD2MP.npcPuppets.outfit_test_npc.dead = true
check('F5 authoritative dead stream vetoes a copy still reading alive locally', KCD2MP_W216Dress('outfit_test_npc', 'under,outer', '') == false and next(owned) == nil)
KCD2MP.npcPuppets.outfit_test_npc.dead = false; entity.dead = nil
check('F5 unreadable death state never blindly creates items', KCD2MP_W216Dress('outfit_test_npc', 'under,outer', '') == false and next(owned) == nil)
entity.dead = false; KCD2MP.npcPuppets.outfit_test_npc = nil
check('F5 inactive unstreamed body is not dressed from an old target', KCD2MP_W216Dress('outfit_test_npc', 'under,outer', '') == false and next(owned) == nil)
KCD2MP.npcPuppets.outfit_test_npc = { lastPacketAt = NOW - 60, dead = false }
check('F5 released silent stream does not recreate the old target', KCD2MP_W216Dress('outfit_test_npc', 'under,outer', '') == false and next(owned) == nil)
KCD2MP.npcPuppets.outfit_test_npc.lastPacketAt = NOW
W217.L.sess.outfit_test_npc = { rev = 4 }
check('F5 full live-loot inventory stays the sole writer even when the copy is alive', KCD2MP_W216Dress('outfit_test_npc', 'under,outer', '') == false and next(owned) == nil and #calls == 0)
W217.L.sess.outfit_test_npc = nil
KCD2MP_W216Dress('outfit_test_npc', 'under,outer', '')
check('F5 active known-alive copy resumes full repair after inventory ownership ends', owned.under and owned.outer and equipped.under and equipped.outer)
OUT = table.concat(RESULTS, "\n")
