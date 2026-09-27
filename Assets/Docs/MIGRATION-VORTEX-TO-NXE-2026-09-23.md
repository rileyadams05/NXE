# Vortex Prime EMU to NXE migration record

Date: 2026-09-23

## Source inventory

`D:\Projects\Vortex Prime EMU` contained these top-level directories:

- `Assets` (empty)
- `Builds` (empty)
- `Dashboard` (empty)
- `Docs` (empty)
- `Emulators` (contained only `Dolphin`)
- `Games` (empty)
- `Tools` (empty)

The Dolphin snapshot contained 6,834 files totaling 143,468,835 bytes. It was not a Git repository. Its sorted relative-path/size/SHA-256 inventory digest was:

`8E1CC03645AEB86AA93D7F2D49064286AF341121F8CBF5AE906C6CF428AAA696`

## Migration

The Dolphin directory was moved without flattening or overwriting:

- From: `D:\Projects\Vortex Prime EMU\Emulators\Dolphin`
- To: `D:\Projects\NXE\Assets\EMU\XBOX\Dolphin`

The destination contains the same 6,834 files, 143,468,835 bytes, and inventory digest. No unique file remained in the Vortex root after the move.

Existing modern emulator repositories remain under `D:\Projects\NXE\Assets\EMU\XBOX`: Xenia, XBSX2, Dolphin, PPSSPP, Flycast, and RetroArch. The Xenia repository, package builder, certificate support, branches, remotes, tracked modifications, and generated/untracked diagnostic artifacts were preserved.

## Deliberate project separation

- `D:\Projects\NXE\CustomDashboard` remains the native Xbox 360 dashboard.
- `D:\Projects\NXE\Assets\XBOX 360 SDK` remains the configured XDK/SDK source.
- Bad Update, ABadAvatar, Aurora, and Freestyle Dash remain under the Xbox 360/reference side in `D:\Projects\NXE\Dashboard`.
- Bad Update and ABadAvatar were not modified.
- UNO is no longer a production startup dependency or a modern package-builder gate.
- Modern Xenia starts without an automatic test title; explicit activation can still supply a backend title.

## Validation

- CustomDashboard XDK build: passed
- `NXEPrototype.exe` and `NXEPrototype.xex` SHA-256: `4A13A185F1B5F2AAD74631F5FD30B3FACFD75A76C879FD230F46FDB34B4AC41F`
- Modern Xenia Release x64 clean builds: passed twice
- Deterministic staging and unpacked payload comparison: passed
- AppX signing and Windows trust verification: passed
- PC launch-order test `[emulator_launch]`: passed
- Local PC AppX installation: passed
- Xbox deployment: not performed

Authoritative modern validation report:

`D:\Projects\NXE\Builds\XboxUWP\migration-unified-nxe-20260923-final4\pipeline-report.json`

## Cleanup status

The old Vortex root now contains seven empty directories, zero files, and zero reparse points. A later audit confirmed the destination still contains 6,834 files totaling 143,468,835 bytes. That audit produced `F26A1B132D59069D2F87A36AB1E7BE2C4B36C7D48D0EDF383F7155D563F5F807` using a newly reconstructed inventory serialization, which is not byte-compatible with the earlier digest method above.

Because the aggregate digest methods cannot be proven identical from the saved records, irreversible deletion was deliberately deferred. `D:\Projects\Vortex Prime EMU` remains only as an empty directory skeleton pending explicit approval; all useful contents are under NXE.
