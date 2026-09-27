# NXE USB Setup Tool

A specialized Windows utility designed for the NXE Dashboard that automatically creates the complete storage directory structure required by NXE on an NTFS external drive, and recursively applies the required Xbox UWP access permissions (`ALL APPLICATION PACKAGES` / `S-1-15-2-1`).

## What this Tool Does

1. **Strict NTFS Validation:** Only detects and lists compatible external NTFS-formatted drives.
2. **Automated Directory Setup:** Creates all necessary root and emulator-specific folders:
   - `Games` (Dolphin, Flycast, RetroArch platform folders, XBSX2, Xenia)
   - `BIOS` (Dolphin, Flycast, RetroArch, XBSX2)
   - `Saves` (Dolphin, Flycast, RetroArch, XBSX2, Xenia)
   - `NXE Themes`
   - `Covers` (Dolphin, Flycast, RetroArch, XBSX2, Xenia)
   - `EmulatorData` (Dolphin, Flycast, RetroArch, XBSX2, Xenia)
   - `Metadata`, `Config`, `Cache`, `Transfers`
   - Virtual root folders under `NXE/`
3. **Non-Destructive:** Preserves all existing game files, saves, covers, and folders without overwriting or deleting any data.
4. **Xbox UWP Permissions:** Recursively applies `ALL APPLICATION PACKAGES` (`S-1-15-2-1`) FullControl permissions with container and object inheritance.
5. **Emulator Package Discovery:** Searches fast user locations first, then mounted fixed drives asynchronously for supported APPX/MSIX packages. Packages are accepted by manifest identity, x64 architecture, publisher, version, protocol, and signature—not by filename alone—and valid local copies are staged silently in the existing `Transfers` root.
6. **Optional Downloads:** When the complete supported set is not available locally, asks once whether to download only the missing Xbox Series X|S packages. Downloads use current approved upstream release metadata, validate in a temporary staging directory, and only replace the destination after validation succeeds.
7. **Verification:** Confirms that all directories and security descriptors exist before reporting completion.

## Supported Current Emulators
- **Dolphin** (GameCube / Wii)
- **Flycast** (Dreamcast)
- **RetroArch** (PSP through the PPSSPP core, PS1, classic consoles, handhelds, and arcade)
- **XBSX2** (PS2)
- **Xenia Canary UWP** (Xbox 360; NXE-approved 1.1.5.2 artifact)

Standalone PPSSPP is intentionally excluded. PSP uses RetroArch with the PPSSPP core.

*(Note: RPCS3 / PS3 is not currently included as it is not an integrated emulator in this NXE release).*

## Usage
1. Connect an NTFS-formatted external USB drive to your PC.
2. Run `NXE USB Setup Tool.exe` (as Administrator).
3. Select your drive from the dropdown.
4. Click **Prepare Drive for NXE**.
5. Wait for the success confirmation.
6. Connect the drive to your Xbox and select **Use for Media** when prompted.
