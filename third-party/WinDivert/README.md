# WinDivert binaries (not committed)

BlurLink Join mode needs the WinDivert driver + DLL at **runtime only**.
The app ships them and stages them automatically — no manual setup.

## Pinned version (verified 2026-09-09)

- WinDivert **2.2.2**, **x64** (`WinDivert.dll` + `WinDivert64.sys`),
  dual-licensed LGPLv3-or-GPLv2 (see `LICENSE.Windivert`).
- Official package: `WinDivert-2.2.2-A.zip` from https://reqrypt.org/download/
- SHA256 (x64):
  - `WinDivert.dll`: `C1E060EE19444A259B2162F8AF0F3FE8C4428A1C6F694DCE20DE194AC8D7D9A2`
  - `WinDivert64.sys`: `8DA085332782708D8767BCACE5327A6EC7283C17CFB85E40B03CD2323A90DDC2`

## How shipping works

1. `scripts/build-portable.ps1` downloads the official zip automatically
   when `x64/` is empty (or uses the files already there), verifies both
   files, and embeds them plus `blurlink-net.exe` into `BlurLink.exe`.
2. On first app launch the GUI extracts all three to
   `%LocalAppData%\BlurLink\bin\` (only when missing or changed).
3. The helper loads `WinDivert.dll` from its own directory first
   (`WinDivertApi::Load`), so no PATH or System32 changes are needed.
4. `scripts/package.ps1` (dev folder) copies the same files next to the
   executables plus `LICENSE.Windivert`, satisfying attribution.

## Manual setup (only if you bypass the scripts)

Copy to EITHER `third-party/WinDivert/x64/` (git-ignored build input) or
next to the built `blurlink-net.exe`:
- `WinDivert.dll`
- `WinDivert64.sys`

## First run

- Starting the bridge triggers a UAC prompt (helper only) and Windows installs
  the WinDivert driver on demand. No firewall/antivirus changes are required.
- On 32-bit or ARM Windows the helper exits with a clear error
  (v1 supports Windows 10/11 x64 only).
