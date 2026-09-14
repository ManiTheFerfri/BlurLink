cmake_minimum_required(VERSION 3.20)

# Portable unit tests: only the platform-independent helper sources.
#   cmake -S . -B build && cmake --build build && ctest
# Compiles with MSVC, MinGW, or Clang — no WinDivert, no Windows SDK needed.
project(blurlink-net-tests LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

set(NET_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../../src/BlurLink.Net")

add_executable(blurlink-net-tests
  src/test_main.cpp
  ${NET_DIR}/src/announce.cpp
  ${NET_DIR}/src/config.cpp
  ${NET_DIR}/src/packet.cpp
  ${NET_DIR}/src/json_min.cpp
  ${NET_DIR}/src/host_classify.cpp
  ${NET_DIR}/src/host_engine.cpp
  ${NET_DIR}/src/host_roster.cpp
)

target_include_directories(blurlink-net-tests PRIVATE ${NET_DIR}/include)

if(MSVC)
  target_compile_options(blurlink-net-tests PRIVATE /W4 /WX /EHsc)
else()
  target_compile_options(blurlink-net-tests PRIVATE -Wall -Wextra -Werror)
endif()

enable_testing()
add_test(NAME blurlink-net-tests COMMAND blurlink-net-tests)
