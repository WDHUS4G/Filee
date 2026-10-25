# Changelog

What changed in each version of Filee, for the people using it. The release workflow puts the section of the
version it publishes under "What's Changed" in the GitHub release notes, above the list of pull requests.

Add a `## <version>` section before tagging a release. Write what someone using Filee notices: what's new, where to
find it, what it does and doesn't do.

Start the section with an `<!-- update -->` block (hidden on GitHub). The app's update notice shows its level and its
one-line summaries in the user's language (any language Filee ships: `ko`, `en`, `zh`, `pl`, `pt-BR`, …; a line per
change, a language may repeat; a language without lines shows the English ones):

- `required`: wrong or lost results, security, Filee not starting. The card shows every time Filee starts.
- `recommended`: bug fixes. The card shows once per version.
- `optional`: new features only. No card, the sidebar button only.

## 2.1.0

<!-- update
level: recommended
ko: Windows용 파일에 코드 서명이 들어가서 스마트 앱 컨트롤이나 백신이 Filee를 막는 일이 줄어요
ko: 스캔본과 그림의 글자를 인식(OCR)해서 검색되는 PDF와 편집할 수 있는 문서로 만들어요
ko: 사진·동영상·오디오를 압축하고 합치고 정보를 지울 수 있고, 동영상에서 클립을 잘라 낼 수 있어요
ko: PDF 페이지를 편집하고, 어떤 PDF에든 워터마크·쪽 번호·암호를 넣을 수 있어요
ko: PDF의 표가 엑셀 시트가 되고, 큰 엑셀 파일을 훨씬 빨리 PDF로 바꿔요
ko: PDF로 저장하기 전에 스프레드시트 페이지를 배치하고, 저장할 AutoCAD 버전을 고를 수 있어요
ko: 원본 바꾸기를 켜면 결과가 원본 자리에 들어가고 원본은 휴지통으로 가요
ko: FITS 천체 사진, iCalendar, HAR, USM 게임 동영상, 자수·플로터 파일을 지원하고 CAD 레이아웃마다 PDF 한 쪽을 만들어요
ko: 프랑스어, 번체 중국어, 네팔어, 태국어, 튀르키예어, 유럽 포르투갈어와 Material You 테마, 창 크기 조절을 지원해요
ko: Linux에서는 Filee 안에서 업데이트하고, GNOME·Hyprland Wayland에서 끌기 제스처를 쓸 수 있어요(실험 기능)
ko: 중국어 텍스트가 한글로 읽히던 문제, Word 기호가 빠지던 문제, 트레이에서 창이 두 개 뜨던 문제 등을 고쳤어요
en: Windows files are now code-signed, so Smart App Control and antivirus programs block Filee less often
en: Recognize text in scans and pictures (OCR): searchable PDFs and documents you can edit
en: Compress, merge and clean up photos, videos and audio, and cut a clip out of a video
en: Edit PDF pages, and add a watermark, page numbers or a password to any PDF
en: Tables in PDFs become Excel sheets, and large Excel files turn into PDFs much faster
en: Lay out spreadsheet pages before saving a PDF, and choose the AutoCAD version to save in
en: Replace the original: the result takes its place and the original goes to the Recycle Bin
en: FITS astrophotos, iCalendar, HAR, USM game videos, embroidery and plotter files, and a PDF page per CAD layout
en: French, Traditional Chinese, Nepali, Thai, Turkish and European Portuguese, a Material You theme and window zoom
en: Linux: updates from inside Filee, and drag gestures on GNOME and Hyprland Wayland (experimental)
en: Fixed Chinese text read as Korean, missing Word symbols, a second window from the tray and more
zh: Windows 文件现已带有代码签名，智能应用控制和杀毒软件拦截 Filee 的情况会更少
zh: 识别扫描件和图片中的文字（OCR），生成可搜索的 PDF 和可编辑的文档
zh: 可以压缩、合并照片、视频和音频并清除其元数据，还能从视频中剪出片段
zh: 可以编辑 PDF 页面，并为任意 PDF 添加水印、页码或密码
zh: PDF 中的表格可转为 Excel 工作表，大型 Excel 文件转 PDF 也快得多
zh: 保存为 PDF 前可以排版电子表格页面，还可以选择保存的 AutoCAD 版本
zh: 替换原文件：结果放到原文件的位置，原文件移到回收站
zh: 支持 FITS 天文照片、iCalendar、HAR、USM 游戏视频、刺绣和绘图仪文件，CAD 的每个布局各成一页 PDF
zh: 新增法语、繁体中文、尼泊尔语、泰语、土耳其语和欧洲葡萄牙语界面，以及 Material You 主题和窗口缩放
zh: Linux：可在 Filee 内直接更新，并支持 GNOME 和 Hyprland Wayland 上的拖动手势（实验性）
zh: 修复了中文文本被读成韩文、Word 符号丢失、从托盘打开两个窗口等问题
pl: Pliki dla Windows są teraz podpisane cyfrowo, więc Inteligentna kontrola aplikacji i antywirusy rzadziej blokują Filee
pl: Rozpoznawanie tekstu w skanach i obrazach (OCR): przeszukiwalne pliki PDF i dokumenty do edycji
pl: Kompresja, łączenie i czyszczenie zdjęć, wideo i dźwięku oraz wycinanie klipu z wideo
pl: Edycja stron PDF oraz znak wodny, numery stron lub hasło w każdym pliku PDF
pl: Tabele z PDF stają się arkuszami Excela, a duże pliki Excela znacznie szybciej zamieniają się w PDF
pl: Układ stron arkusza przed zapisem do PDF i wybór wersji AutoCAD przy zapisie
pl: Zastąp oryginał: wynik zajmuje jego miejsce, a oryginał trafia do Kosza
pl: Zdjęcia astronomiczne FITS, iCalendar, HAR, filmy z gier USM, pliki hafciarskie i plotterowe oraz strona PDF na każdy układ CAD
pl: Język francuski, chiński tradycyjny, nepalski, tajski, turecki i portugalski europejski, motyw Material You i powiększanie okien
pl: Linux: aktualizacje z poziomu Filee i gesty przeciągania w GNOME i Hyprland na Wayland (eksperymentalne)
pl: Poprawiono chiński tekst odczytywany jako koreański, znikające symbole z Worda, drugie okno z zasobnika i więcej
es: Los archivos de Windows ahora están firmados, así que Control inteligente de aplicaciones y los antivirus bloquean Filee con menos frecuencia
es: Reconoce el texto de escaneos e imágenes (OCR): PDF con búsqueda y documentos editables
es: Comprime, une y limpia fotos, vídeos y audio, y recorta un clip de un vídeo
es: Edita páginas de PDF y añade una marca de agua, números de página o una contraseña a cualquier PDF
es: Las tablas de los PDF pasan a hojas de Excel, y los Excel grandes pasan a PDF mucho más rápido
es: Prepara las páginas de una hoja de cálculo antes de guardarla en PDF, y elige la versión de AutoCAD al guardar
es: Reemplazar el original: el resultado ocupa su lugar y el original va a la Papelera
es: Astrofotos FITS, iCalendar, HAR, vídeos de juegos USM, archivos de bordado y de plóter, y una página de PDF por presentación de CAD
es: Francés, chino tradicional, nepalí, tailandés, turco y portugués europeo, un tema Material You y zoom de ventanas
es: Linux: actualizaciones desde Filee y gestos de arrastre en GNOME e Hyprland con Wayland (experimental)
es: Corregidos el texto chino leído como coreano, los símbolos de Word que faltaban, una segunda ventana desde la bandeja y más
hi: Windows की फ़ाइलें अब कोड-साइन की हुई हैं, इसलिए स्मार्ट ऐप कंट्रोल और एंटीवायरस Filee को कम रोकते हैं
hi: स्कैन और चित्रों का टेक्स्ट पहचानें (OCR): खोजने योग्य PDF और संपादन योग्य दस्तावेज़
hi: फ़ोटो, वीडियो और ऑडियो को कंप्रेस करें, जोड़ें और उनका मेटाडेटा हटाएँ, और वीडियो से क्लिप काटें
hi: PDF के पेज संपादित करें, और किसी भी PDF में वॉटरमार्क, पेज नंबर या पासवर्ड जोड़ें
hi: PDF की तालिकाएँ Excel शीट बनती हैं, और बड़ी Excel फ़ाइलें बहुत तेज़ी से PDF बनती हैं
hi: PDF सहेजने से पहले स्प्रेडशीट के पेज व्यवस्थित करें, और सहेजने के लिए AutoCAD संस्करण चुनें
hi: मूल फ़ाइल बदलें: परिणाम उसकी जगह लेता है और मूल फ़ाइल रीसायकल बिन में जाती है
hi: FITS खगोल फ़ोटो, iCalendar, HAR, USM गेम वीडियो, कढ़ाई और प्लॉटर फ़ाइलें, और हर CAD लेआउट का एक PDF पेज
hi: फ़्रेंच, पारंपरिक चीनी, नेपाली, थाई, तुर्की और यूरोपीय पुर्तगाली भाषाएँ, Material You थीम और विंडो ज़ूम
hi: Linux: Filee के अंदर से अपडेट, और GNOME व Hyprland Wayland पर ड्रैग जेस्चर (प्रायोगिक)
hi: चीनी टेक्स्ट का कोरियाई पढ़ा जाना, Word के गायब चिह्न, ट्रे से दूसरी विंडो खुलना और कई गड़बड़ियाँ ठीक की गईं
ja: Windows 版のファイルにコード署名が付き、スマート アプリ コントロールやウイルス対策ソフトに止められにくくなりました
ja: スキャンや画像の文字を認識（OCR）して、検索できる PDF や編集できる文書にできます
ja: 写真・動画・音声の圧縮、結合、メタデータの削除と、動画からのクリップの切り出しができます
ja: PDF のページを編集し、どの PDF にも透かし・ページ番号・パスワードを付けられます
ja: PDF の表が Excel のシートになり、大きな Excel ファイルもずっと速く PDF にできます
ja: PDF に保存する前に表計算のページをレイアウトでき、保存する AutoCAD のバージョンも選べます
ja: 元のファイルを置き換える：結果がその場所に入り、元のファイルはごみ箱へ移ります
ja: FITS の天体写真、iCalendar、HAR、USM のゲーム動画、刺しゅう・プロッターのファイルに対応し、CAD のレイアウトごとに PDF のページを作ります
ja: フランス語、繁体字中国語、ネパール語、タイ語、トルコ語、ヨーロッパ・ポルトガル語、Material You テーマ、ウィンドウの拡大・縮小に対応しました
ja: Linux：Filee の中から更新でき、GNOME と Hyprland の Wayland でドラッグ操作が使えます（試験的）
ja: 中国語のテキストが韓国語として読まれる問題、Word の記号が消える問題、タスクトレイから二つ目のウィンドウが開く問題などを修正しました
ru: Файлы для Windows теперь подписаны, поэтому Интеллектуальное управление приложениями и антивирусы реже блокируют Filee
ru: Распознавание текста на сканах и картинках (OCR): PDF с поиском и редактируемые документы
ru: Сжатие, объединение и очистка фото, видео и аудио, а также вырезание клипа из видео
ru: Редактирование страниц PDF, а также водяной знак, номера страниц или пароль для любого PDF
ru: Таблицы из PDF становятся листами Excel, а большие файлы Excel намного быстрее превращаются в PDF
ru: Разметка страниц таблицы перед сохранением в PDF и выбор версии AutoCAD для сохранения
ru: Заменить оригинал: результат занимает его место, а оригинал отправляется в Корзину
ru: Астрофото FITS, iCalendar, HAR, игровые видео USM, файлы вышивки и плоттера, а также страница PDF на каждый лист CAD
ru: Французский, традиционный китайский, непальский, тайский, турецкий и европейский португальский, тема Material You и масштаб окон
ru: Linux: обновления прямо из Filee и жесты перетаскивания в GNOME и Hyprland на Wayland (экспериментально)
ru: Исправлены чтение китайского текста как корейского, пропадающие символы Word, второе окно из трея и другое
ar: أصبحت ملفات Windows موقّعة رقميًا، فيقلّ حظر التحكم الذكي في التطبيقات وبرامج مكافحة الفيروسات لـ Filee
ar: التعرّف على النص في المستندات الممسوحة والصور (OCR): ملفات PDF قابلة للبحث ومستندات قابلة للتحرير
ar: ضغط الصور والفيديو والصوت ودمجها وتنظيفها، واقتطاع مقطع من فيديو
ar: تحرير صفحات PDF، وإضافة علامة مائية أو أرقام صفحات أو كلمة مرور إلى أي ملف PDF
ar: تتحول جداول PDF إلى أوراق Excel، وتتحول ملفات Excel الكبيرة إلى PDF بسرعة أكبر بكثير
ar: تنسيق صفحات جدول البيانات قبل حفظه بصيغة PDF، واختيار إصدار AutoCAD عند الحفظ
ar: استبدال الأصل: تأخذ النتيجة مكانه وينتقل الأصل إلى سلة المحذوفات
ar: صور الفلك FITS وiCalendar وHAR وفيديوهات الألعاب USM وملفات التطريز والراسمات، وصفحة PDF لكل تخطيط CAD
ar: الفرنسية والصينية التقليدية والنيبالية والتايلاندية والتركية والبرتغالية الأوروبية، ومظهر Material You وتكبير النوافذ
ar: Linux: التحديث من داخل Filee، وإيماءات السحب في GNOME وHyprland على Wayland (تجريبي)
ar: إصلاح قراءة النص الصيني كأنه كوري، واختفاء رموز Word، وفتح نافذة ثانية من منطقة الإعلام، وغير ذلك
pt-BR: Os arquivos para Windows agora são assinados, então o Controle Inteligente de Aplicativos e os antivírus bloqueiam o Filee com menos frequência
pt-BR: Reconheça o texto de digitalizações e imagens (OCR): PDFs pesquisáveis e documentos editáveis
pt-BR: Comprima, junte e limpe fotos, vídeos e áudio, e corte um trecho de um vídeo
pt-BR: Edite páginas de PDF e adicione marca d'água, números de página ou senha a qualquer PDF
pt-BR: Tabelas de PDF viram planilhas do Excel, e arquivos grandes do Excel viram PDF muito mais rápido
pt-BR: Organize as páginas de uma planilha antes de salvar em PDF e escolha a versão do AutoCAD ao salvar
pt-BR: Substituir o original: o resultado fica no lugar dele e o original vai para a Lixeira
pt-BR: Astrofotos FITS, iCalendar, HAR, vídeos de jogos USM, arquivos de bordado e de plotter, e uma página de PDF por layout de CAD
pt-BR: Francês, chinês tradicional, nepalês, tailandês, turco e português europeu, um tema Material You e zoom das janelas
pt-BR: Linux: atualizações dentro do Filee e gestos de arrastar no GNOME e no Hyprland com Wayland (experimental)
pt-BR: Corrigidos texto chinês lido como coreano, símbolos do Word que sumiam, uma segunda janela pela bandeja e mais
-->

