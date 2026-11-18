-- Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
-- GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
-- content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
-- WO-86 synthetic test for NPC death sync:
--   * the corpse-drag safeguard in KCD2MP_NpcPuppetTick (a body dead only
--     LOCALLY never follows a stream that says alive)
--   * the WO-38 body-follow that the safeguard must NOT break (stream says
--     dead, body follows a >0.5 m stream move as a one-shot placement)
--   * the death observer (mp_npc_death_observe via its three callers):
--     first-seen-dead is silent, a witnessed alive->dead announces exactly
--     once, alive-again clears the marks, remote/DLL-announced deaths are
--     not announced back
--   * KCD2MP_NpcRemoteDeath / KCD2MP_NpcDeathAnnounced / KCD2MP_SetNpcDeathSync
--   * the emitter's outbound dead bit and its Phase 1 log line
--
-- Driven by Test-WO86Synthetic.ps1 through the WO-77 MoonSharp driver: the
-- real kdcmp.lua is spliced in at the marker below with the engine stubbed and
-- os.clock replaced by a fake clock. No game, relay or agent involved.
--
-- What this proves: gating, bookkeeping, write suppression and event emission
-- against known sequences. What it does NOT prove: that actor:IsDead() flips
-- for a killed world NPC in the live game, that ApplyDeath through the DLL
-- produces a corpse on the receiving machine, or that a human sees the same
-- death on both screens. Those need a live session -- see
-- docs/WO-86-findings.md.
--
-- Part 1 (before the marker): engine stubs + a fake clock.

NOW = 0                                  -- the fake wall clock, seconds
os.clock = function() return NOW end
LOG = {}; TIMERS = {}; ENTS = {}; ERRS = {}; TOASTS = {}; CMDS = {}

local function mkstub()
    return setmetatable({}, { __index = function(_, k) return function(...) return nil end end })
