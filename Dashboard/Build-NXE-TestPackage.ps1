[CmdletBinding()]
param(
    [string]$Version = '0.2.0.79',
    [string]$Configuration = 'Release',
    [string]$Platform = 'x64',
    [string]$RetroArchAllCoresAppx = ''
)

$ErrorActionPreference = 'Stop'
$dashboardRoot = $PSScriptRoot
$repoRoot = Split-Path -Parent $dashboardRoot
$uwpProject = Join-Path $dashboardRoot 'UWP\NxeDashboard.Uwp.csproj'
$nativeProject = Join-Path $dashboardRoot 'NXE.Emulation.Native\NXE.Emulation.Native.vcxproj'
$releaseRoot = Join-Path $repoRoot 'Release'
$workRoot = Join-Path $repoRoot 'Builds\NxeReproduciblePackage'
$stageRoot = Join-Path $workRoot 'Stage'
$dashboardAppx = Join-Path $workRoot 'Dashboard.appx'
$outputAppx = Join-Path $releaseRoot ("NXE Dashboard {0}.appx" -f $Version)
$retroArchCoreStage = Join-Path $workRoot 'RetroArchAllCores'

function Resolve-Tool([string]$name) {
    $command = Get-Command $name -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    if ($name -ieq 'MSBuild.exe') {
        $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
        if (Test-Path $vswhere) {
            $vs = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
            if ($vs) {
                $candidate = Join-Path $vs 'MSBuild\Current\Bin\MSBuild.exe'
                if (Test-Path $candidate) { return $candidate }
            }
        }
    }
    $roots = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin",
        "${env:ProgramFiles}\Windows Kits\10\bin"
    )
    foreach ($root in $roots) {
        if (Test-Path $root) {
            $candidate = Get-ChildItem -LiteralPath $root -Filter $name -Recurse -File -ErrorAction SilentlyContinue |
                Sort-Object FullName -Descending | Select-Object -First 1
            if ($candidate) { return $candidate.FullName }
        }
    }
    throw "Required tool '$name' was not found."
}

function Require-File([string]$path, [string]$description) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Missing ${description}: $path"
    }
}

