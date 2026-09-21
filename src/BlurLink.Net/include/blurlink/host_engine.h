#pragma once

// Host-mode engine: every host-mode decision, with no WinDivert and no Windows
// dependency, so it can be tested exhaustively in the portable test binary.
// `HostSession` is then only the driver plumbing around this.
//
// Scope, deliberately: this class decides WHAT to do with a packet and owns the
// counters. It never injects, never opens a handle, and never reads the clock —
// the caller passes `now_ms`, which makes expiry and debounce deterministic.
//
// Counters that depend on the *result* of an action (did the clone actually go
// out?) are reported back by the caller via NoteCloneSent / NoteCloneFailed /
// NoteReinject, exactly as the bridge only counts a forward once WinDivertSend
// has succeeded. A decision is not a delivery.

#include <array>
#include <cstdint>
#include <mutex>
#include <optional>
#include <string>
#include <vector>

#include "blurlink/announce.h"
#include "blurlink/config.h"
#include "blurlink/host_classify.h"
#include "blurlink/host_roster.h"

namespace blurlink {

// Debounce window for filter rebuilds: a burst of joins must cause one reopen,
// not one per join.
inline constexpr std::int64_t kHostFilterDebounceMs = 2000;

struct HostConfig {
  int discovery_port = 0;    // required, and never the introduction port
  int adapter_if_index = 0;  // required: which overlay adapter to watch
  int rate_per_second = kDefaultRatePerSecond;
  int rate_burst = kDefaultRateBurst;
  // Observe-only reply-shape expectation from the verified profile (Task 12,
  // R6: length + leading prefix). Empty means off. Counted where replies are
  // classified (Dispatch), never enforced: the outcome is final either way.
  std::optional<int> expected_reply_length;
  std::vector<std::uint8_t> expected_reply_prefix;
  // There is deliberately no "preserve original" toggle: the original reply is
  // ALWAYS reinjected unchanged, so the host's own LAN is unaffected.
};

ValidationResult ValidateHostConfig(const HostConfig& cfg);

struct HostCounters {
  std::int64_t captured = 0;
  std::int64_t forwards_heard = 0;
  std::int64_t replies_forwarded = 0;
  std::int64_t reinjected = 0;
  std::int64_t dropped = 0;
  std::int64_t injection_errors = 0;
  std::int64_t announce_rejected = 0;
  std::int64_t broadcast_replies = 0;
  std::int64_t ambiguous_replies = 0;
  std::int64_t unmatched_replies = 0;
  std::int64_t filter_reopens = 0;
  // Observe-only reply-shape outcomes (Task 12, R6). Checked counts every
  // prospective reply evaluated against a set expectation; mismatch counts
  // the failures. Neither ever changes the outcome.
  std::int64_t reply_shape_checked = 0;
  std::int64_t reply_shape_mismatch = 0;
};

// What the caller should do with one captured packet.
struct HostOutcome {
  HostAction action = HostAction::Reinject;  // Reinject | Forward | refusal
  std::array<std::uint8_t, 4> target_overlay{};
  std::string reason;         // always set: every outcome is explainable
  bool roster_changed = false;  // the filter address set actually changed
};

class HostEngine {
 public:
  explicit HostEngine(HostConfig cfg);

  const HostConfig& config() const { return cfg_; }

  // Classifies one packet: an introduction, a player's forward, or a reply.
  // Unparseable input is reported, never acted on.
  HostOutcome Dispatch(const std::uint8_t* packet, std::size_t len, bool inbound,
                       std::int64_t now_ms);

  // Expires quiet players. Returns true exactly once when a filter rebuild is
  // due (debounced), so the caller reopens the handle at most once per burst.
  bool Tick(std::int64_t now_ms);

  // The current filter, or an empty string with `error` set.
  std::string FilterString(std::string& error) const;

  // Caller-reported facts.
  void NoteCloneSent(const std::array<std::uint8_t, 4>& dst_ip, std::uint16_t dst_port);
  void NoteCloneFailed();
  void NoteReinject();

  HostCounters counters() const;
  std::vector<HostPlayer> players() const;
  bool Revoke(const std::array<std::uint8_t, 4>& overlay, std::int64_t now_ms);
  int collisions() const;
  std::string last_note() const;

 private:
  // Caller holds mutex_.
  std::string FilterStringLocked() const;
  void MarkRosterChanged(std::int64_t now_ms);

  mutable std::mutex mutex_;
  HostConfig cfg_;
  HostRoster roster_;
  HostCounters counters_{};
  std::string last_note_;
  bool filter_dirty_ = false;
  std::int64_t filter_dirty_since_ms_ = 0;
};

}  // namespace blurlink
