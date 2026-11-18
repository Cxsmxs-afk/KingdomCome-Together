-- P0/T3 console contract. Driver injects only the final block; no engine APIs or retail assets.
local events, commands = {}, {}
KCD2MP = {}
function KCD2MP_EmitEvent(name, value) events[#events + 1] = { name, value } end
function mp_log(text) end
System = { AddCCommand = function(name, script) commands[name] = script end }
-- @@KDCMP@@
assert(commands.mp_proc_frame_timeout)
assert(KCD2MP_T3ReportCfg()); assert(events[#events][1] == "t3_proc_timeout" and events[#events][2] == "2000")
assert(KCD2MP_T3SetProcFrameTimeout(" 50 ")); assert(events[#events][2] == "50")
assert(KCD2MP_T3SetProcFrameTimeout("30000")); assert(events[#events][2] == "30000")
for _, bad in ipairs({ "49", "30001", "-1", "2.5", "nan", "1e3", "on", "" .. string.rep("9", 40) }) do
    local before = #events; assert(not KCD2MP_T3SetProcFrameTimeout(bad)); assert(#events == before)
    assert(KCD2MP.t3.procFrameTimeoutMs == 30000)
end
assert(KCD2MP_T3SetProcFrameTimeout("")); assert(events[#events][2] == "30000")
assert(commands.mp_rp_measure_clock == 'KCD2MP_RpMeasureClock(%line)')
assert(KCD2MP_RpMeasureClock(" on ")); assert(events[#events][1] == "rp_measure_clock" and events[#events][2] == "on")
assert(KCD2MP_RpMeasureClock("off")); assert(events[#events][1] == "rp_measure_clock" and events[#events][2] == "off")
for _, bad in ipairs({ "", "ON", "1", "on off" }) do
    local before = #events; assert(not KCD2MP_RpMeasureClock(bad)); assert(#events == before)
end
OUT = "PASS P0/T3 console: defaults, report, bounds and malformed input\nPASS s2-1 console: explicit on/off test-mode request"
print(OUT)
