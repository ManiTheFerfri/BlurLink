// blurlink-net.exe — elevated WinDivert helper. Windows-only.
// Usage:
//   blurlink-net.exe --pipe <name> --token <64-hex>
//                    [--watchdog-sec N] [--log-file <path>] [--verbose]
// The GUI launches this elevated (UAC); it exits on `shutdown`, on GUI
// disconnect policy (watchdog), or when the bridge is stopped + shutdown
// requested. This process NEVER listens on the network — local pipe only.
//
// Console dashboard: the console IS the helper's story. Startup, every
// command, every decision, every error — with plain-language hints — is
// written here (and mirrored to --log-file when given). `--verbose` adds
// per-packet reasoning and per-poll chatter for debugging sessions.

#ifdef _WIN32

#include <windows.h>

#include <sys/stat.h>

#include <atomic>
#include <chrono>
#include <cstdint>
#include <csignal>
#include <iostream>
#include <sstream>
#include <string>

#include "blurlink/bridge.h"
#include "blurlink/config.h"
#include "blurlink/host_session.h"
#include "blurlink/console_log.h"
#include "blurlink/ipc.h"
#include "blurlink/json_min.h"

namespace {

std::atomic<bool> g_shutdown{false};
// Watchdog: GUI polls with get_status ~every 1.5s. If authenticated traffic
// stops (GUI crashed/killed), the helper stops the bridge, closes WinDivert,
// and exits instead of diverting forever. Default 15s, --watchdog-sec 2..300.
// Before the first authenticated command, a fixed 120s grace applies.
int g_watchdog_sec = 15;
std::atomic<std::int64_t> g_last_activity_ms{0};
std::atomic<bool> g_ever_active{false};
bool g_verbose = false;

// Bridge-session exclusivity (defense in depth behind the GUI's own
// single-instance guard): a machine-global named event held only while a real
// bridge is active, so two helpers (two GUI copies, a stale instance, or a
// manually started helper) can never divert the same broadcasts at once.
// Events carry no thread ownership, so acquire/release work from any thread,
// and the kernel destroys the object when the last handle closes - a crashed
// helper leaves no stale lock. Sniff-only sessions skip this: observing
// traffic is harmless to duplicate.
HANDLE g_bridge_session = nullptr;

bool AcquireBridgeSession(std::string& err) {
  if (g_bridge_session) return true;  // already ours (re-start / sniff switch)
  SetLastError(0);
  HANDLE h = CreateEventA(nullptr, TRUE /*manual-reset*/, FALSE,
                          "Global\\BlurLink-BridgeSession");
  if (!h) {
    err = "cannot create the bridge-session lock (error " +
          std::to_string(GetLastError()) + ")";
    return false;
  }
  if (GetLastError() == ERROR_ALREADY_EXISTS) {
    CloseHandle(h);
    err = "another BlurLink bridge is already active on this PC";
    return false;
  }
  g_bridge_session = h;
  return true;
}

void ReleaseBridgeSession() {
  if (!g_bridge_session) return;
  CloseHandle(g_bridge_session);
  g_bridge_session = nullptr;
}

// Ends a bridge and drops the exclusive session in one step, so every path
// that stops diverting (stop, shutdown, discovery sniff, process exit) frees
// it for the next run.
void StopBridgeAndReleaseSession(blurlink::Bridge& bridge) {
  bridge.Stop();
  ReleaseBridgeSession();
}

std::int64_t NowMs() {
  return std::chrono::duration_cast<std::chrono::milliseconds>(
             std::chrono::steady_clock::now().time_since_epoch())
      .count();
}

void OnSignal(int) { g_shutdown.store(true); }

bool IsValidPipeName(const std::string& n) {
  const std::string prefix = "BlurLink-";
  if (n.rfind(prefix, 0) != 0) return false;
  std::string suf = n.substr(prefix.size());
  if (suf.size() != 16) return false;
  for (char c : suf) {
    if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
      return false;
  }
  return true;
}

bool IsValidToken(const std::string& t) {
  if (t.size() != 64) return false;
  for (char c : t) {
    if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
      return false;
  }
  return true;
}

// Manifest-proof Windows 10+ check via RtlGetVersion (VerifyVersionInfo /
// IsWindows10OrGreater lie without an app manifest, so they must not gate).
bool IsWindows10OrGreater() {
  struct OsVersionInfoW {
    ULONG size = sizeof(OsVersionInfoW);
    ULONG major = 0;
    ULONG minor = 0;
    ULONG build = 0;
    ULONG platform = 0;
    WCHAR csd[128]{};
  };
  using FnRtlGetVersion = LONG(WINAPI*)(OsVersionInfoW*);
  HMODULE ntdll = GetModuleHandleW(L"ntdll.dll");
  if (!ntdll) return false;
#if defined(__GNUC__) && !defined(__clang__)
#pragma GCC diagnostic push
#pragma GCC diagnostic ignored "-Wcast-function-type"
#endif
  auto fn = reinterpret_cast<FnRtlGetVersion>(GetProcAddress(ntdll, "RtlGetVersion"));
#if defined(__GNUC__) && !defined(__clang__)
#pragma GCC diagnostic pop
#endif
  if (!fn) return false;
  OsVersionInfoW info;
  if (fn(&info) != 0) return false;
  return info.major >= 10;
}

// Creates every missing directory of --log-file's parent path. Returns the
// created count, or -1 on failure (caller degrades to a clear hint instead
// of the old silent exit 6).
int EnsureParentDirs(const std::string& path) {
  if (path.empty()) return 0;
  std::string dir = path;
  const std::size_t slash = dir.find_last_of("\\/");
  if (slash == std::string::npos) return 0;  // relative to CWD, nothing to make
  dir.resize(slash);
  if (dir.empty()) return 0;
  int created = 0;
  for (std::size_t i = 1; i <= dir.size(); ++i) {
    // Create each path prefix; skip drive roots (e.g. "C:").
    if (dir[i] == '\\' || dir[i] == '/' || i == dir.size()) {
      std::string part = dir.substr(0, i);
      if (part.size() >= 2 && part[1] == ':') continue;
      if (CreateDirectoryA(part.c_str(), nullptr)) ++created;
    }
  }
  return created;
}

std::string StatusJson(const blurlink::Bridge& bridge, const blurlink::Sniffer& sniffer,
                       const blurlink::HostSession& host) {
  using blurlink::minjson::Escape;
  auto c = bridge.counters();
  auto recent = bridge.recent();
  auto sniff = sniffer.results();
  const auto hc = host.counters();
  const auto hplayers = host.players();
  std::ostringstream os;
  os << "{\"type\":\"status\",\"active\":" << (bridge.active() ? "true" : "false")
     << ",\"watchdogSec\":" << g_watchdog_sec
     << ",\"filter\":\"" << Escape(bridge.filter()) << "\""
     << ",\"captured\":" << c.captured << ",\"forwarded\":" << c.forwarded
     << ",\"reinjected\":" << c.reinjected << ",\"dropped\":" << c.dropped
     << ",\"injectionErrors\":" << c.injection_errors
     << ",\"fragmentsRejected\":" << c.fragments_rejected
     << ",\"dedupSkipped\":" << c.dedup_skipped
     << ",\"announcementsSent\":" << c.announcements_sent
     << ",\"lastError\":\"" << Escape(bridge.last_error()) << "\""
     << ",\"routeInterface\":\"" << Escape(bridge.route_interface()) << "\""
     << ",\"sniffActive\":" << (sniffer.active() ? "true" : "false")
     << ",\"sniffResults\":[";
  bool sfirst = true;
  for (const auto& r : sniff) {
    if (!sfirst) os << ',';
    sfirst = false;
    os << "{\"port\":" << r.port << ",\"count\":" << r.count << ",\"srcIp\":\""
       << Escape(r.src_ip) << "\"}";
  }
  // Host mode. `forwardsHeard` is the prerequisite diagnostic: zero means the
  // host is not receiving player forwards at all, so the GUI can say so instead
  // of implying a firewall problem.
  // `filter` above is the bridge's, so host mode reports its own. Without this
  // the Host tab's filter line is always blank: the two sessions are mutually
  // exclusive, so `filter` is empty exactly when host mode is the one running.
  os << "],\"hostActive\":" << (host.active() ? "true" : "false")
     << ",\"hostFilter\":\"" << Escape(host.filter()) << "\""
     << ",\"hostForwardsHeard\":" << hc.forwards_heard
     << ",\"hostRepliesForwarded\":" << hc.replies_forwarded
     << ",\"hostBroadcastReplies\":" << hc.broadcast_replies
     << ",\"hostAmbiguousReplies\":" << hc.ambiguous_replies
     << ",\"hostUnmatchedReplies\":" << hc.unmatched_replies
     << ",\"hostReinjected\":" << hc.reinjected
     << ",\"hostInjectionErrors\":" << hc.injection_errors
     << ",\"hostAnnounceRejected\":" << hc.announce_rejected
     << ",\"hostFilterReopens\":" << hc.filter_reopens
     << ",\"hostCollisions\":" << host.collisions()
     << ",\"hostLastNote\":\"" << Escape(host.last_note()) << "\""
     << ",\"hostPlayers\":[";
  bool hfirst = true;
  for (const auto& p : hplayers) {
    if (!hfirst) os << ',';
    hfirst = false;
    os << "{\"overlayIp\":\"" << Escape(blurlink::Ipv4ToString(p.overlay))
       << "\",\"lanIp\":\"" << Escape(blurlink::Ipv4ToString(p.lan))
       << "\",\"blurSourcePort\":" << p.blur_src_port
       << ",\"firstSeenMs\":" << p.first_seen_ms
       << ",\"lastSeenMs\":" << p.last_seen_ms
       << ",\"forwardsHeard\":" << p.forwards_heard
       << ",\"repliesForwarded\":" << p.replies_forwarded
       << ",\"inFilter\":" << (p.in_filter ? "true" : "false") << "}";
  }
  os << "],\"recent\":[";
  bool first = true;
  for (const auto& e : recent) {
    if (!first) os << ',';
    first = false;
    os << "{\"type\":\"event_packet\",\"timestampUtc\":\"" << Escape(e.timestamp_iso)
       << "\",\"srcIp\":\"" << Escape(e.src_ip) << "\",\"srcPort\":" << e.src_port
       << ",\"origDstIp\":\"" << Escape(e.orig_dst_ip) << "\",\"origDstPort\":" << e.orig_dst_port
       << ",\"forwardedDstIp\":\"" << Escape(e.forwarded_dst_ip) << "\",\"size\":" << e.size
       << ",\"action\":\"" << Escape(e.action) << "\"}";
  }
  os << "]}";
  return os.str();
}

// Builds BridgeConfig from a validated `start` body. Every field is parsed
// and range-checked; nothing is concatenated into the filter unvalidated.
bool ConfigFromStart(const std::string& body, blurlink::BridgeConfig& out, std::string& err) {
  using namespace blurlink;
  std::string host, bcast, hex;
  long long port = 0, rate = 10, burst = 20, ifidx = 0;
  bool preserve = true;
  if (!minjson::GetString(body, "hostOverlayIp", host)) {
    err = "missing hostOverlayIp";
    return false;
  }
  if (!minjson::GetInt(body, "discoveryUdpPort", port)) {
    err = "missing discoveryUdpPort";
    return false;
  }
  if (!minjson::GetString(body, "broadcastDestination", bcast)) {
    bcast = "255.255.255.255";
  }
  minjson::GetString(body, "payloadPrefixHex", hex);
  minjson::GetBool(body, "preserveOriginalBroadcast", preserve);
  if (minjson::GetInt(body, "rateLimitPerSecond", rate)) {
  }
  if (minjson::GetInt(body, "rateLimitBurst", burst)) {
  }
  minjson::GetInt(body, "adapterIfIndex", ifidx);

  // Host-mode support (optional): our own overlay address, so we can introduce
  // ourselves to a host running host mode. Absent/invalid simply means we do not
  // announce - the bridge works exactly as before, it just cannot be mapped.
  std::string local_overlay;
  const bool have_local_overlay = minjson::GetString(body, "localOverlayIp", local_overlay) &&
                                  !local_overlay.empty();
  bool announce = true;
  minjson::GetBool(body, "announceToHost", announce);

  std::array<std::uint8_t, 4> host_ip{};
  if (!TryParseIpv4(host, host_ip)) {
    err = "hostOverlayIp must be a valid IPv4 address";
    return false;
  }
  if (port < 1 || port > 65535) {
    err = "discoveryUdpPort must be 1-65535 (no implicit default)";
    return false;
  }
  std::array<std::uint8_t, 4> bcast_ip{};
  if (!TryParseIpv4(bcast, bcast_ip)) {
    err = "broadcastDestination must be a valid IPv4 address";
    return false;
  }
  if (auto r = ValidateBroadcastDestination(bcast); !r.ok) {
    err = r.error;
    return false;
  }
  if (have_local_overlay) {
    std::array<std::uint8_t, 4> local_ip{};
    if (!TryParseIpv4(local_overlay, local_ip)) {
      err = "localOverlayIp must be a valid IPv4 address";
      return false;
    }
    out.local_overlay_ip = local_ip;
    out.local_overlay_ip_str = Ipv4ToString(local_ip);
  }
  out.announce_to_host = announce && have_local_overlay;

  std::vector<std::uint8_t> prefix;
  if (auto r = ParseHexSignature(hex, prefix); !r.ok) {
    err = r.error;
    return false;
  }
  if (rate < 1 || rate > kMaxRatePerSecond || burst < 1 || burst > kMaxRateBurst) {
    err = "rate limit out of range";
    return false;
  }

  out.host_ip = host_ip;
  out.host_ip_str = Ipv4ToString(host_ip);
  out.discovery_port = static_cast<int>(port);
  out.broadcast = bcast_ip;
  out.broadcast_str = Ipv4ToString(bcast_ip);
  out.payload_prefix = std::move(prefix);
  out.preserve_original_broadcast = preserve;
  out.rate_per_second = static_cast<int>(rate);
  out.rate_burst = static_cast<int>(burst);
  out.adapter_if_index = static_cast<int>(ifidx);
  return true;
}

}  // namespace

