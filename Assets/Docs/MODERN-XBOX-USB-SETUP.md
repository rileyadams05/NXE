# Preparing an External Drive for NXE

This is the Windows-side preparation step for the modern Xbox NXE app on Xbox One and Xbox Series consoles. It does not install a second NXE utility and it does not create the NXE game folders. NXE creates and maintains its own `NXE` directory after the prepared drive is connected to the console.

## Before you begin

- Use a USB HDD or SSD intended for media storage, not Xbox-managed game storage.
- The drive must use NTFS. If it does not, copy off anything important and format it as NTFS with Windows first.
- The permission step does not format the drive and does not delete existing files.
- NXE touches only the `NXE` directory. Existing photos, movies, backups, documents, and other folders are left alone.

## Apply the Xbox/UWP permission

The downloaded `SvenGDK/XboxMediaUSB` source was reviewed for this project. Its relevant preparation behavior is a full-control allow rule for the **ALL APPLICATION PACKAGES** security identifier (`S-1-15-2-1`) on the NTFS drive root, inherited by both files and folders. The script below performs that same ACL operation with standard Windows APIs.

Open **Windows PowerShell as Administrator**, copy the complete block, and run it. It asks for the drive letter and refuses the Windows system drive, non-NTFS volumes, and disks that Windows does not identify as USB/removable.

```powershell
$DriveInput = (Read-Host "External USB drive letter (example: E)").Trim().TrimEnd(':')

if ($DriveInput -notmatch '^[D-Zd-z]$') {
    throw "Enter one external drive letter from D through Z. The system drive is not allowed."
}

$DriveLetter = $DriveInput.ToUpperInvariant()
$Drive = "${DriveLetter}:"
$Root = "${DriveLetter}:\"

if ($DriveLetter -eq 'C') {
    throw "Refusing to modify the Windows system drive."
}

$Volume = Get-Volume -DriveLetter $DriveLetter -ErrorAction Stop
if ($Volume.FileSystem -ne 'NTFS') {
    throw "${Drive} uses $($Volume.FileSystem). NXE external storage must be NTFS."
}

$Partition = Get-Partition -DriveLetter $DriveLetter -ErrorAction Stop
$Disk = $Partition | Get-Disk -ErrorAction Stop
$IsExternal = ($Disk.BusType -eq 'USB') -or ($Volume.DriveType -eq 'Removable')
if (-not $IsExternal) {
    throw "${Drive} is not reported as a USB/removable disk. No permissions were changed."
}

Write-Host "Target drive: $Drive"
Write-Host "Volume label: $($Volume.FileSystemLabel)"
Write-Host "Filesystem: $($Volume.FileSystem)"
Write-Host "Disk: $($Disk.FriendlyName)"
Write-Host "Bus type: $($Disk.BusType)"
Write-Host "This adds ALL APPLICATION PACKAGES full control with file/folder inheritance."

$Confirmation = (Read-Host "Type $DriveLetter to confirm this exact drive").Trim().ToUpperInvariant()
if ($Confirmation -ne $DriveLetter) {
    throw "Confirmation did not match. No permissions were changed."
}

$Sid = New-Object System.Security.Principal.SecurityIdentifier('S-1-15-2-1')
$Acl = Get-Acl -LiteralPath $Root
$Rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
    $Sid,
    [System.Security.AccessControl.FileSystemRights]::FullControl,
    ([System.Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
     [System.Security.AccessControl.InheritanceFlags]::ObjectInherit),
    [System.Security.AccessControl.PropagationFlags]::None,
    [System.Security.AccessControl.AccessControlType]::Allow
)
$Acl.SetAccessRule($Rule)
Set-Acl -LiteralPath $Root -AclObject $Acl

$Verified = (Get-Acl -LiteralPath $Root).Access | Where-Object {
    try {
        $RuleSid = $_.IdentityReference.Translate(
            [System.Security.Principal.SecurityIdentifier]).Value
    } catch {
        $RuleSid = $_.IdentityReference.Value
    }
    $RuleSid -eq 'S-1-15-2-1' -and
    ($_.FileSystemRights -band [System.Security.AccessControl.FileSystemRights]::FullControl) -and
    ($_.InheritanceFlags -band [System.Security.AccessControl.InheritanceFlags]::ContainerInherit) -and
    ($_.InheritanceFlags -band [System.Security.AccessControl.InheritanceFlags]::ObjectInherit) -and
    $_.AccessControlType -eq [System.Security.AccessControl.AccessControlType]::Allow
}

if (-not $Verified) {
    throw "The permission could not be verified. Do not move the drive to Xbox yet."
}

Write-Host "SUCCESS: $Drive is NTFS and the Xbox/UWP permission is present." -ForegroundColor Green
Write-Host "Safely eject it, connect it to Xbox as media storage, and start NXE."
```

Windows may display the normal administrator/UAC confirmation when PowerShell is opened as Administrator. Do not disable UAC or Windows security.

## What NXE does on Xbox

When NXE sees a compatible writable drive, it creates only this owned structure:

```text
NXE\
├── Games\
│   ├── Xbox360\
│   ├── PS2\
│   ├── GameCube\
│   ├── Wii\
│   ├── PSP\
│   ├── Dreamcast\
│   ├── PS1\
│   ├── NES\
│   ├── SNES\
│   ├── N64\
│   ├── GameBoy\
│   ├── GBA\
│   ├── Genesis\
│   └── Arcade\
├── EmulatorData\
│   ├── Xenia\
│   ├── XBSX2\
│   ├── Dolphin\
│   ├── PPSSPP\
│   ├── Flycast\
│   └── RetroArch\
├── BIOS\
│   ├── PS2\
│   ├── PSP\
│   ├── Dreamcast\
│   └── Retro\
├── Covers\
├── Metadata\
├── Saves\
├── Config\
├── Cache\
└── Transfers\
```

Games are indexed automatically from those known folders. Folder location selects the backend: Xbox 360 uses Xenia; PS2 uses XBSX2; GameCube/Wii use Dolphin; PSP uses PPSSPP; Dreamcast uses Flycast; PS1 and the retro folders use RetroArch. There is no Add Path screen and no NXE storage quota.

Multiple prepared drives can be connected at once. NXE exposes them to FTP as `/usb0`, `/usb1`, and so on and merges their games into one library. Removing a drive makes its games unavailable without deleting cached covers or metadata; reconnecting it causes an automatic rescan.

## FTP transfers

Start FTP from **System Settings > FTP** in NXE. Port `2121` is the default. Blank username and password fields mean authentication is disabled, which is convenient on a trusted LAN but allows any device on that LAN to connect while the server is running.

Upload games directly to the platform folder, for example:

```text
/usb0/Games/PS2/Game.iso
/usb1/Games/Xbox360/Game.iso
```

NXE streams large transfers through a temporary `.part` file. Only a successfully completed upload receives its final filename and enters the game library. Upload, download, list, rename/move files, create directories, delete files, and remove empty directories are restricted to NXE's approved virtual roots.
