<p align="center">
  <a href="https://filee.sh"><img src="docs/media/banner-en.jpg" alt="Filee: Перетащите. Отпустите. Готово." width="100%"></a>
</p>

<p align="center">
  <b>Бесплатный конвертер файлов с открытым кодом, которым пользуются перетаскиванием.</b><br>
  Зажмите клавишу (<kbd>Ctrl</kbd>, на macOS <kbd>Option</kbd>), перетащите файлы и отпустите их на нужный формат в круговом меню, которое откроется у курсора.
</p>

<p align="center">
  <a href="https://filee.sh"><b>Сайт</b></a> ·
  <a href="https://github.com/WDHUS4G/Filee/releases/latest"><b>Скачать</b></a> ·
  <a href="https://www.linkedin.com/company/filee-sh/"><b>LinkedIn</b></a> ·
  <a href="#установка">Установка</a> ·
  <a href="#командная-строка">Командная строка</a> ·
  <a href="#для-разработчиков">Для разработчиков</a>
</p>

<p align="center">
  <a href="https://github.com/WDHUS4G/Filee/actions/workflows/ci.yml"><img src="https://github.com/WDHUS4G/Filee/actions/workflows/ci.yml/badge.svg" alt="CI"></a>
  <a href="https://github.com/WDHUS4G/Filee/releases/latest"><img src="https://img.shields.io/github/v/release/WDHUS4G/Filee?color=7F77DD&label=release" alt="Последний выпуск"></a>
  <a href="https://github.com/WDHUS4G/Filee/releases"><img src="https://img.shields.io/github/downloads/WDHUS4G/Filee/total?color=1D9E75" alt="Загрузки"></a>
  <img src="https://img.shields.io/badge/Windows-10%20%7C%2011-3C3489" alt="Windows 10 | 11">
  <img src="https://img.shields.io/badge/Linux-x64%20%7C%20ARM64-3C3489" alt="Linux x64 | ARM64">
  <img src="https://img.shields.io/badge/macOS-preview-534AB7" alt="macOS — предварительная версия">
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-6A62C9" alt="Лицензия MIT"></a>
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
  <a href="README.zh-CN.md">简体中文</a> ·
  <a href="README.zh-TW.md">繁體中文</a> ·
  <a href="README.pl.md">Polski</a> ·
  <a href="README.hi.md">हिन्दी</a> ·
  <b>Русский</b> ·
  <a href="README.ar.md">العربية</a> ·
  <a href="README.pt-BR.md">Português (Brasil)</a> ·
  <a href="README.pt-PT.md">Português (Portugal)</a> ·
  <a href="README.fr.md">Français</a> ·
  <a href="README.th.md">ไทย</a>
</p>

<table>
  <tr>
    <td width="33%" valign="top">
      <b>Прямо у курсора</b><br>
      Не нужно открывать окно программы.
      Форматы появляются там, где вы уже работаете, и только те, что подходят перетаскиваемым файлам.
    </td>
    <td width="33%" valign="top">
      <b>263 форматов без лишних программ</b><br>
      Изображения, PDF, Word, Excel, PowerPoint, HWP, электронные книги, архивы, шрифты, чертежи CAD, 3D-модели и геоданные работают без Office, Hancom Office и LibreOffice.
    </td>
    <td width="33%" valign="top">
      <b>Всё остаётся на вашем компьютере</b><br>
      Ничего не загружается в сеть.
      Filee работает офлайн и сохраняет результат рядом с исходным файлом.
    </td>
  </tr>
</table>

## Как это работает

<p align="center">
  <a href="https://filee.sh"><img src="docs/media/demo-en.webp" alt="Файл trip.jpg перетаскивают с зажатой клавишей Ctrl: у курсора открывается круговое меню форматов, файл отпускают на PNG, кольцо прогресса замыкается и появляется trip.png" width="820"></a><br>
  <sub>Посмотрите 27-секундное видео и попробуйте интерактивное демо на <a href="https://filee.sh">filee.sh</a></sub>
</p>

## Установка

