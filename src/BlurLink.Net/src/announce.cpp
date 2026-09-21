#include "blurlink/announce.h"

#include <algorithm>
#include <set>
#include <sstream>

#include "blurlink/config.h"  // Ipv4ToString

namespace blurlink {
namespace {

constexpr std::uint8_t kMagic[4] = {'B', 'L', 'N', 'K'};
constexpr std::uint8_t kVersion = 1;

bool IsAllZero(const std::array<std::uint8_t, 4>& ip) {
  return ip[0] == 0 && ip[1] == 0 && ip[2] == 0 && ip[3] == 0;
}

}  // namespace

std::vector<std::uint8_t> EncodeAnnounce(const AnnouncePacket& p) {
  std::vector<std::uint8_t> b(kAnnouncePacketSize, 0);
  std::copy(std::begin(kMagic), std::end(kMagic), b.begin());
  b[4] = kVersion;
  b[5] = 0;  // flags, reserved
  std::copy(p.overlay.begin(), p.overlay.end(), b.begin() + 6);
  std::copy(p.lan.begin(), p.lan.end(), b.begin() + 10);
  b[14] = static_cast<std::uint8_t>(p.blur_src_port >> 8);
  b[15] = static_cast<std::uint8_t>(p.blur_src_port & 0xFF);
  // 16..19 stay zero: reserved forward-compatibility space.
  return b;
}

bool DecodeAnnounce(const std::uint8_t* data, std::size_t len, AnnouncePacket& out,
                    std::string& reason) {
  out = AnnouncePacket{};
  reason.clear();

  if (len != kAnnouncePacketSize || data == nullptr) {
    reason = "bad-length";
    return false;
  }
  if (!std::equal(std::begin(kMagic), std::end(kMagic), data)) {
    reason = "bad-magic";
    return false;
  }
  if (data[4] != kVersion) {
    reason = "unsupported-version";
    return false;
  }

  AnnouncePacket p{};
  std::copy(data + 6, data + 10, p.overlay.begin());
  std::copy(data + 10, data + 14, p.lan.begin());
  p.blur_src_port = static_cast<std::uint16_t>((data[14] << 8) | data[15]);

  if (IsAllZero(p.overlay)) {
    reason = "bad-overlay-address";
    return false;
  }
  if (IsAllZero(p.lan)) {
    reason = "bad-lan-address";
    return false;
  }
  if (p.blur_src_port == 0) {
    reason = "zero-port";
    return false;
  }

  out = p;
  return true;
}

std::string BuildHostFilterString(int discovery_port,
                                  const std::vector<std::array<std::uint8_t, 4>>& player_lans,
                                  std::string& error, int adapter_if_index) {
  error.clear();

  if (discovery_port < 1 || discovery_port > 65535) {
    error = "discovery port out of range (detect it first)";
    return {};
  }
  if (discovery_port == static_cast<int>(kHostAnnounceUdpPort)) {
    error = "the discovery port cannot be BlurLink's own introduction port";
    return {};
  }
  if (player_lans.size() > kHostMaxPlayers) {
    error = "too many players (max " + std::to_string(kHostMaxPlayers) + ")";
    return {};
  }

  // <set> gives both canonicalisation by string form and de-duplication: two
  // players behind the same LAN address collapse into one address term, which
  // is all the filter needs.
  std::set<std::string> distinct;
  for (const auto& ip : player_lans) {
    if (IsAllZero(ip)) {
      error = "invalid player address 0.0.0.0";
      return {};
    }
    distinct.insert(Ipv4ToString(ip));
  }

  std::ostringstream out;
  // An adapter index scopes every term to one interface; absent (or
  // non-positive) leaves the filter exactly as it was before.
  std::string ifidx;
  if (adapter_if_index > 0) {
    ifidx = " && ifIdx == " + std::to_string(adapter_if_index);
  }
  out << "(inbound && ip && udp && udp.DstPort == " << kHostAnnounceUdpPort << ifidx << ")";

  if (!distinct.empty()) {
    std::string src_terms;
    std::string dst_terms;
    bool first = true;
    for (const auto& ip : distinct) {
      if (!first) {
        src_terms += " || ";
        dst_terms += " || ";
      }
      src_terms += "ip.SrcAddr == " + ip;
      dst_terms += "ip.DstAddr == " + ip;
      first = false;
    }
    // Inbound term: the prerequisite diagnostic — are player forwards arriving?
    out << " || (inbound && ip && udp && udp.DstPort == " << discovery_port << " && ("
        << src_terms << ")" << ifidx << ")";
    // Outbound term: the host's replies to those players.
    out << " || (outbound && ip && udp && udp.SrcPort == " << discovery_port << " && ("
        << dst_terms << ")" << ifidx << ")";
  }

  return out.str();
}

}  // namespace blurlink
