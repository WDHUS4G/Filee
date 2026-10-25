<p align="center">
  <a href="https://filee.sh"><img src="docs/media/banner-en.jpg" alt="Filee : Glissez. Déposez. Converti." width="100%"></a>
</p>

<p align="center">
  <b>Un convertisseur de fichiers gratuit et open source qui s'utilise en glissant.</b><br>
  Maintenez une touche enfoncée (<kbd>Ctrl</kbd>, <kbd>Option</kbd> sur macOS), faites glisser des fichiers et déposez-les sur un format du menu circulaire qui s'ouvre sous votre curseur.
</p>

<p align="center">
  <a href="https://filee.sh"><b>Site web</b></a> ·
  <a href="https://github.com/WDHUS4G/Filee/releases/latest"><b>Télécharger</b></a> ·
  <a href="https://www.linkedin.com/company/filee-sh/"><b>LinkedIn</b></a> ·
  <a href="#installation">Installation</a> ·
  <a href="#ligne-de-commande">Ligne de commande</a> ·
  <a href="#développement">Développement</a>
</p>

<p align="center">
  <a href="https://github.com/WDHUS4G/Filee/actions/workflows/ci.yml"><img src="https://github.com/WDHUS4G/Filee/actions/workflows/ci.yml/badge.svg" alt="CI"></a>
  <a href="https://github.com/WDHUS4G/Filee/releases/latest"><img src="https://img.shields.io/github/v/release/WDHUS4G/Filee?color=7F77DD&label=release" alt="Dernière version"></a>
  <a href="https://github.com/WDHUS4G/Filee/releases"><img src="https://img.shields.io/github/downloads/WDHUS4G/Filee/total?color=1D9E75" alt="Téléchargements"></a>
  <img src="https://img.shields.io/badge/Windows-10%20%7C%2011-3C3489" alt="Windows 10 | 11">
  <img src="https://img.shields.io/badge/Linux-x64%20%7C%20ARM64-3C3489" alt="Linux x64 | ARM64">
  <img src="https://img.shields.io/badge/macOS-preview-534AB7" alt="macOS en préversion">
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-6A62C9" alt="Licence MIT"></a>
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
  <a href="README.ru.md">Русский</a> ·
  <a href="README.ar.md">العربية</a> ·
  <a href="README.pt-BR.md">Português (Brasil)</a> ·
  <a href="README.pt-PT.md">Português (Portugal)</a> ·
  <b>Français</b> ·
  <a href="README.th.md">ไทย</a>
</p>

<table>
  <tr>
    <td width="33%" valign="top">
      <b>Juste sous votre curseur</b><br>
      Aucune fenêtre à ouvrir.
      Les formats apparaissent là où vous êtes déjà, et seulement ceux qui ont un sens pour les fichiers que vous faites glisser.
    </td>
    <td width="33%" valign="top">
      <b>263 formats, sans autre logiciel</b><br>
      Images, PDF, Word, Excel, PowerPoint, HWP, livres numériques, archives, polices, CAO, modèles 3D et données géospatiales fonctionnent sans Office, Hancom Office ni LibreOffice.
    </td>
    <td width="33%" valign="top">
      <b>Tout reste sur votre ordinateur</b><br>
      Rien n'est envoyé en ligne.
      Filee fonctionne hors connexion et enregistre le résultat à côté de l'original.
    </td>
  </tr>
</table>

## Voir Filee en action

<p align="center">
  <a href="https://filee.sh"><img src="docs/media/demo-en.webp" alt="On fait glisser trip.jpg en maintenant Ctrl : un menu circulaire de formats s'ouvre sous le curseur, le fichier est déposé sur PNG, un anneau de progression se remplit et trip.png apparaît" width="820"></a><br>
  <sub>Regardez la vidéo de 27 secondes et essayez la démo interactive sur <a href="https://filee.sh">filee.sh</a></sub>
</p>

## Installation

