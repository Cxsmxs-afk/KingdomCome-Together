// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
#pragma once
namespace kcdmp::cvarlease {
enum class Result { Unowned, Held, Restored, Unknown, Foreign, Failed };
struct Lease { bool held = false; int previous = 0, value = 0; };
// Read/Write are the existing verified integer CVar operations. Never invent a
// previous value and never restore over a different current writer. A foreign
// rewrite to the identical held value is unobservable without a separate hook.
template<class Read, class Write>
Result change(Lease& lease, bool on, int value, Read read, Write write) {
    if (!on && !lease.held) return Result::Unowned;
    int current = 0;
    if (!read(&current)) { if (!on) lease.held = false; return Result::Unknown; }
    if (lease.held && current != lease.value) { lease.held = false; return Result::Foreign; }
    if (on) {
        if (lease.held) return Result::Held;
        if (current != value && !write(value)) return Result::Failed;
        lease.previous = current; lease.value = value; lease.held = true;
        return Result::Held;
    }
    lease.held = false;
    return current == lease.previous || write(lease.previous) ? Result::Restored : Result::Failed;
}
inline bool success(Result r) { return r == Result::Unowned || r == Result::Held || r == Result::Restored || r == Result::Foreign; }
} // namespace kcdmp::cvarlease
