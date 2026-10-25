<p align="center">
  <a href="https://filee.sh/es/"><img src="docs/media/banner-en.jpg" alt="Filee: Arrastra. Suelta. Convertido." width="100%"></a>
</p>

<p align="center">
  <b>Un conversor de archivos gratuito y de código abierto que se usa arrastrando.</b><br>
  Mantén pulsada una tecla (<kbd>Ctrl</kbd>, <kbd>Option</kbd> en macOS), arrastra los archivos y suéltalos sobre un formato del menú circular que se abre junto al cursor.
</p>

<p align="center">
  <a href="https://filee.sh/es/"><b>Sitio web</b></a> ·
  <a href="https://github.com/WDHUS4G/Filee/releases/latest"><b>Descargar</b></a> ·
  <a href="https://www.linkedin.com/company/filee-sh/"><b>LinkedIn</b></a> ·
  <a href="#instalación">Instalación</a> ·
  <a href="#línea-de-comandos">Línea de comandos</a> ·
  <a href="#desarrollo">Desarrollo</a>
</p>

<p align="center">
  <a href="https://github.com/WDHUS4G/Filee/actions/workflows/ci.yml"><img src="https://github.com/WDHUS4G/Filee/actions/workflows/ci.yml/badge.svg" alt="CI"></a>
  <a href="https://github.com/WDHUS4G/Filee/releases/latest"><img src="https://img.shields.io/github/v/release/WDHUS4G/Filee?color=7F77DD&label=release" alt="Última versión"></a>
  <a href="https://github.com/WDHUS4G/Filee/releases"><img src="https://img.shields.io/github/downloads/WDHUS4G/Filee/total?color=1D9E75" alt="Descargas"></a>
  <img src="https://img.shields.io/badge/Windows-10%20%7C%2011-3C3489" alt="Windows 10 | 11">
  <img src="https://img.shields.io/badge/Linux-x64%20%7C%20ARM64-3C3489" alt="Linux x64 | ARM64">
  <img src="https://img.shields.io/badge/macOS-preview-534AB7" alt="macOS en versión preliminar">
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-6A62C9" alt="Licencia MIT"></a>
</p>

<p align="center">
  <a href="https://trendshift.io/repositories/276987"><img src="https://trendshift.io/api/badge/trendshift/repositories/276987/weekly?language=C%23&v=2" alt="Filee: #1 C# repository of the week on Trendshift" width="250" height="55"></a>
  <a href="https://trendshift.io/repositories/276987"><img src="https://trendshift.io/api/badge/trendshift/repositories/276987/daily?language=C%23&v=2" alt="Filee: #1 C# repository of the day on Trendshift" width="250" height="55"></a>
</p>

<p align="center">
  <a href="README.md">English</a> ·
  <b>Español</b> ·
  <a href="README.ja.md">日本語</a> ·
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
      <b>Justo en tu cursor</b><br>
      No hay ninguna ventana que abrir.
      Los formatos aparecen donde ya estás, y solo los que tienen sentido para los archivos que arrastras.
    </td>
    <td width="33%" valign="top">
      <b>263 formatos, sin otros programas</b><br>
      Imágenes, PDF, Word, Excel, PowerPoint, HWP, libros electrónicos, archivos comprimidos, tipografías, CAD, modelos 3D y datos geoespaciales funcionan sin Office, Hancom Office ni LibreOffice.
    </td>
    <td width="33%" valign="top">
      <b>Todo se queda en tu equipo</b><br>
      No se sube nada.
      Filee funciona sin conexión y guarda el resultado junto al original.
    </td>
  </tr>
</table>

## Míralo en acción

<p align="center">
  <a href="https://filee.sh/es/"><img src="docs/media/demo-en.webp" alt="Se arrastra trip.jpg con Ctrl pulsado: junto al cursor se abre un menú circular de formatos, el archivo se suelta sobre PNG, un anillo de progreso se completa y aparece trip.png" width="820"></a><br>
  <sub>Mira el vídeo de 27 segundos y prueba la demo interactiva en <a href="https://filee.sh/es/">filee.sh</a></sub>
</p>

## Instalación

