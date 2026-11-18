-- Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
-- GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
-- content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
-- WO-131 synthetic test, against the real kdcmp.lua under MoonSharp.
-- Harness (stubs, mkEntity) copied from Test-WO129Synthetic.lua.
--
--   (a) the copy guard's condition: joiner + host authority + shared + a fresh agent tick
--   (b) the sweep parks every unstreamed host-owned NPC (suspend + hide); never a
--       puppet, a corpse, a horse, an avatar, or anyone while the player talks
--   (c) the stream unparks (the puppet start shows the body)
--   (d) a released copy is parked, NEVER resumed (the field's free copy)
--   (e) the agent goes silent -> everything is given back (unhidden, resumed)
--   (f) a load -> every parked body re-parked on the next sweep
--   (g) looting / pickpocketing a host-owned NPC is blocked on a joiner, with the
--       game's own toast; horses, avatars, the host: straight through
--   (h) perception: never AI-ignorant on the host of a shared world
--   (i) stand-ins: a host-only NPC gets a body under its own name; never for a
--       horse (0x80) or a corpse; removed when its stream stops
--   (j) stuck poses: a ragdolled copy the host has standing is stood up; one the
--       host has down is left down
--   (k) the leash's dialogue hold needs the DialogTwin stand-in (a bark is not a hold)
--   (l) the host's far band: past the 60 m cull, a 2 s heartbeat out to 150 m
--
-- What this proves: the Lua halves. What it does NOT prove: a live
-- two-machine session (docs/WO-131-findings.md).

NOW = 0
os.clock = function() return NOW end
LOG = {}; TIMERS = {}; ENTS = {}; ERRS = {}; TOASTS = {}; SPHERE = {}
CMDS = {}; DRAWS = {}; SPAWNS = {}; CCMDS = {}; LOCKS = {}

local function mkstub()
    return setmetatable({}, { __index = function(_, k) return function(...) return nil end end })
end
System = mkstub()
System.LogAlways = function(s) LOG[#LOG + 1] = tostring(s) end
System.GetCVarValue = function() return "0" end
System.GetCVar = function() return "0" end
System.GetEntityByName = function(n) return ENTS[n] end
System.GetEntitiesInSphere = function() return SPHERE end
System.ExecuteCommand = function(s) CMDS[#CMDS + 1] = tostring(s) end
System.DrawText = function(x, y, text, size) DRAWS[#DRAWS + 1] = { x = x, y = y, text = tostring(text) } end
System.RemoveEntity = function(eid) for n, e in pairs(ENTS) do if e.id == eid then ENTS[n] = nil end end end
System.AddCCommand = function(name, body, help) CCMDS[name] = { body = tostring(body), help = tostring(help or "") } end
Script = mkstub()
Script.SetTimer = function(ms, f) TIMERS[#TIMERS + 1] = { ms = ms, f = f, at = NOW } end
Game = mkstub(); AI = mkstub(); Sound = mkstub(); Physics = mkstub(); Terrain = mkstub()
Game.AddSaveLock = function(name, desc) if LOCKS[name] then return false end LOCKS[name] = true; return true end
Game.RemoveSaveLock = function(name) local had = LOCKS[name] == true; LOCKS[name] = nil; return had end
UIAction = mkstub()
UIAction.CallFunction = function(panel, inst, fn, text) TOASTS[#TOASTS + 1] = tostring(text) end
WORLD_T = 1000
Calendar = { GetWorldTime = function() return WORLD_T end, SetWorldTime = function(t) WORLD_T = t end,
             GetWorldTimeRatio = function() return 15 end, SetWorldTimeRatio = function() end }
XGenAIModule = mkstub()
XGenAIModule.SpawnEntity = function(t) SPAWNS[#SPAWNS + 1] = t; ENTS[t.Name] = mkEntityLate(t.Name, t.Pos[1], t.Pos[2], t.Pos[3]) end

player = { id = 1, GetName = function(self) return "Dude" end,
           GetWorldPos = function() return { x = 0, y = 0, z = 0 } end,
           GetWorldAngles = function() return { x = 0, y = 0, z = 0 } end,
           actor = { GetHealth = function() return 100 end, IsDead = function() return false end } }

local rawpcall = pcall
pcall = function(f, ...)
    local r = { rawpcall(f, ...) }
    if not r[1] then ERRS[#ERRS + 1] = tostring(r[2]) end
    return table.unpack(r)
end

-- @@KDCMP@@

-- Part 2: scenarios.

local RESULTS = {}
local function check(name, ok, detail)
    RESULTS[#RESULTS + 1] = (ok and "PASS  " or "FAIL  ") .. name .. (detail and ("  [" .. tostring(detail) .. "]") or "")
end
local function logCount(pat)
    local n = 0
    for _, l in ipairs(LOG) do if string.find(l, pat, 1, true) then n = n + 1 end end
    return n
end
local function lastLog(pat)
    for i = #LOG, 1, -1 do if string.find(LOG[i], pat, 1, true) then return LOG[i] end end
    return nil
end
local function clearLog() LOG = {} end
NEXTID = 5000
local function mkEntity(name, x, y, z)
    NEXTID = NEXTID + 1
    local e = { class = "NPC", id = NEXTID, px = x or 0, py = y or 0, pz = z or 0, rz = 0, dead = false, hp = 100 }
    e.GetName = function(self) return name end
    e.GetWorldPos = function(self) return { x = self.px, y = self.py, z = self.pz } end
    e.GetWorldAngles = function(self) return { x = 0, y = 0, z = self.rz } end
    e.SetWorldPos = function(self, pos) self.px, self.py, self.pz = pos.x, pos.y, pos.z end
    e.SetWorldAngles = function(self, a) self.rz = a.z end
    e.StartAnimation = function() end
    e.actor = { IsDead = function() return e.dead end, IsUnconscious = function() return false end, GetHealth = function() return e.hp end }
    e.human = { IsWeaponDrawn = function() return false end, IsInDialog = function() return false end }
    e.hidden = false
    e.Hide = function(self, v) self.hidden = (v == 1 or v == true) end
    e.IsHidden = function(self) return self.hidden end
    e.standups = 0
    e.actor.StandUp = function() e.standups = e.standups + 1 end
    e.actor.IsUnconscious = function() return e.ko == true end
    e.soul = { DealDamage = function() end }
    return e
end
mkEntityLate = mkEntity


local function cmdCount(pat)
    local n = 0
    for _, c in ipairs(CMDS) do if string.find(c, pat, 1, true) then n = n + 1 end end
    return n
end
local function reset()
    KCD2MP.npcPuppets = {}; KCD2MP.npcTracked = {}; KCD2MP.dragging = {}; KCD2MP.dragWatch = {}
    KCD2MP._npcPaused = {}; KCD2MP._npcResumePending = {}; KCD2MP._npcEverPaused = {}
    KCD2MP._npcPauseExec = {}; KCD2MP._npcPauseEntity = {}
    KCD2MP.ghosts = {}; KCD2MP.horseGhosts = {}
    KCD2MP.w131.parked = {}; KCD2MP.w131.standins = {}; KCD2MP.w131.active = false; KCD2MP.w131.guard = true
    KCD2MP.w131.reassert = false; KCD2MP.w131.standinLastAt = -1e9
    KCD2MP.wo102.authorityHost = true; KCD2MP.hitSensorOn = false
    KCD2MP.w122.sharedWorld = true
    ENTS = {}; SPHERE = {}; ERRS = {}; CMDS = {}; SPAWNS = {}; TOASTS = {}
end

do -- (a) the guard's condition
    reset(); clearLog(); NOW = 100
    check("a: not active before any agent tick", KCD2MP_W131IsActive() == false)
    KCD2MP_W131Tick(true, true)
    check("a: joiner + shared + host authority + fresh tick = active", KCD2MP_W131IsActive() == true and KCD2MP.w131.active == true)
    check("a: said once", logCount("WO131-GUARD state=on") == 1)
    KCD2MP_W131Tick(true, false); check("a: separate worlds: off", KCD2MP.w131.active == false)
    KCD2MP_W131Tick(false, true); check("a: the authority (host) never guards", KCD2MP.w131.active == false)
    KCD2MP.hitSensorOn = true; KCD2MP_W131Tick(true, true)
    check("a: a machine holding NPC authority never guards", KCD2MP.w131.active == false)
    KCD2MP.hitSensorOn = false; KCD2MP.wo102.authorityHost = false; KCD2MP_W131Tick(true, true)
    check("a: host authority off: no guard", KCD2MP.w131.active == false)
    KCD2MP.wo102.authorityHost = true; KCD2MP_W131Tick(true, true)
    NOW = 111; check("a: an agent silent past 10 s is not active", KCD2MP_W131IsActive() == false)
    check("a: no Lua errors", #ERRS == 0, ERRS[1])
end

do -- (b) the sweep
    reset(); clearLog(); NOW = 200
    local free = mkEntity("ttkc_man_1", 10, 0, 0)
    local pup = mkEntity("ttkc_man_2", 20, 0, 0)
    local corpse = mkEntity("ttkc_man_3", 30, 0, 0); corpse.dead = true
    local horse = mkEntity("ttkc_horse_1", 40, 0, 0); horse.class = "Horse"
    local avatar = mkEntity("kcd2mp_1", 5, 0, 0)
    local twin = mkEntity("DialogTwin_ttkc_man_9", 6, 0, 0)
    local woman = mkEntity("ttkc_woman_1", 12, 0, 0); woman.class = "NPC_Female"
    for _, e in ipairs({ free, pup, corpse, horse, avatar, twin, woman }) do ENTS[e:GetName()] = e end
    SPHERE = { free, pup, corpse, horse, avatar, twin, woman }
    KCD2MP.npcPuppets["ttkc_man_2"] = { dead = false, ko = false }
    KCD2MP_W131Tick(true, true)
    check("b: a free host-owned NPC is parked (suspend + hide)", KCD2MP.w131.parked["ttkc_man_1"] ~= nil and free.hidden
        and cmdCount("wh_ai_PauseNPC ttkc_man_1") == 1)
    check("b: NPC_Female too", KCD2MP.w131.parked["ttkc_woman_1"] ~= nil and woman.hidden)
    check("b: a puppet is the stream's, never parked", KCD2MP.w131.parked["ttkc_man_2"] == nil and not pup.hidden)
    check("b: a corpse has no brain: left alone", KCD2MP.w131.parked["ttkc_man_3"] == nil and not corpse.hidden)
    check("b: a horse is left alone", KCD2MP.w131.parked["ttkc_horse_1"] == nil and not horse.hidden)
    check("b: our avatar and the dialogue stand-ins are left alone", not avatar.hidden and not twin.hidden)
    CMDS = {}
    KCD2MP_W131Tick(true, true)
    check("b: a parked body still hidden is not re-paused every sweep", cmdCount("wh_ai_PauseNPC ttkc_man_1") == 0)
    free.hidden = false; CMDS = {}
    KCD2MP_W131Tick(true, true)
    check("b: one found shown again (the engine reset it) is re-hidden AND re-paused", free.hidden and cmdCount("wh_ai_PauseNPC ttkc_man_1") == 1)
    NOW = NOW + 11; CMDS = {}
    KCD2MP_W131Tick(true, true)
    check("b: every 10 s every parked body in range is re-paused anyway", cmdCount("wh_ai_PauseNPC ttkc_man_1") == 1)
    local late = mkEntity("ttkc_man_4", 14, 0, 0); ENTS["ttkc_man_4"] = late; SPHERE[#SPHERE + 1] = late
    player.human = { IsInDialog = function() return true end }
    KCD2MP_W131Tick(true, true)
    check("b: nothing parked while the player is in a conversation", KCD2MP.w131.parked["ttkc_man_4"] == nil)
    player.human = nil
    KCD2MP_W131Tick(true, true)
    check("b: parked once the conversation ended", KCD2MP.w131.parked["ttkc_man_4"] ~= nil)
    check("b: no Lua errors", #ERRS == 0, ERRS[1])
end

do -- (c) the stream unparks; (d) a release parks, never resumes
    reset(); clearLog(); NOW = 300
    local n = mkEntity("ttkc_man_5", 10, 0, 0); ENTS["ttkc_man_5"] = n; SPHERE = { n }
    KCD2MP_W131Tick(true, true)
    check("c: parked first (not streamed yet)", n.hidden == true)
    KCD2MP._npcPauseEntity.ttkc_man_5 = { id = "old-body", soul = "old-soul" }
    KCD2MP_ApplyNpcState("ttkc_man_5", 50, 0, 0, 0, 100, 0, 0, 1, 1000)
    check("c: the puppet start shows the body", KCD2MP.npcPuppets["ttkc_man_5"] ~= nil and n.hidden == false
        and KCD2MP.w131.parked["ttkc_man_5"] == nil)
    check("c: and the lever pauses it", KCD2MP._npcPaused["ttkc_man_5"] ~= nil)
    check("c: a soul without GetId cannot inherit the old body's cleanup lease", KCD2MP._npcPauseEntity.ttkc_man_5 == nil)
    KCD2MP.npcPuppets["ttkc_man_5"] = nil
    CMDS = {}
    local parked = KCD2MP_W131ParkReleased("ttkc_man_5", "silence")
    check("d: a released copy is parked", parked == true and n.hidden == true and KCD2MP.w131.parked["ttkc_man_5"] ~= nil)
    check("d: never resumed (no wh_ai_ResumeNPC), and out of the lever's tables", cmdCount("wh_ai_ResumeNPC") == 0
        and KCD2MP._npcPaused["ttkc_man_5"] == nil and KCD2MP._npcResumePending["ttkc_man_5"] == nil)
    check("d: no Lua errors", #ERRS == 0, ERRS[1])
end

do -- (e) the agent goes silent; (f) a load
    reset(); clearLog(); NOW = 400
    local a = mkEntity("ttkc_man_6", 10, 0, 0); ENTS["ttkc_man_6"] = a; SPHERE = { a }
    KCD2MP_W131Tick(true, true)
    check("e: parked", a.hidden)
    NOW = 405; KCD2MP_W131Backstop()
    check("e: a fresh tick keeps it parked", a.hidden and KCD2MP.w131.active)
    NOW = 412; CMDS = {}; KCD2MP_W131Backstop()
    check("e: 12 s without the agent -> given back: unhidden and resumed", not a.hidden and cmdCount("wh_ai_ResumeNPC ttkc_man_6") == 1
        and KCD2MP.w131.active == false)
    check("e: said", logCount("WO131-GUARD state=off why=agent-silent") == 1)
    NOW = 420; KCD2MP_W131Tick(true, true)
    check("e: the agent back: parked again", a.hidden)
    a.hidden = false; CMDS = {}
    KCD2MP_OnChainDeadRestart("test")
    KCD2MP_W131Tick(true, true)
    check("f: a load re-parks (pause + hide again)", a.hidden and cmdCount("wh_ai_PauseNPC ttkc_man_6") == 1)
    check("f: said", logCount("WO131-GUARD reassert") == 1)
    check("e/f: no Lua errors", #ERRS == 0, ERRS[1])
end

do -- (g) looting and pickpocketing
    reset(); clearLog(); NOW = 500
    local calls = {}
    BasicAIActions = { OnLoot = function(self, user) calls[#calls + 1] = "loot:" .. self:GetName() end,
                       OnPickpocketing = function(self, user) calls[#calls + 1] = "pick:" .. self:GetName() end }
    KCD2MP_W131Tick(true, true)
    check("g: both wrapped", BasicAIActions.OnLoot == KCD2MP.w131.lootWrap and BasicAIActions.OnPickpocketing == KCD2MP.w131.pickWrap)
    local body = mkEntity("ttkc_man_7", 3, 0, 0); body.dead = true; ENTS["ttkc_man_7"] = body
    BasicAIActions.OnLoot(body, player)
    check("g: a host-owned body is not looted on the joiner", #calls == 0)
    local toastOk = false
    for _, t in ipairs(TOASTS) do if t == "Only the host can loot bodies in co-op for now." then toastOk = true end end
    check("g: the game's own toast says why", toastOk)
    BasicAIActions.OnPickpocketing(mkEntity("ttkc_man_8", 3, 0, 0), player)
    check("g: pickpocketing a host-owned NPC is blocked too", #calls == 0)
    local h = mkEntity("ttkc_horse_2", 3, 0, 0); h.class = "Horse"
    BasicAIActions.OnLoot(h, player)
    check("g: a horse's saddlebags: straight through", calls[1] == "loot:ttkc_horse_2")
    BasicAIActions.OnLoot(mkEntity("DialogTwin_ttkc_man_9", 3, 0, 0), player)
    check("g: a name the guard does not own (a dialogue stand-in): straight through", calls[2] == "loot:DialogTwin_ttkc_man_9")
    -- WO-139: our avatar is the other player -- never looted, on either machine
    BasicAIActions.OnLoot(mkEntity("kcd2mp_1", 3, 0, 0), player)
    local robLine = false
    for _, t in ipairs(TOASTS) do if t == "You can't steal from each other in co-op." then robLine = true end end
    check("g: our avatar: refused since WO-139, with the co-op line", calls[3] == nil and robLine)
    KCD2MP_W131Tick(false, true)
    BasicAIActions.OnLoot(body, player)
    check("g: the host loots as ever", calls[3] == "loot:ttkc_man_7")
    BasicAIActions.OnLoot = function() calls[#calls + 1] = "fresh" end
    KCD2MP_W131Tick(true, true)
    check("g: re-wrapped after the game replaced the function", BasicAIActions.OnLoot == KCD2MP.w131.lootWrap)
    BasicAIActions = nil
    check("g: no Lua errors", #ERRS == 0, ERRS[1])
end

do -- (h) perception
    reset(); clearLog()
    KCD2MP.ghostsIgnorant = true
    KCD2MP.hitSensorOn = true; KCD2MP.w122.sharedWorld = true
    check("h: host of a shared world: never ignorant", KCD2MP_GhostIgnorantWanted() == false)
    KCD2MP.w122.sharedWorld = false
    check("h: separate worlds: the old rule (ignorant)", KCD2MP_GhostIgnorantWanted() == true)
    KCD2MP.w122.sharedWorld = true; KCD2MP.w131.perceive = false
    check("h: mp_avatar_perceive off: the old rule", KCD2MP_GhostIgnorantWanted() == true)
    KCD2MP.w131.perceive = true
    local set = {}
    AI.SetIgnorant = function(id, v) set[#set + 1] = v end
    KCD2MP.ghosts = { ["1"] = { entity = { id = 77 } } }
    KCD2MP_ReassertGhostIgnorance()
    check("h: the 2.5 s re-assert writes 0 (perceivable) on the host of a shared world", set[1] == 0, tostring(set[1]))
    KCD2MP.w122.sharedWorld = false; set = {}
    KCD2MP_ReassertGhostIgnorance()
    check("h: and 1 again in separate worlds", set[1] == 1, tostring(set[1]))
    AI.SetIgnorant = nil; KCD2MP.ghosts = {}
    check("h: no Lua errors", #ERRS == 0, ERRS[1])
end

do -- (i) stand-ins
    reset(); clearLog(); NOW = 600
    KCD2MP_W131Tick(true, true)
    -- WO-136: a stand-in first asks the agent for the host's own soul (w136_soul);
    -- these synthetic names are not in the game's tables, so the answer is
    -- "unknown" and WO-131's guess stands in, exactly as before.
    for _, n in ipairs({ "prepadeniNaCeste_bandit_9", "traveller_horse_3", "roadside_corpse_1", "traveller_man_2", "prepadeniNaCeste_bandit_7" }) do
        KCD2MP_W136SoulFor(n, "", "")
    end
    KCD2MP_ApplyNpcState("prepadeniNaCeste_bandit_9", 40, 5, 0, 0, 100, 4, 0, 1, 1000)
    check("i: a host-only NPC gets a stand-in under its own name", SPAWNS[1] ~= nil and SPAWNS[1].Name == "prepadeniNaCeste_bandit_9"
        and ENTS["prepadeniNaCeste_bandit_9"] ~= nil)
    check("i: a road bandit wears a bandit soul", SPAWNS[1] ~= nil and SPAWNS[1].SharedSoulGuid == "29f8bb4d-87f1-465e-9ba8-679f889d4de6")
    check("i: and it is an ordinary puppet", KCD2MP.npcPuppets["prepadeniNaCeste_bandit_9"] ~= nil)
    NOW = 601
    KCD2MP_ApplyNpcState("traveller_horse_3", 40, 5, 0, 0, 100, 128, 0, 1, 1000)
    check("i: never for a horse (0x80)", #SPAWNS == 1 and ENTS["traveller_horse_3"] == nil)
    NOW = 602
    KCD2MP_ApplyNpcState("roadside_corpse_1", 40, 5, 0, 0, 0, 1, 0, 1, 1000)
    -- WO-137 Phase 2b replaced "never for a body already dead on the host": the stand-in is
    -- created dead (spawned hidden, killed by the owner-death apply, shown once dead here).
    check("i: a body already dead on the host: a stand-in, hidden until it is dead here (WO-137)",
        #SPAWNS == 2 and SPAWNS[2].Name == "roadside_corpse_1" and ENTS["roadside_corpse_1"] ~= nil and ENTS["roadside_corpse_1"].hidden == true)
    NOW = 603
    KCD2MP_ApplyNpcState("traveller_man_2", 40, 5, 0, 0, 100, 0, 0, 1, 1000)
    check("i: anyone else: a commoner from the avatar roster", #SPAWNS == 3 and SPAWNS[3].Name == "traveller_man_2")
    KCD2MP.npcPuppets["prepadeniNaCeste_bandit_9"] = nil
    KCD2MP_W131ParkReleased("prepadeniNaCeste_bandit_9", "silence")
    check("i: removed (not parked) when its stream stops", ENTS["prepadeniNaCeste_bandit_9"] == nil
        and KCD2MP.w131.standins["prepadeniNaCeste_bandit_9"] == nil and KCD2MP.w131.parked["prepadeniNaCeste_bandit_9"] == nil)
    KCD2MP_W131UnparkAll("test")
    check("i: all of them go when the guard goes off", ENTS["traveller_man_2"] == nil)
    KCD2MP_W131Tick(false, true)
    NOW = 610
    KCD2MP_ApplyNpcState("prepadeniNaCeste_bandit_7", 40, 5, 0, 0, 100, 0, 0, 1, 1000)
    check("i: the host (or solo) never makes one", ENTS["prepadeniNaCeste_bandit_7"] == nil)
    check("i: no Lua errors", #ERRS == 0, ERRS[1])
end

do -- (i2) WO-153 4: one body per NPC -- no stand-in over a body that already answers to the name
    reset(); clearLog(); NOW = 800
    KCD2MP_W131Tick(true, true)
    KCD2MP_W136SoulFor("prepadeniNaCeste_bandit_5", "", "")
    local real = mkEntity("prepadeniNaCeste_bandit_5", 40, 5, 0); ENTS["prepadeniNaCeste_bandit_5"] = real
    local n0 = #SPAWNS
    local got = KCD2MP_W131StandIn("prepadeniNaCeste_bandit_5", 41, 6, 0, 0, 1)   -- dead on the host
    check("i2: a body already answers to the name: the stand-in is refused, the body is returned", got == real and #SPAWNS == n0, tostring(#SPAWNS - n0))
    check("i2: ... it is not registered as a stand-in (so nothing removes it by name later)", KCD2MP.w131.standins["prepadeniNaCeste_bandit_5"] == nil)
    check("i2: ... and said", logCount("WO131-STANDIN refused npc=prepadeniNaCeste_bandit_5") == 1)
    check("i2: no Lua errors", #ERRS == 0, ERRS[1])
end

do -- (j) stuck poses
    reset(); clearLog(); NOW = 700
    local b = mkEntity("ttkc_man_10", 10, 0, 0); ENTS["ttkc_man_10"] = b
    KCD2MP.npcPuppets["ttkc_man_10"] = { dead = false, ko = false, nativeSent = true }
    KCD2MP_NpcNativeAck("ttkc_man_10", 0, "not-living")
    check("j: a ragdolled copy the host has standing is stood up", b.standups == 1)
    check("j: and the bind retried soon", (KCD2MP.npcPuppets["ttkc_man_10"].nativeRetryAt or 99) - NOW <= 2.01)
    KCD2MP_NpcNativeAck("ttkc_man_10", 0, "not-living")
    check("j: not more than once per 3 s", b.standups == 1)
    NOW = 704; KCD2MP.npcPuppets["ttkc_man_10"].ko = true
    KCD2MP_NpcNativeAck("ttkc_man_10", 0, "not-living")
    check("j: the host has it knocked out: left down", b.standups == 1)
    KCD2MP.npcPuppets["ttkc_man_10"].ko = false; b.dead = true
    KCD2MP_NpcNativeAck("ttkc_man_10", 0, "not-living")
    check("j: a local corpse is never stood up", b.standups == 1)
    b.dead = false; b.ko = true; NOW = 710; SPHERE = { b }
    KCD2MP_W131Tick(true, true)
    check("j: a local knockout the host does not have: stood up by the sweep", b.standups == 2)
    check("j: no Lua errors", #ERRS == 0, ERRS[1])
end

do -- (k) the leash's dialogue hold
    reset()
    player.human = { IsInDialog = function() return true end, IsMounted = function() return true end }
    local d = KCD2MP_Wo114BusyRead()
    check("k: IsInDialog without a conversation stand-in (a bark near a rider) is not a hold", d == 0)
    ENTS["DialogTwin_Dude"] = mkEntity("DialogTwin_Dude", 0, 0, 0)
    d = KCD2MP_Wo114BusyRead()
    check("k: with the player's DialogTwin: a real conversation holds", d == 1)
    player.human.IsInDialog = function() return false end
    d = KCD2MP_Wo114BusyRead()
    check("k: a lingering twin alone is not a hold", d == 0)
    player.human = nil
    check("k: no Lua errors", #ERRS == 0, ERRS[1])
end

do -- (l) the host's far band
    reset(); clearLog()
    KCD2MP.hitSensorOn = true; KCD2MP.w122.sharedWorld = true
    KCD2MP.wo102.npcScanNative = false
    KCD2MP.npcSync.enabled = true; KCD2MP.npcSyncRunning = true
    KCD2MP.wo1025.together = false; KCD2MP._togetherWantSince = nil; KCD2MP._colocatePendingRelease = {}
    local mid = mkEntity("l_mid", 100, 0, 0); ENTS["l_mid"] = mid
    local far = mkEntity("l_far", 200, 0, 0); ENTS["l_far"] = far
    local near = mkEntity("l_near", 30, 0, 0); ENTS["l_near"] = near
    SPHERE = { mid, far, near }
    NOW = 1000; KCD2MP._npcScanAt = 0; KCD2MP_NpcSyncTick()
    check("l: inside the cull radius: streamed", logCount("npc_state l_near") >= 1)
    check("l: 100 m (past the 60 m cull, inside the 150 m band): on the heartbeat", logCount("npc_state l_mid") == 1)
    check("l: 200 m (owned, outside the band): not streamed", KCD2MP.npcTracked["l_far"] ~= nil and logCount("npc_state l_far") == 0)
    mid.px = 101; NOW = 1000.5; KCD2MP_NpcSyncTick()
    check("l: a far-band NPC that moves is not sent at full rate", logCount("npc_state l_mid") == 1)
    NOW = 1002.5; KCD2MP_NpcSyncTick()
    check("l: ...but on the next 2 s heartbeat", logCount("npc_state l_mid") == 2)
    check("l: no re-entry log spam for far-band sends", logCount("WO1025-CULL re-entry l_mid") == 0)
    KCD2MP.w122.sharedWorld = false; clearLog(); NOW = 1010; KCD2MP_NpcSyncTick()
    check("l: separate worlds: the 60 m cull as before", logCount("npc_state l_mid") == 0)
    KCD2MP.npcSyncRunning = false; KCD2MP.hitSensorOn = false
    check("l: no Lua errors", #ERRS == 0, ERRS[1])
end

do -- (m) WO-153 2: a domestic dog the host never streams is suspended (not hidden) on the joiner, and given back
    reset(); clearLog(); NOW = 3000
    local dog = mkEntity("korenarka_dog", 5, 0, 0); dog.class = "Dog"; ENTS["korenarka_dog"] = dog; SPHERE = { dog }
    local hen = mkEntity("some_hen", 6, 0, 0); hen.class = "Hen"; ENTS["some_hen"] = hen; SPHERE = { dog, hen }
    KCD2MP.w131.pausedAnimals = {}
    KCD2MP_W131Tick(true, true)
    check("m: the dog is paused", cmdCount("wh_ai_PauseNPC korenarka_dog") == 1, cmdCount("wh_ai_PauseNPC korenarka_dog"))
    check("m: ...and NOT hidden (nothing of the host's replaces it)", dog.hidden == false)
    check("m: a class the list does not name is left alone", cmdCount("wh_ai_PauseNPC some_hen") == 0)
    check("m: said once", logCount("WO131-GUARD pause npc=korenarka_dog class=Dog") == 1)
    KCD2MP_W131Tick(true, true)
    check("m: not paused again on the next sweep (only on the 10 s re-pause)", cmdCount("wh_ai_PauseNPC korenarka_dog") == 1)
    NOW = 3011; KCD2MP_W131Tick(true, true)
    check("m: re-paused after the repause interval", cmdCount("wh_ai_PauseNPC korenarka_dog") == 2)
    KCD2MP.w131.pausedAnimals["korenarka_dog"] = nil; dog.dead = true; NOW = 3030; KCD2MP_W131Tick(true, true)
    check("m: a dead dog has no brain to stop", cmdCount("wh_ai_PauseNPC korenarka_dog") == 2)
    dog.dead = false; NOW = 3031; KCD2MP_W131Tick(true, true)
    local before = cmdCount("wh_ai_ResumeNPC korenarka_dog")
    KCD2MP_W131UnparkAll("mp_npc_guard off")
    check("m: given back when the guard goes off", cmdCount("wh_ai_ResumeNPC korenarka_dog") == before + 1 and next(KCD2MP.w131.pausedAnimals) == nil)
    check("m: no Lua errors", #ERRS == 0, ERRS[1])
end

do -- (n) WO-158 S2: Mutt, the player's own dog, is never paused; any other dog still is
    reset(); clearLog(); NOW = 4000
    local other = mkEntity("korenarka_dog", 5, 0, 0); other.class = "Dog"; ENTS["korenarka_dog"] = other
    local mutt = mkEntity("tvez_vorech", 6, 0, 0); mutt.class = "Dog"; ENTS["tvez_vorech"] = mutt
    local mutt2 = mkEntity("player_dogCompanion_vorech", 7, 0, 0); mutt2.class = "Dog"; ENTS["player_dogCompanion_vorech"] = mutt2
    local mutt3 = mkEntity("player_dogCompanion_other", 8, 0, 0); mutt3.class = "Dog"; ENTS["player_dogCompanion_other"] = mutt3
    SPHERE = { other, mutt, mutt2, mutt3 }
    KCD2MP.w131.pausedAnimals = {}
    KCD2MP_W131Tick(true, true)
    check("n: another dog is still paused", cmdCount("wh_ai_PauseNPC korenarka_dog") == 1, cmdCount("wh_ai_PauseNPC korenarka_dog"))
    check("n: Mutt (the soul's name) is never paused", cmdCount("wh_ai_PauseNPC tvez_vorech") == 0 and KCD2MP.w131.pausedAnimals["tvez_vorech"] == nil)
    check("n: Mutt (the companion entity's name) is never paused", cmdCount("wh_ai_PauseNPC player_dogCompanion_vorech") == 0)
    check("n: any player_dogCompanion* dog is the player's own", cmdCount("wh_ai_PauseNPC player_dogCompanion_other") == 0)
    NOW = 4011; KCD2MP_W131Tick(true, true)
    check("n: nor on the re-pause", cmdCount("wh_ai_PauseNPC tvez_vorech") == 0 and cmdCount("wh_ai_PauseNPC korenarka_dog") == 2)
    check("n: the predicate: Mutt yes, a stranger's dog no", KCD2MP_W131IsPlayersDog("tvez_vorech") == true
        and KCD2MP_W131IsPlayersDog("korenarka_dog") == false and KCD2MP_W131IsPlayersDog(nil) == false)
    check("n: no Lua errors", #ERRS == 0, ERRS[1])
end


do -- P5: actual parking/reassert/release and late combat-failure methods
    reset(); NOW = 5000; KCD2MP_T3CorpseWorldReset("test")
    local byId, trace, events = {}, {}, {}
    System.GetEntity = function(id) return byId[id] end
    local originalRemove, originalEmit = System.RemoveEntity, KCD2MP_EmitEvent
    System.RemoveEntity = function(id) trace[#trace + 1] = "remove:" .. tostring(id); byId[id] = nil; originalRemove(id) end
    KCD2MP_EmitEvent = function(kind, arg) events[#events + 1] = { kind, arg }; return originalEmit(kind, arg) end
    local function body(name)
        local e = mkEntity(name, 5, 0, 0)
        ENTS[name], byId[e.id] = e, e
        local hide = e.Hide
        e.Hide = function(self, v) trace[#trace + 1] = "hide:" .. tostring(v); hide(self, v) end
        return e
    end
    local function attempts(kind)
        local n = 0; for _, t in ipairs(trace) do if t:find(kind, 1, true) == 1 then n = n + 1 end end; return n
    end
    local e = body("p5_parked"); SPHERE = {e}
    KCD2MP_W131Tick(true, true)
    check("p5 live body really parks before the transition", e.hidden and KCD2MP.w131.parked.p5_parked ~= nil)
    e.dead, trace, CMDS = true, {}, {}; NOW = NOW + 11; KCD2MP.w131.reassert = true
    KCD2MP_W131Tick(true, true)
    check("p5 actual death overrides parked repause/reassert with no Hide1", not e.hidden and attempts("hide:1") == 0 and cmdCount("wh_ai_PauseNPC p5_parked") == 0)
    check("p5 actual death retires the parking record", KCD2MP.w131.parked.p5_parked == nil)
    if W217.E then W217.E.listed.p5_parked = NOW - 61 end
    trace, CMDS = {}, {}; KCD2MP_W131ParkReleased("p5_parked", "silence")
    check("p5 stream release after corpse-list TTL keeps actual corpse", not e.hidden and attempts("hide:1") == 0 and attempts("remove:") == 0 and #CMDS == 0)
    trace = {}; KCD2MP_W215Ghost("p5_parked"); KCD2MP_W215Dormant("p5_parked")
    check("p5 late Ghost and Dormant cannot hide or mark the corpse", attempts("hide:1") == 0 and not W215.ghosts.p5_parked and not W215.dormantUntil.p5_parked)

    local stand = body("p5_stand"); stand.kcdmpStandIn = "exact-owned-token"; stand.dead = true
    KCD2MP.w131.standins.p5_stand = { token = stand.kcdmpStandIn, id = stand.id, cls = "NPC", at = NOW }
    trace = {}; KCD2MP_W131RemoveStandIn("p5_stand", "stream-stopped")
    check("p5 confirmed dead owned stand-in is never removed", byId[stand.id] == stand and attempts("remove:") == 0)
    local other = body("p5_stand")
    System.GetEntitiesByClass = function() return { stand, other } end
    KCD2MP.w131.standinSweepAt = -1e9; trace = {}; KCD2MP_W131StandInSweep()
    check("p5 returning world NPC retires binding but keeps physical stand-in corpse", KCD2MP.w131.standins.p5_stand == nil and byId[stand.id] == stand and attempts("remove:") == 0)
    check("p5 returning alive same-name NPC never inherits corpse fact", KCD2MP_T3CorpseProbe("p5_stand", other) == "alive")

    local pending = body("p5_pending"); pending.kcdmpStandIn = "pending-token"
    local si = { token = pending.kcdmpStandIn, id = pending.id, at = NOW, cls = "NPC" }
    KCD2MP.w131.standins.p5_pending = si
    KCD2MP_W137DeadStandInSpawned("p5_pending", pending, si)
    trace = {}; KCD2MP_W131ParkReleased("p5_pending", "silence")
    check("p5 alive-pending stand-in remains hidden and retained", pending.hidden and byId[pending.id] == pending and attempts("remove:") == 0 and attempts("hide:0") == 0)
    check("p5 alive-pending is not an observed corpse", KCD2MP_T3CorpseProbe("p5_pending", pending) == "alive-pending")
    pending.dead = true; KCD2MP_W131ParkReleased("p5_pending", "death-applied")
    check("p5 successful actual death shows the pending body without a new death writer", not pending.hidden and attempts("remove:") == 0)

    local retiring = body("p5_replaced_pending"); retiring.kcdmpStandIn = "retire-pending"
    local retiringEntry = { token = retiring.kcdmpStandIn, id = retiring.id, at = NOW, cls = "NPC", deadPending = true }
    KCD2MP.w131.standins.p5_replaced_pending = retiringEntry; retiring.hidden = true
    local returned = body("p5_replaced_pending")
    System.GetEntitiesByClass = function() return { retiring, returned } end
    KCD2MP.w131.standinSweepAt = -1e9; trace = {}; KCD2MP_W131StandInSweep()
    check("p5 verified alive pending duplicate retires without ever being shown", byId[retiring.id] == nil and attempts("hide:0") == 0)
    local unresolved = body("p5_replaced_unknown"); unresolved.kcdmpStandIn = "retire-unknown"; unresolved.actor.IsDead = function() return nil end
    local unresolvedEntry = { token = unresolved.kcdmpStandIn, id = unresolved.id, at = NOW, cls = "NPC" }
    KCD2MP.w131.standins.p5_replaced_unknown = unresolvedEntry
    local newer = body("p5_replaced_unknown")
    System.GetEntitiesByClass = function() return { unresolved, newer } end
    KCD2MP.w131.standinSweepAt = -1e9; trace = {}; KCD2MP_W131StandInSweep()
    check("p5 unknown duplicate retains its exact retry owner instead of orphaning a hidden entity", KCD2MP.w131.standins.p5_replaced_unknown == unresolvedEntry and attempts("remove:") == 0)
    unresolved.dead = true; unresolved.actor.IsDead = function() return unresolved.dead end
    KCD2MP.w131.standinSweepAt = -1e9; trace = {}; KCD2MP_W131StandInSweep()
    check("p5 later confirmed duplicate corpse retires binding and keeps actual body", KCD2MP.w131.standins.p5_replaced_unknown == nil and byId[unresolved.id] == unresolved and attempts("remove:") == 0)

    local unknown = body("p5_unknown"); unknown.actor.IsDead = function() return nil end
    local beforeUnknown = KCD2MP.t3Corpses.stats.lua_corpse_unknown_kept
    trace, CMDS = {}, {}; SPHERE = {unknown}; KCD2MP_W131Tick(true, true); KCD2MP_W215Dormant("p5_unknown")
    check("p5 unknown life read neither parks nor removes", attempts("hide:1") == 0 and attempts("remove:") == 0 and #CMDS == 0)
    check("p5 unknown-kept counts destructive attempts only", KCD2MP.t3Corpses.stats.lua_corpse_unknown_kept >= beforeUnknown + 2)
    local stable = KCD2MP.t3Corpses.stats.lua_corpse_unknown_kept
    NOW = NOW + 1; KCD2MP_T3CorpseStatsTick()
    check("p5 stats poll does not inflate unknown-kept", KCD2MP.t3Corpses.stats.lua_corpse_unknown_kept == stable)

    local beforeError = #ERRS
    unknown.actor.IsDead = function() error("p5-expected-life-read-failure") end
    trace, CMDS = {}, {}; KCD2MP_W215Ghost("p5_unknown")
    check("p5 throwing life read is unknown and has no destructive call", attempts("hide:1") == 0 and attempts("remove:") == 0 and #CMDS == 0)
    check("p5 fixture observed the caught engine error", #ERRS == beforeError + 1 and tostring(ERRS[#ERRS]):find("p5-expected-life-read-failure", 1, true) ~= nil)
    if #ERRS == beforeError + 1 and tostring(ERRS[#ERRS]):find("p5-expected-life-read-failure", 1, true) then table.remove(ERRS) end

    local old = body("p5_reuse"); old.dead = true; KCD2MP_T3CorpseProbe("p5_reuse", old)
    local replacement = body("p5_reuse"); byId[replacement.id] = nil; replacement.id = old.id; byId[old.id] = replacement
    check("p5 same name and reused ID with a new table is a living replacement", KCD2MP_T3CorpseProbe("p5_reuse", replacement) == "alive")
    local soul = body("p5_soul"); soul.dead = true; local sid = "one"; soul.soul.GetId = function() return sid end
    KCD2MP_T3CorpseProbe("p5_soul", soul); sid = "two"; soul.dead = false
    check("p5 changed soul token retires the old death", KCD2MP_T3CorpseProbe("p5_soul", soul) == "alive")
    local tagged = body("p5_token"); tagged.dead = true; tagged.kcdmpStandIn = "old-life"
    KCD2MP_T3CorpseProbe("p5_token", tagged); tagged.kcdmpStandIn = "new-life"; tagged.actor.IsDead = function() return nil end
    check("p5 changed stand-in token cannot inherit a death through an unknown read", KCD2MP_T3CorpseProbe("p5_token", tagged) == "unknown" and KCD2MP.t3Corpses.rows[tagged] == nil)
    e.dead = false; check("p5 same instance actually alive again retires its corpse", KCD2MP_T3CorpseProbe("p5_parked", e) == "alive")

    KCD2MP_T3CorpseWorldReset("gauge"); local c = body("p5_visible"); c.dead = true
    ENTS.p5_alias = c; KCD2MP_T3CorpseProbe("p5_visible", c); KCD2MP_T3CorpseProbe("p5_alias", c)
    local d = body("p5_visible"); d.dead = true; KCD2MP_T3CorpseProbe("p5_visible", d)
    local uncertain = body("p5_no_visibility"); uncertain.dead = true; uncertain.IsHidden = function() return nil end
    KCD2MP_T3CorpseProbe("p5_no_visibility", uncertain)
    local scans = 0; System.GetEntitiesInSphere = function() scans = scans + 1; return SPHERE end
    System.GetEntitiesByClass = function() scans = scans + 1; return {} end
    events = {}; KCD2MP.t3Corpses.reportedAt = NOW; NOW = NOW + 61; KCD2MP_T3CorpseStatsTick()
    local stats = nil; for _, event in ipairs(events) do if event[1] == "t3_corpse_stats" then stats = event[2] end end
    check("p5 exact three unsigned integer fields are emitted after60s", stats and stats:match("^lua_corpse_visible=%d+ lua_corpse_hidden_blocked=%d+ lua_corpse_unknown_kept=%d+$") ~= nil)
    check("p5 gauge dedupes one body aliases and counts two genuine same-name bodies", stats and stats:match("lua_corpse_visible=2 ") ~= nil, stats)
    check("p5 gauge never enumerates the world", scans == 0)
    local world = KCD2MP.t3Corpses.world
    KCD2MP_W134LootLifeReset()
    check("p5 Henry-life reset does not retire NPC corpse world", KCD2MP.t3Corpses.world == world)
    KCD2MP_W136Hold(true, 240, "test-load")
    check("p5 genuine load retires old corpse identities", KCD2MP.t3Corpses.world > world and next(KCD2MP.t3Corpses.rows) == nil)
    local version = KCD2MP.t3Corpses.world; KCD2MP_W136Hold(true, 240, "keepalive")
    check("p5 repeated load keepalive is idempotent for corpse reset", KCD2MP.t3Corpses.world == version)
    local fatal = 0; for _, event in ipairs(events) do if event[1] == "npc_death" then fatal = fatal + 1 end end
    check("p5 all visibility and retirement operations create no FATAL/death event", fatal == 0)
    check("p5 no Lua errors", #ERRS == 0, ERRS[1])
    System.RemoveEntity, KCD2MP_EmitEvent = originalRemove, originalEmit
end

local pass, fail = 0, 0
for _, r in ipairs(RESULTS) do if r:sub(1, 4) == "PASS" then pass = pass + 1 else fail = fail + 1 end end
OUT = table.concat(RESULTS, "\n") .. string.format("\n%d passed, %d failed", pass, fail)
