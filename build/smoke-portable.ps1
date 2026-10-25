# Checks a portable package the way a user gets it: verifies the archive checksum, unpacks it and runs the CLI,
# rhwp and 7-Zip from it, including conversions through rhwp (TXT -> HWPX -> HWP -> PDF). On Linux it also loads
# the X11 input hook back end (libuiohook-xrecord.so) to prove its libraries and glibc requirement resolve.
# Used by .github/workflows/portable.yml on the build machine and on the oldest supported Ubuntu.
#
# In GitHub Actions it sets FILEE_SMOKE_APP (the desktop app, for build/smoke-launch-app.py) and FILEE_SMOKE_PACKAGE
# (the unpacked package folder).
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')][string]$Runtime,
    [string]$Packages = (Join-Path $PSScriptRoot '../Releases'),
    [string]$WorkDir = (Join-Path ($env:RUNNER_TEMP ?? [IO.Path]::GetTempPath()) 'filee-smoke')
)

$ErrorActionPreference = 'Stop'

function Invoke-Checked([string]$name, [scriptblock]$command) {
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$name failed with exit code $LASTEXITCODE" }
}

$archives = @(Get-ChildItem -LiteralPath $Packages -Filter "*-$Runtime.tar.gz")
if ($archives.Count -ne 1) { throw "Expected one portable archive for $Runtime in $Packages, found $($archives.Count)." }
$archive = $archives[0]
$expected = ((Get-Content -LiteralPath (Join-Path $Packages ($archive.Name.Replace('.tar.gz', '-SHA256SUMS.txt')))) |
    Where-Object { $_.EndsWith("  $($archive.Name)") }).Split(' ')[0]
if ((Get-FileHash -LiteralPath $archive.FullName -Algorithm SHA256).Hash -ne $expected) { throw 'Archive checksum mismatch.' }

if (Test-Path -LiteralPath $WorkDir) { throw "$WorkDir already exists. Use a fresh folder." }
New-Item -ItemType Directory -Path $WorkDir | Out-Null
$smoke = (Resolve-Path -LiteralPath $WorkDir).Path
Invoke-Checked 'Archive extraction' { tar -xzf $archive.FullName -C $smoke }
$package = Join-Path $smoke ($archive.Name.Replace('.tar.gz', ''))
if ($IsMacOS) {
    Invoke-Checked 'Extracted app signature' { codesign --verify --deep --strict (Join-Path $package 'Filee.app') }
}
$bin = if ($IsMacOS) { Join-Path $package 'Filee.app/Contents/MacOS' } else { $package }
$cli = Join-Path $bin 'filee-cli'

Invoke-Checked 'CLI start' { & $cli version }
Invoke-Checked 'rhwp start' { & (Join-Path $bin 'engines/rhwp/rhwp') --version }
Invoke-Checked '7-Zip start' { & (Join-Path $bin 'engines/7zip/7zz') i | Out-Null }

function Convert-Smoke([string]$source, [string]$target) {
    $folder = Join-Path $smoke "to-$target"
    $result = & $cli convert $source --to $target -o $folder --json
    $code = $LASTEXITCODE
    $output = Join-Path $folder ([IO.Path]::GetFileNameWithoutExtension($source) + ".$target")
    if ($code -ne 0 -or -not (Test-Path -LiteralPath $output -PathType Leaf)) {
        $result | Write-Host
        throw "Published CLI conversion to $target failed (exit code $code)."
    }
    Write-Host "Converted $([IO.Path]::GetFileName($source)) -> $target ($((Get-Item -LiteralPath $output).Length) bytes)"
    return $output
}

$inputFile = Join-Path $smoke 'sample.txt'
'Filee 한글 conversion smoke test' | Set-Content -LiteralPath $inputFile -Encoding utf8NoBOM
$hwpx = Convert-Smoke $inputFile 'hwpx'
# HWPX -> HWP and HWP -> PDF are rhwp's routes: they run the bundled rhwp from the package.
$hwp = Convert-Smoke $hwpx 'hwp'
$pdf = Convert-Smoke $hwp 'pdf'
$header = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($pdf), 0, 5)
if ($header -ne '%PDF-') { throw "rhwp's PDF does not start with %PDF- ($header)." }

if ($IsLinux) {
    # The loader dlopens this back end in an X11 session; loading it here fails like the app would when a library
    # or the glibc version it needs is missing.
    $backend = Join-Path $bin 'libuiohook-xrecord.so'
    Invoke-Checked 'Load libuiohook-xrecord.so' {
        python3 -c 'import ctypes, sys; ctypes.CDLL(sys.argv[1]); print("Loaded " + sys.argv[1])' $backend
    }
}

if ($env:GITHUB_ENV) {
    "FILEE_SMOKE_APP=$(Join-Path $bin 'Filee')" >> $env:GITHUB_ENV
    "FILEE_SMOKE_PACKAGE=$package" >> $env:GITHUB_ENV
}
Write-Host "Package checks passed: $package"
