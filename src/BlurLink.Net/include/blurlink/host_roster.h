#pragma once

// Host-mode player roster.
//
// A player is identified by the overlay address they introduced themselves from,
// and matched to replies by (LAN address, Blur source port) — the pair the host's
// Blur actually addresses its replies to.
//
// Two deliberate rules:
//   * An expired player is removed from the *filter* but kept in the roster, so
//     the GUI keeps its forwards-heard counter. Losing that number exactly when a
//     player goes quiet would hide the diagnostic that explains why nothing is
//     being forwarded.
//   * A live player already using the same (LAN address, Blur source port) makes
//     a newcomer's replies ambiguous. The newcomer is refused rather than risking
//     one player receiving another's reply.

#include <array>
#include <cstdint>
#include <mutex>
#include <string>
#include <vector>

#include "blurlink/announce.h"

namespace blurlink {

constexpr std::int64_t kHostPlayerExpirySeconds = 45;

enum class AddResult { Added, Refreshed, RefusedCap, RefusedCollision };

struct HostPlayer {
  std::array<std::uint8_t, 4> overlay{};
  std::array<std::uint8_t, 4> lan{};
  std::uint16_t blur_src_port = 0;
  std::int64_t first_seen_ms = 0;
  std::int64_t last_seen_ms = 0;
  std::int64_t forwards_heard = 0;
  std::int64_t replies_forwarded = 0;
  bool in_filter = true;
};

class HostRoster {
 public:
  // Adds, refreshes, or refuses an introduction. `note` explains a refusal.
  AddResult Observe(const AnnouncePacket& p, std::int64_t now_ms, std::string& note);

  // Drops players quiet for longer than kHostPlayerExpirySeconds from the
  // filter, keeping their entries.
  void Expire(std::int64_t now_ms);

  // Counts a discovery packet arriving from a player. False when unknown.
  bool NoteForward(const std::array<std::uint8_t, 4>& lan, std::int64_t now_ms);

  // Counts a forwarded reply. Matches on LAN address AND destination port, so a
  // reply to the wrong port is never attributed to a player.
  bool NoteReplyForwarded(const std::array<std::uint8_t, 4>& lan, std::uint16_t blur_dst_port);

  bool Revoke(const std::array<std::uint8_t, 4>& overlay);

  std::vector<HostPlayer> Players() const;

  // Distinct LAN addresses of players currently in the filter.
  std::vector<std::array<std::uint8_t, 4>> FilterAddresses() const;

  int CountCollisions() const;

 private:
  mutable std::mutex mutex_;
  std::vector<HostPlayer> players_;
  int collisions_ = 0;
};

}  // namespace blurlink
