// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
#pragma once
#include <cstddef>
#include <cstdint>
#include <array>
#include <cstring>
#include <deque>
#include <string>
#include <unordered_map>
#include <utility>
#include <vector>

namespace kcdmp::pipeout {
// Called under the short producer lock, never while physical I/O is running.
// Reliable records stay FIFO. Samples have one newest value per body and cannot
// pass a reliable record that preceded their newest value.
class Outbox {
    struct Sample { std::vector<uint8_t> bytes; uint64_t after = 0; };
    std::deque<std::vector<uint8_t>> reliable;
    std::deque<std::string> order;
    std::unordered_map<std::string, Sample> samples;
    std::unordered_map<std::string, std::array<uint8_t, 5>> npcState;
    size_t bytes = 0;
    uint64_t issued = 0, taken = 0;
public:
    static constexpr size_t kReliableBytes = 8 * 1024 * 1024;
    bool push_reliable(std::vector<uint8_t> frame) {
        if (frame.size() > kReliableBytes - bytes) return false;
        bytes += frame.size(); ++issued;
        reliable.push_back(std::move(frame));
        return true;
    }
    // WO-138 0xA0: flags, x/y/z/yaw, hp, name. All life/KO/draw/engagement
    // and HP changes, first sightings and swing cues are reliable. Only a
    // position with equal state becomes a replaceable sample.
    bool push_wire(std::vector<uint8_t> frame) {
        const auto* p = frame.data(); const size_t len = frame.size();
        std::vector<std::pair<size_t, size_t>> rows;
        size_t at = 4;
        if (len >= 4 && p[0] == 0xA0) {
            for (unsigned i = 0; i < p[3] && at + 22 <= len; ++i) {
                const size_t n = 22u + p[at + 21];
                if (p[at + 21] > 63 || at + n > len) break;
                rows.emplace_back(at, n); at += n;
            }
        }
        if (len < 4 || p[0] != 0xA0 || rows.size() != p[3] || at != len) return push_reliable(std::move(frame));
        for (const auto& row : rows) {
            const uint8_t* r = p + row.first;
            const std::string name(reinterpret_cast<const char*>(r + 22), r[21]);
            const std::array<uint8_t, 5> state{{static_cast<uint8_t>(r[0] & ~uint8_t{8}), r[17], r[18], r[19], r[20]}};
            const auto previous = npcState.find(name);
            const bool reliableRow = previous == npcState.end() || previous->second != state || (r[0] & 8) != 0;
            std::vector<uint8_t> out(4 + row.second);
            out[0] = 0xA0; out[1] = static_cast<uint8_t>((row.second + 1) & 255); out[2] = static_cast<uint8_t>((row.second + 1) >> 8); out[3] = 1;
            std::memcpy(out.data() + 4, r, row.second);
            if (reliableRow) {
                erase_sample(name);
                if (!push_reliable(std::move(out))) return false;
                npcState[name] = state;
            } else push_sample(name, std::move(out));
        }
        return true;
    }
    void erase_sample(const std::string& key) { samples.erase(key); }
    void push_sample(const std::string& key, std::vector<uint8_t> frame) {
        auto it = samples.find(key);
        if (it == samples.end()) { order.push_back(key); samples.emplace(key, Sample{std::move(frame), issued}); }
        else it->second = Sample{std::move(frame), issued};
    }
    bool pop(std::vector<uint8_t>* frame) {
        while (!order.empty() && !samples.count(order.front())) order.pop_front();
        if (!order.empty()) {
            auto it = samples.find(order.front());
            if (it->second.after <= taken) {
                *frame = std::move(it->second.bytes); samples.erase(it); order.pop_front(); return true;
            }
        }
        if (reliable.empty()) return false;
        *frame = std::move(reliable.front()); reliable.pop_front();
        bytes -= frame->size(); ++taken;
        return true;
    }
    bool empty() const { return reliable.empty() && samples.empty(); }
    size_t reliable_bytes() const { return bytes; }
    size_t sample_count() const { return samples.size(); }
    void clear() { reliable.clear(); samples.clear(); npcState.clear(); order.clear(); bytes = 0; issued = taken = 0; }
};
inline bool same_connection(uint64_t queued, uint64_t active, bool connected) { return connected && queued == active; }
} // namespace kcdmp::pipeout
