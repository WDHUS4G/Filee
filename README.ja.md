<p align="center">
  <a href="https://filee.sh"><img src="docs/media/banner-en.jpg" alt="Filee：ドラッグ。ドロップ。変換完了。" width="100%"></a>
</p>

<p align="center">
  <b>ドラッグで使う、無料のオープンソースのファイル変換ツール。</b><br>
  キー（<kbd>Ctrl</kbd>、macOSでは<kbd>Option</kbd>）を押しながらファイルをドラッグし、カーソル位置に開くドーナツメニューの形式にドロップするだけ。
</p>

<p align="center">
  <a href="https://filee.sh"><b>ウェブサイト</b></a> ·
  <a href="https://github.com/WDHUS4G/Filee/releases/latest"><b>ダウンロード</b></a> ·
  <a href="https://www.linkedin.com/company/filee-sh/"><b>LinkedIn</b></a> ·
  <a href="#インストール">インストール</a> ·
  <a href="#コマンドライン">コマンドライン</a> ·
  <a href="#開発">開発</a>
</p>

<p align="center">
  <a href="https://github.com/WDHUS4G/Filee/actions/workflows/ci.yml"><img src="https://github.com/WDHUS4G/Filee/actions/workflows/ci.yml/badge.svg" alt="CI"></a>
  <a href="https://github.com/WDHUS4G/Filee/releases/latest"><img src="https://img.shields.io/github/v/release/WDHUS4G/Filee?color=7F77DD&label=release" alt="最新リリース"></a>
  <a href="https://github.com/WDHUS4G/Filee/releases"><img src="https://img.shields.io/github/downloads/WDHUS4G/Filee/total?color=1D9E75" alt="ダウンロード数"></a>
  <img src="https://img.shields.io/badge/Windows-10%20%7C%2011-3C3489" alt="Windows 10 | 11">
  <img src="https://img.shields.io/badge/Linux-x64%20%7C%20ARM64-3C3489" alt="Linux x64 | ARM64">
  <img src="https://img.shields.io/badge/macOS-preview-534AB7" alt="macOS プレビュー版">
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-6A62C9" alt="MITライセンス"></a>
</p>

<p align="center">
  <a href="https://trendshift.io/repositories/276987"><img src="https://trendshift.io/api/badge/trendshift/repositories/276987/weekly?language=C%23&v=2" alt="Filee: #1 C# repository of the week on Trendshift" width="250" height="55"></a>
  <a href="https://trendshift.io/repositories/276987"><img src="https://trendshift.io/api/badge/trendshift/repositories/276987/daily?language=C%23&v=2" alt="Filee: #1 C# repository of the day on Trendshift" width="250" height="55"></a>
</p>

<p align="center">
  <a href="README.md">English</a> ·
  <a href="README.es.md">Español</a> ·
  <b>日本語</b> ·
  <a href="README.ko.md">한국어</a> ·
  <a href="README.zh-CN.md">简体中文</a> ·
  <a href="README.zh-TW.md">繁體中文</a> ·
  <a href="README.pl.md">Polski</a> ·
  <a href="README.hi.md">हिन्दी</a> ·
  <a href="README.ru.md">Русский</a> ·
  <a href="README.ar.md">العربية</a> ·
  <a href="README.pt-BR.md">Português (Brasil)</a> ·
  <a href="README.pt-PT.md">Português (Portugal)</a> ·
  <a href="README.fr.md">Français</a> ·
  <a href="README.th.md">ไทย</a>
</p>

<table>
  <tr>
    <td width="33%" valign="top">
      <b>カーソルのすぐそばで</b><br>
      アプリのウィンドウを開く必要はありません。
      いま作業している場所に、ドラッグ中のファイルに合う形式だけが表示されます。
    </td>
    <td width="33%" valign="top">
      <b>263形式、ほかのソフトは不要</b><br>
      画像、PDF、Word、Excel、PowerPoint、HWP、電子書籍、アーカイブ、フォント、CAD、3Dモデル、地理空間データを、Office、Hancom Office、LibreOfficeなしで変換できます。
    </td>
    <td width="33%" valign="top">
      <b>すべて手元のPCで完結</b><br>
      何もアップロードしません。
      Fileeはオフラインで動作し、変換結果を元のファイルと同じ場所に保存します。
    </td>
  </tr>
