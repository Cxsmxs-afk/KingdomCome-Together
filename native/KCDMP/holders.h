// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
#pragma once
// Who holds a copy's body (WO-221 C.3, mp_holders; docs/DESIGN-AUDIT.md 4.10: "one holder at a time, in one table").
//
// One table, one holder per body at any moment: the driver (the engine moves it by our movement request), the writer (the stream
// places it), a seat, an activity, a fall, a mount, a one-shot. Whoever is not the holder does not set the body's position; a
// hand-over is one entry changing, in one frame, and the new holder starts from where the body stands.
//
// The claims are still found where they always were (the seat rule, the posture rule, the activity hold, the knockdown, a swing's
// hold, the stream's own dead / unconscious / carried bits, a parent); what this replaces is the six separate "who yields" rules of
// the position writer: decide() is the one rule, in one order.
#include "holder_rules.h"

namespace kcdmp::holders {

const char* name(Holder h);
constexpr char kStreamSetter[] = "npc-drive-stream";

struct Claims {
    bool fall = false;       // dead, unconscious or carried (the stream's bits), or lying for its host NPC's knockdown: the engine's ragdoll / the carrier
    bool mount = false;      // a parent holds it: a horse, a cart's seat
    bool oneShot = false;    // a swing's hold runs: the one-shot's own motion
    bool seat = false;       // its seat holds it (the seat rule's word)
    bool activity = false;   // an activity or a posture holds it
    bool probe = false;      // a research probe owns it (the bench only)
};

// The one rule (pure): the first claim in this order holds the body; with none, the stream does (the writer, or the driver when the
// body walks and can be driven -- the driver asks the table and takes the body from the writer in the writer's own loop).
inline Holder decide(const Claims& c) {
    if (c.fall) return Holder::Fall;
    if (c.mount) return Holder::Mount;
    if (c.oneShot) return Holder::OneShot;
    if (c.probe) return Holder::Probe;
    if (c.seat) return Holder::Seat;
    if (c.activity) return Holder::Activity;
    return Holder::Writer;
}

bool on();                                                          // mp_holders (the bench's twin: the writer's own six rules, as 0.65.0)
// Publish a deliberate handover after the previous engine request has ended.
// A reservation already executing for this body blocks the next owner.
Holder set(uint32_t eid, const char* npc, Holder h, double now);
bool handover(uint32_t eid, const char* npc, Holder h, double now);
Holder of(uint32_t eid);                                            // None: not a bound copy
bool forget(uint32_t eid);                                         // after an unbind, retain this frame's observed setter
void engine_stopped(uint32_t eid, Holder holder);                   // game confirmed cancellation or the verified actor vanished
OwnerStats owner_stats();

class [[nodiscard]] Permit {
    uint32_t eid_;
    Holder holder_;
    const char* path_;
    uint64_t lease_;
    bool written_ = false;

    Permit(uint32_t eid, Holder holder, const char* path, uint64_t lease)
        : eid_(eid), holder_(holder), path_(path), lease_(lease) {}
    friend Permit may_place(uint32_t, Holder, const char*, Claim);

public:
    Permit(const Permit&) = delete;
    Permit& operator=(const Permit&) = delete;
    Permit(Permit&& other) noexcept;
    Permit& operator=(Permit&&) = delete;
    ~Permit();

    explicit operator bool() const { return lease_ != 0; }
    // Call after the first successful physical mutation, before any fallback.
    void wrote();
};

// Every lowest-level body placement asks with a verified entity ID and a
// static string identifying the actual path. Claim::Held requires a published
// owner, for asynchronous/persistent movement requests. Holds are scoped
// across the call, including timeout or reentrant engine callbacks.
Permit may_place(uint32_t eid, Holder who, const char* path, Claim claim = Claim::Available);

} // namespace kcdmp::holders
