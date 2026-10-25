# Builds the Linux or macOS portable package (tar.gz + SHA256SUMS) on its own operating system. macOS also gets a
# disk image (Filee.app next to an Applications link, to drag it there), signed and notarized as the app is.
#
# Linux packages must run on Ubuntu 22.04 (glibc 2.35). rhwp's Linux release needs a newer glibc, so CI builds it from
# source on Ubuntu 22.04 (build/build-linux-native.py) and passes it in:
#   -RhwpOverride <dir>  a folder with the rhwp program (rhwp's release archive layout) used instead of the rhwp
#                        download pinned in engines.json
# Without it (local builds) the package uses the download, which needs a newer glibc.
[CmdletBinding()]
param(
    [ValidateSet('linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')][string]$Runtime,
    [string]$Version,
    [string]$OutputDir = (Join-Path $PSScriptRoot '../Releases'),
    [string]$SigningIdentity = '-',
    [switch]$SkipFonts,
    [string]$RhwpOverride
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
if ($IsWindows) { throw 'Create Unix packages on Linux or macOS to preserve executable permissions and sign macOS binaries.' }
if (-not $Runtime) {
    $cpu = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
    $Runtime = $(if ($IsMacOS) { 'osx' } else { 'linux' }) + "-$cpu"
}
if (($Runtime.StartsWith('osx-') -and -not $IsMacOS) -or ($Runtime.StartsWith('linux-') -and -not $IsLinux)) {
    throw "Build $Runtime on its native operating system."
}
if ($RhwpOverride -and -not $Runtime.StartsWith('linux-')) {
    throw '-RhwpOverride is for Linux packages.'
}
if ($RhwpOverride) {
    $RhwpOverride = (Resolve-Path -LiteralPath $RhwpOverride).Path
    if (-not (Test-Path -LiteralPath (Join-Path $RhwpOverride 'rhwp') -PathType Leaf)) {
        throw "-RhwpOverride: $RhwpOverride has no rhwp program."
    }
}
if (-not $Version) {
    $Version = ([xml](Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version |
        Where-Object { $_ } | Select-Object -First 1
}
$Version = $Version.TrimStart('v')
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z][0-9A-Za-z.-]*)?$') {
    throw 'Version must be a three-part version with an optional prerelease suffix.'
}
$bundleVersion = ($Version -split '-')[0]
$packageName = "Filee-$Version-$Runtime"
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$OutputDir = (Resolve-Path -LiteralPath $OutputDir).Path
$archive = Join-Path $OutputDir "$packageName.tar.gz"
$diskImage = Join-Path $OutputDir "$packageName.dmg"
$sums = Join-Path $OutputDir "$packageName-SHA256SUMS.txt"
if ((Test-Path -LiteralPath $archive) -or (Test-Path -LiteralPath $sums) -or (Test-Path -LiteralPath $diskImage)) {
    throw "Package $packageName already exists. Use another output folder or version."
}
$stage = Join-Path $root ('build/.cache/portable/' + [Guid]::NewGuid().ToString('N'))
$package = Join-Path $stage $packageName
$appBundle = Join-Path $package 'Filee.app'
$publish = if ($Runtime.StartsWith('osx-')) { Join-Path $appBundle 'Contents/MacOS' } else { $package }
New-Item -ItemType Directory -Force -Path $publish | Out-Null

function Invoke-Checked([string]$name, [scriptblock]$command) {
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$name failed with exit code $LASTEXITCODE" }
}

