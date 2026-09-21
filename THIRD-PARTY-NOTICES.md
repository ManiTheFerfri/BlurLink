# Third-party notices

BlurLink itself is MIT-licensed (see `LICENSE`).

## WinDivert (required at runtime for Join mode)

- WinDivert is developed by basil00 and distributed under your choice of the
  GNU Lesser General Public License (LGPL) Version 3 or the GNU General
  Public License (GPL) Version 2. See https://reqrypt.org/windivert.html and
  https://github.com/basil00/Divert for the canonical license text.
  The full text ships as `third-party/WinDivert/LICENSE.Windivert` and is
  included in distributions that bundle the binaries.
- WinDivert binaries (`WinDivert.dll`, `WinDivert64.sys` / `WinDivert32.sys`,
  `WinDivert64.sys` variants) are **not** shipped in this repository.
  Download the version documented in `third-party/WinDivert/README.md` and
  place the files next to `blurlink-net.exe` for development, or let the
  packaging script stage them.
- The WinDivert license notices must be included in any distribution that
  bundles WinDivert binaries. `package.ps1` copies the license files
  automatically when they are present.

## .NET runtime

- BlurLink.Desktop targets .NET 10 (or newer). The .NET runtime is covered by
  the MIT license (see https://github.com/dotnet/runtime/blob/main/LICENSE.TXT).
