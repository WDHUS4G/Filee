<p align="center">
  <a href="https://filee.sh"><img src="docs/media/banner-en.jpg" alt="Filee: Sürükle. Bırak. Dönüştürüldü." width="100%"></a>
</p>

<p align="center">
  <b>Sürükleyerek kullandığınız ücretsiz, açık kaynaklı bir dosya dönüştürücü.</b><br>
  Bir tuşa basın (<kbd>Ctrl</kbd>, macOS'ta <kbd>Option</kbd>), dosyaları sürükleyip imlecinizde açılan halka üzerindeki bir formata bırakın.
</p>

<p align="center">
  <a href="https://filee.sh"><b>Web Sitesi</b></a> ·
  <a href="https://github.com/WDHUS4G/Filee/releases/latest"><b>İndir</b></a> ·
  <a href="#kurulum">Kurulum</a> ·
  <a href="#komut-satırı">Komut satırı</a> ·
  <a href="#geliştirme">Geliştirme</a>
</p>

<p align="center">
  <a href="https://github.com/KnifeLemon/Filee/actions/workflows/ci.yml"><img src="https://github.com/KnifeLemon/Filee/actions/workflows/ci.yml/badge.svg" alt="CI"></a>
  <a href="https://github.com/WDHUS4G/Filee/releases/latest"><img src="https://img.shields.io/github/v/release/WDHUS4G/Filee?color=7F77DD&label=sürüm" alt="Son sürüm"></a>
  <img src="https://img.shields.io/badge/Windows-10%20%7C%2011-3C3489" alt="Windows 10 | 11">
  <img src="https://img.shields.io/badge/macOS%20%7C%20Linux-önizleme-534AB7" alt="macOS | Linux önizleme">
  <a href="LICENSE"><img src="https://img.shields.io/badge/lisans-MIT-6A62C9" alt="MIT lisansı"></a>
</p>

<p align="center"><a href="README.md">English</a> · <a href="README.ko.md">한국어</a> · <a href="README.zh-CN.md">简体中文</a> · <b>Türkçe</b></p>

<table>
  <tr>
    <td width="33%" valign="top"><b>İmlecinizin tam yerinde</b><br>Açılacak uygulama penceresi yok. Formatlar zaten bulunduğunuz yerde görünür; yalnızca sürüklediğiniz dosyalar için anlamlı olanlar.</td>
    <td width="33%" valign="top"><b>244 format, başka yazılım gerektirmez</b><br>Resimler, PDF, Word, Excel, PowerPoint, HWP, e-kitaplar, arşivler, yazı tipleri, CAD ve 3D modeller Office, Hancom Office veya LibreOffice olmadan çalışır.</td>
    <td width="33%" valign="top"><b>Bilgisayarınızda kalır</b><br>Hiçbir şey yüklenmez. Filee çevrimdışı çalışır ve sonucu orijinalin yanına kaydeder.</td>
  </tr>
</table>

## Çalışırken görün

<p align="center">
  <a href="https://filee.sh"><img src="docs/media/demo-en.webp" alt="trip.jpg dosyası Ctrl tuşu basılıyken sürükleniyor: imleçte bir format halkası açılıyor, dosya PNG üzerine bırakılıyor, bir ilerleme halkası tamamlanıyor ve trip.png görünüyor" width="820"></a><br>
  <sub>27 saniyelik videoyu izleyin ve <a href="https://filee.sh">filee.sh</a> üzerinde interaktif demoyu deneyin</sub>
</p>

## Kurulum

[Releases](https://github.com/WDHUS4G/Filee/releases/latest) sayfasından indirin.

- **Windows 10/11**: `Filee-<sürüm>-win-Setup.exe` dosyasını çalıştırın ya da `win-Portable.zip` dosyasını açıp `Filee.exe`'yi çalıştırın.
  Kurulum; Explorer menüsünü, oturum açıldığında başlatmayı, `filee` komutunu ve isteğe bağlı motorları ekleyebilir.
- **macOS 14+** (önizleme): `osx-arm64` veya `osx-x64` paketini açın ve `./install.sh` çalıştırın.
  Derleme henüz notarize edilmemiştir; ilk başlatmaya Sistem Ayarları → Gizlilik ve Güvenlik → **Yine de Aç** seçeneğiyle izin verin.
- **Linux x64/ARM64** (önizleme): Paketi açın ve `./install.sh` çalıştırın (root gerekmez, Python 3 gerekir).
  Sürükleme hareketleri X11 gerektirir. Wayland'da bırakma alanını ve dosya yöneticisinin sağ tıklama menüsünü kullanın.

Filee, menüsünde yeni sürümleri gösterir ve ayarlarınızı ile motorlarınızı koruyarak kendini günceller.

## Özellikler

- **İmleçte bir halka**: Bir tuşa basın, dosyaları sürükleyip bir formata bırakın. Yalnızca dosyalara uyan formatlar görünür.
- **Dosya türü başına bir araç çubuğu**, sürükle ve bırakla düzenlenmiş; kalite, boyut, DPI, PDF sayfaları, video ve ses, çıktı klasörü ve dosya adları için ön ayarlarla.
- **Birçok dosya aynı anda**, paralel olarak. Birden fazla adım içeren yollar (HWP → HWPX → DOCX) otomatik olarak bulunur.
- **İzleme klasörleri**, içlerine düşen her dosyayı dönüştürür.
- **Diğer giriş yöntemleri**: Sağ tıklama menüsü, klavye kısayolu, bırakma alanı ve [`filee` komutu](#komut-satırı).
- Açık ve koyu temalar; İngilizce, 한국어, 简体中文 ve **Türkçe** dillerinde.

## Ekran Görüntüleri

<table>
  <tr>
    <td width="50%"><img src="docs/media/page-toolbar-en.png" alt="Profiller ve ön ayarlarla halka araç çubuğu düzenleyicisi"></td>
    <td width="50%"><img src="docs/media/page-home-en.png" alt="Bırakma alanı ve son dönüşümleri gösteren ana ekran"></td>
  </tr>
  <tr>
    <td align="center"><sub>Halkayı sürükle ve bırakla düzenleyin, her dosya türü için bir profil</sub></td>
    <td align="center"><sub>Ana sayfa: bırakma alanı ve son dönüşümler</sub></td>
  </tr>
</table>

## Formatlar

| Kaynaktan | Hedefe |
|---|---|
| **Resimler**: JPG, PNG, WEBP, AVIF, TIFF, BMP, GIF, APNG, ICO, ICNS, JPEG XL, JPEG 2000, PSD/PSB, TGA, PPM, HEIC, QOI, DDS, PCX, MNG, CUR, …; GIMP XCF, ham kamera (CR2, CR3, NEF, ARW, DNG, …), DICOM, OpenEXR, HDR okuma | bunlardan herhangi biri (HEIC⁵), PDF, SVG (izleme), yeniden boyutlandırılmış, gri tonlamalı |
| **Vektör**: SVG, SVGZ, EMF, WMF, AI · EPS, PS¹ · CDR, VSD, CGM, ODG, FODG² | PDF (vektör), PNG ve diğer resimler, SVG ↔ SVGZ, SVG → DXF/DWG, PDF → EPS/PS¹, SVG → EMF, WMF, ODG² |
| **PDF** | DOCX, HWPX, TXT, PNG, JPG, TIFF, CBZ, SVG²; birleştirme, bölme, sayfa aralıkları |
| **Belgeler**: DOCX, DOCM, DOTX, EML, Outlook MSG · DOC, ODT, FODT, OTT, RTF, WPS, WPD, Pages, ClarisWorks, MacWrite, T602, …² | PDF, DOCX, HWPX, EPUB, TXT, HTML; MSG → EML · ODT, FODT, OTT² |
| **Elektronik tablolar**: XLSX, XLSM, XLS, XLT, ODS, CSV, TSV · OTS, FODS, Numbers, ET² | PDF, XLSX, ODS, CSV, TSV, JSON, HWPX, HTML · XLT, OTS² |
| **Sunumlar**: PPTX, PPTM, POTX, PPSX · PPT, ODP, OTP, FODP, Keynote² | PDF, PNG, JPG, DOCX, HWPX · OTP² |
| **HWP**: HWP, HWPX | PDF, PNG, DOCX, HWPX ↔ HWP, TXT, Markdown, HTML |
| **Metin**: TXT, Markdown, HTML · reStructuredText, LaTeX, Org, AsciiDoc, Typst, MediaWiki, DocBook, Jupyter, Textile, man, OPML³ | PDF, DOCX, HWPX, EPUB · birbirleri, PPTX slaytları³ |
| **E-kitaplar**: EPUB, KEPUB, MOBI, AZW3, AZW, AZW4, FB2, CBZ, CBR, CB7, HTMLZ, TXTZ · LIT, LRF, CHM, PDB, …⁴ | PDF, EPUB, KEPUB (Kobo), DOCX, TXT, HTML, HWPX · MOBI, AZW3⁴ |
| **Video**⁵: MP4, MOV, MKV, WEBM, AVI, WMV, FLV, MPEG, TS, M2TS, 3GP, OGV, VOB, … | MP4 (H.264, HEVC, AV1), WEBM, MOV, MKV, AVI, GIF, APNG, MP3, M4A, 720p, altyazılar (SRT, ASS, WebVTT) |
| **Ses**⁵: MP3, M4A, AAC, WAV, FLAC, OGG, OPUS, WMA, AIFF, AMR, APE, WavPack, TTA, DSD (DSF, DFF), DTS, AC3, E-AC3, … | MP3, M4A (AAC, ALAC), WAV, FLAC, OGG, OPUS, AAC, WavPack, TTA, W64, AC3, E-AC3 |
| **Altyazılar**: SRT, ASS/SSA, WebVTT, SAMI (SMI), LRC, TTML, MicroDVD, SCC | UTF-8 olarak birbirleri (Korece CP949 dosyaları doğru okunur) |
| **Arşivler**: ZIP, 7Z, RAR, TAR, TAR.GZ, TAR.BZ2, TAR.XZ, GZ, ISO, CAB, DMG, ALZ, EGG, … | çıkar, ZIP, 7Z, TAR, TAR.GZ; herhangi dosyalar → tek ZIP |
| **CAD**: DWG, DXF | PDF, SVG, PNG, DWG ↔ DXF |
| **Yazı tipleri**: TTF, OTF, WOFF, WOFF2, EOT | birbirleri |
| **Veri**: JSON, JSON Lines, YAML, TOML, XML · vCard | birbirleri, CSV ve XLSX (tablolar); vCard ↔ CSV |
| **3D modeller**: GLB, glTF, STL, OBJ (+ MTL), 3MF, PLY | birbirleri, PNG önizleme |

İşaretsiz formatlar, Office, Hancom Office veya LibreOffice olmadan kullanıma hazır çalışır.
İşaretli olanlar isteğe bağlı bir motor gerektirir, yalnızca gerektiğinde indirilir: ¹ Ghostscript, ² LibreOffice, ³ Pandoc, ⁴ calibre,
⁵ FFmpeg ([ayrıntılar](docs/ENGINES.md)).

## Komut satırı

`filee` komutu, uygulama ile aynı motorları ve ön ayarları kullanır (Kurulum veya `install.sh` bunu ekler).

```
filee convert photo.heic --to jpg
filee convert *.png --to webp --quality 80 -o converted
filee watch D:\Inbox --to pdf --move-originals
```

Her komut ve seçenek: [filee.sh/cli](https://filee.sh/cli/) veya `filee help`.

## Geliştirme

Windows, macOS veya Linux'ta .NET 10 SDK ile derleyin:

```bash
git clone https://github.com/mosmduali/Filee.git
cd Filee
pwsh build/fetch-engines.ps1 -Only rhwp,pandoc   # isteğe bağlı motorlar
dotnet run --project src/Filee.App
dotnet test
```

[Mimari](docs/ARCHITECTURE.md) · [Motorlar](docs/ENGINES.md) · [Dönüştürücü ekleme](docs/ADDING-A-CONVERTER.md) ·
[Dil ekleme](docs/ADDING-A-LANGUAGE.md) · [Katkıda bulunma](CONTRIBUTING.md)

Pull request'ler memnuniyetle karşılanır. Filee size birkaç tıklama kazandırıyorsa, bir ⭐ başkalarının bulmasına yardımcı olur.

## Kod imzalama politikası

Filee, **Myeonggu Kim** ([KnifeLemon](https://github.com/KnifeLemon)) tarafından geliştirilmekte ve yayınlanmaktadır.

Windows sürümleri, Myeonggu Kim adına düzenlenmiş bir [Certum](https://www.certum.eu/) açık kaynak kod imzalama sertifikasıyla imzalanacaktır. Sertifika düzenleme aşamasındadır; bu süreçte sürümler kod imzalanmamıştır ve Windows SmartScreen yükleyiciyi çalıştırdığınızda uyarı verebilir ("Daha fazla bilgi" → "Yine de çalıştır"). macOS paketleri geçici olarak imzalanmış ancak notarize edilmemiştir.

### Gizlilik politikası

Filee, dosyaları kendi bilgisayarınızda dönüştürür ve asla yüklemez. Kullanım verisi toplamaz ve telemetri göndermez.
İnternete yalnızca şu durumlarda bağlanır:

- Başlangıçtan kısa süre sonra ve ardından birkaç saatte bir GitHub'a (`api.github.com`) daha yeni bir sürüm olup olmadığını sormak için.
  Bunu Ayarlar → Hakkında'dan kapatabilirsiniz ("Güncellemeleri otomatik denetle").
- Yüklemeyi seçtiğiniz dönüşüm motorlarını resmi konumlardan indirmek için
  ([`engines.json`](src/Filee.Engines/Infrastructure/engines.json) dosyasında sabitlenmiştir).
- Bir bağlantıya tıkladığınızda, bu web tarayıcınızı açar.

## Sponsor

Filee ücretsizdir ve ücretsiz kalır. Desteklemek isterseniz, size uyan yöntemi seçin
([Fairy](https://fairy.hada.io/@filee) Korece kartları ve kolay ödemeyi kabul eder).

**Buraya adınız veya logonuz.** Aylık destekçiler burada adlarıyla listelenir ve sponsorlar logolarını buraya ve
Filee'nin Hakkında sayfasına alır. Tek seferlik destekçiler de listelenmeyi talep edebilir.

<!-- sponsors: src/Filee.App/Assets/Sponsors/sponsors.json -->

<p align="center">
  <a href="https://github.com/sponsors/KnifeLemon"><img src="https://img.shields.io/badge/GitHub%20Sponsors-EA4AAA?style=for-the-badge&logo=githubsponsors&logoColor=white" alt="GitHub Sponsors"></a>
  <a href="https://patreon.com/KnifeLemon"><img src="https://img.shields.io/badge/Patreon-000000?style=for-the-badge&logo=patreon&logoColor=white" alt="Patreon"></a>
  <a href="https://ko-fi.com/knifelemon"><img src="https://img.shields.io/badge/Ko--fi-FF6433?style=for-the-badge&logo=kofi&logoColor=white" alt="Ko-fi"></a>
  <a href="https://paypal.me/knifelemon"><img src="https://img.shields.io/badge/PayPal-002991?style=for-the-badge&logo=paypal&logoColor=white" alt="PayPal"></a>
  <a href="https://fairy.hada.io/@filee"><img src="https://img.shields.io/badge/Fairy-2E9E6B?style=for-the-badge" alt="Fairy"></a>
</p>

## Lisans

MIT, © 2026 Myeonggu Kim (KnifeLemon) ve Filee katkıda bulunanlar. Birlikte verilen üçüncü taraf bileşenler kendi lisanslarını korur, [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) dosyasında listelenmiştir.

---

## Türkçe Çeviri Hakkında

Bu projenin Türkçe arayüz çevirisi (`tr.json`) ve bu Türkçe README dosyası **[mosmduali](https://github.com/mosmduali)** tarafından katkı olarak sağlanmıştır.
