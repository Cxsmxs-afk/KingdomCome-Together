-- Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
-- GPLv3 section 7 additional terms: NOTICE. This project's own code only.
-- Run from the checkout with Lua 5.1 or later: lua tools/Test-TestlaufSynthetic.lua
-- An optional first argument names a different copy of kdcmp.lua.

local commands, events, hints = {}, {}, {}
System = {
    AddCCommand = function(name, script, help)
        assert(not commands[name])
        commands[name] = { script = script, help = help }
    end
}
function KCD2MP_EmitEvent(name, value) events[#events + 1] = { name, value } end
function mp_log(message) hints[#hints + 1] = message end

-- @@KDCMP@@
if not commands.mp_testlauf then
    local source = assert(io.open(arg[1] or "kdcmp/Data/Scripts/Startup/kdcmp.lua", "rb"))
    local text = source:read("*a")
    source:close()
    local first = assert(text:find("-- ===== T5: guided test run console", 1, true))
    local nextBlock = assert(text:find("-- ===== P1:", first, true))
    assert((loadstring or load)(text:sub(first, nextBlock - 1), "@kdcmp.lua/testlauf"))()
end
assert(commands.mp_testlauf.script == "KCD2MP_GuidedRunCommand(%line)")
assert(commands.mp_testlauf.help:find("weiter [ja|nein]", 1, true))
assert(#events == 0 and #hints == 0)

for _, valid in ipairs({ "start", "status", "weiter", "weiter ja", "weiter nein", "wiederholen", "abbruch" }) do
    local before = #events
    assert(KCD2MP_GuidedRunCommand("  --test-guided-run\t" .. valid .. "  "))
    assert(#events == before + 1 and events[#events][1] == "guided_run")
    assert(events[#events][2] == "--test-guided-run " .. valid)
end
assert(#hints == 0)
assert(KCD2MP_GuidedRunCommand("--test-guided-run  weiter\t  ja"))
assert(events[#events][2] == "--test-guided-run weiter ja")

for _, invalid in ipairs({
    "", "start", "--test-guided-run", "--test-guided-run START", "--test-guided-run unbekannt",
    "--test-guided-run status ja", "--test-guided-run start nein", "--test-guided-run abbruch nein",
    "--test-guided-run weiter ja nein", "--test-guided-run weiterja", "--test-guided-run weiter vielleicht",
    "--test-guided-run weiter Ja", "--test-guided-run weiter 1", "--test-guided-run start;",
    "--test-guided-run\nstart", "--test-guided-run weiter\rja", "--test-guided-run weiter\0ja",
    "--test-guided-run weiter ja; abbruch", "--test-guided-run --test-guided-run status",
    42, false, {}
}) do
    local before = #events
    assert(not KCD2MP_GuidedRunCommand(invalid))
    assert(#events == before)
    assert(hints[#hints]:find("KCDMP_TEST_GUIDED_RUN=1", 1, true))
end
assert(not KCD2MP_GuidedRunCommand(nil))
OUT = "PASS T5 console: exact opt-in, explicit manual results and German help"
print(OUT)
