#include "blurlink/host_session.h"

#ifdef _WIN32

#include <chrono>
#include <exception>
#include <vector>

#include "blurlink/console_log.h"
#include "blurlink/packet.h"

namespace blurlink {
namespace {

constexpr std::size_t kMaxPacket = 65535;

std::string LastErrorString(DWORD code) {
  char* buf = nullptr;
  DWORD n = FormatMessageA(FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_SYSTEM |
                               FORMAT_MESSAGE_IGNORE_INSERTS,
                           nullptr, code, 0, reinterpret_cast<LPSTR>(&buf), 0, nullptr);
  std::string s = (n && buf) ? std::string(buf, n) : ("error " + std::to_string(code));
  if (buf) LocalFree(buf);
  while (!s.empty() && (s.back() == '\n' || s.back() == '\r')) s.pop_back();
  return s;
}

// Same wording as the bridge, so a driver problem reads identically whichever
// mode the user started.
std::string OpenFailureText(DWORD code, const std::string& filter) {
  if (code == 5) return "WinDivertOpen failed: access denied. The helper must run elevated (UAC).";
  if (code == 2) {
    return "WinDivertOpen failed: driver not found (code 2). Install WinDivert64.sys per "
           "third-party/WinDivert/README.md.";
  }
  if (code == 87) {
    return "WinDivert rejected the filter string (code 87): '" + filter +
           "'. Check the WinDivert version (2.x x64 required).";
  }
  return "WinDivertOpen failed: " + LastErrorString(code);
}

// Waits for an exit flag, with a deadline. A blocked WinDivertRecv must never
// be able to hang Stop(): Stop() runs on the command loop, so hanging there also
// disables the watchdog and leaves a helper that only Task Manager can kill.
// Deliberately NOT std::thread::native_handle() -- that is a Win32 HANDLE under
// MSVC but an opaque value under MinGW, and the interop harness builds with
// MinGW, so a native wait would not compile there.
bool WaitForExit(const std::atomic<bool>& exited, int ms) {
  const int step = 25;
  for (int waited = 0; waited < ms; waited += step) {
    if (exited.load()) return true;
    std::this_thread::sleep_for(std::chrono::milliseconds(step));
  }
  return exited.load();
}

}  // namespace

HostSession::HostSession() = default;

HostSession::~HostSession() { Stop(); }

std::string HostSession::filter() const {
  std::lock_guard<std::mutex> lock(mutex_);
  return filter_;
}

std::string HostSession::last_error() const {
  std::lock_guard<std::mutex> lock(mutex_);
  return last_error_;
}

std::int64_t HostSession::NowMs() {
  return std::chrono::duration_cast<std::chrono::milliseconds>(
             std::chrono::steady_clock::now().time_since_epoch())
      .count();
}

bool HostSession::Start(const HostConfig& cfg) {
  Stop();
  {
    std::lock_guard<std::mutex> lock(mutex_);
    last_error_.clear();
    filter_.clear();
    engine_.reset();
    limiter_.reset();
  }

  if (auto r = ValidateHostConfig(cfg); !r.ok) {
    std::lock_guard<std::mutex> lock(mutex_);
    last_error_ = "Invalid host parameters: " + r.error;
    return false;
  }

  auto engine = std::make_unique<HostEngine>(cfg);
  std::string err;
  const std::string filter = engine->FilterString(err);
  if (filter.empty()) {
    std::lock_guard<std::mutex> lock(mutex_);
    last_error_ = "Invalid host parameters: " + (err.empty() ? "filter build failed" : err);
    return false;
  }

  if (!OpenHandle(filter)) {
    return false;  // OpenHandle set last_error_
  }

  {
    std::lock_guard<std::mutex> lock(mutex_);
    cfg_ = cfg;
    engine_ = std::move(engine);
    limiter_ = std::make_unique<RateLimiter>(cfg.rate_per_second, cfg.rate_burst);
  }

  active_.store(true);
  worker_exited_.store(false);
  worker_ = std::thread(&HostSession::WorkerLoop, this);
  housekeep_ = std::thread(&HostSession::HousekeepLoop, this);

  blog::Info("host mode started");
  blog::Info("  filter : %s", filter.c_str());
  blog::Info("  window : discovery port %d, introduction port %d", cfg.discovery_port,
             static_cast<int>(kHostAnnounceUdpPort));
  blog::Info("  note   : players must start their bridge to be introduced.");
  blog::Info("host mode watches only traffic aimed at accepted players - nothing else.");
  return true;
}

bool HostSession::OpenHandle(const std::string& filter) {
  std::string load_err;
  if (!api_.Load(load_err)) {
    std::lock_guard<std::mutex> lock(mutex_);
    last_error_ = load_err;
    return false;
  }

  // Flags = 0, NOT SNIFF: replies must be diverted to be cloned, and every
  // diverted original is reinjected immediately below.
  HANDLE h = api_.open_(filter.c_str(), kDivertLayerNetwork, 0 /*priority*/, 0 /*flags*/);
  if (!h || h == INVALID_HANDLE_VALUE) {
    DWORD code = GetLastError();
    std::lock_guard<std::mutex> lock(mutex_);
    last_error_ = OpenFailureText(code, filter);
    return false;
  }

  api_.set_param_(h, kDivertParamQueueLength, 1024);
  api_.set_param_(h, kDivertParamQueueTime, 1000);
  {
    std::lock_guard<std::mutex> lock(mutex_);
    divert_ = h;
    filter_ = filter;
  }
  return true;
}

bool HostSession::RebuildFilterIfDue() {
  HostEngine* engine = nullptr;
  {
    std::lock_guard<std::mutex> lock(mutex_);
    engine = engine_.get();
  }
  if (!engine) return false;

  const std::int64_t now = NowMs();
  if (!engine->Tick(now)) return false;

  std::string err;
  const std::string next = engine->FilterString(err);
  if (next.empty()) {
    std::lock_guard<std::mutex> lock(mutex_);
    last_error_ = "Host filter rebuild failed: " + (err.empty() ? "unknown" : err);
    return false;
  }

  // Detach the old handle first so the worker stops using it, then close it and
  // open the replacement. A reply in flight during this instant is missed; the
  // recovery is refreshing Blur's LAN list, and we say so in the log.
  HANDLE old = nullptr;
  {
    std::lock_guard<std::mutex> lock(mutex_);
    old = divert_;
    divert_ = nullptr;
  }
  if (old && old != INVALID_HANDLE_VALUE) {
    if (api_.shutdown_) api_.shutdown_(old, kDivertShutdownRecv);
    if (api_.close_) api_.close_(old);
  }

  if (!OpenHandle(next)) {
    blog::Error("host filter rebuild failed: %s", last_error().c_str());
    return false;
  }

  blog::Info("host filter rebuilt for the new player set: brief interception gap, "
             "refresh Blur's LAN list if a reply was missed");
  blog::Info("  filter : %s", next.c_str());
  return true;
}

void HostSession::HousekeepLoop() {
  while (active_.load()) {
    std::this_thread::sleep_for(std::chrono::milliseconds(250));
    if (!active_.load()) break;
    RebuildFilterIfDue();
  }
}

void HostSession::WorkerLoop() {
  std::vector<std::uint8_t> buf(kMaxPacket);

  while (active_.load()) {
    HANDLE h = nullptr;
    {
      std::lock_guard<std::mutex> lock(mutex_);
      h = divert_;
    }
    if (!h || h == INVALID_HANDLE_VALUE) {
      // A rebuild is in progress: wait for the replacement rather than spin.
      std::this_thread::sleep_for(std::chrono::milliseconds(20));
      continue;
    }

    DivertAddress addr{};
    UINT recv_len = 0;
    if (!api_.recv_(h, buf.data(), static_cast<UINT>(buf.size()), &recv_len, &addr)) {
      if (!active_.load()) break;  // Stop() woke us.
      const DWORD code = GetLastError();
      // ERROR_OPERATION_ABORTED / ERROR_INVALID_HANDLE are the normal result of
      // a rebuild or a shutdown, not failures worth reporting as errors.
      if (code != ERROR_OPERATION_ABORTED && code != ERROR_INVALID_HANDLE) {
        std::lock_guard<std::mutex> lock(mutex_);
        last_error_ = "WinDivertRecv failed: " + LastErrorString(code);
      }
      std::this_thread::sleep_for(std::chrono::milliseconds(20));
      continue;
    }

    const bool inbound = addr.Outbound == 0;

    UdpPacketView view{};
    std::string reject;
    const bool parsed = TryParseUdpOverIpv4(buf.data(), recv_len, view, reject);
    if (!parsed) {
      // Never acted on, always reinjected below — exactly like the bridge.
      blog::Debug("host: reinjected unparsed packet (%s)",
                  reject.empty() ? "parse" : reject.c_str());
    }

    HostEngine* engine = nullptr;
    {
      std::lock_guard<std::mutex> lock(mutex_);
      engine = engine_.get();
    }

    HostOutcome outcome;
    if (engine) outcome = engine->Dispatch(buf.data(), recv_len, inbound, NowMs());

    if (engine && parsed && outcome.action == HostAction::Forward) {
      bool acquire = false;
      {
        std::lock_guard<std::mutex> lock(mutex_);
        if (limiter_) acquire = limiter_->TryAcquire();
      }
      if (!acquire) {
        blog::Debug("[host-drop] rate limit (%s:%d)", Ipv4ToString(view.dst_ip).c_str(),
                    view.dst_port);
      } else {
        try {
          auto clone = CloneWithNewDestination(buf.data(), recv_len, outcome.target_overlay);
          DivertAddress caddr = addr;
          caddr.Outbound = 1;
          caddr.Impostor = 1;  // new packet: TTL-decrement loop defense per docs
          caddr.IPChecksum = 0;
          caddr.TCPChecksum = 0;
          caddr.UDPChecksum = 0;
          api_.calc_checksums_(clone.data(), static_cast<UINT>(clone.size()), &caddr, 0);
          UINT written = static_cast<UINT>(clone.size());
          if (api_.send_(h, clone.data(), static_cast<UINT>(clone.size()), &written, &caddr)) {
            engine->NoteCloneSent(view.dst_ip, view.dst_port);
            blog::Info("[host-fwd] reply %s:%d -> %s:%d => player %s",
                       Ipv4ToString(view.src_ip).c_str(), view.src_port,
                       Ipv4ToString(view.dst_ip).c_str(), view.dst_port,
                       Ipv4ToString(outcome.target_overlay).c_str());
          } else {
            engine->NoteCloneFailed();
            std::lock_guard<std::mutex> lock(mutex_);
            last_error_ = "WinDivertSend (host clone) failed: " + LastErrorString(GetLastError());
          }
        } catch (const std::exception& ex) {
          engine->NoteCloneFailed();
          std::lock_guard<std::mutex> lock(mutex_);
          last_error_ = ex.what();
        }
      }
    }

    // ALWAYS reinject the original byte-for-byte: the host's own LAN behaviour
    // must be identical to running without BlurLink.
    DivertAddress oaddr = addr;
    UINT owritten = static_cast<UINT>(recv_len);
    if (api_.send_(h, buf.data(), static_cast<UINT>(recv_len), &owritten, &oaddr)) {
      if (engine) engine->NoteReinject();
    } else {
      std::lock_guard<std::mutex> lock(mutex_);
      last_error_ = "WinDivertSend (original) failed: " + LastErrorString(GetLastError());
    }
  }

  // Last statement of the loop: Stop() waits on this before joining.
  worker_exited_.store(true);
}

void HostSession::Stop() {
  const bool was = active_.exchange(false);

  HANDLE h = nullptr;
  {
    std::lock_guard<std::mutex> lock(mutex_);
    h = divert_;
    divert_ = nullptr;
  }
  if (h && h != INVALID_HANDLE_VALUE) {
    // kDivertShutdownRecv is a FLAG (0x1); see windivert_abi.h. Passing an
    // invalid value here silently leaves the worker blocked in recv.
    if (api_.shutdown_) api_.shutdown_(h, kDivertShutdownRecv);
  }

  if (worker_.joinable()) {
    // Only start joining once the worker has actually exited; if it has not,
    // close the handle to unblock its recv, because an unbounded join here is
    // exactly the wedge that made the helper unkillable.
    if (!WaitForExit(worker_exited_, 3000) && h && h != INVALID_HANDLE_VALUE && api_.close_) {
      blog::Warn("host worker did not exit after shutdown; closing the WinDivert "
                 "handle to force it");
      api_.close_(h);
      h = nullptr;  // already closed
      WaitForExit(worker_exited_, 2000);
    }
    worker_.join();
  }
  if (housekeep_.joinable()) housekeep_.join();

  if (h && h != INVALID_HANDLE_VALUE && api_.close_) {
    api_.close_(h);
  }

  HostCounters final;
  bool have = false;
  {
    std::lock_guard<std::mutex> lock(mutex_);
    if (engine_) {
      final = engine_->counters();
      have = true;
    }
  }
  if (was && have) {
    blog::Info("host mode stopped: captured=%lld forwardsHeard=%lld forwarded=%lld "
               "reinjected=%lld refusedBroadcast=%lld ambiguous=%lld unmatched=%lld errors=%lld",
               static_cast<long long>(final.captured),
               static_cast<long long>(final.forwards_heard),
               static_cast<long long>(final.replies_forwarded),
               static_cast<long long>(final.reinjected),
               static_cast<long long>(final.broadcast_replies),
               static_cast<long long>(final.ambiguous_replies),
               static_cast<long long>(final.unmatched_replies),
               static_cast<long long>(final.injection_errors));
  }
}

HostCounters HostSession::counters() const {
  std::lock_guard<std::mutex> lock(mutex_);
  return engine_ ? engine_->counters() : HostCounters{};
}

std::vector<HostPlayer> HostSession::players() const {
  std::lock_guard<std::mutex> lock(mutex_);
  return engine_ ? engine_->players() : std::vector<HostPlayer>{};
}

int HostSession::collisions() const {
  std::lock_guard<std::mutex> lock(mutex_);
  return engine_ ? engine_->collisions() : 0;
}

std::string HostSession::last_note() const {
  std::lock_guard<std::mutex> lock(mutex_);
  return engine_ ? engine_->last_note() : std::string{};
}

bool HostSession::Revoke(const std::array<std::uint8_t, 4>& overlay) {
  HostEngine* engine = nullptr;
  {
    std::lock_guard<std::mutex> lock(mutex_);
    engine = engine_.get();
  }
  return engine ? engine->Revoke(overlay, NowMs()) : false;
}

}  // namespace blurlink

#endif  // _WIN32