### New

- **Signed for Windows** (issue #333). 2.1.0 is the first Filee whose Windows files are code-signed, with a Certum
  open source certificate issued to "Open Source Developer MYEONGGU KIM". `Filee-2.1.0-win-Setup.exe`, the portable
  zip's files, `Filee.exe`, `filee-cli.exe`, the uninstaller and the bundled programs and libraries that came unsigned
  (rhwp, 7-Zip and most libraries) carry that signature; files their makers signed (.NET, ImageMagick, the Microsoft
  C++ runtime …) keep their own. Smart App Control and other Application Control policies, which could refuse to load
  parts of an unsigned Filee, let it start, antivirus programs flag it less often, and Windows names the publisher
  instead of "Unknown publisher". SmartScreen can still warn about a new release until it has been downloaded enough
  ("More info" → "Run anyway"). macOS packages stay ad-hoc signed and not notarized.
- **Text recognition (OCR)** (issue #288, by @cre8ive-marketing; Apple Vision by @smjrifle). Scans and pictures become
  searchable PDFs, and their text goes into DOCX, TXT, Markdown and HWPX: a scanned PDF → DOCX gives text you can edit
  instead of pictures of the pages. Tick **Recognize text (OCR)** in the PDF card of the preset editor (for PDF, DOCX,
  TXT, Markdown and HWPX targets) and pick the language of the text: 69 languages, each downloaded the first time it's
  needed (0.4 to 10 MB). The new **Searchable PDF** preset does it in one step; it's in the preset list, not on a
  donut, so add it in Settings → Donut toolbar. Pages that already have text are copied as they are, and a PDF with
  text on every page is left as it is, with a note. Bookmarks and form fields of a scanned PDF don't come along.
  - On Windows, **Tesseract** is a new engine in Settings → Engines (offered, not installed for you). On Linux and
    macOS, Filee uses the system's Tesseract (`sudo apt install tesseract-ocr`, `brew install tesseract`).
  - On macOS, **Apple Vision** reads the text with nothing to download. Languages it can't read (Hindi, Nepali, Greek
    …) need Tesseract.
- **Everyday media tools** (by @cre8ive-marketing). Eight new presets:
  - **Compress image**: the same format, smaller (quality 75, never above a JPEG's own; PNG and TIFF losslessly).
  - **Compress video**: H.264 MP4 at the Small quality, at most 1080p. **Compress audio**: AAC M4A at 128 kbit/s.
  - **Extract audio**: a video's sound as M4A, copied without re-encoding when it's AAC or ALAC.
  - **Merge videos** and **Merge audio**: the files joined into one, in the order you set in the merge order window.
    Clips recorded the same way are copied as they are; others are fitted into the first clip's frame and encoded.
  - **Video from images**: the pictures as a 1080p MP4, 3 s each with 1 s crossfades (both can be changed in the
    preset editor), up to 300 pictures.
  - **Remove metadata**: a copy named `{name}_clean` without EXIF, location, comments, tags, chapters, or a PDF's
    document information. Videos and audio aren't re-encoded, and MKV subtitle fonts stay.

  A compressed result that isn't smaller is replaced by the original, with a note. A new library has them on the
  back faces of the image, video, audio and PDF donuts (Extract Live Photo video leaves the image donut's back face to
  make room, and stays in the preset list); an updated library gets them where a back face has room, and Extract audio
  takes the M4A slice's place on the video donut's back. Video from images is on no donut. All but Compress image, and
  Remove metadata for pictures and PDFs, need FFmpeg.
- **Cut a clip** (by @flexeykinDev). The first slice on the back of the video donut (in FLAC's place; FLAC stays in
  the preset list) opens your videos one after another in a clip window. Pick the start and end on an overview of the
  whole video, or press 15 s / 30 s / 60 s; **Space** plays the clip with its sound. **C** cuts at the playhead,
  **Delete** takes a piece out, and dragging a piece's edge trims it. Each piece can have its own volume, mute and
  speed (0.25× to 4×, the pitch kept), and the clip can get music from another file, *Remove silence*, fades in and
  out, and Ctrl+Z / Ctrl+Y. *Fast, no re-encoding* copies a plain clip in a moment but starts at the key frame before
  the start; cuts, speed, volume or fades are encoded with the preset's settings. Results are saved as `{name}_clip`.
  Needs FFmpeg.
- **Edit PDF pages** (by @Perricheno). **Edit**, the first slice on the back of the PDF donut, opens into Crop, Rotate left,
  Rotate right, Pages, Split and Add. Crop and rotate change only the page boxes and the rotation, so text and
  drawings stay sharp and bookmarks and forms stay. **Pages** opens a page editor with thumbnails and a large preview:
  reorder, rotate and delete pages, add pages from another PDF, or extract the selected page as its own file.
- **Watermark, page numbers and a password** (by @cre8ive-marketing). The PDF card of the preset editor has three new
  options for every PDF a preset writes, whatever it is made from: a **Watermark** text ("DRAFT"), large and light
  grey across each page; **Page numbers** ("3 / 10" at the bottom); and a **Password to open** (AES-256). With *Merge
  into one PDF* the pages are numbered through. The watermark and the numbers can't be selected or searched, and a
  PDF/A can't have a password. The password is saved with the preset in Filee's settings, but left out of the history
  and of Settings → General → Export.
- **PDF tables to Excel** (issue #267, by @cre8ive-marketing). The XLSX preset now takes PDFs: every table drawn with
  lines becomes its own sheet, with merged cells, borders, column widths, and numbers, amounts, percentages and dates
  as real values (codes with a leading zero stay text). A table that goes on over the next page stays one sheet,
  without its repeated header. Text outside the tables is left out, tables without lines aren't found, and a PDF
  without ruled tables (or a scan) stops with a message. ODS and CSV work the same way. The XLSX preset isn't on the
  PDF donut; add it in Settings → Donut toolbar.
- **Spreadsheet Print Studio** (by @Perricheno). **PDF + Edit**, the first slice on the back of the spreadsheets
  donut, opens an Excel workbook before it becomes a PDF: the sheets in output order on the left, the real PDF pages
  in the middle, and the page setup on the right (print area, paper, portrait or landscape, scale or fit to pages,
  margins, page order, hidden rows and columns, manual page breaks). A sheet can be split into regions with a page
  setup each. When a workbook asks for a tiny scale, or a very wide sheet has no print area, it offers a suggested
  layout. The preview is exactly what is exported, and your workbook isn't changed. Needs LibreOffice.
- **Replace the original** (issue #332, by @cre8ive-marketing). A new option in the preset editor's output card, off
  unless you tick it. When a file's result has the same format as the file (Compress image, Rotate, Trim transparent
  borders …), the original goes to the Recycle Bin / Trash and the result takes its name and place. When the original
  can't go there (a network drive, a file open in another program), both are kept and the notice says why. It never
  applies to watch folders, files from a browser, ZIP results, files that become several, or the command line.
- **Trim transparent borders** (issue #332, by @cre8ive-marketing). A new preset on the image donut's back face, after
  Remove background: a copy named `name_trimmed`, cropped to the pixels that aren't fully transparent. Pictures without
  transparency and animations stay as they are.
- **More formats**, 263 in all.
  - **FITS** astrophotos (`.fits`, `.fts`; issue #460) from telescope cameras and capture or stacking programs such as
    N.I.N.A., on the image donut. To PNG, JPG and the other picture formats they're stretched automatically, like a
    capture program's preview, and a raw frame from a colour camera is debayered with the Bayer pattern in its header
    (*Debayer FITS from a colour camera* in the preset editor, on unless you turn it off). To TIFF they keep their
    data: *FITS (astronomy) to TIFF* is *Linear: the data as it is* (8 and 16 bits stay as they are, 32-bit and
    floating-point data become 32-bit floating point, never debayered) or *Stretched, like a preview*. PNG and JPG are
    8-bit. Tile-compressed files (fpack, `.fz`) stop with a message asking to `funpack` them, and `.fit` is left to
    Garmin FIT files.
  - **CRI USM** game videos (`.usm`; issue #485), used for the cutscenes of many games, to MP4 and the other video and
    audio formats, with FFmpeg. Encrypted USM files can't be read.
  - **iCalendar** (`.ics`; issue #340, by @SeoJCalive): events and to-dos become CSV, one row each, and through it
    JSON and the other data formats; CSV goes back to `.ics`. The new "iCalendar" preset is on the back of the
    spreadsheets donut, after vCard. Recurring events keep their rule; they aren't expanded.
  - **HAR** network logs (issue #328): to JSON, YAML, XML, or a table with one request per row (CSV, spreadsheets,
    PDF).
  - **Raw H.265 / H.264 streams** (`.h265`, `.265`, `.hevc`, `.h264`, `.264`; issue #301) to MP4 and the other video
    formats, with FFmpeg.
  - **Tajima DST embroidery** (issue #397): the stitches at real size as PNG, JPG, PDF or SVG, a colour per thread
    (DST stores none). AutoCAD sheet sets, which also use `.dst`, are refused with the reason.
  - **HP-GL plot files** (`.plt`, `.hpgl`, `.hpg`, `.hgl`; issue #410), also AutoCAD's "plot to file", to PDF, PNG,
    JPG and SVG with their pen colours and widths. Text labels and fill patterns aren't drawn.
  - **Lua, CSS and PowerShell** (`.lua`, `.css`, `.ps1`; issues #388, #368, #442) become text like the other scripts:
    read, never run.
  - **`.png_large` / `.jpg_large`** downloads open as PNG and JPEG (issue #461, by @Nitjsefnie).

  Your donuts learn the new extensions, unless one of your own donuts already takes them.
- **CAD drawings.**
  - **One page per layout** (issue #172, by @cre8ive-marketing). DWG / DXF → PDF makes a page for each paper-space
    layout, in tab order, with model space drawn through each viewport at its scale. Drawings without layouts come
    out as before, and SVG and pictures are still one picture of model space. Twisted views, layers frozen per
    viewport, clipped viewports and the layout's paper size and plot scale aren't followed yet.
  - **AutoCAD version** (issue #489, by @cre8ive-marketing). DWG and DXF presets have a "CAD drawing" card: 2018 (as
    before), 2013, 2010, 2007, 2004, 2000 or *Same as the source*, and DWG → DWG or DXF → DXF just changes the
    version. Objects the version doesn't have (multileaders, meshes …) are left out with a note. Before 2007, Korean,
    Chinese or Japanese text is saved in the code page that fits it. A 2007 DWG is saved as 2004, which AutoCAD 2007
    opens.
- **Material You** (by @gtxPrime). Settings → Theme → *Theme style* has *Filee Soft* (as before) and *Material You*,
  with Material 3 surfaces toned from your accent colour. The accent colours come as vibrant swatches and pastel
  presets.
- **Window size** (by @flexeykinDev). Settings → Theme → *Window size* makes everything in Filee's windows larger or
  smaller (70 to 180 %), with Reset. In any window, Ctrl + mouse wheel and Ctrl + plus / minus do the same, and
  Ctrl + 0 goes back to 100 % (⌘ on a Mac). The donut and the toasts keep their size.
- **Preset tabs** (issue #343, by @SeoJCalive). *Available presets* in Settings → Donut toolbar is split into tabs:
  All, Image, Document, Video & audio, Edit, 3D & CAD, Geospatial, Archive and Font; tabs with nothing for the selected
  donut are hidden. A search looks through every tab and also shows, dimmed and with the reason, presets the donut's
  files can't use.
- **Missing fonts** (issue #413, by @Perricheno). When a DOCX, XLSX, PPTX or HWPX names fonts this computer doesn't
  have, the conversion still runs and its card lists them, with **Get fonts** opening each font's own page
  (Microsoft's, the project's, or Google Fonts). Nothing is downloaded or installed for you. Fonts embedded in the
  document, or with a look-alike installed (Calibri → Carlito), don't count. The command line reports them too.
- **Choose files on Home** (by @gtxPrime). Double-click the drop zone to pick files; the donut opens at the pointer.
- **Updates on Linux** (by @manish0467). A Filee installed with `install.sh` downloads and installs new versions
  itself, as on Windows: it checks the download against the release's checksums, closes, installs and starts again,
  and your settings, presets, history and engines stay. If the install fails, the previous version is put back and
  Filee says so. To get 2.1.0 over 2.0.0, unpack the package and run `./install.sh --upgrade`, which keeps the engines
  you downloaded (without `--upgrade`, an existing install is still never overwritten).
- **Wayland** (experimental).
  - **GNOME** (by @yanscholze). On GNOME Shell 45 to 50, `install.sh` adds the Shell extension "Filee donut at the
    cursor". Turn it on in Extensions (sign out and in again if it doesn't show), then hold Ctrl and drag a file in
    Files. No `input` group or root is needed. Selection shortcuts, Hold, and flipping the donut during a drag don't
    work there. `install.sh` and updates keep the extension current, and Settings → Shortcuts says when Filee is
    waiting for it or when the running extension belongs to another version (sign out and in again).
  - **Hyprland** (by @mahaveergurjar). The drag gesture 2.0.0 brought to KDE (`FILEE_WAYLAND_GESTURES=1` and the
    `input` group) works on Hyprland too, with a few window rules. `FILEE_USE_WAYLAND=1` runs Filee as a native Wayland
    app instead of through XWayland. installer/linux/INSTALL.txt has the steps.
- **macOS disk image** (issue #379). Next to the tar.gz, each macOS package comes as a `.dmg` too: open it and drag
  Filee to Applications. The `filee` command still comes with the tar.gz's `install.sh`, and the first start still
  goes through Privacy & Security → Open Anyway.
- **Languages.** Filee speaks French (by @devohmycode), Traditional Chinese, also for Hong Kong and Macau (by
  @LouisChen-TW), Nepali (by @smjrifle), Thai, with its own font (by @Sudo-Bannawat), Turkish, in Setup too (by
  @xmetropolicex), and European Portuguese, next to Brazilian Portuguese (by @Blackspirits). It picks them when
  Windows, macOS or Linux does: a PC set to Portugal gets European Portuguese, one set to Brazil Brazilian, and a
  language picked in Settings stays as it is.

### Improved

- **Large spreadsheets to PDF.** XLSX, XLS, ODS and CSV going to PDF or pictures through Filee's built-in route are
  drawn far faster: a 65-page workbook takes 25 seconds instead of 24 minutes, and the progress bar no longer stalls
  halfway.
- **Rows to repeat at top** (issue #407, by @cre8ive-marketing). XLSX → PDF repeats the rows a sheet sets in Page
  Layout → Print Titles on every page, not only frozen rows.
- **Every audio track** (issue #396, by @cre8ive-marketing). Video → video keeps all audio tracks (a film with several
  languages) with their language tags, not only the first. FLV and DV still hold one.
- **Cyrillic, Greek and Central European text to PDF** (by @anatolykoptev). Russian, Greek, Polish, Czech, Turkish,
  Vietnamese and other text with letters beyond Western European ones came out of DOCX, Markdown, TXT, HTML, XLSX,
  PPTX and HWPX → PDF with letters on top of each other. Such documents now go through LibreOffice when it's
  installed; without it they convert as before.
- **Arabic in `.xls`** (issue #345, by @cre8ive-marketing). Old Excel files with Arabic now take the route XLSX took
  in 2.0.0 (LibreOffice, or a message without it) instead of becoming PDFs with broken letters.
- **Word → Markdown** (issue #391). Tables with merged cells become plain Markdown tables instead of HTML, pictures are
  linked relative to the result, and EMF / WMF pictures become PNG on Windows.
- **Crop** (issue #369, by @cre8ive-marketing). The frame's centre snaps onto the picture's centre lines, with a dashed
  guide, while you drag it. The arrow keys still move it by a pixel.
- **The donut's flip** (issue #371, by @SeoJCalive). Each slice now turns over on its own, in a short wave that starts
  where you point and spreads both ways, and the whole flip still takes 0.2 s. The face switches the moment the flip
  starts, so a file dropped during the flip goes to the new face's slice. With "Reduce animations" the faces still
  swap at once.
- **Recent conversions** (by @gtxPrime) show thumbnails of videos too, with FFmpeg.
- **Installing an update** (Windows, by @gtxPrime) from the sidebar, About or the tray shows a window with the
  download's progress.

### Fixed

- A TXT, Markdown or CSV file saved as "ANSI" was always read as Korean, so Chinese, Japanese or Western text turned
  into Hangul (issue #491, by @cre8ive-marketing). It's now read in the computer's own code page, and as Korean when it
  looks Korean or on a Korean PC. A Chinese file on an English PC still can't be told apart.
- Check boxes, ticks, arrows and other symbols inserted from Wingdings, Webdings or Symbol came out of Word and
  PowerPoint → PDF or HWPX as black dots (issue #350). They're the right characters now.
- On Linux without Korean fonts, DOCX, XLSX and other documents converted to PDF through rhwp came out without their
  text (by @Perricheno).
- Spreadsheets converted to PDF through LibreOffice could get completely empty pages. They're removed, with a note;
  PDF/A results are left as LibreOffice wrote them (by @Perricheno).
- A file with a file name token in its own name had it filled in: `Minutes {date}.png` became `Minutes 20261022.jpg`
  (by @Bortlesboat). Only the tokens in your file name pattern are filled in now.
- *One ZIP* packed only the `.shp` of a Shapefile, and selecting a Shapefile with its `.dbf`, `.shx`, `.prj` … opened
  the donut for mixed files and failed once per companion. A Shapefile now goes into the ZIP with its companion files,
  and a selected set counts as one file (by @KAPPI1925).
- Double-clicking the tray icon could open two main windows (Windows).
- After quitting Filee from the tray, the mouse stuttered for about five seconds (issue #358).
- Shortcuts (issue #450): the recorder couldn't take Space or Enter, so even Ctrl+Alt+Space couldn't be recorded again.
  Hold and the keyboard shortcut didn't work on files selected on the desktop. Hold never fired when it used the same
  keys and button as Drag. A keyboard shortcut is now judged by the window in front, not by where the pointer is. When
  *Start at sign-in* doesn't stick (security software, for example), the switch says so.
- On macOS, the Accessibility alert showed at every start after an update or reinstall (issues #316, #426). It shows
  once, and the reminder says to remove Filee from the list with − and add it again with +. `install.sh` clears the
  download flag on Filee.app and replaces a `filee` link left behind by an app moved to the Trash.
- On KDE Plasma with Wayland, the donut could open behind the file manager (by @Perricheno). After updating Filee,
  upgrade the KWin script: the `kpackagetool6` line in installer/linux/INSTALL.txt with `--upgrade` instead of
  `--install`, then the reconfigure line (`qdbus6`, `qdbus-qt6` or `qdbus`, depending on the distribution).
- On macOS, one press of the flip key while dragging could flip the donut twice, back to the face it started on (issue
  #504, by @cre8ive-marketing). It flips once.
- A glTF model whose buffer or texture names are percent-encoded (`mesh%20data.bin`) failed to find those files (by
  @Stamina9).
- The sample switch in Settings → Theme's preview didn't react to clicks. It now flips on and off to show the
  theme's switch, with the mouse or Space, and still changes no setting (issue #363, by @Saad-elnagar).
- The engine setup window ran off small or scaled screens with its buttons out of reach (issue #370). The list
  scrolls now, the buttons stay in the window, and Esc closes it.
- A donut slice's small caption was cut off; it takes a second line where the slice has room (issue #361, by
  @devohmycode).
- In Settings → Donut toolbar, a donut's count showed only its front face, and the preset search had no clear button
  and stayed when you chose another donut (issues #425, #427, by @cre8ive-marketing). The count shows both faces
  (`10 · 9`), every search box has an ×, and the preset search empties when you switch donuts.
- **Security** (by @Bddgc): before the built-in updater runs a downloaded installer, it checks that Windows trusts its
  signature and that the publisher who signed the running Filee signed it too. From 2.1.0 on, an installer signed by
  anyone else, or not at all, is deleted and you're sent to the download page instead.

## 2.0.0

<!-- update
level: recommended
ko: Filee 전용 Windows 설치 프로그램으로 바뀌었어요. 설정, 프리셋, 변환 엔진은 그대로 남아요
ko: 브라우저의 그림이나 파일 링크를 원형 툴바로 바로 끌어 놓을 수 있어요
ko: 원형 툴바에 뒷면이 생겼어요. Tab 키나 마우스 휠로 뒤집어요
ko: 자르기가 새로워졌어요. 그림 위에서 A를 눌러 그 자리에서 비율을 골라요
ko: 라이브 포토·모션 포토에서 동영상을 꺼내고, 동영상으로 만들 수도 있어요(실험 기능)
ko: OFD, MHTML, POTM, FBX, JPEG XR, Shapefile·GeoJSON·KML·GPX 지도를 지원해요
ko: 강조색을 자유롭게 고르고 원형 툴바를 반투명하게 만들 수 있어요
ko: 스페인어, 힌디어, 일본어, 러시아어, 아랍어, 브라질 포르투갈어를 지원해요
ko: PDF의 표가 Word·한글 표로, Word 수식이 한글 수식으로 바뀌어요
ko: 전자책·SVG·3D 모델·이메일의 보안 문제와 여러 오류를 고쳤어요
en: Filee's own Windows installer; your settings, presets and engines stay as they are
en: Drag pictures and file links from a web browser straight onto the donut
en: The donut has a back face: press Tab or turn the mouse wheel to flip it
en: A new crop tool: press A over the picture to pick a ratio right there
en: Take the video out of Live and Motion Photos, or make one from a video (experimental)
en: OFD, MHTML, POTM, FBX, JPEG XR, and Shapefile, GeoJSON, KML and GPX maps
en: Any accent colour, and a see-through donut
en: Spanish, Hindi, Japanese, Russian, Arabic and Brazilian Portuguese
en: Tables in PDFs become real Word tables, and Arabic or Hindi documents become proper PDFs
en: Security fixes for e-books, SVG, 3D models and e-mails, and many bug fixes
zh: 改用 Filee 自己的 Windows 安装程序，设置、预设和转换引擎保持不变
zh: 可以把浏览器中的图片或文件链接直接拖到环形工具栏上
zh: 环形工具栏有了背面，按 Tab 键或滚动鼠标滚轮即可翻转
zh: 全新的裁剪工具，在图片上按 A 即可就地选择比例
zh: 可以从实况照片和动态照片中提取视频，也可以用视频制作（实验性）
zh: 支持 OFD、MHTML、POTM、FBX、JPEG XR 以及 Shapefile、GeoJSON、KML、GPX 地图
zh: 可以任选强调色，并让环形工具栏半透明
zh: 新增西班牙语、印地语、日语、俄语、阿拉伯语和巴西葡萄牙语界面
zh: PDF 中的表格可转为真正的 Word 表格，阿拉伯语和印地语文档也能正确转为 PDF
zh: 修复了电子书、SVG、3D 模型和电子邮件的安全问题以及许多错误
pl: Własny instalator Filee dla Windows; ustawienia, ustawienia wstępne i silniki zostają bez zmian
pl: Obrazy i linki do plików można przeciągać z przeglądarki prosto na pasek-pierścień
pl: Pasek-pierścień ma drugą stronę: obróć go klawiszem Tab lub kółkiem myszy
pl: Nowe kadrowanie: naciśnij A nad obrazem, aby wybrać proporcje na miejscu
pl: Wideo z Live Photo i Motion Photo oraz tworzenie ich z filmu (eksperymentalne)
pl: Nowe formaty: OFD, MHTML, POTM, FBX, JPEG XR oraz mapy Shapefile, GeoJSON, KML i GPX
pl: Dowolny kolor akcentu i półprzezroczysty pasek-pierścień
pl: Język hiszpański, hindi, japoński, rosyjski, arabski i portugalski brazylijski
pl: Tabele z PDF stają się prawdziwymi tabelami Worda, a dokumenty po arabsku i w hindi poprawnymi plikami PDF
pl: Poprawki bezpieczeństwa dla e-booków, SVG, modeli 3D i e-maili oraz wiele innych poprawek
es: Instalador propio de Filee para Windows; tu configuración, ajustes preestablecidos y motores se mantienen
es: Arrastra imágenes y enlaces a archivos desde el navegador directamente al menú circular
es: El menú circular tiene reverso: dale la vuelta con Tab o con la rueda del ratón
es: Nueva herramienta de recorte: pulsa A sobre la imagen para elegir la proporción ahí mismo
es: Extrae el vídeo de las Live Photos y Motion Photos, o créalas a partir de un vídeo (experimental)
es: Nuevos formatos: OFD, MHTML, POTM, FBX, JPEG XR y mapas Shapefile, GeoJSON, KML y GPX
es: Cualquier color de acento y un menú circular translúcido
es: Español, hindi, japonés, ruso, árabe y portugués de Brasil
es: Las tablas de los PDF pasan a ser tablas de Word, y los documentos en árabe o hindi salen bien en PDF
es: Correcciones de seguridad para libros electrónicos, SVG, modelos 3D y correos, y muchos errores corregidos
hi: Windows के लिए Filee का अपना इंस्टॉलर; आपकी सेटिंग, प्रीसेट और इंजन वैसे ही रहते हैं
hi: ब्राउज़र से चित्र या फ़ाइल के लिंक सीधे डोनट पर खींचें
hi: डोनट का अब पिछला हिस्सा भी है: Tab दबाएँ या माउस व्हील घुमाकर पलटें
hi: नया क्रॉप टूल: चित्र पर A दबाकर वहीं अनुपात चुनें
hi: लाइव / मोशन फ़ोटो से वीडियो निकालें, या वीडियो से बनाएँ (प्रायोगिक)
hi: नए फ़ॉर्मैट: OFD, MHTML, POTM, FBX, JPEG XR और Shapefile, GeoJSON, KML, GPX नक्शे
hi: कोई भी एक्सेंट रंग चुनें और डोनट को अर्ध-पारदर्शी बनाएँ
hi: स्पैनिश, हिन्दी, जापानी, रूसी, अरबी और ब्राज़ीलियाई पुर्तगाली भाषाएँ
hi: PDF की तालिकाएँ Word की असली तालिकाएँ बनती हैं, और हिन्दी या अरबी दस्तावेज़ सही PDF बनते हैं
hi: ई-बुक, SVG, 3D मॉडल और ईमेल की सुरक्षा समस्याएँ और कई गड़बड़ियाँ ठीक की गईं
ja: Filee 独自の Windows インストーラーになりました。設定・プリセット・変換エンジンはそのままです
ja: ブラウザーから画像やファイルへのリンクをドーナツメニューに直接ドラッグできます
ja: ドーナツメニューに裏面ができました。Tab キーかマウスホイールで裏返せます
ja: トリミングが新しくなりました。画像の上で A を押すとその場で比率を選べます
ja: Live Photo・モーションフォトから動画を取り出せます。動画から作ることもできます（試験的）
ja: OFD、MHTML、POTM、FBX、JPEG XR、Shapefile・GeoJSON・KML・GPX の地図に対応しました
ja: 好きなアクセントカラーを選び、ドーナツメニューを半透明にできます
ja: スペイン語、ヒンディー語、日本語、ロシア語、アラビア語、ブラジルポルトガル語に対応しました
ja: PDF の表が Word の表になり、アラビア語やヒンディー語の文書も正しく PDF にできます
ja: 電子書籍・SVG・3D モデル・メールのセキュリティ問題と多くの不具合を修正しました
ru: Собственный установщик Filee для Windows; настройки, пресеты и движки сохраняются
ru: Картинки и ссылки на файлы можно перетаскивать из браузера прямо на круговое меню
ru: У кругового меню есть обратная сторона: переверните его клавишей Tab или колесом мыши
ru: Новая обрезка: нажмите A над картинкой и выберите пропорции прямо там
ru: Видео из Live Photo и Motion Photo, а также их создание из видео (экспериментально)
ru: Новые форматы: OFD, MHTML, POTM, FBX, JPEG XR и карты Shapefile, GeoJSON, KML и GPX
ru: Любой акцентный цвет и полупрозрачное круговое меню
ru: Испанский, хинди, японский, русский, арабский и бразильский португальский
ru: Таблицы из PDF становятся настоящими таблицами Word, а документы на арабском и хинди правильно конвертируются в PDF
ru: Исправления безопасности для электронных книг, SVG, 3D-моделей и писем, а также много других исправлений
ar: مثبّت Filee الخاص لنظام Windows، مع بقاء إعداداتك والإعدادات المسبقة والمحركات كما هي
ar: اسحب الصور وروابط الملفات من المتصفح مباشرةً إلى القائمة الدائرية
ar: للقائمة الدائرية وجه آخر: اقلبها بمفتاح Tab أو بعجلة الفأرة
ar: أداة اقتصاص جديدة: اضغط A فوق الصورة لاختيار نسبة الأبعاد في مكانها
ar: استخراج الفيديو من Live Photo وMotion Photo، أو إنشاؤها من فيديو (تجريبي)
ar: صيغ جديدة: OFD وMHTML وPOTM وFBX وJPEG XR وخرائط Shapefile وGeoJSON وKML وGPX
ar: اختر أي لون مميّز واجعل القائمة الدائرية شبه شفافة
ar: الإسبانية والهندية واليابانية والروسية والعربية والبرتغالية البرازيلية
ar: تتحول جداول PDF إلى جداول Word حقيقية، وتُحوَّل المستندات العربية والهندية إلى PDF بشكل صحيح
ar: إصلاحات أمنية للكتب الإلكترونية وSVG والنماذج ثلاثية الأبعاد ورسائل البريد، وإصلاح أخطاء كثيرة
pt-BR: Instalador próprio do Filee para Windows; suas configurações, predefinições e motores continuam como estão
pt-BR: Arraste imagens e links de arquivos do navegador direto para o menu circular
pt-BR: O menu circular tem um verso: vire-o com Tab ou com a roda do mouse
pt-BR: Nova ferramenta de recorte: pressione A sobre a imagem para escolher a proporção ali mesmo
pt-BR: Extraia o vídeo de Live Photos e Motion Photos, ou crie-as a partir de um vídeo (experimental)
pt-BR: Novos formatos: OFD, MHTML, POTM, FBX, JPEG XR e mapas Shapefile, GeoJSON, KML e GPX
pt-BR: Qualquer cor de destaque e um menu circular translúcido
pt-BR: Espanhol, hindi, japonês, russo, árabe e português do Brasil
pt-BR: Tabelas de PDF viram tabelas de verdade no Word, e documentos em árabe ou hindi viram PDFs corretos
pt-BR: Correções de segurança para e-books, SVG, modelos 3D e e-mails, e muitos outros erros corrigidos
-->

### New

- **Filee's own installer** (Windows). `Filee-<version>-win-Setup.exe` is now Filee's own installer instead of an Inno
  Setup one; the download names stay the same. It is one card in Filee's look, in every app language (Windows' display
  language first): **Install**, and **Options** for the language, folder, desktop icon, Explorer menu, starting at
  sign-in, `filee` on the PATH and the engines to download. While it installs, it shows this version's What's new
  slides. It installs over Filee 1.x, also from the built-in updater: same folder, your choices and one entry in Apps &
  Features, with settings, presets and engines kept. The uninstaller asks whether to delete your settings (kept unless
  you say so). Setup checks its files before changing anything and puts everything back if an install fails halfway.
  The usual switches still work (`/SILENT`, `/VERYSILENT`, `/SUPPRESSMSGBOXES`, `/NORESTART`, `/DIR=`, `/LANG=`,
  `/TASKS=`, `/MERGETASKS=`, `/NOICONS`, `/CURRENTUSER`, `/LOG`), and `/NOLAUNCH` doesn't start Filee at the end.
  Tools that unpacked the old Setup with innounp need the portable zip now.
- **What's new.** The first time Filee starts after an update, a small window shows the new features as short animated
  slides (Previous / Next, ← / →, Esc). About → "What's new in Filee 2.0" opens them again. With "Reduce animations"
  each slide is a still picture.
- **Files from a web browser.** Hold the shortcut key and drag a picture, a link to a file or a download out of
  Chrome, Edge, Firefox or Safari: the donut opens as it does for your own files, and dropping on a slice saves the
  file and converts it. The Home drop zone takes the same drags. Settings → General → "Files dragged from a browser"
  sets where they are saved (Downloads unless you choose another folder) and can keep only the converted file. A badge
  over the progress ring shows the download. Filee downloads a link itself, without your browser's sign-in, so a file
  behind a sign-in has to be saved from the browser first. Web pages, programs and scripts are refused, and nothing is
  run. Attachments dragged out of Outlook and files dragged out of a ZIP in Explorer open the donut too.
- **The donut's back face** (issue #195). Every donut has a second set of up to 12 slices. While the donut is open,
  press **Tab** or turn the **mouse wheel** to flip it; the hub shows the hint when the back has something for your
  files. Built-in donuts come with a back face: Crop, Rotate, Flip, Remove background and Live Photo video on the image
  donut, Compress PDF on the PDF donut, frame extraction and Live / Motion Photos on the video donut, One ZIP on most.
  The Front / Back switch in Settings → Donut toolbar edits it, and Settings → Shortcuts → "Flip the donut" changes the
  key or turns the wheel off.
- **Crop** (by @smjrifle). The Crop slice (on the back of the image donut) opens your pictures one after another in a
  crop window. Drag the frame or its corners, pick a ratio on the aspect dial (Free, Original, 1:1, 4:3, 3:2, 16:9,
  and their portrait forms; **A** over the picture opens it at the pointer), then **Crop & Next** (Enter) or Skip.
  The arrow keys move the frame by a pixel (Shift: 10), R resets and X swaps portrait and landscape. A strip shows the
  files, and a summary lists what was skipped or failed. The quality of the picture is kept; animated pictures aren't
  cropped.
- **Live Photos and Motion Photos** (issue #174, by @SunGang-pine). "Extract Live Photo video" takes the video out of
  an Android Motion Photo, or out of the MOV next to an iPhone Live Photo (LIVP packages too), as MP4 or MOV.
  **Experimental:** "Live Photo (iPhone)" and "Motion Photo (Android)" turn a short clip of a video (0.5 to 5 s) into
  an iPhone Live Photo (a JPG or HEIC plus a MOV with the same name; import both into Photos together) or an Android
  Motion Photo (one `NAME.MP.jpg`). They aren't confirmed on every phone yet. Both need FFmpeg.
- **More formats**, 255 in all.
  - **OFD** (by @MMNHHK): Chinese e-invoices and other OFD documents become DOCX, PDF, HWPX, TXT and the other document
    formats, with their seals. Built in.
  - **MHTML / MHT** web archives ("Webpage, Single File", issue #210) become HTML, PDF, DOCX, HWPX, TXT, Markdown or
    EPUB. Scripts and anything outside the archive are left out.
  - **POTM** (issue #213, by @ganeshmshetty): macro-enabled PowerPoint templates, to and from the other PowerPoint
    formats and to documents and PDF.
  - **Geospatial data** (by @KAPPI1925): Shapefile, GeoJSON, KML, KMZ, GPX and CSV with coordinates convert into
    each other on a donut of their own, built in. Results are in WGS 84 longitude / latitude; projected data is
    converted using its `.prj`. *Save the result as a ZIP* (a preset option, also for every geospatial result in
    Settings → General, and `--zip` on the command line) keeps a Shapefile's files together in one ZIP.
  - **FBX** 3D models (issue #148), read only: to GLB, glTF, STL, OBJ, 3MF, PLY and pictures.
  - **JPEG XR** (`.jxr`, `.wdp`, `.hdp`; issue #150), on Windows: HDR screenshots are tone-mapped instead of clipped.
  - **Scripts and logs as text** (issues #254, #273, #284, by @Mustafa1p): `.log`, `.py`, `.pac`, `.bat` and
    `.command` become UTF-8 TXT. They are read, never run.
  - **Video CD** `.dat` (issue #292) becomes MP4 or MKV with FFmpeg; other `.dat` files are refused.
  - **`.wbpz`** Workbench archives open as ZIP (issue #294).

  Your donuts learn the new extensions, unless one of your own donuts already takes them.
- **Playback speed** (issue #158). GIF, WebP, APNG and MNG presets can make animations play from 0.25× to 4× as fast,
  also for video → GIF or APNG.
- **Theme** (accent picker by @Mustafa1p, donut opacity by @gtxPrime, `theme.json` by @AkiVonAkira). Settings →
  Theme has a colour picker with a hex field next to the accent swatches, and *Donut toolbar opacity* (10 to 100 %,
  issue #276). Softness rounds the donut's slices too (issue #206). A `theme.json` next to `settings.json` can replace
  Filee's page, surface, text and status colours (docs/CUSTOM-THEMES.md).
- **Reset one donut** (issue #205). *Reset to default* next to *Delete profile* in Settings → Donut toolbar puts a
  built-in donut back as it came, front and back. Your other donuts and your presets stay as they are.
- **Recent conversions** (by @gtxPrime). Home has a search box over the list, thumbnails of pictures, and *Open file*
  and *Open with* next to the folder button. Settings → General → "Conversions to keep in the history" (50 to 2,000,
  200 unless you change it).
- **Folders into one ZIP** (issue #186, by @SonicaReddy and @gtxPrime). Dragged folders go into One ZIP with
  everything in them, and One ZIP is on the archives donut too. Other presets still don't take folders.
- **Watch folders that write into themselves** (issue #234, by @Mustafa1p). A watch folder's output may be the folder
  it watches; its own results aren't converted again.
- **Merge order** (issue #268, by @Mustafa1p). Merge PDF first shows the files in name order (2 before 10), with each
  file's folder. Move them up or down (Alt+↑ / Alt+↓) or cancel before anything starts.
- **AV1 on the graphics chip** (issue #196). *Use hardware acceleration (graphics chip)* encodes AV1 too, on NVIDIA
  RTX 40, Intel Arc and Core Ultra, AMD RDNA 3 and newer, and through VA-API on Linux. Other chips leave AV1 to the
  usual encoder, as before.
- **A subfolder only when needed** (issue #259, by @flexeykinDev). With "A subfolder", *Only when a file becomes
  several* saves a single result next to the original and keeps the subfolder for files that become several (a PDF
  split into pages).
- **Dock and menu bar** (macOS, issue #132, by @SeoJCalive). Settings → General → *Show Filee in the Dock* and *Show
  Filee in the menu bar*. With both off, opening Filee again brings up its window.
- **macOS 13** (by @yoamzil). Filee runs on macOS 13 Ventura, not only 14 and newer.
- **Linux** packages are no longer a preview.
- **KDE Plasma on Wayland** (experimental, by @IPedrax). With `FILEE_WAYLAND_GESTURES=1`, the drag gesture works on KDE
  Plasma 6 on Wayland. installer/linux/INSTALL.txt has the steps (the `input` group and a KWin script) and the limits.
- **Languages.** Filee speaks Spanish (by @danielcastro-dev), Hindi (by @adnan-bhaldar), Japanese (by @sh-ayu),
  Russian (by @allamitsamara-cmd), Arabic, right to left in every window and in Setup (by @Mustafa1p), and Brazilian
  Portuguese, also for Portuguese from Portugal (by @RaulSKKS). It picks them when Windows, macOS or Linux does. Hindi,
  Japanese and Arabic bring their own fonts, so they show on systems without one.

### Improved

- **Arabic, Hebrew and Indic text to PDF** (issues #185, #202). Documents with Arabic, Hebrew, Gujarati, Hindi, Thai
  or another script whose letters join or combine go to PDF through LibreOffice, whatever Settings → Engines picks.
  Without LibreOffice they stop with a message saying so instead of a PDF with broken letters. PDF → DOCX reads such
  text in its proper order, and right-to-left paragraphs stay right to left.
- **Ruled tables in PDF → DOCX / HWPX** (issue #138). Tables drawn with lines become real tables with merged cells,
  borders, shading and column widths. Tables without lines and nested tables still come out as text, and a table
  running over several pages becomes one per page.
- **Word equations** (issue #143). DOCX → HWPX, HWP and PDF keep fractions, sub- and superscripts, roots, sums,
  integrals, brackets and matrices as 한글 equations. HTML, EPUB, Markdown and TXT get them on one line, as
  `(a+b)/(2c)`.
- **PDF → Markdown** (issue #144). Pictures are saved in a `<name>_files` folder next to the Markdown file instead of
  being broken links (HWP and HWPX → Markdown too), and fewer running heads and page numbers come along. The new
  option *Leave out pictures (text only)* keeps just the text.
- **Chinese Word documents to PDF** (issue #237). WordArt titles, indents set in characters and full-width
  punctuation come out as in Word; quotation marks no longer sit on top of the next character.
- **DOCX → HWPX** (by @amebaleon). Cell padding and small bullets (•, ▪, –) are kept.
- **Remove background** (issue #139). On a coloured backdrop, a drawing keeps its own colours instead of turning half
  transparent.
- **Donut toolbar page** (issue #266, by @SeoJCalive). The list of presets shows only those the selected donut's files
  can use; those that need an engine are dimmed, with the reason when you point at them.
- **Engines without the Visual C++ Redistributable** (Windows, issue #248). rhwp, unhwp and LibreOffice now come with
  the runtime files they need, so HWP conversions no longer fail with "exit -1073741515" on a PC without them. When
  such a file is missing anyway, the message says what to install.
- **Automatic language** (by @flexeykinDev). Filee follows the display language you chose in Windows, not the
  language Windows was installed in.

### Fixed

- A failed or cancelled conversion left a half-written file where the result should be, and converting again gave
  `photo (2).png` (by @flexeykinDev). A file the preset was going to overwrite is never deleted.
- Work folders a crash or a shutdown left in the temp folder stayed there for good. They are deleted after a day (by
  @flexeykinDev).
- With several Explorer tabs open (Windows 11), the selection shortcut read the files of an older tab (issue #239).
- A Windows shortcut (`.lnk`) brought in the shortcut instead of the file it points to (issue #159).
- An empty file (0 bytes, such as a DOCX made with "New" in Explorer) failed with "Central Directory corrupt". The
  message now says the file is empty, and an empty PDF no longer breaks a merge (issue #157).
- Some DOCX files (from Tencent Docs, for example) failed with "source file could not be loaded" through LibreOffice.
  Filee tries again from a cleaned-up copy (issue #141).
- On macOS, Finder replacements such as QSpace Pro couldn't hand files to Filee, and a donut opened from another
  launch could close at once (issue #160).
- With FFmpeg installed, audio presets counted as available for pictures, which would have made silent files.
- The donut could sit on a frosted rectangle (issue #269). Filee now takes away a backdrop another program puts on its
  windows, and the log says how transparent windows are drawn.
- On Linux and macOS, converting a document to PDF that names a font that isn't installed could break other
  conversions running at the same time.
- **Security** (by @Stamina9): e-books (issue #167), SVG files (issue #191) and 3D models (issue #317) read pictures
  and styles only from their own package or folder, never from elsewhere on the disk or the web. E-mails converted to
  HTML (issue #165) lose scripts, forms, frames and remote pictures.

## 1.12.0

<!-- update
level: recommended
ko: PDF를 PowerPoint로, PDF를 더 작은 PDF로 바꿀 수 있어요
ko: 동영상의 모든 프레임을 그림으로 저장할 수 있어요
ko: 그래픽 칩으로 동영상을 더 빠르게 인코딩할 수 있어요
ko: 변환이 끝나면 메모리를 돌려줘서 트레이에서 훨씬 가벼워요
ko: 창을 닫을 때 트레이에 둘지 종료할지 고를 수 있어요
ko: 폴란드어를 지원해요
ko: 일부 SVG가 변환되지 않던 문제와 드롭다운 오류를 고쳤어요
en: Convert PDF to PowerPoint, or to a smaller PDF
en: Save every frame of a video as pictures
en: Encode video faster on the graphics chip
en: Filee gives memory back after conversions and stays light in the tray
en: Choose whether closing the window keeps Filee in the tray or quits
en: Polish translation
en: Fixed some SVGs that couldn't be converted and an error in dropdowns
zh: 可以将 PDF 转为 PowerPoint，或压缩为更小的 PDF
zh: 可以将视频的每一帧保存为图片
zh: 可以用显卡更快地编码视频
zh: 转换结束后会释放内存，在托盘中占用更少
zh: 关闭窗口时可以选择保留在托盘或退出
zh: 新增波兰语界面
zh: 修复了部分 SVG 无法转换的问题和下拉列表的错误
pl: Konwersja PDF do PowerPointa lub do mniejszego PDF
pl: Zapis każdej klatki wideo jako obrazów
pl: Szybsze kodowanie wideo na karcie graficznej
pl: Filee oddaje pamięć po konwersjach i mniej obciąża zasobnik
pl: Wybór, czy zamknięcie okna zostawia Filee w zasobniku, czy je zamyka
pl: Język polski
pl: Naprawiono niektóre pliki SVG, których nie dało się przekonwertować, i błąd w listach rozwijanych
-->

### New

- **PDF → PowerPoint** (issue #96). "PPTX (slides)" puts a picture of each PDF page on its own slide, at the page's
  size, with the page's text in the speaker notes. The slides look exactly like the PDF but aren't editable; PDF → DOCX
  is the editable route. It takes TIFF's place on the PDF donut when you never changed that donut.
- **Compress PDF** (issue #103). The new "Compress PDF" preset, and a *Compress* option on every PDF → PDF preset, make
  a PDF smaller while it stays a PDF: pictures shown larger than needed are scaled down (Smallest 72 dpi, Balanced
  150 dpi, High quality 300 dpi), photos are saved again as JPEG, and everything else is compressed losslessly. Text,
  fonts, links, bookmarks and forms stay. A PDF that can't be made smaller is left as it was, and the toast says so.
  With Ghostscript installed, *Also try Ghostscript* keeps the smaller of the two results.
- **Every frame of a video** (issue #63, by @pleguen). "Extract frames (PNG)" and "Extract frames (JPG)", or *Save
  every frame of a video* in a PNG or JPG preset, write each frame (or every Nth) into a folder next to the video. A
  video that would give more than 100,000 pictures or fill the disk is refused before it starts. Add the presets to a
  donut in Settings → Toolbar.
- **Hardware video encoding** (issue #100). *Use hardware acceleration (graphics chip)* in a video preset encodes
  H.264 and HEVC with NVENC, Quick Sync, AMF, VideoToolbox or VA-API: much faster and lighter on power, slightly larger
  at the same quality. Without a usable chip, the video is encoded as before. Off by default.
- **Closing the window** (issue #111). The first time you close the main window, Filee asks whether to keep running in
  the tray or quit, with "Don't ask again". Settings → General → "When I close the window" changes it later.
- **Request a format** (issue #117). When you bring files Filee can't read to the donut, or a conversion fails for
  that reason, *Request this format* opens a GitHub form with the extension filled in (only the extension, never a
  file name). Encrypted or copy-protected (DRM) files can't be converted, whatever their format.
- **Ubuntu 22.04** (issue #102). The Linux packages run on Ubuntu 22.04 and newer again, global gestures included.
- **Polish** (by @rafek1241). Filee speaks Polish, and picks it when Windows, macOS or Linux does.
- **Other file managers** (issue #124). Gestures set to "File managers and desktop" also start over Files, Directory
  Opus, Total Commander, XYplorer, OneCommander, Q-Dir and FreeCommander, not only Explorer.
- **calibre for e-book PDFs** (issue #127). With calibre installed and moved above the HWPX writer in Settings →
  Engines, EPUB, MOBI, AZW3 and other e-books become PDFs through calibre, much faster for long books.

### Improved

- **Memory** (issue #119). Half a minute after the last conversion Filee gives the memory back: Task Manager shows it
  at a few tens of MB in the tray instead of several hundred. Closing the main window releases it; it opens again where
  and on the page you left it.
- **Running as administrator** (issue #99). Explorer can't drop files on a Filee that runs as administrator or use its
  menu. Filee now notices it, says so on Home, and offers *Restart without administrator rights*. A second launch that
  can't reach the running Filee says why instead of quitting silently.

### Fixed

- SVGs with an unescaped "&" or HTML entities such as `&nbsp;` in their text (often from AI tools) failed with "An
  error occurred while parsing EntityName" (issue #120).
- Pressing Ctrl+Space twice in a dropdown, or Ctrl+clicking the selected item, showed an error (issue #107).
- Long e-books to PDF stopped at 95 % after two minutes (issue #127): the time limit now grows with the text, up to
  an hour.
- "Retry gestures" could leave gestures dead while Filee said they were running (by @leishifu666).
- calibre couldn't be downloaded on macOS and Linux: it now comes from calibre's own server.

## 1.11.0

<!-- update
level: recommended
ko: 지원 형식이 244가지로 늘었어요: 자막, JSON·YAML 같은 데이터, 3D 모델 등
ko: 그림을 SVG로 바꾸는 "벡터화"와 회전·반전 프리셋이 생겼어요
ko: 도넛 한 칸에 여러 프리셋을 묶어 두고 펼쳐서 고를 수 있어요
ko: HEVC·AV1 동영상, 무손실 오디오, 동영상 자막 추출을 지원해요
ko: 바탕화면 아이콘이 가끔 클릭되지 않던 문제를 고쳤어요
ko: HWP를 PDF로 바꿀 때 표 안의 표가 아래로 밀리던 문제를 고쳤어요
en: 244 formats now, including subtitles, data files (JSON, YAML…) and 3D models
en: "Trace to SVG" turns pictures into vectors, and new presets rotate and flip images
en: A donut slice can hold a group of presets that fans out
en: HEVC and AV1 video, lossless audio formats and subtitle extraction
en: Fixed desktop icons that sometimes stopped reacting to clicks
en: Fixed tables inside a table cell drawn too low in HWP → PDF
zh: 支持的格式增至 244 种，新增字幕、数据文件（JSON、YAML 等）和 3D 模型
zh: 新增"描摹为 SVG"可将图片转为矢量图，并新增旋转和翻转预设
zh: 圆环工具栏的一格可以放一组预设，展开后选择
zh: 支持 HEVC 和 AV1 视频、无损音频格式以及从视频提取字幕
zh: 修复了桌面图标有时点击无反应的问题
zh: 修复了 HWP 转 PDF 时表格内的表格位置偏下的问题
-->

### New

- **Subtitles.** A new category with its own donut: SRT, ASS, WebVTT, SAMI (SMI), LRC, TTML and MicroDVD convert into
  each other without any engine, and SSA and SCC are read. Korean SMI and SRT files saved in CP949 are detected and
  written as UTF-8. "Extract subtitles" on the video donut takes the first text subtitle track of an MKV, MP4, MOV or
  WebM out as SRT (needs FFmpeg).
- **Data files.** A new category for JSON, JSON Lines, YAML, TOML and XML, converting into each other and, when the data
  is a table, to and from CSV and Excel. Outlook `.msg` e-mails convert to EML, HTML, text or a ZIP of their
  attachments, and vCard contacts (also CP949 ones from old phones) to and from CSV.
- **3D models.** A new category for GLB, glTF, STL, OBJ, 3MF and PLY, converting into each other with colours,
  textures and materials where the format keeps them, and to a shaded PNG or JPG picture. Nothing to install.
- **Trace to SVG.** Two new image presets turn a picture into vector shapes: "Trace to SVG" in up to 64 colours and
  "Line art to SVG" in black for logos, signatures and scans. SVG drawings also convert to DXF and DWG for CAD.
- **Rotate and flip** (issue #71, by @SeoJCalive). Images can be turned left, right or 180° and mirrored, after the
  photo's own orientation, as a preset option and as ready-made presets.
- **Donut groups** (issue #74, by @SeoJCalive). A donut slice can hold up to six presets that fan out in an outer ring
  when you rest on it, drag outward or click it; the keyboard works too. Create one with "New group" in Settings →
  Toolbar. The ready-made "Rotate" and "Flip" groups are on the Presets page.
- **Video and audio.** Video presets can encode HEVC (H.265) or AV1 (MP4 HEVC is on the video donut), keeping 10-bit
  and HDR video. New audio targets WavPack, TTA, W64, E-AC3 and ALAC (in M4A), and APE, Musepack, TAK, DSD (DSF/DFF),
  DTS and Speex are read.
- **More formats.**
  - Images: APNG (animated PNG, without FFmpeg), QOI, DDS, PCX, CUR, XPM, XBM, WBMP, PAM, PFM, SGI, Sun Raster, MIFF
    and MNG; DICOM medical images, OpenEXR, Radiance HDR and Photo CD are read; Fujifilm `.hif` is read as HEIC.
  - Documents and text (with Pandoc): Org, AsciiDoc, Typst, MediaWiki, DocBook, Jupyter notebooks, Textile, man pages
    and OPML, and Markdown → PowerPoint slides.
  - With LibreOffice: ClarisWorks, MacWrite, T602, Pocket Word and flat OpenDocument files are read, OTT, OTS, OTP,
    FODT and XLT written, SVG → EMF/WMF and one PDF page → SVG.
  - E-books: KEPUB for Kobo readers, built in.
- **Sponsor.** "Sponsor" in the sidebar and the About page opens the ways to support Filee (GitHub Sponsors, Patreon,
  Ko-fi, PayPal, Fairy), and sponsors are thanked on the About page.

### Changed

- **Engines page.** LibreOffice's switches moved into a Settings window on its card, and engines are put in order by
  dragging their cards.
- **Shorter texts.** Long hints in the app are shorter and break after each sentence.

### Fixed

- **Desktop icons that stopped reacting to clicks.** After a conversion through the donut, a Ctrl+drag that brought no
  files (selecting icons on the desktop, dragging a folder) could leave Filee's invisible drag window over the desktop,
  catching clicks until Filee quit. A donut opened from Explorer's menu on Windows 10 also closes with Esc and clicks
  elsewhere again.
- **Tables inside a table cell in HWP → PDF** (issue #49). A table placed in a table cell was drawn too low, over the
  paragraph below it, when the file was HWP (HWPX was fine).
- **Korean e-mails after an Outlook message.** Converting an Outlook `.msg` first left later Korean (EUC-KR) e-mails of
  the same session garbled.

## 1.10.4

<!-- update
level: recommended
ko: 서명·도장·로고의 단색 배경을 투명하게 만드는 "배경 제거" 프리셋이 생겼어요
ko: 자동 변환 폴더에서 하위 폴더의 파일은 결과도 같은 하위 폴더에 저장돼요
ko: Filee가 켜지는 중에 연 파일이나 업데이트 요청이 사라지던 문제를 고쳤어요
ko: 보안 업데이트: PDF 처리에 쓰는 .NET 암호화 라이브러리를 최신 버전으로 바꿨어요
en: A "Remove background" preset makes the plain backdrop of signatures, stamps and logos transparent
en: Watch folders keep the subfolders of their files in the output folder
en: Files opened, or an update started, while Filee is still starting are no longer lost
en: Security update: the .NET cryptography library used for PDFs is updated
zh: 新增"去除背景"预设，可将签名、印章、标志的纯色背景变为透明
zh: 自动转换文件夹会在输出中保留文件所在的子文件夹
zh: 修复了 Filee 启动过程中打开的文件或更新请求丢失的问题
zh: 安全更新：处理 PDF 所用的 .NET 加密库已更新
-->

### New

- **Remove background** (issue #69, by @SeoJCalive). A new image option and built-in preset make a plain backdrop
  transparent: the paper behind a signature or stamp, the backdrop of a logo. Offline, with smooth edges and the ink
  colour kept, also on phone photos with uneven lighting. "Keep enclosed areas" keeps the inside of a logo. Formats
  without transparency (JPG) get the background colour instead. The preset is on the Presets page; add it to a donut
  in Settings → Toolbar.

### Changed

- **Watch folders keep subfolders** (issue #72, by @SeoJCalive). With "Include subfolders", files from
  `Inbox\2025\scans` now go to `converted\2025\scans` instead of all into `converted` (where files of the same name
  became `a (2).pdf`), the way originals are already moved. `filee watch` does the same.

### Fixed

- **Starting Filee again while it is still starting.** Files opened from the right-click menu or Send To, or the
  installer's request to close Filee for an update, were lost when Filee took more than a few seconds to start (a
  slow or busy computer). They now wait until Filee is ready.
- **Security update.** The .NET cryptography library PDFsharp uses (System.Security.Cryptography.Pkcs) is updated to
  10.0.13, which fixes two denial-of-service advisories (CVE-2026-84008, CVE-2026-70162).

## 1.10.3

<!-- update
level: recommended
ko: 엑셀에서 정한 페이지 나누기가 PDF에 반영돼요
ko: 중국어·일본어 등 여러 언어 문서를 PDF로 바꿀 때 그 언어의 글꼴을 써요
ko: 한글 문서의 보기 상자와 수식이 PDF에서 제대로 나와요
ko: Word·엑셀 → PDF를 LibreOffice로 변환하도록 고를 수 있어요
ko: 업데이트 알림에 중요도와 바뀐 내용이 나와요
en: Excel page breaks carry over to PDF
en: Chinese, Japanese and other languages get a font of their own in PDFs
en: Boxes and equations of 한글 documents come out right in PDFs
en: Word and Excel → PDF can go through LibreOffice
en: Update notices say how much an update matters and what it changes
zh: Excel 中设置的分页符会保留到 PDF 中
zh: 中文、日文等文档转为 PDF 时使用对应语言的字体
zh: 韩文 Hangul 文档中的框和公式在 PDF 中正确显示
zh: 可以选择用 LibreOffice 将 Word、Excel 转为 PDF
zh: 更新通知会显示重要程度和更新内容
-->

### New

- **Update notices say how much an update matters.** The card names the update required, recommended or optional and
  lists what it changes in one line each, in the app's language (for someone a few versions behind, every version in
  between). Optional updates no longer bring up the card, only the sidebar button; required ones show it every time
  Filee starts.
- **LibreOffice for Word and Excel → PDF** (issue #66). Settings → Engines → "When LibreOffice is installed" has two
  new switches, off by default: "Word documents → PDF" (DOCX, DOC, ODT, RTF, laid out as in Word, with Chinese and
  Japanese punctuation rules) and "Spreadsheets → PDF" (XLSX, XLS, ODS, CSV, with the workbook's print area and page
  setup).

### Fixed

- **Excel page breaks** (issue #67). XLSX → PDF starts a new page where a page break was set in Excel (Page Layout →
  Breaks), with frozen header rows repeated at the top. This works without LibreOffice.
- **Fonts for every language in PDFs** (issue #64). Markdown, text, HTML and other documents Filee turns into PDF used
  Korean fonts, so Chinese text mixed two fonts character by character. Chinese (Simplified or Traditional) and
  Japanese text now gets a Chinese or Japanese font already on the computer (e.g. Microsoft YaHei), and text in other
  scripts (Russian, Greek, Thai, Arabic, …) a font that has all its characters. Filee does not download or ship fonts.
- **한글 boxes and equations in PDFs** (issue #65). In HWP / HWPX → PDF:
  - Boxes inside a text box (the "보기" box of an exam paper) came out narrow, with their content clipped and pictures
    shrunk. They now have the size 한글 shows.
  - Equations written in 한글 2022 showed commands as text ("fCIRCf", "ASMALLINTERB", "lek", "rootx") and != or +-
    instead of ≠ and ±. They now show ∘, ∩, ⊂, ≤, √, ≠ and ±.
  - When the computer has no font with the equation symbols (∪, ∈, ⊂, …), Cambria Math, which comes with Windows,
    draws the equations.

## 1.10.2

### Fixed

- **One progress card per conversion** (issue #60). When a long conversion moved from the ring around the donut to
  the progress card in the bottom-right corner, the card appeared twice.
- **Blank PDF pages with variable fonts** (issue #61). A Word or 한글 document whose font is installed as a variable
  OpenType font with CFF2 outlines, such as Source Han Serif VF (思源宋体 VF), became a PDF without text. Such a font is
  now replaced by a similar font already on the computer (e.g. SimSun for Simplified Chinese serif text). Filee does
  not download or ship fonts.

## 1.10.1

### New

- **Long images: trim and split** (issue #50). Two options under "Stack the pages into one tall image" in the PDF
  part of a preset:
  - **Cut the blank space above and below each page.** The pages follow each other without wide white gaps; about
    4 mm stays around the content.
  - **Split into several images when the stack is too tall for the format.** JPG holds up to 65,500 px and WebP
    16,383 px. Instead of shrinking the long image to fit, Filee writes several (name_1, name_2, …), split between
    pages, at full size.
- **Resize to a width.** "Set the width" next to "Scale by percent" and "Limit the long edge" makes images, and long
  images, a given number of pixels wide (e.g. 800 or 1200). Smaller images are left as they are.

### Fixed

- Images taller or wider than a format holds (a long PNG → JPG, over 65,500 px) failed with "could not write the
  file". They are now scaled down to fit (issue #50).

## 1.10.0

### New

- **PDF → one long image** (issue #50). A multi-page PDF becomes a single tall image with the pages one under
  another, e.g. to post a whole document at once. Use **Long image** on the PDF donut (PNG), or tick "Stack the pages
  into one tall image" in any image preset. Pages of different widths are centred; a result taller than the format
  stores (JPG 65,500 px, WebP 16,383 px) or very large is scaled down to fit. TIFF and GIF keep one page per frame.
- **Photos → video in full colour.** HEIC, JPG, PNG, AVIF and other pictures → MP4, MOV or WEBM (with FFmpeg) used
  to pass through a GIF, which leaves 256 colours and bands skies and skin. They now go straight from a full-size PNG:
  a 5-second still video at the photo's own size, with its colours (Display P3 photos stay P3) and transparent areas
  in black. Only photos over about 36 megapixels (48 MP phone cameras) are scaled down, to the largest picture H.264
  and VP9 players decode. Animated GIF/WebP and PDF pages still become a moving video or a slideshow.
- **MOV ↔ MP4 without quality loss.** When the new format can hold the video and sound as they are, like an iPhone
  video (HEVC or H.264 with AAC) going from MOV to MP4, Filee now only rewraps them instead of re-encoding: nothing
  is lost, HDR and 10-bit colour stay, and it takes seconds. The same goes for MKV, WebM and TS. Choosing a smaller
  size (e.g. 720p), another audio bitrate or quality **Small** still re-encodes.
- **Save images as HEIC** (with FFmpeg). Any image Filee reads → HEIC, the format iPhones take photos in: about a
  quarter of a JPG's size at the same quality, stored in 512-pixel tiles like a phone photo, upright, with the colour
  profile and Exif kept. The **HEIC** preset is on the Presets page (quality 80); add it to a donut to use it.
  Transparent areas are filled with the preset's background colour.

## 1.9.0

### New

- **Engine updates.** When a Filee update brings a newer version of an engine you downloaded (FFmpeg, LibreOffice,
  Pandoc, Ghostscript, calibre), a card in the bottom-right corner says so once per version, and Settings → Engines
  shows "New version" with an **Update** button. Engines you never installed are left alone, and the card follows
  "Check for updates automatically".
- **When LibreOffice is installed** (Settings → Engines): presentations (PPTX, PPT, ODP) → PDF now go through
  LibreOffice, so slide backgrounds and shape positions come out as in PowerPoint; turn it off to use the built-in
  route. HWP·HWPX → PDF can go through LibreOffice too (off by default), for a document rhwp lays out wrong.

### Fixes

- **Update installed an older release.** A release found by an earlier check stayed the one Update downloaded, even
  after a newer one came out. Update now asks for the latest release first.
- **PPTX → PDF, HWPX, images: picture backgrounds were dropped** (issue #49). A slide whose background is a picture
  (Format Background → Picture) came out white, hiding white titles; the picture now covers the page.
- **HWP·HWPX → PDF: rhwp 0.8.7.** Among other layout fixes, the header table of two-column exam papers sits at the top
  of the page again instead of over the first questions (issue #51; another overlap in that file is reported to rhwp
  as edwardkim/rhwp#7826).

## 1.8.1

### New

- **Sponsor Filee.** Filee stays free; if it helps you, you can now support its development. **Sponsor** sits at the
  bottom of the navigation next to **GitHub**, above the version, and on the About page next to Star and Report. It
  opens Fairy when Filee is in Korean (Korean cards and easy-pay) and GitHub Sponsors in other languages.

## 1.8.0

### New

- **Failed files at a glance, and a retry.** When files of a conversion fail, Recent conversions on the Home page
  lists them with the reason (click "2 failed" to open the list; names and reasons can be selected and copied).
  **Retry** converts the files that failed again with the same settings and save location, even after the preset
  was changed or deleted. Files that are no longer there are left out. The progress card that pops up after a
  conversion has the same **Retry** button. A retried file from a watch folder is handled like the folder handles
  its files: when the folder moves originals to "originals", the original moves once it converts.
- **Watch folders: choose files and name them** (Settings → Watch folders → *More options*, all optional; empty
  fields change nothing):
  - *Files to convert*: patterns entered as tags. Typing `heic` adds `*.heic`, other text picks names that contain
    it, and `*` stands for any text, `?` for one character. Extensions are suggested while typing, and the card says
    how many files in the folder match. Other files in the folder are left alone.
  - *Names of converted files*: **Edit name rules** opens a window with the base name (typed, or built with buttons
    for the original name, date, time, number and preset name; empty keeps the preset's) and changes applied to it
    from top to bottom. Each change says what it does — replace text, remove text, add at the start or end, spaces
    to `_`, remove a copy number like "(2)", text in brackets, a leading number or symbols, all lowercase or
    uppercase — and asks only for the text it needs; a regular expression is there as the last, advanced kind.
    **Common replacements** on the left lists ready-made changes with an example of each; double-click one or press +
    to add it. The preview shows the name each file in the folder will get, and any name you type. Nothing changes
    until you save. The card shows the name in words ("[Original name]_scan") and the changes.
  - A **preview** on every watch folder shows a converted file as it will be saved (`IMG_0412.jpg →
    converted\Photo_0412.pdf`, using a file already in the folder when there is one), what happens when the name is
    taken and what happens to the original.
  - **Recent results** on every watch folder: how many files it converted and which failed, with the reason, and
    **Retry** right there. A file that converts on a retry or a later copy no longer counts as failed.

### Documentation

- docs/ENGINES.md said Filee never uses engines installed on the computer. It does, as the last choice after a copy
  you picked and Filee's own: that is how FFmpeg and Ghostscript are found on macOS. The order is now described.

## 1.7.2

### Fixes

- When the computer was busy (a screen recorder running, a slow graphics card), slices of the donut could stay lit
  after the pointer left them, pile up in the accent colour and fly apart as the donut closed. A late frame moved the
  highlight further than it should, and every following frame made it worse. The animations now stay steady however
  late the frames come. Turning on "Reduce animations" was the workaround until now.

## 1.7.1

### Fixes

- Moving the pointer from one slice of the donut to another no longer makes the slice it left flash in the accent
  colour as its highlight fades.
- After Filee sat unused for a long time, the donut could take seconds to appear or open without its animation. A
  frame the animation waited for could get lost; the donut now asks again and, if frames still don't come, shows
  itself fully open.
- With "Reduce animations" following Windows, a moment when Windows reported animations off (a remote session,
  waking from sleep) could switch them off in Filee until the setting was changed. Filee reads the Windows setting
  again whenever the donut or the main window opens.

## 1.7.0

### macOS and Linux (preview)

- Filee runs on macOS 14 or later (Apple silicon and Intel) and on Linux (x64 and ARM64). Download the package for
  your system from this release and run `install.sh` in it. These are previews: the macOS app isn't notarized yet,
  so macOS asks you to allow it once under System Settings → Privacy & Security.
- macOS: hold Option and drag files to open the donut. Filee asks for the Accessibility permission the gesture needs
  and starts it as soon as you switch Filee on. "Convert with Filee" is in Finder's right-click menu.
- Linux: the drag gesture works in an X11 session (on Ubuntu, "Ubuntu on Xorg" on the login screen). On Wayland, use
  the drop zone and the right-click action in Nautilus, Nemo, Thunar or Dolphin.
- FFmpeg and Ghostscript come from Homebrew on macOS and from your distribution's packages on Linux. Filee finds them
  by itself.

### New

- Search boxes above the profiles and the available presets (Donut toolbar) and above the preset list. A profile is
  also found by an extension it handles: type "png" to find the image donut.
- "Never trigger in these apps" (Shortcuts) holds the apps as tags. Type a name, with the running apps suggested, or
  pick one from the apps that have a window open.
- The update settings are on the About page now: the automatic check, Check now and the update button.
- The first-run engine choice starts with nothing ticked, so only what you pick is downloaded.

### Fixes

- On macOS and Linux, watch folders wait for a copy that pauses to finish instead of converting it half-written.

## 1.6.0

### New in the preset editor for audio

- Opus and the other lossy audio formats offer 32, 48 and 64 kbit/s as well, for speech and small files (#37).
- **Channels**: same as the source, mono or stereo, for every audio format except AMR (which is always mono).
- WAV gets a **sample rate** (8, 16, 22.05, 44.1 or 48 kHz, or the same as the source) and a **bit depth** (8, 16,
  24 or 32-bit, or 32-bit float). 16-bit stays the default.

### Fixes

- EPS and PS → PNG keep a transparent background where the drawing leaves the page empty, instead of white (#39).
  Ghostscript renders the PNG directly; JPG and other formats without transparency still get a white background.
- PDF → DOCX keeps rows laid out with tab stops, such as a numbered schedule with dates on the right ("1.  RFP
  Published   08/14/2026"): each row stays one paragraph with tabs at the PDF's positions, instead of the numbers,
  labels and dates coming out as separate paragraphs. Forms with a label and a value on each row come out the same
  way (#38).
- PDF → DOCX keeps the line breaks of address blocks and other short lines: "West Linn, Oregon 97068" no longer
  moves up onto the street line (#38). PDF → TXT has the tabs and line breaks too.

## 1.5.2

### Fixes

- Converting audio to Opus or Ogg Vorbis now keeps the album art. FFmpeg can't put a picture into those files, so
  Filee writes it the way they store pictures (a METADATA_BLOCK_PICTURE tag); title, artist and the other tags stay
  too. Matroska audio (MKA) gets the album art as a cover attachment.

## 1.5.1

### Fixes

- Sometimes, after Ctrl+drag opened the donut, the format under the dragged files didn't light up and dropping
  didn't pick it, until a while later. The donut's animation could wait forever for a frame that never came; it now
  starts again after a quarter of a second.
- Each drag over the donut writes one line to the log (`%APPDATA%\Filee\logs`), so if a donut ever ignores a drag
  again, the log shows whether the drag reached it.

## 1.5.0

### FFmpeg, calibre and the others you already have are used by themselves

- Filee now finds FFmpeg, calibre, Pandoc, Ghostscript and LibreOffice that are already on your PC, without asking
  you to download them: on the PATH (Scoop's shims included) or where their installers put them (LibreOffice,
  calibre and Ghostscript under Program Files). Settings → Engines shows "Found on this PC" and the folder.
- If that copy is older than the version Filee is tested with, Settings → Engines recommends an update. It keeps
  working; most conversions don't need the newest version.
- Order: a copy you chose with **Use my copy…**, then Filee's own download (if you installed it), then one found on
  the PC. **Install** is still there if you'd rather use Filee's tested copy. **Check again** searches again after
  you install something.

### One multi-page TIFF

- New option for TIFF presets: **Put every file into one multi-page TIFF**. Every image, every PDF page and every
  page of a TIFF becomes a page of one TIFF, in order, named after the first file. One PDF is enough: a 10-page PDF
  becomes one 10-page TIFF.
- Pages use the preset's compression and are marked as pages of a multi-page document, so older viewers (fax
  software, Microsoft Office Document Imaging) show them page by page. Transparent areas get the preset's background.

### Arranging the donut

- On the Donut toolbar page, dragging a preset (or a slice) over the donut opens a gap where it will land, with a
  see-through slice showing its name. The other slices slide aside, and a moved slice leaves its old place.

## 1.4.0

### Updates install themselves

- In an installed copy, **Update** (bottom of the menu, tray menu, the update notice, Settings → General) now
  downloads the new installer inside Filee, checks it against the release's SHA-256 file and runs it without the setup
  wizard. Setup closes Filee, updates it, keeps your settings and engines and starts Filee again. Windows asks for
  administrator rights once.
- The sidebar, tray menu, Settings → General and the notice show the download progress.
- If the download or the installer fails (no connection, a file that doesn't match its checksum, an installer
  that won't start), Filee tells you, and after OK it opens filee.sh so you can download the new version there.
- The portable copy still opens the release page: download the new ZIP and unpack it over the old folder.
- Updating **to** 1.4.0 from an older version still goes the old way. From 1.4.0 on, it's one click.

### Default save location

- Settings → General → **Default save location**: next to the original (as before), in a subfolder such as
  `converted`, or in one fixed folder.
- Every preset follows it unless you give the preset its own location (Presets → Output → Save to).
- Built-in presets, and your presets that saved next to the original, follow the default from now on. Since the
  default starts as "next to the original", nothing changes until you pick another one.

### Keep the original file dates for every conversion

- New switch in Settings → General: converted files get the created and modified dates of the original, with every
  preset (photos, videos, PDFs, archive conversions).
- Not when several files become one (merged PDF, several files packed into one ZIP): that file has no single original.
- The per-preset option from 1.3.1 stays, for keeping dates with some presets only.
- The `filee` command follows the default save location and this switch too.

### Use the engines you already have

- Settings → Engines → **Use my copy…** next to FFmpeg, calibre, Pandoc, Ghostscript and LibreOffice. Pick the
  program (`ffmpeg.exe`, `ebook-convert.exe`, `pandoc.exe`, `gswin64c.exe` or `soffice.exe`) and Filee uses it
  instead of downloading its own. **Stop using it** goes back.
- FFmpeg needs `ffprobe.exe` in the same folder (most builds have both). Picking a build's main folder finds its
  `bin` folder too.
- The `filee` command uses the same copies.

## 1.3.1

### Keep the original file dates

- Presets → Output → **Keep the original file dates**: converted files get the created and modified dates of the
  original, so converted photos still sort by when they were taken. On the command line: `--keep-dates`.

### Conversions that were there but hard to find

- New **HWP** preset (saved through HWPX). It is on the PDF, documents, HWP, spreadsheet, presentation, text and
  e-book donuts, so Word, PDF, Excel, PowerPoint, Markdown and EPUB files can be saved as `.hwp` directly.
- On the donuts now: PDF → Markdown and EPUB, Word → PNG, HWP → JPG and EPUB. These worked before through another
  format, but weren't on any donut.
- Existing donuts get the new entries once, unless a donut is full or already has them.

### Texts

- Plainer wording in English, Korean and Chinese.
- Fixed: the Korean shortcut button said "recording", and removing a watch folder read like deleting it in Chinese.

## 1.3.0

### The `filee` command line

- Setup can add the `filee` command to PATH. `filee convert` converts files, wildcards (`*.heic`) and folders with
  `--to <format>` or one of your presets; `filee watch` converts files that land in a folder; `filee formats` and
  `filee presets` list what's available.
- `--json` prints the result for scripts. Exit codes: 0 all converted, 1 some failed, 2 wrong arguments or a missing
  file, 3 nothing to convert. Guide: https://filee.sh/cli/

### Watch folders

- A new **Watch folders** page: files that land in a folder you choose are converted while Filee runs, once
  downloads and copies are complete. Keep the originals, or move them to an `originals` folder so the folder works
  like an inbox.

### History

- Settings → General: turn off the list of recent conversions, or clear it.

### Fixes

- Ctrl+drag stopped working after the Photos app had been opened, until Filee was restarted. The donut now stays on
  top of other windows.
