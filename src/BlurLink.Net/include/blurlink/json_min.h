#pragma once

// Minimal flat-JSON helpers for the fixed IPC schema (no third-party JSON
// dependency). Only what the helper needs: read string/int/bool fields,
// emit status/error/event objects with proper string escaping.

#include <cstdint>
#include <optional>
#include <string>
#include <vector>

namespace blurlink::minjson {

// Finds a top-level "key" and returns its raw value range. Returns false if absent.
bool FindValue(const std::string& obj, const std::string& key, std::size_t& vbegin,
               std::size_t& vend);

bool GetString(const std::string& obj, const std::string& key, std::string& out);
bool GetInt(const std::string& obj, const std::string& key, long long& out);
bool GetBool(const std::string& obj, const std::string& key, bool& out);
// Top-level string array, e.g. "broadcasts": ["a","b"]. Numbers/bools rejected.
bool GetStringArray(const std::string& obj, const std::string& key,
                    std::vector<std::string>& out);

std::string Escape(const std::string& s);

}  // namespace blurlink::minjson
