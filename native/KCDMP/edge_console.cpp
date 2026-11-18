// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
#include "edge_console_rules.h"
#include "anchors.h"
#include "engine.h"
#include "fault_guard.h"
#include "log.h"
#include "main_thread.h"
#include "wo163.h"
#include "wo203.h"

namespace kcdmp::edgeconsole {
namespace {

// Main-thread execution only. A fault after entry may have changed the world.
// Its reply must not lead to another attempt through the HTTP door.
bool g_faulted = false;
bool g_resolved = false;
bool g_probeReturned = false;
size_t g_executeSlot = 0;
anchor::Range g_consoleCode;

void resolve() {
    if (g_resolved) return;
    HMODULE sys = GetModuleHandleA("CrySystem.dll");
    if (!sys || !GetModuleHandleA("CryScriptSystem.dll")) return;
    g_resolved = true;
    const char* reason = nullptr;
    g_executeSlot = engine::console_execute_slot(&reason);
    if (g_executeSlot && !anchor::section(sys, ".text", &g_consoleCode)) {
        reason = "CrySystem code section unavailable";
        g_executeSlot = 0;
    }
    logf("S2-EDGE-CONSOLE slot=0x%zX readiness=%s", g_executeSlot, reason);
}

Result run(const Command& command) {
    const unsigned long gameThread = main_thread::main_thread_id();
    if (!gameThread || GetCurrentThreadId() != gameThread || g_faulted) return Result::NotEntered;
    if (command.kind == Kind::Lua && !g_probeReturned) return Result::NotEntered;
    resolve();
    void* console = g_executeSlot ? engine::console() : nullptr;
    if (!console) return Result::NotEntered;

    void* entry = nullptr;
    KCDMP_FAULT_READ(read, "edge_console::entry");
    if (!fault::guarded(read, [&] { entry = (*static_cast<void***>(console))[g_executeSlot / sizeof(void*)]; }) ||
        !entry || !g_consoleCode.contains(entry)) return Result::NotEntered;

    // These arguments are the ones pushed by the anchored Lua binding.
    using Execute = void (*)(void*, const char*, bool, bool);
    bool entered = false;
    KCDMP_FAULT_CALL(call, "edge_console::ExecuteString");
    const bool returned = fault::guarded(call, [&] {
        entered = true;
        reinterpret_cast<Execute>(entry)(console, command.text.data(), false, false);
    });
    if (returned) {
        if (command.kind == Kind::Probe) g_probeReturned = true;
        return Result::Returned;
    }
    if (!entered) return Result::NotEntered;
    g_faulted = true;
    logf("S2-EDGE-CONSOLE disabled after ambiguous engine result");
    return Result::Uncertain;
}

uint8_t handle(uint8_t, const uint8_t* body, size_t len, uint8_t* out, size_t cap, size_t* used) {
    *used = 0;
    Command command{};
    if (!cap || !decode(body, len, command)) return wo163::kRBadRequest;
    out[0] = static_cast<uint8_t>(run(command));
    *used = 1;
    return wo163::kROk;
}

// Do the image scan at DLL startup before the pipe services any live traffic.
// If a module was absent at startup, the main-thread probe can try once it loads.
const bool kRegistered = wo203::reg_op(kOp, handle) && wo203::reg_install("edge-console", resolve);

} // namespace
} // namespace kcdmp::edgeconsole
