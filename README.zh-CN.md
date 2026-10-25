<p align="center">
  <a href="https://filee.sh/zh-cn/"><img src="docs/media/banner-zh-CN.jpg" alt="Filee 拖一下，放一下，转好了。" width="100%"></a>
</p>

<p align="center">
  <b>用拖放操作的免费开源文件格式转换器。</b><br>
  按住一个键（<kbd>Ctrl</kbd>，macOS 为 <kbd>Option</kbd>）拖动文件，光标处会弹出格式圆环，放到想要的格式上即可。
</p>

<p align="center">
  <a href="https://filee.sh/zh-cn/"><b>官网</b></a> ·
  <a href="https://github.com/WDHUS4G/Filee/releases/latest"><b>下载</b></a> ·
  <a href="https://www.linkedin.com/company/filee-sh/"><b>LinkedIn</b></a> ·
  <a href="#安装">安装</a> ·
  <a href="#命令行">命令行</a> ·
  <a href="#开发">开发</a>
</p>

<p align="center">
  <a href="https://github.com/WDHUS4G/Filee/actions/workflows/ci.yml"><img src="https://github.com/WDHUS4G/Filee/actions/workflows/ci.yml/badge.svg" alt="CI"></a>
  <a href="https://github.com/WDHUS4G/Filee/releases/latest"><img src="https://img.shields.io/github/v/release/WDHUS4G/Filee?color=7F77DD&label=release" alt="最新版本"></a>
  <a href="https://github.com/WDHUS4G/Filee/releases"><img src="https://img.shields.io/github/downloads/WDHUS4G/Filee/total?color=1D9E75" alt="下载次数"></a>
  <img src="https://img.shields.io/badge/Windows-10%20%7C%2011-3C3489" alt="Windows 10 | 11">
  <img src="https://img.shields.io/badge/Linux-x64%20%7C%20ARM64-3C3489" alt="Linux x64 | ARM64">
  <img src="https://img.shields.io/badge/macOS-preview-534AB7" alt="macOS 预览版">
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-6A62C9" alt="MIT 许可证"></a>
</p>

<p align="center">
  <a href="https://trendshift.io/repositories/276987"><img src="https://trendshift.io/api/badge/trendshift/repositories/276987/weekly?language=C%23&v=2" alt="Filee: #1 C# repository of the week on Trendshift" width="250" height="55"></a>
  <a href="https://trendshift.io/repositories/276987"><img src="https://trendshift.io/api/badge/trendshift/repositories/276987/daily?language=C%23&v=2" alt="Filee: #1 C# repository of the day on Trendshift" width="250" height="55"></a>
</p>

<p align="center">
  <a href="README.md">English</a> ·
  <a href="README.es.md">Español</a> ·
  <a href="README.ja.md">日本語</a> ·
  <a href="README.ko.md">한국어</a> ·
  <b>简体中文</b> ·
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
      <b>就在光标旁</b><br>
      无需打开应用窗口。
      只显示适合当前拖动文件的格式，就在你所在的位置。
    </td>
    <td width="33%" valign="top">
      <b>263 种格式，无需其他软件</b><br>
      图片、PDF、Word、Excel、PowerPoint、HWP、电子书、压缩包、字体、CAD、3D 模型和地理信息数据，无需 Office、韩文办公软件或 LibreOffice。
    </td>
    <td width="33%" valign="top">
      <b>只在你的电脑上</b><br>
      不上传任何文件。
      Filee 离线工作，结果保存在原文件旁边。
    </td>
  </tr>
</table>

## 实际效果

<p align="center">
  <a href="https://filee.sh/zh-cn/"><img src="docs/media/demo-zh-CN.webp" alt="按住 Ctrl 拖动 旅行.jpg，光标处弹出格式圆环，放到 PNG 上，进度环走完后生成 旅行.png" width="820"></a><br>
  <sub>27 秒视频和可交互演示请访问 <a href="https://filee.sh/zh-cn/">filee.sh</a></sub>
</p>

## 安装

