<#
.SYNOPSIS
  Downloads and prepares conversion engines for development and for the installer (which bundles rhwp and 7-Zip
  only; the app downloads the large engines on demand, see src/Filee.Engines/Infrastructure/EngineInstaller.cs).

.DESCRIPTION
  Produces this layout (next to Filee.exe in a published build, or in the repo root for development):

    engines/
      libreoffice/   LibreOffice (MPL-2.0), extracted from the official MSI via an administrative install
                     + H2Orestart (GPL-3.0) unpacked into share/extensions for HWP/HWPX import
      jre/           Eclipse Temurin JRE 21 (GPLv2 + Classpath Exception), used by H2Orestart
      rhwp/          rhwp command line (MIT), HWP/HWPX -> PDF
      pandoc/        Pandoc (GPL-2.0-or-later, separate program), Markdown/HTML and MD/HTML/ODT/RTF -> HWPX
      7zip/          7-Zip console (7z.exe + 7z.dll, LGPL-2.1 + unRAR restriction, separate program), archives
      ghostscript/   Ghostscript (AGPL-3.0, separate program) from conda-forge, EPS/PS <-> PDF, with the Microsoft
                     C++ runtime DLLs (vcruntime/) copied next to gswin64c.exe
      ffmpeg/        FFmpeg (GPL-3.0 build, separate program), video and audio; bin/ffmpeg.exe + bin/ffprobe.exe
      calibre/       calibre (GPL-3.0, separate program), extracted from the official MSI via an administrative
                     install: ebook-convert for LIT, LRF, PDB, ... and MOBI / AZW3 output

  Every download is pinned to a version and verified with SHA-256 (src/Filee.Engines/Infrastructure/engines.json,
  shared with the app). Downloads are cached in build/.cache.

.PARAMETER Destination
  Target "engines" folder. Default: <repo>/engines (picked up automatically by debug builds).

.PARAMETER Only
  Restrict to some engines, e.g. -Only rhwp,pandoc (LibreOffice is large: ~356 MB download).

.EXAMPLE
  pwsh build/fetch-engines.ps1
  pwsh build/fetch-engines.ps1 -Only rhwp
  pwsh build/fetch-engines.ps1 -Destination publish/win-x64/engines
