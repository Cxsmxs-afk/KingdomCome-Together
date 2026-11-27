// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
#pragma once

#include "motion.h"
#include <array>

namespace kcdmp::motion {

struct ModelScope {
    uint64_t load = 0, lifecycle = 0, life = 0;
    bool operator==(const ModelScope& other) const {
        return load == other.load && lifecycle == other.lifecycle && life == other.life;
    }
};

// Frame-local observations. All slots have fixed storage. A collision chain beyond the probe budget
// goes straight to the Engine, so a busy scene still gets fresh data with a bounded lookup cost.
class FrameModels {
    struct Entry {
        uint64_t frame = 0;
        ModelScope scope;
        uint32_t eid = 0;
        const void* entity = nullptr;
        ModelRead model{};
        bool readable = false;
    };
    std::array<Entry, 256> entries_{};
    uint64_t frame_ = 0;

public:
    struct Counts { unsigned reads = 0, reused = 0, overflow = 0; } counts;

    void begin() { ++frame_; counts = {}; }

    template<class Lookup, class Read>
    bool read(uint32_t eid, const void* entity, ModelScope scope, ModelRead* out, Lookup actor, Read raw) {
        for (size_t p = 0; p < 4; ++p) {
            Entry& entry = entries_[(static_cast<size_t>(eid) + p) % entries_.size()];
            if (entry.frame == frame_ && entry.eid != eid) continue;
            if (entity && entry.frame == frame_ && entry.scope == scope && entry.entity == entity) {
                *out = entry.model;
                ++counts.reused;
                return entry.readable;
            }
            ++counts.reads;
            const bool readable = raw(actor(), out);
            entry = {frame_, scope, eid, entity, *out, readable};
            return readable;
        }
        ++counts.reads;
        ++counts.overflow;
        return raw(actor(), out);
    }
};

} // namespace kcdmp::motion
