// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
#pragma once

#include <cstddef>
#include <cstdint>
#include <string_view>
#include <unordered_map>
#include <vector>

namespace kcdmp::holders {

enum class Holder : uint8_t {
    None = 0, Writer, Driver, Seat, Activity, Fall, Mount, OneShot,
    Route, Respawn, Leash, Unstuck, Figure, Cart, Probe, Escort, Count
};
enum class Claim { Available, Held, Release };

struct OwnerStats {
    uint64_t violations = 0;
    uint64_t checks = 0;
    uint64_t handovers = 0;
    uint64_t denied_sets = 0;
};

struct Scope { uint64_t lifecycle = 0, connection = 0; };

struct Selection {
    Holder previous = Holder::None;
    bool selected = false;
    bool changed = false;
    double heldSeconds = 0;
    uint32_t bodyHandovers = 0;
};

// Decisions and accounting share this table. The integration serializes its
// methods, while the physical engine call runs outside the lock with a permit.
class Table {
    struct Body {
        Holder holder = Holder::None;
        double since = 0;
        uint32_t handovers = 0;
        uint64_t selection = 0;
        uint64_t lease = 0;
        Holder leaseHolder = Holder::None;
        Claim leaseClaim = Claim::Available;
        std::string_view leasePath;
        uint64_t writeFrame = 0;
        uint64_t writeSelection = 0;
        Holder writeHolder = Holder::None;
        std::string_view writePath;
        bool transient = false;
        bool retired = false;
        bool engineActive = false;
        bool draining = false;
    };
    std::unordered_map<uint32_t, Body> bodies_;
    std::vector<uint32_t> transient_;
    OwnerStats stats_{};
    Scope scope_{};
    uint64_t frame_ = 0;
    uint64_t lease_ = 0;

    void remember_transient(uint32_t eid, Body& body) {
        if (body.transient) return;
        body.transient = true;
        transient_.push_back(eid);
    }

    static bool valid(Holder who) { return who > Holder::None && who < Holder::Count; }
    static bool engine_request(Holder who) {
        return who == Holder::Driver || who == Holder::Route || who == Holder::Probe || who == Holder::Escort;
    }

    static bool conflicts(const Body& body, Holder who, std::string_view path, uint64_t frame) {
        if (body.writeHolder == Holder::None || body.writeFrame != frame ||
            (body.writeHolder == who && body.writePath == path))
            return false;
        // An explicit owner transition permits a different holder to start
        // after its predecessor finished. Namesakes still cannot double-write.
        return body.holder != who || body.writeHolder == who || body.writeSelection == body.selection;
    }

    uint64_t deny() {
        ++stats_.denied_sets;
        return 0;
    }

public:
    void synchronize(Scope scope, uint64_t frame) {
        if (scope.lifecycle != scope_.lifecycle || scope.connection != scope_.connection) {
            scope_ = scope;
            transient_.clear();
            for (auto it = bodies_.begin(); it != bodies_.end();) {
                if (!it->second.lease && !engine_request(it->second.holder)) it = bodies_.erase(it);
                else {
                    it->second.retired = true;
                    it->second.transient = false;
                    ++it;
                }
            }
        }
        if (frame <= frame_) return;
        frame_ = frame;
        size_t retained = 0;
        for (uint32_t eid : transient_) {
            const auto it = bodies_.find(eid);
            if (it == bodies_.end()) continue;
            auto& body = it->second;
            if (body.holder != Holder::None) body.transient = false;
            else if (!body.lease) bodies_.erase(it);
            else transient_[retained++] = eid;
        }
        transient_.resize(retained);
    }

    Selection select(uint32_t eid, Holder who, double now) {
        if (!eid || !valid(who)) return {};
        auto& body = bodies_[eid];
        const Holder was = body.holder;
        if (body.retired || body.draining) return {was};
        if (was == who) return {was, true};
        if (body.lease || body.engineActive) return {was};
        const double seconds = now - body.since;
        body.holder = who;
        body.since = now;
        ++body.selection;
        if (was != Holder::None) {
            ++body.handovers;
            ++stats_.handovers;
        }
        return {was, true, true, seconds, body.handovers};
    }

    Holder owner(uint32_t eid) const {
        const auto it = bodies_.find(eid);
        return it == bodies_.end() ? Holder::None : it->second.holder;
    }

    bool forget(uint32_t eid) {
        const auto it = bodies_.find(eid);
        if (it == bodies_.end()) return true;
        auto& body = it->second;
        if (body.lease || body.engineActive || body.draining) return false;
        if (body.writeHolder == Holder::None || body.writeFrame != frame_) {
            bodies_.erase(it);
        } else {
            body.holder = Holder::None;
            remember_transient(eid, body);
        }
        return true;
    }

    bool begin_drain(uint32_t eid, Holder holder) {
        const auto it = bodies_.find(eid);
        if (it == bodies_.end() || it->second.holder != holder || !engine_request(holder)) return false;
        it->second.draining = true;
        return true;
    }

    bool engine_stopped(uint32_t eid, Holder holder) {
        const auto it = bodies_.find(eid);
        if (it == bodies_.end()) return true;
        auto& body = it->second;
        if (body.lease || body.holder != holder || !engine_request(holder)) return false;
        body.engineActive = false;
        body.draining = false;
        if (body.retired) bodies_.erase(it);
        return true;
    }

    uint64_t acquire(uint32_t eid, Holder who, std::string_view path, Claim claim) {
        ++stats_.checks;
        if (!eid || !valid(who) || path.empty() || !frame_) return deny();
        auto it = bodies_.find(eid);
        if (it == bodies_.end()) {
            if (claim != Claim::Available) return deny();
            it = bodies_.try_emplace(eid).first;
            remember_transient(eid, it->second);
        }
        auto& body = it->second;
        if (((body.retired || body.draining) && claim != Claim::Release) || body.lease ||
            (body.holder != who && (body.holder != Holder::None || claim != Claim::Available)) ||
            conflicts(body, who, path, frame_))
            return deny();
        do { ++lease_; } while (!lease_);
        body.lease = lease_;
        body.leaseHolder = who;
        body.leasePath = path;
        body.leaseClaim = claim;
        return lease_;
    }

    bool wrote(uint32_t eid, Holder who, std::string_view path, uint64_t lease) {
        const auto it = bodies_.find(eid);
        if (it == bodies_.end() || !lease || it->second.lease != lease ||
            it->second.leaseHolder != who || it->second.leasePath != path ||
            conflicts(it->second, who, path, frame_)) {
            ++stats_.violations;
            return false;
        }
        auto& body = it->second;
        body.writeHolder = who;
        body.writePath = path;
        body.writeFrame = frame_;
        body.writeSelection = body.selection;
        if (body.leaseClaim == Claim::Held && engine_request(who)) body.engineActive = true;
        return true;
    }

    void finish(uint32_t eid, uint64_t lease) {
        if (!lease) return;
        const auto it = bodies_.find(eid);
        if (it == bodies_.end() || it->second.lease != lease) return;
        if (it->second.retired && !engine_request(it->second.holder)) bodies_.erase(it);
        else it->second.lease = 0;
    }

    OwnerStats stats() const { return stats_; }
    size_t size() const { return bodies_.size(); }
};

} // namespace kcdmp::holders
