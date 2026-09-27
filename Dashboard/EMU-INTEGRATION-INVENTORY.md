# NXE emulator integration inventory

This is the pre-integration inventory required by the NXE full implementation brief. It records what is present in the supplied trees and what is not yet integrated into the NXE UWP package. No emulator source was copied into the dashboard as part of this inventory.

## Current NXE boundary

- `Dashboard/UWP/NxeDashboard.Uwp.csproj` is a C#/.NET UWP x64 application. It has no native C++/WinRT component project or native emulator host project.
- `Dashboard/UWP/Emulators/EmulatorManager.cs` builds protocol URIs and `Dashboard/UWP/Runtime/UwpBackendLaunchAdapter.cs` calls `Launcher.LaunchUriAsync`; this is the current external-app boundary, not an in-process emulator lifecycle.
- `Dashboard/Shared/RetroArchPlatformManifest.cs` and `Dashboard/Shared/EmulatorPackageManifest.cs` are managed metadata only.
- The current manifest's package splash is `Dashboard/UWP/Assets/SplashScreen.png` (620×300). The required validation screen source is `Assets/NXE/NXE covers/NXE.png` (1920×1080), so startup validation has not yet been implemented.
- The shared storage contract exists at `Dashboard/UWP/Shared/NxeStorageContract.cs`; it must be extended or unified with the USB Setup Tool layout before adding new destinations.

## Supplied source trees

### Xenia Canary UWP

- Location: `Assets/NXE/EMU/XENIA`
- Git state recoverable from metadata: branch `nxe-retail-dashboard-pc`, commit `78f9250cf59299eb193b85d223b086461eca2271`.
- License file: `LICENSE`; license compatibility and third-party notices still require a release audit.
- Build systems: large native C++ tree with CMake/premake-generated projects and a dedicated `xenia-canary-uwp/xenia-canary-uwp.vcxproj`.
- UWP layer: `xenia-canary-uwp/App.cpp`, `XeniaUWP.cpp`, `surface_uwp.cpp`, `UWPUtil.cpp`, package manifest, x64 Release output.
- Engine pieces visible in source: PowerPC CPU/JIT, Xbox kernel emulation, GPU, audio, VFS/STFS/XEX loading, save/config support.
- Current UWP graphics/audio linkage in the project includes D3D12 and XAudio2; SDL2/XInput-related Xbox support is also present in the tree.
- Integration consequence: the UWP app shell, window/frontend loop, protocol activation, and presentation ownership must be separated from the emulator lifecycle before it can be hosted by NXE.

### Dolphin UWP

- Location/version directory: `Assets/NXE/EMU/Dolphin/dolphin-1.1.9.0`.
- No repository metadata is present in this supplied directory, so branch/commit cannot be recovered locally.
- Build systems: CMake plus `Source/dolphin-emu.sln`; dedicated `Source/DolphinWinRT/DolphinWinRT.vcxproj` and `Package.appxmanifest`.
- UWP target: the supplied README identifies Xbox Series S/X as the recommended UWP target.
- Engine pieces: PowerPC/JIT, GameCube/Wii hardware, DSP/audio, filesystem, saves and configuration.
- Graphics/input choices in the source include D3D/D3D11/D3D12, Vulkan, OpenGL/EGL and SDL-based input; the UWP project must be treated as the authoritative Xbox adaptation rather than generic desktop assumptions.
- License: `COPYING` and `Data/license.txt` are present; the fork identifies GPLv2+ licensing in its README.

### Flycast

- Location: `Assets/NXE/EMU/flycast`.
- Git state recoverable from metadata: branch `master`, commit `628bd3dbb160ea2750230fc6b00c0bb8173cb1f6`.
- Build systems: CMake/CMakePresets plus multiple platform projects; UWP manifest at `shell/uwp/package.appxManifest`.
- Libretro implementation: `shell/libretro/libretro.cpp` and related storage, audio, options, input and logging files are present.
- Engine pieces: Dreamcast/NAOMI/Atomiswave hardware, SH4/ARM7, PVR/renderers, audio and VMU/save support.
- Graphics backends in the tree include D3D9, D3D11, Vulkan, GLES and OpenGL; the first NXE integration target should use the existing libretro path as required by the brief.
- License: `LICENSE` plus dependency notices are present; the project is GPLv2.

### RetroArch / libretro reference

- Location: `Assets/NXE/EMU/RetroArch`.
- Git state recoverable from metadata: branch `master`, commit `fa88a32376bc64e3d868176476af0bbaa8e91f43`.
- Build systems: Makefiles, CMake fragments and `pkg/msvc-uwp/RetroArch-msvcUWP.sln`.
- UWP layer: `uwp/` and `libretro-common/vfs/vfs_implementation_uwp.cpp`.
- Libretro host/reference surface: core discovery/loading, environment callbacks, video/audio/input callbacks, options, save/state and system/content paths are distributed across the RetroArch core/runloop/libretro-common code.
- Integration consequence: NXE should reuse the libretro ABI/hosting concepts and Xbox storage fixes, but must not ship RetroArch's visible menu/frontend as the NXE dashboard.
- License: `COPYING` and per-component notices are present; the main project is GPLv3.

### XBSX2 / PCSX2 UWP

- Location: `Assets/NXE/EMU/XBSX2`.
- Git state recoverable from metadata: branch `master`, commit `88dbbc50bc633ec0f2fa4443cbafbac51c837082`.
- Build systems: CMake plus `PCSX2_qt.sln`; dedicated `pcsx2-uwp/pcsx2-uwp.vcxproj` and package manifest.
- UWP layer: `pcsx2-uwp/src/main.cpp`, `UWPUtils`, `UWPKeyboard`, `UWPSound`, and `WINRT_XBOX` project configuration.
- Engine pieces: PCSX2/XBSX2 virtual machine, MIPS interpreters/recompilers, GS, SPU2/audio, memory cards, configuration and BIOS loading.
- Graphics/configuration: OpenGL and Vulkan are enabled in the supplied build configuration; the UWP project links Xbox/UWP input, XAudio2 and Windows libraries.
- BIOS: the supplied README explicitly requires a legitimate PS2 BIOS; no BIOS may be bundled by NXE.
- License: `COPYING.GPLv3`; release distribution must preserve GPLv3 source/notice obligations.

## Integration blockers and order

1. Create a native C++/WinRT NXE host component and prove a managed-to-native lifecycle call on Series X.
2. Implement a minimal NXE-owned libretro host and test one legal test core/content path before Flycast.
3. Integrate Flycast through its existing libretro implementation.
4. Separate Xenia's UWP frontend from its engine and test JIT/GPU/first-frame behavior with UNO (Title ID `584107F3`).
5. Integrate XBSX2 with explicit BIOS validation, then Dolphin, then remaining libretro systems.
6. Add common game presentation, native video/audio/controller ownership, startup validation, update checking, shared storage-layout metadata and licensing notices only as each native milestone is proven.

The current URI adapters, external emulator package model, dashboard storage enumeration, FTP/management services, and USB permissions remain unchanged by this inventory. They must not be replaced wholesale or treated as evidence that an in-process backend already exists.
