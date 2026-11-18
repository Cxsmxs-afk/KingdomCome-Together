// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
#include "dialog_vote_rules.h"

#include <algorithm>
#include <cstdio>

int wo203_conv_tests(int* passed) {
    using namespace kcdmp::dialogvote;
    int ok = 0, failed = 0;
    const auto check = [&](bool pass, const char* caseName) {
        if (pass) ++ok;
        else { ++failed; std::printf("Confirmed dialog vote failed: %s\n", caseName); }
    };

    std::array<uint8_t, kBytes> bytes{kOp, 3, kTyped, 0x78, 0x56, 0x34, 0x12};
    std::fill(bytes.begin() + 7, bytes.end(), 'b');
    Request request;
    check(decode(bytes.data(), bytes.size(), request) && request.index == 3 && request.snapshot == 0x12345678 &&
          request.token[0] == 'b' && request.token[32] == 0, "typed vote carries exact HUD capture and opaque token");
    check(matches(request, 0x12345678, 4, true), "same still-current HUD accepts the selected row");
    check(!matches(request, 0x12345679, 4, true) &&
          !matches(request, 0x12345678, 4, false), "retired snapshot and different HUD both refuse");
    check(!matches(request, 0x12345678, 3, true), "captured row cannot be fabricated");
    bytes[1] = kClear;
    check(decode(bytes.data(), bytes.size(), request) && matches(request, 0x12345678, 1, true) &&
          !matches(request, 0x12345678, 0, true), "clear still needs a current open set of rows");
    bytes[7] = '"';
    check(!decode(bytes.data(), bytes.size(), request), "staged token cannot alter the fixed Lua call");
    bytes[7] = 'b';
    bytes[1] = 8;
    check(!decode(bytes.data(), bytes.size(), request), "unsupported row index rejected");
    bytes[1] = 0;
    check(!decode(bytes.data(), bytes.size() - 1, request), "truncated token rejected");
    bytes[2] = 0;
    check(!typed(bytes.data(), 3), "legacy mark remains a distinct wire format");
    std::array<uint8_t, 3 + 255> longestLegacy{kOp, 3, 255};
    check(!typed(longestLegacy.data(), longestLegacy.size()), "typed marker does not consume a legal legacy name");

    if (passed) *passed = ok;
    return failed;
}
