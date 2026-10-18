[CmdletBinding()]
param(
    [ValidateSet('linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')][string]$Runtime,
    [string]$Version,
    [string]$OutputDir = (Join-Path $PSScriptRoot '../Releases'),
    [string]$SigningIdentity = '-',
    [switch]$SkipFonts
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
$sums = Join-Path $OutputDir "$packageName-SHA256SUMS.txt"
if ((Test-Path -LiteralPath $archive) -or (Test-Path -LiteralPath $sums)) {
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
& (Join-Path $PSScriptRoot 'fetch-portable-engines.ps1') -Runtime $Runtime -Destination (Join-Path $publish 'engines')
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
}
Invoke-Checked 'Installer permissions' { chmod +x (Join-Path $package 'install.sh') (Join-Path $package 'uninstall.sh') }
$temporaryArchive = "$archive.$([Guid]::NewGuid().ToString('N')).partial"
Invoke-Checked 'Create tar.gz' { tar -czf $temporaryArchive -C $stage $packageName }
Move-Item -LiteralPath $temporaryArchive -Destination $archive
"$((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant())  $packageName.tar.gz" |
    Set-Content -LiteralPath $sums -Encoding ascii
Write-Host "Package: $archive"
Write-Host "Checksums: $sums"
Write-Host "Unpacked build retained for testing: $package"
if ($Runtime.StartsWith('osx-') -and $SigningIdentity -eq '-') {
    Write-Host 'This macOS build is ad-hoc signed for testing; it is not notarized for public distribution.'
}
