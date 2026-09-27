# NXE Dashboard

NXE is a controller-first Windows/Xbox Developer Mode dashboard for Xbox Series X|S. It presents one horizontal game library, the NXE Guide and a console-hosted FTP/file-management surface. The Series X is the reference target; physical emulator results remain pending until the current signed AppX is installed and tested on hardware.

## Current architecture

The current test architecture is deliberately hybrid:

- Xenia Canary is linked into `NXE.Emulation.Native.dll`.
- Dolphin UWP, XBSX2 UWP and RetroArch UWP are co-packaged engine applications.
- Flycast is packaged as a libretro core for RetroArch.
- The dashboard routes games by platform and persists a structured engine launch record before activating a co-packaged engine.

The dashboard does not use the old URI launch path for normal game launch. No standalone PPSSPP application is included; PSP is routed to RetroArch when a verified PPSSPP core is present.

## Storage layout

The USB Setup Tool prepares an NTFS drive non-destructively. The top-level contract is `Games`, `BIOS`, `Covers`, `Metadata`, `Saves`, `Config`, `Cache`, `EmulatorData`, `Transfers` and `NXE Themes`. Games are scanned through `KnownFolders.RemovableDevices` and real `StorageFolder`/`StorageFile` objects; the dashboard never assumes a Windows drive letter.

## FTP and Vortex Prime

NXE exposes the console FTP service and the local management service. Connect manually with the console IP shown by NXE, the configured FTP port, and optional credentials. The supported controls are Turn On, Turn Off, Restart, Reconnect, Change Connection Details and Forget Console. There is no QR pairing workflow.

## Build

Use a Visual Studio Developer PowerShell with the Windows SDK, MSBuild, MakeAppx and SignTool available:

```powershell
& .\Dashboard\Build-NXE-TestPackage.ps1 -Version 0.2.0.79
```

The script builds the dashboard and native bridge, verifies the required engine payloads, creates a clean staging directory, generates the multi-application manifest, packs and signs the AppX, verifies the signature and prints the SHA-256. The development certificate is local-only and must never be committed.

## Sideload/test

Install the resulting AppX in Xbox Developer Mode with the normal Device Portal package manager. The startup gate checks package configuration, controller assets, storage visibility, engine entries, core files and service readiness before revealing the dashboard. Hardware emulator boot, controller input inside each engine and return-to-dashboard behavior must still be tested on the reference Series X.

## Supported routing

Xbox 360 → Xenia; PS2 → XBSX2; GameCube/Wii → Dolphin; Dreamcast/NAOMI → Flycast; PSP, PS1 and classic systems → RetroArch only when the corresponding packaged core exists. Unsupported systems must remain unavailable rather than being advertised as runnable.

## Source layout

- `Dashboard/UWP` — dashboard, navigation, scanner, FTP and management runtime
- `Dashboard/NXE.Emulation.Native` — native Xenia/Flycast bridge
- `Dashboard/NXE USB Setup Tool` — NTFS preparation and emulator package discovery
- `Dashboard/Shared` — storage, emulator and RetroArch contracts
- `Assets/NXE/EMU` — pinned upstream source/build inputs and engine outputs
- `Dashboard/Build-NXE-TestPackage.ps1` — reproducible test-package assembly
- `THIRD_PARTY.md` — upstream projects and licenses

Generated output (`bin`, `obj`, AppPackages, CMake trees, temporary staging and release binaries) is ignored. Do not commit PFX files, credentials, tokens or Device Portal secrets.
