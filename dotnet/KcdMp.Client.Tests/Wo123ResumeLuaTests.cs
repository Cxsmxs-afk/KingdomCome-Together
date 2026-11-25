// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using MoonSharp.Interpreter;

namespace KcdMp.Client.Tests;

public sealed class Wo123ResumeLuaTests
{
    [Fact]
    public void The_lua_receipt_requires_clock_readback_and_a_retired_try_cannot_pause_again()
    {
        string source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Lua", "kdcmp.lua"));
        int start = source.IndexOf("function KCD2MP_JoinTry(", StringComparison.Ordinal);
        int end = source.IndexOf("function KCD2MP_JoinProgress(", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var script = new Script();
        script.DoString("""
            KCD2MP = { w123 = { tries = 0, ratioSet = false, safetyGen = 0, scanGen = 0, resumes = 0 }, _npcPaused = {} }
            ratio, lastEvent, failRestore = 15, "", true
            os.clock = function() return 10 end
            Calendar = {
                GetWorldTimeRatio = function() return ratio end,
                GetWorldTime = function() return 100 end,
                SetWorldTimeRatio = function(value) if value ~= 0 and failRestore then error("fixture") end; ratio = value end
            }
            Script = { SetTimer = function() end }
            function KCD2MP_JoinBusyReason() end
            function KCD2MP_JoinScanNpcs() return 0 end
            function KCD2MP_JoinHold() return true, "fixture" end
            function mp_log() end
            function KCD2MP_ShowInteractionMsg() end
            function KCD2MP_EmitEvent(name, arg) lastEvent = name .. " " .. arg end
            """);
        script.DoString(source[start..end], codeFriendlyName: "production JoinTry/JoinResume");
        script.DoString("""
            KCD2MP_JoinTry("00000001", "player", 180)
            KCD2MP_JoinResumeControl("00000001", "ready", "000000a1", false)
            """);
        Assert.Equal(0, script.Globals.Get("ratio").Number);
        Assert.Equal("join_resume_check 00000001 000000a1 paused", script.Globals.Get("lastEvent").String);
        script.DoString("""
            failRestore = false
            KCD2MP_JoinResumeControl("00000001", "ready", "000000a2", false)
            """);
        Assert.Equal(15, script.Globals.Get("ratio").Number);
        Assert.Equal("join_resume_check 00000001 000000a2 running", script.Globals.Get("lastEvent").String);
        Assert.False(script.DoString("""return KCD2MP_JoinTry("00000001", "player", 180)""").Boolean);
        Assert.Equal(15, script.Globals.Get("ratio").Number);
        Assert.True(script.DoString("""return KCD2MP_JoinTry("00000002", "player", 180)""").Boolean);
        script.DoString("""KCD2MP_JoinResumeControl("00000001", "cancel", "000000a3", true)""");
        Assert.Equal(0, script.Globals.Get("ratio").Number);
        Assert.Equal("join_resume_check 00000001 000000a3 paused", script.Globals.Get("lastEvent").String);
    }
}
