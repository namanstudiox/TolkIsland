# Builds the shipping artifact: ONE self-contained TolkIsland.exe.
#
#   powershell -ExecutionPolicy Bypass -File tools\publish.ps1
#   powershell -ExecutionPolicy Bypass -File tools\publish.ps1 -Version 1.0.1 -PfxPath .\cert.pfx -PfxPassword **** -DownloadUrl https://example.com/TolkIsland.exe
#
# Switches
#   -Version         semver to stamp (default: the <Version> in the csproj)
#   -OutputDirectory folder for the artifact (default: dist)
#   -ReadyToRun      crossgen for faster cold start (~40 MB larger)
#   -NoCompression   skip single-file compression (faster start, larger file)
#   -Portable        also produce a zip next to the exe
#   -DownloadUrl     when set, writes update.json for Tolk Island's update check
#   -PfxPath/-PfxPassword  sign with signtool (needs the Windows SDK)

[CmdletBinding()]
param(
    [string]$Version = '',
    [string]$OutputDirectory = 'dist',
    [switch]$ReadyToRun,
    [switch]$NoCompression,
    [switch]$Portable,
    [string]$DownloadUrl = '',
    [string]$PfxPath = '',
    [string]$PfxPassword = '',
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'TolkIsland.csproj'
$output = Join-Path $root $OutputDirectory

if (-not (Test-Path $project)) {
    throw "Project not found: $project"
}

# The script deletes the output folder before publishing. Refuse anything that
# resolves to the repository itself, one of its parents, or a drive root.
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    throw 'OutputDirectory must name a folder.'
}

$outputFull = [System.IO.Path]::GetFullPath($output).TrimEnd('\', '/')
$rootFull = [System.IO.Path]::GetFullPath($root).TrimEnd('\', '/')
$separator = [System.IO.Path]::DirectorySeparatorChar

if ($outputFull.Equals($rootFull, [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputDirectory must not be the repository root ($rootFull)."
}

if ($rootFull.StartsWith($outputFull + $separator, [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputDirectory must not be a parent of the repository ($outputFull)."
}

if ([string]::IsNullOrEmpty([System.IO.Path]::GetDirectoryName($outputFull))) {
    throw "OutputDirectory must not be a drive root ($outputFull)."
}

$output = $outputFull

# ---- version -----------------------------------------------------------------
if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$xml = Get-Content $project
    $Version = @($xml.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]
}

$parsed = $null
if (-not [System.Version]::TryParse($Version, [ref]$parsed)) {
    throw "Version must be numeric, e.g. 1.2.3 (got '$Version')."
}

$fileVersion = if ($parsed.Revision -ge 0) { "$($parsed.Major).$($parsed.Minor).$($parsed.Build).$($parsed.Revision)" }
    elseif ($parsed.Build -ge 0) { "$($parsed.Major).$($parsed.Minor).$($parsed.Build).0" }
    else { "$Version.0" }

Write-Host "Tolk Island $Version  (file version $fileVersion)" -ForegroundColor Cyan

# ---- clean -------------------------------------------------------------------
if (Test-Path $output) {
    Remove-Item $output -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $output | Out-Null

# ---- publish -----------------------------------------------------------------
$compression = if ($NoCompression) { 'false' } else { 'true' }
$crossgen = if ($ReadyToRun) { 'true' } else { 'false' }

$publishArgs = @(
    'publish', $project,
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', 'true',
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    "-p:EnableCompressionInSingleFile=$compression",
    "-p:PublishReadyToRun=$crossgen",
    '-p:DebugType=embedded',
    "-p:Version=$Version",
    "-p:FileVersion=$fileVersion",
    "-p:AssemblyVersion=$fileVersion",
    '-o', $output,
    '-v', 'minimal',
    '--nologo'
)

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

# ---- verify it really is one file -------------------------------------------
$exe = Join-Path $output 'TolkIsland.exe'
if (-not (Test-Path $exe)) {
    throw 'TolkIsland.exe was not produced.'
}

$dlls = @(Get-ChildItem $output -Filter *.dll -Recurse -ErrorAction SilentlyContinue)
if ($dlls.Count -gt 0) {
    throw ("Single-file publish left $($dlls.Count) DLL(s) behind: " + (($dlls | Select-Object -ExpandProperty Name) -join ', '))
}

$exes = @(Get-ChildItem $output -Filter *.exe -Recurse)
if ($exes.Count -ne 1) {
    throw "Expected exactly one exe in $OutputDirectory, found $($exes.Count)."
}

# ---- optional signing --------------------------------------------------------
function Find-SignTool {
    $command = Get-Command 'signtool.exe' -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (Test-Path $kits) {
        foreach ($kit in (Get-ChildItem $kits -Directory | Sort-Object Name -Descending)) {
            $candidate = Join-Path $kit.FullName 'x64\signtool.exe'
            if (Test-Path $candidate) { return $candidate }
        }
    }

    return $null
}

$signed = $false
if (-not [string]::IsNullOrWhiteSpace($PfxPath)) {
    if (-not (Test-Path $PfxPath)) {
        throw "Signing certificate not found: $PfxPath"
    }

    $signtool = Find-SignTool
    if (-not $signtool) {
        throw 'signtool.exe not found. Install the Windows SDK or publish without -PfxPath.'
    }

    Write-Host 'Signing...' -ForegroundColor Cyan
    & $signtool sign /fd SHA256 /f $PfxPath /p $PfxPassword /tr $TimestampUrl /td SHA256 $exe
    if ($LASTEXITCODE -ne 0) {
        throw "signtool sign failed with exit code $LASTEXITCODE."
    }

    & $signtool verify /pa $exe
    if ($LASTEXITCODE -ne 0) {
        throw 'Signature verification failed.'
    }

    $signed = $true
}

# ---- hashes, manifest, portable zip -----------------------------------------
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash
$size = (Get-Item $exe).Length
$megabytes = [Math]::Round($size / 1MB, 1)

if (-not [string]::IsNullOrWhiteSpace($DownloadUrl)) {
    $manifest = [ordered]@{
        version     = $Version
        url         = $DownloadUrl
        notes       = "Tolk Island $Version"
        sha256      = $hash
        bytes       = $size
        signed      = $signed
        publishedAt = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    }

    # Windows PowerShell's Set-Content -Encoding UTF8 emits a BOM, which makes
    # the manifest invalid JSON for strict parsers. Write plain UTF-8 instead.
    $manifestJson = $manifest | ConvertTo-Json -Depth 4
    [System.IO.File]::WriteAllText(
        (Join-Path $output 'update.json'),
        $manifestJson,
        (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "Wrote $OutputDirectory\update.json for the in-app update check." -ForegroundColor Cyan
}

if ($Portable) {
    $zip = Join-Path $output "TolkIsland-$Version-win-x64.zip"
    Compress-Archive -Path $exe -DestinationPath $zip -Force
    Write-Host "Wrote $zip" -ForegroundColor Cyan
}

Write-Host ''
Write-Host "  $exe" -ForegroundColor Green
Write-Host "  $megabytes MB · sha256 $hash · signed=$signed"
Write-Host ''
Write-Host 'Ship this single file. Settings, notes, shelf paths and crash.log live in %LOCALAPPDATA%\TolkIsland.' -ForegroundColor DarkGray

