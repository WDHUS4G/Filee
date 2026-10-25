// Editable view of one Preset. Shared by the Presets page and the preset popup (right-click / ✎ on the donut),
// so both always show exactly the same editor.
//
// Works on a clone; Apply() copies the edits back into the original preset (Cancel = just drop the VM).

using CommunityToolkit.Mvvm.ComponentModel;
using Filee.Core.Formats;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Engines.Ocr;

namespace Filee.App.ViewModels;

/// <summary>What item templates show of a <see cref="Choice{T}"/> (XAML can't name a generic type).</summary>
public interface IChoice
{
    string Label { get; }

    /// <summary>Secondary text shown right-aligned, e.g. the category of a format.</summary>
    string? Detail { get; }
}

/// <summary>A value with a localized label for combo boxes.</summary>
public sealed record Choice<T>(T Value, string Label, string? Detail = null) : IChoice
{
    public override string ToString() => Label;
}

/// <summary>Selections bound to properties that always hold a value (a choice, the open page).</summary>
public static class Choices
{
    /// <summary>
    /// For the OnXxxChanged(oldValue, newValue) hook of such a property. A combo box or list box sets null when it
    /// loses its selection (the selected item toggled off, its items replaced): the previous value is put back through
    /// <paramref name="restore"/>, which also shows it in the control again. Returns true then, so the hook stops.
    /// </summary>
    public static bool Restore<T>(T? oldValue, T? newValue, Action<T> restore) where T : class
    {
        if (newValue is not null || oldValue is null)
            return false;
        restore(oldValue);
        return true;
    }
}

public sealed partial class PresetEditorViewModel : ObservableObject
{
    private static readonly HashSet<string> LossyTargets = ["jpg", "webp", "avif", "heic"];

    /// <summary>Audio targets without a bitrate or quality to choose.</summary>
    private static readonly HashSet<string> LosslessAudioTargets = ["wav", "flac", "aiff", "wv", "tta", "w64"];

    private static readonly int[] MaxHeightSteps = [0, 2160, 1440, 1080, 720, 480];
    // 32-64 kbit/s: speech (Opus is still clear at 32 kbit/s mono), small files.
    private static readonly int[] BitrateSteps = [0, 32, 48, 64, 96, 128, 192, 256, 320];
    private static readonly int[] SampleRateSteps = [0, 8000, 16000, 22050, 44100, 48000];

    private readonly Preset _original;
    private readonly Preset _edit;
    private readonly ILocalizer _loc;

