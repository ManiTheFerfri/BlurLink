// IPv4/UDP parse + clone-with-new-destination implementation.

#include "blurlink/packet.h"

#include <cstdint>
#include <stdexcept>

namespace blurlink {
namespace {

std::uint16_t ReadU16BE(const std::uint8_t* p) {
  return static_cast<std::uint16_t>((static_cast<std::uint16_t>(p[0]) << 8) | p[1]);
}

void WriteU16BE(std::uint8_t* p, std::uint16_t v) {
  p[0] = static_cast<std::uint8_t>(v >> 8);
  p[1] = static_cast<std::uint8_t>(v & 0xFF);
}

}  // namespace

bool TryParseUdpOverIpv4(const std::uint8_t* data, std::size_t len, UdpPacketView& out,
                         std::string& reject_reason) {
  out = UdpPacketView{};
  if (len < 20) {
    reject_reason = "too-short-for-ipv4";
    return false;
  }
  if ((data[0] >> 4) != 4) {  // IPv6 (or other) can never satisfy v1
    reject_reason = "not-ipv4";
    return false;
  }
  std::size_t ihl = (data[0] & 0x0F) * 4;
  if (ihl < 20 || len < ihl + 8) {
    reject_reason = "bad-ihl-or-too-short-for-udp";
    return false;
  }
  std::uint16_t flags_frag = ReadU16BE(data + 6);
  if ((flags_frag & 0x3FFF) != 0) {
    reject_reason = "ip-fragment";
    return false;
  }
  if (data[9] != 17) {
    reject_reason = "not-udp";
    return false;
  }
  std::uint16_t total_len = ReadU16BE(data + 2);
  if (total_len > len) {
    reject_reason = "truncated";
    return false;
  }
  std::uint16_t udp_len = ReadU16BE(data + ihl + 4);
  if (udp_len < 8 || ihl + udp_len > len) {
    reject_reason = "bad-udp-length";
    return false;
  }
  out.src_port = ReadU16BE(data + ihl);
  out.dst_port = ReadU16BE(data + ihl + 2);
  out.ip_header_len = ihl;
  out.payload_offset = ihl + 8;
  out.payload_len = static_cast<std::size_t>(udp_len - 8);
  for (int i = 0; i < 4; ++i) {
    out.src_ip[static_cast<std::size_t>(i)] = data[12 + i];
    out.dst_ip[static_cast<std::size_t>(i)] = data[16 + i];
  }
  out.ip_id = ReadU16BE(data + 4);
  reject_reason.clear();
  return true;
}

std::vector<std::uint8_t> CloneWithNewDestination(const std::uint8_t* packet, std::size_t len,
                                                  const std::array<std::uint8_t, 4>& new_dst) {
  UdpPacketView v;
  std::string rej;
  if (!TryParseUdpOverIpv4(packet, len, v, rej)) {
    throw std::invalid_argument("Unsupported packet: " + rej);
  }
  std::vector<std::uint8_t> clone(packet, packet + len);
  clone[16] = new_dst[0];
  clone[17] = new_dst[1];
  clone[18] = new_dst[2];
  clone[19] = new_dst[3];
  // Recompute IPv4 header checksum.
  clone[10] = 0;
  clone[11] = 0;
  WriteU16BE(clone.data() + 10, Ipv4HeaderChecksum(clone.data(), v.ip_header_len));
  // Recompute UDP checksum over pseudo-header + segment.
  std::size_t udp_off = v.ip_header_len;
  std::uint16_t udp_len = ReadU16BE(clone.data() + udp_off + 4);
  clone[udp_off + 6] = 0;
  clone[udp_off + 7] = 0;
  std::uint16_t sum = UdpChecksumOverIpv4(clone.data() + 12, clone.data() + 16,
                                          clone.data() + udp_off, udp_len);
  WriteU16BE(clone.data() + udp_off + 6, sum);
  return clone;
}

}  // namespace blurlink
