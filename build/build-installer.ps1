<#
.SYNOPSIS
  Builds the Windows installer Filee-<version>-win-Setup.exe (installer/Filee.iss) and, with -Portable, the portable
  zip, into -OutputDir.

.DESCRIPTION
  Without -PublishDir the app is published first (Release, self-contained, -p:FileeRelease=true) into
  publish/win-x64, together with the Windows 11 Explorer menu extension (build-explorer-menu.ps1) and the bundled
  small engines rhwp and 7-Zip (fetch-engines.ps1); the large engines are downloaded by the app (engines.json).

  Inno Setup 7 is downloaded once into build/.cache (pinned, SHA-256 verified) and set up there in its own portable
  mode: nothing is installed or registered on the build machine. The installer's engine page is generated from
  EngineDownloads by build/tools/make-installer-engines.cs.

.PARAMETER CheckOnly
  Compiles the script against a stub app without compression, to check installer/Filee.iss quickly (CI).

.EXAMPLE
  pwsh build/build-installer.ps1
  pwsh build/build-installer.ps1 -Version 1.2.0 -Portable
  pwsh build/build-installer.ps1 -CheckOnly
#>
[CmdletBinding()]
param(
    # Defaults to <Version> in Directory.Build.props.
    [string]$Version,
    # A finished publish folder (Filee.exe, FileeExplorerMenu.dll/.msix, engines\); skips publishing.
    [string]$PublishDir,
    [string]$OutputDir = (Join-Path $PSScriptRoot '..\Releases'),
    [switch]$Portable,
    [switch]$CheckOnly
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$cache = Join-Path $PSScriptRoot '.cache'

# Inno Setup 7.1.0, 64-bit edition. Update the version, hash and size together
# (hashes: https://github.com/jrsoftware/issrc/releases, "Verifying Inno Setup Downloads" on jrsoftware.org).
$innoVersion = '7.1.0'
$innoSha256 = '0362A383ED217D4C4239B5933866DD96D3EB2102737DA92F80F6057A4B40DF2F'
$innoSize = 14304168
$innoUrl = "https://github.com/jrsoftware/issrc/releases/download/is-$($innoVersion.Replace('.', '_'))/innosetup-$innoVersion-x64.exe"

function Get-Iscc {
    $folder = Join-Path $cache "innosetup-$innoVersion"
    $iscc = Join-Path $folder 'ISCC.exe'
    if (Test-Path $iscc) { return $iscc }

    New-Item -ItemType Directory -Force -Path $cache | Out-Null
    $setup = Join-Path $cache "innosetup-$innoVersion-x64.exe"
    if (-not ((Test-Path $setup) -and (Get-FileHash $setup -Algorithm SHA256).Hash -eq $innoSha256)) {
        Write-Host "get  $innoUrl"
        & curl.exe --fail --location --silent --show-error --connect-timeout 20 --retry 3 --max-time 300 --output $setup $innoUrl
        if ($LASTEXITCODE -ne 0) { throw "curl exit code $LASTEXITCODE" }
        $hash = (Get-FileHash $setup -Algorithm SHA256).Hash
        if ($hash -ne $innoSha256) {
            Remove-Item $setup
            throw "Inno Setup checksum mismatch (got $hash, size $((Get-Item $setup).Length), expected $innoSize bytes)"
        }
    }

    # /PORTABLE=1 is Inno Setup's own portable mode: no uninstaller, no file association, no shortcuts, no registry.
    # /CURRENTUSER keeps it from asking for administrator rights.
    Write-Host "unpack Inno Setup $innoVersion (portable)"
    $partial = "$folder.partial"
    Remove-Item $partial -Recurse -Force -ErrorAction SilentlyContinue
    $process = Start-Process -FilePath $setup -Wait -PassThru -ArgumentList @(
        '/PORTABLE=1', '/CURRENTUSER', '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/NOICONS',
        "/DIR=`"$partial`"", "/LOG=`"$cache\innosetup-$innoVersion.log`"")
    if ($process.ExitCode -ne 0 -or -not (Test-Path (Join-Path $partial 'ISCC.exe'))) {
        throw "Inno Setup setup failed with exit code $($process.ExitCode) (see $cache\innosetup-$innoVersion.log)"
    }
    Move-Item $partial $folder
    return $iscc
}

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
    # The script only needs a folder with Filee.exe in it.
    $PublishDir = Join-Path $cache 'installer-check\app'
    New-Item -ItemType Directory -Force -Path $PublishDir | Out-Null
    Set-Content (Join-Path $PublishDir 'Filee.exe') 'stub' -NoNewline
    $OutputDir = Join-Path $cache 'installer-check'
}
elseif (-not $PublishDir) {
    $PublishDir = Join-Path $root 'publish\win-x64'
    Remove-Item $PublishDir -Recurse -Force -ErrorAction SilentlyContinue
    Invoke-Checked 'dotnet publish' {
        dotnet publish (Join-Path $root 'src\Filee.App\Filee.App.csproj') -c Release -r win-x64 --self-contained `
            "-p:Version=$Version" -p:PublishReadyToRun=true -p:FileeRelease=true -o $PublishDir
    }
    # The command line, in the same folder so it shares the runtime and the engines (filee-cli.exe). Its name can't
    # be filee.exe next to Filee.exe, so the "filee" command is cli\filee.cmd, which the installer can put on PATH.
    Invoke-Checked 'dotnet publish (cli)' {
        dotnet publish (Join-Path $root 'src\Filee.Cli\Filee.Cli.csproj') -c Release -r win-x64 --self-contained `
            "-p:Version=$Version" -p:PublishReadyToRun=true -p:FileeRelease=true -o $PublishDir
    }
    New-Item -ItemType Directory -Force -Path (Join-Path $PublishDir 'cli') | Out-Null
    Set-Content (Join-Path $PublishDir 'cli\filee.cmd') "@`"%~dp0..\filee-cli.exe`" %*" -Encoding ascii
    # Next to Filee.exe: the Explorer menu package's external location is the install folder.
    & (Join-Path $PSScriptRoot 'build-explorer-menu.ps1') -Destination $PublishDir -Version $Version
    # Only the small rhwp and 7-Zip are bundled; the app downloads the others when the user picks them.
    & (Join-Path $PSScriptRoot 'fetch-engines.ps1') -Only rhwp, 7zip -Destination (Join-Path $PublishDir 'engines')
}
$PublishDir = (Resolve-Path $PublishDir).Path
if (-not (Test-Path (Join-Path $PublishDir 'Filee.exe'))) { throw "Filee.exe is missing in $PublishDir" }

$iscc = Get-Iscc
Push-Location $root
try {
    Invoke-Checked 'make-installer-engines' { dotnet run build/tools/make-installer-engines.cs -- installer/obj/engines.iss }
}
finally {
    Pop-Location
}

Write-Host "compile installer $Version"
$options = @('/Q', "/DAppVersion=$Version", "/DPublishDir=$PublishDir", "/O$OutputDir")
if ($CheckOnly) { $options += '--no-compression' }
Invoke-Checked 'ISCC' { & $iscc @options (Join-Path $root 'installer\Filee.iss') }
$setupExe = Join-Path $OutputDir "Filee-$Version-win-Setup.exe"
if (-not (Test-Path $setupExe)) { throw "ISCC did not write $setupExe" }
if ($CheckOnly) {
    Write-Host "installer/Filee.iss compiles"
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
