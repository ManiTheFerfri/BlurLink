#pragma once

// Dynamic-load shim for WinDivert.dll. The helper builds without the WinDivert
// SDK: all entry points resolve at runtime via GetProcAddress. If the DLL (or
// the driver) is missing, Start fails with a clear message instead of a
// load-time crash. Place WinDivert.dll next to blurlink-net.exe; see
// third-party/WinDivert/README.md for the supported version/arch.
//
// Requires Windows + the real WinDivert types at build time only for the
// WINDIVERT_ADDRESS layout. To stay buildable with stock MSVC/MinGW headers,
// the layout is redeclared here (verified against WinDivert 2.2).

#ifdef _WIN32

#include <windows.h>

#include <cstdint>
#include <string>

#include "blurlink/windivert_abi.h"

namespace blurlink {

// The numeric ABI constants (layer, flags, params, shutdown) live in
// windivert_abi.h so the portable tests can pin them; see that header for why
// the shutdown values in particular must not be "simplified".

// WINDIVERT_ADDRESS layout as documented for WinDivert 2.2:
//   INT64 Timestamp; bitfields (Layer:8, Event:8, Sniffed/Outbound/Loopback/
//   Impostor/IPv6/IPChecksum/TCPChecksum/UDPChecksum:1); union with
//   WINDIVERT_DATA_NETWORK { IfIdx, SubIfIdx }.
// Total: 8 + 8 + 8 = 24 bytes.
struct DivertDataNetwork {
  std::uint32_t IfIdx;
  std::uint32_t SubIfIdx;
};

struct DivertAddress {
  std::int64_t Timestamp;
  std::uint64_t Layer : 8;
  std::uint64_t Event : 8;
  std::uint64_t Sniffed : 1;
  std::uint64_t Outbound : 1;
  std::uint64_t Loopback : 1;
  std::uint64_t Impostor : 1;
  std::uint64_t IPv6 : 1;
  std::uint64_t IPChecksum : 1;
  std::uint64_t TCPChecksum : 1;
  std::uint64_t UDPChecksum : 1;
  std::uint64_t Reserved : 40;
  DivertDataNetwork Network;
};

static_assert(sizeof(DivertAddress) == 24, "DivertAddress must match WinDivert 2.x (24 bytes)");

using FnDivertOpen = HANDLE(WINAPI*)(const char*, int, std::int16_t, std::uint64_t);
using FnDivertRecv = BOOL(WINAPI*)(HANDLE, void*, UINT, UINT*, DivertAddress*);
using FnDivertSend = BOOL(WINAPI*)(HANDLE, const void*, UINT, UINT*, const DivertAddress*);
using FnDivertClose = BOOL(WINAPI*)(HANDLE);
using FnDivertShutdown = BOOL(WINAPI*)(HANDLE, int);
using FnDivertSetParam = BOOL(WINAPI*)(HANDLE, int, std::uint64_t);
using FnDivertHelperCalcChecksums = BOOL(WINAPI*)(void*, UINT, DivertAddress*, std::uint64_t);

class WinDivertApi {
 public:
  WinDivertApi() = default;
  ~WinDivertApi() { Unload(); }

  WinDivertApi(const WinDivertApi&) = delete;
  WinDivertApi& operator=(const WinDivertApi&) = delete;

  // Loads WinDivert.dll from the helper's own directory first (safe DLL
  // search), then system search. Returns false with a message on failure.
  bool Load(std::string& error);

  void Unload();

  bool loaded() const { return module_ != nullptr && open_ != nullptr; }

  FnDivertOpen open_ = nullptr;
  FnDivertRecv recv_ = nullptr;
  FnDivertSend send_ = nullptr;
  FnDivertClose close_ = nullptr;
  FnDivertShutdown shutdown_ = nullptr;
  FnDivertSetParam set_param_ = nullptr;
  FnDivertHelperCalcChecksums calc_checksums_ = nullptr;

 private:
  HMODULE module_ = nullptr;
};

}  // namespace blurlink

#endif  // _WIN32
