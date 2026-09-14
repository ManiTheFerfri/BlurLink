#include "blurlink/host_classify.h"

namespace blurlink {
namespace {

// Conservative broadcast/multicast test. Anything that cannot be a unicast
// destination is refused rather than forwarded on a guess.
bool IsBroadcastDst(const std::array<std::uint8_t, 4>& ip) {
  if (ip[0] == 255 && ip[1] == 255 && ip[2] == 255 && ip[3] == 255) return true;  // limited
  if (ip[0] == 0) return true;              // 0.0.0.0/8 "this network"
  if (ip[3] == 255) return true;            // subnet-directed broadcast
  return ip[0] >= 224 && ip[0] <= 239;      // IPv4 multicast
}

}  // namespace

HostDecision ClassifyHostPacket(const UdpPacketView& view, bool inbound, int discovery_port,
                                const std::vector<HostPlayer>& players) {
  (void)discovery_port;  // the filter already scopes the source port

  if (inbound) {
    return {HostAction::Reinject, {}, "inbound"};
  }

  if (IsBroadcastDst(view.dst_ip)) {
    return {HostAction::RefuseBroadcast, {}, "broadcast-reply"};
  }

  // Address first, so a mismatch on the port alone is reportable as such.
  std::vector<const HostPlayer*> by_addr;
  for (const auto& p : players) {
    if (p.in_filter && p.lan == view.dst_ip) by_addr.push_back(&p);
  }
  if (by_addr.empty()) {
    return {HostAction::Reinject, {}, "unmatched-address"};
  }

  std::vector<const HostPlayer*> exact;
  for (const auto* p : by_addr) {
    if (p->blur_src_port == view.dst_port) exact.push_back(p);
  }
  if (exact.empty()) {
    // A player owns this address but on a different port. Reported, never
    // guessed at: this is the visible signal for the src-port assumption.
    return {HostAction::Reinject, {}, "unmatched-port"};
  }
  if (exact.size() > 1) {
    return {HostAction::RefuseAmbiguous, {}, "ambiguous"};
  }

  return {HostAction::Forward, exact[0]->overlay, "forwarded"};
}

}  // namespace blurlink
