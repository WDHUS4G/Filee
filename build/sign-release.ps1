<#
.SYNOPSIS
  Signs a release's Windows files and adds them to its draft on GitHub: run on a PC signed in to SimplySign Desktop
  once the release workflow of the tag has finished.

.DESCRIPTION
  The release workflow tests and builds the tag, keeps the unsigned Windows build (artifact windows-unsigned: the
  publish folder and the Setup program) and drafts the release with the Linux and macOS packages. This script:

  1. downloads that build, exactly as it was tested;
  2. checks out the tag in a worktree and runs its build-installer.ps1 with -CertificateThumbprint, which signs the
     unsigned programs and the Setup program, packs Setup.exe, signs it, zips the portable copy and writes the
     SHA-256 sums;
  3. installs the signed Setup into a sandbox (nothing on the PC is touched) and checks that Filee.exe and
     uninstall.exe come out signed, then uninstalls it;
  4. uploads Filee-<version>-win-Setup.exe, the portable zip and Filee-<version>-SHA256SUMS.txt to the draft, and
     with -Publish publishes the release.

  Needs gh (signed in), git and the .NET SDK. Output: build/.cache/release-<version>/out.

.EXAMPLE
  pwsh build/sign-release.ps1 -Version 2.1.0 -CertificateThumbprint <sha1>
  pwsh build/sign-release.ps1 -Version 2.1.0 -Publish
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Version,
    # Defaults to $env:FILEE_SIGNING_CERT (see sign-windows.ps1).
    [string]$CertificateThumbprint = $env:FILEE_SIGNING_CERT,
    [switch]$Publish
)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Windows releases are signed on Windows.' }
if (-not $CertificateThumbprint) { throw 'Pass -CertificateThumbprint or set FILEE_SIGNING_CERT.' }
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$Version = $Version.TrimStart('v')
$tag = "v$Version"

function Invoke-Checked([string]$what, [scriptblock]$command) {
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$what failed with exit code $LASTEXITCODE" }
}

# The draft and the run that made it.
$release = gh release view $tag --json isDraft | ConvertFrom-Json
if (-not $release.isDraft) { throw "$tag is already published." }
$run = gh run list --workflow release.yml --branch $tag --limit 1 --json databaseId,status,conclusion | ConvertFrom-Json | Select-Object -First 1
if (-not $run) { throw "No release workflow run for $tag." }
if ($run.status -ne 'completed' -or $run.conclusion -ne 'success') { throw "The release run $($run.databaseId) is $($run.status) ($($run.conclusion))." }

$work = Join-Path $PSScriptRoot ".cache\release-$Version"
$source = Join-Path $work 'source'
if (Test-Path $source) { Invoke-Checked 'git worktree remove' { git -C $root worktree remove --force $source } }
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $work | Out-Null

Write-Host "download the unsigned build of run $($run.databaseId)"
Invoke-Checked 'gh run download' { gh run download $run.databaseId --name windows-unsigned --dir (Join-Path $work 'unsigned') }

Invoke-Checked 'git fetch' { git -C $root fetch --quiet origin "refs/tags/${tag}:refs/tags/$tag" }
Invoke-Checked 'git worktree add' { git -C $root worktree add --detach $source $tag }
try {
    $out = Join-Path $work 'out'
    & (Join-Path $source 'build\build-installer.ps1') -Version $Version -Portable -OutputDir $out `
        -PublishDir (Join-Path $work 'unsigned\app') -SetupProgram (Join-Path $work 'unsigned\Filee.Setup.exe') `
        -CertificateThumbprint $CertificateThumbprint
}
finally {
    git -C $root worktree remove --force $source
}

$setup = Join-Path $out "Filee-$Version-win-Setup.exe"
$files = @($setup, (Join-Path $out "Filee-$Version-win-Portable.zip"), (Join-Path $out "Filee-$Version-SHA256SUMS.txt"))
foreach ($file in $files) { if (-not (Test-Path $file)) { throw "$file was not written." } }

# The signed Setup still finds its payload, and what it installs is signed, the uninstaller too.
$sandbox = Join-Path $work 'sandbox'
function Invoke-Setup([string]$exe) {
    $process = Start-Process -FilePath $exe -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', "/SANDBOX=`"$sandbox`"") -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "$(Split-Path $exe -Leaf) failed in the sandbox with exit code $($process.ExitCode) (logs: $sandbox\Temp)" }
}
Write-Host 'install the signed Setup into a sandbox'
Invoke-Setup $setup
$installed = Join-Path $sandbox 'Program Files\Filee'
foreach ($file in 'Filee.exe', 'filee-cli.exe', 'uninstall.exe', 'engines\rhwp\rhwp.exe') {
    $signature = Get-AuthenticodeSignature (Join-Path $installed $file)
    if ($signature.Status -ne 'Valid') { throw "$file is not signed after installing ($($signature.Status))." }
}
Invoke-Setup (Join-Path $installed 'uninstall.exe')
Remove-Item $sandbox -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "upload to the draft of $tag"
Invoke-Checked 'gh release upload' { gh release upload $tag @files --clobber }
if ($Publish) {
    Invoke-Checked 'gh release edit' { gh release edit $tag --draft=false --latest }
    Write-Host "published $tag"
}
else {
    Write-Host "$tag is still a draft: check it on GitHub, then publish it there or run again with -Publish."
}
