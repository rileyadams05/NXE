[CmdletBinding()]
param(
    [string]$ConfigPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ([string]::IsNullOrWhiteSpace($ConfigPath)) {
    $ConfigPath = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'translator-config.json'
}

function Invoke-LoggedTool {
    param(
        [Parameter(Mandatory)] [string]$FilePath,
        [Parameter(Mandatory)] [string[]]$Arguments,
        [Parameter(Mandatory)] [string]$LogPath,
        [string]$WorkingDirectory,
        [switch]$AllowDuplicateArchiveEntry
    )

    $commandText = '"' + $FilePath + '" ' + (($Arguments | ForEach-Object {
        if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ }
    }) -join ' ')

    $previousLocation = Get-Location
    $previousErrorPreference = $ErrorActionPreference
    try {
        if ($WorkingDirectory) { Set-Location -LiteralPath $WorkingDirectory }
        $ErrorActionPreference = 'Continue'
        $lines = @(& $FilePath @Arguments 2>&1 | ForEach-Object { $_.ToString() })
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorPreference
        Set-Location -LiteralPath $previousLocation
    }

    @(
        "Command: $commandText"
        "ExitCode: $exitCode"
        ''
        $lines
    ) | Set-Content -LiteralPath $LogPath -Encoding utf8

    if ($exitCode -ne 0) {
        $duplicateOnly = $AllowDuplicateArchiveEntry -and
            (($lines -join "`n") -match 'already exists') -and
            (($lines -join "`n") -notmatch '(?i)corrupt|invalid|failed')
        if (-not $duplicateOnly) {
            throw "Tool failed with exit code $exitCode. See $LogPath"
        }
    }
}

function Assert-Path {
    param([string]$Path, [string]$Description)
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "$Description is missing: $Path"
    }
}

