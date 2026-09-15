#pragma once

// Bridge: owns the WinDivert handle + capture worker, bounded counters, and
// the recent-packets ring (metadata only, never payloads).
//
// Packet flow per matching discovery datagram:
//   1. WinDivert delivers an outbound broadcast UDP packet (narrow filter).
//   2. Classify: dst port + broadcast dst already guaranteed by the filter;
//      optional payload-prefix checked here (code-side, not in filter).
//   3. Fragments/IPv6: filter is IPv4-only; fragments are reinjected
//      unchanged and counted (never cloned).
//   4. Dedup check: suspected echo of our own reinject => reinject only.
//   5. Rate limiter: over-limit => reinject original, count dropped.
//   6. Clone: dst := host overlay IP, recalc checksums via
//      WinDivertHelperCalcChecksums, inject outbound with Impostor=1.
//   7. Reinject the ORIGINAL byte-for-byte (unless user disabled preserve).
//
// Reinjection-loop strategy (documented, tested):
//   a. Structural: filter matches only broadcast/multicast dst, so unicast
//      clones can never re-match the same handle.
//   b. Platform: per WinDivert docs, injected packets are only re-captured
//      by *other lower-priority* handles, never by the injecting handle.
//   c. Defense in depth: clones are injected with Impostor=1, so WinDivert
//      auto-decrements TTL (last-resort loop mitigation per docs).
//   d. Backstop: DedupCache suppresses re-cloning of identical echoes.

#ifdef _WIN32

#include <windows.h>

#include <atomic>
#include <chrono>
#include <cstdint>
#include <deque>
#include <functional>
#include <map>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#include "blurlink/config.h"
#include "blurlink/dedup.h"
#include "blurlink/rate_limiter.h"
#include "blurlink/windivert_api.h"

namespace blurlink {

struct PacketEventMeta {
  std::string timestamp_iso;  // UTC ISO-8601
  std::string src_ip;
  int src_port = 0;
  std::string orig_dst_ip;
  int orig_dst_port = 0;
  std::string forwarded_dst_ip;  // empty when not forwarded
  int size = 0;
  std::string action;  // forwarded | reinjected | dropped-rate | dropped-echo |
                       // rejected-fragment | error
};

struct BridgeCounters {
  std::int64_t captured = 0;
  std::int64_t forwarded = 0;
  // Introductions sent to the host so it can map its replies back to us.
  std::int64_t announcements_sent = 0;
  std::int64_t reinjected = 0;
  std::int64_t dropped = 0;
  std::int64_t injection_errors = 0;
  std::int64_t fragments_rejected = 0;
  std::int64_t dedup_skipped = 0;
  // Outbound broadcasts refused by the code-side payload-prefix gate
  // (reinjected, never cloned). Observe-only refusal signal.
  std::int64_t payload_gate_skipped = 0;
  // Observe-only reply-shape outcomes (Task 12, R6). The bridge diverts
  // outbound queries only, so these stay 0 by construction; they exist so
  // the status contract is uniform across sessions.
  std::int64_t reply_shape_checked = 0;
  std::int64_t reply_shape_mismatch = 0;
};

class Bridge {
 public:
  Bridge();
  ~Bridge();
  Bridge(const Bridge&) = delete;
  Bridge& operator=(const Bridge&) = delete;

  bool active() const { return active_.load(); }
  std::string filter() const;
  std::string last_error() const;
  std::string route_interface() const;
  BridgeCounters counters() const;
  std::vector<PacketEventMeta> recent() const;

  // Opens WinDivert with the validated config and starts the worker.
  // Returns false with last_error() set on failure (bad params, missing
  // WinDivert.dll/driver, filter rejected, architecture mismatch...).
  bool Start(const BridgeConfig& cfg);

  // Closes the WinDivert handle and stops the worker. Idempotent.
  void Stop();

 private:
  void WorkerLoop();
  void BeatLoop();  // 30s heartbeat: proves liveness while Blur is quiet
  void PushRecent(PacketEventMeta meta);
  static std::string UtcNowIso();

  // Tells the host who we are. Safe to call often: it returns false without
  // doing anything until we have actually seen one of our own forwards, whose
  // source address and port are exactly what the host's Blur will reply to.
  bool SendAnnounce();

  mutable std::mutex mutex_;
  BridgeConfig cfg_{};
  std::string filter_;
  std::string last_error_;
  std::string route_iface_;
  BridgeCounters counters_{};
  std::deque<PacketEventMeta> recent_;  // capped at 100

  WinDivertApi api_;
  HANDLE divert_ = nullptr;
  std::atomic<bool> active_{false};
  // Set by WorkerLoop on its way out; Stop() waits for it before joining, so a
  // blocked WinDivertRecv cannot make Stop() hang without bound.
  std::atomic<bool> worker_exited_{true};
  std::thread worker_;
  std::thread beat_;  // heartbeat; joined in Stop()

  // Learned from a forwarded discovery packet: where the host's Blur will
  // address its reply, and therefore what we must introduce ourselves as.
  bool have_forward_source_ = false;
  std::array<std::uint8_t, 4> last_src_ip_{};
  std::uint16_t last_src_port_ = 0;
};

/// Per-port observation from a research sniff. Outbound: port = broadcast
/// destination port, srcIp empty. Inbound: port = sender port, srcIp set.
struct SniffPortCount {
  int port = 0;
  std::int64_t count = 0;
  std::string src_ip;
};

/// Research sniffer: counts outbound broadcast UDP packets per destination
/// port so the GUI can suggest the discovery port. SNIFF mode only — packets
/// are never diverted, modified, reinjected, or blocked. Bounded by duration
/// and packet cap. Metadata (ports/counts) only, never payloads.
class Sniffer {
 public:
  Sniffer();
  ~Sniffer();

  Sniffer(const Sniffer&) = delete;
  Sniffer& operator=(const Sniffer&) = delete;

  bool active() const { return active_.load(); }
  std::string filter() const;
  std::string last_error() const;
  std::int64_t observed() const;
  std::vector<SniffPortCount> results() const;  // sorted by count desc

  // Opens WinDivert with WINDIVERT_FLAG_SNIFF and counts for up to
  // duration_sec (or max_packets). Returns false with last_error() set.
  bool Start(const SniffConfig& cfg);

  // Stops early; results stay available. Idempotent.
  void Stop();

 private:
  void WorkerLoop(int duration_sec, int max_packets);
  void DeadlineLoop(std::chrono::steady_clock::time_point deadline);

  mutable std::mutex mutex_;
  SniffConfig cfg_{};
  std::string filter_;
  std::string last_error_;
  std::int64_t observed_ = 0;
  // (srcIp, port): outbound counts key on dst port (ip empty), inbound on sender.
  std::map<std::pair<std::string, std::uint16_t>, std::int64_t> per_port_;

  WinDivertApi api_;
  HANDLE divert_ = nullptr;
  std::atomic<bool> active_{false};
  std::thread worker_;
  std::thread deadline_;  // wakes the blocked recv at the sniff expiry
};

}  // namespace blurlink

#endif  // _WIN32