    public PresetEditorViewModel(Preset preset, ILocalizer loc)
    {
        _original = preset;
        _edit = preset.Clone();
        _loc = loc;

        // ~150 formats in registry order, which groups them by category; the category is shown next to each name
        // and typing the first letters of a name jumps to it.
        Targets =
        [
            new Choice<string>(BuiltInData.SameAsSource, loc["presets.same_as_source"]),
            .. FormatRegistry.Known
                .Where(f => f.Writable)
                .Select(f => new Choice<string>(
                    f.Id,
                    f.Id == FormatRegistry.Folder ? loc["presets.target_folder"] : f.DisplayName,
                    FormatLabels.Category(loc, f.Category))),
        ];
        ResizeModes = Enum.GetValues<ResizeMode>().Select(v => new Choice<ResizeMode>(v, loc[$"presets.resize.{v}"])).ToList();
        Rotations = Enum.GetValues<ImageRotation>().Select(v => new Choice<ImageRotation>(v, loc[$"presets.rotation.{v}"])).ToList();
        Flips = Enum.GetValues<ImageFlip>().Select(v => new Choice<ImageFlip>(v, loc[$"presets.flip.{v}"])).ToList();
        PageSizes = Enum.GetValues<PdfPageSize>().Select(v => new Choice<PdfPageSize>(v, loc[$"presets.page_size.{v}"])).ToList();
        CompressionLevels = Enum.GetValues<PdfCompression>().Select(v => new Choice<PdfCompression>(v, loc[$"presets.pdf_compression.{v}"])).ToList();
        // Each language under its own name; one not downloaded yet shows its size (it downloads with the first file).
        // On a Mac, Apple Vision reads the common languages without a download.
        var vision = new VisionConverter();
        OcrHint = loc[vision.GetStatus().IsAvailable ? "presets.ocr_hint_macos" : "presets.ocr_hint"];
        OcrLanguageChoices = [.. OcrLanguages.All
            .OrderBy(l => l.Code != OcrLanguages.Default).ThenBy(l => l.NativeName, StringComparer.CurrentCulture)
            .Select(l => new Choice<string>(l.Code, l.NativeName,
                OcrLanguages.IsInstalled(l.Code) || vision.Reads(l.Code) ? null
                    : loc.Format("presets.ocr_language_size", $"{l.Size / 1_000_000.0:0.#} MB")))];
        Locations = Enum.GetValues<OutputLocation>().Select(v => new Choice<OutputLocation>(v, loc[$"presets.location.{v}"])).ToList();
        Conflicts = Enum.GetValues<ConflictPolicy>().Select(v => new Choice<ConflictPolicy>(v, loc[$"presets.conflict.{v}"])).ToList();
        TiffCompressions = Enum.GetValues<TiffCompression>().Select(v => new Choice<TiffCompression>(v, v.ToString().ToUpperInvariant())).ToList();
        FitsTiffModes = Enum.GetValues<FitsTiffMode>().Select(v => new Choice<FitsTiffMode>(v, loc[$"presets.fits_tiff.{v}"])).ToList();
        MediaQualities = Enum.GetValues<MediaQuality>().Select(v => new Choice<MediaQuality>(v, loc[$"presets.media_quality.{v}"])).ToList();
        MaxHeights = Steps(MaxHeightSteps, _edit.Media.MaxHeight, h => h == 0 ? loc["presets.max_height.original"] : $"{h}p");
        AudioBitrates = Steps(BitrateSteps, _edit.Media.AudioBitrateKbps, k => k == 0 ? loc["presets.audio_bitrate.auto"] : $"{k} kbps");
        AudioChannelChoices = new[] { 0, 1, 2 }.Select(c => new Choice<int>(c, loc[$"presets.audio_channels.{c}"])).ToList();
        SampleRates = Steps(SampleRateSteps, _edit.Media.AudioSampleRate,
            r => r == 0 ? loc["presets.sample_rate.source"] : $"{r / 1000.0:0.##} kHz");
        WavFormats = Enum.GetValues<WavSampleFormat>().Select(v => new Choice<WavSampleFormat>(v, loc[$"presets.wav_format.{v}"])).ToList();
        ArchiveLevels = Enum.GetValues<ArchiveLevel>().Select(v => new Choice<ArchiveLevel>(v, loc[$"presets.archive_level.{v}"])).ToList();
        TraceModes = Enum.GetValues<TracingMode>().Select(v => new Choice<TracingMode>(v, loc[$"presets.trace_mode.{v}"])).ToList();
        // Version names stay as AutoCAD calls them, in every language.
        CadVersions = Enum.GetValues<CadVersion>().Select(v => new Choice<CadVersion>(v,
            v == Core.Presets.CadVersion.SameAsSource ? loc["presets.cad_version.same"] : "AutoCAD " + v.ToString()["AutoCad".Length..])).ToList();
        // A speed set outside the editor (a hand-edited preset file) is offered too, instead of being lost.
        AnimationSpeeds = [.. ImageOptions.AnimationSpeeds.Append(_edit.Image.PlaybackSpeed).Distinct().Order()
            .Select(s => new Choice<double>(s, s == 1 ? loc["presets.animation_speed.original"] : SpeedLabel(s)))];

        _name = string.IsNullOrWhiteSpace(_edit.Name) ? loc.DisplayName(_edit) : _edit.Name;
        _target = Targets.FirstOrDefault(t => t.Value == _edit.TargetFormat) ?? Targets[1];
        _resize = ResizeModes.First(r => r.Value == _edit.Image.Resize);
        _rotation = Rotations.FirstOrDefault(r => r.Value == _edit.Image.Rotation) ?? Rotations[0];
        _flip = Flips.FirstOrDefault(f => f.Value == _edit.Image.Flip) ?? Flips[0];
        _pageSize = PageSizes.First(p => p.Value == _edit.Pdf.PageSize);
        _compressionLevel = CompressionLevels.FirstOrDefault(c => c.Value == _edit.Pdf.Compression) ?? CompressionLevels[0];
        _compressWithGhostscript = _edit.Pdf.CompressWithGhostscript;
        _watermark = _edit.Pdf.Watermark;
        _pageNumbers = _edit.Pdf.PageNumbers;
        _pdfPassword = _edit.Pdf.Password;
        _ocr = _edit.Pdf.Ocr;
        _ocrLanguage = OcrLanguageChoices.FirstOrDefault(l => l.Value == _edit.Pdf.OcrLanguage) ?? OcrLanguageChoices[0];
        _location = Locations.First(l => l.Value == _edit.Output.Location);
        _conflict = Conflicts.First(c => c.Value == _edit.Output.Conflict);
        _tiffCompression = TiffCompressions.First(t => t.Value == _edit.Image.TiffCompression);
        _fitsTiffMode = FitsTiffModes.FirstOrDefault(m => m.Value == _edit.Image.FitsTiff) ?? FitsTiffModes[0];
        _fitsDebayer = _edit.Image.FitsDebayer;
        _mediaQuality = MediaQualities.First(q => q.Value == _edit.Media.Quality);
        _maxHeight = MaxHeights.First(h => h.Value == _edit.Media.MaxHeight);
        _audioBitrate = AudioBitrates.First(b => b.Value == _edit.Media.AudioBitrateKbps);
        _removeAudio = _edit.Media.RemoveAudio;
        _audioChannels = AudioChannelChoices.FirstOrDefault(c => c.Value == _edit.Media.AudioChannels) ?? AudioChannelChoices[0];
        _sampleRate = SampleRates.First(r => r.Value == _edit.Media.AudioSampleRate);
        _wavFormat = WavFormats.First(f => f.Value == _edit.Media.WavFormat);
        _videoCodecValue = _edit.Media.VideoCodec;
        _audioCodecValue = _edit.Media.AudioCodec;
        _hardwareEncoding = _edit.Media.HardwareEncoding;
        _archiveLevel = ArchiveLevels.First(l => l.Value == _edit.Archive.Level);
        _combineIntoOne = _edit.Archive.CombineIntoOne;
        _saveEveryFrame = _edit.Media.SaveEveryFrame;
        _extractMotionPhoto = _edit.Media.ExtractMotionPhoto;
        _livePhotoValue = _edit.Media.LivePhoto;
        _livePhotoStart = _edit.Media.LivePhotoStart;
        _livePhotoLength = _edit.Media.LivePhotoLength;
        _livePhotoCover = _edit.Media.LivePhotoCover;
        _frameInterval = _edit.Media.FrameInterval > 0 ? _edit.Media.FrameInterval : 1;
        _isSlideshow = _edit.Media.Slideshow;
        _slideSeconds = _edit.Media.SlideDuration;
        _crossfade = _edit.Media.Crossfade;
        _traceMode = TraceModes.First(m => m.Value == _edit.Trace.Mode);
        _cadVersion = CadVersions.First(v => v.Value == _edit.Cad.Version);
        _animationSpeed = AnimationSpeeds.First(s => s.Value == _edit.Image.PlaybackSpeed);
        _traceColors = Math.Clamp(_edit.Trace.Colors, TracingOptions.MinColors, TracingOptions.MaxColors);
        _traceDetail = Math.Clamp(_edit.Trace.Detail, 0, 100);
        _quality = _edit.Image.Quality;
        _resizePercent = _edit.Image.ResizePercent;
        _longEdge = _edit.Image.LongEdge;
        _width = _edit.Image.Width;
        _setDpi = _edit.Image.Dpi.HasValue;
        _dpi = _edit.Image.Dpi ?? 300;
        _keepMetadata = _edit.Image.KeepMetadata;
        _keepColorProfile = _edit.Image.KeepColorProfile;
        _grayscale = _edit.Image.Grayscale;
        _removeBackground = _edit.Image.RemoveBackground;
        _backgroundTolerance = _edit.Image.BackgroundTolerance;
        _keepEnclosedBackground = _edit.Image.KeepEnclosedBackground;
        _background = _edit.Image.Background;
        _webpLossless = _edit.Image.WebpLossless;
        _multiPageTiff = _edit.Image.MultiPageTiff;
        _icoSizes = string.Join(", ", _edit.Image.IcoSizes);
        _merge = _edit.Pdf.MergeIntoSingle;
        _marginMm = _edit.Pdf.MarginMm;
        _renderDpi = _edit.Pdf.RenderDpi;
        _pageRange = _edit.Pdf.PageRange;
        _splitPages = _edit.Pdf.SplitPages;
        _combinePages = _edit.Pdf.CombinePages;
        _trimPageMargins = _edit.Pdf.TrimPageMargins;
        _splitTallImage = _edit.Pdf.SplitTallImage;
        _pdfA = _edit.Document.PdfA;
        _leaveOutPictures = _edit.Document.LeaveOutPictures;
        _subfolderName = _edit.Output.SubfolderName;
        _subfolderOnlyForSeveralFiles = _edit.Output.SubfolderOnlyForSeveralFiles;
        _customFolder = _edit.Output.CustomFolder;
        _pattern = _edit.Output.FileNamePattern;
        _keepDates = _edit.Output.KeepDates;
        _zipResult = _edit.Output.ZipResult;
        _replaceOriginal = _edit.Output.ReplaceOriginal;
    }