从 [Releases](https://github.com/WDHUS4G/Filee/releases/latest) 下载。

- **Windows 10/11** 运行 `Filee-<version>-win-Setup.exe`，或解压 `win-Portable.zip` 后运行 `Filee.exe`。
  - 安装程序是 Filee 自己的，使用与 Filee 相同的语言，电脑上无需另外安装任何组件。
  - 安装时可以添加资源管理器菜单、登录时启动、`filee` 命令和可选引擎。
  - 静默安装沿用 Inno Setup 的参数（`/VERYSILENT /SUPPRESSMSGBOXES`）。
- **macOS 13 及以上**（预览）解压 `osx-arm64` 或 `osx-x64` 包并运行 `./install.sh`。
  - 也可以打开 `.dmg`，把 Filee 拖到“应用程序”文件夹（`filee` 命令只由 `install.sh` 添加）。
  - 目前尚未公证，首次启动时请在系统设置 → 隐私与安全性中点 **仍要打开**。
- **Linux x64、ARM64**（Ubuntu 22.04 及以上）解压后运行 `./install.sh`（无需 root，需要 Python 3）。
  - 拖动手势需要 X11，Wayland 下请使用拖放区域和文件管理器的右键菜单。

有新版本时 Filee 会在菜单中提示，更新时保留设置和引擎。
更新后会用几张幻灯片介绍新功能。

**Windows：Filee 无法启动（没有窗口也没有报错）？**
这种情况发生在长期未更新的 Windows 10 上，且电脑的处理器支持“硬件强制堆栈保护”（CET）。
Windows 会开启这项保护，却缺少 .NET 需要的一项功能，于是 Filee 和安装程序会立即退出。

- **推荐**：安装 Windows 更新中可用的更新（最新的累积更新）。
- **无法更新时**：通常只对 Filee 关闭这项保护就能启动。
  以管理员身份打开 PowerShell 并运行：

```powershell
Set-ProcessMitigation -Name Filee.exe -Disable UserShadowStack
Set-ProcessMitigation -Name filee-cli.exe -Disable UserShadowStack
```

安装程序也运行同样的命令，换成它的文件名（例如 `Filee-<version>-win-Setup.exe`）；使用便携版 ZIP 则只需上面两行。
要撤销，请在“Windows 安全中心 → 应用和浏览器控制 → Exploit Protection 设置 → 程序设置”中删除这些条目。

## 功能

- **光标旁的圆环**，按住按键拖动文件，放到格式上即可。
  只显示适合这些文件的格式。
- **直接从浏览器拖**，Chrome、Edge、Firefox 或 Safari 里的图片、文件链接和下载项也能这样拖放。
  - Filee 会保存到“下载”文件夹（也可以只保留转换后的文件）再转换。
  - 链接下载时不使用浏览器的登录信息。
- **每种文件类型一个工具栏**，拖放排列。
  下列项目都能用预设设置：
  - 质量、尺寸、DPI
  - PDF 页面和压缩
  - 视频音频
  - 保存位置和文件名
- **圆环有背面**，圆环打开时按 <kbd>Tab</kbd> 或转动鼠标滚轮翻过来，背面是不常用的格式和工具。
- **转换前先裁剪**，“裁剪”会逐张打开图片。
  拖动选框、选择比例，只保存选中的部分。
- **一次转换多个文件**，并行处理。
  需要多步的转换（HWP → HWPX → DOCX）会自动找到路线。
- **监视文件夹**，放入的文件自动转换。
- **最近的转换**显示在主页上，每项都有缩略图，可以直接“打开文件”，也可以用“打开方式”换个应用打开。
- **其他方式** 右键菜单、键盘快捷键、拖放区域和 [`filee` 命令](#命令行)。
- 浅色和深色主题，可自选强调色和环形工具栏不透明度，支持 English、Español、日本語、한국어、简体中文、繁體中文、Polski、हिन्दी、Русский、العربية（从右到左）、Português (Brasil)、Français、नेपाली、ไทย、Türkçe、Português (Portugal)。

## 截图

<table>
  <tr>
    <td width="50%"><img src="docs/media/page-toolbar-zh-CN.png" alt="用配置文件和预设编辑圆环工具栏"></td>
    <td width="50%"><img src="docs/media/page-home-zh-CN.png" alt="带拖放区和最近转换记录的主页"></td>
  </tr>
  <tr>
    <td align="center"><sub>拖放排列圆环，每种文件类型一个配置</sub></td>
    <td align="center"><sub>主页上的拖放区和最近转换记录</sub></td>
  </tr>
</table>

## 支持格式

| 源格式 | 目标格式 |
|---|---|
| **图片** JPG、PNG、WEBP、AVIF、TIFF、BMP、GIF、APNG、ICO、ICNS、JPEG XL、JPEG 2000、PSD/PSB、TGA、PPM、HEIC、QOI、DDS、PCX、MNG、CUR 等，<br>GIMP XCF、相机 RAW（CR2、CR3、NEF、ARW、DNG 等）、DICOM、OpenEXR、HDR、FITS、JPEG XR（Windows）、iPhone 实况照片（LIVP）仅读取 | 上述格式互转（HEIC⁵）、PDF、SVG（描摹）、缩放、灰度，<br>实况/动态照片（iPhone、安卓）→ 其中的视频（MP4、MOV⁵），以及视频 → 实况照片（iPhone）或动态照片（安卓）⁵（实验功能） |
| **矢量图** SVG、SVGZ、EMF、WMF、AI<br>EPS、PS¹<br>CDR、VSD、CGM、ODG、FODG² | PDF（矢量）、PNG 等图片、<br>SVG ↔ SVGZ、SVG → DXF/DWG、PDF → EPS/PS¹、SVG → EMF、WMF、ODG² |
| **PDF** | DOCX、HWPX、TXT、PNG、JPG、TIFF、CBZ、PPTX（每页一张图片）、SVG²，<br>合并（先选择顺序）、拆分、页码范围、压缩（更小的 PDF） |
| **文档** DOCX、DOCM、DOTX、EML、Outlook MSG，<br>OFD（电子发票、公文）仅读取<br>DOC、ODT、FODT、OTT、RTF、WPS、WPD、Pages、ClarisWorks、MacWrite、T602 等² | PDF、DOCX、HWPX、EPUB、TXT、HTML、<br>MSG → EML<br>ODT、FODT、OTT² |
| **表格** XLSX、XLSM、XLS、XLT、ODS、CSV、TSV<br>OTS、FODS、Numbers、ET² | PDF、XLSX、ODS、CSV、TSV、JSON、HWPX、HTML<br>XLT、OTS² |
| **演示文稿** PPTX、PPTM、POTX、POTM、PPSX<br>PPT、ODP、OTP、FODP、Keynote² | PDF、PNG、JPG、DOCX、HWPX<br>OTP² |
| **HWP** HWP、HWPX | PDF、PNG、DOCX、HWPX ↔ HWP、TXT、Markdown、HTML |
| **文本** TXT、LOG、脚本源码（PY、PAC、BAT、COMMAND，只读取，从不运行）、Markdown、HTML，<br>MHTML（保存的网页）仅读取<br>reStructuredText、LaTeX、Org、AsciiDoc、Typst、MediaWiki、DocBook、Jupyter、Textile、man、OPML³ | UTF-8 TXT、PDF、DOCX、HWPX、EPUB<br>互相转换、PPTX 幻灯片³ |
| **电子书** EPUB、KEPUB、MOBI、AZW3、AZW、AZW4、FB2、CBZ、CBR、CB7、HTMLZ、TXTZ<br>LIT、LRF、CHM、PDB 等⁴ | PDF、EPUB、KEPUB（Kobo）、DOCX、TXT、HTML、HWPX<br>MOBI、AZW3⁴ |
| **视频**⁵ MP4、MOV、MKV、WEBM、AVI、WMV、FLV、MPEG、VCD（DAT）、TS、M2TS、3GP、OGV、VOB 等 | MP4（H.264、HEVC、AV1）、WEBM、MOV、MKV、AVI、GIF、APNG、MP3、M4A、720p、<br>字幕（SRT、ASS、WebVTT） |
| **音频**⁵ MP3、M4A、AAC、WAV、FLAC、OGG、OPUS、WMA、AIFF、AMR、APE、WavPack、TTA、DSD（DSF、DFF）、DTS、AC3、E-AC3 等 | MP3、M4A（AAC、ALAC）、WAV、FLAC、OGG、OPUS、AAC、WavPack、TTA、W64、AC3、E-AC3 |
| **字幕** SRT、ASS/SSA、WebVTT、SAMI（SMI）、LRC、TTML、MicroDVD、SCC | 互相转换，保存为 UTF-8（也能读取 CP949 韩文字幕） |
| **压缩包** ZIP、ANSYS Workbench（WBPZ）、7Z、RAR、TAR、TAR.GZ、TAR.BZ2、TAR.XZ、GZ、ISO、CAB、DMG、ALZ、EGG 等 | 解压、ZIP、7Z、TAR、TAR.GZ，<br>多个文件 → 一个 ZIP |
| **CAD** DWG、DXF | PDF、SVG、PNG、DWG ↔ DXF |
| **字体** TTF、OTF、WOFF、WOFF2、EOT | 互相转换 |
| **数据** JSON、JSON Lines、YAML、TOML、XML<br>vCard、iCalendar | 互相转换、CSV 和 XLSX（表格数据）、<br>vCard、iCalendar ↔ CSV |
| **3D 模型** GLB、glTF、STL、OBJ（+ MTL）、3MF、PLY，<br>FBX 仅读取 | 互相转换、PNG 预览 |
| **地理信息（矢量）** Shapefile、GeoJSON、KML、KMZ、GPX<br>带坐标的 CSV | 互相转换、CSV / XLSX / JSON，<br>投影坐标系（UTM、Web 墨卡托、兰伯特、阿尔伯斯）会转换为 WGS 84 经纬度 |

未标注的格式装好即用，无需 Office、韩文办公软件或 LibreOffice。
标注的格式需要可选引擎，只在需要时下载，分别是 ¹ Ghostscript、² LibreOffice、³ Pandoc、⁴ calibre、⁵ FFmpeg（[详情](docs/ENGINES.md)）。

## 命令行

`filee` 命令使用与应用相同的引擎和预设（安装程序或 `install.sh` 会添加它）。

```
filee convert photo.heic --to jpg
filee convert *.png --to webp --quality 80 -o converted
filee watch D:\Inbox --to pdf --move-originals
```

全部命令和选项见 [filee.sh/zh-cn/cli](https://filee.sh/zh-cn/cli/) 或 `filee help`。

## 开发

在 Windows、macOS 或 Linux 上使用 .NET 10 SDK 构建。

```bash
git clone https://github.com/WDHUS4G/Filee.git
cd Filee
pwsh build/fetch-engines.ps1 -Only rhwp,pandoc   # optional engines
dotnet run --project src/Filee.App
dotnet test
```

[架构](docs/ARCHITECTURE.md) · [引擎](docs/ENGINES.md) · [添加转换器](docs/ADDING-A-CONVERTER.md) · [添加语言](docs/ADDING-A-LANGUAGE.md) · [贡献指南](CONTRIBUTING.md)

欢迎提交 Pull Request。
如果 Filee 帮你省了时间，点个 ⭐ 能让更多人发现它。

## 代码签名政策

Filee 由 **Myeonggu Kim**（[KnifeLemon](https://github.com/KnifeLemon)）开发和发布。

- Windows 版本使用颁发给 Myeonggu Kim 的 [Certum](https://www.certum.eu/) 开源代码签名证书签名。
  - 2.0.0 及更早的版本没有签名。

详细政策和隐私政策见[英文 README](README.md#code-signing-policy)。
Filee 不会上传文件，也不会发送任何使用数据。

## 赞助

Filee 免费，并将一直免费。
想支持开发的话，选一种方便的方式即可（[Fairy](https://fairy.hada.io/@filee) 支持韩国银行卡和简易支付）。
谢谢！

**你的名字或标志可以出现在这里。**
按月赞助者的名字会列在这里，标志档位的标志会显示在这里和 Filee 的“关于”页面。
一次性赞助的朋友也可以要求列出名字。

<!-- sponsors: src/Filee.App/Assets/Sponsors/sponsors.json -->

<p align="center">
  <a href="https://github.com/sponsors/KnifeLemon"><img src="https://img.shields.io/badge/GitHub%20Sponsors-EA4AAA?style=for-the-badge&logo=githubsponsors&logoColor=white" alt="GitHub Sponsors"></a>
  <a href="https://patreon.com/KnifeLemon"><img src="https://img.shields.io/badge/Patreon-000000?style=for-the-badge&logo=patreon&logoColor=white" alt="Patreon"></a>
  <a href="https://ko-fi.com/knifelemon"><img src="https://img.shields.io/badge/Ko--fi-FF6433?style=for-the-badge&logo=kofi&logoColor=white" alt="Ko-fi"></a>
  <a href="https://paypal.me/knifelemon"><img src="https://img.shields.io/badge/PayPal-002991?style=for-the-badge&logo=paypal&logoColor=white" alt="PayPal"></a>
  <a href="https://fairy.hada.io/@filee"><img src="https://img.shields.io/badge/Fairy-2E9E6B?style=for-the-badge" alt="Fairy"></a>
</p>

## 许可证

MIT，© 2026 Myeonggu Kim (KnifeLemon) 和 Filee 贡献者。
随附的第三方组件遵循各自的许可证，详见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

<p align="center">
  <a href="https://star-history.com/#WDHUS4G/Filee&Date"><img src="https://api.star-history.com/svg?repos=WDHUS4G/Filee&type=Date" alt="Star history" width="600"></a>
</p>
