#pragma once

// The WinDivert 2.2 ABI numbers BlurLink depends on, transcribed from
// windivert.h (basil00/Divert, the header shipped with WinDivert 2.2.2).
//
// Platform-independent on purpose, exactly like announce.h, so the portable
// test binary can assert the values. That matters more than it looks: every
// other ABI mistake fails loudly, but a wrong value here fails *inside the
// driver* at runtime.
//
// The trap these constants fell into once already (2026-09-12, found by
// scripts/test-host-e2e.ps1): WINDIVERT_SHUTDOWN_* are BIT FLAGS, not an
// ordinal enum. They were recorded as RECV=0, SEND=1, BOTH=2, so Stop() passed
// 0 -- an invalid value. WinDivertShutdown failed, a blocked WinDivertRecv
// stayed blocked, worker_.join() never returned, and the whole helper wedged:
// `stop` never replied, the watchdog's exit path hung on the same call, and the
// only way out was killing the process. Meanwhile *filter rebuilds* kept
// working, because they also call WinDivertClose() right afterwards, which
// unblocks a pending recv by invalidating the handle -- which is precisely why
// the wrong value stayed hidden.
//
// So: values below are pinned by static_assert, and the shape of each group
// (flags vs ordinal) is stated. Do not "simplify" these to 0,1,2.

#include <cstdint>

namespace blurlink {

// Ordinal. WINDIVERT_LAYER_NETWORK is 0 because it is the first enumerator.
inline constexpr int kDivertLayerNetwork = 0;

// Bit flags (WINDIVERT_FLAG_*).
inline constexpr std::uint64_t kDivertFlagSniff = 0x0001;
inline constexpr std::uint64_t kDivertFlagDrop = 0x0002;

// Ordinal (WINDIVERT_PARAM_*).
inline constexpr int kDivertParamQueueLength = 0;
inline constexpr int kDivertParamQueueTime = 1;

// Bit flags (WINDIVERT_SHUTDOWN_*). BOTH is RECV|SEND, not 2.
inline constexpr int kDivertShutdownRecv = 0x1;
inline constexpr int kDivertShutdownSend = 0x2;
inline constexpr int kDivertShutdownBoth = 0x3;

static_assert(kDivertLayerNetwork == 0, "WINDIVERT_LAYER_NETWORK is the first enumerator (0).");
static_assert(kDivertFlagSniff == 0x0001, "WINDIVERT_FLAG_SNIFF is 0x0001 in windivert.h.");
static_assert(kDivertFlagDrop == 0x0002, "WINDIVERT_FLAG_DROP is 0x0002 in windivert.h.");
static_assert(kDivertParamQueueLength == 0 && kDivertParamQueueTime == 1,
              "WINDIVERT_PARAM_* are ordinal: QUEUE_LENGTH=0, QUEUE_TIME=1.");
static_assert(kDivertShutdownRecv == 0x1,
              "WINDIVERT_SHUTDOWN_RECV is 0x1. Passing 0 makes WinDivertShutdown "
              "fail, leaving a blocked recv blocked, which hangs Stop().");
static_assert(kDivertShutdownSend == 0x2, "WINDIVERT_SHUTDOWN_SEND is 0x2 in windivert.h.");
static_assert(kDivertShutdownBoth == 0x3,
              "WINDIVERT_SHUTDOWN_BOTH is RECV|SEND == 0x3, not an ordinal 2.");
static_assert(kDivertShutdownBoth == (kDivertShutdownRecv | kDivertShutdownSend),
              "SHUTDOWN values are flags, so BOTH must equal RECV | SEND.");

}  // namespace blurlink
