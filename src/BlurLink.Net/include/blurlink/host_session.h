#pragma once

// Host mode session: the WinDivert half of host mode.
//
// Everything that decides is in HostEngine (portable, unit-tested). This class
// only does I/O: one handle, one combined filter, recv -> Dispatch -> clone and
// reinject. Keeping the split means the safety-critical logic is tested without
// a driver, and this file stays thin enough to review at a glance.
//
// Invariants, matching the bridge:
//   * ONE WinDivert handle. Rebuilt (debrief, reopened) when the roster changes.
//   * NO network listener — the helper never binds a UDP port. Introductions are
//     observed through the driver and then reinjected unchanged.
//   * The original packet is ALWAYS reinjected byte-for-byte, so the host's own
//     LAN behaviour is identical to running without BlurLink.

#ifdef _WIN32

#include <windows.h>

#include <array>
#include <atomic>
#include <cstdint>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#include "blurlink/host_engine.h"
#include "blurlink/rate_limiter.h"
#include "blurlink/windivert_api.h"

namespace blurlink {

class HostSession {
 public:
  HostSession();
  ~HostSession();

  HostSession(const HostSession&) = delete;
  HostSession& operator=(const HostSession&) = delete;

  bool active() const { return active_.load(); }
  std::string filter() const;
  std::string last_error() const;

  // Validates (ValidateHostConfig), builds the filter, loads WinDivert, opens the
  // handle and starts the worker. Returns false with last_error() set.
  bool Start(const HostConfig& cfg);

  // Closes the handle and stops the workers. Idempotent.
  void Stop();

  HostCounters counters() const;
  std::vector<HostPlayer> players() const;
  int collisions() const;
  std::string last_note() const;
  bool Revoke(const std::array<std::uint8_t, 4>& overlay);

 private:
  void WorkerLoop();
  void HousekeepLoop();
  bool OpenHandle(const std::string& filter);
  bool RebuildFilterIfDue();
  static std::int64_t NowMs();

  mutable std::mutex mutex_;
  HostConfig cfg_{};
  std::unique_ptr<HostEngine> engine_;
  std::unique_ptr<RateLimiter> limiter_;
  std::string filter_;
  std::string last_error_;

  WinDivertApi api_;
  HANDLE divert_ = nullptr;
  std::atomic<bool> active_{false};
  // Set by WorkerLoop on its way out; Stop() waits for it before joining, so a
  // blocked WinDivertRecv cannot make Stop() hang without bound.
  std::atomic<bool> worker_exited_{true};
  std::thread worker_;
  std::thread housekeep_;
};

}  // namespace blurlink

#endif  // _WIN32
