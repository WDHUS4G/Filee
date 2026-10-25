// Renders the demo animations of the "What's new" slides (src/Filee.App/Assets/WhatsNew/<version>/*.webp) with the
// headless app: real windows, real donut animations, frame by frame, encoded as compact animated WebP by Magick.NET.
// The files it writes are product assets; run it again when the UI they show changes. It is explicit, so a normal test
// run skips it:
//
//   dotnet build tests/Filee.App.Tests
//   tests/Filee.App.Tests/bin/Debug/net10.0/Filee.App.Tests.exe -method "Filee.App.Tests.WhatsNewMedia.*" -explicit only
// Set FILEE_WHATSNEW_LANGUAGE=pt-PT (or pt-BR) for localized recordings; shared English recordings stay unchanged.
//
// Each clip is drawn on a 768 × 432 canvas (16:9, shown at 496 × 279 in the window, sharp up to 150 % scaling) in the
// light theme. The first frame of each clip is what reduced motion shows, so every clip starts on a meaningful picture;
// a clip that marks a better one writes it into slides.json as the slide's "poster".

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Filee.App.Controls;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.ViewModels.Pages;
using Filee.App.Views;
using Filee.App.Views.Pages;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Core.Settings;
using ImageMagick;
using ImageMagick.Drawing;
using static Avalonia.VisualExtensions;

namespace Filee.App.Tests;

[Collection("Motion")]
public class WhatsNewMedia
{
    internal const int CanvasWidth = 768;
    internal const int CanvasHeight = 432;
    private const double Frame = 1 / 25.0;

    /// <summary>The light theme's PageBrush: the canvas behind every clip.</summary>
    private const string Page = "#F6F5F1";

    /// <summary>Where the 2.0.0 slides live in the repository.</summary>
    internal static string Folder(string version) =>
        Path.Combine(RepositoryRoot, "src", "Filee.App", "Assets", "WhatsNew", version);