    public string PresetId => _original.Id;
    public bool IsTool => _original.IsTool;
    public bool CanChangeTarget => !IsTool;

    public IReadOnlyList<Choice<string>> Targets { get; }
    public IReadOnlyList<Choice<ResizeMode>> ResizeModes { get; }

    /// <summary>Turning and mirroring: what the built-in rotate / flip presets do, for any image preset.</summary>
    public IReadOnlyList<Choice<ImageRotation>> Rotations { get; }
    public IReadOnlyList<Choice<ImageFlip>> Flips { get; }
    public IReadOnlyList<Choice<PdfPageSize>> PageSizes { get; }

    /// <summary>Languages Tesseract reads (PdfOptions.OcrLanguage), English first.</summary>
    public IReadOnlyList<Choice<string>> OcrLanguageChoices { get; }

    /// <summary>What "Recognize text" needs: Tesseract, or on a Mac Apple Vision (Tesseract only for other languages).</summary>
    public string OcrHint { get; }

    /// <summary>PDF → PDF: don't compress, or the level (pictures at 72, 150 or 300 dpi).</summary>
    public IReadOnlyList<Choice<PdfCompression>> CompressionLevels { get; }
    public IReadOnlyList<Choice<OutputLocation>> Locations { get; }
    public IReadOnlyList<Choice<ConflictPolicy>> Conflicts { get; }
    public IReadOnlyList<Choice<TiffCompression>> TiffCompressions { get; }

    /// <summary>FITS → TIFF: the data as it is, or stretched (ImageOptions.FitsTiff).</summary>
    public IReadOnlyList<Choice<FitsTiffMode>> FitsTiffModes { get; }
    public IReadOnlyList<Choice<MediaQuality>> MediaQualities { get; }
    public IReadOnlyList<Choice<int>> MaxHeights { get; }
    public IReadOnlyList<Choice<int>> AudioBitrates { get; }
    public IReadOnlyList<Choice<int>> AudioChannelChoices { get; }
    public IReadOnlyList<Choice<int>> SampleRates { get; }
    public IReadOnlyList<Choice<WavSampleFormat>> WavFormats { get; }
    public IReadOnlyList<Choice<ArchiveLevel>> ArchiveLevels { get; }
    public IReadOnlyList<Choice<TracingMode>> TraceModes { get; }

    /// <summary>The AutoCAD version a DWG or DXF is written in (CadOptions.Version, #489).</summary>
    public IReadOnlyList<Choice<CadVersion>> CadVersions { get; }

