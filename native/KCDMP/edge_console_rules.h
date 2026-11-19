// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
#pragma once

#include <array>
#include <cstdio>

#include "rp_timeline_action_rules.h"
#include <cstddef>
#include <cstdint>
#include <cstring>

namespace kcdmp::edgeconsole {

constexpr uint8_t kOp = 254;
constexpr size_t kMaxRequestBytes = 1024;
constexpr size_t kNonceBytes = 8;
enum class Kind : uint8_t { Probe = 0, Lua = 1 };
enum class Result : uint8_t { NotEntered = 0, Returned = 1, Uncertain = 2 };
// Main only. This narrow call runs the staged vote for the same HUD snapshot
// that WO-203 just verified. Returning is separate from Lua's applied receipt.
Result present_dialog_vote(const char* token, int index, int current, bool right);


struct Command {
    Kind kind;
    std::array<char, kMaxRequestBytes> text;
};

inline bool ranged_hand_command(const timelineactions::RangedHandRequest& request, const uint8_t item[16], Command& command) {
    using namespace timelineactions;
    if (!item || request.operation != HandOperation::Declare) return false;
    const bool drawn = (request.flags & kHandDrawn) != 0;
    const bool oversized = (request.flags & kHandOversized) != 0;
    char cls[37] = "-";
    if (drawn) {
        std::snprintf(cls, sizeof cls, "%08x-%04x-%04x-%02x%02x-%02x%02x%02x%02x%02x%02x",
            static_cast<unsigned>(read_le(item, 4)), static_cast<unsigned>(read_le(item + 4, 2)),
            static_cast<unsigned>(read_le(item + 6, 2)), unsigned(item[8]), unsigned(item[9]),
            unsigned(item[10]), unsigned(item[11]), unsigned(item[12]), unsigned(item[13]), unsigned(item[14]), unsigned(item[15]));
    }
    const auto& fence = request.fence;
    command.kind = Kind::Lua;
    const int length = std::snprintf(command.text.data(), command.text.size(),
        "#if KCD2MP_W214FigureHand then KCD2MP_W214FigureHand(\"%u\",%s,\"%s\",%s,\"%08X\",\"%08X%016llX%016llX%08X%016llX\",\"%016llX\") end",
        unsigned(request.peer), drawn ? "true" : "false", cls, oversized ? "true" : "false", unsigned(request.incarnation),
        unsigned(request.world), static_cast<unsigned long long>(request.session),
        static_cast<unsigned long long>(fence.peerSerial), unsigned(fence.bodyGeneration),
        static_cast<unsigned long long>(fence.revision), static_cast<unsigned long long>(fence.revision));
    return length > 0 && static_cast<size_t>(length) < command.text.size();
}

// No supplied Lua text. This reaches the existing validated console slot on the game thread.
Result present_ranged_hand(const timelineactions::RangedHandRequest& request, const uint8_t item[16]);

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