New-Item -ItemType Directory -Force -Path $workRoot,$releaseRoot | Out-Null
if (Test-Path -LiteralPath $stageRoot) { Remove-Item -LiteralPath $stageRoot -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stageRoot | Out-Null

$msbuild = Resolve-Tool 'MSBuild.exe'
$makeappx = Resolve-Tool 'makeappx.exe'
$signtool = Resolve-Tool 'signtool.exe'

# Keep the source manifest and the emitted package version in lockstep for every build.
$sourceManifestPath = Join-Path $dashboardRoot 'UWP\Package.appxmanifest'
[xml]$sourceManifest = Get-Content -LiteralPath $sourceManifestPath -Raw
$sourceIdentity = $sourceManifest.SelectSingleNode("/*[local-name()='Package']/*[local-name()='Identity']")
if (-not $sourceIdentity) { throw 'Source AppxManifest.xml has no Identity element.' }
$sourceIdentity.SetAttribute('Version', $Version)
$sourceManifest.Save($sourceManifestPath)

& $msbuild $nativeProject /t:Rebuild /p:Configuration=$Configuration /p:Platform=$Platform /m
if ($LASTEXITCODE -ne 0) { throw 'NXE native build failed.' }
& $msbuild $uwpProject /t:Rebuild /p:Configuration=$Configuration /p:Platform=$Platform /p:AppxPackageSigningEnabled=false /m
if ($LASTEXITCODE -ne 0) { throw 'NXE dashboard build failed.' }

$dashboardPackage = Get-ChildItem -LiteralPath (Join-Path $dashboardRoot 'UWP\AppPackages') -Filter '*.appx' -Recurse -File |
    Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
if (-not $dashboardPackage) { throw 'Dashboard build did not produce an AppX.' }
Copy-Item -LiteralPath $dashboardPackage.FullName -Destination $dashboardAppx -Force
& $makeappx unpack /p $dashboardAppx /d $stageRoot /o
if ($LASTEXITCODE -ne 0) { throw 'Could not unpack dashboard AppX.' }

$native = Join-Path $dashboardRoot 'NXE.Emulation.Native\x64\Release\NXE.Emulation.Native.dll'
$flycast = Join-Path $repoRoot 'Assets\NXE\EMU\flycast\build-nxe-libretro-uwp\Release\flycast_libretro.dll'
$dolphin = Join-Path $repoRoot 'Builds\SternXD-Dolphin\Build\x64\Release\DolphinWinRT\bin\DolphinWinRT.exe'
$xbsx2 = Join-Path $repoRoot 'Assets\NXE\EMU\XBSX2\bin\pcsx2-uwpx64.exe'
$retroarch = Join-Path $repoRoot 'Assets\NXE\EMU\RetroArch\pkg\msvc-uwp\x64\Release\RetroArch-msvcUWP\RetroArch-msvcUWP.exe'
$requiredRetroArchCores = @(
    @('stella_libretro.dll'),
    @('a5200_libretro.dll'),
    @('prosystem_libretro.dll'),
    @('virtualjaguar_libretro.dll'),
    @('handy_libretro.dll'),
    @('mesen_libretro.dll','fceumm_libretro.dll'),
    @('snes9x_libretro.dll'),
    @('mupen64plus_next_libretro.dll','parallel_n64_libretro.dll'),
    @('gambatte_libretro.dll','mgba_libretro.dll'),
    @('mgba_libretro.dll'),
    @('melonds_libretro.dll','desmume_libretro.dll'),
    @('mednafen_vb_libretro.dll'),
    @('genesis_plus_gx_libretro.dll','picodrive_libretro.dll'),
    @('mednafen_saturn_libretro.dll','yabause_libretro.dll'),
    @('pcsx_rearmed_libretro.dll','beetle_psx_hw_libretro.dll'),
    @('ppsspp_libretro.dll'),
    @('mednafen_pce_fast_libretro.dll'),
    @('mednafen_supergrafx_libretro.dll'),
    @('mednafen_pcfx_libretro.dll'),
    @('fbneo_libretro.dll'),
    @('mednafen_ngp_libretro.dll'),
    @('mednafen_wswan_libretro.dll'),
    @('opera_libretro.dll'),
    @('gearcoleco_libretro.dll'),
    @('freeintv_libretro.dll'),
    @('vecx_libretro.dll'),
    @('o2em_libretro.dll'),
    @('freechaf_libretro.dll'),
    @('pokemini_libretro.dll'),
    @('mame2003_plus_libretro.dll')
)
Require-File $native 'native engine bridge'
Require-File $flycast 'Flycast core'
Require-File $dolphin 'Dolphin UWP engine'
Require-File $xbsx2 'XBSX2 UWP engine'
Require-File $retroarch 'RetroArch UWP engine'

if ([string]::IsNullOrWhiteSpace($RetroArchAllCoresAppx)) {
    $RetroArchAllCoresAppx = Join-Path $repoRoot 'Builds\RetroArch-SeriesConsoles-AllCores.appx'
}
Require-File $RetroArchAllCoresAppx 'RetroArch Series All Cores AppX'
if (-not (Test-Path -LiteralPath $retroArchCoreStage)) {
    New-Item -ItemType Directory -Force -Path $retroArchCoreStage | Out-Null
    & $makeappx unpack /p $RetroArchAllCoresAppx /d $retroArchCoreStage /o | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not unpack RetroArch Series All Cores AppX.' }
}
$retroArchCoreSource = Join-Path $retroArchCoreStage 'cores'
if (-not (Test-Path -LiteralPath $retroArchCoreSource -PathType Container)) {
    throw "RetroArch extracted core directory is missing: $retroArchCoreSource"
}
$selectedRetroArchCores = New-Object System.Collections.Generic.List[string]
foreach ($candidates in $requiredRetroArchCores) {
    $selected = $null
    foreach ($candidate in $candidates) {
        $candidatePath = Join-Path $retroArchCoreSource $candidate
        if (Test-Path -LiteralPath $candidatePath -PathType Leaf) { $selected = $candidatePath; break }
    }
    if (-not $selected) {
        throw ('Required RetroArch core missing: ' + ($candidates -join ' or '))
    }
    $selectedRetroArchCores.Add($selected)
}

Copy-Item $native (Join-Path $stageRoot 'NXE.Emulation.Native.dll') -Force
New-Item -ItemType Directory -Force -Path (Join-Path $stageRoot 'Cores'),(Join-Path $stageRoot 'Engines\Dolphin'),(Join-Path $stageRoot 'Engines\XBSX2'),(Join-Path $stageRoot 'Engines\RetroArch\cores') | Out-Null
Copy-Item $flycast (Join-Path $stageRoot 'Cores\flycast_libretro.dll') -Force
Copy-Item $flycast (Join-Path $stageRoot 'Engines\RetroArch\cores\flycast_libretro.dll') -Force
$dolphinRoot = Split-Path -Parent $dolphin
$xbsx2Root = Split-Path -Parent $xbsx2
$retroarchRoot = Split-Path -Parent $retroarch
# Engine executables rely on adjacent resources/DLLs; package the complete built payload trees.
Copy-Item (Join-Path $dolphinRoot '*') (Join-Path $stageRoot 'Engines\Dolphin') -Recurse -Force
Copy-Item (Join-Path $xbsx2Root '*') (Join-Path $stageRoot 'Engines\XBSX2') -Recurse -Force
Copy-Item (Join-Path $retroarchRoot '*') (Join-Path $stageRoot 'Engines\RetroArch') -Recurse -Force
# Copy the verified Xbox Series core set into the canonical NXE location.
foreach ($corePath in $selectedRetroArchCores) {
    Copy-Item $corePath (Join-Path $stageRoot ('Engines\RetroArch\cores\' + [IO.Path]::GetFileName($corePath))) -Force
}
# Keep the NXE-tested Flycast build in the canonical location.
Copy-Item $flycast (Join-Path $stageRoot 'Engines\RetroArch\cores\flycast_libretro.dll') -Force

$manifestPath = Join-Path $stageRoot 'AppxManifest.xml'
[xml]$manifest = Get-Content -LiteralPath $manifestPath -Raw
$ns = New-Object System.Xml.XmlNamespaceManager($manifest.NameTable)
$ns.AddNamespace('f','http://schemas.microsoft.com/appx/manifest/foundation/windows10')
$ns.AddNamespace('uap','http://schemas.microsoft.com/appx/manifest/uap/windows10')
$applications = $manifest.SelectSingleNode("/*[local-name()='Package']/*[local-name()='Applications']")
if (-not $applications) { throw 'Staged AppxManifest.xml has no Applications element.' }
foreach ($app in @(
    @{ Id='DolphinEngine'; Executable='Engines\Dolphin\DolphinWinRT.exe'; EntryPoint='DolphinUWP.App'; Display='Dolphin UWP Engine' },
    @{ Id='Xbsx2Engine'; Executable='Engines\XBSX2\pcsx2-uwpx64.exe'; EntryPoint='pcsx2_uwp.App'; Display='XBSX2 Engine' },
    @{ Id='RetroArchEngine'; Executable='Engines\RetroArch\RetroArch-msvcUWP.exe'; EntryPoint='RetroArch.App'; Display='RetroArch Engine' }
)) {
    $node = $manifest.CreateElement('Application','http://schemas.microsoft.com/appx/manifest/foundation/windows10')
    $node.SetAttribute('Id',$app.Id); $node.SetAttribute('Executable',$app.Executable); $node.SetAttribute('EntryPoint',$app.EntryPoint)
    $visual = $manifest.CreateElement('VisualElements','http://schemas.microsoft.com/appx/manifest/uap/windows10')
    $visual.SetAttribute('DisplayName',$app.Display); $visual.SetAttribute('Description','NXE embedded engine')
    $visual.SetAttribute('Square150x150Logo','Assets\Square150x150Logo.png'); $visual.SetAttribute('Square44x44Logo','Assets\Square44x44Logo.png'); $visual.SetAttribute('BackgroundColor','#000000')
    $node.AppendChild($visual) | Out-Null; $applications.AppendChild($node) | Out-Null
}
$manifest.Save($manifestPath)

Remove-Item (Join-Path $stageRoot 'AppxBlockMap.xml'),(Join-Path $stageRoot 'AppxSignature.p7x'),(Join-Path $stageRoot 'AppxMetadata') -Recurse -Force -ErrorAction SilentlyContinue
if (Test-Path -LiteralPath $outputAppx) { Remove-Item -LiteralPath $outputAppx -Force }
& $makeappx pack /d $stageRoot /p $outputAppx /o
if ($LASTEXITCODE -ne 0) { throw 'makeappx failed.' }
$pfx = Join-Path $dashboardRoot 'UWP\SirMangler-modern.pfx'
Require-File $pfx 'development signing certificate'
& $signtool sign /fd SHA256 /a /f $pfx /p '' $outputAppx
if ($LASTEXITCODE -ne 0) { throw 'AppX signing failed.' }
& $signtool verify /pa /all $outputAppx
if ($LASTEXITCODE -ne 0) { throw 'AppX signature verification failed.' }

$hash = (Get-FileHash -LiteralPath $outputAppx -Algorithm SHA256).Hash
$item = Get-Item -LiteralPath $outputAppx
Write-Output ("FINAL_APPX={0}`nSIZE={1}`nSHA256={2}`nUTC={3}" -f $outputAppx,$item.Length,$hash,$item.LastWriteTimeUtc)
