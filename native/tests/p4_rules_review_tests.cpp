// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// Independent engine-free review of the boundaries that authorize a P4 placement.
#include "p4_rules.h"
#include <array>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <limits>

namespace {
int failures = 0, successes = 0;
#define P4CHECK(condition, message) do { if (condition) ++successes; else { ++failures; std::printf("FAIL %s:%d %s\n", __FILE__, __LINE__, message); } } while (false)
}

int p4_rules_review_tests(int* passed) {
    using namespace kcdmp::p4rules;
    failures = successes = 0;
    std::array<uint8_t, 19> wire{};
    wire.front() = static_cast<uint8_t>(Source::Leash);
    put64(wire.data() + 1, 12000); put64(wire.data() + 9, 7);
    Context c;
    P4CHECK(context(wire.data(), 17, &c) && c.source == Source::Leash && c.deadline == 12000 && c.generation == 7, "context keeps original absolute deadline and generation");
    for (size_t n = 0; n < wire.size(); ++n)
        if (n != 17) P4CHECK(!context(wire.data(), n, &c), "context rejects every truncated or trailing shape");
    P4CHECK(!context(nullptr, 17, &c) && !context(wire.data(), 17, nullptr), "context rejects null storage");
    wire[0] = 9; P4CHECK(!context(wire.data(), 17, &c), "unknown source enum is refused");
    for (unsigned source = 1; source <= 8; ++source) {
        c = {static_cast<Source>(source), 12000, 7};
        P4CHECK(fresh(c, 10000, 7, true), "each named source preserves its challenge start");
        P4CHECK(fresh(c, 11999, 7, true) && fresh(c, 12000, 7, true), "age 1999 and exactly 2000ms remain within proof");
        P4CHECK(!fresh(c, 12001, 7, true) && !fresh(c, 9999, 7, true), "expired and more than two seconds future deadlines are refused");
        P4CHECK(!fresh(c, 11000, 8, true) && !fresh(c, 11000, 7, false), "new world and missing gameplay invalidate a future deadline");
        P4CHECK(!fresh(c, 11000, 7, true, 10000) && fresh(c, 11000, 7, true, 9999), "challenge start equal to life edge refused; edge+1 accepted");
        P4CHECK(!fresh(c, 11000, 7, true, 10500), "old-life proof cannot survive merely because its deadline lies in the future");
        P4CHECK(std::strcmp(source_name(c.source), "unknown") != 0, "every accepted source has provenance text");
    }
    P4CHECK(!fresh({Source::Unknown,12000,7},11000,7,true) && !fresh({Source::Join,12000,0},11000,0,true), "unknown source or generation cannot authorize placement");
    P4CHECK(!fresh({Source::Join,1999,7},1000,7,true,1), "deadline below freshness window never underflows the computed source start");
    P4CHECK(std::strcmp(source_name(static_cast<Source>(255)), "unknown") == 0, "out-of-range source text stays bounded");

    const float origin[3] = {2,3,62}, target[3] = {2,3,61}, ground[3] = {2,3,60};
    Drops drop;
    P4CHECK(ground_ok(origin,target,ground,&drop) && drop.source == 2 && drop.placement == 1, "original source drop exactly two metres is accepted");
    const float highDeath[3] = {2,3,76.4f}, snappedTarget[3] = {2,3,60};
    P4CHECK(!ground_ok(highDeath,snappedTarget,ground,&drop) && drop.source > 16 && drop.placement == 0, "snapping target does not erase the original lethal source drop");
    const float over[3] = {2,3,62.001f}, below[3] = {2,3,57.999f}, edgeBelow[3] = {2,3,58};
    P4CHECK(!ground_ok(over,target,ground,&drop), "source drop above two metres is refused");
    P4CHECK(!ground_ok(ground,over,ground,&drop) && !ground_ok(ground,below,ground,&drop), "both upper and lower placement corrections above two metres are refused");
    P4CHECK(ground_ok(ground,edgeBelow,ground,&drop) && drop.placement == 0 && drop.placementDz == -2, "upward target correction boundary is retained as signed evidence");
    for (float bad : {std::numeric_limits<float>::quiet_NaN(), std::numeric_limits<float>::infinity(), -std::numeric_limits<float>::infinity()}) {
        const float invalid[3] = {bad,3,60};
        P4CHECK(!ground_ok(invalid,target,ground,&drop) && !ground_ok(origin,invalid,ground,&drop) && !ground_ok(origin,target,invalid,&drop), "nonfinite positions never become safe ground");
    }
    P4CHECK(!ground_ok(nullptr,target,ground,&drop) && !ground_ok(origin,target,ground,nullptr), "missing ground evidence is refused");
    const float vertical[3] = {2,3,90};
    P4CHECK(distance(ground,vertical) == 30, "partner distance includes vertical floors");
    for (unsigned bits = 0; bits < 64; ++bits) {
        const bool ck=(bits&1)!=0, combat=(bits&2)!=0, dk=(bits&4)!=0, dead=(bits&8)!=0, kk=(bits&16)!=0, ko=(bits&32)!=0;
        P4CHECK(state_safe(ck,combat,dk,dead,kk,ko,false) == (ck && dk && kk && !combat && !dead && !ko), "automatic movement requires known-safe combat, dead and knockout states");
        P4CHECK(state_safe(ck,combat,dk,dead,kk,ko,true) == (ck && dk && kk && !dead), "explicit recovery never treats an unknown or still-dead actor as safe");
    }

    Landing landing;
    P4CHECK(!landing.release(0,7,true), "no protection lease means no release operation");
    landing.arm(100,7);
    P4CHECK(!landing.release(200,7,true) && !landing.release(1199,7,true), "first positive ground observation starts a full continuous second");
    P4CHECK(!landing.release(1200,7,false) && !landing.haveGrounded, "an unknown/flying/falling observation resets continuity at the threshold");
    P4CHECK(!landing.release(1300,7,true) && !landing.release(2299,7,true) && landing.release(2300,7,true), "one uninterrupted known second is required after interruption");
    landing = {}; landing.arm(100,7); landing.release(200,7,true); landing.arm(1000,7);
    P4CHECK(landing.began == 100 && !landing.haveGrounded, "repeated placement resets ground proof but never extends original hard protection deadline");
    P4CHECK(!landing.release(20099,7,false) && landing.release(20100,7,false), "unknown ground cannot hold fall suppression beyond twenty seconds");
    landing = {}; landing.arm(200,7);
    P4CHECK(landing.release(201,8,false) && landing.release(199,7,false), "generation change and backward clock terminate the old lease");
    landing.arm(300,8);
    P4CHECK(landing.began == 300 && landing.generation == 8, "new generation gets its own hard protection boundary");

    Recovery recovery;
    P4CHECK(!recovery.active(100,7,0x1234,11), "no recovery proof is active by default");
    recovery.arm(100,7,0x1234,11);
    P4CHECK(recovery.active(100,7,0x1234,11) && recovery.active(5099,7,0x1234,11) && !recovery.active(5100,7,0x1234,11), "known recovery identity lasts five seconds and expires exactly at its boundary");
    P4CHECK(!recovery.active(200,8,0x1234,11) && !recovery.active(200,7,0x5678,11) && !recovery.active(200,7,0x1234,12), "world, soul and WUID changes each invalidate recovery");
    P4CHECK(!recovery.active(200,0,0x1234,11) && !recovery.active(200,7,0,11) && !recovery.active(200,7,0x1234,0), "unknown identity fields never acquire recovery immunity");

    std::array<uint64_t,kFields> values{};
    for (size_t i=0;i<values.size();++i) values[i] = (uint64_t(1)<<40) + i;
    std::array<uint8_t,kStatsBytes+2> encoded{}; encoded.fill(0xCC);
    P4CHECK(kFields == 19 && kStatsBytes == 173 && stats(encoded.data()+1,kStatsBytes,values.data()) == 173, "versioned stats contain nineteen exact TLV records");
    P4CHECK(encoded.front()==0xCC && encoded.back()==0xCC && encoded[1]==1 && encoded[2]==19, "stats stay within caller-provided buffer");
    for(size_t i=0;i<values.size();++i) P4CHECK(encoded[3+9*i]==i && u64(encoded.data()+4+9*i)==values[i], "stats field identity and full 64-bit counter survive serialization");
    const auto before=encoded;
    P4CHECK(stats(encoded.data()+1,kStatsBytes-1,values.data())==0 && encoded==before && stats(nullptr,kStatsBytes,values.data())==0 && stats(encoded.data(),kStatsBytes,nullptr)==0, "short or missing stats storage produces no partial write");
    P4CHECK(metres(-1)==0 && metres(NAN)==0 && metres(INFINITY)==0 && metres(0.01f)==1 && metres(1.01f)==2, "metric conversion refuses nonfinite input and conservatively rounds distances");
    P4CHECK(metres(std::numeric_limits<float>::max())==static_cast<uint64_t>(std::numeric_limits<int64_t>::max()), "metric conversion saturates at signed report counter limit");
    if (passed) *passed = successes;
    return failures;
}
