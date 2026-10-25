<#
.SYNOPSIS
  Downloads the Noto Sans fonts (SIL Open Font License 1.1) used by the Filee UI.

.DESCRIPTION
  Fonts are not committed to git (≈36 MB). Run this once after cloning, and CI runs it before publishing.
  Without the fonts the app still works and falls back to system fonts (Malgun Gothic, Microsoft YaHei, ...).

  Static font files are used because Avalonia does not support variable fonts.
  Every file is pinned to a tag/commit and verified with SHA-256.

.EXAMPLE
  pwsh build/fetch-fonts.ps1
#>
[CmdletBinding()]
param(
    [string]$Destination = (Join-Path $PSScriptRoot '..\src\Filee.App\Assets\Fonts')
)

$ErrorActionPreference = 'Stop'
$noto = 'https://raw.githubusercontent.com/notofonts/notofonts.github.io/28b15b4b43b7bed62b5cf6e6b0b5ff5846270535/fonts'
$latin = "$noto/NotoSans/hinted/ttf"
$devanagari = "$noto/NotoSansDevanagari/hinted/ttf"
$arabic = "$noto/NotoSansArabic/hinted/ttf"
$thai = "$noto/NotoSansThai/hinted/ttf"
$cjk = 'https://raw.githubusercontent.com/notofonts/noto-cjk/Sans2.004/Sans/SubsetOTF'

# File name => (URL, SHA-256). Update both together when bumping a version.
$fonts = [ordered]@{
    'NotoSansArabic-Regular.ttf'    = @("$arabic/NotoSansArabic-Regular.ttf", 'C270782EE478C685F95509E724125729E1284C82EBFBE414CC67926F29449E1D')
    'NotoSansArabic-Medium.ttf'     = @("$arabic/NotoSansArabic-Medium.ttf", '2730BADCE9D162C8203B28591893A1BC8F126454C5B16AB16A2020C2AECE4F40')
    'NotoSansArabic-Bold.ttf'       = @("$arabic/NotoSansArabic-Bold.ttf", 'CABDC065D2D3D30562CF1D3E753DC7062BC420BEB14942E0C28A211020E2B8D1')
    'NotoSans-Regular.ttf'           = @("$latin/NotoSans-Regular.ttf", '478C558EA716033CD60C03438F628DFA75694DCF6B5F6D505A2F05FD2B4F3823')
    'NotoSans-Medium.ttf'            = @("$latin/NotoSans-Medium.ttf", '635D93D1131D791F2576DE90B3BB0F7CDF61929906E8420A61B5F7F8E76420BB')
    'NotoSans-Bold.ttf'              = @("$latin/NotoSans-Bold.ttf", '1DF075A380FC7CB898ACF64C1F7B3B4DD780DE3CAA860178BF929DE35817A913')
    'NotoSansDevanagari-Regular.ttf' = @("$devanagari/NotoSansDevanagari-Regular.ttf", '306B53ECFB182A504DD8A7446093C316387D2FD8DC350D0792ED1753FE0996CD')
    'NotoSansDevanagari-Medium.ttf'  = @("$devanagari/NotoSansDevanagari-Medium.ttf", '4F46CF50B7992E61C7CEE7C4A8284450CCEED8B540B8ADBC8DF637C982C65757')
    'NotoSansDevanagari-Bold.ttf'    = @("$devanagari/NotoSansDevanagari-Bold.ttf", '3AD8362A06271814869838DCC3D161B13C9FB97681B627AF1F7F283EA9387D56')
    'NotoSansThai-Regular.ttf'       = @("$thai/NotoSansThai-Regular.ttf", '61CF814EEC46B294D6EA4401AC295D0CECD5207BD2331DCC5A15E7301D30EE44')
    'NotoSansThai-Medium.ttf'        = @("$thai/NotoSansThai-Medium.ttf", '11BC8F6CF3523241995F07225FCC7D8E25EB55064FBFA39281A5E44DCD59F5F5')
    'NotoSansThai-Bold.ttf'          = @("$thai/NotoSansThai-Bold.ttf", '2AC6C6E8A478E23B15F76E4894AF1FA2210F8F350E4E6E54AAD530BEC03EFBFB')
    'NotoSansJP-Regular.otf'         = @("$cjk/JP/NotoSansJP-Regular.otf", 'DFF723BA59D57D136764A04B9B2D03205544F7CD785A711442D6D2D085AC5073')
    'NotoSansJP-Bold.otf'            = @("$cjk/JP/NotoSansJP-Bold.otf", '1B0EDFB500B73A4FA8A4FCAAE1BBBD403994E08E73E3E0DA37E70D3853F42C5F')
    'NotoSansKR-Regular.otf'         = @("$cjk/KR/NotoSansKR-Regular.otf", '69975A0AC8472717870AEFEAB0A4D52739308D90856B9955313B2AD5E0148D68')
    'NotoSansKR-Bold.otf'            = @("$cjk/KR/NotoSansKR-Bold.otf", '5A6CEB287ED2FC6CFC6213144EBEA68CBD94B20FC9EB873D8486493BF02D9BDA')
    'NotoSansSC-Regular.otf'         = @("$cjk/SC/NotoSansSC-Regular.otf", 'FAA6C9DF652116DDE789D351359F3D7E5D2285A2B2A1F04A2D7244DF706D5EA9')
    'NotoSansSC-Bold.otf'            = @("$cjk/SC/NotoSansSC-Bold.otf", 'C6CB5A93ABAA9EDC8EE7463B7EBB7F42D618D40E6ED2F7A5371C97B0B64767C0')
}

New-Item -ItemType Directory -Force -Path $Destination | Out-Null

foreach ($name in $fonts.Keys) {
    $url, $expected = $fonts[$name]
    $target = Join-Path $Destination $name

    if (Test-Path $target) {
        $actual = (Get-FileHash $target -Algorithm SHA256).Hash
        if ($actual -eq $expected) {
            Write-Host "ok   $name (cached)"
            continue
        }
    }

    Write-Host "get  $name"
    Invoke-WebRequest -Uri $url -OutFile $target -UseBasicParsing
    $actual = (Get-FileHash $target -Algorithm SHA256).Hash
    if ($actual -ne $expected) {
        Remove-Item $target
        throw "Checksum mismatch for $name. Expected $expected, got $actual."
    }
}

Write-Host "Fonts ready in $Destination"