    internal static string RepositoryRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Filee.slnx")))
                dir = dir.Parent;
            return dir!.FullName;
        }
    }

    [AvaloniaFact(Explicit = true)]
    public void Generate_2_0_0()
    {
        var language = Environment.GetEnvironmentVariable("FILEE_WHATSNEW_LANGUAGE") ?? "en";
        Assert.Contains(language, AppLanguages.All.Select(l => l.Code));
        TestServices.EnsureInitialized(language);
        var folder = language == "en" ? Folder("2.0.0") : Path.Combine(Folder("2.0.0"), language);
        Directory.CreateDirectory(folder);
        var theme = AppHost.Get<ThemeService>();
        // Animations on whatever this machine prefers: every theme applied here says ReduceMotion = false, since
        // ThemeService.Apply reads the setting again.
        Motion.Set(true);
        // The recording's region, independently of the machine that generates it.
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo(language == "en" ? "en-US" : language);
        try
        {
            theme.Apply(new ThemeSettings { ReduceMotion = false, Mode = ThemeMode.Light }, language);
            // browser.webp and flip.webp come from the demo-video generator (branch media/demo-videos: a scripted desktop
            // with a browser and a file manager). These headless versions are written only when asked for by name
            // (FILEE_WHATSNEW_ONLY=browser,flip).
            Write(folder, "browser.webp", BrowserDrop, onRequestOnly: true);
            Write(folder, "flip.webp", DonutFlip, onRequestOnly: true);
            Write(folder, "formats.webp", FormatDonuts);
            Write(folder, "crop.webp", Crop);
            Write(folder, "theme.webp", () => Theme("2.0.0"));
            // This slide intentionally demonstrates several languages, rather than the reader's UI language.
            if (language == "en")
                Write(folder, "languages.webp", () => Languages("2.0.0"));
            Write(folder, "more.webp", () => More("2.0.0"));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = culture;
            theme.Apply(new ThemeSettings(), "en");
            TestServices.EnsureInitialized("en");
        }
    }

    // ───────────────────────── Scenes ─────────────────────────

    /// <summary>
    /// A picture dropped from a browser (#261): the donut for it, "Drop → JPG", then the ring with the download badge
    /// above it while the picture is saved, then the check. The badge shows what JobViewModel would write; the real
    /// drag from a browser can't be made headless.
    /// </summary>
    private static Clip BrowserDrop()
    {
        var clip = new Clip();
        // Where a browser picture stands before it is saved (BrowserDrags: placeholder paths named like the file).
        var (window, vm) = OpenDonut([Path.Combine(Path.GetTempPath(), "Filee", "web", "drop", "1", "sunset.jpg")], clickMode: false);
        var donut = window.DonutControl;
        var badge = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "DownloadBadge");
        var badgeText = (TextBlock)badge.Child!;
        var loc = AppHost.Get<ILocalizer>();
        try
        {
            // Frame 0 (reduced motion): the open donut with "Drop → JPG".
            donut.FinishAnimations();
            Highlight(window, vm, 1);
            clip.Add(Capture(window), 1400);

            donut.BeginProgress();
            badge.Transitions = null;
            const long size = 3_100_000;
            var shown = false;
            for (var t = 0.0; t < 1.6; t += Frame)
            {
                if (t >= 0.3)
                {
                    var part = Math.Min(1, (t - 0.3) / 1.2);
                    var done = (long)(size * part);
                    donut.SetProgress(part / 2); // saving is the first half of a browser file's progress
                    badgeText.Text = ViewModels.DownloadBadge.Format(loc, 2_400_000, new Filee.Core.Conversion.JobTransfer(done, size, 0, 1, done));
                    if (!shown)
                    {
                        (badge.Opacity, badge.RenderTransform, shown) = (1, null, true);
                    }
                }
                Step(donut);
                clip.Add(Capture(window), (int)(Frame * 1000));
            }
            badge.Opacity = 0;
            donut.SetProgress(1);
            Run(clip, window, 0.6);
            donut.CompleteProgress(DonutProgressOutcome.Succeeded, () => { });
            Run(clip, window, 2.5, () => donut.ShownOutcome is not null);
            // The check stays a moment before the clip starts over.
            clip.Hold(1600);
        }
        finally
        {
            window.Close();
        }
        return clip;
    }

    /// <summary>
    /// The donut's back face (#262): the images donut with "Tab ↻ back" in the hub turns over to its back face and,
    /// after a moment, to the front again.
    /// </summary>
    private static Clip DonutFlip()
    {
        var clip = new Clip();
        var (window, vm) = OpenDonut([@"C:\photos\a.jpg", @"C:\photos\b.jpg"], clickMode: true);
        var donut = window.DonutControl;
        try
        {
            donut.FinishAnimations();
            Pump(); // the back face is checked once the front is on screen
            Assert.True(vm.CanFlip);
            Settle(donut);
            // Frame 0: the front with its "Tab ↻ back" hint.
            clip.Add(Capture(window), 1600);
            for (var turn = 0; turn < 2; turn++)
            {
                Assert.True(donut.Flip());
                for (var t = 0.0; t < DonutMenu.FlipDuration + Frame; t += Frame)
                {
                    Step(donut);
                    clip.Add(Capture(window), (int)(Frame * 1000));
                }
                Settle(donut);
                clip.Add(Capture(window), turn == 0 ? 1800 : 900);
            }
        }
        finally
        {
            window.Close();
            if (vm.IsBackFace)
                vm.ToggleFace();
        }
        return clip;
    }

    /// <summary>The donut opening for one new format after another.</summary>
    private static Clip FormatDonuts()
    {
        var clip = new Clip();
        string[][] sets =
        [
            [@"C:\gis\roads.shp"],
            [@"C:\invoices\invoice.ofd"],
            [@"C:\web\article.mhtml"],
            [@"C:\photos\scan.jxr"],
            [@"C:\models\chair.fbx"],
            [@"C:\slides\template.potm"],
        ];
        foreach (var files in sets)
        {
            var (window, vm) = OpenDonut(files, clickMode: true);
            // As someone with the optional engines (LibreOffice) sees it, not greyed out for a missing engine.
            vm.Items = vm.Items.Select(Enabled).ToList();
            try
            {
                var donut = window.DonutControl;
                if (clip.Count == 0)
                {
                    // Frame 0: the first donut open, for reduced motion.
                    donut.FinishAnimations();
                    Highlight(window, vm, 0);
                    clip.Add(Capture(window), 900);
                    donut.SetExternalHighlight(-1);
                }
                donut.PlayOpenAnimation();
                Run(clip, window, 0.4);
                Highlight(window, vm, 0);
                clip.Add(Capture(window), 1100);
            }
            finally
            {
                window.Close();
            }
        }
        return clip;
    }

    /// <summary>
    /// The crop tool (#307): three pictures dragged onto the donut's Crop slice, the crop overlay opening on the first,
    /// A pressed with the pointer over the picture opening the aspect dial around the pointer, 1:1 picked, then "Crop
    /// &amp; next" opening the second picture while the strip marks the first done. Real pointer and key input; the
    /// donut and the canvas animate on the clip's clock (<see cref="CropCanvas.Clock"/>), and the transitions of the
    /// dial, its slices and the strip are played here as the window's styles time them (the render timer, which runs
    /// them in the app, keeps wall-clock time).
    /// </summary>
    private static Clip Crop()
    {
        var clip = new Clip { Quality = 72 };
        var folder = Directory.CreateTempSubdirectory("filee-whatsnew-crop-").FullName;
        var clock = System.Diagnostics.Stopwatch.GetTimestamp();
        CropCanvas.Clock = () => clock;
        // Crop on the donut's front (it ships on the back face, where Tab turns the donut): one drop in a short clip.
        var images = AppHost.Get<UserDataStore>().Profiles.Single(p => p.Id == "images");
        var (front, back) = (images.PresetIds.ToList(), images.BackPresetIds?.ToList());
        images.PresetIds[images.PresetIds.IndexOf("half-size")] = "tool-crop";
        images.BackPresetIds?.Remove("tool-crop");
        CropOverlayWindow? window = null;
        try
        {
            var files = new[] { "harbour.jpg", "meadow.jpg", "dunes.jpg" }.Select((name, i) =>
            {
                var path = Path.Combine(folder, name);
                DrawLandscape(path, i);
                return path;
            }).ToList();
            DropOnCrop(clip, files);

            // The overlay opens on the first picture, which settles in on the stepped clock.
            var preset = BuiltInData.CreatePresets().Single(p => p.Id == "tool-crop");
            var vm = new CropOverlayViewModel(files, preset, AppHost.Get<ILocalizer>(), AppHost.Get<UserDataStore>());
            window = new CropOverlayWindow
            {
                DataContext = vm,
                Width = 889, // 16:9 at the window's least height
                Height = 500,
                Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(Page)),
            };
            window.Show();
            WaitFor(() => vm.PreviewImage is not null && !vm.IsBusy);
            // The styles' transitions off (played below).
            window.Classes.Set("reduced-motion", true);
            Pump();

            // 20 frames a second keep the clip within the slides' budget.
            var scene = new Scene(window, clip) { FrameLength = 1 / 20.0 };
            scene.Steps.Add(dt => clock += (long)(System.Diagnostics.Stopwatch.Frequency * dt));
            PlayCropTransitions(window, vm, scene);

            var canvas = window.CropCanvas;
            var origin = (Avalonia.Vector)canvas.TranslatePoint(default, window)!.Value;
            var image = canvas.CalculateImageBounds().Translate(origin);
            Avalonia.Point Middle(Control control) =>
                control.TranslatePoint(new Avalonia.Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
            Avalonia.Point OnPicture(double x, double y) => new(image.X + image.Width * x, image.Y + image.Height * y);

            scene.Place(OnPicture(0.8, 0.75));
            scene.Hold(0.7);

            // A with the pointer over the picture: the dial opens around the pointer.
            scene.MoveTo(OnPicture(0.6, 0.45), 0.45);
            scene.Hold(0.2);
            scene.Key("A", () => window.KeyPress(Key.A, RawInputModifiers.None, PhysicalKey.A, "a"));
            Assert.True(vm.IsAspectDialOpen);
            scene.Hold(0.45);
            window.KeyRelease(Key.A, RawInputModifiers.None, PhysicalKey.A, "a");
            scene.HideKey();
            var dial = window.AspectDial;
            var square = dial.Segments.ToList().FindIndex(s => s.Mode == "1:1");
            var onSquare = dial.TranslatePoint(DonutGeometry.PointAt(new Avalonia.Point(dial.Bounds.Width / 2, dial.Bounds.Height / 2),
                dial.OuterRadius - 14, DonutGeometry.MidAngle(square, dial.Segments.Count())), window)!.Value; // beside its label
            scene.MoveTo(onSquare, 0.45);
            scene.Hold(0.35);
            // Shown with reduced motion: the dial around the pointer, on 1:1.
            clip.MarkPoster();
            scene.Click();
            Assert.Equal("1:1", vm.SelectedAspect);
            scene.Hold(1.0);

            // Crop & next: the second picture settles in, the strip marks the first one done.
            var primary = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PrimaryButton");
            scene.MoveTo(Middle(primary) + new Avalonia.Vector(-12, 2), 0.55);
            scene.Hold(0.2);
            scene.Press();
            scene.Tick();
            scene.Release();
            WaitFor(() => vm.CurrentIndex == 1 && !vm.IsBusy && vm.PreviewImage is not null);
            Assert.Equal(CropItemState.Done, vm.Queue[0].State);
            scene.Hold(0.4);
            scene.MoveTo(Middle(primary) + new Avalonia.Vector(150, -14), 0.4);
            scene.Hold(1.2);
        }
        finally
        {
            window?.Close();
            CropCanvas.Clock = System.Diagnostics.Stopwatch.GetTimestamp;
            (images.PresetIds, images.BackPresetIds) = (front, back);
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException) { }
        }
        return clip;
    }

    /// <summary>
    /// The pictures dragged with the gesture key: the donut opens around the pointer, which goes onto the Crop slice
    /// and drops there (what RadialWindow does with a drag over it, on the donut's own clock).
    /// </summary>
    private static void DropOnCrop(Clip clip, IReadOnlyList<string> files)
    {
        var (window, vm) = OpenDonut(files, clickMode: false);
        var donut = window.DonutControl;
        try
        {
            var crop = vm.Presets.ToList().FindIndex(p => p.Id == "tool-crop");
            Assert.True(crop >= 0, "Crop isn't on the donut");
            var center = new Avalonia.Point(donut.Bounds.Width / 2, donut.Bounds.Height / 2);
            var inner = donut.OuterRadius * donut.HoleRatio;
            Avalonia.Point InWindow(Avalonia.Point p) => donut.TranslatePoint(p, window)!.Value;

            // Its own frame loop would run on wall-clock time: only its clock, stepped here, moves it.
            var scene = new Scene(window, clip) { FrameLength = 1 / 20.0, RunsRenderTimer = false };
            scene.Steps.Add(dt =>
            {
                var at = window.TranslatePoint(scene.Pointer, donut)!.Value;
                var hit = donut.HitTest(at);
                donut.SetExternalHit(hit, at);
                vm.ShowHint(hit.Kind == DonutHitKind.Segment ? hit.Index : -1, -1, hit.Kind == DonutHitKind.Center);
                KeepFramesComing(donut);
                donut.Advance(dt);
                KeepFramesComing(donut);
            });
            scene.Grab(true); // the files are being dragged
            scene.Place(InWindow(center + new Avalonia.Vector(6, 8)));
            scene.ShowKey("Ctrl", down: true);
            donut.PlayOpenAnimation();
            scene.Hold(0.6);
            scene.MoveTo(InWindow(DonutGeometry.PointAt(center, (inner + donut.OuterRadius) / 2 + 6,
                DonutGeometry.MidAngle(crop, donut.Items!.Count) + 0.05)), 0.6);
            scene.Hold(0.45);
            Assert.Equal(new DonutHit(DonutHitKind.Segment, crop), donut.HitTest(window.TranslatePoint(scene.Pointer, donut)!.Value));
            scene.HideKey();
        }
        finally
        {
            window.Close();
            donut.SetExternalHighlight(-1);
        }
    }

    /// <summary>
    /// What CropOverlayWindow.axaml's transitions do, on the clip's clock: the dial grows out of its button (opacity
    /// 0.16 s, scale 0.55 → 1 in 0.34 s) and shrinks away, a slice under the pointer moves out (0.24 s), and the strip's
    /// tiles grow and shrink as the current file changes (0.32 s).
    /// </summary>
    private static void PlayCropTransitions(CropOverlayWindow window, CropOverlayViewModel vm, Scene scene)
    {
        var dial = window.AspectDial;
        dial.Transitions = null;
        var (opacity, scale) = (new Glide(0, 0.16, t => t), new Glide(0.55, 0.34, BackEaseOut));
        var pops = dial.Segments.ToDictionary(s => s, _ => new Glide(0, 0.24, BackEaseOut));
        foreach (var segment in pops.Keys)
            segment.Transitions = null;
        var tiles = new Dictionary<Border, (Glide Scale, Glide Opacity)>();
        scene.Steps.Add(dt =>
        {
            var open = vm.IsAspectDialOpen;
            dial.Opacity = opacity.Step(open ? 1 : 0, dt);
            var s = scale.Step(open ? 1 : 0.55, dt);
            dial.RenderTransform = new Avalonia.Media.ScaleTransform(s, s);
            dial.IsVisible = open || dial.Opacity > 0.001;
            foreach (var (segment, pop) in pops)
                segment.Pop = pop.Step(segment.IsPointerOver ? 1 : 0, dt);

            foreach (var tile in window.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("tile")))
            {
                if (!tiles.TryGetValue(tile, out var glide))
                    tiles[tile] = glide = (new Glide(TileScale(tile), 0.32, BackEaseOut), new Glide(TileOpacity(tile), 0.2, t => t));
                var size = glide.Scale.Step(TileScale(tile), dt);
                tile.RenderTransform = new Avalonia.Media.ScaleTransform(size, size);
                tile.Opacity = glide.Opacity.Step(TileOpacity(tile), dt);
            }
        });

        static double TileScale(Border tile) => tile.Classes.Contains("current") ? 1.08 : 0.86;
        static double TileOpacity(Border tile) =>
            tile.Classes.Contains("current") ? 1 : tile.Classes.Contains("skipped") || tile.Classes.Contains("failed") ? 0.55 : 0.9;
    }

    /// <summary>
    /// The Theme page: an accent swatch clicked (the whole window takes the colour), a colour picked on the spectrum of
    /// the colour picker (#291), Softness rounding the preview donut, and the donut toolbar's opacity (#275) letting the
    /// backdrop through. Swatch and spectrum get real pointer input; the sliders are moved through the page's settings
    /// with the pointer on their thumbs.
    /// </summary>
    private static Clip Theme(string version)
    {
        // 15 frames a second at a lower quality: every change of colour redraws the whole window.
        var clip = new Clip { Quality = AppHost.Get<ILocalizer>().Language == "en" ? 72U : 70U };
        var store = AppHost.Get<UserDataStore>();
        var theme = AppHost.Get<ThemeService>();
        var (themeBefore, opacityBefore) = (store.Settings.Theme, store.Settings.Donut.Opacity);
        store.Settings.Theme = new ThemeSettings { ReduceMotion = false, Mode = ThemeMode.Light };
        store.Settings.Donut.Opacity = 1;
        theme.Apply(store.Settings.Theme, AppHost.Get<ILocalizer>().Language);
        var vm = new MainWindowViewModel(AppHost.Services, AppHost.Get<ILocalizer>(), AppHost.Get<UpdateService>());
        var window = new MainWindow { DataContext = vm, Width = 1080, Height = 608 };
        try
        {
            vm.Navigate("theme");
            window.Show();
            Pump();
            var page = (ThemePageViewModel)vm.CurrentPage!;
            var view = window.GetVisualDescendants().OfType<ThemePage>().Single();
            var softness = view.GetVisualDescendants().OfType<Slider>().Single(s => s.Name != "DonutOpacitySlider");
            var opacity = view.FindControl<Slider>("DonutOpacitySlider")!;

            // Scrolled, if the window is too short for it, so the opacity card shows whole.
            var scroller = window.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "PageScroller");
            var card = opacity.FindAncestorOfType<Border>()!;
            while (!card.Classes.Contains("card"))
                card = card.FindAncestorOfType<Border>()!;
            var cardBottom = card.TranslatePoint(new Avalonia.Point(0, card.Bounds.Height), scroller)!.Value.Y;
            scroller.Offset = new Avalonia.Vector(0, Math.Max(0, cardBottom + 18 - scroller.Bounds.Height));
            Pump();

            var scene = new Scene(window, clip, version) { FrameLength = 1 / 15.0 };
            (string?, double) applied = (store.Settings.Theme.Accent, store.Settings.Theme.CornerRadius);
            // The app applies the theme when the settings change (App.ApplySettings); here the clip does.
            scene.Steps.Add(_ =>
            {
                var now = (store.Settings.Theme.Accent, store.Settings.Theme.CornerRadius);
                if (now != applied)
                {
                    applied = now;
                    theme.Apply(store.Settings.Theme, AppHost.Get<ILocalizer>().Language);
                }
            });
            Avalonia.Point Middle(Control control) =>
                control.TranslatePoint(new Avalonia.Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
            Avalonia.Point Thumb(Slider slider) =>
                Middle(slider.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Thumb>().Single()) + new Avalonia.Vector(3, 5);

            // Frame 0: the page as it opens.
            var preview = view.GetVisualDescendants().OfType<DonutMenu>().Single();
            scene.Place(Middle(preview) + new Avalonia.Vector(150, 40));
            scene.Hold(0.9);

            // A swatch: the window takes its colour.
            var swatches = view.FindControl<ListBox>("AccentSwatches")!;
            scene.MoveTo(Middle(swatches.ContainerFromIndex(1)!) + new Avalonia.Vector(4, 6), 0.5);
            scene.Click();
            scene.Hold(0.8);

            // Any colour: a click on the spectrum under the picker.
            var picker = view.FindControl<ColorPicker>("AccentPicker")!;
            var dropDown = picker.GetVisualDescendants().OfType<Button>().First();
            scene.MoveTo(Middle(dropDown) + new Avalonia.Vector(4, 6), 0.45);
            scene.Click();
            scene.Hold(0.3);
            var spectrum = ((Control)((Flyout)dropDown.Flyout!).Content!).GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ColorSpectrum>().Single();
            scene.MoveTo(spectrum.TranslatePoint(new Avalonia.Point(spectrum.Bounds.Width * 0.6, spectrum.Bounds.Height * 0.3), window)!.Value, 0.45);
            scene.Click();
            scene.Hold(0.8);
            clip.MarkPoster();
            // A click beside it closes it.
            scene.MoveTo(Middle(preview) + new Avalonia.Vector(0, 205), 0.45);
            scene.Click();
            scene.Hold(0.2);

            // Softness: the preview donut's slices round off and sharpen.
            scene.MoveTo(Thumb(softness), 0.45);
            scene.Grab(true);
            foreach (var (to, seconds) in new[] { (28.0, 0.35), (2.0, 0.7), (20.0, 0.45) })
                Slide(scene, page.CornerRadius, to, seconds, v => page.CornerRadius = Math.Round(v), () => Thumb(softness));
            scene.Grab(false);
            scene.Hold(0.3);

            // The donut toolbar's opacity: the backdrop shows through the slices.
            scene.MoveTo(Thumb(opacity), 0.4);
            scene.Grab(true);
            Slide(scene, page.DonutOpacity, 45, 0.8, v => page.DonutOpacity = Math.Round(v), () => Thumb(opacity));
            scene.Grab(false);
            scene.Hold(1.4);
        }
        finally
        {
            window.Close();
            vm.Dispose();
            (store.Settings.Theme, store.Settings.Donut.Opacity) = (themeBefore, opacityBefore);
            theme.Apply(new ThemeSettings { ReduceMotion = false, Mode = ThemeMode.Light }, AppHost.Get<ILocalizer>().Language);
        }
        return clip;

        static void Slide(Scene scene, double from, double to, double seconds, Action<double> set, Func<Avalonia.Point> thumb)
        {
            var frames = Math.Max(1, (int)Math.Round(seconds / scene.FrameLength));
            for (var i = 1; i <= frames; i++)
            {
                set(from + (to - from) * Scene.EaseInOut(i / (double)frames));
                scene.Tick(thumb);
            }
        }
    }

    /// <summary>The Home page in one language after another.</summary>
    private static Clip Languages(string version)
    {
        var clip = new Clip();
        foreach (var language in AppLanguages.All.Select(l => l.Code))
        {
            TestServices.EnsureInitialized(language);
            AppHost.Get<ThemeService>().Apply(new ThemeSettings { ReduceMotion = false, Mode = ThemeMode.Light }, language);
            var vm = new MainWindowViewModel(AppHost.Services, AppHost.Get<ILocalizer>(), AppHost.Get<UpdateService>());
            vm.Navigate("home");
            var window = new MainWindow { DataContext = vm, Width = 960, Height = 540 };
            window.Show();
            Pump();
            clip.Add(Capture(window, version), 1100);
            window.Close();
            vm.Dispose();
        }
        TestServices.EnsureInitialized("en");
        return clip;
    }

    /// <summary>
    /// "And more": the history search finding an old conversion as it is typed, then "Reset to default" beside a
    /// built-in donut on the Toolbar page.
    /// </summary>
    private static Clip More(string version)
    {
        var clip = new Clip();
        var store = AppHost.Get<UserDataStore>();
        var loc = AppHost.Get<ILocalizer>();
        var report = loc.Language.StartsWith("pt-", StringComparison.Ordinal) ? "relatório" : "report";
        var work = OperatingSystem.IsWindows() ? @"C:\Work" : OperatingSystem.IsMacOS() ? "/Users/demo/Documents" : "/home/demo/Documents";
        var photos = OperatingSystem.IsWindows() ? @"C:\Photos" : OperatingSystem.IsMacOS() ? "/Users/demo/Pictures" : "/home/demo/Pictures";
        store.ClearHistory();
        store.History.AddRange(Enumerable.Range(0, store.Settings.EffectiveHistoryLimit).Select(i => new Filee.Core.History.HistoryEntry
        {
            FinishedAt = DateTime.Now.AddMinutes(-7 * i - 3),
            PresetName = i == 150 ? "PDF" : i % 3 == 0 ? "JPG" : "PNG",
            State = Filee.Core.Conversion.JobState.Completed,
            Sources = i == 150 ? [Path.Combine(work, $"{report}.docx")] : [Path.Combine(photos, $"IMG_{4000 + i:0000}.heic")],
        }));
        var profiles = store.Profiles.Select(p => p.Clone()).ToList();
        try
        {
            var vm = new MainWindowViewModel(AppHost.Services, loc, AppHost.Get<UpdateService>());
            vm.Navigate("home");
            var window = new MainWindow { DataContext = vm, Width = 960, Height = 540 };
            window.Show();
            Pump();
            var home = (HomePageViewModel)vm.CurrentPage!;
            // Frame 0: the old report found.
            home.Search = report;
            Pump();
            clip.Add(Capture(window, version), 1400);
            home.Search = "";
            Pump();
            clip.Add(Capture(window, version), 700);
            for (var length = 1; length <= report.Length; length++)
            {
                home.Search = report[..length];
                Pump();
                clip.Add(Capture(window, version), 160);
            }
            clip.Add(Capture(window, version), 1400);

            // A tall window shows the whole profile card; the clip shows its lower part, down to "Reset to default".
            vm.Navigate("toolbar");
            window.Width = 1240;
            window.Height = 1700;
            var page = (ToolbarPageViewModel)vm.CurrentPage!;
            page.SelectedProfile = page.Profiles.Single(p => p.Profile.Id == "images");
            Pump();
            var actions = window.GetVisualDescendants().OfType<StackPanel>().Single(s => s.Name == "ProfileActions");
            var card = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "ProfileCard");
            var cardAt = Avalonia.VisualExtensions.TranslatePoint(card, default, window)!.Value;
            var actionsAt = Avalonia.VisualExtensions.TranslatePoint(actions, default, window)!.Value;
            var bottom = actionsAt.Y + actions.Bounds.Height + 28;
            var region = new Avalonia.Rect(Math.Max(0, cardAt.X - 8), Math.Max(0, bottom - 540), 960, 540);
            clip.Add(Capture(window, version, region), 2200);
            window.Close();
            vm.Dispose();
        }
        finally
        {
            store.ClearHistory();
            store.Profiles.Clear();
            store.Profiles.AddRange(profiles);
        }
        return clip;
    }

    // ───────────────────────── Helpers ─────────────────────────

    /// <summary>
    /// A window played frame by frame with a pointer: real pointer input (hover, press, drag) and a picture of the
    /// pointer on every frame. <see cref="Steps"/> run before each frame with its length, for what the clip times itself.
    /// </summary>
    private sealed class Scene(TopLevel window, Clip clip, string? version = null)
    {
        private bool _grabbed;
        private readonly Glide _key = new(0, 0.12, t => t);
        private (string Label, bool Down, bool Shown) _keyCap;

        public Avalonia.Point Pointer { get; private set; }
        public bool Pressed { get; private set; }
        public List<Action<double>> Steps { get; } = [];

        /// <summary>Seconds per frame.</summary>
        public double FrameLength { get; init; } = Frame;

        /// <summary>
        /// False for a window whose animations the clip steps itself (the donut): the render timer would also run its
        /// frame loop, on wall-clock time.
        /// </summary>
        public bool RunsRenderTimer { get; init; } = true;

        /// <summary>Puts the pointer somewhere at once (before the first frame).</summary>
        public void Place(Avalonia.Point at)
        {
            // Tooltips open on a wall-clock delay: they would come and go at random in the clip.
            ToolTip.SetServiceEnabled(window, false);
            Pointer = at;
            window.MouseMove(at);
        }

        /// <summary>One frame; <paramref name="pointer"/>, if given, places the pointer once the frame is laid out.</summary>
        public void Tick(Func<Avalonia.Point>? pointer = null)
        {
            foreach (var step in Steps)
                step(FrameLength);
            // A few rounds are enough: the capture renders the frame itself.
            for (var i = 0; i < 3; i++)
            {
                Dispatcher.UIThread.RunJobs();
                if (RunsRenderTimer)
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            }
            if (pointer is not null)
                Pointer = pointer();
            var frame = Capture(window, version, pointer: Pointer, pressed: Pressed || _grabbed);
            var shown = _key.Step(_keyCap.Shown ? 1 : 0, FrameLength);
            if (shown > 0.01)
                DrawKeyCap(frame, _keyCap.Label, shown, _keyCap.Down);
            clip.Add(frame, (int)Math.Round(FrameLength * 1000));
        }

        /// <summary>A keycap at the bottom left, as the demo videos show the keys held.</summary>
        public void ShowKey(string label, bool down) => _keyCap = (label, down, true);

        public void HideKey() => _keyCap = _keyCap with { Shown = false };

        /// <summary>A key pressed: shown down, and <paramref name="press"/> sends it.</summary>
        public void Key(string label, Action press)
        {
            ShowKey(label, down: true);
            press();
            Tick();
        }

        public void Hold(double seconds)
        {
            for (var i = (int)Math.Round(seconds / FrameLength); i > 0; i--)
                Tick();
        }

        /// <summary>Moves the pointer to <paramref name="to"/> along a slight curve, easing in and out (a drag while pressed).</summary>
        public void MoveTo(Avalonia.Point to, double seconds)
        {
            var from = Pointer;
            var normal = new Avalonia.Vector(from.Y - to.Y, to.X - from.X) * 0.06;
            var frames = Math.Max(1, (int)Math.Round(seconds / FrameLength));
            for (var i = 1; i <= frames; i++)
            {
                var u = EaseInOut(i / (double)frames);
                Pointer = new Avalonia.Point(from.X + (to.X - from.X) * u, from.Y + (to.Y - from.Y) * u) + normal * Math.Sin(Math.PI * u);
                window.MouseMove(Pointer, Pressed ? RawInputModifiers.LeftMouseButton : RawInputModifiers.None);
                Tick();
            }
        }

        public void Press()
        {
            window.MouseDown(Pointer, MouseButton.Left);
            Pressed = true;
        }

        public void Release()
        {
            window.MouseUp(Pointer, MouseButton.Left);
            Pressed = false;
        }

        /// <summary>A click, the button shown pressed for a frame.</summary>
        public void Click()
        {
            Press();
            Tick();
            Release();
        }

        /// <summary>The pointer drawn pressed without input: for a control the clip moves through its settings.</summary>
        public void Grab(bool grabbed) => _grabbed = grabbed;

        public static double EaseInOut(double t) => t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
    }

    /// <summary>A value easing to where the styles put it, as their transition would: a new target starts from where it is.</summary>
    private sealed class Glide(double value, double seconds, Func<double, double> easing)
    {
        private double _from = value, _to = value;
        private double _age = double.PositiveInfinity; // at rest
        private double _value = value;

        public double Step(double target, double dt)
        {
            if (target != _to)
                (_from, _to, _age) = (_value, target, 0);
            _age += dt;
            _value = _from + (_to - _from) * easing(Math.Clamp(_age / seconds, 0, 1));
            return _value;
        }
    }

    /// <summary>Avalonia's BackEaseOut (the SoftEase of Filee's styles).</summary>
    private static double BackEaseOut(double t)
    {
        var p = 1 - t;
        return 1 - p * (p * p - Math.Sin(p * Math.PI));
    }

    private static (RadialWindow Window, RadialViewModel Vm) OpenDonut(IReadOnlyList<string> files, bool clickMode)
    {
        var vm = AppHost.Get<RadialViewModel>();
        if (!clickMode)
            vm.ResetForDrag();
        vm.Load(files, clickMode);
        vm.FitToThisDonut();
        // The headless platform has no transparent windows: the page colour instead of black around the donut.
        var window = new RadialWindow { DataContext = vm, Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(Page)) };
        window.Show();
        Pump();
        return (window, vm);
    }

    private static DonutItem Enabled(DonutItem item) => new()
    {
        Id = item.Id,
        Label = item.Label,
        Caption = item.Caption,
        Children = item.Children?.Select(Enabled).ToList(),
    };

    private static void Highlight(RadialWindow window, RadialViewModel vm, int index)
    {
        window.DonutControl.SetExternalHighlight(index);
        vm.ShowHint(index, -1, centerHot: false);
        Settle(window.DonutControl);
    }

    /// <summary>Lets the donut's springs (highlight, hub) come to rest, and draws it.</summary>
    private static void Settle(DonutMenu donut)
    {
        for (var i = 0; i < 90; i++)
            donut.Advance(1 / 60.0);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>One frame: the donut's clock moves on by <see cref="Frame"/>.</summary>
    private static void Step(DonutMenu donut)
    {
        KeepFramesComing(donut);
        donut.Advance(Frame);
        Dispatcher.UIThread.RunJobs();
        KeepFramesComing(donut);
    }

    /// <summary>Runs the donut's animations <paramref name="seconds"/> (or until <paramref name="until"/>), a frame every <see cref="Frame"/>.</summary>
    private static void Run(Clip clip, RadialWindow window, double seconds, Func<bool>? until = null)
    {
        for (var t = 0.0; t < seconds && until?.Invoke() != true; t += Frame)
        {
            Step(window.DonutControl);
            clip.Add(Capture(window), (int)(Frame * 1000));
        }
    }

    private static void Pump()
    {
        for (var i = 0; i < 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static void WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(20);
        }
        Assert.True(condition());
    }

    /// <summary>
    /// A rendered frame of <paramref name="window"/> centred on the page-coloured canvas, scaled down to fit. With
    /// <paramref name="version"/>, the sidebar shows that version instead of the one being built (the clips are made
    /// before the version is bumped). With <paramref name="pointer"/> (in the window), a mouse pointer is drawn there.
    /// </summary>
    private static MagickImage Capture(TopLevel window, string? version = null, Avalonia.Rect? region = null,
        Avalonia.Point? pointer = null, bool pressed = false)
    {
        if (window is RadialWindow radial)
            KeepFramesComing(radial.DonutControl);
        try
        {
            return CaptureCore(window, version, region, pointer, pressed);
        }
        finally
        {
            if (window is RadialWindow after)
                KeepFramesComing(after.DonutControl);
        }
    }

    private static readonly System.Reflection.FieldInfo LastFrameTick =
        typeof(DonutMenu).GetField("_lastTick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("DonutMenu._lastTick is gone: update WhatsNewMedia.KeepFramesComing");

    /// <summary>
    /// Taking a frame (PNG, compare) takes longer than the donut's stalled-frames watchdog waits (StalledLoopMs), which
    /// would then jump every animation to its end. The clips step the clock themselves, so the watchdog is told that
    /// frames are coming.
    /// </summary>
    private static void KeepFramesComing(DonutMenu donut) => LastFrameTick.SetValue(donut, Environment.TickCount64);

    private static MagickImage CaptureCore(TopLevel window, string? version, Avalonia.Rect? region, Avalonia.Point? pointer, bool pressed)
    {
        if (version is not null)
        {
            foreach (var text in window.GetVisualDescendants().OfType<TextBlock>())
                if (text.Text?.Contains(UpdateService.CurrentVersion, StringComparison.Ordinal) == true)
                    text.Text = text.Text.Replace(UpdateService.CurrentVersion, version, StringComparison.Ordinal);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
        using var png = new MemoryStream();
        if (window is RadialWindow)
        {
            // The donut is drawn straight from its state: the clips step its clock themselves, and the render timer
            // would run its own frame loop as well.
            window.UpdateLayout();
            using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(
                new Avalonia.PixelSize((int)Math.Ceiling(window.Bounds.Width), (int)Math.Ceiling(window.Bounds.Height)));
            bitmap.Render(window);
            bitmap.Save(png, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }
        else
        {
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            frame.Save(png, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }
        png.Position = 0;
        using var shot = new MagickImage(png);
        var corner = new Avalonia.Point(0, 0);
        if (region is { } r)
        {
            var x = (int)Math.Clamp(r.X, 0, Math.Max(0, shot.Width - r.Width));
            var y = (int)Math.Clamp(r.Y, 0, Math.Max(0, shot.Height - r.Height));
            shot.Crop(new MagickGeometry(x, y, (uint)r.Width, (uint)r.Height));
            shot.ResetPage();
            corner = new Avalonia.Point(x, y);
        }
        var canvas = new MagickImage(new MagickColor(Page), CanvasWidth, CanvasHeight);
        var scale = Math.Min(1, Math.Min((double)CanvasWidth / shot.Width, (double)CanvasHeight / shot.Height));
        if (scale < 1)
        {
            shot.FilterType = FilterType.Lanczos;
            shot.Resize((uint)Math.Round(shot.Width * scale), (uint)Math.Round(shot.Height * scale));
        }
        canvas.Composite(shot, Gravity.Center, CompositeOperator.Over);
        if (pointer is { } p)
        {
            var left = (CanvasWidth - (int)shot.Width) / 2;
            var top = (CanvasHeight - (int)shot.Height) / 2;
            DrawPointer(canvas, new PointD(left + (p.X - corner.X) * scale, top + (p.Y - corner.Y) * scale), pressed);
        }
        return canvas;
    }

    /// <summary>A keyboard key at the bottom left of the canvas, <paramref name="opacity"/> 0 to 1, pressed or not.</summary>
    private static void DrawKeyCap(MagickImage canvas, string label, double opacity, bool down)
    {
        var font = Path.Combine(RepositoryRoot, "src", "Filee.App", "Assets", "Fonts", "NotoSans-Bold.ttf");
        var width = Math.Max(46, 22 + 11 * label.Length);
        var (x, y) = (40.0, CanvasHeight - 70.0 + (down ? 2 : 0));
        var alpha = (byte)Math.Round(255 * opacity);
        MagickColor Colour(string hex, double share = 1)
        {
            var c = new MagickColor(hex);
            return new MagickColor(c.R, c.G, c.B, (byte)Math.Round(alpha * share));
        }
        var drawing = new Drawables()
            .StrokeColor(Colour("#000000", 0)).FillColor(Colour("#000000", 0.14)).RoundRectangle(x, y + 4, x + width, y + 38, 9, 9)
            .FillColor(Colour("#FFFFFF")).StrokeColor(Colour(down ? "#7F77DD" : "#D6D3CB")).StrokeWidth(down ? 1.6 : 1)
            .RoundRectangle(x, y, x + width, y + 34, 9, 9);
        if (File.Exists(font))
            drawing.Font(font);
        drawing.FontPointSize(15).StrokeColor(Colour("#000000", 0)).FillColor(Colour("#1F1E1D"))
            .TextAlignment(TextAlignment.Center).Text(x + width / 2.0, y + 23, label)
            .Draw(canvas);
    }

    /// <summary>An arrow pointer with its tip at <paramref name="tip"/>, a little smaller while pressed.</summary>
    private static void DrawPointer(MagickImage canvas, PointD tip, bool pressed)
    {
        var size = pressed ? 1.06 : 1.15;
        PointD[] shape = [new(0, 0), new(0, 17.5), new(4.2, 13.6), new(7.1, 20.3), new(10.1, 19), new(7.3, 12.5), new(12.8, 12.5)];
        PointD[] At(double dx, double dy) => [.. shape.Select(q => new PointD(tip.X + q.X * size + dx, tip.Y + q.Y * size + dy))];
        new Drawables()
            .StrokeLineJoin(LineJoin.Round)
            .FillColor(new MagickColor("#00000038")).StrokeColor(new MagickColor("#0000001F")).StrokeWidth(2.4).Polygon(At(0.6, 1.6))
            .FillColor(MagickColors.White).StrokeColor(new MagickColor("#1F1E1D")).StrokeWidth(1.15).Polygon(At(0, 0))
            .Draw(canvas);
    }

    /// <summary>
    /// A made-up photo to crop (1600 × 1000): a harbour (sky, sun, sea, hills, a sail), a meadow at noon or dunes at
    /// dusk. Generated, so no picture is kept in the repository.
    /// </summary>
    private static void DrawLandscape(string path, int variant = 0)
    {
        var (sky, sun, ground, near, far) = variant switch
        {
            1 => ("gradient:#9FD3F2-#E9F6E4", "#FFF7D6", "#7DB65A", "#4E8F3C", "#A5CC7A"),
            2 => ("gradient:#F6B98A-#FCE3C4", "#FFF1D0", "#D9925A", "#B86E3E", "#E7B07A"),
            _ => ("gradient:#8EC5FC-#F8D2A6", "#FFF2C2", "#4F7CAC", "#3F6E4A", "#6C9A5B"),
        };
        using var image = new MagickImage(sky, 1600, 1000);
        var drawing = new Drawables()
            .FillColor(new MagickColor(sun)).Circle(1080, 360, 1080, 460)
            .FillColor(new MagickColor(ground)).Rectangle(0, 640, 1600, 1000)
            .FillColor(new MagickColor(far)).Polygon(new PointD(0, 700), new PointD(380, 470), new PointD(760, 690), new PointD(0, 760))
            .FillColor(new MagickColor(near)).Polygon(new PointD(600, 700), new PointD(1000, 520), new PointD(1600, 700), new PointD(1600, 760), new PointD(600, 760));
        if (variant == 0)
            drawing
                .FillColor(new MagickColor("#F2F2F2")).Polygon(new PointD(1180, 760), new PointD(1260, 600), new PointD(1260, 760))
                .FillColor(new MagickColor("#7A4E2D")).Rectangle(1150, 760, 1300, 790);
        drawing.Draw(image);
        image.Quality = 92;
        image.Write(path, MagickFormat.Jpeg);
    }

    /// <summary>
    /// Renders and writes one clip. FILEE_WHATSNEW_ONLY (names without extension, comma-separated) limits a run to some
    /// clips, e.g. <c>FILEE_WHATSNEW_ONLY=crop,theme</c>; <paramref name="onRequestOnly"/> clips (made elsewhere) are
    /// written only when named there.
    /// </summary>
    private static void Write(string folder, string name, Func<Clip> render, bool onRequestOnly = false)
    {
        var only = Environment.GetEnvironmentVariable("FILEE_WHATSNEW_ONLY");
        var named = !string.IsNullOrWhiteSpace(only)
                    && only.Split(',', StringSplitOptions.TrimEntries).Contains(Path.GetFileNameWithoutExtension(name), StringComparer.OrdinalIgnoreCase);
        if (onRequestOnly ? !named : !string.IsNullOrWhiteSpace(only) && !named)
            return;
        var language = AppHost.Get<ILocalizer>().Language;
        Console.WriteLine($"Rendering {language}: {name}");
        using (var clip = render())
        {
            var path = Path.Combine(folder, name);
            clip.WriteWebP(path);
            // The first frame next to the screenshots, for review.
            clip.SaveFirstFrame(Path.Combine(TestServices.ScreenshotDirectory, $"whatsnew-{language}-" + Path.ChangeExtension(name, ".png")));
            Assert.InRange(new FileInfo(path).Length, 1, 400 * 1024);
            if (language != "en" || clip.Poster is not null)
                SetPoster(folder, name, clip.Poster ?? 0);
        }
    }

    /// <summary>Writes the frame a clip marked as its <c>poster</c> into the slide of slides.json that shows it.</summary>
    private static void SetPoster(string folder, string media, int frame)
    {
        var language = AppHost.Get<ILocalizer>().Language;
        if (language != "en")
        {
            var manifest = Path.Combine(Path.GetDirectoryName(folder)!, "slides.json");
            var root = JsonNode.Parse(File.ReadAllText(manifest))!;
            var item = root["slides"]!.AsArray().Single(s => s!["media"]!.GetValue<string>() == media)!;
            item["localizedMedia"] ??= new JsonObject();
            item["localizedMedia"]![language] = new JsonObject { ["media"] = $"{language}/{media}", ["poster"] = frame };
            File.WriteAllText(manifest, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
            return;
        }
        var path = Path.Combine(folder, "slides.json");
        var text = File.ReadAllText(path);
        var slide = Regex.Match(text, $$"""\{[^{}]*"media": "{{Regex.Escape(media)}}"[^{}]*\}""");
        Assert.True(slide.Success, $"slides.json has no slide showing {media}");
        var updated = Regex.Replace(slide.Value, """ "poster": \d+,""", "")
            .Replace($"\"media\": \"{media}\",", $"\"media\": \"{media}\", \"poster\": {frame},", StringComparison.Ordinal);
        File.WriteAllText(path, text.Replace(slide.Value, updated, StringComparison.Ordinal));
    }

    /// <summary>Frames with how long each shows; a frame identical to the one before only lengthens it.</summary>
    private sealed class Clip : IDisposable
    {
        private readonly List<(MagickImage Image, int Milliseconds)> _frames = [];

        public int Count => _frames.Count;

        /// <summary>WebP quality of the frames: lower for a long clip with much movement, to stay within the budget.</summary>
        public uint Quality { get; init; } = 82;

        /// <summary>The frame reduced motion shows instead of the first (<see cref="MarkPoster"/>).</summary>
        public int? Poster { get; private set; }

        /// <summary>The frame added last is the clip's poster.</summary>
        public void MarkPoster() => Poster = _frames.Count - 1;

        /// <summary>Shows the last frame <paramref name="milliseconds"/> longer.</summary>
        public void Hold(int milliseconds) => _frames[^1] = (_frames[^1].Image, _frames[^1].Milliseconds + milliseconds);

        public void Add(MagickImage image, int milliseconds)
        {
            if (_frames.Count > 0 && _frames[^1].Image.Compare(image, ErrorMetric.Absolute) == 0)
            {
                _frames[^1] = (_frames[^1].Image, _frames[^1].Milliseconds + milliseconds);
                image.Dispose();
                return;
            }
            _frames.Add((image, milliseconds));
        }

        public void WriteWebP(string path)
        {
            using var collection = new MagickImageCollection();
            foreach (var (image, milliseconds) in _frames)
            {
                var copy = image.Clone();
                // Delays are in 1/100 s; at least 2 so viewers don't speed the frame up.
                copy.AnimationDelay = (uint)Math.Max(2, Math.Round(milliseconds / 10.0));
                copy.AnimationTicksPerSecond = 100;
                copy.Quality = Quality;
                collection.Add(copy);
            }
            var defines = new ImageMagick.Formats.WebPWriteDefines { Method = 6, Lossless = false, ThreadLevel = true };
            if (_frames.Count > 1)
                collection[0].Settings.SetDefine(MagickFormat.WebP, "mixed", "true");
            collection.Write(path, defines);
        }

        public void SaveFirstFrame(string path) => _frames[0].Image.Write(path, MagickFormat.Png);

        public void Dispose()
        {
            foreach (var (image, _) in _frames)
                image.Dispose();
            _frames.Clear();
        }
    }
}
