#pragma once

// Host-mode reply classification.
//
// Deliberately split from the WinDivert plumbing: this is the whole decision
// core, so it can be tested exhaustively without a driver or elevation.
//
// A reply is matched to a player by (destination address, destination port) —
// the pair the host's Blur actually addresses. Every non-forward outcome carries
// a reason string, because "we saw it and chose not to forward it" must be
// distinguishable from "we never saw it".

#include <array>
#include <cstdint>
#include <string>
#include <vector>

#include "blurlink/host_roster.h"
#include "blurlink/packet.h"

namespace blurlink {

enum class HostAction {
  Forward,          // clone with dst := target_overlay
  Reinject,         // not ours: reinject the original, change nothing
  RefuseAmbiguous,  // two live players share the match key: refuse, never guess
  RefuseBroadcast,  // unverified shape: refuse and report
};

struct HostDecision {
  HostAction action = HostAction::Reinject;
  std::array<std::uint8_t, 4> target_overlay{};
  std::string reason;
};

// `inbound` traffic is never forwarded. Broadcast/multicast destinations are
// refused before player matching: by address alone the host cannot tell a
// broadcast *reply* from its own Blur broadcasting a discovery *request*, and
// forwarding that request would hand a player an unroutable source address.
//
// Known limitation (see the spec's §14): a player whose LAN address legitimately
// ends in .255 — possible on a subnet wider than /24 — has replies refused as
// broadcast. The refusal is reported, so it is visible rather than silent.
HostDecision ClassifyHostPacket(const UdpPacketView& view, bool inbound, int discovery_port,
                                const std::vector<HostPlayer>& players);

}  // namespace blurlink
