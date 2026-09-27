# NXE unified project architecture

NXE is one dashboard family with two platform implementations. Both implementations share the Aurora-inspired library identity, interaction rules, metadata concepts, and asset direction. They do not share a binary or platform backend.

## Xbox 360 implementation

- Location: `D:\Projects\NXE\CustomDashboard`
- Runtime: PowerPC, Xbox 360 XDK, native XEX
- Entry-point research: `D:\Projects\NXE\Dashboard\Xbox360BadUpdate` and `D:\Projects\NXE\Dashboard\ABadAvatar`
- References: Aurora and Freestyle Dash under `D:\Projects\NXE\Dashboard`
- Flow: Xbox 360 entry point -> NXE Xbox 360 dashboard -> native games/apps

Bad Update and ABadAvatar remain architecturally separate entry-point projects. They are not rewritten into the dashboard and were not changed by the modern-Xbox migration.

## Modern Xbox implementation

- Targets: Xbox One, One S, One X, Series S, Series X
- Runtime: x64 UWP in Dev Mode
- Backend sources: `D:\Projects\NXE\Assets\EMU\XBOX`
- Xenia backend: `D:\Projects\NXE\Assets\EMU\XBOX\XENIA`
- Package builder: `D:\Projects\NXE\Assets\EMU\XBOX\XENIA\tools\NXEPackageBuilder`
- Build output and reports: `D:\Projects\NXE\Builds\XboxUWP`
- Flow: launch NXE -> dashboard appears -> browse unified library -> backend is selected -> title launches

The modern app no longer selects or launches UNO during startup. Xenia remains a hidden Xbox 360 backend and may receive an explicit title through its supported activation path. The dashboard, not an emulator frontend or a test game, owns production startup.

Modern storage must use app-owned `StorageFolder` roots and `KnownFolders.RemovableDevices`; it must not be designed around arbitrary drive letters or `broadFileSystemAccess`.

## Shared design contract

The shared layer is a source/design contract rather than a cross-platform executable library until compatible implementations exist. It owns:

- Aurora-like cover library behavior and controller navigation
- typography, spacing, focus animation, and metadata presentation
- game, platform, backend, favorite, compatibility, and history schemas
- cover-art and common asset concepts
- backend-neutral launch and compatibility interfaces

Xbox 360 and modern Xbox implement this contract with their own rendering, storage, networking, and launch APIs.

## Modern service ownership

- Shared storage contract: `D:\Projects\NXE\Dashboard\Shared`
- Modern dashboard application and translated retail NXE frontend: `D:\Projects\NXE\Dashboard\Dashboard`
- Persistent normalized SQLite library and six importer families: `D:\Projects\NXE\Dashboard\Library`
- StorageFolder discovery, automatic NXE layout and incremental crawler: `D:\Projects\NXE\Dashboard\Dashboard\NxeDashboard.Uwp\Runtime`
- UWP FTP/transfer service and virtual-root isolation: `D:\Projects\NXE\Dashboard\Dashboard\NxeDashboard.Uwp\Runtime`
- IEmulatorBackend, backend manager and launch abstraction: `D:\Projects\NXE\Dashboard\Emulators`
- Modern package orchestration: `D:\Projects\NXE\Dashboard\Packaging`

Optical-device monitoring remains a later modern-Xbox feature. It is intentionally not part of the unified game-library milestone.

These are architectural destinations for later work. They are not claimed as implemented by this migration.