// Builds HostConfig from a validated `start_host` body. Like ConfigFromStart,
// every field is parsed and range-checked before anything touches the filter.
bool ConfigFromHostStart(const std::string& body, blurlink::HostConfig& out, std::string& err) {
  using namespace blurlink;
  long long port = 0, rate = 10, burst = 20, ifidx = 0;
  if (!minjson::GetInt(body, "discoveryUdpPort", port)) {
    err = "missing discoveryUdpPort (detect it first)";
    return false;
  }
  minjson::GetInt(body, "rateLimitPerSecond", rate);
  minjson::GetInt(body, "rateLimitBurst", burst);
  minjson::GetInt(body, "adapterIfIndex", ifidx);

  out.discovery_port = static_cast<int>(port);
  out.adapter_if_index = static_cast<int>(ifidx);
  out.rate_per_second = static_cast<int>(rate);
  out.rate_burst = static_cast<int>(burst);

  // The same validation the portable tests exercise; the helper never trusts
  // the GUI any more than it does for a bridge start.
  if (auto r = ValidateHostConfig(out); !r.ok) {
    err = r.error;
    return false;
  }
  return true;
}

// Builds SniffConfig from a validated `sniff` body.
bool ConfigFromSniff(const std::string& body, blurlink::SniffConfig& out, std::string& err) {
  using namespace blurlink;
  std::vector<std::string> broadcasts;
  std::string direction;
  long long duration = 15, max_packets = 200, port = 0;
  minjson::GetStringArray(body, "broadcasts", broadcasts);  // optional for inbound
  minjson::GetString(body, "direction", direction);
  minjson::GetInt(body, "durationSec", duration);
  minjson::GetInt(body, "maxPackets", max_packets);
  minjson::GetInt(body, "port", port);
  out.inbound = (direction == "in");
  if (!direction.empty() && direction != "out" && direction != "in") {
    err = "unknown sniff direction (want 'out' or 'in')";
    return false;
  }
  out.broadcasts.clear();
  for (const auto& b : broadcasts) {
    std::array<std::uint8_t, 4> ip{};
    if (!TryParseIpv4(b, ip)) {
      err = "invalid broadcast destination: " + b;
      return false;
    }
    out.broadcasts.push_back(ip);
  }
  out.duration_sec = static_cast<int>(duration);
  out.max_packets = static_cast<int>(max_packets);
  out.port = static_cast<int>(port);
  SniffConfig copy = out;
  if (auto r = ValidateSniffConfig(copy); !r.ok) {
    err = r.error;
    return false;
  }
  out.broadcast_strs = copy.broadcast_strs;
  return true;
}

