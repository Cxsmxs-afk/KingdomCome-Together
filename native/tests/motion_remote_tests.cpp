// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
#include "motion_remote_rules.h"

#include <cstdio>

int motion_remote_tests(int* passed) {
    using namespace kcdmp::motion::remote;
    using kcdmp::holders::Holder;
    using Disposition = JumpDisposition;
    int ok = 0, failed = 0;
    const auto check = [&](bool pass, const char* name) {
        if (pass) ++ok;
        else { ++failed; std::printf("Remote motion failed: %s\n", name); }
    };

    int entity = 0, actor = 0, other = 0;
    const JumpScope scheduled{&entity, &actor, 3, 5, 7};
    const double deadline = 1.0 + kJumpHandoverS;
    check(jump_after_handover(scheduled, scheduled, deadline, 1.0, Holder::Driver) == Disposition::Wait,
          "an active movement request remains the owner and never receives the jump");
    check(jump_after_handover(scheduled, scheduled, deadline, 1.1, Holder::Writer) == Disposition::Submit,
          "the old guarded Writer can pass the queued jump to the verified expansion");
    check(jump_after_handover(scheduled, scheduled, deadline, 1.1, Holder::Figure) == Disposition::Submit,
          "legacy Figure writer remains an eligible source owner");
    check(jump_after_handover(scheduled, scheduled, deadline, deadline, Holder::Writer) == Disposition::Expired,
          "same body and eligible holder cannot revive an expired jump");
    check(jump_after_handover(scheduled, scheduled, deadline, 1.1, Holder::OneShot) == Disposition::Expired,
          "an already accepted jump or swing cannot take a second input via the same path");
    check(jump_after_handover(scheduled, scheduled, deadline, 1.1, Holder::Mount) == Disposition::Expired &&
          jump_after_handover(scheduled, scheduled, deadline, 1.1, Holder::Fall) == Disposition::Expired,
          "a mount or ragdoll keeps its independent owner");
    auto current = scheduled;
    current.entity = &other;
    check(jump_after_handover(scheduled, current, deadline, 1.1, Holder::Writer) == Disposition::Expired, "recycled entity ID is rejected");
    current = scheduled;
    current.actor = &other;
    check(jump_after_handover(scheduled, current, deadline, 1.1, Holder::Writer) == Disposition::Expired, "replaced actor is rejected");
    current = scheduled;
    current.connection++;
    check(jump_after_handover(scheduled, current, deadline, 1.1, Holder::Writer) == Disposition::Expired, "a fresh pipe rejects the old receipt");
    current = scheduled;
    current.lifecycle++;
    check(jump_after_handover(scheduled, current, deadline, 1.1, Holder::Writer) == Disposition::Expired, "a world transition rejects the old receipt");
    current = scheduled;
    current.life++;
    check(jump_after_handover(scheduled, current, deadline, 1.1, Holder::Writer) == Disposition::Expired, "a life transition rejects the old receipt");

    if (passed) *passed = ok;
    return failed;
}
