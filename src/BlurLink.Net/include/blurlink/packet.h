#pragma once

// Pure IPv4/UDP packet classification + transform. Platform-independent.
// Transform rule: clone packet, replace ONLY IPv4 destination, recompute IP
// and UDP checksums. Payload bytes and UDP ports are never modified.

#include <array>
#include <cstddef>
#include <cstdint>
#include <string>
#include <vector>

#include "checksum.h"

namespace blurlink {

struct UdpPacketView {
  std::uint16_t src_port = 0;
  std::uint16_t dst_port = 0;
  std::size_t ip_header_len = 0;
  std::size_t payload_offset = 0;  // offset of UDP payload within packet
  std::size_t payload_len = 0;
  std::array<std::uint8_t, 4> src_ip{};
  std::array<std::uint8_t, 4> dst_ip{};
  std::uint16_t ip_id = 0;
};

// Returns true for outbound-eligible IPv4/UDP datagrams. On false,
// reject_reason is one of: too-short-for-ipv4, not-ipv4,
// bad-ihl-or-too-short-for-udp, ip-fragment, not-udp, truncated,
// bad-udp-length. IPv6 can never satisfy this (version nibble != 4),
// so v1 rejects IPv6 structurally.
bool TryParseUdpOverIpv4(const std::uint8_t* data, std::size_t len, UdpPacketView& out,
                         std::string& reject_reason);

inline bool IsPayloadPrefixMatch(const std::uint8_t* packet, std::size_t len,
                                 const std::uint8_t* prefix, std::size_t prefix_len) {
  if (prefix_len == 0) {
    return true;
  }
  UdpPacketView v;
  std::string rej;
  if (!TryParseUdpOverIpv4(packet, len, v, rej)) {
    return false;
  }
  if (v.payload_len < prefix_len) {
    return false;
  }
  for (std::size_t i = 0; i < prefix_len; ++i) {
    if (packet[v.payload_offset + i] != prefix[i]) {
      return false;
    }
  }
  return true;
}

// Clone + replace IPv4 dst + fix checksums. Input untouched. Throws
// std::invalid_argument on unsupported packets.
std::vector<std::uint8_t> CloneWithNewDestination(const std::uint8_t* packet, std::size_t len,
                                                  const std::array<std::uint8_t, 4>& new_dst);

inline bool VerifyIpChecksum(const std::uint8_t* packet, std::size_t len) {
  if (len < 20) {
    return false;
  }
  std::size_t ihl = (packet[0] & 0x0F) * 4;
  if (ihl < 20 || len < ihl) {
    return false;
  }
  return OnesComplementSum(packet, ihl) == 0;
}

}  // namespace blurlink
