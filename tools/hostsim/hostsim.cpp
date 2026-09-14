// hostsim -- synthetic-peer packet injector for BlurLink's host-mode tests.
//
// WHY THIS EXISTS
// ---------------
// Host mode's two inputs are both INBOUND-only filter terms:
//     (inbound && udp && udp.DstPort == 47811)                       # introduction
//     (inbound && udp && udp.DstPort == <discovery> && ip.SrcAddr == <player>)
// and WinDivert "considers loopback packets to be outbound only, and will not
// capture loopback packets on the inbound path" (WinDivert 2.2 docs,
// WINDIVERT_ADDRESS). So no amount of sending UDP to ourselves can feed host
// mode, which is why the planned single-machine loopback harness cannot work
// (see TODO.md, "Task 14 amendment"). The only way to exercise host mode's real
// inbound path on one machine is to inject into the inbound path -- this tool.
//
// HOW
// ---
//   * WinDivertSend() with pAddr->Outbound = 0 injects into the INBOUND path.
//     The docs are explicit that "only the Outbound field, and not the IP
//     addresses in the injected packet, determines the packet's direction", so
//     a foreign source address can be faked -- which is the point: a packet
//     that looks like it arrived from a remote player.
//   * The handle opens with the filter "false", so it diverts NOTHING and cannot
//     disturb host mode's own traffic, at priority 1. That priority is required
//     rather than cosmetic: per the docs, injected packets are offered to "other
//     WinDivert handles with lower priorities", and host mode runs at 0.
//   * Inbound injection needs a valid pAddr->Network.IfIdx/SubIfIdx, resolved
//     with GetBestInterface() unless --ifindex is given.
//
// WHY IT IS NOT IN THE SHIPPED HELPER
// -----------------------------------
// This tool can synthesize arbitrary packets from arbitrary addresses. That is
// exactly what the elevated shipped helper must never be able to do, so it lives
// here and blurlink-net.exe gains no injection path or injection constants.
//
// It also CAPTURES: `hostsim capture` opens a sniffing handle (nothing is
// blocked or modified) and prints each matching packet's payload as hex, so a
// real packet can be captured and then replayed with --payload-hex. That is the
// only way to test with the real game on the other end: the game's own
// discovery packet, re-sent as though a second PC had broadcast it.
//
// It injects and reports; it asserts nothing. Every PASS/FAIL decision lives in
// scripts/test-e2e.ps1, which drives it.
//
// NOTES
// -----
// --ip-id is not cosmetic. An IPv4 ID of 0 is not a value: it is the stack
// saying "assign me one", and Windows does exactly that for a locally injected
// DF datagram (RFC 6864 calls such packets atomic). Injected packets therefore
// come out with consecutive per-interface IDs even when the buffer is byte-for-
// byte the same, which is why `--count 2` alone can never produce two packets
// that the bridge's identity check sees as identical. A non-zero --ip-id IS
// preserved verbatim (measured, not assumed: ids 1..5 sent were observed as
// 1..5), so testing the echo backstop needs an explicit one.
//
// Requires Administrator (WinDivertOpen) and WinDivert.dll beside this exe.

#include <winsock2.h>
#include <ws2tcpip.h>

#include <atomic>
#include <chrono>
#include <cstdint>
#include <iostream>
#include <map>
#include <sstream>
#include <string>
#include <thread>
#include <vector>

#include "blurlink/announce.h"
#include "blurlink/config.h"
#include "blurlink/packet.h"
#include "blurlink/windivert_api.h"

#include <iphlpapi.h>

#ifdef _MSC_VER
#pragma comment(lib, "iphlpapi.lib")
#pragma comment(lib, "ws2_32.lib")
#endif

