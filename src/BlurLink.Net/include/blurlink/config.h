#pragma once

// Bridge configuration + strict validation + narrow filter builder.
// Platform-independent so unit tests can compile it anywhere.
// Rule: user text is parsed first; only canonical numeric forms are
// interpolated into the WinDivert filter string. Arbitrary filter injection
// is impossible by construction.

#include <array>
#include <cstdint>
#include <optional>
#include <string>
#include <vector>

namespace blurlink {

inline constexpr int kDefaultRatePerSecond = 10;
inline constexpr int kDefaultRateBurst = 20;
inline constexpr int kMaxRatePerSecond = 100;
inline constexpr int kMaxRateBurst = 200;
inline constexpr std::size_t kMaxSignatureBytes = 64;

struct ValidationResult {
  bool ok = false;
  std::string error;
  static ValidationResult Ok() { return {true, {}}; }
  static ValidationResult Fail(const std::string& e) { return {false, e}; }
};

// Strict canonical dotted-quad IPv4 parse. Rejects IPv6, hostnames, CIDR,
// leading zeros ("01.2.3.4"), and surrounding whitespace is trimmed.
bool TryParseIpv4(const std::string& text, std::array<std::uint8_t, 4>& out);
ValidationResult ValidateIpv4(const std::string& text);

// Parses "42 4C 55 52" (also "42:4C-55,52", "0x42..", or continuous "424C5552").
// Empty/blank => no constraint. Max 64 bytes.
ValidationResult ParseHexSignature(const std::string& text, std::vector<std::uint8_t>& out);

// Broadcast/multicast destination check: 255.255.255.255, directed broadcast
// (last octet 255, or equal to adapter_broadcast), or IPv4 multicast
// (224.0.0.0/4). Unicast addresses are rejected.
ValidationResult ValidateBroadcastDestination(const std::string& text,
                                              const std::string& adapter_broadcast = {});

// Discovery port: required, 1..65535. nullopt => Research mode (rejected here).
ValidationResult ValidateDiscoveryPort(std::optional<int> port);

struct BridgeConfig {
  std::array<std::uint8_t, 4> host_ip{};  // validated overlay IPv4
  std::string host_ip_str;
  int discovery_port = 0;  // validated 1..65535
  std::array<std::uint8_t, 4> broadcast{};
  std::string broadcast_str;
  std::vector<std::uint8_t> payload_prefix;  // empty => any
  bool preserve_original_broadcast = true;
  int rate_per_second = kDefaultRatePerSecond;
  int rate_burst = kDefaultRateBurst;
  int adapter_if_index = 0;

  // Host-mode support: introduce ourselves to the host so it can map our
  // replies back to us. BlurLink's own introduction packet (see announce.h),
  // never a Blur protocol constant, and never a payload inspection.
  bool announce_to_host = true;
  std::array<std::uint8_t, 4> local_overlay_ip{};  // our own overlay address
  std::string local_overlay_ip_str;
};

// Builds e.g.:
//   outbound && ip && udp && udp.DstPort == 1234 && ip.DstAddr == 255.255.255.255
// Returns Fail on any invalid input. Only canonical values are interpolated.
ValidationResult BuildFilterString(const BridgeConfig& cfg, std::string& out_filter);

std::string Ipv4ToString(const std::array<std::uint8_t, 4>& ip);

// --- Research sniffer (explicit "detect discovery port" action) ---
inline constexpr int kSniffMinDurationSec = 5;
inline constexpr int kSniffMaxDurationSec = 60;
inline constexpr int kSniffMinMaxPackets = 10;
inline constexpr int kSniffMaxMaxPackets = 500;
inline constexpr std::size_t kSniffMaxBroadcasts = 4;

struct SniffConfig {
  std::vector<std::array<std::uint8_t, 4>> broadcasts;
  std::vector<std::string> broadcast_strs;  // canonical, same order
  bool inbound = false;  // false: outbound broadcasts; true: inbound replies to port
  int port = 0;          // required when inbound; optional outbound dst-port gate (0=any)
  int duration_sec = 15;
  int max_packets = 200;
};

// Validates direction/port/duration/cap + every broadcast; canonicalizes in place.
ValidationResult ValidateSniffConfig(SniffConfig& cfg);

// out: outbound && ip && udp && (dst addrs) [&& udp.DstPort == P]
// in:  inbound && ip && udp && udp.DstPort == P
ValidationResult BuildSniffFilterString(const SniffConfig& cfg, std::string& out_filter);

}  // namespace blurlink