#>
[CmdletBinding()]
param(
    [string]$Destination = (Join-Path $PSScriptRoot '..\engines'),
    # Accepts an array (-Only rhwp,jre from PowerShell) or one comma-separated string (pwsh -File ... -Only rhwp,jre).
    [string[]]$Only
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue' # Invoke-WebRequest is ~10x faster without the progress bar

# Versions, URLs and hashes come from the same list the app uses for on-demand downloads.
$manifest = Get-Content (Join-Path $PSScriptRoot '..\src\Filee.Engines\Infrastructure\engines.json') -Raw | ConvertFrom-Json
$engines = [ordered]@{}
foreach ($component in $manifest.components.PSObject.Properties) {
    $engines[$component.Name] = @{ Url = $component.Value.url; Sha256 = $component.Value.sha256 }
}

$cache = Join-Path $PSScriptRoot '.cache'
New-Item -ItemType Directory -Force -Path $cache, $Destination | Out-Null
$Destination = (Resolve-Path $Destination).Path

function Get-Engine([string]$name) {
    $info = $engines[$name]
    $file = Join-Path $cache ([Uri]::UnescapeDataString(($info.Url -split '/')[-1]))
    if ((Test-Path $file) -and (Get-FileHash $file -Algorithm SHA256).Hash -eq $info.Sha256) {
        Write-Host "ok   $name (cached)"
        return $file
    }
    Write-Host "get  $name  $($info.Url)"
    Invoke-WebRequest -Uri $info.Url -OutFile $file -UseBasicParsing
    $hash = (Get-FileHash $file -Algorithm SHA256).Hash
    if ($hash -ne $info.Sha256) {
        Remove-Item $file
        throw "Checksum mismatch for $name. Expected $($info.Sha256), got $hash."
    }
    return $file
}

function Reset-Folder([string]$path) {
    if (Test-Path $path) { Remove-Item $path -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $path | Out-Null
}

# Moves the content of the single top-level folder of an extracted archive up one level.
function Expand-Flat([string]$zip, [string]$target) {
    $tmp = Join-Path $cache ('x-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    Expand-Archive -Path $zip -DestinationPath $tmp
    $items = Get-ChildItem $tmp
    $root = if ($items.Count -eq 1 -and $items[0].PSIsContainer) { $items[0].FullName } else { $tmp }
    Reset-Folder $target
    Get-ChildItem $root -Force | Move-Item -Destination $target
    Remove-Item $tmp -Recurse -Force
}

# Unpacks the Windows binaries (Library/bin) of a conda-forge package: a zip holding pkg-*.tar.zst. Windows' own
# tar.exe (bsdtar with zstd, Windows 10 1803 and later) reads the tarball; the app uses a managed decompressor.
function Expand-Conda([string]$conda, [string]$target) {
    $tmp = Join-Path $cache ('c-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::ExtractToDirectory($conda, $tmp)
    $pkg = Get-ChildItem $tmp -Filter 'pkg-*.tar.zst' | Select-Object -First 1
    if (-not $pkg) { throw "$conda is not a conda package." }
    $files = Join-Path $tmp 'files'
    New-Item -ItemType Directory -Force -Path $files | Out-Null
    & (Join-Path $env:SystemRoot 'System32\tar.exe') -xf $pkg.FullName -C $files
    if ($LASTEXITCODE -ne 0) { throw "tar.exe could not unpack $($pkg.Name) (it needs zstd support)." }
    Reset-Folder $target
    Get-ChildItem (Join-Path $files 'Library\bin') -Force | Move-Item -Destination $target
    Remove-Item $tmp -Recurse -Force
}

# Removes parts of LibreOffice that headless conversion never uses (~500 MB):
# help, readmes, the MSI stub copied by the admin install, the gallery, and spell-check dictionaries except
# English and Korean (their hyphenation patterns can affect page layout when exporting PDFs).
function Optimize-LibreOffice([string]$root) {
    $remove = @('help', 'readmes', 'share\gallery') | ForEach-Object { Join-Path $root $_ }
    $remove += Get-ChildItem $root -Filter '*.msi' | ForEach-Object FullName
    $remove += Get-ChildItem (Join-Path $root 'share\extensions') -Directory -Filter 'dict-*' |
        Where-Object { $_.Name -notin @('dict-en', 'dict-ko') } | ForEach-Object FullName
    foreach ($path in $remove) { Remove-Item $path -Recurse -Force -ErrorAction SilentlyContinue }
}

$selected = if ($Only) { @($Only -split ',' | ForEach-Object { $_.Trim().ToLowerInvariant() } | Where-Object { $_ }) } else { @($engines.Keys) }
foreach ($name in $selected) {
    if (-not $engines.Contains($name)) { throw "Unknown engine '$name'. Valid: $($engines.Keys -join ', ')" }
}

if ('rhwp' -in $selected) {
    $zip = Get-Engine 'rhwp'
    Expand-Flat $zip (Join-Path $Destination 'rhwp')
    if (-not (Get-ChildItem (Join-Path $Destination 'rhwp') -Recurse -Filter 'rhwp.exe')) { throw 'rhwp.exe not found in the archive.' }
    # Keep rhwp.exe directly in engines/rhwp.
    $exe = Get-ChildItem (Join-Path $Destination 'rhwp') -Recurse -Filter 'rhwp.exe' | Select-Object -First 1
    if ($exe.DirectoryName -ne (Join-Path $Destination 'rhwp')) {
        Get-ChildItem $exe.DirectoryName -Force | Move-Item -Destination (Join-Path $Destination 'rhwp') -Force
    }
}

if ('pandoc' -in $selected) {
    $zip = Get-Engine 'pandoc'
    Expand-Flat $zip (Join-Path $Destination 'pandoc')
    if (-not (Test-Path (Join-Path $Destination 'pandoc\pandoc.exe'))) { throw 'pandoc.exe not found in the archive.' }
}

if ('7zip' -in $selected) {
    # The official MSI unpacked with an administrative install (files only, no system changes). Only the console
    # program is kept: 7z.exe + 7z.dll (all formats, incl. RAR), the license and the readme; not the GUI or help.
    $msi = Get-Engine '7zip'
    $tmp = Join-Path $cache '7z-admin'
    Reset-Folder $tmp
    $proc = Start-Process msiexec.exe -ArgumentList @('/a', "`"$msi`"", '/qn', "TARGETDIR=`"$tmp`"") -Wait -PassThru
    if ($proc.ExitCode -ne 0) { throw "msiexec /a failed for 7-Zip with exit code $($proc.ExitCode)." }

    $exe = Get-ChildItem $tmp -Recurse -Filter '7z.exe' | Select-Object -First 1
    if (-not $exe) { throw '7z.exe not found after extracting the 7-Zip MSI.' }
    $target = Join-Path $Destination '7zip'
    Reset-Folder $target
    foreach ($name in @('7z.exe', '7z.dll', 'License.txt', 'readme.txt')) {
        Copy-Item (Join-Path $exe.DirectoryName $name) $target
    }
    Remove-Item $tmp -Recurse -Force
}

if ('ffmpeg' -in $selected) {
    # Same layout as the app's installer: the archive's single top folder is dropped, the programs stay in bin/.
    $zip = Get-Engine 'ffmpeg'
    Expand-Flat $zip (Join-Path $Destination 'ffmpeg')
    foreach ($exe in 'ffmpeg.exe', 'ffprobe.exe') {
        if (-not (Test-Path (Join-Path $Destination "ffmpeg\bin\$exe"))) { throw "$exe not found in the archive." }
    }
}

if ('jre' -in $selected) {
    $zip = Get-Engine 'jre'
    Expand-Flat $zip (Join-Path $Destination 'jre')
}

if ('libreoffice' -in $selected) {
    $msi = Get-Engine 'libreoffice'
    $tmp = Join-Path $cache 'lo-admin'
    Reset-Folder $tmp
    Write-Host 'unpack LibreOffice (administrative install, no system changes)'
    $proc = Start-Process msiexec.exe -ArgumentList @('/a', "`"$msi`"", '/qn', "TARGETDIR=`"$tmp`"") -Wait -PassThru
    if ($proc.ExitCode -ne 0) { throw "msiexec /a failed with exit code $($proc.ExitCode)." }

    $soffice = Get-ChildItem $tmp -Recurse -Filter 'soffice.exe' | Select-Object -First 1
    if (-not $soffice) { throw 'soffice.exe not found after extracting the MSI.' }
    $root = Split-Path $soffice.DirectoryName -Parent   # folder containing program/, share/, ...
    $target = Join-Path $Destination 'libreoffice'
    Reset-Folder $target
    Get-ChildItem $root -Force | Move-Item -Destination $target
    Remove-Item $tmp -Recurse -Force

    Optimize-LibreOffice $target
}

if ('h2orestart' -in $selected) {
    $oxt = Get-Engine 'h2orestart'
    $lo = Join-Path $Destination 'libreoffice'
    if (-not (Test-Path (Join-Path $lo 'program\soffice.exe'))) {
        Write-Warning 'H2Orestart skipped: LibreOffice is not in the destination folder.'
    }
    else {
        # Bundled extensions are plain folders under share/extensions; LibreOffice registers them on start-up.
        $target = Join-Path $lo 'share\extensions\H2Orestart'
        Reset-Folder $target
        $zipCopy = Join-Path $cache 'H2Orestart.zip'
        Copy-Item $oxt $zipCopy -Force
        Expand-Archive -Path $zipCopy -DestinationPath $target -Force
        Remove-Item $zipCopy
    }
}

if ('vcruntime' -in $selected -or 'ghostscript' -in $selected) {
    Expand-Conda (Get-Engine 'vcruntime') (Join-Path $Destination 'vcruntime')
}

if ('ghostscript' -in $selected) {
    $target = Join-Path $Destination 'ghostscript'
    Expand-Conda (Get-Engine 'ghostscript') $target
    if (-not (Test-Path (Join-Path $target 'gswin64c.exe'))) { throw 'gswin64c.exe not found in the Ghostscript package.' }
    # App-local Microsoft C++ runtime next to gswin64c.exe, as EngineInstaller does.
    Copy-Item (Join-Path $Destination 'vcruntime\*.dll') $target -Force
}

if ('calibre' -in $selected) {
    $msi = Get-Engine 'calibre'
    $tmp = Join-Path $cache 'calibre-admin'
    Reset-Folder $tmp
    Write-Host 'unpack calibre (administrative install, no system changes)'
    $proc = Start-Process msiexec.exe -ArgumentList @('/a', "`"$msi`"", '/qn', "TARGETDIR=`"$tmp`"") -Wait -PassThru
    if ($proc.ExitCode -ne 0) { throw "msiexec /a failed with exit code $($proc.ExitCode)." }

    $convert = Get-ChildItem $tmp -Recurse -Filter 'ebook-convert.exe' | Select-Object -First 1
    if (-not $convert) { throw 'ebook-convert.exe not found after extracting the MSI.' }
    $target = Join-Path $Destination 'calibre'
    Reset-Folder $target
    Get-ChildItem $convert.DirectoryName -Force | Move-Item -Destination $target
    Remove-Item $tmp -Recurse -Force
}

Write-Host "Engines ready in $Destination"