    /// <summary>Playback speeds of animations, 0.25× to 4× (ImageOptions.AnimationSpeed).</summary>
    public IReadOnlyList<Choice<double>> AnimationSpeeds { get; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(NameError), nameof(IsValid))]
    private string _name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowImage), nameof(ShowTurnForPdf), nameof(ShowQuality), nameof(ShowTiff), nameof(ShowMultiPageTiff), nameof(ShowWebp), nameof(ShowIco),
        nameof(ShowFitsTiff), nameof(ShowFitsDebayer),
        nameof(ShowZipResult), nameof(ShowPdf), nameof(ShowPdfBuild), nameof(ShowDocument), nameof(ShowLeaveOutPictures), nameof(ShowPdfSplit), nameof(ShowPdfCombine),
        nameof(ShowPdfCompression), nameof(ShowPdfCompressionOptions), nameof(ShowOcr), nameof(ShowPageSize), nameof(ShowPdfTablesNote),
        nameof(ShowMedia), nameof(ShowMediaQuality), nameof(ShowMaxHeight), nameof(ShowAudioBitrate), nameof(ShowRemoveAudio),
        nameof(ShowAudioChannels), nameof(ShowWav), nameof(VideoCodecs), nameof(VideoCodecChoice), nameof(ShowVideoCodec), nameof(ShowHardwareEncoding),
        nameof(AudioCodecs), nameof(AudioCodecChoice), nameof(ShowAudioCodec),
        nameof(ShowArchive), nameof(ShowArchiveOptions), nameof(ShowExtractNote),
        nameof(ShowSaveEveryFrame), nameof(ShowAnimationSpeed), nameof(ShowTrace), nameof(ShowCad), nameof(ShowExtractMotionPhoto), nameof(ShowSlideshow),
        nameof(ShowLivePhotoChoice), nameof(LivePhotoKinds), nameof(LivePhotoChoice), nameof(IsLivePhoto), nameof(LivePhotoHint))]
    private Choice<string> _target;

    private LivePhotoKind _livePhotoValue;

    /// <summary>What a video becomes for a JPG or HEIC target: one still frame, or a Live / Motion Photo it can be written as.</summary>
    public IReadOnlyList<Choice<LivePhotoKind>> LivePhotoKinds =>
        [.. Enum.GetValues<LivePhotoKind>()
            .Where(k => k == LivePhotoKind.None || MediaOptions.LivePhotoTargets(k).Contains(TargetId))
            .Select(k => new Choice<LivePhotoKind>(k, _loc[$"presets.live_photo.{k}"]))];

    /// <summary>The chosen kind; one the target can't be written as shows (and is saved) as None.</summary>
    public Choice<LivePhotoKind> LivePhotoChoice
    {
        get => LivePhotoKinds.FirstOrDefault(c => c.Value == _livePhotoValue) ?? LivePhotoKinds[0];
        set
        {
            // The combo box clears its selection while its list is replaced: announcing the value shows it again.
            if (value is null)
            {
                OnPropertyChanged();
                return;
            }
            if (value.Value == _livePhotoValue)
                return;
            _livePhotoValue = value.Value;
            OnPropertyChanged();
            foreach (var name in (string[])[nameof(IsLivePhoto), nameof(LivePhotoHint), nameof(ShowImage), nameof(ShowPdf), nameof(ShowSaveEveryFrame)])
                OnPropertyChanged(name);
        }
    }

    [ObservableProperty] private double _livePhotoStart;
    [ObservableProperty] private double _livePhotoLength;
    [ObservableProperty] private double _livePhotoCover;

    /// <summary>The clip's video codec: H.264, or HEVC for smaller files (MediaOptions.VideoCodec).</summary>
    public IReadOnlyList<Choice<VideoCodec>> LivePhotoCodecs =>
    [
        new(VideoCodec.Default, _loc["presets.video_codec.h264"]),
        new(VideoCodec.Hevc, _loc["presets.video_codec.hevc"]),
    ];

    public Choice<VideoCodec> LivePhotoCodecChoice
    {
        get => LivePhotoCodecs.FirstOrDefault(c => c.Value == _videoCodecValue) ?? LivePhotoCodecs[0];
        set
        {
            if (value is null)
            {
                OnPropertyChanged();
                return;
            }
            if (value.Value == _videoCodecValue)
                return;
            _videoCodecValue = value.Value;
            OnPropertyChanged();
        }
    }

    private VideoCodec _videoCodecValue;
    private AudioCodec _audioCodecValue;

    /// <summary>The video codecs the target can hold (H.264 / VP9, HEVC, AV1); one entry for most targets.</summary>
    public IReadOnlyList<Choice<VideoCodec>> VideoCodecs =>
        [.. MediaCodecs.VideoCodecsFor(TargetId).Select(c => new Choice<VideoCodec>(c, _loc[c switch
        {
            VideoCodec.Hevc => "presets.video_codec.hevc",
            VideoCodec.Av1 => "presets.video_codec.av1",
            _ => TargetId == "webm" ? "presets.video_codec.vp9" : "presets.video_codec.h264",
        }]))];

    /// <summary>The chosen video codec; a codec the target can't hold shows (and is saved) as its usual one.</summary>
    public Choice<VideoCodec> VideoCodecChoice
    {
        get => VideoCodecs.FirstOrDefault(c => c.Value == _videoCodecValue) ?? VideoCodecs[0];
        set
        {
            // The combo box clears its selection while its list is replaced (or the selected item is toggled off):
            // announcing the value shows it again.
            if (value is null)
            {
                OnPropertyChanged();
                return;
            }
            if (value.Value == _videoCodecValue)
                return;
            _videoCodecValue = value.Value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowHardwareEncoding));
        }
    }

    /// <summary>The audio codecs the target can hold (AAC or ALAC for M4A); one entry for most targets.</summary>
    public IReadOnlyList<Choice<AudioCodec>> AudioCodecs =>
        [.. MediaCodecs.AudioCodecsFor(TargetId).Select(c => new Choice<AudioCodec>(c, _loc[c == AudioCodec.Alac ? "presets.audio_codec.alac" : "presets.audio_codec.aac"]))];

    /// <summary>The chosen audio codec; ALAC is lossless, so it has no quality or bitrate.</summary>
    public Choice<AudioCodec> AudioCodecChoice
    {
        get => AudioCodecs.FirstOrDefault(c => c.Value == _audioCodecValue) ?? AudioCodecs[0];
        set
        {
            if (value is null)
            {
                OnPropertyChanged();
                return;
            }
            if (value.Value == _audioCodecValue)
                return;
            _audioCodecValue = value.Value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowMediaQuality));
            OnPropertyChanged(nameof(ShowAudioBitrate));
            OnPropertyChanged(nameof(ShowMedia));
        }
    }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowPercent), nameof(ShowLongEdge), nameof(ShowWidth))]
    private Choice<ResizeMode> _resize;

    [ObservableProperty] private Choice<ImageRotation> _rotation;
    [ObservableProperty] private Choice<ImageFlip> _flip;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowMargin))]
    private Choice<PdfPageSize> _pageSize;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowSubfolder), nameof(ShowCustomFolder))]
    private Choice<OutputLocation> _location;

    [ObservableProperty] private Choice<ConflictPolicy> _conflict;
    [ObservableProperty] private Choice<TiffCompression> _tiffCompression;
    [ObservableProperty] private Choice<FitsTiffMode> _fitsTiffMode;

    /// <summary>FITS → other pictures: debayer a colour camera's raw frame (ImageOptions.FitsDebayer).</summary>
    [ObservableProperty] private bool _fitsDebayer;
    [ObservableProperty] private int _quality;
    [ObservableProperty] private int _resizePercent;
    [ObservableProperty] private int _longEdge;
    [ObservableProperty] private int _width;
    [ObservableProperty] private bool _setDpi;
    [ObservableProperty] private int _dpi;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChangeColorProfile), nameof(ColorProfileChecked))]
    private bool _keepMetadata;

    /// <summary>Keep the ICC colour profile when metadata is removed (ImageOptions.KeepColorProfile).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ColorProfileChecked))]
    private bool _keepColorProfile;

    /// <summary>The colour profile choice only matters while metadata is removed; kept metadata includes it.</summary>
    public bool CanChangeColorProfile => !KeepMetadata;

    /// <summary>What the checkbox shows: always ticked (and disabled) while all metadata is kept.</summary>
    public bool ColorProfileChecked
    {
        get => KeepMetadata || KeepColorProfile;
        set
        {
            if (!KeepMetadata)
                KeepColorProfile = value;
        }
    }
    [ObservableProperty] private bool _grayscale;
    [ObservableProperty] private bool _removeBackground;
    [ObservableProperty] private int _backgroundTolerance;
    [ObservableProperty] private bool _keepEnclosedBackground;
    [ObservableProperty] private string _background;
    [ObservableProperty] private bool _webpLossless;
    [ObservableProperty] private bool _multiPageTiff;
    [ObservableProperty] private string _icoSizes;
    [ObservableProperty] private bool _merge;
    [ObservableProperty] private double _marginMm;
    [ObservableProperty] private int _renderDpi;
    [ObservableProperty] private string _pageRange;
    [ObservableProperty] private bool _splitPages;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowPdfCompressionOptions))]
    private Choice<PdfCompression> _compressionLevel;

    [ObservableProperty] private bool _compressWithGhostscript;

    /// <summary>A PDF target's finishing: watermark text, page numbers, a password to open it (PdfOptions).</summary>
    [ObservableProperty] private string _watermark;
    [ObservableProperty] private bool _pageNumbers;
    [ObservableProperty] private string _pdfPassword;

    /// <summary>AES-256 readers use at most 127 UTF-8 bytes of a password: the box takes no more.</summary>
    internal const int MaxPasswordBytes = 127;

    partial void OnPdfPasswordChanged(string value)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(value) <= MaxPasswordBytes)
            return;
        var bytes = 0;
        var fits = string.Concat(value.EnumerateRunes().TakeWhile(r => (bytes += r.Utf8SequenceLength) <= MaxPasswordBytes));
        PdfPassword = fits;
    }

    /// <summary>Recognize the text of scans and pictures (PdfOptions.Ocr), in <see cref="OcrLanguage"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPdfSplit), nameof(ShowPdfCompression), nameof(ShowPdfCompressionOptions), nameof(ShowPageSize))]
    private bool _ocr;

    /// <summary>
    /// Text recognition makes the PDF itself (Tesseract): one file with each page the size of its scan or picture, so one
    /// file per page, compression (and with it removing metadata), page size and margin don't apply and are hidden.
    /// </summary>
    private bool Recognizes => ShowOcr && Ocr;
    [ObservableProperty] private Choice<string> _ocrLanguage;

    /// <summary>PDF → image: one tall image of all pages.</summary>
    [ObservableProperty] private bool _combinePages;
    [ObservableProperty] private bool _trimPageMargins;
    [ObservableProperty] private bool _splitTallImage;
    [ObservableProperty] private bool _pdfA;

    /// <summary>Markdown: the text only, no picture folder next to it (DocumentOptions.LeaveOutPictures).</summary>
    [ObservableProperty] private bool _leaveOutPictures;
    [ObservableProperty] private string _subfolderName;
    /// <summary>The subfolder only for a file that becomes several (OutputRule.SubfolderOnlyForSeveralFiles).</summary>
    [ObservableProperty] private bool _subfolderOnlyForSeveralFiles;
    [ObservableProperty] private string _customFolder;
    [ObservableProperty] private string _pattern;
    [ObservableProperty] private bool _keepDates;
    /// <summary>Geospatial targets: the result is saved as one ZIP (OutputRule.ZipResult).</summary>
    [ObservableProperty] private bool _zipResult;
    /// <summary>The result takes the original's place, which goes to the Recycle Bin / Trash (OutputRule.ReplaceOriginal).</summary>
    [ObservableProperty] private bool _replaceOriginal;
    [ObservableProperty] private Choice<MediaQuality> _mediaQuality;
    [ObservableProperty] private Choice<int> _maxHeight;
    [ObservableProperty] private Choice<int> _audioBitrate;
    [ObservableProperty] private Choice<int> _audioChannels;
    [ObservableProperty] private Choice<int> _sampleRate;
    [ObservableProperty] private Choice<WavSampleFormat> _wavFormat;
    [ObservableProperty] private bool _removeAudio;

    /// <summary>Encode H.264 / HEVC / AV1 on the graphics chip when one works (MediaOptions.HardwareEncoding).</summary>
    [ObservableProperty] private bool _hardwareEncoding;
    [ObservableProperty] private Choice<ArchiveLevel> _archiveLevel;
    [ObservableProperty] private bool _combineIntoOne;
    [ObservableProperty] private bool _saveEveryFrame;
    [ObservableProperty] private bool _extractMotionPhoto;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(FrameEstimate))]
    private int _frameInterval = 1;

    /// <summary>"Video from images" (MediaOptions.Slideshow): how long each picture shows, and fades between them.</summary>
    private readonly bool _isSlideshow;
    [ObservableProperty] private double _slideSeconds;
    [ObservableProperty] private bool _crossfade;

    /// <summary>How many pictures a minute of 30 fps video gives at the chosen interval, and the limit per video.</summary>
    public string FrameEstimate => _loc.Format("presets.frame_estimate",
        (int)Math.Ceiling(1800.0 / Math.Clamp(FrameInterval, 1, 1000)), MediaOptions.MaxFrames.ToString("N0", System.Globalization.CultureInfo.CurrentCulture));

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowTraceColors))]
    private Choice<TracingMode> _traceMode;

    [ObservableProperty] private int _traceColors;
    [ObservableProperty] private Choice<CadVersion> _cadVersion;
    [ObservableProperty] private int _traceDetail;
    [ObservableProperty] private Choice<double> _animationSpeed;

    // A combo box that loses its selection sets null: every choice keeps its value (see Choices.Restore).
    partial void OnTargetChanged(Choice<string>? oldValue, Choice<string> newValue) => Choices.Restore(oldValue, newValue, v => Target = v);
    partial void OnResizeChanged(Choice<ResizeMode>? oldValue, Choice<ResizeMode> newValue) => Choices.Restore(oldValue, newValue, v => Resize = v);
    partial void OnRotationChanged(Choice<ImageRotation>? oldValue, Choice<ImageRotation> newValue) => Choices.Restore(oldValue, newValue, v => Rotation = v);
    partial void OnFlipChanged(Choice<ImageFlip>? oldValue, Choice<ImageFlip> newValue) => Choices.Restore(oldValue, newValue, v => Flip = v);
    partial void OnPageSizeChanged(Choice<PdfPageSize>? oldValue, Choice<PdfPageSize> newValue) => Choices.Restore(oldValue, newValue, v => PageSize = v);
    partial void OnLocationChanged(Choice<OutputLocation>? oldValue, Choice<OutputLocation> newValue) => Choices.Restore(oldValue, newValue, v => Location = v);
    partial void OnConflictChanged(Choice<ConflictPolicy>? oldValue, Choice<ConflictPolicy> newValue) => Choices.Restore(oldValue, newValue, v => Conflict = v);
    partial void OnTiffCompressionChanged(Choice<TiffCompression>? oldValue, Choice<TiffCompression> newValue) =>
        Choices.Restore(oldValue, newValue, v => TiffCompression = v);
    partial void OnFitsTiffModeChanged(Choice<FitsTiffMode>? oldValue, Choice<FitsTiffMode> newValue) =>
        Choices.Restore(oldValue, newValue, v => FitsTiffMode = v);
    partial void OnMediaQualityChanged(Choice<MediaQuality>? oldValue, Choice<MediaQuality> newValue) => Choices.Restore(oldValue, newValue, v => MediaQuality = v);
    partial void OnMaxHeightChanged(Choice<int>? oldValue, Choice<int> newValue) => Choices.Restore(oldValue, newValue, v => MaxHeight = v);
    partial void OnAudioBitrateChanged(Choice<int>? oldValue, Choice<int> newValue) => Choices.Restore(oldValue, newValue, v => AudioBitrate = v);
    partial void OnAudioChannelsChanged(Choice<int>? oldValue, Choice<int> newValue) => Choices.Restore(oldValue, newValue, v => AudioChannels = v);
    partial void OnSampleRateChanged(Choice<int>? oldValue, Choice<int> newValue) => Choices.Restore(oldValue, newValue, v => SampleRate = v);
    partial void OnWavFormatChanged(Choice<WavSampleFormat>? oldValue, Choice<WavSampleFormat> newValue) => Choices.Restore(oldValue, newValue, v => WavFormat = v);
    partial void OnArchiveLevelChanged(Choice<ArchiveLevel>? oldValue, Choice<ArchiveLevel> newValue) => Choices.Restore(oldValue, newValue, v => ArchiveLevel = v);
    partial void OnTraceModeChanged(Choice<TracingMode>? oldValue, Choice<TracingMode> newValue) => Choices.Restore(oldValue, newValue, v => TraceMode = v);
    partial void OnCadVersionChanged(Choice<CadVersion>? oldValue, Choice<CadVersion> newValue) => Choices.Restore(oldValue, newValue, v => CadVersion = v);
    partial void OnCompressionLevelChanged(Choice<PdfCompression>? oldValue, Choice<PdfCompression> newValue) =>
        Choices.Restore(oldValue, newValue, v => CompressionLevel = v);
    partial void OnAnimationSpeedChanged(Choice<double>? oldValue, Choice<double> newValue) =>
        Choices.Restore(oldValue, newValue, v => AnimationSpeed = v);

    private string TargetId => Target.Value;
    private FormatCategory? TargetCategory => FormatRegistry.FindById(TargetId)?.Category;
    private bool TargetIsImage => TargetId == BuiltInData.SameAsSource || TargetCategory == FormatCategory.Image;
    /// <summary>A video target, or "Cut a clip", whose exact cuts are encoded with the video options.</summary>
    private bool TargetIsVideo => TargetCategory == FormatCategory.Video || _original.IsClipTool;
    private bool TargetIsAudio => TargetCategory == FormatCategory.Audio;
    private bool TargetIsLossyMedia => TargetIsVideo
        || (TargetIsAudio && !LosslessAudioTargets.Contains(TargetId) && MediaCodecs.Effective(TargetId, _audioCodecValue) != AudioCodec.Alac);

    /// <summary>A tool (Crop) keeps the source as it is: none of the image options apply.</summary>
    public bool ShowImage => !IsTool && TargetIsImage && !IsLivePhoto;
    /// <summary>JPG and HEIC: a video can become a Live / Motion Photo instead of one still frame.</summary>
    public bool ShowLivePhotoChoice => !IsTool && LivePhotoKinds.Count > 1;
    /// <summary>A Live / Motion Photo is chosen and the target can be written as it: its clip options are shown.</summary>
    public bool IsLivePhoto => ShowLivePhotoChoice && _livePhotoValue != LivePhotoKind.None
        && MediaOptions.LivePhotoTargets(_livePhotoValue).Contains(TargetId);
    /// <summary>What the chosen kind writes and where it is expected to work.</summary>
    public string LivePhotoHint => _loc[_livePhotoValue == LivePhotoKind.Android ? "presets.live_photo_hint.android" : "presets.live_photo_hint.apple"];
    /// <summary>Images and PDFs (pictures placed on pages) can be turned and flipped.</summary>
    private bool CanTurn => TargetIsImage || TargetId == "pdf";
    /// <summary>Rotate and flip for a PDF target, which has no image card to show them in.</summary>
    public bool ShowTurnForPdf => TargetId == "pdf";
    public bool ShowQuality => !IsTool && (TargetId == BuiltInData.SameAsSource || LossyTargets.Contains(TargetId));
    public bool ShowTiff => TargetId is "tiff" or BuiltInData.SameAsSource;
    public bool ShowWebp => TargetId is "webp" or BuiltInData.SameAsSource;
    public bool ShowMultiPageTiff => TargetId is "tiff";
    /// <summary>FITS (astronomy) → TIFF: linear or stretched, at the file's own depth.</summary>
    public bool ShowFitsTiff => !IsTool && TargetId is "tiff";
    /// <summary>FITS → any other picture is stretched; a colour camera's raw frame can be debayered or kept mono.</summary>
    public bool ShowFitsDebayer => !IsTool && TargetCategory == FormatCategory.Image && TargetId != "tiff";
    public bool ShowIco => TargetId == "ico";
    /// <summary>PDF options (also used when a PDF source becomes images) mean nothing for media, archive, font, subtitle, data, e-mail, 3D and geospatial targets.</summary>
    public bool ShowPdf => !IsTool && !IsLivePhoto && TargetCategory is not (FormatCategory.Video or FormatCategory.Audio or FormatCategory.Archive or FormatCategory.Font
        or FormatCategory.Subtitle or FormatCategory.Data or FormatCategory.Model3D or FormatCategory.Geo) && TargetId != "eml";
    /// <summary>"Save as ZIP" is offered for geospatial targets, whose results are several files (a Shapefile) or travel zipped.</summary>
    public bool ShowZipResult => TargetCategory == FormatCategory.Geo;
    public bool ShowPdfBuild => TargetId == "pdf";

    /// <summary>Page size and margin of a PDF built from pictures; not with text recognition (<see cref="Recognizes"/>).</summary>
    public bool ShowPageSize => ShowPdfBuild && !Recognizes;
    public bool ShowPdfSplit => TargetId == "pdf" && !Recognizes;

    /// <summary>Compression is for PDF → PDF; Ghostscript and metadata only matter while it is on.</summary>
    public bool ShowPdfCompression => TargetId == "pdf" && !Recognizes;

    /// <summary>XLSX: what a PDF becomes (its ruled tables, PdfTablesConverter).</summary>
    public bool ShowPdfTablesNote => TargetId == "xlsx";
    public bool ShowPdfCompressionOptions => ShowPdfCompression && CompressionLevel.Value != PdfCompression.None;

    /// <summary>Text from scans goes into the formats Filee writes from a PDF's text itself (PdfOptions.OcrTargets).</summary>
    public bool ShowOcr => !IsTool && PdfOptions.OcrTargets.Contains(TargetId);

    /// <summary>PDF → a single-image format: the pages can be stacked into one tall image (TIFF, GIF, APNG and MNG keep pages).</summary>
    public bool ShowPdfCombine => TargetCategory == FormatCategory.Image
        && TargetId is not ("tiff" or "gif" or "apng" or "mng" or "ico" or "cur" or "icns" or "svg" or "pdf");
    public bool ShowDocument => TargetId == "pdf";
    /// <summary>Markdown can't hold pictures: they go into a folder next to it, or are left out.</summary>
    public bool ShowLeaveOutPictures => TargetId == "md";
    public bool ShowMedia => ShowMediaQuality || ShowMaxHeight || ShowRemoveAudio || ShowAudioChannels || ShowVideoCodec || ShowAudioCodec;
    /// <summary>Mono / stereo for every audio target but AMR, which is mono by definition.</summary>
    public bool ShowAudioChannels => TargetIsAudio && TargetId != "amr";
    /// <summary>Sample rate and sample format: the PCM of WAV and W64 takes any.</summary>
    public bool ShowWav => TargetId is "wav" or "w64";
    /// <summary>MP4, MKV, MOV and WebM can be written with HEVC or AV1 besides their usual codec.</summary>
    public bool ShowVideoCodec => TargetIsVideo && VideoCodecs.Count > 1;
    /// <summary>Hardware encoding applies to H.264, HEVC and AV1 only (not VP9, MPEG-2, …).</summary>
    public bool ShowHardwareEncoding => TargetIsVideo && MediaCodecs.TakesHardwareEncoding(TargetId, _videoCodecValue);
    /// <summary>M4A / M4B can hold Apple Lossless besides AAC.</summary>
    public bool ShowAudioCodec => AudioCodecs.Count > 1;
    public bool ShowMediaQuality => TargetIsLossyMedia;
    public bool ShowMaxHeight => TargetIsVideo;
    public bool ShowAudioBitrate => TargetIsLossyMedia;
    public bool ShowRemoveAudio => TargetIsVideo;
    public bool ShowArchive => TargetCategory == FormatCategory.Archive;
    public bool ShowArchiveOptions => ShowArchive && TargetId != FormatRegistry.Folder;
    /// <summary>"Extract" has nothing to set; a line says what it does instead.</summary>
    public bool ShowExtractNote => TargetId == FormatRegistry.Folder;
    /// <summary>"Save every frame" of a video: PNG and JPG (format ids, <see cref="MediaOptions.FrameTargets"/>).</summary>
    public bool ShowSaveEveryFrame => !IsTool && !IsLivePhoto && MediaOptions.FrameTargets.Contains(TargetId);
    /// <summary>The slideshow options, for a preset that makes a video of pictures (MP4).</summary>
    public bool ShowSlideshow => _isSlideshow && MediaOptions.JoinTargets.Contains(TargetId) && TargetIsVideo;
    /// <summary>The video of a Live / Motion Photo: MP4 and MOV (<see cref="MediaOptions.MotionPhotoTargets"/>).</summary>
    public bool ShowExtractMotionPhoto => !IsTool && MediaOptions.MotionPhotoTargets.Contains(TargetId);
    /// <summary>
    /// Playback speed: for the formats an animation stays animated in (an animated GIF, WebP, APNG or MNG, or a video
    /// made into a GIF or APNG). Other targets get one frame.
    /// </summary>
    public bool ShowAnimationSpeed => TargetId is "gif" or "webp" or "apng" or "mng" or BuiltInData.SameAsSource;
    /// <summary>
    /// Tracing options: raster images reach SVG (and DXF / DWG through it) only by tracing. For other sources (a
    /// drawing, a CAD file) the options are ignored.
    /// </summary>
    public bool ShowTrace => TargetId is "svg" or "svgz" or "dxf" or "dwg";

    /// <summary>The AutoCAD version, for presets that write DWG or DXF.</summary>
    public bool ShowCad => !IsTool && TargetId is "dxf" or "dwg";
    public bool ShowTraceColors => TraceMode.Value == TracingMode.Color;
    public bool ShowPercent => Resize.Value == ResizeMode.Percent;
    public bool ShowLongEdge => Resize.Value == ResizeMode.LongEdge;
    public bool ShowWidth => Resize.Value == ResizeMode.Width;
    public bool ShowMargin => PageSize.Value != PdfPageSize.FitImage;
    public bool ShowSubfolder => Location.Value == OutputLocation.Subfolder;
    public bool ShowCustomFolder => Location.Value == OutputLocation.CustomFolder;

    public string? NameError => string.IsNullOrWhiteSpace(Name) ? _loc["presets.name_required"] : null;
    public bool IsValid => NameError is null;

    /// <summary>Writes the edits into the original preset. Returns false if the input is invalid.</summary>
    public bool Apply()
    {
        if (!IsValid)
            return false;

        // Keep a built-in preset's translatable name unless the user actually renamed it.
        if (_original.NameKey is null || Name.Trim() != _loc.DisplayName(_original))
            _original.Name = Name.Trim();

        _original.TargetFormat = TargetId;
        var image = _original.Image;
        image.Quality = Math.Clamp(Quality, 1, 100);
        image.Resize = Resize.Value;
        // Only images and PDFs are turned; for any other target a rotation left over from an earlier target would make
        // every file fail ("can only turn images") with nothing on screen to turn it off.
        image.Rotation = CanTurn ? Rotation.Value : ImageRotation.None;
        image.Flip = CanTurn ? Flip.Value : ImageFlip.None;
        image.ResizePercent = Math.Clamp(ResizePercent, 1, 1000);
        image.LongEdge = Math.Clamp(LongEdge, 16, 20000);
        image.Width = Math.Clamp(Width, 16, 20000);
        image.Dpi = SetDpi ? Math.Clamp(Dpi, 36, 2400) : null;
        image.KeepMetadata = KeepMetadata;
        image.KeepColorProfile = KeepColorProfile;
        image.Grayscale = Grayscale;
        image.RemoveBackground = RemoveBackground;
        image.BackgroundTolerance = Math.Clamp(BackgroundTolerance, 0, 100);
        image.KeepEnclosedBackground = KeepEnclosedBackground;
        image.Background = string.IsNullOrWhiteSpace(Background) ? "#FFFFFF" : Background.Trim();
        image.TiffCompression = TiffCompression.Value;
        image.WebpLossless = WebpLossless;
        image.MultiPageTiff = MultiPageTiff;
        image.FitsTiff = FitsTiffMode.Value;
        image.FitsDebayer = FitsDebayer;
        image.IcoSizes = IcoSizes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var v) ? v : 0)
            .Where(v => v is > 0 and <= 256)
            .Distinct()
            .ToList();
        if (image.IcoSizes.Count == 0)
            image.IcoSizes = [256, 48, 32, 16];
        // Only where the option is shown: a speed chosen for GIF and left behind by a JPG target saves nothing.
        image.AnimationSpeed = ShowAnimationSpeed ? AnimationSpeed.Value : 1;

        var pdf = _original.Pdf;
        pdf.MergeIntoSingle = Merge;
        pdf.PageSize = PageSize.Value;
        pdf.MarginMm = Math.Clamp(MarginMm, 0, 100);
        pdf.RenderDpi = Math.Clamp(RenderDpi, 36, 1200);
        pdf.PageRange = PageRange.Trim();
        pdf.SplitPages = SplitPages;
        pdf.CombinePages = CombinePages;
        pdf.TrimPageMargins = TrimPageMargins;
        pdf.SplitTallImage = SplitTallImage;
        pdf.Compression = CompressionLevel.Value;
        pdf.CompressWithGhostscript = CompressWithGhostscript;
        // Only for a PDF target, where they are shown: a password left behind by it must not lock nothing.
        pdf.Watermark = ShowPdfBuild ? Watermark.Trim() : "";
        pdf.PageNumbers = ShowPdfBuild && PageNumbers;
        // PDF/A can't be encrypted, so the box is off while it is ticked.
        pdf.Password = ShowPdfBuild && !PdfA ? PdfPassword : "";
        pdf.PasswordLeftOut = false;
        // Only where the option is shown, as with SaveEveryFrame below.
        pdf.Ocr = Recognizes;
        pdf.OcrLanguage = OcrLanguage.Value;
        if (pdf.Ocr)
        {
            // Hidden while text is recognized, so nothing is kept behind it that the PDF wouldn't do (Recognizes).
            pdf.SplitPages = false;
            pdf.Compression = PdfCompression.None;
            pdf.CompressWithGhostscript = false;
            pdf.PageSize = PdfPageSize.FitImage;
            pdf.MarginMm = 0;
        }
        _original.Document.PdfA = PdfA;
        // Only where the option is shown, as with SaveEveryFrame below.
        _original.Document.LeaveOutPictures = ShowLeaveOutPictures && LeaveOutPictures;

        var media = _original.Media;
        media.Quality = MediaQuality.Value;
        media.MaxHeight = MaxHeight.Value;
        media.AudioBitrateKbps = AudioBitrate.Value;
        media.RemoveAudio = RemoveAudio;
        media.AudioChannels = AudioChannels.Value;
        media.AudioSampleRate = SampleRate.Value;
        media.WavFormat = WavFormat.Value;
        // A Live Photo's clip is a MOV (or an MP4 in a Motion Photo): H.264 or HEVC, whatever the photo's format.
        media.VideoCodec = IsLivePhoto
            ? (_videoCodecValue == VideoCodec.Hevc ? VideoCodec.Hevc : VideoCodec.Default)
            : MediaCodecs.Effective(TargetId, _videoCodecValue);
        media.AudioCodec = MediaCodecs.Effective(TargetId, _audioCodecValue);
        media.HardwareEncoding = HardwareEncoding;
        // Only where the option is shown: a box ticked for PNG and left behind by another target saves nothing.
        media.SaveEveryFrame = ShowSaveEveryFrame && SaveEveryFrame;
        media.FrameInterval = media.SaveEveryFrame ? Math.Clamp(FrameInterval, 1, 1000) : 1;
        media.ExtractMotionPhoto = ShowExtractMotionPhoto && ExtractMotionPhoto;
        if (ShowSlideshow)
        {
            media.SlideSeconds = double.IsFinite(SlideSeconds) ? Math.Clamp(SlideSeconds, MediaOptions.MinSlideSeconds, MediaOptions.MaxSlideSeconds) : 3;
            media.Crossfade = Crossfade;
        }
        media.LivePhoto = IsLivePhoto ? _livePhotoValue : LivePhotoKind.None;
        static double Seconds(double value, double fallback, double min, double max) =>
            double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
        media.LivePhotoStart = Seconds(LivePhotoStart, 0, 0, 24 * 3600);
        media.LivePhotoLength = Seconds(LivePhotoLength, 3, MediaOptions.MinLivePhotoLength, MediaOptions.MaxLivePhotoLength);
        media.LivePhotoCover = Seconds(LivePhotoCover, 1.5, 0, MediaOptions.MaxLivePhotoLength);
        _original.Archive.Level = ArchiveLevel.Value;
        _original.Archive.CombineIntoOne = CombineIntoOne;
        _original.Trace.Mode = TraceMode.Value;
        _original.Trace.Colors = Math.Clamp(TraceColors, TracingOptions.MinColors, TracingOptions.MaxColors);
        _original.Trace.Detail = Math.Clamp(TraceDetail, 0, 100);
        // Only where it is shown: a version chosen for DWG and left behind by a PNG target is not kept.
        _original.Cad.Version = ShowCad ? CadVersion.Value : default;

        var output = _original.Output;
        output.Location = Location.Value;
        output.SubfolderName = string.IsNullOrWhiteSpace(SubfolderName) ? "converted" : SubfolderName.Trim();
        // Only with a subfolder, where it is offered: a box ticked there must not stay on hidden behind another location.
        output.SubfolderOnlyForSeveralFiles = ShowSubfolder && SubfolderOnlyForSeveralFiles;
        output.CustomFolder = CustomFolder.Trim();
        output.FileNamePattern = string.IsNullOrWhiteSpace(Pattern) ? "{name}" : Pattern.Trim();
        output.Conflict = Conflict.Value;
        output.KeepDates = KeepDates;
        // Only for geospatial targets, where it is offered: a box ticked for Shapefile must not zip a later PNG.
        output.ZipResult = ShowZipResult && ZipResult;
        output.ReplaceOriginal = ReplaceOriginal;
        return true;
    }

    /// <summary>Playback multipliers using the user's regional decimal separator.</summary>
    private static string SpeedLabel(double speed) => speed.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture) + "×";

    /// <summary>Fixed choices plus the preset's own value if it isn't one of them (e.g. edited by hand).</summary>
    private static List<Choice<int>> Steps(int[] steps, int current, Func<int, string> label) =>
        [.. steps.Append(current).Distinct().Select(v => new Choice<int>(v, label(v)))];
}
