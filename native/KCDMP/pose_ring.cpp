// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
#include "pose_ring.h"
#include "local_state.h"

#include <cstring>

#define WIN32_LEAN_AND_MEAN
#include <windows.h>

namespace kcdmp::posering {

namespace {
HANDLE g_map = nullptr;
void* g_bytes = nullptr;
View g_view{};
uint32_t g_lastHostMs = 0;
}

void start() {
    if (g_bytes) return;
    g_map = CreateFileMappingA(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, bytes_needed(), "Local\\KCD2MP.PoseRing.1");
    if (!g_map) return;
    g_bytes = MapViewOfFile(g_map, FILE_MAP_ALL_ACCESS, 0, 0, bytes_needed());
    if (!g_bytes) { CloseHandle(g_map); g_map = nullptr; return; }
    std::memset(g_bytes, 0, bytes_needed());
    g_view = view_of(g_bytes);
    g_view.header->magic = kMagic;
    g_view.header->version = kVersion;
    g_view.header->capacity = kCapacity;
    g_view.header->slotBytes = sizeof(Slot);
}

void tick() {
    if (!g_bytes) start();
    if (!g_view.header) return;
    localstate::LocalState s{};
    if (!localstate::read_local_state(&s) || s.refuse != localstate::kOk) return;
    uint32_t ms = static_cast<uint32_t>(s.frame);
    if (ms == g_lastHostMs) return;
    g_lastHostMs = ms;
    publish(g_view, ms, s.x, s.y, s.z, s.rotZ, kPlayer, s.flags, "henry");
    if (s.flags & 1u) publish(g_view, ms, s.x, s.y, s.z, s.rotZ, kHorse, s.flags, "horse");
}

} // namespace kcdmp::posering
