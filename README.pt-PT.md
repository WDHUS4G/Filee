<p align="center">
  <a href="https://filee.sh"><img src="docs/media/banner-en.jpg" alt="Filee: Arrasta. Larga. Convertido." width="100%"></a>
</p>

<p align="center">
  <b>Um conversor de ficheiros gratuito e de código aberto: basta arrastar e largar.</b><br>
  Mantém uma tecla premida (<kbd>Ctrl</kbd>, <kbd>Option</kbd> no macOS), arrasta ficheiros e larga-os num formato da barra circular que se abre junto ao cursor.
</p>

<p align="center">
  <a href="https://filee.sh"><b>Site</b></a> ·
  <a href="https://github.com/WDHUS4G/Filee/releases/latest"><b>Descarregar</b></a> ·
  <a href="https://www.linkedin.com/company/filee-sh/"><b>LinkedIn</b></a> ·
  <a href="#instalação">Instalação</a> ·
  <a href="#linha-de-comandos">Linha de comandos</a> ·
  <a href="#desenvolvimento">Desenvolvimento</a>
</p>

<p align="center">
  <a href="https://github.com/WDHUS4G/Filee/actions/workflows/ci.yml"><img src="https://github.com/WDHUS4G/Filee/actions/workflows/ci.yml/badge.svg" alt="Integração contínua"></a>
  <a href="https://github.com/WDHUS4G/Filee/releases/latest"><img src="https://img.shields.io/github/v/release/WDHUS4G/Filee?color=7F77DD&label=release" alt="Versão mais recente"></a>
  <a href="https://github.com/WDHUS4G/Filee/releases"><img src="https://img.shields.io/github/downloads/WDHUS4G/Filee/total?color=1D9E75" alt="Descargas"></a>
  <img src="https://img.shields.io/badge/Windows-10%20%7C%2011-3C3489" alt="Windows 10 | 11">
  <img src="https://img.shields.io/badge/Linux-x64%20%7C%20ARM64-3C3489" alt="Linux x64 | ARM64">
  <img src="https://img.shields.io/badge/macOS-preview-534AB7" alt="macOS: versão de pré-visualização">
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-6A62C9" alt="Licença MIT"></a>
</p>

<p align="center">
  <a href="https://trendshift.io/repositories/276987"><img src="https://trendshift.io/api/badge/trendshift/repositories/276987/weekly?language=C%23&v=2" alt="Filee: repositório C# da semana em primeiro lugar no Trendshift" width="250" height="55"></a>
  <a href="https://trendshift.io/repositories/276987"><img src="https://trendshift.io/api/badge/trendshift/repositories/276987/daily?language=C%23&v=2" alt="Filee: repositório C# do dia em primeiro lugar no Trendshift" width="250" height="55"></a>
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
  <a href="README.ru.md">Русский</a> ·
  <a href="README.ar.md">العربية</a> ·
  <a href="README.pt-BR.md">Português (Brasil)</a> ·
  <b>Português (Portugal)</b> ·
  <a href="README.fr.md">Français</a> ·
  <a href="README.th.md">ไทย</a>
</p>

<table>
  <tr>
    <td width="33%" valign="top">
      <b>Junto ao cursor</b><br>
      Não precisas de abrir a janela da aplicação.
      Os formatos aparecem onde estás, apenas os que fazem sentido para os ficheiros que arrastas.
    </td>
    <td width="33%" valign="top">
      <b>263 formatos, sem outro software</b><br>
      Imagens, PDF, Word, Excel, PowerPoint, HWP, livros digitais, arquivos, tipos de letra, CAD, modelos 3D e dados geoespaciais funcionam sem Office, Hancom Office ou LibreOffice.
    </td>
    <td width="33%" valign="top">
      <b>Tudo fica no teu computador</b><br>
      Nada é enviado para a Internet.
      O Filee funciona sem ligação e guarda o resultado junto ao original.
    </td>
  </tr>
</table>

## Vê como funciona

<p align="center">
  <a href="https://filee.sh"><img src="docs/media/demo-en.webp" alt="Ao arrastar trip.jpg com Ctrl premido, abre-se uma barra circular de formatos junto ao cursor. O ficheiro é largado em PNG, o anel de progresso completa-se e surge trip.png" width="820"></a><br>
  <sub>Vê o vídeo de 27 segundos e experimenta a demonstração interativa em <a href="https://filee.sh">filee.sh</a></sub>