Téléchargez Filee depuis les [Releases](https://github.com/WDHUS4G/Filee/releases/latest).

- **Windows 10/11** : exécutez `Filee-<version>-win-Setup.exe`, ou décompressez `win-Portable.zip` et lancez `Filee.exe`.
  - Le programme d'installation est celui de Filee, dans les langues de Filee, et ne demande rien d'autre sur le PC.
  - Il peut ajouter le menu de l'Explorateur, le démarrage à l'ouverture de session, la commande `filee` et des moteurs facultatifs.
  - Les installations silencieuses acceptent les options d'Inno Setup (`/VERYSILENT /SUPPRESSMSGBOXES`).
- **macOS 13+** (préversion) : décompressez le paquet `osx-arm64` ou `osx-x64` et lancez `./install.sh`.
  - Ou ouvrez le `.dmg` et faites glisser Filee dans Applications (seul `install.sh` ajoute la commande `filee`).
  - L'application n'est pas encore notarisée : autorisez le premier lancement dans Réglages Système → Confidentialité et sécurité → **Ouvrir quand même**.
- **Linux x64/ARM64** (Ubuntu 22.04+) : décompressez le paquet et lancez `./install.sh` (sans root, Python 3 requis).
  - Les gestes de glisser nécessitent X11.
    Sous Wayland, utilisez la zone de dépôt et le menu contextuel du gestionnaire de fichiers.

Filee signale les nouvelles versions dans son menu et se met à jour tout seul, en conservant vos paramètres et vos moteurs.
Après une mise à jour, quelques diapositives présentent les nouveautés.

**Windows : Filee ne démarre pas (ni fenêtre ni erreur) ?**
Cela arrive sur un Windows 10 qui n'a pas été mis à jour depuis longtemps, sur un PC dont le processeur prend en charge la protection de la pile appliquée par le matériel (CET).
Windows active cette protection mais il lui manque un élément dont .NET a besoin, si bien que Filee et le programme d'installation s'arrêtent aussitôt.

- **Recommandé :** installez les mises à jour Windows disponibles (la dernière mise à jour cumulative).
- **Si la mise à jour est impossible :** désactiver la protection pour Filee seul fonctionne généralement.
  Exécutez ceci dans PowerShell en tant qu'administrateur :

```powershell
Set-ProcessMitigation -Name Filee.exe -Disable UserShadowStack
Set-ProcessMitigation -Name filee-cli.exe -Disable UserShadowStack
```

Faites de même avec le nom de fichier du programme d'installation (par ex. `Filee-<version>-win-Setup.exe`), ou utilisez le ZIP portable, qui n'a besoin que de ces deux lignes.
Pour annuler, supprimez ces entrées dans Sécurité Windows → Contrôle des applications et du navigateur → Paramètres de protection contre les exploits → Paramètres du programme.

## Fonctionnalités

- **Un menu circulaire sous le curseur** : maintenez une touche, faites glisser des fichiers et déposez-les sur un format.
  Seuls les formats adaptés aux fichiers apparaissent.
- **Directement depuis le navigateur** : faites glisser de la même façon une image, un lien vers un fichier ou un téléchargement depuis Chrome, Edge, Firefox ou Safari.
  - Filee l'enregistre dans votre dossier Téléchargements (ou ne garde que le fichier converti) et le convertit.
  - Les liens sont téléchargés sans la session ouverte dans votre navigateur.
- **Un menu circulaire par type de fichier**, organisé par glisser-déposer, avec des préréglages pour :
  - qualité, taille, DPI
  - pages et compression PDF
  - vidéo et audio
  - dossier de destination et noms de fichiers
- **Deux faces pour chaque menu circulaire** : menu ouvert, appuyez sur <kbd>Tab</kbd> ou tournez la molette de la souris pour le retourner et accéder aux formats et outils moins courants.
- **Recadrer avant de convertir** : la tranche Recadrer ouvre les images une par une.
  Faites glisser le cadre, choisissez des proportions, et seule cette partie est enregistrée.
- **Plusieurs fichiers à la fois**, en parallèle.
  Les chaînes de conversion en plusieurs étapes (HWP → HWPX → DOCX) sont trouvées automatiquement.
- **Des dossiers surveillés** convertissent chaque fichier qui y arrive.
- **Conversions récentes** dans l'Accueil, chacune avec sa miniature : ouvrez le résultat, ou ouvrez-le avec une autre application.
- **D'autres points d'entrée** : le menu contextuel, un raccourci clavier, la zone de dépôt et la [commande `filee`](#ligne-de-commande).
- Thèmes clair et sombre avec votre propre couleur d'accentuation et l'opacité du menu circulaire, en English, Español, 日本語, 한국어, 简体中文, 繁體中文, Polski, हिन्दी, Русский, العربية (de droite à gauche), Português (Brasil), Français, नेपाली, ไทย, Türkçe et Português (Portugal).

## Captures d'écran

<table>
  <tr>
    <td width="50%"><img src="docs/media/page-toolbar-en.png" alt="Éditeur du menu circulaire avec profils et préréglages"></td>
    <td width="50%"><img src="docs/media/page-home-en.png" alt="Écran d'accueil avec la zone de dépôt et les conversions récentes"></td>
  </tr>
  <tr>
    <td align="center"><sub>Organisez le menu circulaire par glisser-déposer, un profil par type de fichier</sub></td>
    <td align="center"><sub>Accueil : zone de dépôt et conversions récentes</sub></td>
  </tr>
</table>

## Formats

| De | Vers |
|---|---|
| **Images** : JPG, PNG, WEBP, AVIF, TIFF, BMP, GIF, APNG, ICO, ICNS, JPEG XL, JPEG 2000, PSD/PSB, TGA, PPM, HEIC, QOI, DDS, PCX, MNG, CUR, … ;<br>GIMP XCF, RAW d'appareil photo (CR2, CR3, NEF, ARW, DNG, …), DICOM, OpenEXR, HDR, FITS, JPEG XR (Windows), Live Photo d'iPhone (LIVP) en lecture | n'importe lequel de ces formats (HEIC⁵), PDF, SVG (vectorisé), redimensionné, niveaux de gris ;<br>Live/Motion Photos (iPhone, Android) → leur vidéo (MP4, MOV⁵), et une vidéo → Live Photo (iPhone) ou Motion Photo (Android)⁵, expérimental |
| **Vectoriel** : SVG, SVGZ, EMF, WMF, AI<br>EPS, PS¹<br>CDR, VSD, CGM, ODG, FODG² | PDF (vectoriel), PNG et autres images,<br>SVG ↔ SVGZ, SVG → DXF/DWG, PDF → EPS/PS¹, SVG → EMF, WMF, ODG² |
| **PDF** | DOCX, HWPX, TXT, PNG, JPG, TIFF, CBZ, PPTX (une image par page), SVG² ;<br>fusionner (dans l'ordre que vous choisissez d'abord), diviser, plages de pages, compresser (PDF plus légers) |
| **Documents** : DOCX, DOCM, DOTX, EML, Outlook MSG ;<br>OFD (factures électroniques et documents officiels chinois) en lecture<br>DOC, ODT, FODT, OTT, RTF, WPS, WPD, Pages, ClarisWorks, MacWrite, T602, …² | PDF, DOCX, HWPX, EPUB, TXT, HTML ;<br>MSG → EML<br>ODT, FODT, OTT² |
| **Feuilles de calcul** : XLSX, XLSM, XLS, XLT, ODS, CSV, TSV<br>OTS, FODS, Numbers, ET² | PDF, XLSX, ODS, CSV, TSV, JSON, HWPX, HTML<br>XLT, OTS² |
| **Présentations** : PPTX, PPTM, POTX, POTM, PPSX<br>PPT, ODP, OTP, FODP, Keynote² | PDF, PNG, JPG, DOCX, HWPX<br>OTP² |
| **HWP** : HWP, HWPX | PDF, PNG, DOCX, HWPX ↔ HWP, TXT, Markdown, HTML |
| **Texte** : TXT, LOG, code source de scripts (PY, PAC, BAT, COMMAND ; lus, jamais exécutés), Markdown, HTML ;<br>MHTML (pages web enregistrées) en lecture<br>reStructuredText, LaTeX, Org, AsciiDoc, Typst, MediaWiki, DocBook, Jupyter, Textile, man, OPML³ | TXT UTF-8, PDF, DOCX, HWPX, EPUB<br>entre eux, diapositives PPTX³ |
| **Livres numériques** : EPUB, KEPUB, MOBI, AZW3, AZW, AZW4, FB2, CBZ, CBR, CB7, HTMLZ, TXTZ<br>LIT, LRF, CHM, PDB, …⁴ | PDF, EPUB, KEPUB (Kobo), DOCX, TXT, HTML, HWPX<br>MOBI, AZW3⁴ |
| **Vidéo**⁵ : MP4, MOV, MKV, WEBM, AVI, WMV, FLV, MPEG, Video CD (DAT), TS, M2TS, 3GP, OGV, VOB, … | MP4 (H.264, HEVC, AV1), WEBM, MOV, MKV, AVI, GIF, APNG, MP3, M4A, 720p,<br>sous-titres (SRT, ASS, WebVTT) |
| **Audio**⁵ : MP3, M4A, AAC, WAV, FLAC, OGG, OPUS, WMA, AIFF, AMR, APE, WavPack, TTA, DSD (DSF, DFF), DTS, AC3, E-AC3, … | MP3, M4A (AAC, ALAC), WAV, FLAC, OGG, OPUS, AAC, WavPack, TTA, W64, AC3, E-AC3 |
| **Sous-titres** : SRT, ASS/SSA, WebVTT, SAMI (SMI), LRC, TTML, MicroDVD, SCC | entre eux, en UTF-8 (les fichiers coréens en CP949 sont lus correctement) |
| **Archives** : ZIP, ANSYS Workbench (WBPZ), 7Z, RAR, TAR, TAR.GZ, TAR.BZ2, TAR.XZ, GZ, ISO, CAB, DMG, ALZ, EGG, … | extraire, ZIP, 7Z, TAR, TAR.GZ ;<br>n'importe quels fichiers → un seul ZIP |
| **CAO** : DWG, DXF | PDF, SVG, PNG, DWG ↔ DXF |
| **Polices** : TTF, OTF, WOFF, WOFF2, EOT | entre elles |
| **Données** : JSON, JSON Lines, YAML, TOML, XML<br>vCard, iCalendar | entre eux, CSV et XLSX (tableaux) ;<br>vCard, iCalendar ↔ CSV |
| **Modèles 3D** : GLB, glTF, STL, OBJ (+ MTL), 3MF, PLY ;<br>FBX en lecture | entre eux, aperçu PNG |
| **Données géospatiales vectorielles** : Shapefile, GeoJSON, KML, KMZ, GPX<br>CSV avec coordonnées | entre eux et CSV / XLSX / JSON ;<br>les données projetées (UTM, Web Mercator, Lambert, Albers) sortent en longitude / latitude WGS 84 |

Les formats sans marque fonctionnent d'emblée, sans Office, Hancom Office ni LibreOffice.
Les formats marqués nécessitent un moteur facultatif, téléchargé seulement en cas de besoin : ¹ Ghostscript, ² LibreOffice, ³ Pandoc, ⁴ calibre, ⁵ FFmpeg ([détails](docs/ENGINES.md)).

## Ligne de commande

La commande `filee` utilise les mêmes moteurs et préréglages que l'application (le programme d'installation ou `install.sh` l'ajoute).

```
filee convert photo.heic --to jpg
filee convert *.png --to webp --quality 80 -o converted
filee watch D:\Inbox --to pdf --move-originals
```

Toutes les commandes et options : [filee.sh/cli](https://filee.sh/cli/) ou `filee help`.

## Développement

Compilez avec le SDK .NET 10 sous Windows, macOS ou Linux :

```bash
git clone https://github.com/WDHUS4G/Filee.git
cd Filee
pwsh build/fetch-engines.ps1 -Only rhwp,pandoc   # optional engines
dotnet run --project src/Filee.App
dotnet test
```

[Architecture](docs/ARCHITECTURE.md) · [Moteurs](docs/ENGINES.md) · [Ajouter un convertisseur](docs/ADDING-A-CONVERTER.md) · [Ajouter une langue](docs/ADDING-A-LANGUAGE.md) · [Contribuer](CONTRIBUTING.md)

Les pull requests sont les bienvenues.
Si Filee vous épargne quelques clics, une ⭐ aide d'autres personnes à le découvrir.

## Politique de signature du code

Filee est développé et publié par **Myeonggu Kim** ([KnifeLemon](https://github.com/KnifeLemon)).

- Les versions Windows sont signées avec un certificat de signature de code open source [Certum](https://www.certum.eu/) délivré à Myeonggu Kim.
  - Les versions jusqu'à la 2.0.0 ne sont pas signées.
  - Windows SmartScreen peut encore afficher un avertissement pour une nouvelle version tant qu'elle n'a pas été assez téléchargée (« Informations complémentaires » → « Exécuter quand même »).
- Les paquets macOS sont signés ad hoc et ne sont pas notarisés.
- Seuls les fichiers produits par le workflow de publication GitHub Actions de ce dépôt sont signés, et chaque version est signée et approuvée manuellement.
  - Les bibliothèques et programmes déjà signés par leurs éditeurs gardent leur signature ; ceux qui arrivent sans signature (rhwp, 7-Zip et la plupart des bibliothèques) sont signés avec le certificat de Filee.

### Politique de confidentialité

Filee convertit les fichiers sur votre propre ordinateur et ne les envoie jamais en ligne.
Il ne collecte aucune donnée d'utilisation et n'envoie aucune télémétrie.
Il ne se connecte à Internet que :

- pour demander à GitHub (`api.github.com`) si une nouvelle version existe, peu après le démarrage puis toutes les quelques heures.
  Désactivez-le dans Paramètres → À propos (« Rechercher automatiquement les mises à jour »).
- pour télécharger les moteurs de conversion que vous choisissez d'installer, depuis les emplacements officiels fixés dans [`engines.json`](src/Filee.Engines/Infrastructure/engines.json), et les langues de reconnaissance de texte (OCR) qu'un préréglage utilise, fixées dans [`ocr-languages.json`](src/Filee.Engines/Infrastructure/ocr-languages.json).
- quand vous cliquez sur un lien, ce qui ouvre votre navigateur web.

## Soutenir Filee

Filee est gratuit et le restera.
Si vous souhaitez le soutenir, choisissez le moyen qui vous convient ([Fairy](https://fairy.hada.io/@filee) accepte les cartes coréennes et le paiement simplifié).

**Votre nom ou votre logo ici.**
Les soutiens mensuels sont cités ici par leur nom, et les sponsors ont leur logo ici et sur la page À propos de Filee.
Les soutiens ponctuels peuvent aussi demander à y figurer.

<!-- sponsors: src/Filee.App/Assets/Sponsors/sponsors.json -->

<p align="center">
  <a href="https://github.com/sponsors/KnifeLemon"><img src="https://img.shields.io/badge/GitHub%20Sponsors-EA4AAA?style=for-the-badge&logo=githubsponsors&logoColor=white" alt="GitHub Sponsors"></a>
  <a href="https://patreon.com/KnifeLemon"><img src="https://img.shields.io/badge/Patreon-000000?style=for-the-badge&logo=patreon&logoColor=white" alt="Patreon"></a>
  <a href="https://ko-fi.com/knifelemon"><img src="https://img.shields.io/badge/Ko--fi-FF6433?style=for-the-badge&logo=kofi&logoColor=white" alt="Ko-fi"></a>
  <a href="https://paypal.me/knifelemon"><img src="https://img.shields.io/badge/PayPal-002991?style=for-the-badge&logo=paypal&logoColor=white" alt="PayPal"></a>
  <a href="https://fairy.hada.io/@filee"><img src="https://img.shields.io/badge/Fairy-2E9E6B?style=for-the-badge" alt="Fairy"></a>
</p>

## Licence

MIT, © 2026 Myeonggu Kim (KnifeLemon) et les contributeurs de Filee.
Les composants tiers inclus conservent leurs propres licences, répertoriées dans [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

<p align="center">
  <a href="https://star-history.com/#WDHUS4G/Filee&Date"><img src="https://api.star-history.com/svg?repos=WDHUS4G/Filee&type=Date" alt="Star history" width="600"></a>
</p>
