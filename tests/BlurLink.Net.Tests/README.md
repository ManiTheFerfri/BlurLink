# BlurLink.Net.Tests

Portable C++ unit tests for the helper's platform-independent logic
(`config`, `packet`/checksums, `rate_limiter`, `dedup`, `json_min`).
No WinDivert DLL, driver, elevation, or Windows SDK required — they compile
with MSVC, MinGW-g++, or Clang on any host.

```powershell
cmake -S tests/BlurLink.Net.Tests -B build-nativetests
cmake --build build-nativetests --config Release
ctest --test-dir build-nativetests --output-on-failure
```

The Windows-only parts (`bridge`, `ipc_server`, `windivert_api`, `main`)
are covered by code review + the manual checklist in `docs/troubleshooting.md`
(checklist items 7–10) because they require an elevated driver.