Descárgalo desde [Releases](https://github.com/WDHUS4G/Filee/releases/latest).

- **Windows 10/11**: ejecuta `Filee-<version>-win-Setup.exe`, o descomprime `win-Portable.zip` y ejecuta `Filee.exe`.
  - El instalador es el propio de Filee, está en los idiomas de Filee y no necesita nada más en el equipo.
  - Puede añadir el menú del Explorador, el inicio al iniciar sesión, el comando `filee` y motores opcionales.
  - Las instalaciones silenciosas aceptan las opciones de Inno Setup (`/VERYSILENT /SUPPRESSMSGBOXES`).
- **macOS 13+** (versión preliminar): descomprime el paquete `osx-arm64` u `osx-x64` y ejecuta `./install.sh`.
  - O abre el `.dmg` y arrastra Filee a Aplicaciones (el comando `filee` solo lo añade `install.sh`).
  - Aún no está notarizado, así que la primera vez tendrás que permitir que se abra en Ajustes del Sistema → Privacidad y seguridad → **Abrir igualmente**.
- **Linux x64/ARM64** (Ubuntu 22.04+): descomprime el paquete y ejecuta `./install.sh` (no necesita root, sí Python 3).
  - Los gestos de arrastre necesitan X11.
    En Wayland, usa la zona de suelta y el menú contextual del gestor de archivos.

Filee avisa de las versiones nuevas en su menú y se actualiza solo, sin tocar tus ajustes ni tus motores.
Tras una actualización, unas pocas diapositivas muestran las novedades.

**Windows: ¿Filee no se abre (sin ventana ni error)?**
Ocurre en Windows 10 que lleva mucho tiempo sin actualizarse, en un equipo cuyo procesador admite la protección de pila aplicada por hardware (CET).
Windows activa esa protección, pero le falta una parte que .NET necesita, así que Filee y el instalador se cierran al instante.

- **Recomendado:** instala las actualizaciones de Windows disponibles (la última actualización acumulativa).
- **Si no puedes actualizar:** desactivar la protección solo para Filee suele funcionar.
  Ejecuta esto en PowerShell como administrador:

```powershell
Set-ProcessMitigation -Name Filee.exe -Disable UserShadowStack
Set-ProcessMitigation -Name filee-cli.exe -Disable UserShadowStack
```

Haz lo mismo con el nombre de archivo del instalador (p. ej. `Filee-<version>-win-Setup.exe`), o usa el ZIP portátil, que solo necesita estas dos líneas.
Para deshacerlo, quita esas entradas en Seguridad de Windows → Control de aplicaciones y navegador → Configuración de protección contra vulnerabilidades → Configuración de programas.

## Funciones

- **Un menú circular junto al cursor**: mantén pulsada una tecla, arrastra los archivos y suéltalos sobre un formato.
  Solo aparecen los formatos que encajan con esos archivos.
- **Directamente desde el navegador**: arrastra igual una imagen, un enlace a un archivo o una descarga desde Chrome, Edge, Firefox o Safari.
  - Filee lo guarda en tu carpeta Descargas (o conserva solo el archivo convertido) y lo convierte.
  - Los enlaces se descargan sin la sesión iniciada en tu navegador.
- **Un menú circular por tipo de archivo**, que ordenas arrastrando y soltando, con ajustes preestablecidos de:
  - calidad, tamaño, DPI
  - páginas y compresión de PDF
  - vídeo y audio
  - carpeta de salida y nombres de archivo
- **Dos caras en cada menú circular**: con el menú abierto, pulsa <kbd>Tab</kbd> o gira la rueda del ratón para darle la vuelta y ver formatos y herramientas menos habituales.
- **Recorta antes de convertir**: la porción Recortar abre las imágenes una a una.
  Arrastra el marco, elige una proporción y solo se guarda esa parte.
- **Muchos archivos a la vez**, en paralelo.
  Las conversiones de varios pasos (HWP → HWPX → DOCX) se resuelven solas.
- **Carpetas supervisadas** que convierten cada archivo que llega a ellas.
- **Conversiones recientes** en Inicio, cada una con su miniatura: abre el resultado o ábrelo con otra aplicación.
- **Otras formas de usarlo**: el menú contextual, un atajo de teclado, la zona de suelta y el [comando `filee`](#línea-de-comandos).
- Temas claro y oscuro con tu propio color de acento y la opacidad del menú circular, en English, Español, 日本語, 한국어, 简体中文, 繁體中文, Polski, हिन्दी, Русский, العربية (de derecha a izquierda), Português (Brasil), Français, नेपाली, ไทย, Türkçe y Português (Portugal).

## Capturas de pantalla

<table>
  <tr>
    <td width="50%"><img src="docs/media/page-toolbar-en.png" alt="Editor del menú circular con perfiles y ajustes preestablecidos"></td>
    <td width="50%"><img src="docs/media/page-home-en.png" alt="Pantalla de inicio con la zona de suelta y las conversiones recientes"></td>
  </tr>
  <tr>
    <td align="center"><sub>Ordena el menú circular arrastrando y soltando, con un perfil por tipo de archivo</sub></td>
    <td align="center"><sub>Inicio: zona de suelta y conversiones recientes</sub></td>
  </tr>
</table>

## Formatos

| De | A |
|---|---|
| **Imágenes**: JPG, PNG, WEBP, AVIF, TIFF, BMP, GIF, APNG, ICO, ICNS, JPEG XL, JPEG 2000, PSD/PSB, TGA, PPM, HEIC, QOI, DDS, PCX, MNG, CUR, …;<br>GIMP XCF, RAW de cámara (CR2, CR3, NEF, ARW, DNG, …), DICOM, OpenEXR, HDR, FITS, JPEG XR (Windows), Live Photo de iPhone (LIVP) solo lectura | cualquiera de ellos (HEIC⁵), PDF, SVG (vectorizado), redimensionar, escala de grises;<br>Live/Motion Photos (iPhone, Android) → su vídeo (MP4, MOV⁵), y un vídeo → Live Photo (iPhone) o Motion Photo (Android)⁵, experimental |
| **Vectorial**: SVG, SVGZ, EMF, WMF, AI<br>EPS, PS¹<br>CDR, VSD, CGM, ODG, FODG² | PDF (vectorial), PNG y otras imágenes,<br>SVG ↔ SVGZ, SVG → DXF/DWG, PDF → EPS/PS¹, SVG → EMF, WMF, ODG² |
| **PDF** | DOCX, HWPX, TXT, PNG, JPG, TIFF, CBZ, PPTX (una imagen por página), SVG²;<br>unir (en el orden que elijas antes), dividir, rangos de páginas, comprimir (PDF más ligeros) |
| **Documentos**: DOCX, DOCM, DOTX, EML, Outlook MSG;<br>OFD (facturas electrónicas y documentos oficiales chinos) solo lectura<br>DOC, ODT, FODT, OTT, RTF, WPS, WPD, Pages, ClarisWorks, MacWrite, T602, …² | PDF, DOCX, HWPX, EPUB, TXT, HTML;<br>MSG → EML<br>ODT, FODT, OTT² |
| **Hojas de cálculo**: XLSX, XLSM, XLS, XLT, ODS, CSV, TSV<br>OTS, FODS, Numbers, ET² | PDF, XLSX, ODS, CSV, TSV, JSON, HWPX, HTML<br>XLT, OTS² |
| **Presentaciones**: PPTX, PPTM, POTX, POTM, PPSX<br>PPT, ODP, OTP, FODP, Keynote² | PDF, PNG, JPG, DOCX, HWPX<br>OTP² |
| **HWP**: HWP, HWPX | PDF, PNG, DOCX, HWPX ↔ HWP, TXT, Markdown, HTML |
| **Texto**: TXT, LOG, código de scripts (PY, PAC, BAT, COMMAND; se leen, nunca se ejecutan), Markdown, HTML;<br>MHTML (páginas web guardadas) solo lectura<br>reStructuredText, LaTeX, Org, AsciiDoc, Typst, MediaWiki, DocBook, Jupyter, Textile, man, OPML³ | TXT en UTF-8, PDF, DOCX, HWPX, EPUB<br>entre sí, diapositivas PPTX³ |
| **Libros electrónicos**: EPUB, KEPUB, MOBI, AZW3, AZW, AZW4, FB2, CBZ, CBR, CB7, HTMLZ, TXTZ<br>LIT, LRF, CHM, PDB, …⁴ | PDF, EPUB, KEPUB (Kobo), DOCX, TXT, HTML, HWPX<br>MOBI, AZW3⁴ |
| **Vídeo**⁵: MP4, MOV, MKV, WEBM, AVI, WMV, FLV, MPEG, Video CD (DAT), TS, M2TS, 3GP, OGV, VOB, … | MP4 (H.264, HEVC, AV1), WEBM, MOV, MKV, AVI, GIF, APNG, MP3, M4A, 720p,<br>subtítulos (SRT, ASS, WebVTT) |
| **Audio**⁵: MP3, M4A, AAC, WAV, FLAC, OGG, OPUS, WMA, AIFF, AMR, APE, WavPack, TTA, DSD (DSF, DFF), DTS, AC3, E-AC3, … | MP3, M4A (AAC, ALAC), WAV, FLAC, OGG, OPUS, AAC, WavPack, TTA, W64, AC3, E-AC3 |
| **Subtítulos**: SRT, ASS/SSA, WebVTT, SAMI (SMI), LRC, TTML, MicroDVD, SCC | entre sí, en UTF-8 (los archivos coreanos en CP949 se leen correctamente) |
| **Archivos comprimidos**: ZIP, ANSYS Workbench (WBPZ), 7Z, RAR, TAR, TAR.GZ, TAR.BZ2, TAR.XZ, GZ, ISO, CAB, DMG, ALZ, EGG, … | extraer, ZIP, 7Z, TAR, TAR.GZ;<br>cualquier conjunto de archivos → un ZIP |
| **CAD**: DWG, DXF | PDF, SVG, PNG, DWG ↔ DXF |
| **Tipografías**: TTF, OTF, WOFF, WOFF2, EOT | entre sí |
| **Datos**: JSON, JSON Lines, YAML, TOML, XML<br>vCard, iCalendar | entre sí, CSV y XLSX (tablas);<br>vCard, iCalendar ↔ CSV |
| **Modelos 3D**: GLB, glTF, STL, OBJ (+ MTL), 3MF, PLY;<br>FBX solo lectura | entre sí, vista previa en PNG |
| **Datos geoespaciales vectoriales**: Shapefile, GeoJSON, KML, KMZ, GPX<br>CSV con coordenadas | entre sí y a CSV / XLSX / JSON;<br>los datos proyectados (UTM, Web Mercator, Lambert, Albers) salen como longitud / latitud WGS 84 |

Los formatos sin marca funcionan nada más instalar, sin Office, Hancom Office ni LibreOffice.
Los marcados necesitan un motor opcional, que solo se descarga cuando hace falta: ¹ Ghostscript, ² LibreOffice, ³ Pandoc, ⁴ calibre, ⁵ FFmpeg ([detalles](docs/ENGINES.md)).

## Línea de comandos

El comando `filee` usa los mismos motores y ajustes preestablecidos que la aplicación (lo añade el instalador o `install.sh`).

```
filee convert photo.heic --to jpg
filee convert *.png --to webp --quality 80 -o converted
filee watch D:\Inbox --to pdf --move-originals
```

Todos los comandos y opciones: [filee.sh/es/cli](https://filee.sh/es/cli/) o `filee help`.

## Desarrollo

Compila con el SDK de .NET 10 en Windows, macOS o Linux:

```bash
git clone https://github.com/WDHUS4G/Filee.git
cd Filee
pwsh build/fetch-engines.ps1 -Only rhwp,pandoc   # optional engines
dotnet run --project src/Filee.App
dotnet test
```

[Arquitectura](docs/ARCHITECTURE.md) · [Motores](docs/ENGINES.md) · [Añadir un conversor](docs/ADDING-A-CONVERTER.md) · [Añadir un idioma](docs/ADDING-A-LANGUAGE.md) · [Cómo contribuir](CONTRIBUTING.md)

Las pull requests son bienvenidas.
Si Filee te ahorra unos cuantos clics, una ⭐ ayuda a que otras personas lo encuentren.

## Política de firma de código

Filee lo desarrolla y publica **Myeonggu Kim** ([KnifeLemon](https://github.com/KnifeLemon)).

- Las versiones para Windows se firman con un certificado de firma de código para código abierto de [Certum](https://www.certum.eu/) emitido a nombre de Myeonggu Kim.
  - Las versiones hasta la 2.0.0 no están firmadas.

La política completa y la política de privacidad están en el [README en inglés](README.md#code-signing-policy).
Filee no sube tus archivos ni envía datos de uso.

## Patrocinar

Filee es gratis y lo seguirá siendo.
Si quieres apoyarlo, elige la forma que mejor te venga ([Fairy](https://fairy.hada.io/@filee) acepta tarjetas coreanas y pagos rápidos).

**Tu nombre o tu logo aquí.**
Quienes apoyan cada mes aparecen aquí con su nombre, y los patrocinadores tienen su logo aquí y en la página Acerca de de Filee.
Si apoyas con un pago único, también puedes pedir que te incluyamos.

<!-- sponsors: src/Filee.App/Assets/Sponsors/sponsors.json -->

<p align="center">
  <a href="https://github.com/sponsors/KnifeLemon"><img src="https://img.shields.io/badge/GitHub%20Sponsors-EA4AAA?style=for-the-badge&logo=githubsponsors&logoColor=white" alt="GitHub Sponsors"></a>
  <a href="https://patreon.com/KnifeLemon"><img src="https://img.shields.io/badge/Patreon-000000?style=for-the-badge&logo=patreon&logoColor=white" alt="Patreon"></a>
  <a href="https://ko-fi.com/knifelemon"><img src="https://img.shields.io/badge/Ko--fi-FF6433?style=for-the-badge&logo=kofi&logoColor=white" alt="Ko-fi"></a>
  <a href="https://paypal.me/knifelemon"><img src="https://img.shields.io/badge/PayPal-002991?style=for-the-badge&logo=paypal&logoColor=white" alt="PayPal"></a>
  <a href="https://fairy.hada.io/@filee"><img src="https://img.shields.io/badge/Fairy-2E9E6B?style=for-the-badge" alt="Fairy"></a>
</p>

## Licencia

MIT, © 2026 Myeonggu Kim (KnifeLemon) y colaboradores de Filee.
Los componentes de terceros incluidos mantienen sus propias licencias, recogidas en [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

<p align="center">
  <a href="https://star-history.com/#WDHUS4G/Filee&Date"><img src="https://api.star-history.com/svg?repos=WDHUS4G/Filee&type=Date" alt="Star history" width="600"></a>
</p>
