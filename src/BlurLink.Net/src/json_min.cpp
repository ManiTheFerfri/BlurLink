// Minimal flat-JSON implementation for the fixed IPC schema.

#include "blurlink/json_min.h"

#include <cctype>
#include <cstdio>

namespace blurlink::minjson {
namespace {

void SkipWs(const std::string& s, std::size_t& i) {
  while (i < s.size() && std::isspace(static_cast<unsigned char>(s[i]))) ++i;
}

bool ParseString(const std::string& s, std::size_t& i, std::string& out) {
  // s[i] == '"'
  ++i;
  out.clear();
  while (i < s.size()) {
    char c = s[i];
    if (c == '"') {
      ++i;
      return true;
    }
    if (c == '\\' && i + 1 < s.size()) {
      char e = s[i + 1];
      switch (e) {
        case '"': out.push_back('"'); break;
        case '\\': out.push_back('\\'); break;
        case '/': out.push_back('/'); break;
        case 'n': out.push_back('\n'); break;
        case 'r': out.push_back('\r'); break;
        case 't': out.push_back('\t'); break;
        case 'u': {
          // Best-effort \uXXXX -> '?' for non-ASCII (our schema is ASCII).
          if (i + 5 < s.size()) {
            out.push_back('?');
            i += 4;
          }
          break;
        }
        default: out.push_back(e); break;
      }
      i += 2;
    } else {
      out.push_back(c);
      ++i;
    }
  }
  return false;
}

// Walks a bracketed value (object or array) starting at s[i] == open.
// On success i points one past the matching close bracket. String-aware.
bool SkipBrackets(const std::string& s, std::size_t& i, char open, char close) {
  int depth = 0;
  bool in_str = false;
  for (std::size_t j = i; j < s.size(); ++j) {
    char c = s[j];
    if (in_str) {
      if (c == '\\') {
        ++j;
      } else if (c == '"') {
        in_str = false;
      }
    } else if (c == '"') {
      in_str = true;
    } else if (c == open) {
      ++depth;
    } else if (c == close) {
      if (--depth == 0) {
        i = j + 1;
        return true;
      }
    }
  }
  return false;
}

// Skips one JSON value (string, object, array, or bare literal) starting at
// s[i]. On success i points at the first char after the value.
bool SkipValue(const std::string& s, std::size_t& i) {
  if (i >= s.size()) return false;
  if (s[i] == '"') {
    std::string tmp;
    return ParseString(s, i, tmp);
  }
  if (s[i] == '{') return SkipBrackets(s, i, '{', '}');
  if (s[i] == '[') return SkipBrackets(s, i, '[', ']');
  while (i < s.size() && s[i] != ',' && s[i] != '}') ++i;
  return true;
}

}  // namespace

bool FindValue(const std::string& obj, const std::string& key, std::size_t& vbegin,
               std::size_t& vend) {
  // Member walk, not substring search: "key" is only accepted in KEY
  // position (a complete string followed by ':'), so a value that merely
  // contains the quoted key can never shadow the real field.
  std::string quoted = "\"" + key + "\"";
  std::size_t i = obj.find('{');
  if (i == std::string::npos) return false;
  ++i;
  std::string skipped;  // reused sink for member keys/values we walk past
  for (;;) {
    SkipWs(obj, i);
    if (i >= obj.size()) return false;
    if (obj[i] == '}') return false;  // end of object: key absent
    if (obj[i] != '"') {
      ++i;  // malformed member: resync one char
      continue;
    }
    const bool is_key = obj.compare(i, quoted.size(), quoted) == 0;
    if (is_key) {
      std::size_t colon = i + quoted.size();
      SkipWs(obj, colon);
      if (colon < obj.size() && obj[colon] == ':') {
        // Match in key position: parse the value.
        std::size_t v = colon + 1;
        SkipWs(obj, v);
        if (v >= obj.size()) return false;
        vbegin = v;
        if (!SkipValue(obj, v)) return false;
        vend = v;
        return true;
      }
    }
    // Not our key: skip the whole member (key string, colon, value).
    if (!ParseString(obj, i, skipped)) return false;
    SkipWs(obj, i);
    if (i < obj.size() && obj[i] == ':') {
      ++i;
      SkipWs(obj, i);
      if (!SkipValue(obj, i)) return false;
    }

    SkipWs(obj, i);
    if (i < obj.size() && obj[i] == ',') {
      ++i;
      continue;
    }
    return false;  // '}' or malformed: object fully walked
  }
}

bool GetString(const std::string& obj, const std::string& key, std::string& out) {
  std::size_t b = 0, e = 0;
  if (!FindValue(obj, key, b, e)) return false;
  if (b >= obj.size() || obj[b] != '"') return false;
  return ParseString(obj, b, out);
}

bool GetInt(const std::string& obj, const std::string& key, long long& out) {
  std::size_t b = 0, e = 0;
  if (!FindValue(obj, key, b, e)) return false;
  try {
    out = std::stoll(obj.substr(b, e - b));
    return true;
  } catch (...) {
    return false;
  }
}

bool GetBool(const std::string& obj, const std::string& key, bool& out) {
  std::size_t b = 0, e = 0;
  if (!FindValue(obj, key, b, e)) return false;
  std::string v = obj.substr(b, e - b);
  // trim
  std::size_t s = 0;
  while (s < v.size() && std::isspace(static_cast<unsigned char>(v[s]))) ++s;
  std::size_t t = v.size();
  while (t > s && std::isspace(static_cast<unsigned char>(v[t - 1]))) --t;
  v = v.substr(s, t - s);
  if (v == "true") {
    out = true;
    return true;
  }
  if (v == "false") {
    out = false;
    return true;
  }
  return false;
}

std::string Escape(const std::string& s) {
  std::string o;
  o.reserve(s.size() + 2);
  for (char c : s) {
    switch (c) {
      case '"': o += "\\\""; break;
      case '\\': o += "\\\\"; break;
      case '\n': o += "\\n"; break;
      case '\r': o += "\\r"; break;
      case '\t': o += "\\t"; break;
      default:
        if (static_cast<unsigned char>(c) < 0x20) {
          char buf[8];
          snprintf(buf, sizeof(buf), "\\u%04x", c);
          o += buf;
        } else {
          o.push_back(c);
        }
    }
  }
  return o;
}

bool GetStringArray(const std::string& obj, const std::string& key,
                    std::vector<std::string>& out) {
  out.clear();
  std::size_t b = 0, e = 0;
  if (!FindValue(obj, key, b, e)) return false;
  std::string v = obj.substr(b, e - b);
  std::size_t s = 0;
  while (s < v.size() && std::isspace(static_cast<unsigned char>(v[s]))) ++s;
  if (s >= v.size() || v[s] != '[') return false;
  std::size_t t = v.size();
  while (t > s && std::isspace(static_cast<unsigned char>(v[t - 1]))) --t;
  if (t <= s + 1 || v[t - 1] != ']') return false;
  std::size_t i = s + 1;
  while (true) {
    while (i < t && std::isspace(static_cast<unsigned char>(v[i]))) ++i;
    if (i >= t - 1) break;  // empty or trailing
    if (v[i] != '"') return false;
    std::string elem;
    if (!ParseString(v, i, elem)) return false;
    out.push_back(elem);
    while (i < t && std::isspace(static_cast<unsigned char>(v[i]))) ++i;
    if (i < t && v[i] == ',') {
      ++i;
      continue;
    }
    break;
  }
  while (i < t && std::isspace(static_cast<unsigned char>(v[i]))) ++i;
  if (i != t - 1) return false;  // trailing garbage
  return true;
}

}  // namespace blurlink::minjson
