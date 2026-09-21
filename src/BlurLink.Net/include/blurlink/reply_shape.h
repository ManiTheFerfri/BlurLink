#pragma once

// Observe-only reply-shape rule (R6: length + leading prefix; nonce-echo needs
// query memory the helper does not keep, so it is explicitly not here).
//
// Mirrors BlurLink.Core/Net/ReplyShapeValidator.Matches exactly: empty
// expectations mean "off" (matches anything). Never throws, never reads past
// the provided buffers. Platform-independent so the portable test binary can
// compile it.
//
// actual_len is the UDP payload length and actual_leading points at the first
// actual_leading_len payload bytes; the caller passes what it parsed, so a
// short buffer simply mismatches instead of over-reading.

#include <cstddef>
#include <cstdint>
#include <optional>
#include <vector>

namespace blurlink {

inline bool ReplyShapeMatches(std::size_t actual_len, const std::uint8_t* actual_leading,
                              std::size_t actual_leading_len,
                              const std::optional<int>& expected_len,
                              const std::vector<std::uint8_t>& expected_prefix) {
  if (!expected_len.has_value() && expected_prefix.empty()) {
    return true;
  }
  if (expected_len.has_value() && actual_len != static_cast<std::size_t>(*expected_len)) {
    return false;
  }
  if (!expected_prefix.empty()) {
    if (actual_leading == nullptr || actual_leading_len < expected_prefix.size()) {
      return false;
    }
    for (std::size_t i = 0; i < expected_prefix.size(); ++i) {
      if (actual_leading[i] != expected_prefix[i]) {
        return false;
      }
    }
  }
  return true;
}

}  // namespace blurlink