namespace {

using Addr = std::array<std::uint8_t, 4>;

// Higher than host mode's 0, so injected packets are handed down to it.
constexpr std::int16_t kInjectPriority = 1;
// Matches nothing, so this handle is a pure sender: it diverts no traffic and
// cannot re-capture its own injections. "false" is a documented filter literal
// (WinDivert filter language: TRUE equal to 1, FALSE equal to 0).
constexpr const char* kNoDivertFilter = "false";

// Exit codes: the script distinguishes "you cannot run this here" from "the
// packet did not go out".
constexpr int kExitOk = 0;
constexpr int kExitUsage = 2;
constexpr int kExitDriver = 3;
constexpr int kExitSend = 4;

void PrintUsage() {
  std::cout <<
      "hostsim -- inject one synthetic packet for BlurLink's session tests\n"
      "\n"
      "usage: hostsim <kind> [options]\n"
      "\n"
      "kinds (what the other end of the wire looks like):\n"
      "  host mode -- the packet is aimed at the machine running host mode:\n"
      "  intro         INBOUND  udp -> 47811      carrying the 20-byte announcement\n"
      "  forward       INBOUND  udp -> discovery  a player's Blur discovery broadcast\n"
      "  reply         OUTBOUND udp discovery -> player:blurPort   the host's Blur answer\n"
      "  broadcast     OUTBOUND udp discovery -> <broadcast>:port  an answer to everyone\n"
      "\n"
      "  bridge (Join mode) -- the packet is what the local Blur broadcasts on LAN:\n"
      "  blur-broadcast OUTBOUND udp blurLan:blurPort -> <broadcast>:discoveryPort\n"
      "                the discovery broadcast the bridge exists to clone\n"
      "\n"
      "not an injection -- observe only, then replay what you saw:\n"
      "  capture       SNIFF a matching packet and print its payload as hex, so it\n"
      "                can be replayed verbatim with --payload-hex\n"
      "\n"
      "options:\n"
      "  --player-overlay A.B.C.D  intro: the player's overlay address (packet source)\n"
      "  --player-lan     A.B.C.D  the player's physical LAN address\n"
      "  --blur-lan       A.B.C.D  blur-broadcast: the local Blur's own LAN address\n"
      "  --blur-port      N        the Blur source port (the discovery socket)\n"
      "  --host-ip        A.B.C.D  intro/forward: destination (this machine's address)\n"
      "                            reply/broadcast: source (this machine's address)\n"
      "  --discovery-port N        forward/reply/broadcast/blur-broadcast\n"
      "  --broadcast-ip   A.B.C.D  broadcast/blur-broadcast: default = /24 of the LAN\n"
      "                            address with its last octet 255\n"
      "  --payload        TEXT     blur-broadcast/forward: the UDP payload, ASCII\n"
      "                            (default BLSIMFWD -- a STAND-IN only; it is\n"
      "                            NOT what Blur sends. The real discovery payload\n"
      "                            was captured 2026-09-12 and is not ASCII text\n"
      "                            -- see docs/packet-research.md. Use\n"
      "                            --payload-hex to replay the real bytes)\n"
      "  --payload-hex    HEX      the exact payload bytes, e.g. \"1f 8b 08...\" or\n"
      "                            \"1f8b08...\". Overrides --payload. This is how a\n"
      "                            real captured packet is replayed verbatim\n"
      "  capture options:\n"
      "  --port           N        capture: watch udp.DstPort == N. NOTE the\n"
      "                            direction: for INBOUND traffic this is the\n"
      "                            CLIENT's port, not the service's -- a dns\n"
      "                            reply arrives at the ephemeral port, not at\n"
      "                            53, so --in --port 53 matches nothing\n"
      "  --dst            A.B.C.D  capture: watch ip.DstAddr == A.B.C.D\n"
      "  --src            A.B.C.D  capture: watch ip.SrcAddr == A.B.C.D\n"
      "  --in                      capture: watch inbound instead of outbound\n"
      "  --seconds        N        capture: the time LIMIT (default 10, max 300)\n"
      "  --stop-after     N        capture: stop once N matching packets have been\n"
      "                            seen; --stop-after 1 means \"stop at the first\n"
      "                            packet\", so a live capture needs no manual\n"
      "                            timing. --seconds stays as the safety ceiling\n"
      "  --timestamps               capture: prefix each packet with its time since\n"
      "                            the capture started (+12.345s). Without this a\n"
      "                            capture cannot tell a PERIODIC sender from a\n"
      "                            ONE-SHOT one, which is a mistake this project\n"
      "                            already made once: a 20s window saw no broadcast\n"
      "                            and \"hosting is silent\" was recorded, when the\n"
      "                            truth was that a slow broadcast simply fell\n"
      "                            outside the window\n"
      "  --ip-id          N        the IPv4 ID to send (0 means \"assign one\", see\n"
      "                            NOTES). --vary-id bumps it per repetition\n"
      "  --vary-id                 bump the IPv4 ID per repetition, so repeats are\n"
      "                            NOT identical: dedup keys on identity, and the\n"
      "                            bridge suppresses identical echoes by design\n"
      "  --ifindex        N        inbound only; default resolved via GetBestInterface\n"
      "  --count          N        repeat the injection (default 1, max 50)\n"
      "  --quiet                   print nothing on success\n";
}

// A STAND-IN payload for the injector, NOT what Blur sends. The real discovery
// payload was captured on 2026-09-12 (24 bytes, no ASCII text) and is recorded
// in docs/packet-research.md; replay it with --payload-hex. The bridge's
// payload-prefix gate (config.payloadPrefixHex) is a code-side check, so a test
// that wants to reach it only has to send a payload that starts with the
// configured prefix.
const std::vector<std::uint8_t> kDefaultDiscoveryPayload = {'B', 'L', 'S', 'I', 'M',
                                                            'F', 'W', 'D'};

std::string ToHex(const std::uint8_t* p, std::size_t n) {
  static const char* digits = "0123456789abcdef";
  std::string s;
  s.reserve(n * 2);
  for (std::size_t i = 0; i < n; ++i) {
    s.push_back(digits[p[i] >> 4]);
    s.push_back(digits[p[i] & 0x0F]);
  }
  return s;
}

// Accepts "1f8b", "1f 8b", "1f:8b", "0x1f8b" -- the shapes a person actually
// pastes out of a capture. Anything non-hex is rejected with the offending
// pair, because silently skipping a bad digit would replay the wrong bytes and
// the whole point is to replay the RIGHT ones.
bool ParseHexBytes(const std::string& in, std::vector<std::uint8_t>& out, std::string& reason) {
  std::string clean;
  for (char c : in) {
    if (c == ' ' || c == ':' || c == '-' || c == ',' || c == '\t') continue;
    clean.push_back(c);
  }
  if (clean.rfind("0x", 0) == 0 || clean.rfind("0X", 0) == 0) clean = clean.substr(2);
  if (clean.empty()) {
    reason = "empty";
    return false;
  }
  if (clean.size() % 2 != 0) {
    reason = "an even number of hex digits is required";
    return false;
  }
  auto nibble = [](char c) -> int {
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    return -1;
  };
  out.clear();
  for (std::size_t i = 0; i < clean.size(); i += 2) {
    const int hi = nibble(clean[i]);
    const int lo = nibble(clean[i + 1]);
    if (hi < 0 || lo < 0) {
      reason = "not hex: '" + clean.substr(i, 2) + "'";
      return false;
    }
    out.push_back(static_cast<std::uint8_t>((hi << 4) | lo));
  }
  if (out.size() > 512) {
    reason = "payload is longer than 512 bytes";
    return false;
  }
  return true;
}

// Returns false and fills `reason` when the value is missing or unusable.
bool RequireAddr(const std::map<std::string, std::string>& args, const std::string& key,
                 const std::string& label, Addr& out, std::string& reason) {
  const auto it = args.find(key);
  if (it == args.end() || it->second.empty()) {
    reason = "missing " + label + " (--" + key + ")";
    return false;
  }
  if (!blurlink::TryParseIpv4(it->second, out)) {
    reason = label + " must be a canonical IPv4 address, got '" + it->second + "'";
    return false;
  }
  return true;
}

bool RequirePort(const std::map<std::string, std::string>& args, const std::string& key,
                 const std::string& label, int& out, std::string& reason) {
  const auto it = args.find(key);
  if (it == args.end() || it->second.empty()) {
    reason = "missing " + label + " (--" + key + ")";
    return false;
  }
  try {
    const long long v = std::stoll(it->second);
    if (v < 1 || v > 65535) {
      reason = label + " must be 1-65535, got " + it->second;
      return false;
    }
    out = static_cast<int>(v);
  } catch (...) {
    reason = label + " is not a number: '" + it->second + "'";
    return false;
  }
  return true;
}

std::vector<std::uint8_t> BuildUdpIPv4(const Addr& src, const Addr& dst, std::uint16_t sport,
                                       std::uint16_t dport, std::uint16_t ip_id,
                                       const std::vector<std::uint8_t>& payload) {
  const std::size_t udp_len = 8 + payload.size();
  const std::size_t total = 20 + udp_len;
  std::vector<std::uint8_t> p(total, 0);

  p[0] = 0x45;  // IPv4, IHL 5
  p[1] = 0x00;  // DSCP/ECN
  p[2] = static_cast<std::uint8_t>(total >> 8);
  p[3] = static_cast<std::uint8_t>(total & 0xFF);
  p[4] = static_cast<std::uint8_t>(ip_id >> 8);
  p[5] = static_cast<std::uint8_t>(ip_id & 0xFF);
  p[6] = 0x40;  // DF: never fragment a synthetic test packet
  p[8] = 64;    // TTL (WinDivert decrements impostor packets by one)
  p[9] = 17;    // UDP
  for (int i = 0; i < 4; ++i) {
    p[12 + i] = src[static_cast<std::size_t>(i)];
    p[16 + i] = dst[static_cast<std::size_t>(i)];
  }

  const std::size_t u = 20;
  p[u + 0] = static_cast<std::uint8_t>(sport >> 8);
  p[u + 1] = static_cast<std::uint8_t>(sport & 0xFF);
  p[u + 2] = static_cast<std::uint8_t>(dport >> 8);
  p[u + 3] = static_cast<std::uint8_t>(dport & 0xFF);
  p[u + 4] = static_cast<std::uint8_t>(udp_len >> 8);
  p[u + 5] = static_cast<std::uint8_t>(udp_len & 0xFF);
  for (std::size_t i = 0; i < payload.size(); ++i) p[u + 8 + i] = payload[i];
  return p;
}

// A valid interface number is required when injecting inbound. Uses the IP
// Helper API rather than a guess, exactly as the docs direct.
bool ResolveIfIndex(const Addr& dst, const std::string& dst_text, std::uint32_t& out,
                    std::string& reason) {
  (void)dst;
  const IPAddr dest = inet_addr(dst_text.c_str());
  if (dest == INADDR_NONE) {
    reason = "GetBestInterface: could not parse '" + dst_text + "'";
    return false;
  }
  DWORD index = 0;
  const DWORD rc = GetBestInterface(dest, &index);
  if (rc != NO_ERROR) {
    reason = "GetBestInterface failed for " + dst_text + " (code " + std::to_string(rc) + ")";
    return false;
  }
  if (index == 0) {
    reason = "GetBestInterface returned interface 0 for " + dst_text;
    return false;
  }
  out = static_cast<std::uint32_t>(index);
  return true;
}

// Observe-only capture. Prints each matching packet's payload as hex so that a
// real packet can be replayed verbatim with --payload-hex; nothing is blocked,
// modified or reinjected (WINDIVERT_FLAG_SNIFF).
//
// The deadline is enforced with WinDivertShutdown from a helper thread, exactly
// as the helper's own sniffer does, because WinDivertRecv has no timeout: with
// no traffic to watch, a plain recv would block for ever and a test that hangs
// is worse than a test that fails.
//
// THIS TOOL MAY HANDLE RAW PAYLOADS, and that is precisely why it is not the
// helper: the shipped helper has no code path that logs or reports a payload,
// and it must never grow one.
// Elapsed time since t0, formatted as "+12.345s". Hand-rolled rather than via
// <iomanip>: this file does not include it, and one label does not justify a
// header whose manipulators are easy to leave sticky.
std::string ElapsedLabel(std::chrono::steady_clock::time_point t0) {
  const auto ms = std::chrono::duration_cast<std::chrono::milliseconds>(
                      std::chrono::steady_clock::now() - t0)
                      .count();
  std::string frac = std::to_string(ms % 1000);
  while (frac.size() < 3) frac.insert(frac.begin(), '0');
  return "+" + std::to_string(ms / 1000) + "." + frac + "s";
}

int RunCapture(const std::map<std::string, std::string>& args) {
  const bool inbound = args.count("in") > 0;
  const bool timestamps = args.count("timestamps") > 0;
  int port = 0;
  int seconds = 10;
  int stop_after = 0;  // 0 = run the whole window
  Addr dst{};
  Addr src{};
  std::string problem;

  if (const auto it = args.find("stop-after"); it != args.end()) {
    try {
      stop_after = std::stoi(it->second);
    } catch (...) {
      std::cerr << "hostsim: --stop-after is not a number\n";
      return kExitUsage;
    }
    if (stop_after < 1 || stop_after > 100) {
      std::cerr << "hostsim: --stop-after must be 1-100\n";
      return kExitUsage;
    }
  }

  if (const auto it = args.find("seconds"); it != args.end()) {
    try {
      seconds = std::stoi(it->second);
    } catch (...) {
      std::cerr << "hostsim: --seconds is not a number\n";
      return kExitUsage;
    }
    if (seconds < 1 || seconds > 300) {
      std::cerr << "hostsim: --seconds must be 1-300\n";
      return kExitUsage;
    }
  }
  if (args.count("port") && !RequirePort(args, "port", "the port to watch", port, problem)) {
    std::cerr << "hostsim: " << problem << "\n";
    return kExitUsage;
  }
  const bool have_dst = args.count("dst") > 0;
  if (have_dst && !RequireAddr(args, "dst", "the destination to watch", dst, problem)) {
    std::cerr << "hostsim: " << problem << "\n";
    return kExitUsage;
  }
  const bool have_src = args.count("src") > 0;
  if (have_src && !RequireAddr(args, "src", "the source to watch", src, problem)) {
    std::cerr << "hostsim: " << problem << "\n";
    return kExitUsage;
  }

  // Only canonical values are interpolated, same rule as the helper's own
  // filter builders -- a test tool is not a reason to concatenate user text
  // into a filter string.
  std::string filter = inbound ? "inbound && ip && udp" : "outbound && ip && udp";
  if (port > 0) filter += " && udp.DstPort == " + std::to_string(port);
  if (have_dst) filter += " && ip.DstAddr == " + blurlink::Ipv4ToString(dst);
  if (have_src) filter += " && ip.SrcAddr == " + blurlink::Ipv4ToString(src);

  blurlink::WinDivertApi api;
  std::string load_err;
  if (!api.Load(load_err)) {
    std::cerr << "hostsim: " << load_err << "\n";
    return kExitDriver;
  }
  HANDLE h = api.open_(filter.c_str(), blurlink::kDivertLayerNetwork, kInjectPriority,
                       blurlink::kDivertFlagSniff);
  if (!h || h == INVALID_HANDLE_VALUE) {
    const DWORD code = GetLastError();
    std::cerr << "hostsim: WinDivertOpen failed (code " << code << ")";
    if (code == 5) {
      std::cerr << ": access denied -- run this from an elevated shell";
    } else if (code == 2) {
      std::cerr << ": driver not found -- place WinDivert64.sys next to WinDivert.dll";
    }
    std::cerr << "\n";
    return kExitDriver;
  }
  api.set_param_(h, blurlink::kDivertParamQueueLength, 1024);
  api.set_param_(h, blurlink::kDivertParamQueueTime, 1000);

  std::cout << "capture: " << filter;
  if (stop_after > 0) {
    std::cout << ", stopping after " << stop_after << " packet(s)";
  }
  std::cout << " (limit " << seconds << "s; SNIFF: nothing blocked, modified or reinjected)\n";

  // The deadline thread doubles as the early-stop mechanism: it polls a flag
  // rather than sleeping the whole window, so an early stop returns in ~100ms
  // instead of blocking the process until the ceiling. Same 100ms poll as the
  // helper's own sniffer, and for the same reason -- WinDivertRecv has no
  // timeout, so something has to unblock it.
  std::atomic<bool> done{false};
  std::thread stopper([&api, h, seconds, &done] {
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(seconds);
    while (!done.load() && std::chrono::steady_clock::now() < deadline) {
      std::this_thread::sleep_for(std::chrono::milliseconds(100));
    }
    if (!done.load()) api.shutdown_(h, blurlink::kDivertShutdownRecv);
  });

  std::vector<std::uint8_t> buf(65535);
  int count = 0;
  bool hit_target = false;
  const auto t0 = std::chrono::steady_clock::now();
  for (;;) {
    blurlink::DivertAddress a{};
    UINT len = 0;
    if (!api.recv_(h, buf.data(), static_cast<UINT>(buf.size()), &len, &a)) break;
    blurlink::UdpPacketView v;
    std::string why;
    if (!blurlink::TryParseUdpOverIpv4(buf.data(), len, v, why)) {
      std::cout << "  (unparsed: " << why << ") len=" << len << "\n";
      continue;
    }
    ++count;
    std::cout << "  ";
    if (timestamps) std::cout << ElapsedLabel(t0) << " ";
    std::cout << (a.Outbound ? "out" : "in ") << " "
              << blurlink::Ipv4ToString(v.src_ip) << ":" << v.src_port << " -> "
              << blurlink::Ipv4ToString(v.dst_ip) << ":" << v.dst_port << " len=" << len
              << " id=" << v.ip_id
              << " payload=" << ToHex(buf.data() + v.payload_offset, v.payload_len) << "\n";
    // Only PARSEABLE packets count towards the target: the point of stopping is
    // to get a payload we can replay, and a truncated or non-UDP packet is not
    // one. Those were already reported above, so a stray one is not hidden.
    if (stop_after > 0 && count >= stop_after) {
      hit_target = true;
      break;
    }
  }

  // Order matters: signal the stopper, join it, and only then close the handle.
  // Closing first would let the stopper call WinDivertShutdown on a dead handle.
  done.store(true);
  stopper.join();
  api.close_(h);
  std::cout << "capture: " << count << " packet(s)";
  if (hit_target) std::cout << " (stopped at the requested count)";
  if (timestamps) std::cout << " over " << ElapsedLabel(t0);
  std::cout << "\n";
  if (count == 0) {
    std::cout << "  (nothing matched -- check the port, the direction, and that the game is\n"
                 "   actually broadcasting right now)\n";
  }
  return kExitOk;
}

}  // namespace