end
System = mkstub()
System.LogAlways = function(s) LOG[#LOG + 1] = tostring(s) end
System.GetCVarValue = function() return "0" end
System.GetEntityByName = function(n) return ENTS[n] end
System.GetEntitiesInSphere = function() return {} end
System.ExecuteCommand = function(s) CMDS[#CMDS + 1] = tostring(s) end
System.RemoveEntity = function(eid)
    for n, e in pairs(ENTS) do if e.id == eid then ENTS[n] = nil end end
end
Script = mkstub()
Script.SetTimer = function(ms, f) TIMERS[#TIMERS + 1] = { ms = ms, f = f, at = NOW } end
Game = mkstub(); AI = mkstub(); Sound = mkstub(); Physics = mkstub(); Terrain = mkstub()
UIAction = mkstub()
UIAction.CallFunction = function(panel, inst, fn, text) TOASTS[#TOASTS + 1] = tostring(text) end
player = nil

local rawpcall = pcall
pcall = function(f, ...)
    local r = { rawpcall(f, ...) }
    if not r[1] then ERRS[#ERRS + 1] = tostring(r[2]) end
    return unpack(r)
end

-- @@KDCMP@@

-- WO-102: these scenarios pin the 0.23.2 CLAIM model, which now ships as `mp_authority_host_off`
-- (host authority is the shipped default since WO-102; Test-WO102Synthetic.lua covers that side).
KCD2MP.wo102.authorityHost = false

-- Part 2: scenarios.

local RESULTS = {}
local function check(name, ok, detail)
    RESULTS[#RESULTS + 1] = (ok and "PASS  " or "FAIL  ") .. name .. (detail and ("  [" .. detail .. "]") or "")
end
local function countLog(needle, fromLog)
    local n = 0
    for i = (fromLog or 0) + 1, #LOG do if LOG[i]:find(needle, 1, true) then n = n + 1 end end
    return n
end
local function lastLog(needle, fromLog)
    for i = #LOG, (fromLog or 0) + 1, -1 do if LOG[i]:find(needle, 1, true) then return LOG[i] end end
    return nil
end
local function noErrs(label)
    check(label .. ": no swallowed Lua errors", #ERRS == 0, ERRS[1])
end
-- Event lines are "[KCD2-MP-EVT] v1 <seq> <name> <arg>"; count by "<name> <argPrefix>".
local function countEvt(name, argPrefix, fromLog)
    return countLog("[KCD2-MP-EVT] v1 ", fromLog) > 0
        and (function()
            local n = 0
            for i = (fromLog or 0) + 1, #LOG do
                local l = LOG[i]
                if l:find("[KCD2-MP-EVT] v1 ", 1, true) and l:find(" " .. name .. " " .. (argPrefix or ""), 1, true) then n = n + 1 end
            end
            return n
        end)() or 0
end

local NEXTID = 9000
local function mkEntity(name, x, y, z)
    NEXTID = NEXTID + 1
    local e = { class = "NPC", id = NEXTID, px = x or 0, py = y or 0, pz = z or 0,
                rz = 0, writes = {}, anims = {}, dead = false, hp = 100 }
    e.GetName = function(self) return name end
    e.GetWorldPos = function(self) return { x = self.px, y = self.py, z = self.pz } end
    e.GetWorldAngles = function(self) return { x = 0, y = 0, z = self.rz } end
    e.SetWorldPos = function(self, p)
        self.px, self.py, self.pz = p.x, p.y, p.z
        self.writes[#self.writes + 1] = { x = p.x, y = p.y, z = p.z, at = NOW }
    end
    e.SetWorldAngles = function(self, a) self.rz = a.z end
    e.StartAnimation = function(self, layer, anim) self.anims[#self.anims + 1] = { anim = anim, at = NOW } end
    e.actor = {
        IsDead = function() return e.dead end,
        IsUnconscious = function() return false end,
        GetHealth = function() return e.hp end,
    }
    e.human = {
        IsWeaponDrawn = function() return false end,
        PlayAnim = function(self, frag, tag) return true end,
        DrawWeapon = function() return true end,
        HolsterWeapon = function() return true end,
    }
    return e
end

local function resetNpcSync()
    KCD2MP.npcPuppets = {}
    KCD2MP.npcPuppetRunning = false
    KCD2MP._npcPuppetAliveAt = nil
    KCD2MP._npcPuppetPumpAt = nil
    KCD2MP._npcPuppetRetired = {}
    KCD2MP._npcPuppetRetiredN = 0
    KCD2MP._chainLeakSeen = {}
    KCD2MP._chainLeakN = {}
    KCD2MP._chainProbe = {}
    KCD2MP.npcTracked = {}
    KCD2MP.dragging = KCD2MP.dragging or {}
    KCD2MP.dragWatch = KCD2MP.dragWatch or {}
    KCD2MP.npcDeathSync = true
    KCD2MP._npcDeathSeen = {}
    KCD2MP._npcDeathAnnounced = {}
    KCD2MP._npcDeathRemote = {}
    KCD2MP._npcDeathDiverged = {}
    KCD2MP._npcDeathSuppressedN = 0
    ERRS = {}; TOASTS = {}
end

-- Advance the fake clock and run one scheduled puppet tick.
local function tick()
    NOW = NOW + 0.05
    KCD2MP_NpcPuppetTick(nil, KCD2MP.npcPuppetGen)
end

-- ---------------------------------------------------------------------------
-- (a) The field report's corpse. Stream from a world where the NPC is alive
--     and walking; this world's copy dies. Pre-WO-86 the WO-38 body-follow
--     branch teleported the corpse along the walk. Now: no writes, one
--     DIVERGENCE line, one npc_death announcement (puppet reader).
-- ---------------------------------------------------------------------------
do
    resetNpcSync()
    local e = mkEntity("v1", 0, 0, 0); ENTS["v1"] = e
    local logMark = #LOG
    KCD2MP_ApplyNpcState("v1", 0, 0, 0, 0, 100, 0)
    for i = 1, 4 do
        KCD2MP_ApplyNpcState("v1", i * 0.4, 0, 0, 0, 100, 0)
        tick()
    end
    local writesAlive = #e.writes
    check("(a) an alive puppet is written by the stream (harness sanity)", writesAlive > 0, tostring(writesAlive))
    check("(a) nothing announced while it lives", countEvt("npc_death", "v1", logMark) == 0)

    -- Local kill. The stream keeps saying alive and keeps walking.
    e.dead = true; e.hp = 0
    for i = 5, 12 do
        KCD2MP_ApplyNpcState("v1", i * 0.8, 0, 0, 0, 100, 0)   -- flags 0: alive in the sender's world
        tick()
    end
    check("(a) SAFEGUARD: the dead body received no writes from the living stream",
        #e.writes == writesAlive, tostring(#e.writes - writesAlive) .. " writes after death")
    check("(a) the body stayed where it died", e.px < 2.0, string.format("x=%.2f", e.px))
    check("(a) DIVERGENCE logged exactly once for the body", countLog("NPC-DEATH DIVERGENCE v1", logMark) == 1,
        tostring(countLog("NPC-DEATH DIVERGENCE v1", logMark)))
    check("(a) suppressed-write counter advanced", (KCD2MP._npcDeathSuppressedN or 0) >= 8,
        tostring(KCD2MP._npcDeathSuppressedN))
    check("(a) the death was announced exactly once (npc_death event)", countEvt("npc_death", "v1", logMark) == 1,
        tostring(countEvt("npc_death", "v1", logMark)))
    check("(a) ...by the puppet reader, as a witnessed transition",
        (lastLog("NPC-DEATH v1 died here", logMark) or ""):find("by puppet", 1, true) ~= nil
        and (lastLog("NPC-DEATH v1 died here", logMark) or ""):find("witnessed alive->dead", 1, true) ~= nil,
        lastLog("NPC-DEATH v1 died here", logMark))
    check("(a) no body-follow happened", countLog("body follow v1", logMark) == 0)
    noErrs("(a)")
end

-- ---------------------------------------------------------------------------
-- (b) The case body-follow was built for (WO-38): the STREAM says the body is
--     down and moves it >0.5 m -- the authority dragging a corpse. This must
--     still place the body. And a stream-dead body on a copy that is still
--     alive here is frozen (no lerp writes), never announced by this world.
-- ---------------------------------------------------------------------------
do
    resetNpcSync()
    local e = mkEntity("v2", 0, 0, 0); ENTS["v2"] = e
    local logMark = #LOG
    KCD2MP_ApplyNpcState("v2", 0, 0, 0, 0, 100, 0)          -- alive first
    tick()
    KCD2MP_ApplyNpcState("v2", 0, 0, 0, 0, 0, 1)            -- now dead in the sender's world (bit 0)
    check("(b) inbound dead bit logged as a witnessed transition",
        (lastLog("NPC-DEATH v2 inbound stream says DEAD", logMark) or ""):find("witnessed alive->dead on this stream", 1, true) ~= nil,
        lastLog("NPC-DEATH v2 inbound stream says DEAD", logMark))
    local w0 = #e.writes
    tick(); tick()
    check("(b) a stream-dead body is frozen while this copy still lives (no lerp writes)", #e.writes == w0,
        tostring(#e.writes - w0))
    KCD2MP_ApplyNpcState("v2", 3, 0, 0, 0, 0, 1)            -- the authority drags it 3 m
    tick()
    check("(b) BODY-FOLLOW preserved: the stream-dead body was placed at the dragged position",
        #e.writes == w0 + 1 and math.abs(e.px - 3) < 0.01, string.format("writes=%d x=%.2f", #e.writes - w0, e.px))
    check("(b) ...and logged as body follow", countLog("body follow v2", logMark) == 1)
    check("(b) no DIVERGENCE for a body the stream itself says is dead", countLog("NPC-DEATH DIVERGENCE v2", logMark) == 0)
    check("(b) this world announced nothing (its copy never died here)", countEvt("npc_death", "v2", logMark) == 0)
    noErrs("(b)")
end

-- ---------------------------------------------------------------------------
-- (c) Observer semantics through the puppet reader.
-- ---------------------------------------------------------------------------
do
    resetNpcSync()
    local logMark = #LOG
    -- first sight already dead: silent
    local e3 = mkEntity("v3", 0, 0, 0); ENTS["v3"] = e3; e3.dead = true; e3.hp = 0
    KCD2MP_ApplyNpcState("v3", 0, 0, 0, 0, 100, 0)
    tick()
    check("(c) first-seen-dead is logged and NOT announced",
        countLog("NPC-DEATH v3 first seen already dead", logMark) == 1 and countEvt("npc_death", "v3", logMark) == 0)

    -- alive -> dead -> (more ticks) -> alive again -> dead again
    local e4 = mkEntity("v4", 0, 0, 0); ENTS["v4"] = e4
    KCD2MP_ApplyNpcState("v4", 0, 0, 0, 0, 100, 0)
    tick()
    e4.dead = true; e4.hp = 0
    tick(); tick(); tick()
    check("(c) a witnessed death is announced exactly once across repeated reads", countEvt("npc_death", "v4", logMark) == 1,
        tostring(countEvt("npc_death", "v4", logMark)))
    check("(c) the announcement carries name, hp and reader", countEvt("npc_death", "v4 0 puppet", logMark) == 1,
        lastLog("npc_death v4", logMark))
    e4.dead = false; e4.hp = 100
    tick()
    check("(c) alive-again is logged and clears the marks",
        countLog("NPC-DEATH v4 reads ALIVE again", logMark) == 1
        and KCD2MP._npcDeathAnnounced["v4"] == nil and KCD2MP._npcDeathRemote["v4"] == nil)
    e4.dead = true; e4.hp = 0
    tick()
    check("(c) a second death after a revive is announced again", countEvt("npc_death", "v4", logMark) == 2,
        tostring(countEvt("npc_death", "v4", logMark)))
    noErrs("(c)")
end

-- ---------------------------------------------------------------------------
-- (d) KCD2MP_NpcRemoteDeath: the agent's pre-apply call. Marks the death
--     remote, flags the puppet dead, and the observer stays quiet when the
--     local IsDead flips as a result.
-- ---------------------------------------------------------------------------
do
    resetNpcSync()
    local logMark = #LOG
    local e = mkEntity("v5", 0, 0, 0); ENTS["v5"] = e
    KCD2MP_ApplyNpcState("v5", 0, 0, 0, 0, 100, 0)
    tick()
    local r = KCD2MP_NpcRemoteDeath("v5", "0x31 FATAL")
    check("(d) returns true with the toggle on", r == true)
    check("(d) puppet entry flagged dead", KCD2MP.npcPuppets["v5"] and KCD2MP.npcPuppets["v5"].dead == true)
    check("(d) logged with local state before the apply",
        (lastLog("NPC-DEATH v5: peer says dead", logMark) or ""):find("IsDead=false hp=100", 1, true) ~= nil,
        lastLog("NPC-DEATH v5: peer says dead", logMark))
    local w0 = #e.writes
    KCD2MP_ApplyNpcState("v5", 4, 0, 0, 0, 100, 0)      -- a late alive-flagged packet from the same stream
    tick()
    check("(d) no writes once the puppet is flagged dead, even from an alive-flagged packet", #e.writes == w0,
        tostring(#e.writes - w0))
    e.dead = true; e.hp = 0                              -- the DLL's ApplyDeath landed
    tick()
    check("(d) the resulting local death is logged as remote and NOT announced back",
        countLog("applied from a peer via 0x31 FATAL", logMark) == 1 and countEvt("npc_death", "v5", logMark) == 0)
    -- an unloaded name must not error
    local r2 = KCD2MP_NpcRemoteDeath("nobody_here", "0x27 dead transition")
    check("(d) an unloaded name is logged as NOT LOADED without error", r2 == true
        and (lastLog("NPC-DEATH nobody_here: peer says dead", logMark) or ""):find("NOT LOADED", 1, true) ~= nil)
    noErrs("(d)")
end

-- ---------------------------------------------------------------------------
-- (e) KCD2MP_NpcDeathAnnounced: the DLL's FATAL hit already went out.
-- ---------------------------------------------------------------------------
do
    resetNpcSync()
    local logMark = #LOG
    local e = mkEntity("v6", 0, 0, 0); ENTS["v6"] = e
    KCD2MP_ApplyNpcState("v6", 0, 0, 0, 0, 100, 0)
    tick()
    KCD2MP_NpcDeathAnnounced("v6", "dll")
    KCD2MP_NpcDeathAnnounced("v6", "dll")
    check("(e) mark logged once", countLog("NPC-DEATH v6 announced by dll", logMark) == 1)
    e.dead = true; e.hp = 0
    tick()
    check("(e) the local death is attributed to the DLL announcement and not re-sent",
        countLog("already announced by dll", logMark) == 1 and countEvt("npc_death", "v6", logMark) == 0)
    noErrs("(e)")
end

-- ---------------------------------------------------------------------------
-- (f) The toggle. Off = pre-WO-86 verbatim: body-follow on either source, no
--     announce, KCD2MP_NpcRemoteDeath refuses. Bad arguments are refused.
-- ---------------------------------------------------------------------------
do
    resetNpcSync()
    local logMark = #LOG
    KCD2MP_SetNpcDeathSync("bogus")
    check("(f) a bad argument is refused and leaves the toggle on",
        KCD2MP.npcDeathSync == true and countLog("expected 'on' or 'off'", logMark) == 1)
    KCD2MP_SetNpcDeathSync("off")
    check("(f) off is mirrored to the agent as an npc_deathsync event", countEvt("npc_deathsync", "off", logMark) == 1)

    local e = mkEntity("v8", 0, 0, 0); ENTS["v8"] = e
    KCD2MP_ApplyNpcState("v8", 0, 0, 0, 0, 100, 0)
    tick()
    e.dead = true; e.hp = 0
    local w0 = #e.writes
    KCD2MP_ApplyNpcState("v8", 5, 0, 0, 0, 100, 0)      -- stream alive, moved 5 m
    tick()
    check("(f) OFF: the pre-WO-86 body-follow drags the locally-dead body along the living stream",
        #e.writes == w0 + 1 and math.abs(e.px - 5) < 0.01, string.format("writes=%d x=%.2f", #e.writes - w0, e.px))
    check("(f) OFF: the death is logged but NOT announced",
        countLog("mp_npc_deathsync off, NOT announced", logMark) == 1 and countEvt("npc_death", "v8", logMark) == 0)
    check("(f) OFF: no DIVERGENCE line", countLog("NPC-DEATH DIVERGENCE v8", logMark) == 0)
    check("(f) OFF: KCD2MP_NpcRemoteDeath refuses", KCD2MP_NpcRemoteDeath("v8", "0x31 FATAL") == false)

    KCD2MP_SetNpcDeathSync("on")
    check("(f) on is mirrored too", countEvt("npc_deathsync", "on", logMark) == 1 and KCD2MP.npcDeathSync == true)
    noErrs("(f)")
end

-- ---------------------------------------------------------------------------
-- (g) The emitter: outbound dead bit (bit 0) on npc_state, its Phase 1 log
--     line, and the emitter as a death reader.
-- ---------------------------------------------------------------------------
do
    resetNpcSync()
    local logMark = #LOG
    KCD2MP.hitSensorOn = true
    KCD2MP.npcSync.enabled = true
    KCD2MP.npcSyncRunning = true
    KCD2MP._npcScanAt = NOW                              -- skip the rescan (needs a player)
    local e = mkEntity("v7", 10, 10, 0); ENTS["v7"] = e
    KCD2MP.npcTracked["v7"] = {}
    KCD2MP_NpcSyncTick()
    local first = lastLog("npc_state v7", logMark) or ""
    check("(g) an alive tracked NPC emits npc_state with flags 0", first:match("(%d+)%s*$") == "0", first)

    e.dead = true; e.hp = 0
    NOW = NOW + 0.1
    KCD2MP_NpcSyncTick()
    local second = lastLog("npc_state v7", logMark) or ""
    check("(g) after the local death the next npc_state carries dead bit 0 set", second:match("(%d+)%s*$") == "1", second)
    check("(g) the outbound dead bit is logged once", countLog("NPC-DEATH v7 outbound dead bit set on npc_state", logMark) == 1)
    check("(g) the emitter reader announced the death once, as the emitter",
        countEvt("npc_death", "v7 0 emitter", logMark) == 1, tostring(countEvt("npc_death", "v7", logMark)))
    NOW = NOW + 0.1
    KCD2MP_NpcSyncTick()
    check("(g) a further tick announces nothing more", countEvt("npc_death", "v7", logMark) == 1)
    KCD2MP.hitSensorOn = false
    KCD2MP.npcSyncRunning = false
    noErrs("(g)")
end

-- Paket C/F2: the new role intent exists before the host save is loaded.
-- The mod command path blocks its suspend/resume cycle even when readiness failed,
-- and a locally-dead copy cannot announce FATAL through the Lua observer.
do
    resetNpcSync()
    local logMark, cmdMark = #LOG, #CMDS
    check("(h) missing native hook refuses host authority readiness", KCD2MP_HostAuthority(true, false) == false)
    KCD2MP_ModBrainCommand("wh_ai_PauseNPC copy_before_load")
    KCD2MP_ModBrainCommand("wh_ai_ResumeNPC copy_before_load")
    check("(h) no suspend/resume fallback before native readiness", #CMDS == cmdMark)
    check("(h) readiness failure is explicit", countLog("decision_gate=unavailable", logMark) > 0)
    check("(h) native readiness is recorded separately", KCD2MP_HostAuthority(true, true) == true and KCD2MP.hostAuthority.armed)
    System.ExecuteCommand("wh_ai_PauseNPC copy")
    System.ExecuteCommand("wh_ai_ResumeNPC copy")
    check("(h) ready joiner also never suspends/resumes copies", #CMDS == cmdMark)
    local detachCopy = mkEntity("copy_detach", 0, 0, 0); ENTS["copy_detach"] = detachCopy
    local detachState = { detachPending = { since = NOW, checkedAt = -1e9, reason = "dialog" } }
    KCD2MP_NpcDetach("copy_detach", detachState, "puppet-start")
    check("(h) copy start cannot issue local Stance/Unstance reset", #CMDS == cmdMark and detachState.detachPending == nil)
    detachState.detachPending = { since = NOW, checkedAt = -1e9, reason = "dialog" }
    check("(h) pending detach retry is retired without a reset", KCD2MP_NpcDetachRetry("copy_detach", detachState, detachCopy) == false
        and detachState.detachPending == nil and #CMDS == cmdMark)
    check("(h) a dialogue/horse/corpse exemption cannot wake its graph", KCD2MP_W216Exempt("copy", 120, "talk") == false)
    System.ExecuteCommand("sys_budget test")
    check("(h) unrelated engine commands still run", #CMDS == cmdMark + 1 and CMDS[#CMDS] == "sys_budget test")

    local e = mkEntity("copy_dead", 0, 0, 0); ENTS["copy_dead"] = e
    KCD2MP_ApplyNpcState("copy_dead", 0, 0, 0, 0, 100, 0)
    tick()
    e.dead = true; e.hp = 0
    tick(); tick()
    check("(h) locally dead copy never emits npc_death/FATAL", countEvt("npc_death", "copy_dead", logMark) == 0)
    check("(h) local copy death remains observable", countLog("host copy, not announced", logMark) == 1)
    KCD2MP_NpcRemoteDeath("copy_dead", "host")
    tick()
    check("(h) host death arriving later still produces no echo", countEvt("npc_death", "copy_dead", logMark) == 0)
    check("(h) host role reset succeeds", KCD2MP_HostAuthority(false, false) == true)
    System.ExecuteCommand("wh_ai_PauseNPC host_body")
    System.ExecuteCommand("wh_ai_ResumeNPC host_body")
    check("(h) host/local-world engine control is restored", #CMDS == cmdMark + 3)
    noErrs("(h)")
end

-- The ordinary joiner role protects the observer too if its role-intent Lua
-- statement has not arrived yet. CLAIM compatibility remains in (a)-(g).
do
    resetNpcSync()
    KCD2MP.wo102.authorityHost = true
    KCD2MP.w137.joiner, KCD2MP.w137.active = true, true
    local logMark = #LOG
    local e = mkEntity("late_intent", 0, 0, 0); ENTS["late_intent"] = e
    KCD2MP_ApplyNpcState("late_intent", 0, 0, 0, 0, 100, 0)
    tick()
    e.dead = true; e.hp = 0
    tick()
    check("(i) joiner session cannot announce before role intent", countEvt("npc_death", "late_intent", logMark) == 0)
    KCD2MP.w137.joiner, KCD2MP.w137.active = false, false
    KCD2MP.wo102.authorityHost = false
    noErrs("(i)")
end

-- P1: pending never runs the legacy suspend path, off restores it only after native confirmation.
do
    check("(p1) authority defaults on", KCD2MP.p1AuthorityEnabled == true)
    check("(p1) bad switch is refused", KCD2MP_P1SetHostAuthority("maybe") == false and KCD2MP.p1AuthorityEnabled)
    check("(p1) off is emitted to the agent", KCD2MP_P1SetHostAuthority("off") == true and not KCD2MP.p1AuthorityEnabled)
    local mark = #CMDS
    KCD2MP_HostAuthority(true, false, "pending", "10")
    local oldJoiner, oldActive = KCD2MP.w137.joiner, KCD2MP.w137.active
    local oldBrains = KCD2MP_W216On("no_brains")
    KCD2MP.w137.joiner, KCD2MP.w137.active = true, true
    KCD2MP_W216Cfg("no_brains=on")
    W216_RELEASE = true
    System.ExecuteCommand("wh_ai_PauseNPC retail_load"); System.ExecuteCommand("wh_ai_ResumeNPC retail_load")
    W216_RELEASE = false
    check("(p1) pending permits retail pause and resume despite legacy release policy", #CMDS == mark + 2
        and KCD2MP_W216JoinerBrainsOff() and KCD2MP.hostAuthority.phase == "pending")
    mark = #CMDS
    KCD2MP_ModBrainCommand("  wh_ai_PauseNPC load_copy"); KCD2MP_ModBrainCommand("wh_ai_ResumeNPC load_copy")
    check("(p1) pending blocks the mod's exact pause and resume verbs", #CMDS == mark)
    KCD2MP_W176Pause("p1_conditional", true, "pending"); KCD2MP_W176Pause("p1_conditional", false, "pending")
    check("(p1) pending blocks the conditional mod verb callsite", #CMDS == mark)
    KCD2MP.w167.talk.fresh.p1_router = NOW
    check("(p1) pending blocks the variable w164 console router", KCD2MP_W167FreshResume("p1_router") == true and #CMDS == mark)
    KCD2MP_ModBrainCommand("wh_ai_ResumeNPCDiagnostic exact_verb")
    KCD2MP_ModBrainCommand("sys_budget unrelated")
    check("(p1) the mod helper forwards unrelated and similarly named verbs", #CMDS == mark + 2
        and CMDS[#CMDS - 1] == "wh_ai_ResumeNPCDiagnostic exact_verb" and CMDS[#CMDS] == "sys_budget unrelated")
    mark = #CMDS
    check("(p1) a late active from older load is rejected", KCD2MP_HostAuthority(true, true, "active", "9") == false
        and KCD2MP.hostAuthority.phase == "pending" and not KCD2MP.hostAuthority.armed)
    check("(p1) current active confirms native gate", KCD2MP_HostAuthority(true, true, "active", "10") == true and KCD2MP.hostAuthority.armed)
    System.ExecuteCommand("wh_ai_PauseNPC retail_active"); System.ExecuteCommand("wh_ai_ResumeNPC retail_active")
    KCD2MP_ModBrainCommand("wh_ai_PauseNPC mod_active"); KCD2MP_ModBrainCommand("wh_ai_ResumeNPC mod_active")
    check("(p1) active blocks retail and mod pause and resume", #CMDS == mark)
    KCD2MP.w137.joiner, KCD2MP.w137.active = oldJoiner, oldActive
    KCD2MP_W216Cfg("no_brains=" .. (oldBrains and "on" or "off"))
    check("(p1) delayed pending of same load cannot downgrade active", KCD2MP_HostAuthority(true, false, "pending", "10") == false and KCD2MP.hostAuthority.armed)
    check("(p1) confirmed legacy restores local command path", KCD2MP_HostAuthority(false, false, "legacy", "11") == true)
    KCD2MP_ModBrainCommand("wh_ai_PauseNPC legacy_copy"); KCD2MP_ModBrainCommand("wh_ai_ResumeNPC legacy_copy")
    check("(p1) classic mod pause and resume both reach engine", #CMDS == mark + 2)
    System.ExecuteCommand("wh_ai_PauseNPC retail_legacy"); System.ExecuteCommand("wh_ai_ResumeNPC retail_legacy")
    check("(p1) legacy also permits retail pause and resume", #CMDS == mark + 4)
    mark = #CMDS
    check("(p1) delayed pending of same off load cannot disable legacy", KCD2MP_HostAuthority(true, false, "pending", "11") == false and KCD2MP.hostAuthority.phase == "legacy")
    local owned = mkEntity("p1_owned_pause", 0, 0, 0); owned.soul = { GetId = function() return "p1-owned-soul" end }; ENTS.p1_owned_pause = owned
    local reused = mkEntity("p1_reused_pause", 0, 0, 0); reused.soul = { GetId = function() return "p1-new-soul" end }; ENTS.p1_reused_pause = reused
    KCD2MP._npcPaused.p1_owned_pause, KCD2MP._npcPaused.p1_reused_pause = NOW, NOW
    KCD2MP._npcPauseExec.p1_owned_pause, KCD2MP._npcPauseExec.p1_reused_pause = "ok", "ok"
    KCD2MP._npcPauseEntity.p1_owned_pause = { id = tostring(owned.id), soul = "p1-owned-soul" }
    KCD2MP._npcPauseEntity.p1_reused_pause = { id = tostring(reused.id), soul = "p1-old-soul" }
    local noGetter = mkEntity("p1_no_soul_getter", 0, 0, 0); noGetter.soul = {}; ENTS.p1_no_soul_getter = noGetter
    KCD2MP._npcPaused.p1_no_soul_getter, KCD2MP._npcPauseExec.p1_no_soul_getter = NOW, "ok"
    KCD2MP._npcPauseEntity.p1_no_soul_getter = { id = tostring(noGetter.id), soul = "p1-prior-soul" }
    check("(p1) old cleanup generation is refused", KCD2MP_P1ReleaseLegacy("10") == false and #CMDS == mark)
    check("(p1) exact owned legacy pause is released", KCD2MP_P1ReleaseLegacy("11") == true and #CMDS == mark + 1
        and CMDS[#CMDS] == "wh_ai_ResumeNPC p1_owned_pause")
    check("(p1) reused body is never resumed from old name", KCD2MP._npcPaused.p1_reused_pause ~= nil)
    check("(p1) missing soul identity getter never authorizes a cleanup resume", KCD2MP._npcPaused.p1_no_soul_getter ~= nil)
    KCD2MP_HostAuthority(true, false, "pending", "12")
    KCD2MP_ModBrainCommand("wh_ai_PauseNPC next_load_copy")
    check("(p1) next load blocks mod legacy again", #CMDS == mark + 1 and KCD2MP.hostAuthority.phase == "pending")
    check("(p1) load discards old Lua pause markers without resuming new copies", next(KCD2MP._npcPaused) == nil and next(KCD2MP._npcPauseEntity) == nil)
    check("(p1) unversioned delayed active cannot override load", KCD2MP_HostAuthority(true, true) == false and not KCD2MP.hostAuthority.armed)
    KCD2MP_HostAuthority(true, false, "pending", "18446744073709551610")
    check("(p1) uint64 generations are compared without Lua number rounding", KCD2MP_HostAuthority(true, true, "active", "18446744073709551609") == false
        and KCD2MP.hostAuthority.generation == "18446744073709551610")
    KCD2MP_HostAuthority(false, false, "disabled", "18446744073709551611")
    KCD2MP_P1SetHostAuthority("on")
    noErrs("(p1)")
end

OUT = table.concat(RESULTS, "\n")
