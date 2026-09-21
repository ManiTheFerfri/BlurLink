// Portable unit tests for the helper's platform-independent logic:
// config validation, filter builder, checksums, packet transform,
// rate limiter, dedup cache, minimal JSON. No WinDivert needed.
// Build: cmake -S . -B build && cmake --build build && ./blurlink-net-tests

#include <array>
#include <atomic>
#include <cassert>
#include <chrono>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>
#include <thread>
#include <vector>

#include "blurlink/announce.h"
#include "blurlink/checksum.h"
#include "blurlink/config.h"
#include "blurlink/console_log.h"
#include "blurlink/dedup.h"
#include "blurlink/host_classify.h"
#include "blurlink/host_engine.h"
#include "blurlink/host_roster.h"
#include "blurlink/json_min.h"
#include "blurlink/reply_shape.h"
#include "blurlink/windivert_abi.h"
#include "blurlink/packet.h"
#include "blurlink/rate_limiter.h"

static int g_failures = 0;
static int g_checks = 0;

#define CHECK(cond)                                                     \
  do {                                                                  \
    ++g_checks;                                                         \
    if (!(cond)) {                                                      \
      ++g_failures;                                                     \
      std::printf("FAIL %s:%d: %s\n", __FILE__, __LINE__, #cond);        \
    }                                                                   \
  } while (0)

namespace {

std::vector<std::uint8_t> BuildPacket(const std::array<std::uint8_t, 4>& src,
                                      const std::array<std::uint8_t, 4>& dst,
                                      std::uint16_t sport, std::uint16_t dport,
                                      const std::vector<std::uint8_t>& payload,
                                      std::uint16_t ip_id = 0x1234) {
  std::size_t udp_len = 8 + payload.size();
  std::size_t total = 20 + udp_len;
  std::vector<std::uint8_t> p(total, 0);
  p[0] = 0x45;
  p[2] = static_cast<std::uint8_t>(total >> 8);
  p[3] = static_cast<std::uint8_t>(total & 0xFF);
  p[4] = static_cast<std::uint8_t>(ip_id >> 8);
  p[5] = static_cast<std::uint8_t>(ip_id & 0xFF);
  p[8] = 64;
  p[9] = 17;
  for (int i = 0; i < 4; ++i) {
    p[12 + i] = src[i];
    p[16 + i] = dst[i];
  }
  p[20] = static_cast<std::uint8_t>(sport >> 8);
  p[21] = static_cast<std::uint8_t>(sport & 0xFF);
  p[22] = static_cast<std::uint8_t>(dport >> 8);
  p[23] = static_cast<std::uint8_t>(dport & 0xFF);
  p[24] = static_cast<std::uint8_t>(udp_len >> 8);
  p[25] = static_cast<std::uint8_t>(udp_len & 0xFF);
  for (std::size_t i = 0; i < payload.size(); ++i) p[28 + i] = payload[i];
  return p;
}

void TestIpv4() {
  std::array<std::uint8_t, 4> ip{};
  CHECK(blurlink::TryParseIpv4("100.96.47.177", ip));
  CHECK(ip[0] == 100 && ip[3] == 177);
  CHECK(blurlink::TryParseIpv4("255.255.255.255", ip));
  CHECK(!blurlink::TryParseIpv4("::1", ip));
  CHECK(!blurlink::TryParseIpv4("01.2.3.4", ip));
  CHECK(!blurlink::TryParseIpv4("1.2.3", ip));
  CHECK(!blurlink::TryParseIpv4("256.1.1.1", ip));
  CHECK(!blurlink::TryParseIpv4("", ip));
  CHECK(!blurlink::TryParseIpv4("1.2.3.4/24", ip));
}

void TestHex() {
  std::vector<std::uint8_t> out;
  CHECK(blurlink::ParseHexSignature("", out).ok && out.empty());
  CHECK(blurlink::ParseHexSignature("42 4C 55 52", out).ok && out.size() == 4 &&
        out[0] == 0x42 && out[3] == 0x52);
  CHECK(blurlink::ParseHexSignature("424C5552", out).ok && out.size() == 4);
  CHECK(!blurlink::ParseHexSignature("ZZ", out).ok);
  std::string big;
  for (int i = 0; i < 65; ++i) big += "AA ";
  CHECK(!blurlink::ParseHexSignature(big, out).ok);
}

void TestBroadcast() {
  using blurlink::ValidateBroadcastDestination;
  CHECK(ValidateBroadcastDestination("255.255.255.255").ok);
  CHECK(ValidateBroadcastDestination("192.168.1.255").ok);
  CHECK(ValidateBroadcastDestination("239.0.0.1").ok);
  CHECK(!ValidateBroadcastDestination("100.96.47.177").ok);  // unicast host IP
  CHECK(!ValidateBroadcastDestination("192.168.1.100").ok);
  CHECK(ValidateBroadcastDestination("10.0.5.191", "10.0.5.191").ok);
}

void TestPort() {
  using blurlink::ValidateDiscoveryPort;
  CHECK(!ValidateDiscoveryPort(std::nullopt).ok);  // research mode rejected
  CHECK(!ValidateDiscoveryPort(0).ok);
  CHECK(!ValidateDiscoveryPort(65536).ok);
  CHECK(ValidateDiscoveryPort(1234).ok);
}

void TestFilter() {
  blurlink::BridgeConfig cfg;
  cfg.discovery_port = 12345;
  cfg.broadcast = {255, 255, 255, 255};
  cfg.broadcast_str = "255.255.255.255";
  std::string f;
  CHECK(blurlink::BuildFilterString(cfg, f).ok);
  CHECK(f == "outbound && ip && udp && udp.DstPort == 12345 && ip.DstAddr == 255.255.255.255");
  // Injection attempts are not valid destinations -> rejected, never interpolated.
  cfg.broadcast_str = "255.255.255.255 || true";
  CHECK(!blurlink::BuildFilterString(cfg, f).ok);
  cfg.broadcast_str = "255.255.255.255";
  cfg.discovery_port = 0;
  CHECK(!blurlink::BuildFilterString(cfg, f).ok);

  // Task 12: the adapter term is opt-in (default: every interface, a
  // byte-identical filter) and canonical-numeric-only when set.
  blurlink::BridgeConfig plain;
  plain.discovery_port = 12345;
  plain.broadcast = {255, 255, 255, 255};
  plain.broadcast_str = "255.255.255.255";
  CHECK(blurlink::BuildFilterString(plain, f).ok);
  CHECK(f.find("ifIdx") == std::string::npos);
  blurlink::BridgeConfig scoped = plain;
  scoped.adapter_if_index = 22;
  CHECK(blurlink::BuildFilterString(scoped, f).ok);
  CHECK(f == "outbound && ip && udp && udp.DstPort == 12345 && ip.DstAddr == "
            "255.255.255.255 && ifIdx == 22");
}

void TestTransform() {
  auto pkt = BuildPacket({100, 96, 21, 89}, {255, 255, 255, 255}, 50000, 12345,
                         {0x42, 0x4C, 0x55, 0x52, 0x01});
  auto before = pkt;
  auto clone = blurlink::CloneWithNewDestination(pkt.data(), pkt.size(), {100, 96, 47, 177});
  CHECK(pkt == before);  // original untouched
  CHECK(clone[16] == 100 && clone[17] == 96 && clone[18] == 47 && clone[19] == 177);
  CHECK(std::memcmp(clone.data() + 12, pkt.data() + 12, 4) == 0);   // src same
  CHECK(std::memcmp(clone.data() + 20, pkt.data() + 20, 4) == 0);   // ports same
  CHECK(std::memcmp(clone.data() + 28, pkt.data() + 28, pkt.size() - 28) == 0);  // payload same
  CHECK(blurlink::VerifyIpChecksum(clone.data(), clone.size()));
  // Clone dst is unicast: must be rejected as a *filter destination*
  // (structural loop immunity).
  CHECK(!blurlink::ValidateBroadcastDestination("100.96.47.177").ok);
  // Fragments + IPv6 rejected.
  auto frag = pkt;
  frag[6] = 0x20;
  blurlink::UdpPacketView v;
  std::string rej;
  CHECK(!blurlink::TryParseUdpOverIpv4(frag.data(), frag.size(), v, rej) && rej == "ip-fragment");
  std::vector<std::uint8_t> v6(40, 0);
  v6[0] = 0x60;
  CHECK(!blurlink::TryParseUdpOverIpv4(v6.data(), v6.size(), v, rej) && rej == "not-ipv4");
  // Prefix gate.
  std::vector<std::uint8_t> pre = {0x42, 0x4C};
  CHECK(blurlink::IsPayloadPrefixMatch(pkt.data(), pkt.size(), pre.data(), pre.size()));
  std::vector<std::uint8_t> bad = {0x00};
  CHECK(!blurlink::IsPayloadPrefixMatch(pkt.data(), pkt.size(), bad.data(), bad.size()));
}

void TestRateLimiter() {
  blurlink::RateLimiter lim(10, 5);
  for (int i = 0; i < 5; ++i) CHECK(lim.TryAcquire());
  CHECK(!lim.TryAcquire());
  CHECK(lim.rejected() == 1);
  std::this_thread::sleep_for(std::chrono::milliseconds(250));  // ~2.5 tokens
  CHECK(lim.TryAcquire());
}

void TestDedup() {
  blurlink::DedupCache cache;
  blurlink::PacketIdentity a{{100, 96, 21, 89}, {255, 255, 255, 255}, 50000, 12345, 0x1234, 12};
  CHECK(!cache.CheckAndRecord(a));  // first sighting: forward
  CHECK(cache.CheckAndRecord(a));   // echo within TTL: skip clone
  blurlink::PacketIdentity b = a;
  b.ip_id = 0x1235;  // genuine retransmit: new IP ID -> forward again
  CHECK(!cache.CheckAndRecord(b));
}

// The echo backstop as the bridge actually uses it: the SAME bytes hostsim
// injects, parsed by the real parser, keyed the way WorkerLoop keys them. The
// elevated harness found that a byte-identical repeat was NOT suppressed, so
// this pins the layer that has to hold before blaming the driver: if this test
// passes and the live bridge still forwards the repeat, the two packets the
// driver delivers are not identical, and nothing above can know why.
void TestEchoBackstopIdentity() {
  auto build = []() {
    const char* payload = "BLSIMFWD";
    const std::size_t plen = 8;
    const std::size_t udp_len = 8 + plen;
    const std::size_t total = 20 + udp_len;
    std::vector<std::uint8_t> p(total, 0);
    p[0] = 0x45;  // IPv4, IHL 5
    p[2] = static_cast<std::uint8_t>(total >> 8);
    p[3] = static_cast<std::uint8_t>(total & 0xFF);
    p[4] = 0;
    p[5] = 0;     // IP ID 0, exactly what hostsim sends
    p[6] = 0x40;  // DF
    p[8] = 64;    // TTL
    p[9] = 17;    // UDP
    const std::uint8_t src[4] = {10, 5, 206, 140};
    const std::uint8_t dst[4] = {255, 255, 255, 255};
    for (int i = 0; i < 4; ++i) {
      p[12 + i] = src[i];
      p[16 + i] = dst[i];
    }
    p[20] = 51234 >> 8;
    p[21] = 51234 & 0xFF;
    p[22] = 50001 >> 8;
    p[23] = 50001 & 0xFF;
    p[24] = static_cast<std::uint8_t>(udp_len >> 8);
    p[25] = static_cast<std::uint8_t>(udp_len & 0xFF);
    for (std::size_t i = 0; i < plen; ++i) p[28 + i] = static_cast<std::uint8_t>(payload[i]);
    return p;
  };

  blurlink::UdpPacketView v;
  std::string why;
  auto bytes = build();
  CHECK(blurlink::TryParseUdpOverIpv4(bytes.data(), bytes.size(), v, why));
  CHECK(v.src_port == 51234 && v.dst_port == 50001);
  CHECK(v.ip_id == 0);
  CHECK(v.payload_len == 8);
  // Extra parens: the CHECK macro is variadic-unfriendly, and the comma inside
  // std::array<uint8_t, 4> would otherwise be read as an argument separator.
  CHECK((v.src_ip == std::array<std::uint8_t, 4>{10, 5, 206, 140}));
  CHECK((v.dst_ip == std::array<std::uint8_t, 4>{255, 255, 255, 255}));

  // Byte-identical, so the identity must be identical -- this is the premise
  // the whole backstop rests on.
  blurlink::UdpPacketView v2;
  auto bytes2 = build();
  CHECK(blurlink::TryParseUdpOverIpv4(bytes2.data(), bytes2.size(), v2, why));
  CHECK(bytes == bytes2);

  blurlink::PacketIdentity id{v.src_ip,  v.dst_ip,  v.src_port,
                              v.dst_port, v.ip_id,   static_cast<std::uint16_t>(v.payload_len + 8)};
  blurlink::PacketIdentity id2{v2.src_ip,  v2.dst_ip,  v2.src_port,
                               v2.dst_port, v2.ip_id,   static_cast<std::uint16_t>(v2.payload_len + 8)};
  CHECK(id == id2);
  CHECK(blurlink::PacketIdentityHash{}(id) == blurlink::PacketIdentityHash{}(id2));

  blurlink::DedupCache dedup;
  CHECK(!dedup.CheckAndRecord(id));   // first sighting: clone it
  CHECK(dedup.CheckAndRecord(id2));   // the identical repeat is an echo
  CHECK(dedup.echo_hits() == 1);
}

void TestSniffConfig() {
  blurlink::SniffConfig cfg;
  cfg.broadcasts = {{255, 255, 255, 255}};
  cfg.duration_sec = 15;
  cfg.max_packets = 200;
  std::string f;
  CHECK(blurlink::BuildSniffFilterString(cfg, f).ok);
  CHECK(f == "outbound && ip && udp && ip.DstAddr == 255.255.255.255");
  cfg.broadcasts.push_back({10, 88, 255, 255});
  CHECK(blurlink::BuildSniffFilterString(cfg, f).ok);
  CHECK(f ==
        "outbound && ip && udp && (ip.DstAddr == 255.255.255.255 || ip.DstAddr == 10.88.255.255)");
  // Unicast must never validate as a sniff destination.
  blurlink::SniffConfig bad;
  bad.broadcasts = {{100, 96, 47, 177}};
  CHECK(!blurlink::BuildSniffFilterString(bad, f).ok);
  // Bounds.
  blurlink::SniffConfig t = cfg;
  t.duration_sec = 4;
  CHECK(!blurlink::BuildSniffFilterString(t, f).ok);
  t = cfg;
  t.duration_sec = 61;
  CHECK(!blurlink::BuildSniffFilterString(t, f).ok);
  t = cfg;
  t.max_packets = 9;
  CHECK(!blurlink::BuildSniffFilterString(t, f).ok);
  t = cfg;
  t.broadcasts.clear();
  CHECK(!blurlink::BuildSniffFilterString(t, f).ok);
}

void TestSniffInbound() {
  blurlink::SniffConfig cfg;
  cfg.inbound = true;
  cfg.port = 50001;
  cfg.duration_sec = 15;
  cfg.max_packets = 200;
  std::string f;
  CHECK(blurlink::BuildSniffFilterString(cfg, f).ok);
  CHECK(f == "inbound && ip && udp && udp.DstPort == 50001");
  // Broadcasts are irrelevant (and unchecked) inbound.
  cfg.broadcasts = {{1, 2, 3, 4}};
  CHECK(blurlink::BuildSniffFilterString(cfg, f).ok);
  // Port required.
  cfg.port = 0;
  CHECK(!blurlink::BuildSniffFilterString(cfg, f).ok);
}

void TestGetStringArray() {  using namespace blurlink::minjson;
  std::vector<std::string> out;
  CHECK(GetStringArray("{\"broadcasts\":[\"a\",\"b\"]}", "broadcasts", out) && out.size() == 2 &&
        out[0] == "a" && out[1] == "b");
  CHECK(GetStringArray("{\"broadcasts\":[]}", "broadcasts", out) && out.empty());
  CHECK(!GetStringArray("{\"broadcasts\":\"x\"}", "broadcasts", out));
  CHECK(!GetStringArray("{\"broadcasts\":[\"a\",1]}", "broadcasts", out));
  CHECK(!GetStringArray("{}", "broadcasts", out));
}

void TestJson() {
  using namespace blurlink::minjson;
  std::string obj = "{\"type\":\"start\",\"token\":\"abc\",\"discoveryUdpPort\":1234,"
                    "\"preserveOriginalBroadcast\":true}";
  std::string s;
  long long n = 0;
  bool flag = false;
  CHECK(GetString(obj, "type", s) && s == "start");
  CHECK(GetInt(obj, "discoveryUdpPort", n) && n == 1234);
  CHECK(GetBool(obj, "preserveOriginalBroadcast", flag) && flag);
  CHECK(!GetString(obj, "missing", s));
  CHECK(Escape("a\"b\\c") == "a\\\"b\\\\c");
  // Escapes round-trip.
  std::string esc = "{\"a\":\"x\\\"y\\\\z\\n\\t\"}";
  CHECK(GetString(esc, "a", s) && s == "x\"y\\z\n\t");
  CHECK(!GetInt(obj, "type", n));    // wrong type accessor fails cleanly
  CHECK(!GetBool(obj, "type", flag));
  // Non-objects and malformed input: clean false, never a crash.
  CHECK(!GetString("[1,2,3]", "type", s));
  CHECK(!GetString("hello world", "type", s));
  CHECK(!GetString("", "type", s));
  CHECK(!GetString("{\"type\"", "type", s));  // unterminated
  CHECK(!GetString("{\"unterminated\":\"v", "type", s));
}

void TestJsonKeyShadowing() {
  using namespace blurlink::minjson;
  std::string s;
  long long n = 0;
  // A VALUE that contains the quoted key must never shadow the real member.
  std::string forged = "{\"first\":\"\\\"token\\\":\\\"forged\\\"\",\"token\":\"real\"}";
  CHECK(GetString(forged, "token", s) && s == "real");
  // Same for numbers after a value that embeds a fake "port":9.
  std::string tricky = "{\"type\":\"x\\\"} , \\\"port\\\":9\",\"port\":1234}";
  CHECK(GetInt(tricky, "port", n) && n == 1234);
  // Real key before a shadowing value still wins (first match in key position).
  std::string forward = "{\"token\":\"first\",\"note\":\"\\\"token\\\":\\\"later\\\"\"}";
  CHECK(GetString(forward, "token", s) && s == "first");
}

// Deterministic PRNG (xorshift64) for fuzz loops.
struct FuzzRng {
  std::uint64_t s = 0xB10C5EEDull;
  std::uint64_t next() {
    s ^= s << 13;
    s ^= s >> 7;
    s ^= s << 17;
    return s;
  }
};

void TestFuzzPacket() {
  FuzzRng rng;
  std::vector<std::uint8_t> buf(160);
  int accepted = 0;
  for (int i = 0; i < 20000; ++i) {
    for (auto& b : buf) b = static_cast<std::uint8_t>(rng.next());
    std::size_t len = static_cast<std::size_t>(rng.next() % (buf.size() + 1));
    if (i % 2 == 0 && len >= 28) {
      // Structured: coherent length fields so most inputs parse.
      buf[0] = 0x45;
      buf[2] = static_cast<std::uint8_t>(len >> 8);
      buf[3] = static_cast<std::uint8_t>(len & 0xFF);
      buf[6] = 0;
      buf[7] = 0;
      buf[9] = 17;
      std::size_t udp_len = len - 20;
      buf[24] = static_cast<std::uint8_t>(udp_len >> 8);
      buf[25] = static_cast<std::uint8_t>(udp_len & 0xFF);
    }
    blurlink::UdpPacketView v;
    std::string rej;
    bool ok = blurlink::TryParseUdpOverIpv4(buf.data(), len, v, rej);
    if (ok) {
      ++accepted;
      CHECK(rej.empty());
      CHECK(v.ip_header_len >= 20 && v.ip_header_len <= 60);
      CHECK(v.payload_offset + v.payload_len <= len);
      // Prefix gate + clone must uphold transform invariants on any accept.
      if (v.payload_len > 0) {
        std::vector<std::uint8_t> pre = {buf[v.payload_offset]};
        CHECK(blurlink::IsPayloadPrefixMatch(buf.data(), len, pre.data(), 1));
      }
      auto before = buf;
      auto clone = blurlink::CloneWithNewDestination(buf.data(), len, {10, 9, 9, 9});
      CHECK(buf == before);
      CHECK(clone.size() == len);
      CHECK(clone[16] == 10 && clone[19] == 9);
      CHECK(std::memcmp(clone.data() + 12, buf.data() + 12, 4) == 0);
      CHECK(blurlink::VerifyIpChecksum(clone.data(), clone.size()));
    } else {
      CHECK(!rej.empty());
    }
  }
  CHECK(accepted > 100);
}

void TestFuzzHex() {
  FuzzRng rng;
  const char* alpha = "0123456789abcdefABCDEF :-,xX\t";
  std::size_t alpha_len = std::strlen(alpha);
  for (int i = 0; i < 5000; ++i) {
    std::string s;
    std::size_t n = static_cast<std::size_t>(rng.next() % 40);
    for (std::size_t k = 0; k < n; ++k) s.push_back(alpha[rng.next() % alpha_len]);
    std::vector<std::uint8_t> out;
    auto r = blurlink::ParseHexSignature(s, out);
    if (r.ok) {
      CHECK(out.size() <= blurlink::kMaxSignatureBytes);
    } else {
      CHECK(!r.error.empty());
    }
  }
}

void TestFuzzJson() {
  FuzzRng rng;
  for (int i = 0; i < 5000; ++i) {
    std::string s = "{";
    std::size_t fields = static_cast<std::size_t>(rng.next() % 4);
    for (std::size_t f = 0; f < fields; ++f) {
      if (f) s += ",";
      s += "\"k" + std::to_string(rng.next() % 3) + "\":";
      switch (rng.next() % 4) {
        case 0: s += "\"v\""; break;
        case 1: s += std::to_string((long long)(rng.next() % 100000) - 50000); break;
        case 2: s += (rng.next() % 2) ? "true" : "false"; break;
        default: s += "nul"; break;  // malformed on purpose
      }
    }
    if (rng.next() % 2) s += "}";
    // Must never crash; accessors just report true/false.
    std::string str;
    long long num = 0;
    bool b = false;
    (void)blurlink::minjson::GetString(s, "k0", str);
    (void)blurlink::minjson::GetInt(s, "k1", num);
    (void)blurlink::minjson::GetBool(s, "k2", b);
  }
}

void TestDedupCapacity() {
  blurlink::DedupCache cache;  // capacity 1024
  for (int i = 0; i < 2000; ++i) {
    blurlink::PacketIdentity id{{10, 0, 0, 1}, {255, 255, 255, 255}, 40000, 9999,
                                static_cast<std::uint16_t>(i), 12};
    CHECK(!cache.CheckAndRecord(id));
  }
  blurlink::PacketIdentity first{{10, 0, 0, 1}, {255, 255, 255, 255}, 40000, 9999, 0, 12};
  blurlink::PacketIdentity last{{10, 0, 0, 1}, {255, 255, 255, 255}, 40000, 9999, 1999, 12};
  CHECK(!cache.CheckAndRecord(first));  // evicted (bounded memory)
  CHECK(cache.CheckAndRecord(last));    // still resident
}

void TestThreads() {
  blurlink::RateLimiter lim(1000, 64);
  std::atomic<int> acquired{0};
  auto hammer_lim = [&]() {
    for (int i = 0; i < 500; ++i)
      if (lim.TryAcquire()) ++acquired;
  };
  blurlink::DedupCache cache;
  std::atomic<int> hits{0};
  auto hammer_dedup = [&]() {
    for (int i = 0; i < 2000; ++i) {
      blurlink::PacketIdentity id{{10, 0, 0, 1}, {255, 255, 255, 255}, 40000, 9999,
                                  static_cast<std::uint16_t>(i % 64), 12};
      if (cache.CheckAndRecord(id)) ++hits;
    }
  };
  std::thread t1(hammer_lim), t2(hammer_lim), t3(hammer_dedup), t4(hammer_dedup);
  t1.join();
  t2.join();
  t3.join();
  t4.join();
  CHECK(acquired + lim.rejected() == 1000);
  CHECK(acquired >= 64);
  CHECK(hits > 0);  // shared identities across threads must register echoes
}

}  // namespace

void TestConsoleLog() {
  using blurlink::blog::Level;
  auto& log = blurlink::blog::Logger::Instance();
  log.set_level(Level::kInfo);
  CHECK(log.level() == Level::kInfo);
  // Must never crash, at any level, with hostile format strings.
  log.set_level(Level::kDebug);
  blurlink::blog::Info("test %s %d", "ok", 1);
  blurlink::blog::Debug("100%% harmless");
  blurlink::blog::Warn("");
  log.set_level(Level::kError);
  blurlink::blog::Info("suppressed");
  CHECK(log.level() == Level::kError);
  log.set_level(Level::kInfo);  // restore for other output
  CHECK(std::string(blurlink::blog::LevelName(Level::kWarn)) == "WARN");
}

void TestAnnounceCodec() {
  blurlink::AnnouncePacket p{};
  p.overlay = {25, 6, 7, 8};
  p.lan = {192, 168, 1, 50};
  p.blur_src_port = 51234;

  auto bytes = blurlink::EncodeAnnounce(p);
  CHECK(bytes.size() == blurlink::kAnnouncePacketSize);
  CHECK(bytes[0] == 'B' && bytes[1] == 'L' && bytes[2] == 'N' && bytes[3] == 'K');
  CHECK(bytes[4] == 1);   // version
  CHECK(bytes[5] == 0);   // flags
  CHECK(bytes[6] == 25 && bytes[9] == 8);
  CHECK(bytes[10] == 192 && bytes[13] == 50);
  CHECK(bytes[14] == 0xC8 && bytes[15] == 0x22);  // 51234, big-endian
  CHECK(bytes[16] == 0 && bytes[19] == 0);        // reserved

  blurlink::AnnouncePacket back{};
  std::string reason;
  CHECK(blurlink::DecodeAnnounce(bytes.data(), bytes.size(), back, reason));
  CHECK(back.overlay == p.overlay);
  CHECK(back.lan == p.lan);
  CHECK(back.blur_src_port == p.blur_src_port);

  blurlink::AnnouncePacket tmp{};
  CHECK(!blurlink::DecodeAnnounce(bytes.data(), 19, tmp, reason) && reason == "bad-length");
  CHECK(!blurlink::DecodeAnnounce(bytes.data(), 21, tmp, reason) && reason == "bad-length");
  CHECK(!blurlink::DecodeAnnounce(nullptr, 0, tmp, reason) && reason == "bad-length");

  auto bad = bytes;
  bad[0] = 'X';
  CHECK(!blurlink::DecodeAnnounce(bad.data(), bad.size(), tmp, reason) && reason == "bad-magic");
  bad = bytes;
  bad[4] = 99;
  CHECK(!blurlink::DecodeAnnounce(bad.data(), bad.size(), tmp, reason) &&
        reason == "unsupported-version");
  bad = bytes;
  bad[14] = 0;
  bad[15] = 0;
  CHECK(!blurlink::DecodeAnnounce(bad.data(), bad.size(), tmp, reason) && reason == "zero-port");
  bad = bytes;
  bad[6] = bad[7] = bad[8] = bad[9] = 0;
  CHECK(!blurlink::DecodeAnnounce(bad.data(), bad.size(), tmp, reason) &&
        reason == "bad-overlay-address");
  bad = bytes;
  bad[10] = bad[11] = bad[12] = bad[13] = 0;
  CHECK(!blurlink::DecodeAnnounce(bad.data(), bad.size(), tmp, reason) &&
        reason == "bad-lan-address");

  // Reserved bytes are forward-compatibility space: their contents must never
  // influence acceptance or the decoded values.
  bad = bytes;
  bad[16] = bad[17] = bad[18] = bad[19] = 0xFF;
  CHECK(blurlink::DecodeAnnounce(bad.data(), bad.size(), tmp, reason));
  CHECK(tmp.blur_src_port == 51234);

  // Never reads out of bounds, never throws, whatever the length.
  for (std::size_t n = 0; n < 40; ++n) {
    std::vector<std::uint8_t> junk(n, 0xAB);
    blurlink::AnnouncePacket d{};
    std::string r;
    (void)blurlink::DecodeAnnounce(junk.data(), junk.size(), d, r);
  }
}

void TestHostFilterString() {
  std::string err;
  std::vector<std::array<std::uint8_t, 4>> empty;
  auto none = blurlink::BuildHostFilterString(50001, empty, err);
  CHECK(err.empty());
  CHECK(none == "(inbound && ip && udp && udp.DstPort == 47811)");

  std::vector<std::array<std::uint8_t, 4>> lans = {{192, 168, 1, 50}, {10, 0, 0, 9}};
  auto f = blurlink::BuildHostFilterString(50001, lans, err);
  CHECK(err.empty());
  CHECK(f.find("udp.DstPort == 47811") != std::string::npos);
  CHECK(f.find("udp.DstPort == 50001 && (ip.SrcAddr == 10.0.0.9 || ip.SrcAddr == 192.168.1.50)") !=
        std::string::npos);
  CHECK(f.find("udp.SrcPort == 50001 && (ip.DstAddr == 10.0.0.9 || ip.DstAddr == 192.168.1.50)") !=
        std::string::npos);
  CHECK(f.find("255.255.255.255") == std::string::npos);
  // The player's Blur port must NOT be in the filter: a mismatch has to stay
  // visible to the code-side matcher. (The player port is not even passed in.)

  std::vector<std::array<std::uint8_t, 4>> dup = {{192, 168, 1, 50}, {192, 168, 1, 50}};
  auto fd = blurlink::BuildHostFilterString(50001, dup, err);
  CHECK(err.empty());
  CHECK(fd.find("192.168.1.50 || 192.168.1.50") == std::string::npos);

  std::vector<std::array<std::uint8_t, 4>> too_many(blurlink::kHostMaxPlayers + 1,
                                                    {192, 168, 1, 1});
  blurlink::BuildHostFilterString(50001, too_many, err);
  CHECK(!err.empty());
  err.clear();
  blurlink::BuildHostFilterString(0, lans, err);
  CHECK(!err.empty());
  err.clear();
  blurlink::BuildHostFilterString(65536, lans, err);
  CHECK(!err.empty());
  err.clear();
  // A discovery port equal to BlurLink's own introduction port is refused:
  // the two must never be confused for one another.
  CHECK(blurlink::BuildHostFilterString(blurlink::kHostAnnounceUdpPort, lans, err).empty());
  CHECK(!err.empty());

  // Task 12: the adapter term scopes all three terms, and is absent by default.
  CHECK(none.find("ifIdx") == std::string::npos);
  CHECK(f.find("ifIdx") == std::string::npos);
  err.clear();
  auto scoped = blurlink::BuildHostFilterString(50001, lans, err, 22);
  CHECK(err.empty());
  std::size_t ifidx_hits = 0;
  for (std::size_t pos = scoped.find("ifIdx == 22"); pos != std::string::npos;
       pos = scoped.find("ifIdx == 22", pos + 1)) {
    ++ifidx_hits;
  }
  CHECK(ifidx_hits == 3);
}

static blurlink::AnnouncePacket MkPlayer(const char* overlay, const char* lan,
                                         std::uint16_t port) {
  blurlink::AnnouncePacket p{};
  blurlink::TryParseIpv4(overlay, p.overlay);
  blurlink::TryParseIpv4(lan, p.lan);
  p.blur_src_port = port;
  return p;
}

void TestHostRoster() {
  std::string note;
  blurlink::HostRoster r;

  CHECK(r.Observe(MkPlayer("25.1.2.3", "192.168.1.50", 51234), 1000, note) ==
        blurlink::AddResult::Added);
  CHECK(r.Players().size() == 1);
  CHECK(r.Players()[0].in_filter);
  CHECK(r.Players()[0].last_seen_ms == 1000);

  CHECK(r.Observe(MkPlayer("25.1.2.3", "192.168.1.50", 51234), 2000, note) ==
        blurlink::AddResult::Refreshed);
  CHECK(r.Players().size() == 1);          // refresh must not duplicate
  CHECK(r.Players()[0].last_seen_ms == 2000);

  // Cap: keep adding past the limit; the roster must not grow beyond it.
  for (std::size_t i = 0; i < blurlink::kHostMaxPlayers + 2; ++i) {
    const std::string ov = "25.9.0." + std::to_string(i + 1);
    const std::string lan = "10.0.0." + std::to_string(i + 1);
    r.Observe(MkPlayer(ov.c_str(), lan.c_str(), static_cast<std::uint16_t>(40000 + i)), 3000, note);
  }
  CHECK(r.Players().size() == blurlink::kHostMaxPlayers);

  // Collision: the same LAN address AND the same Blur source port, different
  // overlay address. The host cannot tell the replies apart, so it refuses.
  blurlink::HostRoster c;
  CHECK(c.Observe(MkPlayer("25.1.2.3", "192.168.1.50", 51234), 1000, note) ==
        blurlink::AddResult::Added);
  CHECK(c.Observe(MkPlayer("25.4.5.6", "192.168.1.50", 51234), 1001, note) ==
        blurlink::AddResult::RefusedCollision);
  CHECK(!note.empty());
  CHECK(c.Players().size() == 1);          // the refused player does not enter
  CHECK(c.CountCollisions() == 1);

  // Same LAN address but a different Blur port is a genuinely distinct player.
  CHECK(c.Observe(MkPlayer("25.4.5.6", "192.168.1.50", 40000), 1002, note) ==
        blurlink::AddResult::Added);

  // Expiry keeps the entry (so the GUI keeps its counters) but drops it from
  // the filter.
  blurlink::HostRoster e;
  e.Observe(MkPlayer("25.1.2.3", "192.168.1.50", 51234), 0, note);
  CHECK(e.NoteForward({{192, 168, 1, 50}}, 0));
  e.Expire(blurlink::kHostPlayerExpirySeconds * 1000 + 1);
  CHECK(e.Players().size() == 1);
  CHECK(!e.Players()[0].in_filter);
  CHECK(e.Players()[0].forwards_heard == 1);
  CHECK(e.FilterAddresses().empty());

  // An expired player must not block a newcomer on the same address and port.
  CHECK(e.Observe(MkPlayer("25.7.7.7", "192.168.1.50", 51234),
                  blurlink::kHostPlayerExpirySeconds * 1000 + 2, note) ==
        blurlink::AddResult::Added);

  // ...but a live one must: the first entry came back on re-announce.
  blurlink::HostRoster live;
  live.Observe(MkPlayer("25.1.2.3", "192.168.1.50", 51234), 0, note);
  live.Observe(MkPlayer("25.1.2.3", "192.168.1.50", 51234), 100, note);  // refresh = still live
  CHECK(live.Observe(MkPlayer("25.8.8.8", "192.168.1.50", 51234), 200, note) ==
        blurlink::AddResult::RefusedCollision);

  // Re-announcing revives an expired player and puts it back in the filter.
  blurlink::HostRoster rv;
  rv.Observe(MkPlayer("25.1.2.3", "192.168.1.50", 51234), 0, note);
  rv.Expire(blurlink::kHostPlayerExpirySeconds * 1000 + 1);
  CHECK(!rv.Players()[0].in_filter);
  rv.Observe(MkPlayer("25.1.2.3", "192.168.1.50", 51234),
             blurlink::kHostPlayerExpirySeconds * 1000 + 5000, note);
  CHECK(rv.Players()[0].in_filter);
  CHECK(rv.FilterAddresses().size() == 1);

  // Revoke removes the entry entirely.
  CHECK(rv.Revoke({{25, 1, 2, 3}}));
  CHECK(rv.Players().empty());
  CHECK(rv.FilterAddresses().empty());
  CHECK(!rv.Revoke({{25, 1, 2, 3}}));  // idempotent, not a crash

  // Reply accounting matches on address AND destination port.
  blurlink::HostRoster q;
  q.Observe(MkPlayer("25.1.2.3", "192.168.1.50", 51234), 0, note);
  CHECK(q.NoteReplyForwarded({{192, 168, 1, 50}}, 51234));
  CHECK(!q.NoteReplyForwarded({{192, 168, 1, 50}}, 999));  // wrong port: no match
  CHECK(q.Players()[0].replies_forwarded == 1);
}

void TestHostClassifier() {
  std::vector<blurlink::HostPlayer> players(2);
  players[0].overlay = {25, 1, 2, 3};
  players[0].lan = {192, 168, 1, 50};
  players[0].blur_src_port = 51234;
  players[0].in_filter = true;
  players[1].overlay = {25, 4, 5, 6};
  players[1].lan = {10, 0, 0, 9};
  players[1].blur_src_port = 40000;
  players[1].in_filter = true;

  blurlink::UdpPacketView v{};
  v.dst_ip = {192, 168, 1, 50};
  v.src_port = 50001;
  v.dst_port = 51234;

  auto d = blurlink::ClassifyHostPacket(v, /*inbound=*/false, 50001, players);
  CHECK(d.action == blurlink::HostAction::Forward);
  CHECK(d.target_overlay == players[0].overlay);
  CHECK(d.reason == "forwarded");

  // The other player is matched independently.
  v.dst_ip = {10, 0, 0, 9};
  v.dst_port = 40000;
  d = blurlink::ClassifyHostPacket(v, false, 50001, players);
  CHECK(d.action == blurlink::HostAction::Forward);
  CHECK(d.target_overlay == players[1].overlay);

  // Right address, wrong port: NOT forwarded, and reported as a port mismatch
  // so a changed reply port is visible rather than swallowed.
  v.dst_ip = {192, 168, 1, 50};
  v.dst_port = 11111;
  d = blurlink::ClassifyHostPacket(v, false, 50001, players);
  CHECK(d.action == blurlink::HostAction::Reinject);
  CHECK(d.reason == "unmatched-port");

  // Unknown address entirely.
  v.dst_ip = {172, 16, 0, 5};
  v.dst_port = 51234;
  d = blurlink::ClassifyHostPacket(v, false, 50001, players);
  CHECK(d.action == blurlink::HostAction::Reinject);
  CHECK(d.reason == "unmatched-address");

  // Broadcast and multicast shapes are refused, never forwarded.
  for (const std::array<std::uint8_t, 4>& bcast :
       std::vector<std::array<std::uint8_t, 4>>{{255, 255, 255, 255}, {192, 168, 1, 255},
                                                {224, 0, 0, 1}, {0, 0, 0, 0}}) {
    v.dst_ip = bcast;
    d = blurlink::ClassifyHostPacket(v, false, 50001, players);
    CHECK(d.action == blurlink::HostAction::RefuseBroadcast);
    CHECK(d.reason == "broadcast-reply");
  }

  // Ambiguity: two live players sharing LAN address and Blur port. Refused, so
  // one player can never receive another's reply.
  std::vector<blurlink::HostPlayer> clash(2);
  clash[0] = players[0];
  clash[1] = players[0];
  clash[1].overlay = {25, 9, 9, 9};
  v.dst_ip = {192, 168, 1, 50};
  v.dst_port = 51234;
  d = blurlink::ClassifyHostPacket(v, false, 50001, clash);
  CHECK(d.action == blurlink::HostAction::RefuseAmbiguous);
  CHECK(d.reason == "ambiguous");

  // Inbound traffic is never forwarded, whatever it looks like.
  d = blurlink::ClassifyHostPacket(v, /*inbound=*/true, 50001, players);
  CHECK(d.action == blurlink::HostAction::Reinject);
  CHECK(d.reason == "inbound");

  // An expired player is not matched.
  players[0].in_filter = false;
  v.dst_ip = {192, 168, 1, 50};
  d = blurlink::ClassifyHostPacket(v, false, 50001, players);
  CHECK(d.action == blurlink::HostAction::Reinject);

  // Documented limitation: a player whose LAN address legitimately ends in .255
  // is refused as a broadcast reply. Visible (reason set), never silent.
  blurlink::HostPlayer wide{};
  wide.overlay = {25, 3, 3, 3};
  wide.lan = {10, 10, 11, 255};
  wide.blur_src_port = 1234;
  wide.in_filter = true;
  v.dst_ip = {10, 10, 11, 255};
  v.dst_port = 1234;
  d = blurlink::ClassifyHostPacket(v, false, 50001, {wide});
  CHECK(d.action == blurlink::HostAction::RefuseBroadcast);
}

static void AddPlayer(blurlink::HostEngine& e, const char* overlay, const char* lan,
                      std::uint16_t port, std::int64_t now_ms,
                      blurlink::HostOutcome* out = nullptr) {
  blurlink::AnnouncePacket a{};
  blurlink::TryParseIpv4(overlay, a.overlay);
  blurlink::TryParseIpv4(lan, a.lan);
  a.blur_src_port = port;
  const auto payload = blurlink::EncodeAnnounce(a);
  std::array<std::uint8_t, 4> lan_ip{};
  blurlink::TryParseIpv4(lan, lan_ip);
  auto pkt = BuildPacket(lan_ip, {25, 9, 9, 9}, port, 47811, payload);
  const auto result = e.Dispatch(pkt.data(), pkt.size(), /*inbound=*/true, now_ms);
  if (out) *out = result;
}

void TestHostEngineConfig() {
  blurlink::HostConfig ok{};
  ok.discovery_port = 50001;
  ok.adapter_if_index = 7;
  CHECK(blurlink::ValidateHostConfig(ok).ok);

  auto bad = ok;
  bad.discovery_port = 0;
  CHECK(!blurlink::ValidateHostConfig(bad).ok);
  bad = ok;
  bad.discovery_port = 65536;
  CHECK(!blurlink::ValidateHostConfig(bad).ok);
  // The discovery port must never be BlurLink's own introduction port.
  bad = ok;
  bad.discovery_port = 47811;
  CHECK(!blurlink::ValidateHostConfig(bad).ok);
  // An overlay adapter is required.
  bad = ok;
  bad.adapter_if_index = 0;
  CHECK(!blurlink::ValidateHostConfig(bad).ok);
  bad = ok;
  bad.rate_per_second = 0;
  CHECK(!blurlink::ValidateHostConfig(bad).ok);
  bad = ok;
  bad.rate_per_second = blurlink::kMaxRatePerSecond + 1;
  CHECK(!blurlink::ValidateHostConfig(bad).ok);
  bad = ok;
  bad.rate_burst = blurlink::kMaxRateBurst + 1;
  CHECK(!blurlink::ValidateHostConfig(bad).ok);
}

void TestHostEngineDispatch() {
  blurlink::HostConfig cfg{};
  cfg.discovery_port = 50001;
  cfg.adapter_if_index = 7;
  blurlink::HostEngine e(cfg);

  std::string err;
  // With no players, only BlurLink's own introduction port is watched — scoped
  // to this engine's adapter (Task 12: adapter_if_index = 7 above).
  CHECK(e.FilterString(err) == "(inbound && ip && udp && udp.DstPort == 47811 && ifIdx == 7)");
  CHECK(err.empty());

  // 1. A valid introduction enters the roster and schedules a rebuild.
  blurlink::HostOutcome out;
  AddPlayer(e, "25.1.2.3", "192.168.1.50", 51234, 0, &out);
  CHECK(out.reason == "announce-added");
  CHECK(out.roster_changed);
  CHECK(e.players().size() == 1);
  CHECK(e.players()[0].in_filter);

  // A repeat from an already-live player must NOT schedule another rebuild, or
  // a player announcing every few seconds would reopen the handle forever.
  AddPlayer(e, "25.1.2.3", "192.168.1.50", 51234, 100, &out);
  CHECK(out.reason == "announce-refreshed");
  CHECK(!out.roster_changed);

  // 2. Malformed introductions are rejected and counted; the roster is untouched.
  std::vector<std::uint8_t> junk(blurlink::kAnnouncePacketSize, 0xAB);
  auto junkpkt = BuildPacket({192, 168, 1, 50}, {25, 9, 9, 9}, 51234, 47811, junk);
  out = e.Dispatch(junkpkt.data(), junkpkt.size(), true, 200);
  CHECK(out.reason.rfind("announce-", 0) == 0);
  CHECK(e.counters().announce_rejected == 1);
  CHECK(e.players().size() == 1);

  // A wrong length on the introduction port is rejected before it is decoded.
  auto shortpkt = BuildPacket({192, 168, 1, 50}, {25, 9, 9, 9}, 51234, 47811, {1, 2, 3});
  out = e.Dispatch(shortpkt.data(), shortpkt.size(), true, 201);
  CHECK(out.reason == "announce-bad-length");
  CHECK(e.counters().announce_rejected == 2);

  // 3. A player's forward: the prerequisite signal, counted overall and per player.
  auto fwd = BuildPacket({192, 168, 1, 50}, {25, 9, 9, 9}, 51234, 50001, {1, 2, 3, 4});
  out = e.Dispatch(fwd.data(), fwd.size(), true, 300);
  CHECK(out.reason == "forward-heard");
  CHECK(e.counters().forwards_heard == 1);
  CHECK(e.players()[0].forwards_heard == 1);

  // 4. The host's reply is a forwarding DECISION; it is counted only once the
  //    caller reports that the clone actually went out.
  auto reply = BuildPacket({25, 9, 9, 9}, {192, 168, 1, 50}, 50001, 51234, {9, 9});
  out = e.Dispatch(reply.data(), reply.size(), false, 400);
  CHECK(out.action == blurlink::HostAction::Forward);
  CHECK((out.target_overlay == std::array<std::uint8_t, 4>{25, 1, 2, 3}));
  CHECK(e.counters().replies_forwarded == 0);
  e.NoteCloneSent({192, 168, 1, 50}, 51234);
  CHECK(e.counters().replies_forwarded == 1);
  CHECK(e.players()[0].replies_forwarded == 1);
  e.NoteCloneFailed();
  CHECK(e.counters().injection_errors == 1);

  // 5. A broadcast reply is refused and counted, never forwarded.
  auto bcast = BuildPacket({25, 9, 9, 9}, {255, 255, 255, 255}, 50001, 51234, {9});
  out = e.Dispatch(bcast.data(), bcast.size(), false, 500);
  CHECK(out.action == blurlink::HostAction::RefuseBroadcast);
  CHECK(e.counters().broadcast_replies == 1);
  CHECK(e.counters().replies_forwarded == 1);  // unchanged

  // 6. An unmatched reply is counted as unmatched, not silently dropped.
  auto other = BuildPacket({25, 9, 9, 9}, {172, 16, 0, 5}, 50001, 1234, {9});
  out = e.Dispatch(other.data(), other.size(), false, 600);
  CHECK(out.action == blurlink::HostAction::Reinject);
  CHECK(out.reason == "unmatched-address");
  CHECK(e.counters().unmatched_replies == 1);

  // 7. Unparseable input is reported, never acted on.
  std::vector<std::uint8_t> garbage(4, 0);
  out = e.Dispatch(garbage.data(), garbage.size(), false, 700);
  CHECK(out.action == blurlink::HostAction::Reinject);
  CHECK(out.reason.rfind("rejected-", 0) == 0);
  CHECK(e.counters().captured == 9);

  // The filter now carries the accepted player in both scoped terms.
  const auto f = e.FilterString(err);
  CHECK(err.empty());
  CHECK(f.find("ip.SrcAddr == 192.168.1.50") != std::string::npos);
  CHECK(f.find("ip.DstAddr == 192.168.1.50") != std::string::npos);
  CHECK(f.find("udp.DstPort == 47811") != std::string::npos);
}

void TestHostEngineFilterRebuildDebounce() {
  blurlink::HostConfig cfg{};
  cfg.discovery_port = 50001;
  cfg.adapter_if_index = 7;
  blurlink::HostEngine e(cfg);
  CHECK(e.counters().filter_reopens == 0);

  blurlink::HostOutcome o1, o2, o3;
  AddPlayer(e, "25.1.1.1", "192.168.1.50", 50001, 0, &o1);
  AddPlayer(e, "25.1.1.2", "192.168.1.51", 50002, 0, &o2);
  AddPlayer(e, "25.1.1.3", "192.168.1.52", 50003, 0, &o3);
  CHECK(o1.roster_changed && o2.roster_changed && o3.roster_changed);

  CHECK(!e.Tick(1000));                     // inside the debounce window
  CHECK(e.counters().filter_reopens == 0);  // a burst does not reopen per join
  CHECK(e.Tick(5000));                      // settled
  CHECK(e.counters().filter_reopens == 1);  // exactly one reopen for three joins
  CHECK(!e.Tick(9000));                     // nothing changed since
  CHECK(e.counters().filter_reopens == 1);

  std::string err;
  auto f = e.FilterString(err);
  CHECK(f.find("192.168.1.52") != std::string::npos);
  CHECK(f.find("192.168.1.50") != std::string::npos);

  // Expiry drops quiet players from the filter and settles into exactly one
  // further rebuild, while their entries stay visible for the GUI counters.
  const std::int64_t later = blurlink::kHostPlayerExpirySeconds * 1000 + 10000;
  CHECK(!e.Tick(later));
  CHECK(e.Tick(later + 3000));
  CHECK(e.counters().filter_reopens == 2);
  f = e.FilterString(err);
  CHECK(f.find("192.168.1.50") == std::string::npos);
  CHECK(f == "(inbound && ip && udp && udp.DstPort == 47811 && ifIdx == 7)");  // adapter 7, Task 12
  CHECK(e.players().size() == 3);
}

void TestHostEngineRevoke() {
  blurlink::HostConfig cfg{};
  cfg.discovery_port = 50001;
  cfg.adapter_if_index = 7;
  blurlink::HostEngine e(cfg);

  AddPlayer(e, "25.1.2.3", "192.168.1.50", 51234, 0);
  CHECK(e.players().size() == 1);
  CHECK(e.Revoke({25, 1, 2, 3}, 100));
  CHECK(e.players().empty());
  CHECK(!e.Revoke({25, 1, 2, 3}, 200));  // idempotent, not a crash

  std::string err;
  CHECK(e.FilterString(err) ==
        "(inbound && ip && udp && udp.DstPort == 47811 && ifIdx == 7)");  // adapter 7, Task 12
  // A revoke is a roster change, so it must settle into one rebuild.
  CHECK(e.Tick(5000));
  CHECK(e.counters().filter_reopens == 1);
}

// Task 12: the observe-only reply-shape rule (R6), mirroring the managed
// ReplyShapeValidatorTests. The fixture is synthetic at the real reply length
// (160 bytes) with a scrubbed 10.0.0.x address — never real capture bytes.
void TestReplyShape() {
  std::vector<std::uint8_t> reply(160);
  for (std::size_t i = 0; i < reply.size(); ++i) {
    reply[i] = static_cast<std::uint8_t>(0xA0 + (i & 0x0F));
  }
  reply[32] = 10;
  reply[33] = 0;
  reply[34] = 0;
  reply[35] = 20;

  const std::optional<int> no_length;
  const std::vector<std::uint8_t> no_prefix;
  // Off by default: matches anything.
  CHECK(blurlink::ReplyShapeMatches(reply.size(), reply.data(), reply.size(), no_length,
                                    no_prefix));
  // Length mismatch.
  const std::optional<int> len_24 = 24;
  CHECK(!blurlink::ReplyShapeMatches(reply.size(), reply.data(), reply.size(), len_24,
                                     no_prefix));
  // Prefix mismatch: one flipped byte in an otherwise identical prefix.
  std::vector<std::uint8_t> prefix(reply.begin(), reply.begin() + 12);
  prefix[5] ^= 0xFF;
  const std::optional<int> len_160 = 160;
  CHECK(!blurlink::ReplyShapeMatches(reply.size(), reply.data(), reply.size(), len_160,
                                     prefix));
  // Short buffer: fewer leading bytes than the prefix needs.
  std::vector<std::uint8_t> good(reply.begin(), reply.begin() + 12);
  CHECK(!blurlink::ReplyShapeMatches(reply.size(), reply.data(), 4, len_160, good));
}

// Task 12: the host engine counts shape outcomes where replies are classified,
// without ever changing the outcome (observe-only — never a drop).
void TestHostEngineReplyShape() {
  blurlink::HostConfig cfg{};
  cfg.discovery_port = 50001;
  cfg.adapter_if_index = 7;
  cfg.expected_reply_length = 2;
  cfg.expected_reply_prefix = {9, 9};
  blurlink::HostEngine e(cfg);
  AddPlayer(e, "25.1.2.3", "192.168.1.50", 51234, 0);
  CHECK(e.counters().reply_shape_checked == 0);

  // A reply matching the expectation: counted, not a mismatch, still forwarded.
  auto good = BuildPacket({25, 9, 9, 9}, {192, 168, 1, 50}, 50001, 51234, {9, 9});
  auto out = e.Dispatch(good.data(), good.size(), /*inbound=*/false, 100);
  CHECK(out.action == blurlink::HostAction::Forward);
  CHECK(e.counters().reply_shape_checked == 1);
  CHECK(e.counters().reply_shape_mismatch == 0);

  // A reply of the wrong shape: STILL forwarded (observe-only), but counted.
  auto bad = BuildPacket({25, 9, 9, 9}, {192, 168, 1, 50}, 50001, 51234, {9});
  out = e.Dispatch(bad.data(), bad.size(), /*inbound=*/false, 200);
  CHECK(out.action == blurlink::HostAction::Forward);
  CHECK(e.counters().reply_shape_checked == 2);
  CHECK(e.counters().reply_shape_mismatch == 1);

  // Forwards and announcements are not replies: never evaluated.
  auto fwd = BuildPacket({192, 168, 1, 50}, {25, 9, 9, 9}, 51234, 50001, {1, 2, 3, 4});
  out = e.Dispatch(fwd.data(), fwd.size(), /*inbound=*/true, 300);
  CHECK(out.reason == "forward-heard");
  CHECK(e.counters().reply_shape_checked == 2);
}

// The WinDivert ABI numbers we pass into the driver. Nothing else in the suite
// can see them, and getting one wrong does not fail to compile: it fails at
// runtime inside WinDivert, or (for SHUTDOWN) hangs Stop() outright. This test
// exists because exactly that happened -- WINDIVERT_SHUTDOWN_RECV was recorded
// as an ordinal 0 instead of the flag 0x1, and the helper could not be stopped
// or shut down until it was killed. Values per windivert.h (WinDivert 2.2).
static void TestWinDivertAbi() {
  CHECK(blurlink::kDivertLayerNetwork == 0);

  // Flags, not ordinals.
  CHECK(blurlink::kDivertFlagSniff == 0x0001);
  CHECK(blurlink::kDivertFlagDrop == 0x0002);
  CHECK(blurlink::kDivertShutdownRecv == 0x1);
  CHECK(blurlink::kDivertShutdownSend == 0x2);
  CHECK(blurlink::kDivertShutdownBoth == 0x3);
  CHECK((blurlink::kDivertShutdownRecv | blurlink::kDivertShutdownSend) ==
        blurlink::kDivertShutdownBoth);

  // Ordinals (first enumerators), so 0 and 1 are correct here.
  CHECK(blurlink::kDivertParamQueueLength == 0);
  CHECK(blurlink::kDivertParamQueueTime == 1);
}

int main() {
  TestWinDivertAbi();
  TestIpv4();
  TestHex();
  TestBroadcast();
  TestPort();
  TestFilter();
  TestTransform();
  TestRateLimiter();
  TestDedup();
  TestEchoBackstopIdentity();
  TestJson();
  TestJsonKeyShadowing();
  TestSniffConfig();
  TestSniffInbound();
  TestGetStringArray();
  TestConsoleLog();
  TestAnnounceCodec();
  TestHostFilterString();
  TestHostRoster();
  TestHostClassifier();
  TestHostEngineConfig();
  TestHostEngineDispatch();
  TestHostEngineFilterRebuildDebounce();
  TestHostEngineRevoke();
  TestReplyShape();
  TestHostEngineReplyShape();
  TestFuzzPacket();
  TestFuzzHex();
  TestFuzzJson();
  TestDedupCapacity();
  TestThreads();
  std::printf("%d checks, %d failures\n", g_checks, g_failures);
  return g_failures == 0 ? 0 : 1;
}
