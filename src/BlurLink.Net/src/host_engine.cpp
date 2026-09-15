#include "blurlink/host_engine.h"

#include <cstddef>

#include "blurlink/packet.h"
#include "blurlink/reply_shape.h"

namespace blurlink {

ValidationResult ValidateHostConfig(const HostConfig& cfg) {
  if (cfg.discovery_port < 1 || cfg.discovery_port > 65535) {
    return ValidationResult::Fail("Host mode needs the discovery port (detect it first).");
  }
  if (cfg.discovery_port == static_cast<int>(kHostAnnounceUdpPort)) {
    return ValidationResult::Fail(
        "The discovery port cannot be BlurLink's own introduction port.");
  }
  if (cfg.adapter_if_index == 0) {
    return ValidationResult::Fail("Select the overlay adapter to watch first.");
  }
  if (cfg.rate_per_second < 1 || cfg.rate_per_second > kMaxRatePerSecond) {
    return ValidationResult::Fail("Rate limit must be 1-100 packets/second.");
  }
  if (cfg.rate_burst < 1 || cfg.rate_burst > kMaxRateBurst) {
    return ValidationResult::Fail("Burst must be 1-200.");
  }
  return ValidationResult::Ok();
}

HostEngine::HostEngine(HostConfig cfg) : cfg_(cfg) {}

std::string HostEngine::FilterStringLocked() const {
  std::string err;
  return BuildHostFilterString(cfg_.discovery_port, roster_.FilterAddresses(), err,
                               cfg_.adapter_if_index);
}

std::string HostEngine::FilterString(std::string& error) const {
  std::lock_guard<std::mutex> lock(mutex_);
  return BuildHostFilterString(cfg_.discovery_port, roster_.FilterAddresses(), error,
                               cfg_.adapter_if_index);
}

void HostEngine::MarkRosterChanged(std::int64_t now_ms) {
  filter_dirty_ = true;
  filter_dirty_since_ms_ = now_ms;
}

HostOutcome HostEngine::Dispatch(const std::uint8_t* packet, std::size_t len, bool inbound,
                                 std::int64_t now_ms) {
  std::lock_guard<std::mutex> lock(mutex_);
  HostOutcome out;
  ++counters_.captured;

  UdpPacketView view{};
  std::string reject;
  if (packet == nullptr || !TryParseUdpOverIpv4(packet, len, view, reject)) {
    out.reason = "rejected-" + (reject.empty() ? std::string("parse") : reject);
    return out;
  }

  // 1. A player introducing itself.
  if (inbound && view.dst_port == kHostAnnounceUdpPort) {
    if (view.payload_len != kAnnouncePacketSize) {
      ++counters_.announce_rejected;
      out.reason = "announce-bad-length";
      return out;
    }
    AnnouncePacket p{};
    std::string why;
    if (!DecodeAnnounce(packet + view.payload_offset, view.payload_len, p, why)) {
      ++counters_.announce_rejected;
      out.reason = "announce-" + why;
      return out;
    }

    // Compare the filter address set around the mutation: a mere refresh of an
    // already-live player must not schedule a rebuild, while a refresh that
    // *revives* an expired one must.
    const std::string before = FilterStringLocked();
    std::string note;
    switch (roster_.Observe(p, now_ms, note)) {
      case AddResult::Added: out.reason = "announce-added"; break;
      case AddResult::Refreshed: out.reason = "announce-refreshed"; break;
      case AddResult::RefusedCollision: out.reason = "announce-collision"; break;
      case AddResult::RefusedCap: out.reason = "announce-cap"; break;
    }
    last_note_ = note;
    if (FilterStringLocked() != before) {
      MarkRosterChanged(now_ms);
      out.roster_changed = true;
    }
    return out;
  }

  // 2. A player's forwarded discovery. This is the prerequisite diagnostic:
  //    a count of zero means host mode cannot help until the overlay carries
  //    the forward at all.
  if (inbound && view.dst_port == static_cast<std::uint16_t>(cfg_.discovery_port)) {
    ++counters_.forwards_heard;
    roster_.NoteForward(view.src_ip, now_ms);
    out.reason = "forward-heard";
    return out;
  }

  // 3. Everything else outbound is treated as a prospective reply.
  const HostDecision d =
      ClassifyHostPacket(view, inbound, cfg_.discovery_port, roster_.Players());
  out.action = d.action;
  out.target_overlay = d.target_overlay;
  out.reason = d.reason;

  // Observe-only reply-shape check (Task 12, R6: length + leading prefix).
  // Inbound traffic is never a reply, and with no expectation set nothing is
  // evaluated — so the counters stay 0 instead of counting noise. The check
  // never changes the outcome above: the original still flows.
  if (!inbound &&
      (cfg_.expected_reply_length.has_value() || !cfg_.expected_reply_prefix.empty())) {
    ++counters_.reply_shape_checked;
    if (!ReplyShapeMatches(view.payload_len, packet + view.payload_offset, view.payload_len,
                           cfg_.expected_reply_length, cfg_.expected_reply_prefix)) {
      ++counters_.reply_shape_mismatch;
    }
  }

  switch (d.action) {
    case HostAction::RefuseBroadcast:
      ++counters_.broadcast_replies;
      break;
    case HostAction::RefuseAmbiguous:
      ++counters_.ambiguous_replies;
      break;
    case HostAction::Reinject:
      if (d.reason == "unmatched-address" || d.reason == "unmatched-port") {
        ++counters_.unmatched_replies;
      }
      break;
    case HostAction::Forward:
      // Counted by NoteCloneSent, only once the clone has actually gone out.
      break;
  }
  return out;
}

bool HostEngine::Tick(std::int64_t now_ms) {
  std::lock_guard<std::mutex> lock(mutex_);

  const std::string before = FilterStringLocked();
  roster_.Expire(now_ms);
  if (FilterStringLocked() != before) MarkRosterChanged(now_ms);

  if (filter_dirty_ && now_ms - filter_dirty_since_ms_ >= kHostFilterDebounceMs) {
    filter_dirty_ = false;
    ++counters_.filter_reopens;
    return true;
  }
  return false;
}

void HostEngine::NoteCloneSent(const std::array<std::uint8_t, 4>& dst_ip,
                              std::uint16_t dst_port) {
  std::lock_guard<std::mutex> lock(mutex_);
  ++counters_.replies_forwarded;
  roster_.NoteReplyForwarded(dst_ip, dst_port);
}

void HostEngine::NoteCloneFailed() {
  std::lock_guard<std::mutex> lock(mutex_);
  ++counters_.injection_errors;
}

void HostEngine::NoteReinject() {
  std::lock_guard<std::mutex> lock(mutex_);
  ++counters_.reinjected;
}

HostCounters HostEngine::counters() const {
  std::lock_guard<std::mutex> lock(mutex_);
  return counters_;
}

std::vector<HostPlayer> HostEngine::players() const {
  return roster_.Players();
}

bool HostEngine::Revoke(const std::array<std::uint8_t, 4>& overlay, std::int64_t now_ms) {
  std::lock_guard<std::mutex> lock(mutex_);
  const std::string before = FilterStringLocked();
  const bool found = roster_.Revoke(overlay);
  if (found && FilterStringLocked() != before) MarkRosterChanged(now_ms);
  return found;
}

int HostEngine::collisions() const {
  return roster_.CountCollisions();
}

std::string HostEngine::last_note() const {
  std::lock_guard<std::mutex> lock(mutex_);
  return last_note_;
}

}  // namespace blurlink
