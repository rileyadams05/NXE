# Third-party software

NXE is an open-source integration project. Upstream licenses remain with their respective authors; see the source distributions and notices shipped alongside each component.

| Project | Upstream | Version/source used | License | NXE use |
|---|---|---|---|---|
| Xenia Canary UWP | https://github.com/EmulationCollective/xenia-uwp | Local Xbox/UWP source snapshot; linked native build | BSD-3-Clause and component licenses | Linked Xbox 360 backend |
| Dolphin UWP | https://github.com/SternXD/dolphin | Local `DolphinWinRT` UWP build | GPL-2.0-or-later | Co-packaged GameCube/Wii engine |
| XBSX2 / PCSX2 | https://github.com/XboxEmulationHub/XBSX2 | Local AVX2 UWP build | GPL-3.0-or-later | Co-packaged PlayStation 2 engine |
| RetroArch | https://github.com/XboxEmulationHub/RetroArch | XboxEmulationHub release `08-19-2026`, SeriesConsoles-AllCores | GPL-3.0-or-later | Co-packaged frontend and verified x64 core set |
| libretro API | https://github.com/libretro/libretro-common | API headers and platform support | Various permissive/GPL notices | Core loading contract |
| Flycast | https://github.com/flyinghead/flycast | Local x64 UWP libretro build | GPL-2.0-or-later | Dreamcast/NAOMI core |
| Websocket.Client | https://github.com/Marfusios/websocket-client | NuGet 4.6.1 | MIT | Cloud/management bridge |
| Microsoft.NETCore.UniversalWindowsPlatform | https://www.nuget.org/packages/Microsoft.NETCore.UniversalWindowsPlatform | NuGet 6.2.14 | Microsoft package license | UWP runtime |

NXE-specific changes to the UWP engine sources are maintained as source overlays in the working tree: Dolphin, XBSX2 and RetroArch read and consume the structured `nxe-engine-launch.txt` record. The native bridge owns the Xenia linkage and the Flycast libretro host. When redistributing source, preserve each upstream notice and the corresponding source overlay.

The repository must not contain private signing certificates, FTP credentials, Vortex tokens, Device Portal credentials or generated release binaries.