int main(int argc, char** argv) {
  std::map<std::string, std::string> args;
  std::string kind;

  for (int i = 1; i < argc; ++i) {
    const std::string a = argv[i];
    if (a == "--help" || a == "-h" || a == "/?") {
      PrintUsage();
      return kExitOk;
    }
    if (a.rfind("--", 0) == 0) {
      const std::string key = a.substr(2);
      // Flags take no value; everything else is --key value. The list is
      // explicit on purpose, but a flag MISSING from it does not fail here:
      // the parser silently swallows the next argument as its value, and the
      // failure only surfaces one argument later as "unexpected argument 'X'".
      // That is exactly how --timestamps failed first (it ate --seconds, then
      // choked on the bare 120), and how --vary-id failed before it when it
      // happened to be last. So: add a flag here and nowhere else.
      static const char* const kValueLessFlags[] = {"quiet", "vary-id", "in", "timestamps"};
      bool valueless = false;
      for (const char* f : kValueLessFlags) {
        if (key == f) {
          valueless = true;
          break;
        }
      }
      if (valueless) {
        args[key] = "1";
        continue;
      }
      if (i + 1 >= argc) {
        std::cerr << "hostsim: --" << key << " needs a value\n\n";
        PrintUsage();
        return kExitUsage;
      }
      args[key] = argv[++i];
      continue;
    }
    if (kind.empty()) {
      kind = a;
      continue;
    }
    std::cerr << "hostsim: unexpected argument '" << a << "'\n\n";
    PrintUsage();
    return kExitUsage;
  }

  if (kind != "intro" && kind != "forward" && kind != "reply" && kind != "broadcast" &&
      kind != "blur-broadcast" && kind != "capture") {
    std::cerr << "hostsim: unknown or missing kind\n\n";
    PrintUsage();
    return kExitUsage;
  }

  // Capture injects nothing, so it takes none of the injection arguments and
  // reports through its own path.
  if (kind == "capture") return RunCapture(args);

  const bool quiet = args.count("quiet") > 0;
  const bool vary_id = args.count("vary-id") > 0;
  std::string problem;

  Addr player_overlay{};
  Addr player_lan{};
  Addr blur_lan{};
  Addr host{};
  Addr destination{};  // what --broadcast-ip resolved to, when used
  int blur_port = 0;
  int discovery_port = 0;
  int ifindex = 0;
  int count = 1;
  int ip_id = 0;
  std::string payload_text;
  if (const auto it = args.find("payload"); it != args.end()) payload_text = it->second;

  if (const auto it = args.find("count"); it != args.end()) {
    try {
      count = std::stoi(it->second);
    } catch (...) {
      std::cerr << "hostsim: --count is not a number\n";
      return kExitUsage;
    }
    if (count < 1 || count > 50) {
      std::cerr << "hostsim: --count must be 1-50\n";
      return kExitUsage;
    }
  }
  if (const auto it = args.find("ip-id"); it != args.end()) {
    try {
      const long long v = std::stoll(it->second);
      if (v < 0 || v > 65535) {
        std::cerr << "hostsim: --ip-id must be 0-65535\n";
        return kExitUsage;
      }
      ip_id = static_cast<int>(v);
    } catch (...) {
      std::cerr << "hostsim: --ip-id is not a number\n";
      return kExitUsage;
    }
  }
  if (const auto it = args.find("ifindex"); it != args.end()) {
    try {
      ifindex = std::stoi(it->second);
    } catch (...) {
      std::cerr << "hostsim: --ifindex is not a number\n";
      return kExitUsage;
    }
  }

  // Per-kind requirements. Kept explicit rather than "best effort": a missing
  // field here would silently produce a packet that tests the wrong thing.
  if (kind == "intro") {
    if (!RequireAddr(args, "player-overlay", "the player's overlay address", player_overlay,
                     problem) ||
        !RequireAddr(args, "player-lan", "the player's LAN address", player_lan, problem) ||
        !RequireAddr(args, "host-ip", "the host's own address", host, problem) ||
        !RequirePort(args, "blur-port", "the player's Blur source port", blur_port, problem)) {
      std::cerr << "hostsim: " << problem << "\n";
      return kExitUsage;
    }
  } else if (kind == "forward") {
    if (!RequireAddr(args, "player-lan", "the player's LAN address", player_lan, problem) ||
        !RequireAddr(args, "host-ip", "the host's own address", host, problem) ||
        !RequirePort(args, "blur-port", "the player's Blur source port", blur_port, problem) ||
        !RequirePort(args, "discovery-port", "the discovery port", discovery_port, problem)) {
      std::cerr << "hostsim: " << problem << "\n";
      return kExitUsage;
    }
  } else if (kind == "reply") {
    if (!RequireAddr(args, "player-lan", "the player's LAN address", player_lan, problem) ||
        !RequireAddr(args, "host-ip", "the host's own address", host, problem) ||
        !RequirePort(args, "blur-port", "the player's Blur destination port", blur_port,
                     problem) ||
        !RequirePort(args, "discovery-port", "the discovery port", discovery_port, problem)) {
      std::cerr << "hostsim: " << problem << "\n";
      return kExitUsage;
    }
  } else if (kind == "broadcast") {
    if (!RequireAddr(args, "player-lan", "the player's LAN address", player_lan, problem) ||
        !RequireAddr(args, "host-ip", "the host's own address", host, problem) ||
        !RequirePort(args, "blur-port", "the destination port", blur_port, problem) ||
        !RequirePort(args, "discovery-port", "the discovery port", discovery_port, problem)) {
      std::cerr << "hostsim: " << problem << "\n";
      return kExitUsage;
    }
  } else {  // blur-broadcast: OUR Blur, so our LAN address and no host address
    if (!RequireAddr(args, "blur-lan", "the local Blur's LAN address", blur_lan, problem) ||
        !RequirePort(args, "blur-port", "the local Blur's source port", blur_port, problem) ||
        !RequirePort(args, "discovery-port", "the discovery port", discovery_port, problem)) {
      std::cerr << "hostsim: " << problem << "\n";
      return kExitUsage;
    }
  }

  // Blur's own discovery payload. The bridge's optional payload-prefix gate is
  // checked in code, not in the filter, so this is the only thing that reaches
  // it -- and --payload / --payload-hex are the levers that test pulls.
  //
  // --payload-hex exists so a REAL captured packet can be replayed byte for
  // byte, which is the only way to ask the real game what it does with it: a
  // plausible-looking stand-in is not the same question.
  std::vector<std::uint8_t> discovery_payload = kDefaultDiscoveryPayload;
  if (const auto it = args.find("payload-hex"); it != args.end() && !it->second.empty()) {
    if (!ParseHexBytes(it->second, discovery_payload, problem)) {
      std::cerr << "hostsim: --payload-hex: " << problem << "\n";
      return kExitUsage;
    }
  } else if (!payload_text.empty()) {
    discovery_payload.assign(payload_text.begin(), payload_text.end());
    if (discovery_payload.size() > 512) {
      std::cerr << "hostsim: --payload must be 1-512 bytes\n";
      return kExitUsage;
    }
  }

  // Assemble the packet host mode should see: addresses, ports, direction.
  Addr src{};
  Addr dst{};
  std::uint16_t sport = 0;
  std::uint16_t dport = 0;
  bool outbound = false;
  std::vector<std::uint8_t> payload;
  std::string shape;

  if (kind == "intro") {
    blurlink::AnnouncePacket a{};
    a.overlay = player_overlay;
    a.lan = player_lan;
    a.blur_src_port = static_cast<std::uint16_t>(blur_port);
    payload = blurlink::EncodeAnnounce(a);
    src = player_overlay;  // the host takes the player's overlay address from here
    dst = host;
    sport = static_cast<std::uint16_t>(blur_port);  // as the real bridge sends it
    dport = blurlink::kHostAnnounceUdpPort;
    outbound = false;
    shape = "introduction " + blurlink::Ipv4ToString(player_overlay) + " -> host:47811" +
            " announcing overlay " + blurlink::Ipv4ToString(player_overlay) + " / lan " +
            blurlink::Ipv4ToString(player_lan) + ":" + std::to_string(blur_port);
  } else if (kind == "forward") {
    payload = discovery_payload;
    src = player_lan;
    dst = host;
    sport = static_cast<std::uint16_t>(blur_port);
    dport = static_cast<std::uint16_t>(discovery_port);
    outbound = false;
    shape = "forward " + blurlink::Ipv4ToString(player_lan) + ":" + std::to_string(blur_port) +
            " -> host:" + std::to_string(discovery_port);
  } else if (kind == "blur-broadcast") {
    // This machine's own Blur announcing itself on the LAN: OUTBOUND, sourced
    // from our LAN address, sent to the broadcast address on the discovery
    // port. The bridge's filter matches exactly this shape.
    if (const auto it = args.find("broadcast-ip"); it != args.end() && !it->second.empty()) {
      if (!blurlink::TryParseIpv4(it->second, destination)) {
        std::cerr << "hostsim: --broadcast-ip must be a canonical IPv4 address\n";
        return kExitUsage;
      }
    } else {
      destination = blur_lan;
      destination[3] = 255;  // /24 broadcast of the sender's own subnet
    }
    payload = discovery_payload;
    src = blur_lan;
    dst = destination;
    sport = static_cast<std::uint16_t>(blur_port);
    dport = static_cast<std::uint16_t>(discovery_port);
    outbound = true;
    shape = "blur-broadcast " + blurlink::Ipv4ToString(blur_lan) + ":" + std::to_string(blur_port) +
            " -> " + blurlink::Ipv4ToString(destination) + ":" + std::to_string(discovery_port) +
            " payload='" + std::string(discovery_payload.begin(), discovery_payload.end()) + "'";
  } else if (kind == "reply") {
    payload = {'B', 'L', 'S', 'I', 'M', 'R', 'P', 'Y'};
    src = host;
    dst = player_lan;
    sport = static_cast<std::uint16_t>(discovery_port);
    dport = static_cast<std::uint16_t>(blur_port);
    outbound = true;
    shape = "reply host:" + std::to_string(discovery_port) + " -> " +
            blurlink::Ipv4ToString(player_lan) + ":" + std::to_string(blur_port);
  } else {
    if (const auto it = args.find("broadcast-ip"); it != args.end() && !it->second.empty()) {
      if (!blurlink::TryParseIpv4(it->second, destination)) {
        std::cerr << "hostsim: --broadcast-ip must be a canonical IPv4 address\n";
        return kExitUsage;
      }
    } else {
      destination = player_lan;
      destination[3] = 255;  // subnet-directed broadcast of the player's own subnet
    }
    payload = {'B', 'L', 'S', 'I', 'M', 'B', 'C', 'T'};
    src = host;
    dst = destination;
    sport = static_cast<std::uint16_t>(discovery_port);
    dport = static_cast<std::uint16_t>(blur_port);
    outbound = true;
    shape = "broadcast host:" + std::to_string(discovery_port) + " -> " +
            blurlink::Ipv4ToString(destination) + ":" + std::to_string(blur_port);
  }

  const auto packet =
      BuildUdpIPv4(src, dst, sport, dport, static_cast<std::uint16_t>(ip_id), payload);

  blurlink::WinDivertApi api;
  std::string load_err;
  if (!api.Load(load_err)) {
    std::cerr << "hostsim: " << load_err << "\n";
    return kExitDriver;
  }

  HANDLE h = api.open_(kNoDivertFilter, blurlink::kDivertLayerNetwork, kInjectPriority, 0);
  if (!h || h == INVALID_HANDLE_VALUE) {
    const DWORD code = GetLastError();
    std::cerr << "hostsim: WinDivertOpen failed (code " << code << ")";
    if (code == 5) {
      std::cerr << ": access denied -- run this from an elevated shell";
    } else if (code == 2) {
      std::cerr << ": driver not found -- place WinDivert64.sys next to WinDivert.dll";
    }
    std::cerr << "\n";
    return kExitDriver;
  }

  blurlink::DivertAddress addr{};
  addr.Layer = blurlink::kDivertLayerNetwork;
  addr.Event = 0;  // WINDIVERT_EVENT_NETWORK_PACKET
  addr.Outbound = outbound ? 1 : 0;
  addr.Impostor = 1;  // it is one; also gets WinDivert's loop backstop
  addr.IPChecksum = 0;
  addr.TCPChecksum = 0;
  addr.UDPChecksum = 0;

  if (!outbound) {
    std::uint32_t resolved = 0;
    if (ifindex > 0) {
      resolved = static_cast<std::uint32_t>(ifindex);
    } else if (!ResolveIfIndex(dst, blurlink::Ipv4ToString(dst), resolved, problem)) {
      std::cerr << "hostsim: " << problem << " (or pass --ifindex)\n";
      api.close_(h);
      return kExitUsage;
    }
    addr.Network.IfIdx = resolved;
    addr.Network.SubIfIdx = 0;
  }

  // Checksums must be valid for an injected packet, or the corresponding flag
  // must be clear. Compute them the same way the helper's clone path does.
  std::vector<std::uint8_t> bytes = packet;
  if (!api.calc_checksums_(bytes.data(), static_cast<UINT>(bytes.size()), &addr, 0)) {
    std::cerr << "hostsim: WinDivertHelperCalcChecksums failed\n";
    api.close_(h);
    return kExitSend;
  }

  int sent = 0;
  for (int i = 0; i < count; ++i) {
    // --vary-id gives each repetition a distinct IPv4 identity, so a rate-limit
    // test reaches the limiter instead of the dedup backstop (which is RIGHT to
    // suppress identical repeats). Conversely, omitting it while setting an
    // explicit --ip-id is the only way to produce a real echo -- see NOTES.
    if (vary_id && i > 0) {
      const std::uint16_t id = static_cast<std::uint16_t>(ip_id + i);
      bytes[4] = static_cast<std::uint8_t>(id >> 8);
      bytes[5] = static_cast<std::uint8_t>(id & 0xFF);
      if (!api.calc_checksums_(bytes.data(), static_cast<UINT>(bytes.size()), &addr, 0)) {
        std::cerr << "hostsim: WinDivertHelperCalcChecksums failed\n";
        api.close_(h);
        return kExitSend;
      }
    }
    UINT written = static_cast<UINT>(bytes.size());
    if (!api.send_(h, bytes.data(), static_cast<UINT>(bytes.size()), &written, &addr)) {
      std::cerr << "hostsim: WinDivertSend failed (code " << GetLastError() << ")\n";
      api.close_(h);
      return kExitSend;
    }
    ++sent;
  }

  api.close_(h);
  if (!quiet) {
    std::cout << "injected " << sent << "x " << (outbound ? "outbound" : "inbound") << " " << shape
              << " (ifIdx " << addr.Network.IfIdx << ")\n";
  }
  return kExitOk;
}