function Get-RelativeFilePath {
    param([string]$BasePath, [string]$Path)
    $baseUri = [Uri]([System.IO.Path]::GetFullPath($BasePath).TrimEnd('\') + '\')
    $pathUri = [Uri][System.IO.Path]::GetFullPath($Path)
    return [Uri]::UnescapeDataString($baseUri.MakeRelativeUri($pathUri).ToString()).Replace('/', '\')
}

$config = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
$sourceXex = [string]$config.sourceXex
$sharedResourceXzp = [string]$config.sharedResourceXzp
$outputRoot = [System.IO.Path]::GetFullPath([string]$config.outputRoot)
$buildsRoot = [System.IO.Path]::GetFullPath('D:\Projects\NXE\Dashboard')

if (-not $outputRoot.StartsWith($buildsRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to clean output outside $buildsRoot"
}

$imageXex = Join-Path ([string]$config.xboxSdkBin) 'imagexex.exe'
$xuiPkg = Join-Path ([string]$config.xboxSdkBin) 'xuipkg.exe'
$xexTool = [string]$config.xexTool
$xuiHelperProject = Join-Path ([string]$config.xuiHelperRoot) 'XUIHelper.CLI\XUIHelper.CLI.csproj'
$xuiHelperBin = Join-Path ([string]$config.xuiHelperRoot) 'XUIHelper.CLI\bin\Release\net8.0'
$xuiHelper = Join-Path $xuiHelperBin 'XUIHelper.CLI.exe'
$v5Definitions = Join-Path ([string]$config.xuiHelperRoot) 'XUIHelper.Core\Assets\Extensions\V5'

Assert-Path $sourceXex 'Retail dashboard XEX'
Assert-Path $sharedResourceXzp 'Retail shared-resource archive'
Assert-Path $xexTool 'XEXTool'
Assert-Path $imageXex 'Microsoft ImageXEX'
Assert-Path $xuiPkg 'Microsoft XUI package tool'
Assert-Path $xuiHelperProject 'XUIHelper CLI project'
Assert-Path $v5Definitions 'Retail 9199 XUI definitions'

$sourceHash = (Get-FileHash -LiteralPath $sourceXex -Algorithm SHA256).Hash
if ($sourceHash -ne [string]$config.sourceSha256) {
    throw "Retail dashboard hash mismatch. Expected $($config.sourceSha256), got $sourceHash"
}
$sharedResourceHash = (Get-FileHash -LiteralPath $sharedResourceXzp -Algorithm SHA256).Hash
if ($sharedResourceHash -ne [string]$config.sharedResourceSha256) {
    throw "Retail shared-resource hash mismatch. Expected $($config.sharedResourceSha256), got $sharedResourceHash"
}

if (Test-Path -LiteralPath $outputRoot) {
    Remove-Item -LiteralPath $outputRoot -Recurse -Force
}

$workRoot = Join-Path $outputRoot 'work'
$sectionRoot = Join-Path $outputRoot 'sections'
$extractRoot = Join-Path $outputRoot 'extracted'
$xuiRoot = Join-Path $outputRoot 'xui'
$logRoot = Join-Path $outputRoot 'logs'
$reportRoot = Join-Path $outputRoot 'reports'
@($workRoot, $sectionRoot, $extractRoot, $xuiRoot, $logRoot, $reportRoot) |
    ForEach-Object { New-Item -ItemType Directory -Force -Path $_ | Out-Null }

& dotnet build $xuiHelperProject -c Release --nologo | Set-Content -LiteralPath (Join-Path $logRoot 'xuihelper-build.log') -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'XUIHelper build failed.' }
Assert-Path $xuiHelper 'Built XUIHelper CLI'

$extensionTarget = Join-Path $xuiHelperBin 'Extensions\V5'
New-Item -ItemType Directory -Force -Path $extensionTarget | Out-Null
Copy-Item -LiteralPath (Join-Path $v5Definitions '9199SceneElements.xml'),(Join-Path $v5Definitions '9199CustomElements.xml'),(Join-Path $v5Definitions '9199DashElements.xml'),(Join-Path $v5Definitions '9199HUDElements.xml') -Destination $extensionTarget -Force

$unpackedXex = Join-Path $workRoot 'dash-unpacked.xex'
Invoke-LoggedTool -FilePath $xexTool -Arguments @('-c','u','-e','u','-o',$unpackedXex,$sourceXex) -LogPath (Join-Path $logRoot 'xextool-unpack.log')
Assert-Path $unpackedXex 'Unpacked dashboard XEX'

Invoke-LoggedTool -FilePath $imageXex -Arguments @('/DUMP',$unpackedXex) -LogPath (Join-Path $logRoot 'imagexex-dump.log')

foreach ($section in $config.sections) {
    $sectionPath = Join-Path $sectionRoot ($section + '.bin')
    Invoke-LoggedTool -FilePath $imageXex -Arguments @('/DUMP',('/SECTIONNAME:' + $section),('/SECTIONFILE:' + $sectionPath),$unpackedXex) -LogPath (Join-Path $logRoot ('section-' + $section + '.log'))
    Assert-Path $sectionPath "Dashboard section $section"

    if ($section -eq 'FFFE07D1') { continue }

    $sectionExtractRoot = Join-Path $extractRoot $section
    New-Item -ItemType Directory -Force -Path $sectionExtractRoot | Out-Null
    Invoke-LoggedTool -FilePath $xuiPkg -Arguments @('/U',$sectionPath,'/NOLOGO') -WorkingDirectory $sectionExtractRoot -LogPath (Join-Path $logRoot ('unpack-' + $section + '.log')) -AllowDuplicateArchiveEntry:($section -eq 'slots')
}

$sharedExtractRoot = Join-Path $extractRoot 'sharedres'
New-Item -ItemType Directory -Force -Path $sharedExtractRoot | Out-Null
Invoke-LoggedTool -FilePath $xuiPkg -Arguments @('/U',$sharedResourceXzp,'/NOLOGO') -WorkingDirectory $sharedExtractRoot -LogPath (Join-Path $logRoot 'unpack-sharedres.log')

$conversionFailures = [System.Collections.Generic.List[object]]::new()
$xurFiles = @(Get-ChildItem -LiteralPath $extractRoot -Recurse -File -Filter '*.xur' | Sort-Object FullName)
foreach ($xur in $xurFiles) {
    $relative = Get-RelativeFilePath -BasePath $extractRoot -Path $xur.FullName
    $xuiRelative = [System.IO.Path]::ChangeExtension($relative, '.xui')
    $xuiPath = Join-Path $xuiRoot $xuiRelative
    $xuiLog = Join-Path $logRoot ('xui-' + (($relative -replace '[\\/:*?"<>|]', '_')) + '.log')
    New-Item -ItemType Directory -Force -Path (Split-Path $xuiPath) | Out-Null
    try {
        Invoke-LoggedTool -FilePath $xuiHelper -Arguments @('conv','-s',$xur.FullName,'-f','xuiv12','-o',$xuiPath,'-g','V5','-l',$xuiLog,'-v','info') -LogPath ($xuiLog + '.runner.log')
        if (-not (Test-Path -LiteralPath $xuiPath)) {
            throw "Converter returned without producing $xuiPath"
        }
    }
    catch {
        $conversionFailures.Add([ordered]@{ source = $relative; error = $_.Exception.Message })
    }
}

$inventory = foreach ($rootName in @('sections','extracted','xui')) {
    $root = Join-Path $outputRoot $rootName
    Get-ChildItem -LiteralPath $root -Recurse -File | Sort-Object FullName | ForEach-Object {
        [ordered]@{
            path = (Get-RelativeFilePath -BasePath $outputRoot -Path $_.FullName).Replace('\','/')
            size = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    }
}

$inventory | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $reportRoot 'asset-inventory.json') -Encoding utf8
$conversionFailures | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $reportRoot 'conversion-failures.json') -Encoding utf8

$report = [ordered]@{
    generatedUtc = [DateTime]::UtcNow.ToString('o')
    dashboardBuild = [int]$config.dashboardBuild
    sourceXex = $sourceXex
    sourceSha256 = $sourceHash
    sourceSize = (Get-Item -LiteralPath $sourceXex).Length
    sharedResourceXzp = $sharedResourceXzp
    sharedResourceSha256 = $sharedResourceHash
    unpackedXexSha256 = (Get-FileHash -LiteralPath $unpackedXex -Algorithm SHA256).Hash
    sectionCount = @($config.sections).Count
    extractedFileCount = @(Get-ChildItem -LiteralPath $extractRoot -Recurse -File).Count
    xurCount = $xurFiles.Count
    convertedXuiCount = @(Get-ChildItem -LiteralPath $xuiRoot -Recurse -File -Filter '*.xui').Count
    conversionFailureCount = $conversionFailures.Count
    xuiHelperCommit = (& git -C ([string]$config.xuiHelperRoot) rev-parse HEAD).Trim()
    tools = [ordered]@{
        xexTool = $xexTool
        imageXex = $imageXex
        xuiPkg = $xuiPkg
        xuiHelper = $xuiHelper
    }
}
$report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $reportRoot 'translation-report.json') -Encoding utf8

if ($conversionFailures.Count -ne 0) {
    throw "$($conversionFailures.Count) XUR scenes failed conversion. See conversion-failures.json"
}

Write-Host "Retail NXE $($config.dashboardBuild) translation complete."
Write-Host "Converted $($xurFiles.Count) XUR scenes to XUI."
Write-Host "Output: $outputRoot"
