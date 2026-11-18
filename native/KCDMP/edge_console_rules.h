// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
#pragma once

#include <array>
#include <cstddef>
#include <cstdint>
#include <cstring>

namespace kcdmp::edgeconsole {

constexpr uint8_t kOp = 254;
constexpr size_t kMaxRequestBytes = 1024;
constexpr size_t kNonceBytes = 8;
enum class Kind : uint8_t { Probe = 0, Lua = 1 };
enum class Result : uint8_t { NotEntered = 0, Returned = 1, Uncertain = 2 };

struct Command {
    Kind kind;
    std::array<char, kMaxRequestBytes> text;
};

inline bool decode(const uint8_t* body, size_t len, Command& command) {
    if (!body || len < 3 || len > kMaxRequestBytes || body[0] != kOp) return false;
    if (body[1] == static_cast<uint8_t>(Kind::Lua)) {
        if (std::memchr(body + 2, 0, len - 2)) return false;
        command.kind = Kind::Lua;
        command.text[0] = '#';
        std::memcpy(command.text.data() + 1, body + 2, len - 2);
        command.text[len - 1] = '\0';
        return true;
    }
    if (body[1] != static_cast<uint8_t>(Kind::Probe) || len != 2 + kNonceBytes) return false;
    command.kind = Kind::Probe;
    constexpr char start[] = "#if KCD2MP_EdgeProbe then KCD2MP_EdgeProbe(\"";
    constexpr char finish[] = "\") end";
    constexpr char hex[] = "0123456789abcdef";
    char* cursor = command.text.data();
    std::memcpy(cursor, start, sizeof(start) - 1);
    cursor += sizeof(start) - 1;
    for (size_t i = 0; i < kNonceBytes; ++i) {
        const uint8_t octet = body[i + 2];
        *cursor++ = hex[octet >> 4];
        *cursor++ = hex[octet & 0xf];
    }
    std::memcpy(cursor, finish, sizeof(finish));
    return true;
}

// WO-216's System.ExecuteCommand anchor: bSilent and bDefer are both zero,
// then the function calls the vtable displacement we read below. A future
// build must produce one checked match to use this in-process door.
inline size_t execute_slot(const uint8_t* code, size_t len) {
    if (!code) return 0;
    constexpr uint8_t pattern[] = {0x45, 0x33, 0xC9, 0x48, 0x8B, 0x54, 0x24, 0x48, 0x45, 0x33, 0xC0, 0x48, 0x8B, 0x01, 0xFF, 0x90};
    size_t found = 0;
    for (size_t i = 0; i + sizeof(pattern) + sizeof(int32_t) <= len; ++i) {
        if (std::memcmp(code + i, pattern, sizeof(pattern)) != 0) continue;
        int32_t slot = 0;
        std::memcpy(&slot, code + i + sizeof(pattern), sizeof(slot));
        if (found || slot <= 0 || slot > 0x400 || (slot & 7)) return 0;
        found = static_cast<size_t>(slot);
    }
    return found;
}

} // namespace kcdmp::edgeconsole
