// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
#include "t3_recovery_stats_rules.h"

#include <cstdio>
#include <string_view>

int t3_recovery_stats_tests(int* passed) {
    using namespace kcdmp::t3recovery;
    using kcdmp::loadsequence::write_le;
    int ok = 0, failed = 0;
    const auto check = [&](bool success, const char* label) {
        if (success) ++ok;
        else { ++failed; std::printf("Recovery diagnostics failed: %s\n", label); }
    };

    constexpr uint64_t gen = 0x102030405ULL, run = 0x60504030201ULL;
    constexpr uint32_t world = 0x77665544;
    std::array<uint8_t, kRequestBytes> bytes{kOp, kVersion};
    write_le(bytes.data() + 2, gen, 8);
    write_le(bytes.data() + 10, world, 4);
    write_le(bytes.data() + 14, run, 8);
    const auto example = [](size_t i) -> uint64_t {
        return i >= kReconnectAt + 1 && i <= kReconnectAt + 3 ? i % 2 : i + 11;
    };
    for (size_t i = 0; i < kFields; ++i) write_le(bytes.data() + kHeaderBytes + 8 * i, example(i), 8);
    Report received;
    check(decode(bytes.data(), bytes.size(), received) && received.generation == gen && received.world == world && received.run == run,
          "wire has exact versioned local generation, authoritative world and recovery run");
    check(kRequestBytes == 254 && kAckBytes == 253 && kFields == 29, "mirrored response fits the existing native reply budget");
    for (size_t i = 0; i < kFields; ++i)
        check(received.fields[i] == example(i), "little-endian field identity and value are stable");
    check(std::string_view(kNames[0]) == "digest_mismatch_body" && std::string_view(kNames[5]) == "digest_mismatch_weather" &&
          std::string_view(kNames[6]) == "resync_body" && std::string_view(kNames[11]) == "resync_weather" &&
          std::string_view(kNames[kReconnectAt]) == "reconnect_ms" && std::string_view(kNames[kCheckedAt - 1]) == "reconnect_reload" &&
          std::string_view(kNames[kCheckedAt]) == "digest_checked_body" && std::string_view(kNames[kFailedAt]) == "resync_failed_body" &&
          std::string_view(kNames[kFields - 1]) == "resync_failed_weather",
          "six areas, reconnect, coverage and failed-repair names match the typed layout");
    bool importsOwner = false;
    for (const char* name : kNames) importsOwner |= std::string_view(name).find("owner_") == 0;
    check(!importsOwner, "actual body owner audit cannot be populated by the Agent mirror");

    auto malformed = bytes;
    malformed[1] = 2;
    Report report = received;
    check(!decode(malformed.data(), malformed.size(), report) && report.fields == received.fields, "unsupported version cannot publish a partial report");
    check(!decode(bytes.data(), bytes.size() - 1, report) && !decode(nullptr, bytes.size(), report), "truncated and missing reports rejected");
    malformed = bytes;
    write_le(malformed.data() + 14, 0, 8);
    check(!decode(malformed.data(), malformed.size(), report), "unnamed run cannot be recovery evidence");
    malformed = bytes;
    write_le(malformed.data() + kHeaderBytes + 8 * kCheckedAt, 0, 8);
    check(!decode(malformed.data(), malformed.size(), report), "mismatches cannot exist without comparison coverage");
    malformed = bytes;
    write_le(malformed.data() + kHeaderBytes + 8 * (kReconnectAt + 1), 2, 8);
    check(!decode(malformed.data(), malformed.size(), report), "reconnect outcome bits cannot masquerade as counts");
    malformed = bytes;
    write_le(malformed.data() + kHeaderBytes + 8 * kFailedAt, UINT64_MAX, 8);
    check(!decode(malformed.data(), malformed.size(), report), "invalid signed-counter representation cannot reach logs");

    Mirror mirror;
    const Receiver joiner{gen, 19, 2, true}, newer{gen + 1, 19, 2, true}, nextPipe{gen, 20, 2, true};
    check(!mirror.accept({gen, 0, 2, true}, received) && !mirror.accept({gen, 19, 0, true}, received) &&
          !mirror.accept({gen, 19, 2, false}, received), "unbound transport, unknown role and loading scope cannot publish");
    check(!mirror.accept(newer, received), "old generation is rejected even when current pipe and host role agree");
    check(mirror.accept(joiner, received), "first accepted report can attach to an already-running world");
    report = received;
    ++report.fields[2];
    ++report.fields[6 + 2];
    ++report.fields[kCheckedAt + 2];
    check(mirror.accept(joiner, report) && mirror.accept(joiner, report), "specific corpse repair and exact retry remain valid");
    check(!mirror.accept(joiner, received), "reordered digest counters cannot erase a confirmed repair");
    report.fields[kReconnectAt] = 0;
    report.fields[kReconnectAt + 4] = 0;
    check(mirror.accept(joiner, report), "a fresh attempt may reset reconnect gauges without inventing a reload");
    check(mirror.accept(nextPipe, received), "same-world reconnect does not require old DLL counters");
    report = received;
    report.run += 1;
    report.fields.fill(0);
    check(mirror.accept(nextPipe, report), "fresh managed run starts with zero coverage without inheriting older proofs");

    if (passed) *passed = ok;
    return failed;
}
