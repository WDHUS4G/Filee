<#
.SYNOPSIS
  Builds the Windows installer Filee-<version>-win-Setup.exe (Filee's own, src/Filee.Setup) and, with -Portable, the
  portable zip, into -OutputDir.

.DESCRIPTION
  Without -PublishDir the app is published first (Release, self-contained, -p:FileeRelease=true) into
  publish/win-x64, together with the Windows 11 Explorer menu extension (build-explorer-menu.ps1), the bundled
  small engines rhwp and 7-Zip and the Microsoft C++ runtime DLLs next to rhwp.exe and Filee.exe
  (fetch-engines.ps1); the large engines are downloaded by the app (engines.json).

  Setup itself (src/Filee.Setup) is published as one self-contained, trimmed program (no .NET needed on the PC);
  build/tools/make-setup.cs appends the publish folder to it as an LZMA payload with a SHA-256 manifest, and the
  engines Setup offers from EngineDownloads. The portable zip is the publish folder as it is: without Setup's
  uninstall.exe next to Filee.exe, Filee knows it is a portable copy.

.PARAMETER CertificateThumbprint
  Signs the release (build/sign-windows.ps1): the programs in the publish folder that come unsigned, the Setup
  program (it becomes uninstall.exe) and, after the payload is appended, Setup.exe. The release workflow builds
  without it; build/sign-release.ps1 signs its output on a PC signed in to SimplySign.

.PARAMETER CheckOnly
  Builds Setup around a stub app (fast compression) and runs it in a sandbox: install, update and uninstall into a
  scratch folder with its own registry file, nothing on the PC is touched (CI).

.EXAMPLE
  pwsh build/build-installer.ps1
  pwsh build/build-installer.ps1 -Version 2.0.0 -Portable
  pwsh build/build-installer.ps1 -Version 2.0.0 -Portable -PublishDir app -SetupProgram Filee.Setup.exe -CertificateThumbprint <sha1>
  pwsh build/build-installer.ps1 -CheckOnly
#>
[CmdletBinding()]
param(
    # Defaults to <Version> in Directory.Build.props.
    [string]$Version,
    # A finished publish folder (Filee.exe, FileeExplorerMenu.dll/.msix, engines\); skips publishing.
    [string]$PublishDir,
    # A finished Setup program (Filee.Setup.exe from an earlier run); skips publishing src/Filee.Setup.
    [string]$SetupProgram,
    [string]$OutputDir = (Join-Path $PSScriptRoot '..\Releases'),
    [switch]$Portable,
    [string]$CertificateThumbprint,
    [switch]$CheckOnly
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$cache = Join-Path $PSScriptRoot '.cache'

function Invoke-Checked([string]$what, [scriptblock]$command) {
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$what failed with exit code $LASTEXITCODE" }
}

if (-not $Version) {
    $Version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$OutputDir = (Resolve-Path $OutputDir).Path

if ($CheckOnly) {
    # A stand-in app: Setup only needs files, Filee.exe among them.
    $check = Join-Path $cache 'installer-check'
    Remove-Item $check -Recurse -Force -ErrorAction SilentlyContinue
    $PublishDir = Join-Path $check 'app'
    New-Item -ItemType Directory -Force -Path (Join-Path $PublishDir 'cli'), (Join-Path $PublishDir 'engines\rhwp') | Out-Null
    Set-Content (Join-Path $PublishDir 'Filee.exe') 'stub' -NoNewline
    Set-Content (Join-Path $PublishDir 'cli\filee.cmd') "@`"%~dp0..\filee-cli.exe`" %*" -Encoding ascii
    Set-Content (Join-Path $PublishDir 'engines\rhwp\rhwp.exe') 'stub' -NoNewline
    $OutputDir = $check
}
elseif (-not $PublishDir) {
    $PublishDir = Join-Path $root 'publish\win-x64'
    Remove-Item $PublishDir -Recurse -Force -ErrorAction SilentlyContinue
    Invoke-Checked 'dotnet publish' {
        dotnet publish (Join-Path $root 'src\Filee.App\Filee.App.csproj') -c Release -r win-x64 --self-contained `
            "-p:Version=$Version" -p:PublishReadyToRun=true -p:FileeRelease=true -o $PublishDir
    }
    # The command line, in the same folder so it shares the runtime and the engines (filee-cli.exe). Its name can't
    # be filee.exe next to Filee.exe, so the "filee" command is cli\filee.cmd, which Setup can put on PATH.
    Invoke-Checked 'dotnet publish (cli)' {
        dotnet publish (Join-Path $root 'src\Filee.Cli\Filee.Cli.csproj') -c Release -r win-x64 --self-contained `
            "-p:Version=$Version" -p:PublishReadyToRun=true -p:FileeRelease=true -o $PublishDir
    }
    # Native debug symbols from NuGet packages (libSkiaSharp.pdb, libHarfBuzzSharp.pdb: ~105 MB) are useless to users.
    # Filee.App's publish removes them, but publishing the command line into the same folder brings them back.
    Get-ChildItem $PublishDir -Filter 'lib*.pdb' | Remove-Item
    New-Item -ItemType Directory -Force -Path (Join-Path $PublishDir 'cli') | Out-Null
    Set-Content (Join-Path $PublishDir 'cli\filee.cmd') "@`"%~dp0..\filee-cli.exe`" %*" -Encoding ascii
    # Next to Filee.exe: the Explorer menu package's external location is the install folder.
    & (Join-Path $PSScriptRoot 'build-explorer-menu.ps1') -Destination $PublishDir -Version $Version
    # Only the small rhwp and 7-Zip are bundled; the app downloads the others when the user picks them. The Microsoft
    # C++ runtime goes next to rhwp.exe and next to Filee.exe (unhwp.dll imports VCRUNTIME140.dll), so neither needs
    # the Visual C++ Redistributable (#248).
    & (Join-Path $PSScriptRoot 'fetch-engines.ps1') -Only rhwp, 7zip -Destination (Join-Path $PublishDir 'engines') -RuntimeTo $PublishDir
}
$PublishDir = (Resolve-Path $PublishDir).Path
if (-not (Test-Path (Join-Path $PublishDir 'Filee.exe'))) { throw "Filee.exe is missing in $PublishDir" }
if (-not $CheckOnly) {
    # Without them rhwp and unhwp fail on PCs without the Visual C++ Redistributable (#248). They go into the payload
    # and the portable zip; Setup checks again after unpacking (RuntimeCheck) on the user's PC.
    foreach ($dll in 'vcruntime140.dll', 'engines\rhwp\msvcp140.dll', 'engines\rhwp\vcruntime140.dll') {
        if (-not (Test-Path (Join-Path $PublishDir $dll))) { throw "$dll is missing in $PublishDir (see fetch-engines.ps1 -RuntimeTo)" }
    }
    # A leftover from a run of the old installer or of Setup would make the zip an "installed" copy.
    foreach ($file in 'uninstall.exe', 'uninstall.json', 'unins000.exe') {
        if (Test-Path (Join-Path $PublishDir $file)) { throw "$file must not be in $PublishDir" }
    }
}

# Setup: one self-contained, trimmed program (src/Filee.Setup).
if (-not $SetupProgram) {
    $setupFolder = Join-Path $cache 'setup'
    Remove-Item $setupFolder -Recurse -Force -ErrorAction SilentlyContinue
    Invoke-Checked 'dotnet publish (setup)' {
        dotnet publish (Join-Path $root 'src\Filee.Setup\Filee.Setup.csproj') -c Release -r win-x64 -p:FileeSetupPublish=true `
            "-p:Version=$Version" -o $setupFolder
    }
    $SetupProgram = Join-Path $setupFolder 'Filee.Setup.exe'
}
$SetupProgram = (Resolve-Path $SetupProgram).Path

# Signed before packing: the app's files are signed inside the payload, and the Setup program's signature carries
# over to the uninstaller (SetupPayload). Setup.exe itself is signed after the payload is appended.
$sign = if ($CertificateThumbprint -and -not $CheckOnly) { Join-Path $PSScriptRoot 'sign-windows.ps1' }
if ($sign) {
    & $sign -Path $PublishDir, $SetupProgram -CertificateThumbprint $CertificateThumbprint
}

$setupExe = Join-Path $OutputDir "Filee-$Version-win-Setup.exe"
Write-Host "pack installer $Version"
$fast = @(if ($CheckOnly) { '--fast' })
Push-Location $root
try {
    Invoke-Checked 'make-setup' { dotnet run --no-cache build/tools/make-setup.cs -- $SetupProgram $PublishDir $Version $setupExe @fast }
}
finally {
    Pop-Location
}
if (-not (Test-Path $setupExe)) { throw "make-setup did not write $setupExe" }
if ($sign) {
    & $sign -Path $setupExe -CertificateThumbprint $CertificateThumbprint
}

if ($CheckOnly) {
    # Install, update and uninstall in a sandbox: a registry file and folders of its own, no administrator rights.
    $sandbox = Join-Path $check 'sandbox'
    function Invoke-Setup([string]$exe, [string[]]$arguments) {
        $process = Start-Process -FilePath $exe -ArgumentList ($arguments + @('/VERYSILENT', '/SUPPRESSMSGBOXES', "/SANDBOX=`"$sandbox`"")) -Wait -PassThru
        if ($process.ExitCode -ne 0) {
            Get-ChildItem (Join-Path $sandbox 'Temp') -Filter '*.log' -ErrorAction SilentlyContinue | ForEach-Object { Get-Content $_.FullName }
            throw "$(Split-Path $exe -Leaf) $arguments failed with exit code $($process.ExitCode)"
        }
    }
    $installed = Join-Path $sandbox 'Program Files\Filee'
    Invoke-Setup $setupExe @('/MERGETASKS=desktopicon')
    foreach ($file in 'Filee.exe', 'cli\filee.cmd', 'engines\rhwp\rhwp.exe', 'uninstall.exe', 'uninstall.json') {
        if (-not (Test-Path (Join-Path $installed $file))) { throw "Setup did not install $file" }
    }
    $registry = Get-Content (Join-Path $sandbox 'registry.json') -Raw
    if ($registry -notmatch 'C8686E06-8327-40A2-B0F7-3DEDD61D9248\}_is1' -or $registry -notmatch 'uninstall\.exe') { throw 'Setup did not write the uninstall entry' }
    if (-not (Test-Path (Join-Path $sandbox 'Public\Desktop\Filee.lnk'))) { throw 'Setup did not create the desktop shortcut' }
    Invoke-Setup $setupExe @()
    Invoke-Setup (Join-Path $installed 'uninstall.exe') @()
    if (Test-Path $installed) { throw "The uninstaller left $installed" }
    if ((Get-Content (Join-Path $sandbox 'registry.json') -Raw) -match '_is1') { throw 'The uninstaller left the uninstall entry' }
    Write-Host "Setup installs, updates and uninstalls (sandbox: $sandbox)"
    return
}

$files = @($setupExe)
if ($Portable) {
    $zip = Join-Path $OutputDir "Filee-$Version-win-Portable.zip"
    Remove-Item $zip -ErrorAction SilentlyContinue
    Write-Host "zip  $zip"
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($PublishDir, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
    $files += $zip
}

# Checksums for people who verify their download.
$sums = Join-Path $OutputDir "Filee-$Version-SHA256SUMS.txt"
$files | ForEach-Object { "$((Get-FileHash $_ -Algorithm SHA256).Hash.ToLowerInvariant())  $(Split-Path $_ -Leaf)" } |
    Set-Content $sums -Encoding ascii
Get-Item ($files + $sums) | Format-Table Name, Length
