cmake_minimum_required(VERSION 3.20)

# hostsim: the synthetic-peer injector used by scripts/test-e2e.ps1.
# A TEST TOOL, deliberately NOT part of the shipped helper -- it can synthesize
# arbitrary packets, which the elevated blurlink-net.exe must never be able to
# do. Windows-only, because it needs WinDivert.
#
#   cmake -S tools/hostsim -B out/hostsim -DCMAKE_BUILD_TYPE=Release
#   cmake --build out/hostsim --config Release
project(blurlink-hostsim LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

if(NOT WIN32)
  message(FATAL_ERROR "hostsim is Windows-only (it injects through WinDivert).")
endif()

# Reuses the helper's real, already-verified pieces rather than re-deriving
# them: the WinDivert dynamic-load shim and the announcement codec. Nothing here
# re-declares the WinDivert ABI.
set(NET_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../../src/BlurLink.Net")

add_executable(hostsim
  hostsim.cpp
  ${NET_DIR}/src/announce.cpp
  ${NET_DIR}/src/config.cpp
  ${NET_DIR}/src/packet.cpp
  ${NET_DIR}/src/windivert_api.cpp
)

target_include_directories(hostsim PRIVATE ${NET_DIR}/include)
target_link_libraries(hostsim PRIVATE iphlpapi ws2_32)

if(MSVC)
  target_compile_options(hostsim PRIVATE /W4 /WX /permissive- /EHsc)
else()
  target_compile_options(hostsim PRIVATE -Wall -Wextra -Werror)
endif()

# Loaded at runtime, like the helper's copy. Staged next to the exe when the
# vendored WinDivert is present.
set(WINDIVERT_DIR "${NET_DIR}/../../third-party/WinDivert/x64")
if(EXISTS "${WINDIVERT_DIR}/WinDivert.dll")
  add_custom_command(TARGET hostsim POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
      "${WINDIVERT_DIR}/WinDivert.dll" $<TARGET_FILE_DIR:hostsim>)
endif()