Скачайте со страницы [Releases](https://github.com/WDHUS4G/Filee/releases/latest).

- **Windows 10/11**: запустите `Filee-<version>-win-Setup.exe` или распакуйте `win-Portable.zip` и запустите `Filee.exe`.
  - Setup — собственный установщик Filee: он говорит на тех же языках, что и Filee, и ничего не требует от компьютера.
  - Он может добавить пункт в меню Проводника, автозапуск при входе в систему, команду `filee` и дополнительные движки.
  - Для тихой установки подходят ключи Inno Setup (`/VERYSILENT /SUPPRESSMSGBOXES`).
- **macOS 13+** (предварительная версия): распакуйте пакет `osx-arm64` или `osx-x64` и запустите `./install.sh`.
  - Или откройте `.dmg` и перетащите Filee в «Программы» (команду `filee` добавляет только `install.sh`).
  - Сборка пока не нотаризована, поэтому при первом запуске разрешите её в Системных настройках → Конфиденциальность и безопасность → **Всё равно открыть**.
- **Linux x64/ARM64** (Ubuntu 22.04+): распакуйте пакет и запустите `./install.sh` (права root не нужны, требуется Python 3).
  - Жесты перетаскивания работают только в X11.
    В Wayland пользуйтесь зоной перетаскивания и контекстным меню файлового менеджера.

Filee сообщает о новых версиях в своём меню и обновляется сам, сохраняя ваши настройки и движки.
После обновления несколько слайдов расскажут, что нового.

**Windows: Filee не запускается (ни окна, ни ошибки)?**
Так бывает в давно не обновлявшейся Windows 10 на ПК, процессор которого поддерживает аппаратную защиту стека (CET).
Windows включает эту защиту, но в ней нет компонента, нужного .NET, поэтому Filee и Setup сразу закрываются.

- **Рекомендуется:** установите доступные обновления Windows (последнее накопительное обновление).
- **Если обновить Windows нельзя:** обычно достаточно отключить защиту только для Filee.
  Выполните в PowerShell от имени администратора:

```powershell
Set-ProcessMitigation -Name Filee.exe -Disable UserShadowStack
Set-ProcessMitigation -Name filee-cli.exe -Disable UserShadowStack
```

То же сделайте с именем файла Setup (например, `Filee-<version>-win-Setup.exe`) или возьмите портативный ZIP: ему хватит этих двух строк.
Чтобы отменить это, удалите эти записи в «Безопасность Windows» → «Управление приложениями/браузером» → «Параметры защиты от эксплойтов» → «Параметры программ».

## Возможности

- **Круговое меню у курсора**: зажмите клавишу, перетащите файлы и отпустите их на формат.
  Показываются только форматы, подходящие этим файлам.
- **Прямо из браузера**: так же перетаскиваются картинка, ссылка на файл или загрузка из Chrome, Edge, Firefox или Safari.
  - Filee сохраняет файл в папку «Загрузки» (или оставляет только сконвертированный) и конвертирует его.
  - Ссылки скачиваются без входа в аккаунт из вашего браузера.
- **Своё круговое меню для каждого типа файлов**, настраивается перетаскиванием, с пресетами для:
  - качества, размера, DPI
  - страниц и сжатия PDF
  - видео и аудио
  - папки сохранения и имён файлов
- **У каждого кругового меню две стороны**: когда меню открыто, нажмите <kbd>Tab</kbd> или прокрутите колесо мыши, чтобы перевернуть его к редким форматам и инструментам.
- **Обрезка перед конвертацией**: сектор «Обрезать» открывает картинки по одной.
  Перетащите рамку, выберите пропорции, и сохранится только эта часть.
- **Много файлов сразу**, параллельно.
  Конвертации в несколько шагов (HWP → HWPX → DOCX) Filee находит сам.
- **Папки наблюдения** конвертируют каждый файл, который в них попадает.
- **Недавние конвертации** на главной, у каждой миниатюра: результат можно открыть («Открыть файл») или открыть в другой программе («Открыть с помощью»).
- **Другие способы**: контекстное меню, горячая клавиша, зона перетаскивания и [команда `filee`](#командная-строка).
- Светлая и тёмная темы со своим акцентным цветом и непрозрачностью кругового меню, интерфейс на языках English, Español, 日本語, 한국어, 简体中文, 繁體中文, Polski, हिन्दी, Русский, العربية (справа налево), Português (Brasil), Français, नेपाली, ไทย, Türkçe и Português (Portugal).

## Скриншоты

<table>
  <tr>
    <td width="50%"><img src="docs/media/page-toolbar-en.png" alt="Редактор кругового меню с профилями и пресетами"></td>
    <td width="50%"><img src="docs/media/page-home-en.png" alt="Главный экран с зоной перетаскивания и недавними конвертациями"></td>
  </tr>
  <tr>
    <td align="center"><sub>Настраивайте круговое меню перетаскиванием, по профилю на каждый тип файлов</sub></td>
    <td align="center"><sub>Главная: зона перетаскивания и недавние конвертации</sub></td>
  </tr>
</table>

## Форматы

| Из | В |
|---|---|
| **Изображения**: JPG, PNG, WEBP, AVIF, TIFF, BMP, GIF, APNG, ICO, ICNS, JPEG XL, JPEG 2000, PSD/PSB, TGA, PPM, HEIC, QOI, DDS, PCX, MNG, CUR, …;<br>GIMP XCF, RAW с камер (CR2, CR3, NEF, ARW, DNG, …), DICOM, OpenEXR, HDR, FITS, JPEG XR (Windows), Live Photo iPhone (LIVP) только чтение | любой из них (HEIC⁵), PDF, SVG (трассировка), изменение размера, оттенки серого;<br>Live/Motion Photo (iPhone, Android) → их видео (MP4, MOV⁵), а видео → Live Photo (iPhone) или Motion Photo (Android)⁵, экспериментально |
| **Векторная графика**: SVG, SVGZ, EMF, WMF, AI<br>EPS, PS¹<br>CDR, VSD, CGM, ODG, FODG² | PDF (векторный), PNG и другие изображения,<br>SVG ↔ SVGZ, SVG → DXF/DWG, PDF → EPS/PS¹, SVG → EMF, WMF, ODG² |
| **PDF** | DOCX, HWPX, TXT, PNG, JPG, TIFF, CBZ, PPTX (каждая страница картинкой), SVG²;<br>объединение (в выбранном заранее порядке), разделение, диапазоны страниц, сжатие (PDF меньше по размеру) |
| **Документы**: DOCX, DOCM, DOTX, EML, Outlook MSG;<br>OFD (китайские электронные счета, официальные документы) только чтение<br>DOC, ODT, FODT, OTT, RTF, WPS, WPD, Pages, ClarisWorks, MacWrite, T602, …² | PDF, DOCX, HWPX, EPUB, TXT, HTML;<br>MSG → EML<br>ODT, FODT, OTT² |
| **Таблицы**: XLSX, XLSM, XLS, XLT, ODS, CSV, TSV<br>OTS, FODS, Numbers, ET² | PDF, XLSX, ODS, CSV, TSV, JSON, HWPX, HTML<br>XLT, OTS² |
| **Презентации**: PPTX, PPTM, POTX, POTM, PPSX<br>PPT, ODP, OTP, FODP, Keynote² | PDF, PNG, JPG, DOCX, HWPX<br>OTP² |
| **HWP**: HWP, HWPX | PDF, PNG, DOCX, HWPX ↔ HWP, TXT, Markdown, HTML |
| **Текст**: TXT, LOG, исходники скриптов (PY, PAC, BAT, COMMAND; только чтение, без запуска), Markdown, HTML;<br>MHTML (сохранённые веб-страницы) только чтение<br>reStructuredText, LaTeX, Org, AsciiDoc, Typst, MediaWiki, DocBook, Jupyter, Textile, man, OPML³ | TXT в UTF-8, PDF, DOCX, HWPX, EPUB<br>друг в друга, слайды PPTX³ |
| **Электронные книги**: EPUB, KEPUB, MOBI, AZW3, AZW, AZW4, FB2, CBZ, CBR, CB7, HTMLZ, TXTZ<br>LIT, LRF, CHM, PDB, …⁴ | PDF, EPUB, KEPUB (Kobo), DOCX, TXT, HTML, HWPX<br>MOBI, AZW3⁴ |
| **Видео**⁵: MP4, MOV, MKV, WEBM, AVI, WMV, FLV, MPEG, Video CD (DAT), TS, M2TS, 3GP, OGV, VOB, … | MP4 (H.264, HEVC, AV1), WEBM, MOV, MKV, AVI, GIF, APNG, MP3, M4A, 720p,<br>субтитры (SRT, ASS, WebVTT) |
| **Аудио**⁵: MP3, M4A, AAC, WAV, FLAC, OGG, OPUS, WMA, AIFF, AMR, APE, WavPack, TTA, DSD (DSF, DFF), DTS, AC3, E-AC3, … | MP3, M4A (AAC, ALAC), WAV, FLAC, OGG, OPUS, AAC, WavPack, TTA, W64, AC3, E-AC3 |
| **Субтитры**: SRT, ASS/SSA, WebVTT, SAMI (SMI), LRC, TTML, MicroDVD, SCC | друг в друга, в UTF-8 (корейские файлы в CP949 читаются правильно) |
| **Архивы**: ZIP, ANSYS Workbench (WBPZ), 7Z, RAR, TAR, TAR.GZ, TAR.BZ2, TAR.XZ, GZ, ISO, CAB, DMG, ALZ, EGG, … | распаковка, ZIP, 7Z, TAR, TAR.GZ;<br>любые файлы → один ZIP |
| **CAD**: DWG, DXF | PDF, SVG, PNG, DWG ↔ DXF |
| **Шрифты**: TTF, OTF, WOFF, WOFF2, EOT | друг в друга |
| **Данные**: JSON, JSON Lines, YAML, TOML, XML<br>vCard, iCalendar | друг в друга, CSV и XLSX (таблицы);<br>vCard, iCalendar ↔ CSV |
| **3D-модели**: GLB, glTF, STL, OBJ (+ MTL), 3MF, PLY;<br>FBX только чтение | друг в друга, превью в PNG |
| **Геоданные (векторные)**: Shapefile, GeoJSON, KML, KMZ, GPX<br>CSV с координатами | друг в друга и CSV / XLSX / JSON;<br>данные в проекциях (UTM, Web Mercator, Ламберта, Альберса) переводятся в долготу / широту WGS 84 |

Форматы без пометки работают сразу, без Office, Hancom Office и LibreOffice.
Для помеченных нужен дополнительный движок, который скачивается только при необходимости: ¹ Ghostscript, ² LibreOffice, ³ Pandoc, ⁴ calibre, ⁵ FFmpeg ([подробнее](docs/ENGINES.md)).

## Командная строка

Команда `filee` использует те же движки и пресеты, что и приложение (её добавляет установщик или `install.sh`).

```
filee convert photo.heic --to jpg
filee convert *.png --to webp --quality 80 -o converted
filee watch D:\Inbox --to pdf --move-originals
```

Все команды и параметры: [filee.sh/cli](https://filee.sh/cli/) или `filee help`.

## Для разработчиков

Соберите проект с .NET 10 SDK на Windows, macOS или Linux:

```bash
git clone https://github.com/WDHUS4G/Filee.git
cd Filee
pwsh build/fetch-engines.ps1 -Only rhwp,pandoc   # optional engines
dotnet run --project src/Filee.App
dotnet test
```

[Архитектура](docs/ARCHITECTURE.md) · [Движки](docs/ENGINES.md) · [Как добавить конвертер](docs/ADDING-A-CONVERTER.md) · [Как добавить язык](docs/ADDING-A-LANGUAGE.md) · [Участие в разработке](CONTRIBUTING.md)

Пул-реквесты приветствуются.
Если Filee экономит вам пару кликов, поставьте ⭐: так другие смогут его найти.

## Подпись кода

Filee разрабатывает и публикует **Myeonggu Kim** ([KnifeLemon](https://github.com/KnifeLemon)).

- Выпуски для Windows подписываются сертификатом [Certum](https://www.certum.eu/) для подписи кода open source, выданным на имя Myeonggu Kim.
  - Выпуски до 2.0.0 включительно не подписаны.

Полные правила и политику конфиденциальности смотрите в [английском README](README.md#code-signing-policy).
Filee не отправляет ни ваши файлы, ни данные об использовании.

## Поддержать проект

Filee бесплатный и останется бесплатным.
Если хотите его поддержать, выберите удобный способ ([Fairy](https://fairy.hada.io/@filee) принимает корейские карты и платёжные сервисы).

**Здесь может быть ваше имя или логотип.**
Тех, кто поддерживает проект ежемесячно, мы перечисляем здесь по имени, а логотипы спонсоров размещаем здесь и в разделе «О программе» в Filee.
Если вы помогли разово, тоже можете попросить добавить вас в список.

<!-- sponsors: src/Filee.App/Assets/Sponsors/sponsors.json -->

<p align="center">
  <a href="https://github.com/sponsors/KnifeLemon"><img src="https://img.shields.io/badge/GitHub%20Sponsors-EA4AAA?style=for-the-badge&logo=githubsponsors&logoColor=white" alt="GitHub Sponsors"></a>
  <a href="https://patreon.com/KnifeLemon"><img src="https://img.shields.io/badge/Patreon-000000?style=for-the-badge&logo=patreon&logoColor=white" alt="Patreon"></a>
  <a href="https://ko-fi.com/knifelemon"><img src="https://img.shields.io/badge/Ko--fi-FF6433?style=for-the-badge&logo=kofi&logoColor=white" alt="Ko-fi"></a>
  <a href="https://paypal.me/knifelemon"><img src="https://img.shields.io/badge/PayPal-002991?style=for-the-badge&logo=paypal&logoColor=white" alt="PayPal"></a>
  <a href="https://fairy.hada.io/@filee"><img src="https://img.shields.io/badge/Fairy-2E9E6B?style=for-the-badge" alt="Fairy"></a>
</p>

## Лицензия

MIT, © 2026 Myeonggu Kim (KnifeLemon) и участники проекта Filee.
Сторонние компоненты в комплекте распространяются под собственными лицензиями, перечисленными в [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

<p align="center">
  <a href="https://star-history.com/#WDHUS4G/Filee&Date"><img src="https://api.star-history.com/svg?repos=WDHUS4G/Filee&type=Date" alt="Star history" width="600"></a>
</p>
