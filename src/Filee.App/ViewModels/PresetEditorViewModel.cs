// Editable view of one Preset. Shared by the Presets page and the preset popup (right-click / ✎ on the donut),
// so both always show exactly the same editor.
//
// Works on a clone; Apply() copies the edits back into the original preset (Cancel = just drop the VM).

using CommunityToolkit.Mvvm.ComponentModel;
using Filee.Core.Formats;
using Filee.Core.Localization;
using Filee.Core.Presets;

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
        Locations = Enum.GetValues<OutputLocation>().Select(v => new Choice<OutputLocation>(v, loc[$"presets.location.{v}"])).ToList();
        Conflicts = Enum.GetValues<ConflictPolicy>().Select(v => new Choice<ConflictPolicy>(v, loc[$"presets.conflict.{v}"])).ToList();
        TiffCompressions = Enum.GetValues<TiffCompression>().Select(v => new Choice<TiffCompression>(v, v.ToString().ToUpperInvariant())).ToList();
        MediaQualities = Enum.GetValues<MediaQuality>().Select(v => new Choice<MediaQuality>(v, loc[$"presets.media_quality.{v}"])).ToList();
        MaxHeights = Steps(MaxHeightSteps, _edit.Media.MaxHeight, h => h == 0 ? loc["presets.max_height.original"] : $"{h}p");
        AudioBitrates = Steps(BitrateSteps, _edit.Media.AudioBitrateKbps, k => k == 0 ? loc["presets.audio_bitrate.auto"] : $"{k} kbps");
        AudioChannelChoices = new[] { 0, 1, 2 }.Select(c => new Choice<int>(c, loc[$"presets.audio_channels.{c}"])).ToList();
        SampleRates = Steps(SampleRateSteps, _edit.Media.AudioSampleRate,
            r => r == 0 ? loc["presets.sample_rate.source"] : $"{r / 1000.0:0.##} kHz");
        WavFormats = Enum.GetValues<WavSampleFormat>().Select(v => new Choice<WavSampleFormat>(v, loc[$"presets.wav_format.{v}"])).ToList();
        ArchiveLevels = Enum.GetValues<ArchiveLevel>().Select(v => new Choice<ArchiveLevel>(v, loc[$"presets.archive_level.{v}"])).ToList();
        TraceModes = Enum.GetValues<TracingMode>().Select(v => new Choice<TracingMode>(v, loc[$"presets.trace_mode.{v}"])).ToList();

        _name = string.IsNullOrWhiteSpace(_edit.Name) ? loc.DisplayName(_edit) : _edit.Name;
        _target = Targets.FirstOrDefault(t => t.Value == _edit.TargetFormat) ?? Targets[1];
        _resize = ResizeModes.First(r => r.Value == _edit.Image.Resize);
        _rotation = Rotations.FirstOrDefault(r => r.Value == _edit.Image.Rotation) ?? Rotations[0];
        _flip = Flips.FirstOrDefault(f => f.Value == _edit.Image.Flip) ?? Flips[0];
        _pageSize = PageSizes.First(p => p.Value == _edit.Pdf.PageSize);
        _location = Locations.First(l => l.Value == _edit.Output.Location);
        _conflict = Conflicts.First(c => c.Value == _edit.Output.Conflict);
        _tiffCompression = TiffCompressions.First(t => t.Value == _edit.Image.TiffCompression);
        _mediaQuality = MediaQualities.First(q => q.Value == _edit.Media.Quality);
        _maxHeight = MaxHeights.First(h => h.Value == _edit.Media.MaxHeight);
        _audioBitrate = AudioBitrates.First(b => b.Value == _edit.Media.AudioBitrateKbps);
        _removeAudio = _edit.Media.RemoveAudio;
        _audioChannels = AudioChannelChoices.FirstOrDefault(c => c.Value == _edit.Media.AudioChannels) ?? AudioChannelChoices[0];
        _sampleRate = SampleRates.First(r => r.Value == _edit.Media.AudioSampleRate);
        _wavFormat = WavFormats.First(f => f.Value == _edit.Media.WavFormat);
        _videoCodecValue = _edit.Media.VideoCodec;
        _audioCodecValue = _edit.Media.AudioCodec;
        _archiveLevel = ArchiveLevels.First(l => l.Value == _edit.Archive.Level);
        _combineIntoOne = _edit.Archive.CombineIntoOne;
        _traceMode = TraceModes.First(m => m.Value == _edit.Trace.Mode);
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
        _subfolderName = _edit.Output.SubfolderName;
        _customFolder = _edit.Output.CustomFolder;
        _pattern = _edit.Output.FileNamePattern;
        _keepDates = _edit.Output.KeepDates;
    }

    public string PresetId => _original.Id;

    public IReadOnlyList<Choice<string>> Targets { get; }
    public IReadOnlyList<Choice<ResizeMode>> ResizeModes { get; }

    /// <summary>Turning and mirroring: what the built-in rotate / flip presets do, for any image preset.</summary>
    public IReadOnlyList<Choice<ImageRotation>> Rotations { get; }
    public IReadOnlyList<Choice<ImageFlip>> Flips { get; }
    public IReadOnlyList<Choice<PdfPageSize>> PageSizes { get; }
    public IReadOnlyList<Choice<OutputLocation>> Locations { get; }
    public IReadOnlyList<Choice<ConflictPolicy>> Conflicts { get; }
    public IReadOnlyList<Choice<TiffCompression>> TiffCompressions { get; }
    public IReadOnlyList<Choice<MediaQuality>> MediaQualities { get; }
    public IReadOnlyList<Choice<int>> MaxHeights { get; }
    public IReadOnlyList<Choice<int>> AudioBitrates { get; }
    public IReadOnlyList<Choice<int>> AudioChannelChoices { get; }
    public IReadOnlyList<Choice<int>> SampleRates { get; }
    public IReadOnlyList<Choice<WavSampleFormat>> WavFormats { get; }
    public IReadOnlyList<Choice<ArchiveLevel>> ArchiveLevels { get; }
    public IReadOnlyList<Choice<TracingMode>> TraceModes { get; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(NameError), nameof(IsValid))]
    private string _name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowImage), nameof(ShowTurnForPdf), nameof(ShowQuality), nameof(ShowTiff), nameof(ShowMultiPageTiff), nameof(ShowWebp), nameof(ShowIco),
        nameof(ShowPdf), nameof(ShowPdfBuild), nameof(ShowDocument), nameof(ShowPdfSplit), nameof(ShowPdfCombine),
        nameof(ShowMedia), nameof(ShowMediaQuality), nameof(ShowMaxHeight), nameof(ShowAudioBitrate), nameof(ShowRemoveAudio),
        nameof(ShowAudioChannels), nameof(ShowWav), nameof(VideoCodecs), nameof(VideoCodecChoice), nameof(ShowVideoCodec),
        nameof(AudioCodecs), nameof(AudioCodecChoice), nameof(ShowAudioCodec),
        nameof(ShowArchive), nameof(ShowArchiveOptions), nameof(ShowExtractNote), nameof(ShowTrace))]
    private Choice<string> _target;

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
            // The combo box clears its selection while its list is replaced.
            if (value is null || value.Value == _videoCodecValue)
                return;
            _videoCodecValue = value.Value;
            OnPropertyChanged();
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
            if (value is null || value.Value == _audioCodecValue)
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

    /// <summary>PDF → image: one tall image of all pages.</summary>
    [ObservableProperty] private bool _combinePages;
    [ObservableProperty] private bool _trimPageMargins;
    [ObservableProperty] private bool _splitTallImage;
    [ObservableProperty] private bool _pdfA;
    [ObservableProperty] private string _subfolderName;
    [ObservableProperty] private string _customFolder;
    [ObservableProperty] private string _pattern;
    [ObservableProperty] private bool _keepDates;
    [ObservableProperty] private Choice<MediaQuality> _mediaQuality;
    [ObservableProperty] private Choice<int> _maxHeight;
    [ObservableProperty] private Choice<int> _audioBitrate;
    [ObservableProperty] private Choice<int> _audioChannels;
    [ObservableProperty] private Choice<int> _sampleRate;
    [ObservableProperty] private Choice<WavSampleFormat> _wavFormat;
    [ObservableProperty] private bool _removeAudio;
    [ObservableProperty] private Choice<ArchiveLevel> _archiveLevel;
    [ObservableProperty] private bool _combineIntoOne;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowTraceColors))]
    private Choice<TracingMode> _traceMode;

    [ObservableProperty] private int _traceColors;
    [ObservableProperty] private int _traceDetail;

    private string TargetId => Target.Value;
    private FormatCategory? TargetCategory => FormatRegistry.FindById(TargetId)?.Category;
    private bool TargetIsImage => TargetId == BuiltInData.SameAsSource || TargetCategory == FormatCategory.Image;
    private bool TargetIsVideo => TargetCategory == FormatCategory.Video;
    private bool TargetIsAudio => TargetCategory == FormatCategory.Audio;
    private bool TargetIsLossyMedia => TargetIsVideo
        || (TargetIsAudio && !LosslessAudioTargets.Contains(TargetId) && MediaCodecs.Effective(TargetId, _audioCodecValue) != AudioCodec.Alac);

    public bool ShowImage => TargetIsImage;
    /// <summary>Images and PDFs (pictures placed on pages) can be turned and flipped.</summary>
    private bool CanTurn => TargetIsImage || TargetId == "pdf";
    /// <summary>Rotate and flip for a PDF target, which has no image card to show them in.</summary>
    public bool ShowTurnForPdf => TargetId == "pdf";
    public bool ShowQuality => TargetId == BuiltInData.SameAsSource || LossyTargets.Contains(TargetId);
    public bool ShowTiff => TargetId is "tiff" or BuiltInData.SameAsSource;
    public bool ShowWebp => TargetId is "webp" or BuiltInData.SameAsSource;
    public bool ShowMultiPageTiff => TargetId is "tiff";
    public bool ShowIco => TargetId == "ico";
    /// <summary>PDF options (also used when a PDF source becomes images) mean nothing for media, archive, font, subtitle, data, e-mail and 3D targets.</summary>
    public bool ShowPdf => TargetCategory is not (FormatCategory.Video or FormatCategory.Audio or FormatCategory.Archive or FormatCategory.Font
        or FormatCategory.Subtitle or FormatCategory.Data or FormatCategory.Model3D) && TargetId != "eml";
    public bool ShowPdfBuild => TargetId == "pdf";
    public bool ShowPdfSplit => TargetId == "pdf";

    /// <summary>PDF → a single-image format: the pages can be stacked into one tall image (TIFF, GIF, APNG and MNG keep pages).</summary>
    public bool ShowPdfCombine => TargetCategory == FormatCategory.Image
        && TargetId is not ("tiff" or "gif" or "apng" or "mng" or "ico" or "cur" or "icns" or "svg" or "pdf");
    public bool ShowDocument => TargetId == "pdf";
    public bool ShowMedia => ShowMediaQuality || ShowMaxHeight || ShowRemoveAudio || ShowAudioChannels || ShowVideoCodec || ShowAudioCodec;
    /// <summary>Mono / stereo for every audio target but AMR, which is mono by definition.</summary>
    public bool ShowAudioChannels => TargetIsAudio && TargetId != "amr";
    /// <summary>Sample rate and sample format: the PCM of WAV and W64 takes any.</summary>
    public bool ShowWav => TargetId is "wav" or "w64";
    /// <summary>MP4, MKV, MOV and WebM can be written with HEVC or AV1 besides their usual codec.</summary>
    public bool ShowVideoCodec => TargetIsVideo && VideoCodecs.Count > 1;
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
    /// <summary>
    /// Tracing options: raster images reach SVG (and DXF / DWG through it) only by tracing. For other sources (a
    /// drawing, a CAD file) the options are ignored.
    /// </summary>
    public bool ShowTrace => TargetId is "svg" or "svgz" or "dxf" or "dwg";
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
        image.IcoSizes = IcoSizes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var v) ? v : 0)
            .Where(v => v is > 0 and <= 256)
            .Distinct()
            .ToList();
        if (image.IcoSizes.Count == 0)
            image.IcoSizes = [256, 48, 32, 16];

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
        _original.Document.PdfA = PdfA;

        var media = _original.Media;
        media.Quality = MediaQuality.Value;
        media.MaxHeight = MaxHeight.Value;
        media.AudioBitrateKbps = AudioBitrate.Value;
        media.RemoveAudio = RemoveAudio;
        media.AudioChannels = AudioChannels.Value;
        media.AudioSampleRate = SampleRate.Value;
        media.WavFormat = WavFormat.Value;
        media.VideoCodec = MediaCodecs.Effective(TargetId, _videoCodecValue);
        media.AudioCodec = MediaCodecs.Effective(TargetId, _audioCodecValue);
        _original.Archive.Level = ArchiveLevel.Value;
        _original.Archive.CombineIntoOne = CombineIntoOne;
        _original.Trace.Mode = TraceMode.Value;
        _original.Trace.Colors = Math.Clamp(TraceColors, TracingOptions.MinColors, TracingOptions.MaxColors);
        _original.Trace.Detail = Math.Clamp(TraceDetail, 0, 100);

        var output = _original.Output;
        output.Location = Location.Value;
        output.SubfolderName = string.IsNullOrWhiteSpace(SubfolderName) ? "converted" : SubfolderName.Trim();
        output.CustomFolder = CustomFolder.Trim();
        output.FileNamePattern = string.IsNullOrWhiteSpace(Pattern) ? "{name}" : Pattern.Trim();
        output.Conflict = Conflict.Value;
        output.KeepDates = KeepDates;
        return true;
    }

    /// <summary>Fixed choices plus the preset's own value if it isn't one of them (e.g. edited by hand).</summary>
    private static List<Choice<int>> Steps(int[] steps, int current, Func<int, string> label) =>
        [.. steps.Append(current).Distinct().Select(v => new Choice<int>(v, label(v)))];
}
