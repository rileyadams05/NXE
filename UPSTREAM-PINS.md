# Upstream engine pins

NXE keeps the dashboard and integration source in this repository. Large upstream emulator trees are kept outside the repository and are consumed by the reproducible build script; this avoids committing generated binaries, NuGet caches, and multi-gigabyte build trees.

| Engine | Upstream | NXE build input |
|---|---|---|
| Xenia Canary UWP | [EmulationCollective/xenia-uwp](https://github.com/EmulationCollective/xenia-uwp) | Linked native backend in `Dashboard/NXE.Emulation.Native` |
| Dolphin UWP | [SternXD/dolphin](https://github.com/SternXD/dolphin) | `DolphinWinRT` UWP payload |
| XBSX2 / PCSX2 | [XboxEmulationHub/XBSX2](https://github.com/XboxEmulationHub/XBSX2) | `pcsx2-uwpx64` UWP payload |
| RetroArch Xbox/UWP | [XboxEmulationHub/RetroArch](https://github.com/XboxEmulationHub/RetroArch) | `RetroArch-msvcUWP` payload |
| Flycast | [flyinghead/flycast](https://github.com/flyinghead/flycast) | `flycast_libretro.dll` core |

The exact local source/build provenance is recorded in the build logs and release metadata for each test package. Before shipping a release, update this table with the upstream commit SHA used by the local build. NXE does not execute downloaded emulator binaries during startup; package contents are copied, inspected, and signed as part of the AppX build.
