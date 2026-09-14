// Strict validation + narrow filter builder implementation.

#include "blurlink/config.h"

#include <cctype>
#include <sstream>
#include <stdexcept>

namespace blurlink {
namespace {

std::string Trim(const std::string& s) {
  std::size_t b = 0;
  while (b < s.size() && std::isspace(static_cast<unsigned char>(s[b]))) ++b;
  std::size_t e = s.size();
  while (e > b && std::isspace(static_cast<unsigned char>(s[e - 1]))) --e;
  return s.substr(b, e - b);
}

bool IsCanonicalQuad(const std::string& t, std::array<std::uint8_t, 4>& out) {
  // Exactly 4 dot-separated parts, each 1-3 digits, no leading zeros
  // (unless the part is exactly "0"), value 0..255.
  std::array<std::string, 4> parts;
  int idx = 0;
  std::string cur;
  for (char c : t) {
    if (c == '.') {
      if (idx >= 3 || cur.empty()) return false;
      parts[idx++] = cur;
      cur.clear();
    } else if (c >= '0' && c <= '9') {
      cur.push_back(c);
      if (cur.size() > 3) return false;
    } else {
      return false;
    }
  }
  if (idx != 3 || cur.empty()) return false;
  parts[3] = cur;
  for (int i = 0; i < 4; ++i) {
    const auto& p = parts[i];
    if (p.size() > 1 && p[0] == '0') return false;  // leading zero
    int v = 0;
    for (char c : p) v = v * 10 + (c - '0');
    if (v > 255) return false;
    out[static_cast<std::size_t>(i)] = static_cast<std::uint8_t>(v);
  }
  return true;
}

bool IsHexDigit(char c) {
  return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
}

}  // namespace

std::string Ipv4ToString(const std::array<std::uint8_t, 4>& ip) {
  std::ostringstream os;
  os << static_cast<int>(ip[0]) << '.' << static_cast<int>(ip[1]) << '.'
     << static_cast<int>(ip[2]) << '.' << static_cast<int>(ip[3]);
  return os.str();
}

bool TryParseIpv4(const std::string& text, std::array<std::uint8_t, 4>& out) {
  return IsCanonicalQuad(Trim(text), out);
}

ValidationResult ValidateIpv4(const std::string& text) {
  std::array<std::uint8_t, 4> ip{};
  if (!TryParseIpv4(text, ip)) {
    return ValidationResult::Fail("'" + text +
                                  "' is not a valid canonical IPv4 address (e.g. 100.96.47.177).");
  }
  return ValidationResult::Ok();
}

ValidationResult ParseHexSignature(const std::string& text, std::vector<std::uint8_t>& out) {
  out.clear();
  std::string t = Trim(text);
  if (t.empty()) {
    return ValidationResult::Ok();  // no constraint
  }
  // Normalize separators to spaces; strip 0x prefixes.
  std::string norm;
  for (std::size_t i = 0; i < t.size();) {
    if ((t[i] == '0') && i + 1 < t.size() && (t[i + 1] == 'x' || t[i + 1] == 'X')) {
      i += 2;
      continue;
    }
    char c = t[i];
    if (c == ':' || c == '-' || c == ',') {
      norm.push_back(' ');
    } else {
      norm.push_back(c);
    }
    ++i;
  }
  std::istringstream is(norm);
  std::vector<std::string> tokens;
  std::string tok;
  while (is >> tok) tokens.push_back(tok);
  if (tokens.empty()) {
    return ValidationResult::Ok();
  }
  auto push_byte = [&](const std::string& hex2) -> ValidationResult {
    if (hex2.size() != 2 || !IsHexDigit(hex2[0]) || !IsHexDigit(hex2[1])) {
      return ValidationResult::Fail("Invalid hex byte '" + hex2 +
                                     "'. Use bytes like \"42 4C 55 52\".");
    }
    out.push_back(static_cast<std::uint8_t>(std::stoul(hex2, nullptr, 16)));
    return ValidationResult::Ok();
  };
  if (tokens.size() == 1 && tokens[0].size() > 2 && tokens[0].size() % 2 == 0) {
    bool all_hex = true;
    for (char c : tokens[0]) {
      if (!IsHexDigit(c) && !std::isspace(static_cast<unsigned char>(c))) {
        all_hex = false;
        break;
      }
    }
    if (all_hex) {
      for (std::size_t i = 0; i < tokens[0].size(); i += 2) {
        auto r = push_byte(tokens[0].substr(i, 2));
        if (!r.ok) return r;
      }
    } else {
      return ValidationResult::Fail("Invalid hex byte '" + tokens[0] + "'.");
    }
  } else {
    for (const auto& tk : tokens) {
      // Strict: every byte must be exactly two hex digits.
      if (tk.size() != 2) {
        return ValidationResult::Fail("Invalid hex byte '" + tk +
                                       "'. Use bytes like \"42 4C 55 52\".");
      }
      auto r = push_byte(tk);
      if (!r.ok) return r;
    }
  }
  if (out.size() > kMaxSignatureBytes) {
    out.clear();
    return ValidationResult::Fail("Signature is limited to 64 bytes.");
  }
  return ValidationResult::Ok();
}

ValidationResult ValidateBroadcastDestination(const std::string& text,
                                              const std::string& adapter_broadcast) {
  std::array<std::uint8_t, 4> ip{};
  if (!TryParseIpv4(text, ip)) {
    return ValidationResult::Fail("'" + text +
                                  "' is not a valid IPv4 broadcast/multicast destination.");
  }
  // Global broadcast.
  if (ip[0] == 255 && ip[1] == 255 && ip[2] == 255 && ip[3] == 255) {
    return ValidationResult::Ok();
  }
  // Multicast 224.0.0.0/4.
  if (ip[0] >= 224 && ip[0] <= 239) {
    return ValidationResult::Ok();
  }
  // Directed broadcast: last octet 255, or matches adapter's broadcast.
  if (ip[3] == 255) {
    return ValidationResult::Ok();
  }
  if (!adapter_broadcast.empty()) {
    std::array<std::uint8_t, 4> ab{};
    if (TryParseIpv4(adapter_broadcast, ab) && ab == ip) {
      return ValidationResult::Ok();
    }
  }
  return ValidationResult::Fail(
      "'" + text +
      "' is not a broadcast or multicast address. Use 255.255.255.255, a directed "
      "broadcast (x.x.x.255), or a multicast address.");
}

ValidationResult ValidateDiscoveryPort(std::optional<int> port) {
  if (!port.has_value()) {
    return ValidationResult::Fail(
        "Discovery UDP port is unknown (Research mode). Enter the verified port from a "
        "local capture.");
  }
  if (*port < 1 || *port > 65535) {
    return ValidationResult::Fail("Port must be in range 1-65535.");
  }
  return ValidationResult::Ok();
}

ValidationResult BuildFilterString(const BridgeConfig& cfg, std::string& out_filter) {
  auto rp = ValidateDiscoveryPort(cfg.discovery_port);
  if (!rp.ok) return rp;
  auto rb = ValidateBroadcastDestination(cfg.broadcast_str);
  if (!rb.ok) return rb;
  std::ostringstream os;
  os << "outbound && ip && udp && udp.DstPort == " << cfg.discovery_port
     << " && ip.DstAddr == " << Ipv4ToString(cfg.broadcast);
  out_filter = os.str();
  return ValidationResult::Ok();
}

ValidationResult ValidateSniffConfig(SniffConfig& cfg) {
  if (cfg.duration_sec < kSniffMinDurationSec || cfg.duration_sec > kSniffMaxDurationSec) {
    return ValidationResult::Fail("Sniff duration must be 5-60s.");
  }
  if (cfg.max_packets < kSniffMinMaxPackets || cfg.max_packets > kSniffMaxMaxPackets) {
    return ValidationResult::Fail("Sniff packet cap must be 10-500.");
  }
  if (cfg.inbound) {
    if (cfg.port < 1 || cfg.port > 65535) {
      return ValidationResult::Fail("Reply listening needs the discovery port (detect it first).");
    }
    cfg.broadcast_strs.clear();
    return ValidationResult::Ok();
  }
  if (cfg.port < 0 || cfg.port > 65535) {
    return ValidationResult::Fail("Port must be 0-65535.");
  }
  if (cfg.broadcasts.empty() || cfg.broadcasts.size() > kSniffMaxBroadcasts) {
    return ValidationResult::Fail("Sniff needs 1-4 broadcast destinations.");
  }
  cfg.broadcast_strs.clear();
  for (const auto& b : cfg.broadcasts) {
    cfg.broadcast_strs.push_back(Ipv4ToString(b));
    if (auto r = ValidateBroadcastDestination(cfg.broadcast_strs.back()); !r.ok) {
      return r;
    }
  }
  return ValidationResult::Ok();
}

ValidationResult BuildSniffFilterString(const SniffConfig& cfg, std::string& out_filter) {
  SniffConfig copy = cfg;
  if (auto r = ValidateSniffConfig(copy); !r.ok) return r;
  std::ostringstream os;
  if (copy.inbound) {
    os << "inbound && ip && udp && udp.DstPort == " << copy.port;
    out_filter = os.str();
    return ValidationResult::Ok();
  }
  os << "outbound && ip && udp && ";
  if (copy.broadcast_strs.size() == 1) {
    os << "ip.DstAddr == " << copy.broadcast_strs[0];
  } else {
    os << "(";
    for (std::size_t i = 0; i < copy.broadcast_strs.size(); ++i) {
      if (i) os << " || ";
      os << "ip.DstAddr == " << copy.broadcast_strs[i];
    }
    os << ")";
  }
  if (copy.port != 0) {
    os << " && udp.DstPort == " << copy.port;
  }
  out_filter = os.str();
  return ValidationResult::Ok();
}

}  // namespace blurlink
