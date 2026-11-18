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
#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <limits>
#include <string>
#include <unordered_map>
#include <utility>
#include <vector>

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

// Receive direction. This ring lives in the DLL and its caller serializes
// publish/drain. The outgoing cross-process mapping above keeps its v1 ABI.
namespace receive {

constexpr uint8_t kGhost = 0x02, kNpcStateDown = 0x27;
constexpr size_t kEnvelope = 9, kGhostFixed = 18, kNpcTail = 27, kStateBytes = 12;
constexpr size_t kBodyName = 63, kMaxEnvelope = kEnvelope + 2 + kBodyName + kNpcTail;

struct Scope {
    uint64_t observation = 0, connection = 0;
    bool operator==(Scope other) const { return observation == other.observation && connection == other.connection; }
};

struct Pose {
    uint8_t kind = 0, source = 0, flags = 0;
    char body[kBodyName + 1]{};
    float x = 0.0f, y = 0.0f, z = 0.0f, yaw = 0.0f;
    uint16_t sequence = 0;
    uint32_t senderMs = 0;
    int64_t arrivalQpc = 0, firstByteQpc = 0, stateQpc = 0;
    std::array<uint8_t, kStateBytes> state{};
    bool ghost() const { return kind == kGhost; }
};

inline uint16_t u16(const uint8_t* p) { return static_cast<uint16_t>(p[0] | (static_cast<uint16_t>(p[1]) << 8)); }
inline uint32_t u32(const uint8_t* p) { return static_cast<uint32_t>(u16(p)) | (static_cast<uint32_t>(u16(p + 2)) << 16); }
inline float f32(const uint8_t* p) {
    const uint32_t bits = u32(p);
    float value;
    std::memcpy(&value, &bits, sizeof(value));
    return value;
}
inline bool name_byte(uint8_t c) {
    return (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '_';
}

// Matches PositionCodec.TryDecodeGhost's four versions and flag AND length
// checks. The base transform remains usable if a sender's optional flag is short.
inline bool decode(const uint8_t* bytes, size_t size, Pose& pose) {
    pose = {};
    if (!bytes || size <= kEnvelope || size > kMaxEnvelope) return false;
    const uint64_t arrival = static_cast<uint64_t>(u32(bytes + 1)) | (static_cast<uint64_t>(u32(bytes + 5)) << 32);
    if (!arrival || arrival > static_cast<uint64_t>((std::numeric_limits<int64_t>::max)())) return false;
    pose.kind = bytes[0];
    pose.arrivalQpc = static_cast<int64_t>(arrival);
    const uint8_t* payload = bytes + kEnvelope;
    const size_t length = size - kEnvelope;
    const uint8_t* transform;
    if (pose.ghost()) {
        if (length != kGhostFixed && length != kGhostFixed + 4 && length != kGhostFixed + kStateBytes &&
            length != kGhostFixed + kStateBytes + 4) return false;
        pose.source = payload[0];
        std::snprintf(pose.body, sizeof(pose.body), "kcd2mp_%u", static_cast<unsigned>(pose.source));
        transform = payload + 1;
        const uint8_t ghostFlags = payload[17];
        pose.flags = (ghostFlags & 0x01) ? 0x10 : 0; // the parent owns a ridden body
        size_t tail = kGhostFixed;
        if ((ghostFlags & 0x10) && length >= tail + kStateBytes) {
            std::memcpy(pose.state.data(), payload + tail, kStateBytes);
            pose.stateQpc = pose.arrivalQpc;
            tail += kStateBytes;
        }
        if ((ghostFlags & 0x08) && length == tail + 4) pose.senderMs = u32(payload + tail);
    } else if (pose.kind == kNpcStateDown) {
        if (length < 2 + 1 + kNpcTail) return false;
        const size_t nameSize = payload[1];
        if (!nameSize || nameSize > kBodyName || length != 2 + nameSize + kNpcTail) return false;
        for (size_t i = 0; i < nameSize; ++i) if (!name_byte(payload[2 + i])) return false;
        pose.source = payload[0];
        std::memcpy(pose.body, payload + 2, nameSize);
        transform = payload + 2 + nameSize;
        pose.flags = transform[20] & 0x7F; // legacy 0x80 is a pipe-only State2 marker
        pose.sequence = u16(transform + 21);
        pose.senderMs = u32(transform + 23);
    } else return false;
    pose.x = f32(transform); pose.y = f32(transform + 4);
    pose.z = f32(transform + 8); pose.yaw = f32(transform + 12);
    return std::isfinite(pose.x) && std::isfinite(pose.y) && std::isfinite(pose.z) && std::isfinite(pose.yaw);
}

inline std::string body_key(const char* name) {
    std::string key(name);
    for (char& c : key) if (c >= 'A' && c <= 'Z') c += 'a' - 'A';
    return key;
}

inline bool newer(const Pose& candidate, const Pose& existing, bool senderClock) {
    if (candidate.arrivalQpc < existing.arrivalQpc) return false;
    if (candidate.kind != existing.kind || candidate.source != existing.source) return true;
    if (!candidate.ghost()) {
        const auto delta = static_cast<uint16_t>(candidate.sequence - existing.sequence);
        if (!delta || delta > 32768) return false;
    }
    if (senderClock && candidate.senderMs && existing.senderMs) {
        const auto delta = candidate.senderMs - existing.senderMs;
        if (!delta || delta > 0x7FFFFFFFu) return false;
    }
    return true;
}

// Only replaceable state enters this ring. Keep an explicit State2 update if
// the newer transform omits it, with the time at which that update arrived.
enum class Put { Accepted, Superseded, Full };
class Ring {
    struct Entry { Pose pose; uint64_t order; };
    using Entries = std::unordered_map<std::string, Entry>;
    Entries pending_;
    size_t capacity_;
    Scope scope_{};
    uint64_t next_ = 0;

public:
    explicit Ring(size_t capacity) : capacity_(capacity) {}

    Put publish(Pose pose, Scope scope, bool senderClock = true) {
        if (!(scope_ == scope)) { pending_.clear(); scope_ = scope; }
        auto key = body_key(pose.body);
        const auto it = pending_.find(key);
        if (it != pending_.end()) {
            const Pose& old = it->second.pose;
            if (!newer(pose, old, senderClock)) return Put::Superseded;
            if (!pose.stateQpc && old.stateQpc && pose.kind == old.kind && pose.source == old.source) {
                pose.state = old.state;
                pose.stateQpc = old.stateQpc;
            }
            it->second = {pose, ++next_};
        } else {
            if (pending_.size() >= capacity_) return Put::Full;
            pending_.emplace(std::move(key), Entry{pose, ++next_});
        }
        return Put::Accepted;
    }

    std::vector<Pose> drain(Scope current) {
        if (!(scope_ == current)) return {};
        std::vector<Entry> rows;
        rows.reserve(pending_.size());
        for (auto& row : pending_) rows.push_back(std::move(row.second));
        pending_.clear();
        std::sort(rows.begin(), rows.end(), [](const Entry& a, const Entry& b) {
            return a.pose.arrivalQpc == b.pose.arrivalQpc ? a.order < b.order : a.pose.arrivalQpc < b.pose.arrivalQpc;
        });
        std::vector<Pose> poses;
        poses.reserve(rows.size());
        for (auto& row : rows) poses.push_back(std::move(row.pose));
        return poses;
    }
};

} // namespace receive
} // namespace kcdmp::posering
