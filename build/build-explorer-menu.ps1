<#
.SYNOPSIS
  Builds the Windows 11 File Explorer menu extension: FileeExplorerMenu.dll + FileeExplorerMenu.msix.

.DESCRIPTION
  The DLL (src/Filee.ExplorerMenu, plain C, no C runtime) is compiled with a pinned, portable Zig toolchain that is
  downloaded into build/.cache (SHA-256 verified), so neither Visual C++ nor the Windows SDK is needed - locally or
  on GitHub's windows-latest runner. The unsigned sparse package is packed by build/tools/make-explorer-package.cs
  through the packaging API that ships with Windows.

  Both files belong next to Filee.exe; the release workflow passes the publish folder as -Destination.

.EXAMPLE
  pwsh build/build-explorer-menu.ps1
  pwsh build/build-explorer-menu.ps1 -Destination publish/win-x64 -Version 1.2.0
#>
[CmdletBinding()]
param(
    [string]$Destination = (Join-Path $PSScriptRoot '.cache\explorer-menu'),
    # Written into the DLL's version resource; defaults to <Version> in Directory.Build.props.
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$cache = Join-Path $PSScriptRoot '.cache'

# Zig 0.16.0 for x86_64 Windows. Update the version, hash and size together.
$zigName = 'zig-x86_64-windows-0.16.0'
$zigSha256 = '68659EB5F1E4EB1437A722F1DD889C5A322C9954607F5EDCF337BC3684A75A7E'
$zigSize = 97217739
# ziglang.org asks automated builds to prefer the community mirrors (https://ziglang.org/download/community-mirrors/);
# the archive is verified with SHA-256 whichever server it comes from.
$zigUrls = @(
    "https://ziglang.freetls.fastly.net/$zigName.zip",
    "https://pkg.hexops.org/zig/$zigName.zip",
    "https://ziglang.org/download/0.16.0/$zigName.zip"
)

function Get-Zig {
    $folder = Join-Path $cache $zigName
    $exe = Join-Path $folder 'zig.exe'
    if (Test-Path $exe) { return $exe }

    New-Item -ItemType Directory -Force -Path $cache | Out-Null
    $zip = Join-Path $cache "$zigName.zip"
    $ok = (Test-Path $zip) -and (Get-FileHash $zip -Algorithm SHA256).Hash -eq $zigSha256
    foreach ($url in $zigUrls) {
        if ($ok) { break }
        Write-Host "get  $url"
        try {
            # curl.exe (part of Windows) is much faster than Invoke-WebRequest for ~100 MB. A mirror that stalls (under
            # 200 KB/s for 30 s) is dropped quickly for the next one instead of holding the build for many minutes.
            & curl.exe --fail --location --silent --show-error --connect-timeout 20 --speed-limit 200000 --speed-time 30 `
                --max-time 600 --output $zip $url
            if ($LASTEXITCODE -ne 0) { throw "curl exit code $LASTEXITCODE" }
            $hash = (Get-FileHash $zip -Algorithm SHA256).Hash
            if ($hash -ne $zigSha256) { throw "checksum mismatch (got $hash, size $((Get-Item $zip).Length), expected $zigSize bytes)" }
            $ok = $true
        }
        catch {
            Write-Warning "Download from $url failed: $($_.Exception.Message)"
            Remove-Item $zip -ErrorAction SilentlyContinue
        }
    }
    if (-not $ok) { throw "Could not download $zigName from any mirror." }

    Write-Host "unpack $zigName"
    $partial = Join-Path $cache "$zigName.partial"
    Remove-Item $partial -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $partial | Out-Null
    & (Join-Path $env:SystemRoot 'System32\tar.exe') -xf $zip -C $partial
    if ($LASTEXITCODE -ne 0) { throw "tar failed with exit code $LASTEXITCODE" }
    Move-Item (Join-Path $partial $zigName) $folder
    Remove-Item $partial -Recurse -Force
    Remove-Item $zip
    return $exe
}

if (-not $Version) {
    $Version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
$parts = ($Version -split '[-+]')[0].Split('.') + @('0', '0', '0')
$major, $minor, $patch = [int]$parts[0], [int]$parts[1], [int]$parts[2]

$zig = Get-Zig
$env:ZIG_GLOBAL_CACHE_DIR = Join-Path $cache 'zig-cache'
$env:ZIG_LOCAL_CACHE_DIR = Join-Path $cache 'zig-cache'
$source = Join-Path $root 'src\Filee.ExplorerMenu'
$obj = Join-Path $cache 'explorer-menu-obj'
New-Item -ItemType Directory -Force -Path $obj, $Destination | Out-Null

# The version resource gets the numbers through a generated header-like prefix (zig's resource compiler has no -D).
$rc = Join-Path $obj 'FileeExplorerMenu.rc'
$defines = "#define FILEE_VERSION_MAJOR $major`r`n#define FILEE_VERSION_MINOR $minor`r`n#define FILEE_VERSION_PATCH $patch`r`n#define FILEE_VERSION_TEXT `"$Version`"`r`n"
Set-Content -Path $rc -Value ($defines + (Get-Content (Join-Path $source 'FileeExplorerMenu.rc') -Raw)) -Encoding utf8NoBOM -NoNewline

Write-Host "build FileeExplorerMenu.dll $Version"
$dll = Join-Path $obj 'FileeExplorerMenu.dll'
# -nostdlib: no C runtime inside explorer.exe; the entry point is DllMain itself. -s strips symbols.
& $zig cc -target x86_64-windows-gnu -shared -O2 -s -nostdlib -Wall -Wextra -Werror `
    -isystem (Join-Path (Split-Path $zig) 'lib\libc\include\any-windows-any') `
    (Join-Path $source 'FileeExplorerMenu.c') (Join-Path $source 'FileeExplorerMenu.def') $rc `
    -lkernel32 -luser32 -lole32 -lshell32 -lshlwapi '-Wl,--entry=DllMain' -o $dll
if ($LASTEXITCODE -ne 0) { throw "zig cc failed with exit code $LASTEXITCODE" }
Copy-Item $dll $Destination -Force

Write-Host "pack FileeExplorerMenu.msix"
$Destination = (Resolve-Path $Destination).Path
Push-Location $root
try {
    & dotnet run build/tools/make-explorer-package.cs -- $Destination
    if ($LASTEXITCODE -ne 0) { throw "make-explorer-package failed with exit code $LASTEXITCODE" }
}
finally {
    Pop-Location
}

Write-Host "Explorer menu ready in $Destination"
