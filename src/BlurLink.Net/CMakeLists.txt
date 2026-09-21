cmake_minimum_required(VERSION 3.20)

# blurlink-net: elevated WinDivert helper (Windows x64 only).
#   cmake -S src/BlurLink.Net -B build-native -DCMAKE_BUILD_TYPE=Release
#   cmake --build build-native --config Release
project(blurlink-net VERSION 0.1.0 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

if(NOT WIN32)
  message(FATAL_ERROR "blurlink-net is Windows-only.")
endif()

if(NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
  message(FATAL_ERROR "blurlink-net requires x64 (64-bit).")
endif()

add_executable(blurlink-net
  src/main.cpp
  src/announce.cpp
  src/config.cpp
  src/packet.cpp
  src/json_min.cpp
  src/windivert_api.cpp
  src/bridge.cpp
  src/host_classify.cpp
  src/host_roster.cpp
  src/host_engine.cpp
  src/host_session.cpp
  src/ipc_server.cpp
)

if(MSVC)
  # Embeds app.manifest (asInvoker + supportedOS). MinGW links its own
  # default manifest; the helper does not need ours (RtlGetVersion is
  # manifest-proof), so keep this MSVC-only to avoid manifest conflicts.
  target_sources(blurlink-net PRIVATE src/app.rc)
endif()

target_include_directories(blurlink-net PRIVATE include)
target_link_libraries(blurlink-net PRIVATE iphlpapi ws2_32)

if(MSVC)
  target_compile_options(blurlink-net PRIVATE /W4 /WX /permissive- /EHsc)
  # The custom manifest rides in via src/app.rc (see target_sources above);
  # without this the linker emits its own default manifest too and CVTRES
  # fails the link with CVT1100 duplicate resource (observed on MSBuild 18.x).
  target_link_options(blurlink-net PRIVATE "/MANIFEST:NO")
else()
  target_compile_options(blurlink-net PRIVATE -Wall -Wextra -Werror)
endif()

# WinDivert.dll is loaded at runtime; copy a locally staged copy next to the
# exe when present (see third-party/WinDivert/README.md).
set(WINDIVERT_DIR "${CMAKE_SOURCE_DIR}/../../third-party/WinDivert/x64")
if(EXISTS "${WINDIVERT_DIR}/WinDivert.dll")
  add_custom_command(TARGET blurlink-net POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
      "${WINDIVERT_DIR}/WinDivert.dll" $<TARGET_FILE_DIR:blurlink-net>)
endif()
