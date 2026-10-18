[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')][string]$Runtime,
    [Parameter(Mandatory)][string]$Destination,
    [ValidateSet('rhwp', '7zip')][string[]]$Only = @('rhwp', '7zip')
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$manifestPath = Join-Path $PSScriptRoot '../src/Filee.Engines/Infrastructure/engines.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
$components = $manifest.platforms[$Runtime].components
$cache = Join-Path $PSScriptRoot '.cache/portable-downloads'
New-Item -ItemType Directory -Force -Path $cache, $Destination | Out-Null
$Destination = (Resolve-Path -LiteralPath $Destination).Path

foreach ($name in $Only) {
    $component = $components[$name]
    if (-not $component -or $component.sha256 -notmatch '^[0-9a-fA-F]{64}$') {
        throw "A pinned $name download is missing for $Runtime."
    }
    $target = Join-Path $Destination $name
    if (Test-Path -LiteralPath $target) {
        throw "Engine destination already exists: $target. Use a fresh publish folder."
    }
    $archive = Join-Path $cache "$($component.sha256).$($component.kind)"
    if (-not (Test-Path -LiteralPath $archive)) {
        $download = "$archive.$([Guid]::NewGuid().ToString('N')).partial"
        Invoke-WebRequest -Uri $component.url -OutFile $download
        if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ne $component.sha256) {
            Remove-Item -LiteralPath $download
            throw "Checksum mismatch for $name ($Runtime)."
        }
        Move-Item -LiteralPath $download -Destination $archive
    }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $component.sha256) {
        throw "Cached engine checksum mismatch: $archive"
    }
    $members = @(& tar -tf $archive)
    if ($LASTEXITCODE -ne 0) { throw "Cannot read $archive" }
    foreach ($member in $members) {
        if ($member -match '^[/\\]' -or $member -match '(^|[/\\])\.\.([/\\]|$)' -or $member.Contains(':')) {
            throw "Unsafe archive member: $member"
        }
    }
    # These two bundles contain only files and directories; reject links before tar can create them.
    $details = @(& tar -tvf $archive)
    if ($LASTEXITCODE -ne 0) { throw "Cannot inspect entry types in $archive" }
    foreach ($entry in $details) {
        if ($entry -notmatch '^[-d]') {
            throw "Unsupported archive entry (links and special files are not allowed): $entry"
        }
    }
    $stage = Join-Path $cache ('extract-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $stage | Out-Null
    & tar -xf $archive -C $stage
    if ($LASTEXITCODE -ne 0) { throw "Cannot extract $archive" }
    $children = @(Get-ChildItem -LiteralPath $stage -Force)
    $payload = if ($children.Count -eq 1 -and $children[0].PSIsContainer) { $children[0].FullName } else { $stage }
    $executable = if ($name -eq 'rhwp') { 'rhwp' } else { '7zz' }
    if (-not (Test-Path -LiteralPath (Join-Path $payload $executable) -PathType Leaf)) {
        throw "$executable was not found at the expected location in $archive"
    }
    Move-Item -LiteralPath $payload -Destination $target
    if (-not $IsWindows) {
        & chmod +x (Join-Path $target $executable)
        if ($LASTEXITCODE -ne 0) { throw "Could not make $executable executable." }
    }
    if (Test-Path -LiteralPath $stage) {
        Remove-Item -LiteralPath $stage
    }
    Write-Host "$name ready for $Runtime"
}
