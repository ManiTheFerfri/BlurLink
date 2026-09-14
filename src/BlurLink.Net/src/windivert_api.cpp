// Dynamic WinDivert.dll loader: helper directory first, then default search.

#include "blurlink/windivert_api.h"

#ifdef _WIN32

namespace blurlink {

bool WinDivertApi::Load(std::string& error) {
  if (loaded()) {
    return true;
  }
  char dir[MAX_PATH] = {0};
  DWORD n = GetModuleFileNameA(nullptr, dir, MAX_PATH);
  HMODULE mod = nullptr;
  if (n > 0 && n < MAX_PATH) {
    std::string path(dir);
    auto sep = path.find_last_of("\\/");
    std::string dll = (sep == std::string::npos ? "WinDivert.dll"
                                                : path.substr(0, sep + 1) + "WinDivert.dll");
    mod = LoadLibraryA(dll.c_str());
  }
  if (!mod) {
    mod = LoadLibraryA("WinDivert.dll");
  }
  if (!mod) {
    error =
        "WinDivert.dll not found (tried helper directory and PATH). Download the x64 "
        "build documented in third-party/WinDivert/README.md and place WinDivert.dll "
        "(+ WinDivert64.sys) next to blurlink-net.exe.";
    return false;
  }
  auto need = [&](const char* name, FARPROC& out) {
    out = GetProcAddress(mod, name);
    return out != nullptr;
  };
  FARPROC p = nullptr;
  bool ok = true;
  // GetProcAddress returns FARPROC; converting to function pointer types
  // is the documented usage (MSVC accepts it silently).
#if defined(__GNUC__) && !defined(__clang__)
#pragma GCC diagnostic push
#pragma GCC diagnostic ignored "-Wcast-function-type"
#endif
  ok &= need("WinDivertOpen", p);
  open_ = reinterpret_cast<FnDivertOpen>(p);
  ok &= need("WinDivertRecv", p);
  recv_ = reinterpret_cast<FnDivertRecv>(p);
  ok &= need("WinDivertSend", p);
  send_ = reinterpret_cast<FnDivertSend>(p);
  ok &= need("WinDivertClose", p);
  close_ = reinterpret_cast<FnDivertClose>(p);
  ok &= need("WinDivertShutdown", p);
  shutdown_ = reinterpret_cast<FnDivertShutdown>(p);
  ok &= need("WinDivertSetParam", p);
  set_param_ = reinterpret_cast<FnDivertSetParam>(p);
  ok &= need("WinDivertHelperCalcChecksums", p);
  calc_checksums_ = reinterpret_cast<FnDivertHelperCalcChecksums>(p);
#if defined(__GNUC__) && !defined(__clang__)
#pragma GCC diagnostic pop
#endif
  if (!ok) {
    error = "WinDivert.dll is present but too old: required exports are missing.";
    FreeLibrary(mod);
    open_ = nullptr;
    recv_ = nullptr;
    send_ = nullptr;
    close_ = nullptr;
    shutdown_ = nullptr;
    set_param_ = nullptr;
    calc_checksums_ = nullptr;
    return false;
  }
  module_ = mod;
  return true;
}

void WinDivertApi::Unload() {
  if (module_) {
    FreeLibrary(module_);
    module_ = nullptr;
  }
  open_ = nullptr;
  recv_ = nullptr;
  send_ = nullptr;
  close_ = nullptr;
  shutdown_ = nullptr;
  set_param_ = nullptr;
  calc_checksums_ = nullptr;
}

}  // namespace blurlink

#endif  // _WIN32
