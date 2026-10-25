<#
.SYNOPSIS
  Signs Windows programs with Filee's code signing certificate: Authenticode, SHA-256, with a timestamp from Certum.

.DESCRIPTION
  Signs every .exe and .dll under -Path (files or folders) that has no valid signature yet: Filee's own programs, and
  the bundled ones that come unsigned (rhwp, 7-Zip, most NuGet libraries). Files their makers signed (.NET, Microsoft,
  ImageMagick …) keep their signature. Afterwards every program under -Path must have a valid signature.

  The certificate's key is in Certum's SimplySign cloud: sign in to SimplySign Desktop (with the token from the
  SimplySign phone app) before running this. signtool comes from the Microsoft.Windows.SDK.BuildTools NuGet package,
  downloaded once into build/.cache.

.PARAMETER CertificateThumbprint
  SHA-1 thumbprint of the certificate ("Cert Hash(sha1)" in certutil -user -store My). Defaults to
  $env:FILEE_SIGNING_CERT.

.EXAMPLE
  pwsh build/sign-windows.ps1 -Path publish\win-x64 -CertificateThumbprint <sha1>
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string[]]$Path,
    [string]$CertificateThumbprint = $env:FILEE_SIGNING_CERT
)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Authenticode signing needs Windows.' }
if (-not $CertificateThumbprint) { throw 'Pass -CertificateThumbprint or set FILEE_SIGNING_CERT.' }
$CertificateThumbprint = $CertificateThumbprint -replace '\s', ''

$certificate = Get-Item "Cert:\CurrentUser\My\$CertificateThumbprint" -ErrorAction SilentlyContinue
if (-not $certificate) {
    throw "Certificate $CertificateThumbprint is not in the current user's store. Sign in to SimplySign Desktop first."
}
if ($certificate.NotAfter -lt (Get-Date)) { throw "Certificate $CertificateThumbprint expired on $($certificate.NotAfter)." }

# signtool from the Windows SDK build tools, pinned by version and SHA-256 like the engines (fetch-engines.ps1).
$sdkVersion = '10.0.28000.2526'
$sdkSha256 = 'a09a4c9d68160ced4765137a9a7444ea560ea86c45d6a77093dea58c2f7563a0'
$toolFolder = Join-Path $PSScriptRoot ".cache\signtool-$sdkVersion"
$signtool = Join-Path $toolFolder 'signtool.exe'
if (-not (Test-Path $signtool)) {
    $package = Join-Path $PSScriptRoot ".cache\microsoft.windows.sdk.buildtools.$sdkVersion.nupkg"
    New-Item -ItemType Directory -Force -Path (Split-Path $package) | Out-Null
    Write-Host "get  Microsoft.Windows.SDK.BuildTools $sdkVersion (signtool)"
    Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/microsoft.windows.sdk.buildtools/$sdkVersion/microsoft.windows.sdk.buildtools.$sdkVersion.nupkg" -OutFile $package
    $actual = (Get-FileHash $package -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $sdkSha256) {
        Remove-Item $package
        throw "Microsoft.Windows.SDK.BuildTools $sdkVersion has SHA-256 $actual, expected $sdkSha256."
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($package)
    try {
        New-Item -ItemType Directory -Force -Path $toolFolder | Out-Null
        foreach ($entry in $zip.Entries | Where-Object { $_.FullName -match '^bin/[^/]+/x64/[^/]+$' }) {
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $toolFolder $entry.Name), $true)
        }
    }
    finally {
        $zip.Dispose()
    }
    if (-not (Test-Path $signtool)) { throw "signtool.exe is not in Microsoft.Windows.SDK.BuildTools $sdkVersion." }
}

$programs = @(foreach ($item in $Path) {
        $resolved = (Resolve-Path $item).Path
        if (Test-Path $resolved -PathType Container) {
            Get-ChildItem $resolved -Recurse -File -Include *.exe, *.dll | ForEach-Object FullName
        }
        else {
            $resolved
        }
    })
$unsigned = @($programs | Where-Object { (Get-AuthenticodeSignature $_).Status -ne 'Valid' })
Write-Host "sign $($unsigned.Count) of $($programs.Count) programs (the others are signed by their makers)"

# Several files per signtool run: each one is still signed in the cloud, but the session is opened once.
$batch = 40
$options = @('sign', '/sha1', $CertificateThumbprint, '/fd', 'sha256', '/tr', 'http://time.certum.pl', '/td', 'sha256',
    '/d', 'Filee', '/du', 'https://filee.sh', '/q')
for ($i = 0; $i -lt $unsigned.Count; $i += $batch) {
    $files = @($unsigned[$i..([Math]::Min($i + $batch, $unsigned.Count) - 1)])
    & $signtool @options @files
    if ($LASTEXITCODE -ne 0) {
        # The cloud or the timestamp server can drop a request: once more for what is still unsigned.
        $files = @($files | Where-Object { (Get-AuthenticodeSignature $_).Status -ne 'Valid' })
        & $signtool @options @files
        if ($LASTEXITCODE -ne 0) { throw "signtool failed with exit code $LASTEXITCODE" }
    }
    Write-Host "  $([Math]::Min($i + $batch, $unsigned.Count))/$($unsigned.Count)"
}

$bad = @($programs | Where-Object { (Get-AuthenticodeSignature $_).Status -ne 'Valid' })
if ($bad.Count) { throw "Not signed: $($bad -join ', ')" }
Write-Host "all $($programs.Count) programs are signed"