int main(int argc, char** argv) {
  signal(SIGINT, OnSignal);
  signal(SIGTERM, OnSignal);

  blurlink::blog::Logger::Instance().InitWindowsConsole();

  std::string pipe, token, logfile;
  for (int i = 1; i < argc; ++i) {
    std::string a = argv[i];
    if ((a == "--pipe" || a == "--token" || a == "--watchdog-sec" || a == "--log-file") &&
        i + 1 < argc) {
      if (a == "--pipe")
        pipe = argv[++i];
      else if (a == "--token")
        token = argv[++i];
      else if (a == "--log-file")
        logfile = argv[++i];
      else {
        try {
          g_watchdog_sec = std::stoi(argv[++i]);
        } catch (...) {
          g_watchdog_sec = -1;
        }
      }
    } else if (a == "--verbose" || a == "-v") {
      g_verbose = true;
      blurlink::blog::Logger::Instance().set_level(blurlink::blog::Level::kDebug);
    } else if (a == "--help" || a == "-h") {
      std::cout << "blurlink-net.exe --pipe <name> --token <64-hex>"
                << " [--watchdog-sec N] [--log-file <path>] [--verbose]\n";
      return 0;
    }
  }
  if (!IsValidPipeName(pipe)) {
    std::cerr << "blurlink-net: invalid --pipe name (expected BlurLink-<16 hex>).\n";
    return 2;
  }
  if (!IsValidToken(token)) {
    std::cerr << "blurlink-net: invalid --token (expected 64 hex chars).\n";
    return 2;
  }
  if (g_watchdog_sec < 2 || g_watchdog_sec > 300) {
    std::cerr << "blurlink-net: --watchdog-sec must be 2..300.\n";
    return 2;
  }

  // Optional file mirror of the dashboard (metadata only). The parent
  // directory is created on demand so a fresh machine cannot kill the helper
  // just because %LocalAppData%\BlurLink\logs\ does not exist yet. An
  // unwritable path (locked drive, bad letter) still fails fast with a
  // plain-language explanation.
  if (!logfile.empty()) {
    EnsureParentDirs(logfile);
    if (!blurlink::blog::Logger::Instance().log_to_file(logfile)) {
      std::cerr << "blurlink-net: cannot open the log file for writing: " << logfile << "\n"
                << "  Check the path (drive letter, permissions, antivirus lock).\n";
      return 6;
    }
    blurlink::blog::Info("log file open (plain-text mirror of this console)");
  }

  // Single instance per pipe name: named mutex.
  std::string mutex_name = "Global\\BlurLink-" + pipe;
  HANDLE mutex = CreateMutexA(nullptr, TRUE, mutex_name.c_str());
  if (!mutex || GetLastError() == ERROR_ALREADY_EXISTS) {
    std::cerr << "blurlink-net: another helper instance already owns this pipe.\n"
              << "  Close the other BlurLink window (or its GUI) and try again.\n";
    if (mutex) CloseHandle(mutex);
    return 3;
  }

  // Require 64-bit Windows 10+ (WinDivert x64 supported platform).
  if (!IsWindows10OrGreater() || sizeof(void*) != 8) {
    std::cerr << "blurlink-net: requires Windows 10/11 x64.\n";
    CloseHandle(mutex);
    return 4;
  }

  blurlink::Bridge bridge;
  blurlink::Sniffer sniffer;
  blurlink::HostSession host;
  blurlink::IpcCallbacks cb;
  cb.on_start = [&](const std::string& body) -> std::string {
    sniffer.Stop();  // mutual exclusion: one WinDivert session at a time
    host.Stop();     // ...and host mode diverts too, so it stops as well
    blurlink::blog::Section("START REQUESTED");
    blurlink::blog::Debug("cmd: start (raw body %u bytes)", (unsigned)body.size());
    blurlink::BridgeConfig cfg;
    std::string err;
    if (!ConfigFromStart(body, cfg, err)) {
      blurlink::blog::Warn("start rejected: %s", err.c_str());
      blurlink::blog::Info("hint: fix the values in BlurLink's Join tab, then Start again.");
      return "{\"type\":\"error\",\"message\":\"" + blurlink::minjson::Escape(err) + "\"}";
    }
    std::string session_err;
    if (!AcquireBridgeSession(session_err)) {
      blurlink::blog::Warn("start refused: %s", session_err.c_str());
      blurlink::blog::Info("hint: close the other BlurLink window (or stop its bridge), then Start again.");
      return "{\"type\":\"error\",\"message\":\"" +
             blurlink::minjson::Escape(session_err) + "\"}";
    }
    if (!bridge.Start(cfg)) {
      ReleaseBridgeSession();  // a failed start must not hold the session
      blurlink::blog::Error("start failed: %s", bridge.last_error().c_str());
      blurlink::blog::Info("hint: WinDivert needs admin rights (UAC) and its files next to the helper.");
      return "{\"type\":\"error\",\"message\":\"" +
             blurlink::minjson::Escape(bridge.last_error()) + "\"}";
    }
    return StatusJson(bridge, sniffer, host);
  };
  cb.on_stop = [&]() -> std::string {
    blurlink::blog::Section("STOP REQUESTED");
    StopBridgeAndReleaseSession(bridge);
    host.Stop();
    sniffer.Stop();  // stop halts any WinDivert session (bridge, host, or sniff)
    return StatusJson(bridge, sniffer, host);
  };
  cb.on_sniff = [&](const std::string& body) -> std::string {
    blurlink::blog::Section("LISTEN REQUESTED (SNIFF, observe-only)");
    blurlink::blog::Debug("cmd: sniff (raw body %u bytes)", (unsigned)body.size());
    blurlink::SniffConfig cfg;
    std::string err;
    if (!ConfigFromSniff(body, cfg, err)) {
      blurlink::blog::Warn("sniff rejected: %s", err.c_str());
      return "{\"type\":\"error\",\"message\":\"" + blurlink::minjson::Escape(err) + "\"}";
    }
    // Reply-listening coexists with the bridge (separate SNIFF handle);
    // discovery sniffing takes the session alone (and frees the exclusivity
    // lock, since nothing is being diverted any more).
    if (!cfg.inbound) {
      StopBridgeAndReleaseSession(bridge);
      host.Stop();
    }
    if (!sniffer.Start(cfg)) {
      blurlink::blog::Error("sniff failed: %s", sniffer.last_error().c_str());
      return "{\"type\":\"error\",\"message\":\"" +
             blurlink::minjson::Escape(sniffer.last_error()) + "\"}";
    }
    return StatusJson(bridge, sniffer, host);
  };
  cb.on_stop_sniff = [&]() -> std::string {
    blurlink::blog::Section("LISTEN STOP REQUESTED");
    sniffer.Stop();
    return StatusJson(bridge, sniffer, host);
  };
  cb.on_start_host = [&](const std::string& body) -> std::string {
    blurlink::blog::Section("HOST MODE REQUESTED");
    blurlink::blog::Debug("cmd: start_host (raw body %u bytes)", (unsigned)body.size());
    // Mutual exclusion: one WinDivert session at a time.
    sniffer.Stop();
    StopBridgeAndReleaseSession(bridge);
    blurlink::HostConfig cfg;
    std::string err;
    if (!ConfigFromHostStart(body, cfg, err)) {
      blurlink::blog::Warn("start_host rejected: %s", err.c_str());
      blurlink::blog::Info("hint: pick the overlay adapter and detect the discovery port first.");
      return "{\"type\":\"error\",\"message\":\"" + blurlink::minjson::Escape(err) + "\"}";
    }
    // Host mode diverts packets too, so it takes the same exclusive session.
    std::string session_err;
    if (!AcquireBridgeSession(session_err)) {
      blurlink::blog::Warn("start_host refused: %s", session_err.c_str());
      return "{\"type\":\"error\",\"message\":\"" +
             blurlink::minjson::Escape(session_err) + "\"}";
    }
    if (!host.Start(cfg)) {
      ReleaseBridgeSession();  // a failed start must not hold the session
      blurlink::blog::Error("start_host failed: %s", host.last_error().c_str());
      return "{\"type\":\"error\",\"message\":\"" +
             blurlink::minjson::Escape(host.last_error()) + "\"}";
    }
    return StatusJson(bridge, sniffer, host);
  };
  cb.on_revoke_host_player = [&](const std::string& body) -> std::string {
    std::string overlay;
    if (!blurlink::minjson::GetString(body, "overlayIp", overlay)) {
      return "{\"type\":\"error\",\"message\":\"missing overlayIp\"}";
    }
    std::array<std::uint8_t, 4> ip{};
    if (!blurlink::TryParseIpv4(overlay, ip)) {
      return "{\"type\":\"error\",\"message\":\"overlayIp must be a valid IPv4 "
             "address\"}";
    }
    if (!host.Revoke(ip)) {
      return "{\"type\":\"error\",\"message\":\"no such player\"}";
    }
    blurlink::blog::Info("host mode: revoked player %s", overlay.c_str());
    return StatusJson(bridge, sniffer, host);
  };
  cb.on_status = [&]() -> std::string {
    blurlink::blog::Debug("cmd: get_status");
    return StatusJson(bridge, sniffer, host);
  };
  cb.on_shutdown = [&]() {
    blurlink::blog::Section("SHUTDOWN REQUESTED (GUI closed)");
    StopBridgeAndReleaseSession(bridge);
    host.Stop();
    sniffer.Stop();
    g_shutdown.store(true);
  };
  cb.on_activity = [&]() {
    g_ever_active.store(true);
    g_last_activity_ms.store(NowMs());
  };

  blurlink::IpcServer server(pipe, token, std::move(cb));
  std::string error;
  if (!server.Start(error)) {
    std::cerr << "blurlink-net: IPC start failed: " << error << "\n";
    CloseHandle(mutex);
    return 5;
  }

  blurlink::blog::Section("BLURLINK HELPER");
  blurlink::blog::Info("blurlink-net 0.1.0 - Blur LAN discovery bridge helper");
  blurlink::blog::Info("role: copies YOUR Blur LAN broadcasts to the friend's overlay IP.");
  blurlink::blog::Info("mode: observe nothing, block nothing - one narrow outbound filter only.");
  blurlink::blog::Info("watchdog: exits %ds after the GUI stops talking (safety net).", g_watchdog_sec);
  blurlink::blog::Info("log level: %s%s", blurlink::blog::LevelName(blurlink::blog::Logger::Instance().level()),
                       g_verbose ? " (--verbose: per-packet details on)" : " (use --verbose for more)");
  blurlink::blog::Info("waiting for GUI commands (start/stop/sniff/status)...");
  blurlink::blog::Info("close this window any time - the bridge stops with it.");

  const std::int64_t start_ms = NowMs();
  while (!g_shutdown.load()) {
    Sleep(200);
    // Watchdog: no authenticated traffic => GUI is gone. Stop everything.
    std::int64_t idle_ms;
    if (g_ever_active.load()) {
      idle_ms = NowMs() - g_last_activity_ms.load();
      if (idle_ms > static_cast<std::int64_t>(g_watchdog_sec) * 1000) {
        blurlink::blog::Section("WATCHDOG FIRED (GUI SILENT)");
        blurlink::blog::Warn("no GUI command for %ds - the GUI probably closed or crashed.", g_watchdog_sec);
        blurlink::blog::Info("stopping the bridge now; nothing is intercepted any more.");
        break;
      }
    } else {
      // Grace before the first command so slow UAC/connect never kills us.
      idle_ms = NowMs() - start_ms;
      if (idle_ms > 120000) {
        blurlink::blog::Section("NO GUI EVER CONNECTED");
        blurlink::blog::Warn("no GUI command within 120s - exiting (nothing was ever bridged).");
        break;
      }
    }
  }
  server.Stop();
  StopBridgeAndReleaseSession(bridge);
  host.Stop();
  sniffer.Stop();
  blurlink::blog::Info("helper exiting cleanly (bridge stopped, driver closed).");
  blurlink::blog::Logger::Instance().close_log_file();
  CloseHandle(mutex);
  return 0;
}

#else

int main() { return 99; }

#endif  // _WIN32
