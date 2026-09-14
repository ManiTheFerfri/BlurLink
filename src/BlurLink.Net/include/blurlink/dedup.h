#pragma once

// Reinjection-loop defense-in-depth: bounded identity cache of recently
// forwarded discovery packets. A packet whose identity
// (src ip/port, dst ip/port, IP ID, UDP length) was forwarded within the TTL
// is treated as an echo of our own reinjection: reinject per preserve-setting
// but do NOT clone again. Genuine Blur retransmits carry fresh IP IDs, so
// they still forward (subject to the rate limiter).
//
// Primary loop immunity is structural (see bridge.h): the filter matches only
// broadcast/multicast destinations, so unicast clones can never re-match, and
// per WinDivert docs a handle does not re-capture its own reinjections
// (only *other lower-priority* handles can). This cache is the backstop.

#include <array>
#include <chrono>
#include <cstdint>
#include <deque>
#include <mutex>
#include <unordered_set>

namespace blurlink {

struct PacketIdentity {
  std::array<std::uint8_t, 4> src_ip{};
  std::array<std::uint8_t, 4> dst_ip{};
  std::uint16_t src_port = 0;
  std::uint16_t dst_port = 0;
  std::uint16_t ip_id = 0;
  std::uint16_t udp_len = 0;

  bool operator==(const PacketIdentity& o) const {
    return src_ip == o.src_ip && dst_ip == o.dst_ip && src_port == o.src_port &&
           dst_port == o.dst_port && ip_id == o.ip_id && udp_len == o.udp_len;
  }
};

struct PacketIdentityHash {
  std::size_t operator()(const PacketIdentity& k) const noexcept {
    std::size_t h = 1469598103934665603ull;
    auto mix = [&](std::uint64_t v) {
      h ^= static_cast<std::size_t>(v);
      h *= 1099511628211ull;
    };
    for (auto b : k.src_ip) mix(b);
    for (auto b : k.dst_ip) mix(b);
    mix(k.src_port);
    mix(k.dst_port);
    mix(k.ip_id);
    mix(k.udp_len);
    return h;
  }
};

class DedupCache {
 public:
  explicit DedupCache(std::size_t capacity = 1024,
                      std::chrono::seconds ttl = std::chrono::seconds(2))
      : capacity_(capacity), ttl_(ttl) {}

  // Returns true if this identity was recently recorded (caller should skip
  // cloning). Otherwise records it and returns false.
  bool CheckAndRecord(const PacketIdentity& id) {
    std::lock_guard<std::mutex> lock(mutex_);
    EvictExpired(lock_token{});
    if (set_.find(id) != set_.end()) {
      ++echo_hits_;
      return true;
    }
    if (order_.size() >= capacity_) {
      set_.erase(order_.front().id);
      order_.pop_front();
    }
    order_.push_back(Entry{id, std::chrono::steady_clock::now()});
    set_.insert(id);
    return false;
  }

  int echo_hits() const {
    std::lock_guard<std::mutex> lock(mutex_);
    return echo_hits_;
  }

 private:
  struct lock_token {};
  struct Entry {
    PacketIdentity id;
    std::chrono::steady_clock::time_point at;
  };
  void EvictExpired(lock_token) {
    auto now = std::chrono::steady_clock::now();
    while (!order_.empty() && now - order_.front().at > ttl_) {
      set_.erase(order_.front().id);
      order_.pop_front();
    }
  }

  std::size_t capacity_;
  std::chrono::seconds ttl_;
  std::deque<Entry> order_;
  std::unordered_set<PacketIdentity, PacketIdentityHash> set_;
  int echo_hits_ = 0;
  mutable std::mutex mutex_;
};

}  // namespace blurlink
