// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
#include "edge_console_rules.h"

#include <cstdio>
#include <string_view>

using namespace kcdmp::edgeconsole;

int edge_console_tests(int* passed) {
    int pass = 0, fail = 0;
#define CHECK(condition) do { \
    if (condition) ++pass; \
    else { ++fail; std::printf("FAIL edge console line %d: %s\n", __LINE__, #condition); } \
} while (0)
    Command cmd{};
    const uint8_t probe[] = {kOp, 0, 0, 0x10, 0x20, 0x39, 0x7f, 0x80, 0xa5, 0xff};
    CHECK(decode(probe, sizeof(probe), cmd) && cmd.kind == Kind::Probe);
    CHECK(std::string_view(cmd.text.data()) == "#if KCD2MP_EdgeProbe then KCD2MP_EdgeProbe(\"001020397f80a5ff\") end");
    CHECK(!decode(probe, sizeof(probe) - 1, cmd));
    CHECK(!decode(nullptr, sizeof(probe), cmd));

    const uint8_t lua[] = {kOp, 1, 'x', ' ', '=', ' ', '"', 0xc3, 0xa4, '"', '\n', 'y', '=', '2'};
    CHECK(decode(lua, sizeof(lua), cmd) && cmd.kind == Kind::Lua);
    CHECK(std::string_view(cmd.text.data()) == "#x = \"\xc3\xa4\"\ny=2");
    const uint8_t embeddedNul[] = {kOp, 1, 'x', '\0', 'y'};
    const uint8_t unknown[] = {kOp, 2, 'x'};
    const uint8_t wrongOp[] = {253, 1, 'x'};
    CHECK(!decode(embeddedNul, sizeof(embeddedNul), cmd));
    CHECK(!decode(unknown, sizeof(unknown), cmd));
    CHECK(!decode(wrongOp, sizeof(wrongOp), cmd));
    CHECK(!decode(lua, 2, cmd));
    std::array<uint8_t, kMaxRequestBytes + 1> large;
    large.fill('a');
    large[0] = kOp;
    large[1] = 1;
    CHECK(decode(large.data(), kMaxRequestBytes, cmd));
    CHECK(std::strlen(cmd.text.data()) == kMaxRequestBytes - 1 && cmd.text.back() == '\0');
    CHECK(!decode(large.data(), large.size(), cmd));

    // Synthetic copy of the resolver's checked instruction shape. A changed
    // displacement alignment, missing operand or ambiguous match fails closed.
    constexpr uint8_t anchoredCall[] = {
        0x45, 0x33, 0xC9, 0x48, 0x8B, 0x54, 0x24, 0x48, 0x45, 0x33, 0xC0, 0x48, 0x8B, 0x01, 0xFF, 0x90,
        0xB0, 0, 0, 0,
    };
    constexpr size_t bytes = sizeof(anchoredCall);
    std::array<uint8_t, 2 * bytes> function{};
    std::memcpy(function.data(), anchoredCall, bytes);
    CHECK(execute_slot(function.data(), function.size()) == 0xB0);
    CHECK(execute_slot(function.data(), bytes - 1) == 0);
    CHECK(execute_slot(nullptr, bytes) == 0);
    ++function[bytes - 4];
    CHECK(execute_slot(function.data(), bytes) == 0);
    std::memcpy(function.data(), anchoredCall, bytes);
    std::memcpy(function.data() + bytes, anchoredCall, bytes);
    CHECK(execute_slot(function.data(), function.size()) == 0);
    ++function[2];
    CHECK(execute_slot(function.data(), bytes) == 0);
    *passed = pass;
    return fail;
#undef CHECK
}
