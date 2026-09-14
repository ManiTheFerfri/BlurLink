#pragma once

// BlurLink's own host-introduction packet and the host-mode filter builder.
//
// These are BlurLink protocol values, never inferred Blur ones. Fixed 20-byte
// layout: magic "BLNK", version, flags, overlay IPv4, LAN IPv4, Blur source port
// (big-endian), 4 reserved bytes. Addresses and a port only — never payloads.
//
// Platform-independent deliberately, so the portable test binary can compile it.

#include <array>
#include <cstddef>
#include <cstdint>
#include <string>
#include <vector>

namespace blurlink {

constexpr std::size_t kAnnouncePacketSize = 20;
constexpr std::uint16_t kHostAnnounceUdpPort = 47811;
constexpr std::size_t kHostMaxPlayers = 8;

struct AnnouncePacket {
  std::array<std::uint8_t, 4> overlay{};
  std::array<std::uint8_t, 4> lan{};
  std::uint16_t blur_src_port = 0;
};

// Encodes to exactly kAnnouncePacketSize bytes. Never throws.
std::vector<std::uint8_t> EncodeAnnounce(const AnnouncePacket& p);

// Strict gate: the only path the host ever acts on. Validates length, magic,
// version, both addresses and the port. Never throws, never reads past len.
bool DecodeAnnounce(const std::uint8_t* data, std::size_t len, AnnouncePacket& out,
                    std::string& reason);

// Builds the host-mode filter: three OR-ed terms, every one scoped to the given
// player LAN addresses. Empty string with an empty error is impossible — either
// a filter is returned with error empty, or the filter is empty and error set.
//
// The player's Blur source port is deliberately not a parameter: a port mismatch
// must remain visible to the code-side matcher rather than being swallowed by the
// filter.
std::string BuildHostFilterString(int discovery_port,
                                  const std::vector<std::array<std::uint8_t, 4>>& player_lans,
                                  std::string& error);

}  // namespace blurlink