</table>

## 動作の様子

<p align="center">
  <a href="https://filee.sh"><img src="docs/media/demo-en.webp" alt="Ctrlを押しながらtrip.jpgをドラッグすると、カーソル位置に形式のドーナツメニューが開き、PNGにドロップすると進行状況のリングが一周してtrip.pngができあがる" width="820"></a><br>
  <sub>27秒の動画とインタラクティブなデモは<a href="https://filee.sh">filee.sh</a>で</sub>
</p>

## インストール

[Releases](https://github.com/WDHUS4G/Filee/releases/latest)からダウンロードしてください。

- **Windows 10/11**：`Filee-<version>-win-Setup.exe`を実行するか、`win-Portable.zip`を展開して`Filee.exe`を実行します。
  - セットアップはFilee独自のインストーラーで、Fileeと同じ言語で表示され、PCにほかのソフトは必要ありません。
  - エクスプローラーのメニュー、サインイン時の起動、`filee`コマンド、追加の変換エンジンを選べます。
  - サイレントインストールにはInno Setupと同じスイッチ（`/VERYSILENT /SUPPRESSMSGBOXES`）が使えます。
- **macOS 13以降**（プレビュー版）：`osx-arm64`または`osx-x64`のパッケージを展開し、`./install.sh`を実行します。
  - または`.dmg`を開いてFileeを「アプリケーション」へドラッグします（`filee`コマンドは`install.sh`で追加されます）。
  - まだ公証を受けていないため、初回起動時は「システム設定」→「プライバシーとセキュリティ」→「**このまま開く**」で許可してください。
- **Linux x64/ARM64**（Ubuntu 22.04以降）：パッケージを展開して`./install.sh`を実行します（root権限は不要、Python 3が必要）。
  - ドラッグ操作にはX11が必要です。
    Waylandでは、ドロップ領域とファイルマネージャーの右クリックメニューを使ってください。

新しいバージョンが出るとメニューでお知らせし、設定とエンジンはそのままでFilee自身が更新します。
更新後は、新機能を数枚のスライドで紹介します。

**Windows：Fileeが起動しない（ウィンドウもエラーも出ない）場合**
長い間更新していないWindows 10を、ハードウェア強制スタック保護（CET）に対応したプロセッサーのPCで使っていると起こります。
Windowsはこの保護を有効にしますが、.NETに必要な機能が欠けているため、Fileeとセットアップがすぐに終了します。

- **推奨**：Windows Updateで利用できる更新プログラム（最新の累積更新プログラム）をインストールしてください。
- **更新できない場合**：Fileeだけこの保護をオフにすると、たいていは起動します。
  管理者として開いたPowerShellで次を実行します。

```powershell
Set-ProcessMitigation -Name Filee.exe -Disable UserShadowStack
Set-ProcessMitigation -Name filee-cli.exe -Disable UserShadowStack
```

セットアップも、そのファイル名（例：`Filee-<version>-win-Setup.exe`）で同じように実行してください。
ポータブル版のZIPなら上の2行だけで済みます。
元に戻すには、「Windows セキュリティ」→「アプリとブラウザー コントロール」→「Exploit Protection の設定」→「プログラム設定」で該当する項目を削除します。

## 機能

- **カーソル位置にドーナツメニュー**：キーを押しながらファイルをドラッグし、形式の上にドロップするだけ。
  ファイルに合う形式だけが表示されます。
- **ブラウザーから直接**：Chrome、Edge、Firefox、Safariから画像、ファイルへのリンク、ダウンロードも同じようにドラッグできます。
  - Fileeはダウンロードフォルダーに保存して（変換後のファイルだけを残すこともできます）変換します。
  - リンクはブラウザーのログイン情報を使わずにダウンロードします。
- **ファイルの種類ごとのドーナツメニュー**はドラッグ＆ドロップで並べ替えられ、次の項目をプリセットで設定できます：
  - 品質、サイズ、DPI
  - PDFのページと圧縮
  - 動画と音声
  - 保存先、ファイル名
- **ドーナツメニューの裏面**：開いている間に<kbd>Tab</kbd>キーを押すかマウスホイールを回すと裏返り、使用頻度の低い形式やツールが表示されます。
- **変換の前にトリミング**：「トリミング」で画像を1枚ずつ開けます。
  枠をドラッグして比率を選ぶと、その部分だけを保存します。
- **複数のファイルをまとめて**並列で変換します。
  複数の手順が必要な変換（HWP → HWPX → DOCX）も、経路を自動で見つけます。
- **監視フォルダー**に入ったファイルを、すべて自動で変換します。
- **最近の変換**はホームにサムネイル付きで表示され、変換結果を開いたり、別のアプリで開いたりできます。
- **ほかの使い方**：右クリックメニュー、キーボードショートカット、ドロップ領域、[`filee`コマンド](#コマンドライン)。
- ライトテーマとダークテーマ。
  アクセントカラーとドーナツメニューの不透明度も選べます。
  English、Español、日本語、한국어、简体中文、繁體中文、Polski、हिन्दी、Русский、العربية（右から左へ表示）、Português (Brasil)、Français、नेपाली、ไทย、Türkçe、Português (Portugal)に対応。

## スクリーンショット

<table>
  <tr>
    <td width="50%"><img src="docs/media/page-toolbar-en.png" alt="プロファイルとプリセットを使ったドーナツメニューの編集画面"></td>
    <td width="50%"><img src="docs/media/page-home-en.png" alt="ドロップ領域と最近の変換が表示されたホーム画面"></td>
  </tr>
  <tr>
    <td align="center"><sub>ファイルの種類ごとのプロファイルで、ドーナツメニューをドラッグ＆ドロップで編集</sub></td>
    <td align="center"><sub>ホーム：ドロップ領域と最近の変換</sub></td>
  </tr>
</table>

## 対応形式

| 変換元 | 変換先 |
|---|---|
| **画像**：JPG、PNG、WEBP、AVIF、TIFF、BMP、GIF、APNG、ICO、ICNS、JPEG XL、JPEG 2000、PSD/PSB、TGA、PPM、HEIC、QOI、DDS、PCX、MNG、CUR など。<br>GIMP XCF、カメラRAW（CR2、CR3、NEF、ARW、DNG など）、DICOM、OpenEXR、HDR、FITS、JPEG XR（Windows）、iPhoneのLive Photo（LIVP）は読み込みのみ | 上記の形式どうし（HEIC⁵）、PDF、SVG（トレース）、リサイズ、グレースケール。<br>Live Photo/モーションフォト（iPhone、Android）→ その動画（MP4、MOV⁵）、動画 → Live Photo（iPhone）またはモーションフォト（Android）⁵（試験的） |
| **ベクター画像**：SVG、SVGZ、EMF、WMF、AI<br>EPS、PS¹<br>CDR、VSD、CGM、ODG、FODG² | PDF（ベクター）、PNGなどの画像、<br>SVG ↔ SVGZ、SVG → DXF/DWG、PDF → EPS/PS¹、SVG → EMF、WMF、ODG² |
| **PDF** | DOCX、HWPX、TXT、PNG、JPG、TIFF、CBZ、PPTX（各ページを1枚の画像として）、SVG²。<br>結合（先に順番を選べます）、分割、ページ範囲、圧縮（PDFを軽量化） |
| **文書**：DOCX、DOCM、DOTX、EML、Outlook MSG。<br>OFD（中国の電子インボイス、公文書）は読み込みのみ<br>DOC、ODT、FODT、OTT、RTF、WPS、WPD、Pages、ClarisWorks、MacWrite、T602 など² | PDF、DOCX、HWPX、EPUB、TXT、HTML。<br>MSG → EML<br>ODT、FODT、OTT² |
| **表計算**：XLSX、XLSM、XLS、XLT、ODS、CSV、TSV<br>OTS、FODS、Numbers、ET² | PDF、XLSX、ODS、CSV、TSV、JSON、HWPX、HTML<br>XLT、OTS² |
| **プレゼンテーション**：PPTX、PPTM、POTX、POTM、PPSX<br>PPT、ODP、OTP、FODP、Keynote² | PDF、PNG、JPG、DOCX、HWPX<br>OTP² |
| **HWP**：HWP、HWPX | PDF、PNG、DOCX、HWPX ↔ HWP、TXT、Markdown、HTML |
| **テキスト**：TXT、LOG、スクリプトのソース（PY、PAC、BAT、COMMAND。実行はせず読み込むだけ）、Markdown、HTML。<br>MHTML（保存したウェブページ）は読み込みのみ<br>reStructuredText、LaTeX、Org、AsciiDoc、Typst、MediaWiki、DocBook、Jupyter、Textile、man、OPML³ | UTF-8のTXT、PDF、DOCX、HWPX、EPUB<br>相互変換、PPTXスライド³ |
| **電子書籍**：EPUB、KEPUB、MOBI、AZW3、AZW、AZW4、FB2、CBZ、CBR、CB7、HTMLZ、TXTZ<br>LIT、LRF、CHM、PDB など⁴ | PDF、EPUB、KEPUB（Kobo）、DOCX、TXT、HTML、HWPX<br>MOBI、AZW3⁴ |
| **動画**⁵：MP4、MOV、MKV、WEBM、AVI、WMV、FLV、MPEG、ビデオCD（DAT）、TS、M2TS、3GP、OGV、VOB など | MP4（H.264、HEVC、AV1）、WEBM、MOV、MKV、AVI、GIF、APNG、MP3、M4A、720p、<br>字幕（SRT、ASS、WebVTT） |
| **音声**⁵：MP3、M4A、AAC、WAV、FLAC、OGG、OPUS、WMA、AIFF、AMR、APE、WavPack、TTA、DSD（DSF、DFF）、DTS、AC3、E-AC3 など | MP3、M4A（AAC、ALAC）、WAV、FLAC、OGG、OPUS、AAC、WavPack、TTA、W64、AC3、E-AC3 |
| **字幕**：SRT、ASS/SSA、WebVTT、SAMI（SMI）、LRC、TTML、MicroDVD、SCC | 相互変換、UTF-8で保存（CP949の韓国語ファイルも正しく読み込み） |
| **アーカイブ**：ZIP、ANSYS Workbench（WBPZ）、7Z、RAR、TAR、TAR.GZ、TAR.BZ2、TAR.XZ、GZ、ISO、CAB、DMG、ALZ、EGG など | 展開、ZIP、7Z、TAR、TAR.GZ。<br>複数のファイル → 1つのZIP |
| **CAD**：DWG、DXF | PDF、SVG、PNG、DWG ↔ DXF |
| **フォント**：TTF、OTF、WOFF、WOFF2、EOT | 相互変換 |
| **データ**：JSON、JSON Lines、YAML、TOML、XML<br>vCard、iCalendar | 相互変換、CSVとXLSX（表形式のデータ）。<br>vCard、iCalendar ↔ CSV |
| **3Dモデル**：GLB、glTF、STL、OBJ（+ MTL）、3MF、PLY。<br>FBXは読み込みのみ | 相互変換、PNGプレビュー |
| **地理空間ベクターデータ**：Shapefile、GeoJSON、KML、KMZ、GPX<br>座標付きのCSV | 相互変換、CSV/XLSX/JSON。<br>投影座標系（UTM、Webメルカトル、ランベルト、アルベルス）のデータはWGS 84の経度/緯度で出力 |

印のない形式は、Office、Hancom Office、LibreOfficeなしですぐに使えます。
印のある形式には追加の変換エンジンが必要で、必要なときにだけダウンロードされます：¹ Ghostscript、² LibreOffice、³ Pandoc、⁴ calibre、⁵ FFmpeg（[詳細](docs/ENGINES.md)）。

## コマンドライン

`filee`コマンドは、アプリと同じエンジンとプリセットを使います（セットアップまたは`install.sh`で追加されます）。

```
filee convert photo.heic --to jpg
filee convert *.png --to webp --quality 80 -o converted
filee watch D:\Inbox --to pdf --move-originals
```

すべてのコマンドとオプションは、[filee.sh/cli](https://filee.sh/cli/)または`filee help`で確認できます。

## 開発

Windows、macOS、Linuxで、.NET 10 SDKを使ってビルドします。

```bash
git clone https://github.com/WDHUS4G/Filee.git
cd Filee
pwsh build/fetch-engines.ps1 -Only rhwp,pandoc   # optional engines
dotnet run --project src/Filee.App
dotnet test
```

[アーキテクチャ](docs/ARCHITECTURE.md) · [エンジン](docs/ENGINES.md) · [コンバーターの追加](docs/ADDING-A-CONVERTER.md) · [言語の追加](docs/ADDING-A-LANGUAGE.md) · [コントリビューション](CONTRIBUTING.md)

プルリクエスト歓迎です。
Fileeで手間が省けたら、⭐をつけていただけると、ほかの人がFileeを見つけやすくなります。

## コード署名ポリシー

Fileeは**Myeonggu Kim**（[KnifeLemon](https://github.com/KnifeLemon)）が開発・公開しています。

- Windows版のリリースには、Myeonggu Kim名義で発行された[Certum](https://www.certum.eu/)のオープンソース向けコード署名証明書で署名しています。
  - 2.0.0までのリリースは署名されていません。

ポリシーの詳細とプライバシーポリシーは[英語版README](README.md#code-signing-policy)をご覧ください。
Fileeはファイルをアップロードせず、利用データも送信しません。

## 支援

Fileeは無料で、これからも無料です。
応援していただける場合は、ご都合のよい方法をお選びください（[Fairy](https://fairy.hada.io/@filee)は韓国のカードと簡単決済に対応しています）。

**ここにあなたのお名前やロゴを。**
毎月支援してくださる方はここにお名前を、スポンサーの方はこことFileeの「Fileeについて」ページにロゴを掲載します。
一度だけ支援してくださった方も、ご希望があれば掲載します。

<!-- sponsors: src/Filee.App/Assets/Sponsors/sponsors.json -->

<p align="center">
  <a href="https://github.com/sponsors/KnifeLemon"><img src="https://img.shields.io/badge/GitHub%20Sponsors-EA4AAA?style=for-the-badge&logo=githubsponsors&logoColor=white" alt="GitHub Sponsors"></a>
  <a href="https://patreon.com/KnifeLemon"><img src="https://img.shields.io/badge/Patreon-000000?style=for-the-badge&logo=patreon&logoColor=white" alt="Patreon"></a>
  <a href="https://ko-fi.com/knifelemon"><img src="https://img.shields.io/badge/Ko--fi-FF6433?style=for-the-badge&logo=kofi&logoColor=white" alt="Ko-fi"></a>
  <a href="https://paypal.me/knifelemon"><img src="https://img.shields.io/badge/PayPal-002991?style=for-the-badge&logo=paypal&logoColor=white" alt="PayPal"></a>
  <a href="https://fairy.hada.io/@filee"><img src="https://img.shields.io/badge/Fairy-2E9E6B?style=for-the-badge" alt="Fairy"></a>
</p>

## ライセンス

MIT、© 2026 Myeonggu Kim (KnifeLemon) およびFileeのコントリビューター。
同梱のサードパーティ製コンポーネントはそれぞれのライセンスに従います。
一覧は[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)にあります。

<p align="center">
  <a href="https://star-history.com/#WDHUS4G/Filee&Date"><img src="https://api.star-history.com/svg?repos=WDHUS4G/Filee&type=Date" alt="Star history" width="600"></a>
</p>
