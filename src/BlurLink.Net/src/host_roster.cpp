#include "blurlink/host_roster.h"

#include <algorithm>

#include "blurlink/config.h"  // Ipv4ToString

namespace blurlink {

AddResult HostRoster::Observe(const AnnouncePacket& p, std::int64_t now_ms, std::string& note) {
  std::lock_guard<std::mutex> lock(mutex_);
  note.clear();

  // Same player re-announcing (or coming back after expiry): refresh in place.
  for (auto& e : players_) {
    if (e.overlay == p.overlay) {
      e.lan = p.lan;
      e.blur_src_port = p.blur_src_port;
      e.last_seen_ms = now_ms;
      e.in_filter = true;
      return AddResult::Refreshed;
    }
  }

  // A *live* player on the same (LAN address, Blur source port) would make
  // replies ambiguous. Only in-filter entries count: an expired player must not
  // block a newcomer.
  for (const auto& e : players_) {
    if (e.in_filter && e.lan == p.lan && e.blur_src_port == p.blur_src_port) {
      ++collisions_;
      note = "another live player already uses " + Ipv4ToString(p.lan) + ":" +
             std::to_string(p.blur_src_port) + " - refusing to guess which reply is whose";
      return AddResult::RefusedCollision;
    }
  }

  if (players_.size() >= kHostMaxPlayers) {
    note = "player cap reached (" + std::to_string(kHostMaxPlayers) + ")";
    return AddResult::RefusedCap;
  }

  HostPlayer e{};
  e.overlay = p.overlay;
  e.lan = p.lan;
  e.blur_src_port = p.blur_src_port;
  e.first_seen_ms = now_ms;
  e.last_seen_ms = now_ms;
  e.in_filter = true;
  players_.push_back(e);
  return AddResult::Added;
}

void HostRoster::Expire(std::int64_t now_ms) {
  std::lock_guard<std::mutex> lock(mutex_);
  for (auto& e : players_) {
    if (now_ms - e.last_seen_ms > kHostPlayerExpirySeconds * 1000) {
      e.in_filter = false;
    }
  }
}

bool HostRoster::NoteForward(const std::array<std::uint8_t, 4>& lan, std::int64_t now_ms) {
  std::lock_guard<std::mutex> lock(mutex_);
  bool found = false;
  for (auto& e : players_) {
    if (e.lan == lan) {
      ++e.forwards_heard;
      e.last_seen_ms = now_ms;
      found = true;
    }
  }
  return found;
}

bool HostRoster::NoteReplyForwarded(const std::array<std::uint8_t, 4>& lan,
                                    std::uint16_t blur_dst_port) {
  std::lock_guard<std::mutex> lock(mutex_);
  for (auto& e : players_) {
    if (e.lan == lan && e.blur_src_port == blur_dst_port) {
      ++e.replies_forwarded;
      return true;
    }
  }
  return false;
}

bool HostRoster::Revoke(const std::array<std::uint8_t, 4>& overlay) {
  std::lock_guard<std::mutex> lock(mutex_);
  const auto before = players_.size();
  players_.erase(std::remove_if(players_.begin(), players_.end(),
                                [&](const HostPlayer& e) { return e.overlay == overlay; }),
                 players_.end());
  return players_.size() != before;
}

std::vector<HostPlayer> HostRoster::Players() const {
  std::lock_guard<std::mutex> lock(mutex_);
  return players_;
}

std::vector<std::array<std::uint8_t, 4>> HostRoster::FilterAddresses() const {
  std::lock_guard<std::mutex> lock(mutex_);
  std::vector<std::array<std::uint8_t, 4>> out;
  for (const auto& e : players_) {
    if (!e.in_filter) continue;
    if (std::find(out.begin(), out.end(), e.lan) == out.end()) out.push_back(e.lan);
  }
  return out;
}

int HostRoster::CountCollisions() const {
  std::lock_guard<std::mutex> lock(mutex_);
  return collisions_;
}

}  // namespace blurlink
