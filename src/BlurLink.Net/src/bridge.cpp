// Bridge implementation: WinDivert capture worker + classification + clone.

#include "blurlink/bridge.h"

#ifdef _WIN32

#include <iphlpapi.h>

#include <algorithm>
#include <chrono>
#include <thread>
#include <ctime>
#include <iomanip>
#include <memory>
#include <sstream>

#include "blurlink/announce.h"
#include "blurlink/packet.h"

#include "blurlink/console_log.h"

#ifdef _MSC_VER
#pragma comment(lib, "iphlpapi.lib")
#endif

namespace blurlink {
namespace {

constexpr std::size_t kMaxPacket = 65535;
constexpr std::size_t kRecentCap = 100;

// Waits for an exit flag, with a deadline. See the note in Stop(): a blocked
// WinDivertRecv must never be able to hang it. Deliberately NOT
// std::thread::native_handle(), which is a Win32 HANDLE under MSVC but an
// opaque value under MinGW -- and the interop harness builds with MinGW.
bool WaitForExit(const std::atomic<bool>& exited, int ms) {
  const int step = 25;
  for (int waited = 0; waited < ms; waited += step) {
    if (exited.load()) return true;
    std::this_thread::sleep_for(std::chrono::milliseconds(step));
  }
  return exited.load();
}

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

std::string DescribeRoute(const std::array<std::uint8_t, 4>& host) {
  // Read-only GetBestRoute lookup for status reporting. Never alters routes.
  MIB_IPFORWARDROW row{};
  DWORD dest = (static_cast<DWORD>(host[0]) << 24) | (static_cast<DWORD>(host[1]) << 16) |
               (static_cast<DWORD>(host[2]) << 8) | host[3];
  dest = htonl(dest);
  if (GetBestRoute(dest, 0, &row) != NO_ERROR) {
    return "route-unresolved";
  }
  std::ostringstream os;
  os << "ifIndex=" << row.dwForwardIfIndex << " metric=" << row.dwForwardMetric1;
  return os.str();
}

}  // namespace

Bridge::Bridge() = default;

Bridge::~Bridge() { Stop(); }

std::string Bridge::filter() const {
  std::lock_guard<std::mutex> lock(mutex_);
  return filter_;
}

std::string Bridge::last_error() const {
  std::lock_guard<std::mutex> lock(mutex_);
  return last_error_;
}

std::string Bridge::route_interface() const {
  std::lock_guard<std::mutex> lock(mutex_);
  return route_iface_;
}

BridgeCounters Bridge::counters() const {
  std::lock_guard<std::mutex> lock(mutex_);
  BridgeCounters c = counters_;
  return c;
}

std::vector<PacketEventMeta> Bridge::recent() const {
  std::lock_guard<std::mutex> lock(mutex_);
  return std::vector<PacketEventMeta>(recent_.begin(), recent_.end());
}

std::string Bridge::UtcNowIso() {
  auto now = std::chrono::system_clock::now();
  std::time_t t = std::chrono::system_clock::to_time_t(now);
  std::tm tm{};
  gmtime_s(&tm, &t);
  auto ms = std::chrono::duration_cast<std::chrono::milliseconds>(now.time_since_epoch()) % 1000;
  std::ostringstream os;
  os << std::put_time(&tm, "%Y-%m-%dT%H:%M:%S") << '.' << std::setw(3) << std::setfill('0')
     << ms.count() << 'Z';
  return os.str();
}

void Bridge::PushRecent(PacketEventMeta meta) {
  // Caller holds mutex_.
  recent_.push_back(std::move(meta));
  while (recent_.size() > kRecentCap) recent_.pop_front();
}

bool Bridge::Start(const BridgeConfig& cfg) {
  Stop();
  {
    std::lock_guard<std::mutex> lock(mutex_);
    counters_ = BridgeCounters{};
    recent_.clear();
    last_error_.clear();
  }
  if (cfg.rate_per_second < 1 || cfg.rate_per_second > kMaxRatePerSecond ||
      cfg.rate_burst < 1 || cfg.rate_burst > kMaxRateBurst) {
    std::lock_guard<std::mutex> lock(mutex_);
    last_error_ = "Rate limit out of range.";
    return false;
  }
  std::string filter;
  if (auto r = BuildFilterString(const_cast<BridgeConfig&>(cfg), filter); !r.ok) {
    std::lock_guard<std::mutex> lock(mutex_);
    last_error_ = "Invalid bridge parameters: " + r.error;
    return false;
  }
  std::string load_err;
  if (!api_.Load(load_err)) {
    std::lock_guard<std::mutex> lock(mutex_);
    last_error_ = load_err;
    return false;
  }
  HANDLE h = api_.open_(filter.c_str(), kDivertLayerNetwork, 0 /*priority*/, 0 /*flags*/);
  if (!h || h == INVALID_HANDLE_VALUE) {
    DWORD code = GetLastError();
    std::lock_guard<std::mutex> lock(mutex_);
    if (code == 5) {
      last_error_ =
          "WinDivertOpen failed: access denied. The helper must run elevated (UAC).";
    } else if (code == 2) {
      last_error_ =
          "WinDivertOpen failed: driver not found (code 2). Install WinDivert64.sys per "
          "third-party/WinDivert/README.md.";
    } else if (code == 87) {
      last_error_ =
          "WinDivert rejected the filter string (code 87): '" + filter +
          "'. Check the WinDivert version (2.x x64 required).";
    } else {
      last_error_ = "WinDivertOpen failed: " + LastErrorString(code);
    }
    return false;
  }
  api_.set_param_(h, kDivertParamQueueLength, 1024);
  api_.set_param_(h, kDivertParamQueueTime, 1000);
  {
    std::lock_guard<std::mutex> lock(mutex_);
    cfg_ = cfg;
    filter_ = filter;
    route_iface_ = DescribeRoute(cfg.host_ip);
    divert_ = h;
  }
  active_.store(true);
  worker_exited_.store(false);
  worker_ = std::thread(&Bridge::WorkerLoop, this);
  beat_ = std::thread(&Bridge::BeatLoop, this);
  blog::Info("bridge started");
  blog::Info("  filter : %s", filter.c_str());
  blog::Info("  host   : %s  preserve-original=%s  rate=%d/s",
             cfg.host_ip_str.c_str(), cfg.preserve_original_broadcast ? "yes" : "no",
             cfg.rate_per_second);
  blog::Info("  route  : %s", route_iface_.c_str());
  blog::Info("what to expect: one [fwd] line each time Blur refreshes its LAN list.");
  blog::Info("silence while Blur is idle is normal - a status line appears every 15s.");
  return true;
}

void Bridge::Stop() {
  bool was = active_.exchange(false);
  HANDLE h = nullptr;
  {
    std::lock_guard<std::mutex> lock(mutex_);
    h = divert_;
    divert_ = nullptr;
  }
  if (h && h != INVALID_HANDLE_VALUE) {
    // Wake the worker: shut down RECV so a blocked WinDivertRecv returns.
    // kDivertShutdownRecv is a FLAG (0x1), not an ordinal 0; see
    // windivert_abi.h for the bug that mistake caused.
    if (api_.shutdown_) api_.shutdown_(h, kDivertShutdownRecv);
  }
  if (worker_.joinable()) {
    // Bounded, for the same reason as the host session: Stop() runs on the
    // command loop, so a hang here also disables the watchdog.
    if (!WaitForExit(worker_exited_, 3000) && h && h != INVALID_HANDLE_VALUE && api_.close_) {
      blog::Warn("bridge worker did not exit after shutdown; closing the WinDivert "
                 "handle to force it");
      api_.close_(h);
      h = nullptr;  // already closed
      WaitForExit(worker_exited_, 2000);
    }
    worker_.join();
  }
  if (beat_.joinable()) beat_.join();
  if (h && h != INVALID_HANDLE_VALUE && api_.close_) {
    api_.close_(h);
  }
  if (was) {
    // Summary only when the bridge actually ran; Stop() is also called on
    // never-started bridges, where a counter dump would be pure noise.
    std::lock_guard<std::mutex> lock(mutex_);
    blog::Info("bridge stopped: captured=%lld forwarded=%lld reinjected=%lld dropped=%lld "
               "errors=%lld dedup-skipped=%lld",
               (long long)counters_.captured, (long long)counters_.forwarded,
               (long long)counters_.reinjected, (long long)counters_.dropped,
               (long long)counters_.injection_errors, (long long)counters_.dedup_skipped);
    blog::Info("nothing is intercepted any more (WinDivert handle closed).");
  }
}

bool Bridge::SendAnnounce() {
  std::array<std::uint8_t, 4> overlay{};
  std::array<std::uint8_t, 4> lan{};
  std::array<std::uint8_t, 4> host{};
  std::string overlay_str;
  std::uint16_t lan_port = 0;
  HANDLE h = nullptr;
  {
    std::lock_guard<std::mutex> lock(mutex_);
    if (!cfg_.announce_to_host || !have_forward_source_) return false;
    const auto& local = cfg_.local_overlay_ip;
    if (local[0] == 0 && local[1] == 0 && local[2] == 0 && local[3] == 0) {
      // Without our own overlay address the host could not reply to us, so an
      // introduction would be worse than useless.
      return false;
    }
    overlay = local;
    overlay_str = cfg_.local_overlay_ip_str;
    host = cfg_.host_ip;
    lan = last_src_ip_;
    lan_port = last_src_port_;
    h = divert_;
  }
  if (!h || h == INVALID_HANDLE_VALUE) return false;

  AnnouncePacket p{};
  p.overlay = overlay;
  p.lan = lan;
  p.blur_src_port = lan_port;
  const auto payload = EncodeAnnounce(p);

  // A minimal IPv4/UDP datagram: our overlay address -> host:introductionPort.
  // Checksums are left to WinDivert's own helper, exactly as the clone path does.
  const std::size_t udp_len = 8 + payload.size();
  const std::size_t total_len = 20 + udp_len;
  std::vector<std::uint8_t> pkt(total_len, 0);
  pkt[0] = 0x45;  // IPv4, IHL 5
  pkt[2] = static_cast<std::uint8_t>(total_len >> 8);
  pkt[3] = static_cast<std::uint8_t>(total_len & 0xFF);
  pkt[8] = 64;    // TTL
  pkt[9] = 17;    // UDP
  for (int i = 0; i < 4; ++i) {
    pkt[12 + i] = overlay[i];
    pkt[16 + i] = host[i];
  }
  pkt[20] = static_cast<std::uint8_t>(lan_port >> 8);
  pkt[21] = static_cast<std::uint8_t>(lan_port & 0xFF);
  pkt[22] = static_cast<std::uint8_t>(kHostAnnounceUdpPort >> 8);
  pkt[23] = static_cast<std::uint8_t>(kHostAnnounceUdpPort & 0xFF);
  pkt[24] = static_cast<std::uint8_t>(udp_len >> 8);
  pkt[25] = static_cast<std::uint8_t>(udp_len & 0xFF);
  std::copy(payload.begin(), payload.end(), pkt.begin() + 28);

  DivertAddress a{};
  a.Outbound = 1;
  a.Impostor = 1;
  a.IPChecksum = 0;
  a.TCPChecksum = 0;
  a.UDPChecksum = 0;
  api_.calc_checksums_(pkt.data(), static_cast<UINT>(pkt.size()), &a, 0);
  UINT written = static_cast<UINT>(pkt.size());
  const bool ok = api_.send_(h, pkt.data(), static_cast<UINT>(pkt.size()), &written, &a);
  {
    std::lock_guard<std::mutex> lock(mutex_);
    if (ok) {
      ++counters_.announcements_sent;
    } else {
      last_error_ = "WinDivertSend (announce) failed: " + LastErrorString(GetLastError());
    }
  }
  if (ok) {
    blog::Debug("introduced ourselves to host %s as %s (host will reply to %s:%u)",
                Ipv4ToString(host).c_str(), overlay_str.c_str(),
                Ipv4ToString(lan).c_str(), static_cast<unsigned>(lan_port));
  }
  return ok;
}

void Bridge::BeatLoop() {
  // WinDivertRecv blocks, so liveness needs its own thread: every 15s print
  // counters while the bridge is up. Quiet ≠ dead — and zero captured
  // packets gets an explicit hint, because that usually means Blur has not
  // refreshed its LAN list yet (or the filter matches nothing).
  int elapsed = 0;
  while (active_.load()) {
    for (int i = 0; i < 15 && active_.load(); ++i) {
#ifdef _WIN32
      Sleep(1000);
#else
      std::this_thread::sleep_for(std::chrono::seconds(1));
#endif
    }
    if (!active_.load()) break;
    elapsed += 15;
    {
      std::lock_guard<std::mutex> lock(mutex_);
      if (counters_.captured == 0) {
        blog::Info("alive, watching (%ds): 0 packets so far — open Blur and refresh its LAN list.",
                   elapsed);
      } else {
        blog::Info("alive (%ds): captured=%lld forwarded=%lld dropped=%lld errors=%lld",
                   elapsed, (long long)counters_.captured, (long long)counters_.forwarded,
                   (long long)counters_.dropped, (long long)counters_.injection_errors);
      }
      if (!last_error_.empty()) {
        blog::Warn("last error: %s", last_error_.c_str());
      }
    }
    // Keep the host's picture of us fresh, so a host that starts its own host
    // mode late still learns where to send our replies. Bounded and cheap: one
    // small packet per beat, and it does nothing until we have seen a forward.
    SendAnnounce();
  }
}

void Bridge::WorkerLoop() {
  std::vector<std::uint8_t> buf(kMaxPacket);
  auto limiter = std::make_unique<RateLimiter>(cfg_.rate_per_second, cfg_.rate_burst);
  DedupCache dedup;
  int consecutive_recv_failures = 0;
  bool announced_first_fwd = false;

  while (active_.load()) {
    DivertAddress addr{};
    UINT recv_len = 0;
    HANDLE h = nullptr;
    {
      std::lock_guard<std::mutex> lock(mutex_);
      h = divert_;
    }
    if (!h) break;
    if (!api_.recv_(h, buf.data(), static_cast<UINT>(buf.size()), &recv_len, &addr)) {
      DWORD code = GetLastError();
      if (!active_.load()) break;  // Stop() woke us.
      std::lock_guard<std::mutex> lock(mutex_);
      counters_.injection_errors++;
      last_error_ = "WinDivertRecv failed: " + LastErrorString(code);
      // Backoff on persistent failures: a permanently failing recv would
      // otherwise hot-spin the CPU and flood the log with identical errors.
      // Transient failures still recover immediately.
      ++consecutive_recv_failures;
      if (consecutive_recv_failures >= 8) {
        Sleep(50);
      }
      continue;
    }
    consecutive_recv_failures = 0;
    // We only diverted OUTBOUND (filter), but verify direction defensively.
    if (addr.Outbound == 0) {
      UINT w = recv_len;
      api_.send_(h, buf.data(), recv_len, &w, &addr);
      continue;
    }

    UdpPacketView view;
    std::string reject;
    bool parsed = TryParseUdpOverIpv4(buf.data(), recv_len, view, reject);

    PacketEventMeta meta;
    meta.timestamp_iso = UtcNowIso();
    meta.size = static_cast<int>(recv_len);
    if (parsed) {
      meta.src_ip = Ipv4ToString(view.src_ip);
      meta.src_port = view.src_port;
      meta.orig_dst_ip = Ipv4ToString(view.dst_ip);
      meta.orig_dst_port = view.dst_port;
    }

    // Reinjects the ORIGINAL packet byte-for-byte.
    //
    // MUST NOT be called with mutex_ already held: it takes that same lock to
    // count the reinject, and std::mutex is not recursive, so a second acquire
    // on the same thread deadlocks -- silently, and permanently, because the
    // command loop needs the same lock and so cannot even answer `stop`.
    //
    // Four "we decided not to forward this" branches used to call it from
    // inside a held lock. Nothing caught it because every unelevated test
    // bails out at WinDivertOpen, so WorkerLoop never ran; the first elevated
    // bridge harness hung on the payload-prefix path. Every call site below
    // now closes its lock scope first.
    auto send_original = [&](bool count) {
      if (!cfg_.preserve_original_broadcast) return;
      DivertAddress oaddr = addr;  // reuse recv'd addr: flags already valid
      oaddr.Outbound = 1;
      UINT w = recv_len;
      if (api_.send_(h, buf.data(), recv_len, &w, &oaddr)) {
        if (count) {
          std::lock_guard<std::mutex> lock(mutex_);
          counters_.reinjected++;
        }
      } else {
        std::lock_guard<std::mutex> lock(mutex_);
        counters_.injection_errors++;
        last_error_ = "WinDivertSend (original) failed: " + LastErrorString(GetLastError());
      }
    };

    {
      std::lock_guard<std::mutex> lock(mutex_);
      counters_.captured++;
    }

    if (!parsed) {
      // Fragments / non-UDP / truncated: never touch, reinject, count.
      {
        std::lock_guard<std::mutex> lock(mutex_);
        if (reject == "ip-fragment") counters_.fragments_rejected++;
        meta.action = "rejected-" + (reject.empty() ? std::string("parse") : reject);
        PushRecent(meta);
      }
      blog::Debug("reinjected unchanged (%s): never forwarded without a full UDP view",
                  meta.action.c_str());
      send_original(false);
      // Count reinject of rejected packets too.
      {
        std::lock_guard<std::mutex> lock(mutex_);
        if (cfg_.preserve_original_broadcast) counters_.reinjected++;
      }
      continue;
    }

    // Code-side payload-prefix gate (the WinDivert filter itself stays
    // port+address only, which keeps it provably narrow).
    if (!cfg_.payload_prefix.empty() &&
        !IsPayloadPrefixMatch(buf.data(), recv_len, cfg_.payload_prefix.data(),
                              cfg_.payload_prefix.size())) {
      // Filter over-matched (shouldn't happen): preserve original only.
      {
        std::lock_guard<std::mutex> lock(mutex_);
        ++counters_.payload_gate_skipped;
        meta.action = "reinjected";
        PushRecent(meta);
      }
      send_original(true);
      continue;
    }

    // Echo backstop: identical identity within TTL => our own reinject.
    PacketIdentity id{view.src_ip, view.dst_ip, view.src_port, view.dst_port, view.ip_id,
                      static_cast<std::uint16_t>(view.payload_len + 8)};
    const bool is_echo = dedup.CheckAndRecord(id);
    // Debug level: the exact key. "Why was this broadcast forwarded?" is
    // answerable only with the six fields that form the identity -- in
    // particular the IP ID, which is what distinguishes a genuine Blur
    // retransmit from an echo of our own reinjection.
    blog::Debug("dedup: %s:%u -> %s:%u id=%u udpLen=%u => %s",
                Ipv4ToString(id.src_ip).c_str(), static_cast<unsigned>(id.src_port),
                Ipv4ToString(id.dst_ip).c_str(), static_cast<unsigned>(id.dst_port),
                static_cast<unsigned>(id.ip_id), static_cast<unsigned>(id.udp_len),
                is_echo ? "echo (no clone)" : "new (clone)");
    if (is_echo) {
      {
        std::lock_guard<std::mutex> lock(mutex_);
        counters_.dedup_skipped++;
        meta.action = "dropped-echo";
        PushRecent(meta);
      }
      blog::Debug("echo of our own clone suppressed (%s:%d -> :%d)",
                  meta.src_ip.c_str(), meta.src_port, meta.orig_dst_port);
      send_original(true);
      continue;
    }

    if (!limiter->TryAcquire()) {
      {
        std::lock_guard<std::mutex> lock(mutex_);
        counters_.dropped++;
        meta.action = "dropped-rate";
        PushRecent(meta);
      }
      blog::Debug("[drop] rate limit (%s:%d -> :%d)",
                  meta.src_ip.c_str(), meta.src_port, meta.orig_dst_port);
      send_original(true);
      continue;
    }

    // Clone: ONLY destination IP changes; then official checksum recalc.
    bool first_forward_now = false;
    std::vector<std::uint8_t> clone;
    try {
      clone = CloneWithNewDestination(buf.data(), recv_len, cfg_.host_ip);
    } catch (const std::exception& ex) {
      {
        std::lock_guard<std::mutex> lock(mutex_);
        counters_.dropped++;
        last_error_ = ex.what();
        meta.action = "error";
        PushRecent(meta);
      }
      send_original(true);
      continue;
    }

    DivertAddress caddr = addr;
    caddr.Outbound = 1;   // outbound
    caddr.Impostor = 1;   // new packet: TTL-decrement loop defense per docs
    caddr.IPChecksum = 0;
    caddr.TCPChecksum = 0;
    caddr.UDPChecksum = 0;
    // Belt and suspenders: official helper recalc on the final bytes.
    api_.calc_checksums_(clone.data(), static_cast<UINT>(clone.size()), &caddr, 0);
    UINT w = static_cast<UINT>(clone.size());
    bool sent = api_.send_(h, clone.data(), static_cast<UINT>(clone.size()), &w, &caddr);
    {
      std::lock_guard<std::mutex> lock(mutex_);
      if (sent) {
        counters_.forwarded++;
        meta.forwarded_dst_ip = cfg_.host_ip_str;
        meta.action = "forwarded";
        blog::Info("[fwd] %s:%d -> %s:%d  => %s:%d  len=%d",
                   meta.src_ip.c_str(), meta.src_port, meta.orig_dst_ip.c_str(),
                   meta.orig_dst_port, meta.forwarded_dst_ip.c_str(), meta.orig_dst_port,
                   meta.size);
        if (!announced_first_fwd) {
          announced_first_fwd = true;
          // This packet's source address and port are exactly what the host's
          // Blur will reply to, so they are what our introduction must name.
          last_src_ip_ = view.src_ip;
          last_src_port_ = view.src_port;
          have_forward_source_ = true;
          first_forward_now = true;
          blog::Info("first broadcast forwarded to the host - refresh Blur's LAN list "
                     "and the lobby should appear (host must have a game open).");
        }
      } else {
        counters_.injection_errors++;
        last_error_ = "WinDivertSend (clone) failed: " + LastErrorString(GetLastError());
        meta.action = "error";
        blog::Error("inject clone failed: %s", last_error_.c_str());
      }
      PushRecent(meta);
    }
    // Introduce ourselves as soon as we know the address the host will reply to
    // (outside the lock: SendAnnounce takes it itself).
    if (first_forward_now) SendAnnounce();
    send_original(true);
  }

  // Last statement, whatever path left the loop: Stop() waits on this before
  // joining, so a blocked recv cannot make Stop() hang without bound.
  worker_exited_.store(true);
}

Sniffer::Sniffer() = default;

Sniffer::~Sniffer() { Stop(); }

std::string Sniffer::filter() const {
  std::lock_guard<std::mutex> lock(mutex_);
  return filter_;
}

std::string Sniffer::last_error() const {
  std::lock_guard<std::mutex> lock(mutex_);
  return last_error_;
}

std::int64_t Sniffer::observed() const {
  std::lock_guard<std::mutex> lock(mutex_);
  return observed_;
}

std::vector<SniffPortCount> Sniffer::results() const {
  std::lock_guard<std::mutex> lock(mutex_);
  std::vector<SniffPortCount> out;
  for (const auto& [key, count] : per_port_) {
    out.push_back(SniffPortCount{static_cast<int>(key.second), count, key.first});
  }
  std::sort(out.begin(), out.end(),
            [](const SniffPortCount& a, const SniffPortCount& b) { return a.count > b.count; });
  return out;
}

bool Sniffer::Start(const SniffConfig& cfg) {
  Stop();
  {
    std::lock_guard<std::mutex> lock(mutex_);
    observed_ = 0;
    per_port_.clear();
    last_error_.clear();
  }
  SniffConfig validated = cfg;
  if (auto r = ValidateSniffConfig(validated); !r.ok) {
    std::lock_guard<std::mutex> lock(mutex_);
    last_error_ = "Invalid sniff parameters: " + r.error;
    return false;
  }
  std::string filter;
  if (auto r = BuildSniffFilterString(validated, filter); !r.ok) {
    std::lock_guard<std::mutex> lock(mutex_);
    last_error_ = "Invalid sniff parameters: " + r.error;
    return false;
  }
  std::string load_err;
  if (!api_.Load(load_err)) {
    std::lock_guard<std::mutex> lock(mutex_);
    last_error_ = load_err;
    return false;
  }
  // SNIFF flag: observe only. Packets are never blocked, modified, or reinjected.
  HANDLE h = api_.open_(filter.c_str(), kDivertLayerNetwork, 0 /*priority*/, 1 /*WINDIVERT_FLAG_SNIFF*/);
  if (!h || h == INVALID_HANDLE_VALUE) {
    DWORD code = GetLastError();
    std::lock_guard<std::mutex> lock(mutex_);
    if (code == 5) {
      last_error_ = "WinDivertOpen failed: access denied. The helper must run elevated (UAC).";
    } else if (code == 2) {
      last_error_ = "WinDivertOpen failed: driver not found (code 2). Install WinDivert64.sys per "
                    "third-party/WinDivert/README.md.";
    } else if (code == 87) {
      last_error_ = "WinDivert rejected the filter string (code 87): '" + filter + "'.";
    } else {
      last_error_ = "WinDivertOpen failed: " + LastErrorString(code);
    }
    return false;
  }
  {
    std::lock_guard<std::mutex> lock(mutex_);
    cfg_ = validated;
    filter_ = filter;
    divert_ = h;
  }
  active_.store(true);
  worker_ = std::thread(&Sniffer::WorkerLoop, this, validated.duration_sec, validated.max_packets);
  // WinDivertRecv blocks with no timeout, so a quiet network would keep the
  // window open forever. A dedicated deadline thread wakes the blocked recv
  // via WinDivertShutdown at the expiry so the sniff always finishes.
  deadline_ = std::thread(&Sniffer::DeadlineLoop, this,
                          std::chrono::steady_clock::now() + std::chrono::seconds(validated.duration_sec));
  blog::Info("sniff started (SNIFF mode: observing only, nothing blocked)");
  blog::Info("  filter : %s", filter.c_str());
  blog::Info("  window : %ds or %d packets - refresh Blur's LAN list now", validated.duration_sec,
             validated.max_packets);
  return true;
}

void Sniffer::Stop() {
  active_.store(false);
  HANDLE h = nullptr;
  {
    std::lock_guard<std::mutex> lock(mutex_);
    h = divert_;
    divert_ = nullptr;
  }
  if (h && h != INVALID_HANDLE_VALUE) {
    if (api_.shutdown_) api_.shutdown_(h, kDivertShutdownRecv);
  }
  if (worker_.joinable()) worker_.join();
  if (deadline_.joinable()) deadline_.join();
  if (h && h != INVALID_HANDLE_VALUE && api_.close_) {
    api_.close_(h);
  }
}

void Sniffer::DeadlineLoop(std::chrono::steady_clock::time_point deadline) {
  for (;;) {
    if (!active_.load()) return;  // stopped or worker finished early
    if (std::chrono::steady_clock::now() >= deadline) break;
    std::this_thread::sleep_for(std::chrono::milliseconds(100));
  }
  HANDLE h = nullptr;
  {
    std::lock_guard<std::mutex> lock(mutex_);
    h = divert_;
  }
  if (h && h != INVALID_HANDLE_VALUE && api_.shutdown_) {
    // Clear active_ first so the worker treats the woken recv as a normal
    // stop (no spurious error), then unblock WinDivertRecv.
    active_.store(false);
    api_.shutdown_(h, kDivertShutdownRecv);
  }
}

void Sniffer::WorkerLoop(int duration_sec, int max_packets) {
  std::vector<std::uint8_t> buf(kMaxPacket);
  auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(duration_sec);
  while (active_.load()) {
    if (std::chrono::steady_clock::now() >= deadline) break;
    HANDLE h = nullptr;
    {
      std::lock_guard<std::mutex> lock(mutex_);
      h = divert_;
    }
    if (!h) break;
    DivertAddress addr{};
    UINT recv_len = 0;
    if (!api_.recv_(h, buf.data(), static_cast<UINT>(buf.size()), &recv_len, &addr)) {
      DWORD code = GetLastError();
      if (!active_.load()) break;  // Stop() woke us.
      std::lock_guard<std::mutex> lock(mutex_);
      last_error_ = "WinDivertRecv failed: " + LastErrorString(code);
      continue;
    }
    // SNIFF mode: nothing is reinjected — the stack already has the packet.
    UdpPacketView view;
    std::string reject;
    {
      std::lock_guard<std::mutex> lock(mutex_);
      ++observed_;
      if (TryParseUdpOverIpv4(buf.data(), recv_len, view, reject)) {
        // Metadata only: ports + sender, never payloads.
        std::string ip;
        std::uint16_t key_port = view.dst_port;
        if (cfg_.inbound) {
          ip = Ipv4ToString(view.src_ip);
          key_port = view.src_port;
        }
        std::int64_t n = ++per_port_[{ip, key_port}];
        if (cfg_.inbound) {
          blog::Info("[heard] reply %s:%d -> us:%d  (sender total %lld, overall %lld)",
                     ip.c_str(), key_port, view.dst_port, (long long)n, (long long)observed_);
        } else {
          blog::Info("[heard] udp *:* -> %s:%d  (port total %lld, overall %lld)",
                     Ipv4ToString(view.dst_ip).c_str(), key_port, (long long)n,
                     (long long)observed_);
        }
      }
      if (observed_ >= max_packets) {
        active_.store(false);
        break;
      }
    }
  }
  active_.store(false);
  {
    std::lock_guard<std::mutex> lock(mutex_);
    if (per_port_.empty()) {
      blog::Info("sniff finished: heard nothing (observed=%lld).", (long long)observed_);
      blog::Info("hints: keep Blur open and refresh its LAN list DURING the window;");
      blog::Info("       check the direction (out = Blur's broadcasts, in = host replies);");
      blog::Info("       the filter only sees broadcast/reply traffic on the chosen ports.");
    } else {
      blog::Info("sniff finished: observed=%lld", (long long)observed_);
      // Emit top-5 table, most frequent first.
      std::vector<std::pair<std::pair<std::string, std::uint16_t>, std::int64_t>> sorted(
          per_port_.begin(), per_port_.end());
      std::sort(sorted.begin(), sorted.end(),
                [](const auto& a, const auto& b) { return a.second > b.second; });
      for (std::size_t i = 0; i < sorted.size() && i < 5; ++i) {
        if (sorted[i].first.first.empty()) {
          blog::Info("  port %5d : %lld packet(s)", sorted[i].first.second,
                     (long long)sorted[i].second);
        } else {
          blog::Info("  from %-15s port %5d : %lld packet(s)", sorted[i].first.first.c_str(),
                     sorted[i].first.second, (long long)sorted[i].second);
        }
      }
      blog::Info("verdict: outbound -> the top port is the discovery-port candidate (Apply in the GUI).");
      blog::Info("        inbound -> a line above from the host's IP means the reply path works.");
    }
  }
}

}  // namespace blurlink

#endif  // _WIN32
