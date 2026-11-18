-- Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
-- GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
-- content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
-- P3/T3 input: actual Lua slot observation and bounded owner/lifecycle recovery.
-- Engine is stubbed; this does not prove native input state or a live game unlock.

NOW = 0
os.clock = function() return NOW end
LOG = {}; TIMERS = {}; ENTS = {}; ERRS = {}; TOASTS = {}; SPHERE = {}
CMDS = {}; DRAWS = {}; CCMDS = {}; LOCKS = {}; MSGS = {}; EXTRA = {}
QINV = {}; CREATE_REFUSED = {}

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
System.DrawText = function() end
System.RemoveEntity = function(eid)
    for n, e in pairs(ENTS) do if e.id == eid then ENTS[n] = nil; e.removed = true end end
    for _, e in ipairs(EXTRA) do if e.id == eid then e.removed = true end end
end
System.AddCCommand = function(name, body, help) CCMDS[name] = { body = tostring(body), help = tostring(help or "") } end
System.GetTerrainElevation = function() return 0 end
System.GetEntity = function(id)
    for _, e in pairs(ENTS) do if e.id == id then return e end end
    for _, e in ipairs(EXTRA) do if e.id == id and not e.removed then return e end end
    return nil
end
System.GetEntitiesByClass = function(cls)
    local out = {}
    for _, e in pairs(ENTS) do if e.class == cls then out[#out + 1] = e end end
    for _, e in ipairs(EXTRA) do if e.class == cls and not e.removed then out[#out + 1] = e end end
    return out
end
ENTITY_FLAG_NO_SAVE = 32768
Script = mkstub()
Script.SetTimer = function(ms, f) TIMERS[#TIMERS + 1] = { ms = ms, f = f, at = NOW } end
Game = mkstub(); AI = mkstub(); Sound = mkstub(); Physics = mkstub(); Terrain = mkstub()
Game.AddSaveLock = function(name) if LOCKS[name] then return false end LOCKS[name] = true; return true end
Game.RemoveSaveLock = function(name) local had = LOCKS[name] == true; LOCKS[name] = nil; return had end
UIAction = mkstub()
UIAction.CallFunction = function(panel, inst, fn, text) TOASTS[#TOASTS + 1] = tostring(text) end
WORLD_T = 1000
Calendar = { GetWorldTime = function() return WORLD_T end, SetWorldTime = function(t) WORLD_T = t end,
             GetWorldTimeRatio = function() return 15 end, SetWorldTimeRatio = function() end }

local NEXTID = 0x0E0000
local function mkEntity(name, x, y, z, cls, keep)
    NEXTID = NEXTID + 1
    local e = { class = cls or "NPC", id = "userdata: " .. string.format("%016X", NEXTID), px = x or 0, py = y or 0, pz = z or 0, rz = 0,
                dead = false, hp = 100, hidden = false, flags = 0, mounted = false, dismounts = 0, mounts = 0 }
    e.this = { id = "this:" .. name }
    e.GetName = function(self) return name end
    e.GetWorldPos = function(self, t) t = t or {}; t.x, t.y, t.z = self.px, self.py, self.pz; return t end
    e.GetWorldAngles = function(self, t) t = t or {}; t.x, t.y, t.z = 0, 0, self.rz; return t end
    e.SetWorldPos = function(self, pos) self.px, self.py, self.pz = pos.x, pos.y, pos.z end
    e.SetWorldAngles = function(self, a) self.rz = a.z end
    e.SetFlags = function(self, f, mode) if mode == 3 then self.flags = self.flags + f else self.flags = f end end
    e.Hide = function(self, v) self.hidden = (v == 1 or v == true) end
    e.IsHidden = function(self) return self.hidden end
    e.StartAnimation = function() end
    e.actor = { IsDead = function() return e.dead end, IsUnconscious = function() return false end, GetHealth = function() return e.hp end }
    e.human = { IsInDialog = function() return false end, IsMounted = function() return e.mounted end,
                ForceDismount = function() e.mounted = false; e.dismounts = e.dismounts + 1 end,
                ForceMount = function() e.mounted = true; e.mounts = e.mounts + 1 end }
    e.soul = { GetId = function() return "userdata: 05000000000001DC" end }
    if keep then EXTRA[#EXTRA + 1] = e else ENTS[name] = e end
    return e
end

XGenAIModule = mkstub()
XGenAIModule.SpawnEntity = function(t) mkEntity(t.Name, t.Pos[1], t.Pos[2], t.Pos[3], t.ClassName) end
XGenAIModule.MakeTableFromType = function(kind) return { kind = kind, information = {} } end

player = { id = 1, this = { id = 777 }, GetName = function(self) return "Dude" end,
           GetWorldPos = function(self) return { x = 0, y = 0, z = 0 } end,
           GetWorldAngles = function() return { x = 0, y = 0, z = 0 } end,
           actor = { GetHealth = function() return 100 end, IsDead = function() return false end },
           human = { IsMounted = function() return false end, IsInDialog = function() return false end },
           inventory = {
               GetCountOfClass = function(self, g) return QINV[g] or 0 end,
               CreateItem = function(self, g, hp, n) if CREATE_REFUSED[g] then return false end QINV[g] = (QINV[g] or 0) + (n or 1); return true end,
               DeleteItemOfClass = function(self, g, n) local h = QINV[g] or 0; local d = math.min(h, n or 0); QINV[g] = h - d; return d end,
           } }


FLAGS = { loading = false, dead = false, down = false, sleeping = false, dialog = false }
WRITES = {}
local WRITE_THROW, WRITE_FALSE = false, false
local ERROR_OBJECT = "map-write-error"
ActionMapManager = { EnableActionMap = function(map, on)
    if WRITE_THROW then error(ERROR_OBJECT, 0) end
    WRITES[#WRITES + 1] = { map = map, on = on }
    if WRITE_FALSE then return false, "refused" end
    return nil, "receipt", nil, 7, nil
end }
Game.IsLoadingEngineSaveGame = function() return FLAGS.loading end
player.actor.IsDead = function() return FLAGS.dead end
player.actor.IsUnconscious = function() return FLAGS.down end
player.human.IsInDialog = function() return FLAGS.dialog end
player.human.IsSleeping = function() return FLAGS.sleeping end

-- @@KDCMP@@

local RESULTS = {}
local function check(name, ok, detail)
    RESULTS[#RESULTS + 1] = (ok and "PASS  " or "FAIL  ") .. name .. (detail and (" [" .. tostring(detail) .. "]") or "")
end
local function packed(...) return { n = select("#", ...), ... } end
local function clean()
    NOW = NOW + 1000
    FLAGS.loading, FLAGS.dead, FLAGS.down, FLAGS.sleeping, FLAGS.dialog = false, false, false, false, false
    WRITE_THROW, WRITE_FALSE = false, false
    KCD2MP.cutsceneActive = false
    KCD2MP.w179.vid = nil
    KCD2MP.w136.hold = false
    KCD2MP.w123.paused, KCD2MP.w123.joinId = false, nil
    KCD2MP.w211.holding = false
    KCD2MP.W203.cam.listening = nil
    KCD2MP_T3InputMenu(false)
    KCD2MP_T3InputReset("synthetic")
    WRITES = {}
end
local function tickAfter(seconds) NOW = NOW + seconds; KCD2MP_T3InputTick() end
local function releases() return KCD2MP.t3Input.stats.input_releases end

clean()
check("install: real slot wrapper installed", ActionMapManager.EnableActionMap == KCD2MP.t3Input.wrapper)
local r = packed(ActionMapManager.EnableActionMap("unwatched", true))
check("receipt: nil/multiple/trailing-nil returns preserved", r.n == 5 and r[1] == nil and r[2] == "receipt" and r[3] == nil and r[4] == 7 and r[5] == nil)
WRITE_THROW = true
local ok, err = pcall(ActionMapManager.EnableActionMap, "no_input", true)
check("receipt: engine failure propagated without phantom hold", not ok and tostring(err):find(ERROR_OBJECT, 1, true) ~= nil and KCD2MP.t3Input.rows.no_input == nil)
WRITE_THROW = false; WRITE_FALSE = true
local before = KCD2MP.t3Input.serial
r = packed(ActionMapManager.EnableActionMap("no_input", true))
check("receipt: explicit refusal preserved and not observed", r.n == 2 and r[1] == false and r[2] == "refused" and KCD2MP.t3Input.serial == before and KCD2MP.t3Input.rows.no_input == nil)

for _, entry in ipairs({ {"no_input",true}, {"noninteractive",true}, {"player",false}, {"movement",false} }) do
    clean(); local n = releases()
    ActionMapManager.EnableActionMap(entry[1], entry[2])
    tickAfter(2.999)
    check(entry[1] .. ": no release before3s", #WRITES == 1 and releases() == n)
    tickAfter(0.001)
    check(entry[1] .. ": no release at3s", #WRITES == 1)
    tickAfter(0.001)
    check(entry[1] .. ": exact inverse after3s", #WRITES == 2 and WRITES[2].map == entry[1] and WRITES[2].on ~= entry[2] and releases() == n + 1)
    tickAfter(1)
    check(entry[1] .. ": release exactly once", #WRITES == 2 and releases() == n + 1)
end
check("release diagnostic contains required unknown-owner and measured duration", table.concat(LOG, "\n"):find("T3-INPUT-RELEASE owner=? held_s=", 1, true) ~= nil)
clean(); tickAfter(10)
check("no observed write means no recovery write", #WRITES == 0)
ActionMapManager.EnableActionMap("dialog", true); tickAfter(10)
check("unknown dialog map not blindly released", #WRITES == 1)
clean(); ActionMapManager.EnableActionMap("movement", false); tickAfter(2)
ActionMapManager.EnableActionMap("movement", false); tickAfter(1.01)
check("identical reassert preserves original held age", #WRITES == 3 and WRITES[3].on == true)
check("held metric uses rounded-up seconds", KCD2MP.t3Input.stats.input_held_max_s >= 4)
clean(); ActionMapManager.EnableActionMap("movement", false); tickAfter(1)
ActionMapManager.EnableActionMap("movement", true); tickAfter(5)
check("real release removes hold", #WRITES == 2 and KCD2MP.t3Input.rows.movement == nil)
clean(); ActionMapManager.EnableActionMap("no_input", true); WRITE_THROW = true; local n = releases(); tickAfter(4)
WRITE_THROW = false; tickAfter(4)
check("failed recovery not counted or repeatedly attempted", #WRITES == 1 and releases() == n and KCD2MP.t3Input.rows.no_input.attempted)

local ownerCases = {
    {"menu", function() KCD2MP_T3InputMenu(true) end},
    {"no-player", function() player = nil end},
    {"loading", function() FLAGS.loading = true end},
    {"load-hold", function() KCD2MP.w136.hold=true; KCD2MP.w136.holdUntil=NOW+60 end},
    {"scene", function() KCD2MP.cutsceneActive=true end},
    {"video", function() KCD2MP.w179.vid={} end},
    {"dialog", function() FLAGS.dialog=true end},
    {"listener", function() KCD2MP.W203.cam.listening={starter="p",cid="c"} end},
    {"dead", function() FLAGS.dead=true end},
    {"unconscious", function() FLAGS.down=true end},
    {"sleeping", function() FLAGS.sleeping=true end},
    {"join", function() local w=KCD2MP.w123; w.paused=true;w.joinId="join";w.pausedAt=NOW end},
    {"link", function() KCD2MP.w211.holding=true; KCD2MP_T3InputLink(true,60) end},
}
local henry = player
for _, c in ipairs(ownerCases) do
    player = henry; clean(); c[2]()
    -- Observe this write in the current life, including a load/dead lifecycle edge.
    ActionMapManager.EnableActionMap("movement", false)
    tickAfter(4)
    check("owner protects: " .. c[1], #WRITES == 1 and KCD2MP_T3InputOwner() ~= nil)
end
player = henry; clean(); ActionMapManager.EnableActionMap("no_input", true)
local old = player.human.IsInDialog
player.human.IsInDialog = function() error("unreadable") end
tickAfter(4)
check("failed owner query never unlocks", #WRITES == 1 and KCD2MP_T3InputOwner() == "unknown-dialog")
player.human.IsInDialog = old; tickAfter(0.25)
check("owner query becomes readable: exact observed hold recovered", #WRITES == 2)
clean(); ActionMapManager.EnableActionMap("no_input", true)
KCD2MP.w179.hold=true; KCD2MP.w179.holdUntil=NOW+60; tickAfter(4)
check("fader/video hold without actual scene is not input ownership", #WRITES == 2)
KCD2MP.w179.hold=false

clean(); KCD2MP_T3InputWrite("join","A","movement",false)
KCD2MP_T3InputWrite("link","B","movement",false)
local count = #WRITES
ok = pcall(KCD2MP_T3InputWrite,"join","A","movement",true)
check("stale owner cannot release later map writer", not ok and #WRITES == count)
check("current owner can release its exact map", pcall(KCD2MP_T3InputWrite,"link","B","movement",true) and #WRITES == count + 1)
clean(); KCD2MP_T3InputWrite("listener","one","dialog",true)
KCD2MP_T3InputWrite("listener","two","dialog",true)
count = #WRITES; ok=pcall(KCD2MP_T3InputWrite,"listener","one","dialog",false)
check("stale listener cannot release replacement listener", not ok and #WRITES == count)

clean(); ActionMapManager.EnableActionMap("movement",false)
KCD2MP_T3InputReset("load"); tickAfter(5)
check("explicit lifecycle reset never unlocks old world", #WRITES == 1)
clean(); ActionMapManager.EnableActionMap("movement",false)
local originalPlayer = player; player = {id=originalPlayer.id, actor=originalPlayer.actor,human=originalPlayer.human}
tickAfter(5)
check("same entity id with new player object discards old hold", #WRITES == 1 and KCD2MP.t3Input.rows.movement == nil)
player=originalPlayer
clean(); ActionMapManager.EnableActionMap("movement",false)
FLAGS.loading=true; KCD2MP_T3InputTick(); FLAGS.loading=false; tickAfter(5)
check("real loading edges discard old map episodes", #WRITES == 1)

clean(); local use = KCD2MP_T3InputUseSnapshot()
check("use snapshot protects own actual use for bounded4s", KCD2MP_T3InputOwner() == "pickup-use")
check("own use can end without ending a different owner", KCD2MP_T3InputUseCanEnd(use) == true)
local newer = KCD2MP_T3InputUseSnapshot()
check("newer use without map writes fences previous use", KCD2MP_T3InputUseCanEnd(use) == false and KCD2MP_T3InputUseCanEnd(newer) == true)
ActionMapManager.EnableActionMap("another-map",true)
check("any newer observed map write fences old use", KCD2MP_T3InputUseCanEnd(newer) == false)
clean(); use=KCD2MP_T3InputUseSnapshot(); FLAGS.dialog=true
check("actual dialog owns input over use timeout", KCD2MP_T3InputUseCanEnd(use) == false)
clean(); ActionMapManager.EnableActionMap("no_input",true); use=KCD2MP_T3InputUseSnapshot(); tickAfter(3.1)
check("pickup use is protected against premature3s recovery", #WRITES == 1)
tickAfter(1)
check("own recovery keeps same-use EndInteractive authority", #WRITES == 2 and KCD2MP_T3InputUseCanEnd(use) == true)
clean(); ActionMapManager.EnableActionMap("no_input",true); use=KCD2MP_T3InputUseSnapshot()
ActionMapManager.EnableActionMap("another-map",true); tickAfter(4.1)
check("own recovery never restores rights lost to a foreign write", #WRITES == 3 and KCD2MP_T3InputUseCanEnd(use) == false)
clean(); use=KCD2MP_T3InputUseSnapshot(); KCD2MP_T3InputReset("respawn")
check("life reset prevents old use completion", KCD2MP_T3InputUseCanEnd(use) == false)

-- Real existing callsites, with integration instructions applied to temp source.
clean(); local w=KCD2MP.w123; w.joinId="J";w.pausedAt=NOW;w.paused=true
local holdOk=KCD2MP_JoinHold(true,"noinput")
tickAfter(4)
check("real JoinHold writes retain current join owner", holdOk and #WRITES == 1 and KCD2MP.t3Input.rows.no_input.owner == "join")
check("real JoinHold resume releases exactly its own map", KCD2MP_JoinHold(false,"noinput") == true and #WRITES == 2)
clean(); KCD2MP_W211LinkHold(true,60,"test"); tickAfter(4)
check("real W211 link hold is correctly labeled/protected", #WRITES == 2 and KCD2MP.t3Input.rows.movement.owner == "link")
KCD2MP_W211LinkHold(false,0,"")
check("real W211 release restores its two observed maps", #WRITES == 4 and WRITES[3].on == true and WRITES[4].on == true)
clean(); KCD2MP_W203Enter("P","C",""); tickAfter(4)
check("real W203 listener protects its own observed map", #WRITES == 1 and KCD2MP_T3InputOwner() == "listener" and KCD2MP.t3Input.rows.kcd2mp_listen.owner == "listener")
KCD2MP_W203End("P","C","ended")
check("real W203 end releases its own map", #WRITES == 2 and WRITES[2].on == false)


clean(); local savedAuthority = KCD2MP.hostAuthority
KCD2MP.hostAuthority = { generation = "71" }
local lifeResets = 0; local savedLifeReset = KCD2MP_W134LootLifeReset
KCD2MP_W134LootLifeReset = function() lifeResets=lifeResets+1 end
local gen = KCD2MP.t3Input.generation
check("agent state rejects delayed different-authority event", KCD2MP_T3InputAgentState(true,true,10,"70") == false and not KCD2MP.t3Input.menu and KCD2MP.t3Input.generation == gen)
check("agent state accepts actual native menu event", KCD2MP_T3InputAgentState(true,false,10,"71") == true and KCD2MP_T3InputOwner() == "menu")
check("agent state ignores reversed/duplicate HTTP serials", KCD2MP_T3InputAgentState(false,false,9,"71") == false and KCD2MP_T3InputAgentState(false,false,10,"71") == false and KCD2MP.t3Input.menu)
check("agent downed edge invalidates input and loot life once", KCD2MP_T3InputAgentState(false,true,11,"71") == true and KCD2MP.t3Input.generation == gen+1 and lifeResets == 1 and KCD2MP_T3InputOwner() == "downed-event")
KCD2MP_T3InputAgentState(false,true,12,"71")
check("agent same-downed reassert does not reset life", KCD2MP.t3Input.generation == gen+1 and lifeResets == 1)
KCD2MP_T3InputAgentState(false,false,13,"71")
check("agent wake edge invalidates exactly once", KCD2MP.t3Input.generation == gen+2 and lifeResets == 2)
KCD2MP.hostAuthority.generation = "72"
check("new actual authority generation allows new serial basis", KCD2MP_T3InputAgentState(false,false,1,"72") == true)
KCD2MP_T3InputAgentState(false,true,2,"72")
local resetsBeforeWorld = lifeResets
KCD2MP.hostAuthority.generation = "73"
check("old-world downed state does not fabricate new-world wake", KCD2MP_T3InputAgentState(false,false,1,"73") and lifeResets == resetsBeforeWorld)
check("agent state validates integer serial and bool values", KCD2MP_T3InputAgentState(false,false,1.5,"73") == false and KCD2MP_T3InputAgentState("false",false,2,"73") == false)
KCD2MP.hostAuthority.generation = "74"
local revisionResets = lifeResets
KCD2MP_T3InputAgentState(false,false,1,"74",0)
check("initial revision0 without actual edge never resets life", lifeResets == revisionResets)
KCD2MP_T3InputAgentState(false,false,2,"74",100)
check("complete down-wake between snapshots resets by revision", lifeResets == revisionResets+1)
KCD2MP_T3InputAgentState(false,false,3,"74",100)
check("same revision on newer HTTP snapshot resets only once", lifeResets == revisionResets+1)
KCD2MP_T3InputAgentState(false,false,4,"74",99)
check("older life revision cannot manufacture another edge", lifeResets == revisionResets+1)
KCD2MP.hostAuthority.generation = "75"
KCD2MP_T3InputAgentState(false,false,1,"75",100)
check("already-seen revision never manufactures new-world wake", lifeResets == revisionResets+1)
KCD2MP_T3InputAgentState(false,false,2,"75",101)
check("new-world new revision resets life exactly once", lifeResets == revisionResets+2)
KCD2MP_T3InputAgentState(false,false,1,"75",102)
check("stale HTTP serial cannot consume a new life revision", lifeResets == revisionResets+2)
KCD2MP_T3InputAgentState(false,false,3,"75",102)
check("fresh serial can still apply previously rejected revision", lifeResets == revisionResets+3)
KCD2MP.hostAuthority.generation = "76"
KCD2MP_T3InputAgentState(false,false,1,"76",103)
check("first new-gen snapshot with proven new revision resets life", lifeResets == revisionResets+4)
KCD2MP.hostAuthority.generation = "77"
KCD2MP_T3InputAgentState(false,false,1,"77",0)
check("new-gen snapshot with no event revision never resets life", lifeResets == revisionResets+4)
check("invalid life revision is refused before mutation", KCD2MP_T3InputAgentState(false,false,2,"77",103.5) == false and lifeResets == revisionResets+4)
KCD2MP.hostAuthority, KCD2MP_W134LootLifeReset = savedAuthority, savedLifeReset

clean(); local scans = 0; System.GetEntitiesInSphere=function()scans=scans+1;return{}end
local events = {}; local emit=KCD2MP_EmitEvent
KCD2MP_EmitEvent=function(kind,text) events[#events+1]={kind,text} end
tickAfter(60)
check("independent tick performs no world scan", scans == 0)
local ev=events[#events]
check("60s stats use numeric integer whitelist values", ev and ev[1] == "t3_input_stats" and ev[2]:match("^input_held_max_s=%d+ input_releases=%d+$") ~= nil)
KCD2MP_EmitEvent=emit
local timersBefore=#TIMERS
local callback
for i=#TIMERS,1,-1 do if TIMERS[i].ms == 250 then callback=TIMERS[i].f;break end end
if callback then callback() end
check("watchdog independently reschedules its narrow250ms timer", callback and #TIMERS == timersBefore+1 and TIMERS[#TIMERS].ms == 250)
OUT=table.concat(RESULTS,"\n")