</p>

## Instalação

Descarrega o Filee da [página de versões](https://github.com/WDHUS4G/Filee/releases/latest).

- **Windows 10/11**: executa `Filee-<version>-win-Setup.exe` ou extrai o `win-Portable.zip` e executa `Filee.exe`.
  - O Filee tem um instalador próprio, disponível nos idiomas da aplicação, que não precisa de mais nada no computador.
  - Permite adicionar o menu do Explorador, o arranque ao iniciar sessão, o comando `filee` e motores opcionais.
  - As instalações silenciosas aceitam as opções do Inno Setup (`/VERYSILENT /SUPPRESSMSGBOXES`).
- **macOS 13+** (versão de pré-visualização): extrai o pacote `osx-arm64` ou `osx-x64` e executa `./install.sh`.
  - Também podes abrir o `.dmg` e arrastar o Filee para Aplicações (o comando `filee` é instalado pelo `install.sh`).
  - A aplicação ainda não foi notarizada pela Apple. Autoriza o primeiro arranque em Definições do Sistema → Privacidade e Segurança → **Abrir mesmo assim**.
- **Linux x64/ARM64** (Ubuntu 22.04+): extrai o pacote e executa `./install.sh` (não precisa de acesso de administrador; requer Python 3).
  - Os gestos de arrastar são suportados em X11.
    Em Wayland, usa a zona de largada e o menu de contexto do gestor de ficheiros, ou consulta as integrações experimentais para GNOME, KDE Plasma e Hyprland no [guia de instalação para Linux](installer/linux/INSTALL.txt).

O Filee mostra as novas versões no menu e atualiza-se, mantendo as tuas definições e motores.
Depois de uma atualização, algumas páginas apresentam as novidades.

**Windows: o Filee não abre, sem janela nem mensagem de erro?**
Isto pode acontecer num Windows 10 que não é atualizado há muito tempo, num computador cujo processador suporta a proteção de pilha imposta por hardware (CET).
O Windows ativa essa proteção, mas falta-lhe um componente de que o .NET precisa, pelo que o Filee e o instalador terminam de imediato.

- **Recomendado:** instala as atualizações disponíveis do Windows (a atualização cumulativa mais recente).
- **Se não puderes atualizar:** desativar a proteção apenas para o Filee costuma resolver o problema.
  Executa estes comandos no PowerShell como administrador:

```powershell
Set-ProcessMitigation -Name Filee.exe -Disable UserShadowStack
Set-ProcessMitigation -Name filee-cli.exe -Disable UserShadowStack
```

Faz o mesmo para o nome do ficheiro do instalador (por exemplo, `Filee-<version>-win-Setup.exe`) ou usa o ZIP portátil, que precisa apenas destas duas linhas.
Para reverter a alteração, remove as entradas em Segurança do Windows → Controlo de aplicações e browser → Definições de proteção contra exploits → Definições do programa.

## Funcionalidades

- **Uma barra circular junto ao cursor**: mantém uma tecla premida, arrasta ficheiros e larga-os num formato.
  Só aparecem formatos compatíveis com os ficheiros.
- **Diretamente do navegador**: arrasta uma imagem, uma ligação para um ficheiro ou uma transferência do Chrome, Edge, Firefox ou Safari da mesma forma.
  - O Filee guarda o ficheiro na pasta Transferências (ou mantém apenas o ficheiro convertido) e converte-o.
  - Os ficheiros das ligações são descarregados sem usar a sessão iniciada no navegador.
- **Uma barra por tipo de ficheiro**, organizada por arrastar e largar, com predefinições de:
  - qualidade, tamanho e DPI
  - páginas e compressão de PDF
  - vídeo e áudio
  - pasta de destino e nomes de ficheiro
- **Duas faces em cada barra circular**: prime <kbd>Tab</kbd> ou roda a roda do rato enquanto está aberta para a virar e aceder a formatos e ferramentas menos comuns.
- **Recortar antes de converter**: o segmento Recortar abre as imagens uma a uma.
  Arrasta a moldura, escolhe uma proporção e só essa parte é guardada.
- **Vários ficheiros ao mesmo tempo**, em paralelo.
  Os percursos com várias etapas (HWP → HWPX → DOCX) são encontrados automaticamente.
- **Pastas monitorizadas** convertem todos os ficheiros que nelas entram.
- **Conversões recentes** na Página inicial, cada uma com uma miniatura: abre o resultado ou escolhe outra aplicação para o abrir.
- **Outras formas de converter**: o menu de contexto, um atalho de teclado, a zona de largada e o [comando `filee`](#linha-de-comandos).
- Temas claro e escuro, com a tua cor de destaque e a opacidade da barra circular à tua escolha, em English, Español, 日本語, 한국어, 简体中文, 繁體中文, Polski, हिन्दी, Русский, العربية (da direita para a esquerda), Português (Brasil), Français, नेपाली, ไทย, Türkçe e Português (Portugal).

## Capturas de ecrã

<table>
  <tr>
    <td width="50%"><img src="docs/media/page-toolbar-en.png" alt="Editor da barra circular com perfis e predefinições, em inglês"></td>
    <td width="50%"><img src="docs/media/page-home-en.png" alt="Página inicial com a zona de largada e as conversões recentes, em inglês"></td>
  </tr>
  <tr>
    <td align="center"><sub>Organiza a barra circular por arrastar e largar, com um perfil por tipo de ficheiro</sub></td>
    <td align="center"><sub>Página inicial: zona de largada e conversões recentes</sub></td>
  </tr>
</table>

## Formatos

| De | Para |
|---|---|
| **Imagens**: JPG, PNG, WEBP, AVIF, TIFF, BMP, GIF, APNG, ICO, ICNS, JPEG XL, JPEG 2000, PSD/PSB, TGA, PPM, HEIC, QOI, DDS, PCX, MNG, CUR, …;<br>leitura de GIMP XCF, RAW de câmaras (CR2, CR3, NEF, ARW, DNG, …), DICOM, OpenEXR, HDR, FITS, JPEG XR (Windows) e Live Photo do iPhone (LIVP) | qualquer um destes formatos (HEIC⁵), PDF, SVG (vetorizado), redimensionamento, escala de cinzentos;<br>Live/Motion Photos (iPhone, Android) → respetivo vídeo (MP4, MOV⁵), e vídeo → Live Photo (iPhone) ou Motion Photo (Android)⁵, experimental |
| **Vetores**: SVG, SVGZ, EMF, WMF, AI<br>EPS, PS¹<br>CDR, VSD, CGM, ODG, FODG² | PDF (vetorial), PNG e outras imagens,<br>SVG ↔ SVGZ, SVG → DXF/DWG, PDF → EPS/PS¹, SVG → EMF, WMF, ODG² |
| **PDF** | DOCX, HWPX, TXT, PNG, JPG, TIFF, CBZ, PPTX (uma imagem de cada página), SVG²;<br>juntar (pela ordem que escolheres), dividir, selecionar intervalos de páginas, comprimir (PDF mais pequenos) |
| **Documentos**: DOCX, DOCM, DOTX, EML, Outlook MSG;<br>leitura de OFD (faturas eletrónicas e documentos oficiais chineses)<br>DOC, ODT, FODT, OTT, RTF, WPS, WPD, Pages, ClarisWorks, MacWrite, T602, …² | PDF, DOCX, HWPX, EPUB, TXT, HTML;<br>MSG → EML<br>ODT, FODT, OTT² |
| **Folhas de cálculo**: XLSX, XLSM, XLS, XLT, ODS, CSV, TSV<br>OTS, FODS, Numbers, ET² | PDF, XLSX, ODS, CSV, TSV, JSON, HWPX, HTML<br>XLT, OTS² |
| **Apresentações**: PPTX, PPTM, POTX, POTM, PPSX<br>PPT, ODP, OTP, FODP, Keynote² | PDF, PNG, JPG, DOCX, HWPX<br>OTP² |
| **HWP**: HWP, HWPX | PDF, PNG, DOCX, HWPX ↔ HWP, TXT, Markdown, HTML |
| **Texto**: TXT, LOG, código de scripts (PY, PAC, BAT, COMMAND; apenas lido, nunca executado), Markdown, HTML;<br>leitura de MHTML (páginas Web guardadas)<br>reStructuredText, LaTeX, Org, AsciiDoc, Typst, MediaWiki, DocBook, Jupyter, Textile, man, OPML³ | TXT em UTF-8, PDF, DOCX, HWPX, EPUB<br>conversão entre estes formatos, diapositivos PPTX³ |
| **Livros digitais**: EPUB, KEPUB, MOBI, AZW3, AZW, AZW4, FB2, CBZ, CBR, CB7, HTMLZ, TXTZ<br>LIT, LRF, CHM, PDB, …⁴ | PDF, EPUB, KEPUB (Kobo), DOCX, TXT, HTML, HWPX<br>MOBI, AZW3⁴ |
| **Vídeo**⁵: MP4, MOV, MKV, WEBM, AVI, WMV, FLV, MPEG, Video CD (DAT), TS, M2TS, 3GP, OGV, VOB, … | MP4 (H.264, HEVC, AV1), WEBM, MOV, MKV, AVI, GIF, APNG, MP3, M4A, 720p,<br>legendas (SRT, ASS, WebVTT) |
| **Áudio**⁵: MP3, M4A, AAC, WAV, FLAC, OGG, OPUS, WMA, AIFF, AMR, APE, WavPack, TTA, DSD (DSF, DFF), DTS, AC3, E-AC3, … | MP3, M4A (AAC, ALAC), WAV, FLAC, OGG, OPUS, AAC, WavPack, TTA, W64, AC3, E-AC3 |
| **Legendas**: SRT, ASS/SSA, WebVTT, SAMI (SMI), LRC, TTML, MicroDVD, SCC | conversão entre estes formatos em UTF-8 (leitura correta de ficheiros coreanos em CP949) |
| **Arquivos**: ZIP, ANSYS Workbench (WBPZ), 7Z, RAR, TAR, TAR.GZ, TAR.BZ2, TAR.XZ, GZ, ISO, CAB, DMG, ALZ, EGG, … | extrair, ZIP, 7Z, TAR, TAR.GZ;<br>quaisquer ficheiros → um ZIP |
| **CAD**: DWG, DXF | PDF, SVG, PNG, DWG ↔ DXF |
| **Tipos de letra**: TTF, OTF, WOFF, WOFF2, EOT | conversão entre estes formatos |
| **Dados**: JSON, JSON Lines, YAML, TOML, XML<br>vCard, iCalendar | conversão entre estes formatos, CSV e XLSX (tabelas);<br>vCard, iCalendar ↔ CSV |
| **Modelos 3D**: GLB, glTF, STL, OBJ (+ MTL), 3MF, PLY;<br>leitura de FBX | conversão entre estes formatos, pré-visualização em PNG |
| **Vetores geoespaciais**: Shapefile, GeoJSON, KML, KMZ, GPX<br>CSV com coordenadas | conversão entre estes formatos e CSV / XLSX / JSON;<br>os dados projetados (UTM, Web Mercator, Lambert, Albers) são convertidos em longitude / latitude WGS 84 |

Os formatos sem marca funcionam de imediato, sem Office, Hancom Office ou LibreOffice.
Os formatos marcados precisam de um motor opcional, transferido apenas quando necessário: ¹ Ghostscript, ² LibreOffice, ³ Pandoc, ⁴ calibre, ⁵ FFmpeg ([detalhes](docs/ENGINES.md)).

## Linha de comandos

O comando `filee` usa os mesmos motores e predefinições da aplicação (é adicionado pelo instalador ou pelo `install.sh`).

```
filee convert photo.heic --to jpg
filee convert *.png --to webp --quality 80 -o converted
filee watch D:\Inbox --to pdf --move-originals
```

Todos os comandos e opções: [filee.sh/cli](https://filee.sh/cli/) ou `filee help`.

## Desenvolvimento

Compila com o SDK do .NET 10 em Windows, macOS ou Linux:

```bash
git clone https://github.com/WDHUS4G/Filee.git
cd Filee
pwsh build/fetch-engines.ps1 -Only rhwp,pandoc   # motores opcionais
dotnet run --project src/Filee.App
dotnet test
```

[Arquitetura](docs/ARCHITECTURE.md) · [Motores](docs/ENGINES.md) · [Adicionar um conversor](docs/ADDING-A-CONVERTER.md) · [Adicionar um idioma](docs/ADDING-A-LANGUAGE.md) · [Contribuir](CONTRIBUTING.md)

Os pull requests são bem-vindos.
Se o Filee te poupa alguns cliques, uma ⭐ ajuda outras pessoas a descobri-lo.

## Política de assinatura de código

O Filee é desenvolvido e publicado por **Myeonggu Kim** ([KnifeLemon](https://github.com/KnifeLemon)).

- As versões para Windows são assinadas com um certificado [Certum](https://www.certum.eu/) de assinatura de código para projetos de código aberto, emitido a Myeonggu Kim ("Open Source Developer MYEONGGU KIM").
  - As versões anteriores, até à 2.0.0 inclusive, não estão assinadas.
  - O SmartScreen pode continuar a mostrar um aviso sobre uma versão nova até que esta tenha sido descarregada vezes suficientes ("Mais informações" → "Executar mesmo assim").
- Os pacotes para macOS têm uma assinatura ad hoc e não foram notarizados pela Apple.
- Só são assinados ficheiros compilados pelo fluxo de publicação do GitHub Actions deste repositório, e cada versão é assinada e aprovada manualmente.
  - As bibliotecas e os programas assinados pelos respetivos autores (.NET, SkiaSharp, ImageMagick, o runtime do Microsoft C++…) mantêm as suas assinaturas; os componentes incluídos sem assinatura (rhwp, 7-Zip e a maioria das bibliotecas) são assinados com o certificado do Filee, como parte do Filee.

### Política de privacidade

O Filee converte os ficheiros no teu computador e nunca os envia para a Internet.
Não recolhe dados de utilização nem envia telemetria.
Só se liga à Internet:

- para perguntar ao GitHub (`api.github.com`) se existe uma versão mais recente, pouco depois do arranque e, depois, a cada poucas horas.
  Podes desativar esta opção em Definições → Acerca de ("Procurar atualizações automaticamente").
- para descarregar os motores de conversão que escolheres instalar, a partir dos endereços oficiais fixados em [`engines.json`](src/Filee.Engines/Infrastructure/engines.json), e os idiomas de reconhecimento de texto (OCR) usados por uma predefinição, fixados em [`ocr-languages.json`](src/Filee.Engines/Infrastructure/ocr-languages.json).
- quando clicas numa ligação, que abre o navegador.

## Apoiar

O Filee é gratuito e continuará a sê-lo.
Se quiseres apoiar o projeto, escolhe a forma que preferires ([Fairy](https://fairy.hada.io/@filee) aceita cartões coreanos e pagamentos simplificados).

**O teu nome ou logótipo aqui.**
Quem contribui mensalmente é mencionado aqui pelo nome, e os patrocinadores têm o seu logótipo aqui e na página Acerca de do Filee.
Quem faz uma contribuição pontual também pode pedir para ser mencionado.

<!-- sponsors: src/Filee.App/Assets/Sponsors/sponsors.json -->

<p align="center">
  <a href="https://github.com/sponsors/KnifeLemon"><img src="https://img.shields.io/badge/GitHub%20Sponsors-EA4AAA?style=for-the-badge&logo=githubsponsors&logoColor=white" alt="GitHub Sponsors"></a>
  <a href="https://patreon.com/KnifeLemon"><img src="https://img.shields.io/badge/Patreon-000000?style=for-the-badge&logo=patreon&logoColor=white" alt="Patreon"></a>
  <a href="https://ko-fi.com/knifelemon"><img src="https://img.shields.io/badge/Ko--fi-FF6433?style=for-the-badge&logo=kofi&logoColor=white" alt="Ko-fi"></a>
  <a href="https://paypal.me/knifelemon"><img src="https://img.shields.io/badge/PayPal-002991?style=for-the-badge&logo=paypal&logoColor=white" alt="PayPal"></a>
  <a href="https://fairy.hada.io/@filee"><img src="https://img.shields.io/badge/Fairy-2E9E6B?style=for-the-badge" alt="Fairy"></a>
</p>

## Licença

MIT, © 2026 Myeonggu Kim (KnifeLemon) e colaboradores do Filee.
Os componentes de terceiros incluídos mantêm as suas licenças, indicadas em [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

<p align="center">
  <a href="https://star-history.com/#WDHUS4G/Filee&Date"><img src="https://api.star-history.com/svg?repos=WDHUS4G/Filee&type=Date" alt="Histórico de estrelas" width="600"></a>
</p>
