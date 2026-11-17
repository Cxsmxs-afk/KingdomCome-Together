// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
#pragma once
// Phase 3: one ring of poses. The DLL writes a sample per body per frame.
// The agent reads the bytes. A pose does not wait in the Lua console.
//
// The layout is the contract with dotnet/KcdMp.Client/PoseRing.cs. A reader
// whose version does not match reads nothing. A slot whose sequence changes
// mid-copy, or is odd, is a torn write and is skipped.

#include <atomic>
#include <cstdint>
#include <cstring>

namespace kcdmp::posering {

inline constexpr uint32_t kMagic = 0x33524E50u; // 'PNR3'
inline constexpr uint32_t kVersion = 1;
inline constexpr uint32_t kCapacity = 64;
inline constexpr uint32_t kNameLen = 24;
// The bytes "KCDMP-BUILD:0.65.2" live in pose_ring.cpp (kcdmp_build_stamp).
// An unused header string is stripped from the DLL, and the launcher then
// reports a missing stamp.

enum Kind : uint8_t { kPlayer = 1, kHorse = 2, kNpc = 3 };

enum ReadStatus : uint8_t { kOk = 0, kMismatch = 1, kEmpty = 2 };

struct Slot {
    std::atomic<uint32_t> seq{0};
    uint32_t hostMs = 0;
    float x = 0, y = 0, z = 0, rot = 0;
    uint8_t kind = 0, flags = 0, nameLen = 0, pad = 0;
    char name[kNameLen]{};
};

struct Header {
    uint32_t magic = kMagic;
    uint32_t version = kVersion;
    uint32_t capacity = kCapacity;
    std::atomic<uint32_t> writeIndex{0};
    std::atomic<uint32_t> overflow{0};
    uint32_t slotBytes = sizeof(Slot);
};

struct View {
    Header* header = nullptr;
    Slot* slots = nullptr;
};

struct Sample {
    uint32_t hostMs = 0;
    float x = 0, y = 0, z = 0, rot = 0;
    uint8_t kind = 0, flags = 0, nameLen = 0;
    char name[kNameLen]{};
    uint32_t index = 0;
};

inline View view_of(void* bytes) {
    View v;
    v.header = static_cast<Header*>(bytes);
    v.slots = reinterpret_cast<Slot*>(static_cast<char*>(bytes) + sizeof(Header));
    return v;
}

inline constexpr uint32_t bytes_needed() { return sizeof(Header) + kCapacity * sizeof(Slot); }

inline void publish(View v, uint32_t hostMs, float x, float y, float z, float rot,
                    uint8_t kind, uint8_t flags, const char* name) {
    if (!v.header || v.header->magic != kMagic || v.header->version != kVersion) return;
    uint32_t i = v.header->writeIndex.fetch_add(1, std::memory_order_relaxed);
    if (i >= v.header->capacity) v.header->overflow.fetch_add(1, std::memory_order_relaxed);
    Slot& s = v.slots[i % v.header->capacity];
    uint32_t gen = s.seq.load(std::memory_order_relaxed);
    uint32_t odd = (gen & ~1u) + 1u;
    s.seq.store(odd, std::memory_order_release);
    s.hostMs = hostMs;
    s.x = x; s.y = y; s.z = z; s.rot = rot;
    s.kind = kind; s.flags = flags;
    s.nameLen = 0;
    std::memset(s.name, 0, kNameLen);
    if (name) {
        uint32_t n = 0;
        while (name[n] && n < kNameLen) { s.name[n] = name[n]; ++n; }
        s.nameLen = static_cast<uint8_t>(n);
    }
    s.seq.store(odd + 1u, std::memory_order_release);
}

/// Copies samples with index in [cursor, writeIndex). Torn slots are skipped.
/// A header that is not this build returns kMismatch and writes nothing.
inline ReadStatus read_since(View v, uint32_t cursor, Sample* out, uint32_t max, uint32_t* count, uint32_t* next) {
    if (count) *count = 0;
    if (!v.header || v.header->magic != kMagic || v.header->version != kVersion || v.header->capacity != kCapacity)
        return kMismatch;
    uint32_t end = v.header->writeIndex.load(std::memory_order_acquire);
    if (cursor > end) cursor = end > v.header->capacity ? end - v.header->capacity : 0;
    if (end > cursor + v.header->capacity) cursor = end - v.header->capacity;
    uint32_t n = 0;
    for (uint32_t i = cursor; i < end && n < max; ++i) {
        Slot& s = v.slots[i % v.header->capacity];
        uint32_t a = s.seq.load(std::memory_order_acquire);
        if (a & 1u) continue;
        Sample c;
        c.hostMs = s.hostMs; c.x = s.x; c.y = s.y; c.z = s.z; c.rot = s.rot;
        c.kind = s.kind; c.flags = s.flags; c.nameLen = s.nameLen; c.index = i;
        std::memcpy(c.name, s.name, kNameLen);
        uint32_t b = s.seq.load(std::memory_order_acquire);
        if (a != b || (b & 1u)) continue;
        if (out && n < max) out[n] = c;
        ++n;
    }
    if (count) *count = n;
    if (next) *next = end;
    return n == 0 && cursor == end ? kEmpty : kOk;
}

/// Test hook: leave the next slot mid-write so a reader must reject it.
void start();
void tick();

inline void leave_torn(View v) {
    uint32_t i = v.header->writeIndex.fetch_add(1, std::memory_order_relaxed);
    Slot& s = v.slots[i % v.header->capacity];
    s.seq.store(1, std::memory_order_release);
    s.x = 99.f;
}

} // namespace kcdmp::posering
