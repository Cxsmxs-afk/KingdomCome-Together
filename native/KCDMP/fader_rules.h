// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
#pragma once
#include <cstdint>

namespace kcdmp::fader {
enum class Owner : uint8_t { None = 0, Respawn = 1, VideoLanding = 2, Wake = 3, Harness = 4 };
inline bool external(Owner o) { return o == Owner::VideoLanding || o == Owner::Wake || o == Owner::Harness; }
struct Lease {
    Owner owner = Owner::None;
    uint32_t token = 0;
    bool matches(Owner who, uint32_t id) const { return owner == who && token == id && id != 0; }
    bool may_acquire(Owner who, uint32_t id) const {
        return id != 0 && (who == Owner::Respawn || external(who)) && (owner == Owner::None || matches(who, id));
    }
    void acquired(Owner who, uint32_t id) { owner = who; token = id; }
    void clear() { owner = Owner::None; token = 0; }
};
}
