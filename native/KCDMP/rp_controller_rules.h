// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
#pragma once

#include "gait_logic.h"

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <limits>

namespace kcdmp::rpctrl {

enum class Fallback : uint8_t { None, Jump, Mount, Ragdoll, Air };
enum class Relocation : uint8_t { None, Join, Respawn, Travel };
constexpr uint64_t kPermitMs = 3000;
constexpr float kHardStepM = 0.50f;

inline Fallback fallback(bool jump, bool mount, bool ragdoll, float verticalMps) {
    if (ragdoll) return Fallback::Ragdoll;
    if (mount) return Fallback::Mount;
    if (jump) return Fallback::Jump;
    if (std::fabs(verticalMps) > 1.5f) return Fallback::Air;
    return Fallback::None;
}

inline bool finite_position(const float pos[3]) {
    return pos && std::isfinite(pos[0]) && std::isfinite(pos[1]) && std::isfinite(pos[2]) &&
           std::fabs(pos[0]) < 1000000.f && std::fabs(pos[1]) < 1000000.f && std::fabs(pos[2]) < 1000000.f;
}

inline float distance(const float a[3], const float b[3]) {
    const float x = a[0] - b[0], y = a[1] - b[1], z = a[2] - b[2];
    return std::sqrt(x * x + y * y + z * z);
}

struct Proof {
    Relocation kind = Relocation::None;
    uint64_t receivedMs = 0, lifecycle = 0, connection = 0;
    float endpoint[3]{};

    bool valid(uint64_t now, uint64_t world, uint64_t pipe) const {
        return kind != Relocation::None && receivedMs && now >= receivedMs && now - receivedMs <= kPermitMs &&
               connection && world == lifecycle && pipe == connection;
    }
    bool allows(uint64_t now, uint64_t world, uint64_t pipe, const float confirmed[3], const float observed[3]) const {
        return valid(now, world, pipe) && finite_position(endpoint) && finite_position(confirmed) &&
               finite_position(observed) && distance(confirmed, endpoint) <= 0.80f &&
               distance(observed, confirmed) > kHardStepM;
    }
    void consume() { kind = Relocation::None; }
};

// In the writer lane ordinary correction is bounded in all coordinates, even
// during a large unexplained relocation or the first frame after a handover.
inline void soften(const float observed[3], float observedYaw, float pose[4], float speed, double dt) {
    const float elapsed = static_cast<float>((std::clamp)(dt, 0.0, 0.05));
    const float dx = pose[0] - observed[0], dy = pose[1] - observed[1];
    const float len = std::sqrt(dx * dx + dy * dy);
    const float allowance = (std::clamp)(speed + 0.9f, 0.9f, 6.5f) * elapsed;
    if (len > allowance) {
        pose[0] = observed[0] + dx * (allowance / len);
        pose[1] = observed[1] + dy * (allowance / len);
    }
    const float z = 2.5f * elapsed;
    pose[2] = observed[2] + (std::clamp)(pose[2] - observed[2], -z, z);
    constexpr float twoPi = 6.283185307179586f;
    const float angle = 3.2f * elapsed;
    pose[3] = observedYaw + (std::clamp)(std::remainder(pose[3] - observedYaw, twoPi), -angle, angle);
}

struct Plan {
    float move[3]{}, face[3]{};
    float speed = 0, gait = 0, endM = 0;
    bool walking = false;
};

inline Plan movement(const float observed[3], const float target[4], float vx, float vy, float pace,
                     bool crouch, float previousSpeed, double dt) {
    Plan plan;
    const float dx = target[0] - observed[0], dy = target[1] - observed[1];
    const float error = std::sqrt(dx * dx + dy * dy);
    const float horizontal = std::sqrt(vx * vx + vy * vy);
    const float forwardX = -std::sin(target[3]), forwardY = std::cos(target[3]);
    plan.face[0] = observed[0] + forwardX; plan.face[1] = observed[1] + forwardY; plan.face[2] = observed[2];
    plan.walking = pace >= 0.14f || error > 0.20f;
    if (!plan.walking) return plan;
    plan.move[0] = target[0]; plan.move[1] = target[1]; plan.move[2] = target[2];
    plan.endM = error;
    if (pace >= 0.14f) {
        const float moveX = horizontal > 0.01f ? vx / horizontal : forwardX;
        const float moveY = horizontal > 0.01f ? vy / horizontal : forwardY;
        const float look = (std::clamp)(pace * 0.35f, 0.45f, 1.25f);
        plan.move[0] += moveX * look; plan.move[1] += moveY * look;
        plan.endM += look;
        const float errorAlong = dx * moveX + dy * moveY;
        plan.speed = (std::clamp)(pace + errorAlong * 1.4f, pace * 0.65f, pace + 0.85f);
    } else {
        plan.speed = (std::clamp)(error * 0.95f, 0.25f, 1.55f);
    }
    plan.speed = (std::min)(plan.speed, crouch ? 1.55f : 6.5f);
    if (previousSpeed > 0.f && dt > 0.0 && dt < 0.250) {
        const float slew = 3.0f * static_cast<float>(dt);
        plan.speed = (std::clamp)(plan.speed, previousSpeed - slew, previousSpeed + slew);
        if (crouch) plan.speed = (std::min)(plan.speed, 1.55f);
    }
    plan.gait = crouch ? 1.f : (std::max)(1.f, gait::speed_class(pace > 0.14f ? pace : plan.speed));
    return plan;
}

inline bool pull(float observedStep, float expectedSpeed, double dt) {
    return dt > 0.0 && dt <= 0.250 && observedStep > kHardStepM &&
           observedStep - expectedSpeed * static_cast<float>(dt) * 1.5f > kHardStepM;
}

inline uint64_t pulls_per_min(uint64_t count, uint64_t milliseconds) {
    if (!milliseconds) return 0;
    const long double rate = static_cast<long double>(count) * 60000000.L / milliseconds;
    return rate >= (std::numeric_limits<uint64_t>::max)() ? (std::numeric_limits<uint64_t>::max)()
                                                         : static_cast<uint64_t>(rate);
}

} // namespace kcdmp::rpctrl