if (-not $SkipFonts) { & (Join-Path $PSScriptRoot 'fetch-fonts.ps1') }
foreach ($project in @('Filee.App', 'Filee.Cli')) {
    Invoke-Checked "Publish $project" {
        dotnet publish (Join-Path $root "src/$project/$project.csproj") -c Release -r $Runtime --self-contained true `
            "-p:Version=$Version" -p:FileeRelease=true -p:PublishReadyToRun=false -o $publish
    }
}
$engines = Join-Path $publish 'engines'
if ($RhwpOverride) {
    & (Join-Path $PSScriptRoot 'fetch-portable-engines.ps1') -Runtime $Runtime -Destination $engines -Only 7zip
    Copy-Item -LiteralPath $RhwpOverride -Destination (Join-Path $engines 'rhwp') -Recurse
    Invoke-Checked 'rhwp permissions' { chmod +x (Join-Path $engines 'rhwp/rhwp') }
    Write-Host "rhwp from $RhwpOverride"
}
else {
    & (Join-Path $PSScriptRoot 'fetch-portable-engines.ps1') -Runtime $Runtime -Destination $engines
}
foreach ($name in @('Filee', 'filee-cli')) {
    $executable = Join-Path $publish $name
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw "Publish did not produce $name" }
    Invoke-Checked 'Executable permissions' { chmod +x $executable }
}
foreach ($notice in @('LICENSE', 'THIRD-PARTY-NOTICES.md')) {
    if (Test-Path -LiteralPath (Join-Path $root $notice)) {
        Copy-Item -LiteralPath (Join-Path $root $notice) -Destination $package
    }
}

if ($Runtime.StartsWith('osx-')) {
    $contents = Join-Path $appBundle 'Contents'
    $resources = Join-Path $contents 'Resources'
    New-Item -ItemType Directory -Path $resources | Out-Null
    $info = Get-Content -LiteralPath (Join-Path $root 'installer/macos/Info.plist') -Raw
    $info.Replace('__VERSION__', $bundleVersion).Replace('__BUNDLE_VERSION__', $bundleVersion) |
        Set-Content -LiteralPath (Join-Path $contents 'Info.plist') -Encoding utf8NoBOM
    Invoke-Checked 'Validate Info.plist' { plutil -lint (Join-Path $contents 'Info.plist') }
    $iconset = Join-Path $stage 'filee.iconset'
    New-Item -ItemType Directory -Path $iconset | Out-Null
    $sourceIcon = Join-Path $root 'src/Filee.App/Assets/Icons/filee.png'
    foreach ($size in @(16, 32, 128, 256, 512)) {
        foreach ($scale in @(1, 2)) {
            $pixels = $size * $scale
            $suffix = if ($scale -eq 2) { '@2x' } else { '' }
            $target = Join-Path $iconset "icon_${size}x${size}$suffix.png"
            Invoke-Checked 'Render app icon' { sips -z $pixels $pixels $sourceIcon --out $target | Out-Null }
        }
    }
    Invoke-Checked 'Create app icon' { iconutil -c icns $iconset -o (Join-Path $resources 'filee.icns') }
    # Translations of the service title ("Convert with Filee") in Finder's right-click menu.
    foreach ($localization in Get-ChildItem -LiteralPath (Join-Path $root 'installer/macos') -Directory -Filter '*.lproj') {
        foreach ($stringsFile in Get-ChildItem -LiteralPath $localization.FullName -File -Filter '*.strings') {
            Invoke-Checked 'Validate localized strings' { plutil -lint $stringsFile.FullName }
        }
        Copy-Item -LiteralPath $localization.FullName -Destination $resources -Recurse
    }
    $entitlements = Join-Path $root 'installer/macos/entitlements.plist'
    $signOptions = @('--force', '--sign', $SigningIdentity)
    if ($SigningIdentity -ne '-') { $signOptions += @('--timestamp', '--options', 'runtime') }
    # Every file in Contents/MacOS is code to codesign, the .NET assemblies too (their signature goes into extended
    # attributes): signing the main executable checks all of them. Deepest paths first, the main executable last.
    $mainExecutable = Join-Path $publish 'Filee'
    $files = Get-ChildItem -LiteralPath $publish -Recurse -File | Where-Object { $_.FullName -ne $mainExecutable } |
        Sort-Object { $_.FullName.Length } -Descending
    foreach ($file in $files) {
        Invoke-Checked "Sign $($file.Name)" { codesign @signOptions --entitlements $entitlements $file.FullName }
    }
    Invoke-Checked 'Sign main executable' { codesign @signOptions --entitlements $entitlements $mainExecutable }
    Invoke-Checked 'Sign app bundle' { codesign @signOptions --entitlements $entitlements $appBundle }
    Invoke-Checked 'Verify app signature' { codesign --verify --deep --strict $appBundle }
    Copy-Item -LiteralPath (Join-Path $root 'installer/macos/install.sh') -Destination $package
    Copy-Item -LiteralPath (Join-Path $root 'installer/macos/uninstall.sh') -Destination $package
    Copy-Item -LiteralPath (Join-Path $root 'installer/macos/INSTALL.txt') -Destination $package
}
else {
    Copy-Item -LiteralPath (Join-Path $root 'src/Filee.App/Assets/Icons/filee.png') -Destination $package
    Copy-Item -LiteralPath (Join-Path $root 'installer/linux/install.sh') -Destination $package
    Copy-Item -LiteralPath (Join-Path $root 'installer/linux/uninstall.sh') -Destination $package
    Copy-Item -LiteralPath (Join-Path $root 'installer/linux/INSTALL.txt') -Destination $package
    Copy-Item -LiteralPath (Join-Path $root 'installer/linux/kwin') -Destination $package -Recurse
    Copy-Item -LiteralPath (Join-Path $root 'installer/linux/gnome') -Destination $package -Recurse
    Copy-Item -LiteralPath (Join-Path $root 'docs/GNOME-WAYLAND.md') -Destination $package
}
Invoke-Checked 'Installer permissions' { chmod +x (Join-Path $package 'install.sh') (Join-Path $package 'uninstall.sh') }
$temporaryArchive = "$archive.$([Guid]::NewGuid().ToString('N')).partial"
Invoke-Checked 'Create tar.gz' { tar -czf $temporaryArchive -C $stage $packageName }
Move-Item -LiteralPath $temporaryArchive -Destination $archive
$packages = @($archive)
if ($Runtime.StartsWith('osx-')) {
    # The same signed Filee.app, next to a link to /Applications to drag it onto. ditto keeps the signature's
    # extended attributes. install.sh (in the tar.gz) is still the way to get the filee command.
    $volume = Join-Path $stage 'dmg'
    New-Item -ItemType Directory -Path $volume | Out-Null
    Invoke-Checked 'Copy app for the disk image' { ditto $appBundle (Join-Path $volume 'Filee.app') }
    Invoke-Checked 'Applications link' { ln -s /Applications (Join-Path $volume 'Applications') }
    Copy-Item -LiteralPath (Join-Path $root 'installer/macos/INSTALL.txt') -Destination $volume
    $temporaryImage = "$diskImage.$([Guid]::NewGuid().ToString('N')).partial.dmg"
    Invoke-Checked 'Create dmg' { hdiutil create -volname Filee -srcfolder $volume -fs HFS+ -format UDZO -ov $temporaryImage | Out-Null }
    Move-Item -LiteralPath $temporaryImage -Destination $diskImage
    $packages += $diskImage
}
$packages | ForEach-Object { "$((Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant())  $(Split-Path $_ -Leaf)" } |
    Set-Content -LiteralPath $sums -Encoding ascii
Write-Host "Package: $($packages -join ', ')"
Write-Host "Checksums: $sums"
Write-Host "Unpacked build retained for testing: $package"
if ($Runtime.StartsWith('osx-') -and $SigningIdentity -eq '-') {
    Write-Host 'This macOS build is ad-hoc signed for testing; it is not notarized for public distribution.'
}
