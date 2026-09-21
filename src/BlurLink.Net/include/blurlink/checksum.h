#pragma once

// Portable IPv4/UDP checksum helpers (no platform headers).
// Mirrors WinDivertHelperCalcChecksums semantics for the v1 transform:
// after changing header fields, recompute IPv4 header checksum and UDP checksum.

#include <cstddef>
#include <cstdint>

namespace blurlink {

inline std::uint16_t OnesComplementSum(const std::uint8_t* data, std::size_t len,
                                       std::uint32_t initial = 0) {
  std::uint32_t sum = initial;
  std::size_t i = 0;
  while (i + 1 < len) {
    sum += static_cast<std::uint32_t>((static_cast<std::uint16_t>(data[i]) << 8) | data[i + 1]);
    i += 2;
  }
  if (i < len) {
    sum += static_cast<std::uint32_t>(data[i]) << 8;
  }
  while ((sum >> 16) != 0) {
    sum = (sum & 0xFFFFu) + (sum >> 16);
  }
  return static_cast<std::uint16_t>(~sum);
}

// Computes the IPv4 header checksum over ihl bytes (checksum field zeroed by caller).
inline std::uint16_t Ipv4HeaderChecksum(const std::uint8_t* ip_header, std::size_t ihl) {
  return OnesComplementSum(ip_header, ihl);
}

// Computes UDP checksum incl. IPv4 pseudo-header. udp_segment includes the UDP
// header with its checksum field zeroed. Returns 0xFFFF instead of 0 (RFC 768:
// a computed zero is transmitted as all-ones).
inline std::uint16_t UdpChecksumOverIpv4(const std::uint8_t src_ip[4],
                                         const std::uint8_t dst_ip[4],
                                         const std::uint8_t* udp_segment,
                                         std::size_t udp_len) {
  std::uint32_t sum = 0;
  sum += static_cast<std::uint32_t>((static_cast<std::uint16_t>(src_ip[0]) << 8) | src_ip[1]);
  sum += static_cast<std::uint32_t>((static_cast<std::uint16_t>(src_ip[2]) << 8) | src_ip[3]);
  sum += static_cast<std::uint32_t>((static_cast<std::uint16_t>(dst_ip[0]) << 8) | dst_ip[1]);
  sum += static_cast<std::uint32_t>((static_cast<std::uint16_t>(dst_ip[2]) << 8) | dst_ip[3]);
  sum += 17;  // protocol UDP
  sum += static_cast<std::uint32_t>(udp_len);
  std::size_t i = 0;
  while (i + 1 < udp_len) {
    if (i == 6) {  // skip checksum field itself
      i += 2;
      continue;
    }
    sum += static_cast<std::uint32_t>(
        (static_cast<std::uint16_t>(udp_segment[i]) << 8) | udp_segment[i + 1]);
    i += 2;
  }
  if (i < udp_len) {
    sum += static_cast<std::uint32_t>(udp_segment[i]) << 8;
  }
  while ((sum >> 16) != 0) {
    sum = (sum & 0xFFFFu) + (sum >> 16);
  }
  std::uint16_t result = static_cast<std::uint16_t>(~sum);
  return result == 0 ? std::uint16_t{0xFFFF} : result;
}

}  // namespace blurlink
