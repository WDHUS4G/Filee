// The crop overlay's window: the stage with the frame and its size chip, the aspect dial, the strip of several files,
// the end summary (screenshots in light and dark, short and long languages), and its keys and ratio choices.

using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Filee.App.Controls;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.Views;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Core.Settings;
using ImageMagick;
using ImageMagick.Drawing;
using static Filee.App.Tests.RenderTests;

namespace Filee.App.Tests;

public class CropOverlayTests
{
    public static TheoryData<string, bool> Looks => new()
    {
        { "en", false }, { "en", true }, { "ko", false }, { "ko", true }, { "ru", false }, { "hi", true }, { "ar", false }, { "ar", true },
        { "pt-PT", false }, { "pt-PT", true }, { "pt-BR", false }, { "pt-BR", true },
        { "fr", false },
    };

    [AvaloniaTheory]
    [MemberData(nameof(Looks))]
    public void One_picture_shows_the_frame_its_size_and_the_aspect_dial(string language, bool dark)
    {
        WithOverlay(language, dark, ["harbour.jpg"], (window, vm, suffix) =>
        {
            Assert.False(vm.HasMultipleFiles);
            Assert.Equal(AppHost.Get<ILocalizer>()["crop.save"], vm.SaveText);

            vm.SetAspect("4:3");
            window.CropCanvas.NormalizedCropRect = new Rect(0.30, 0.18, 0.5, 0.6);
            Settle(window);
            Assert.Equal("800 × 600 px", vm.DimensionsText);
            Save(window, $"crop-single-{suffix}.png");

            vm.ToggleAspectDial();
            Settle(window);
            Assert.True(window.AspectDial.IsVisible);
            Assert.Equal("4:3", window.AspectDial.Segments.Single(s => s.IsSelected).Mode);
            Save(window, $"crop-dial-{suffix}.png");
        });
    }

    [AvaloniaTheory]
    [MemberData(nameof(Looks))]
    public void Several_files_show_a_strip_with_how_each_went(string language, bool dark)
    {
        WithOverlay(language, dark, ["one.jpg", "two.jpg", "spinner.gif", "four.jpg", "five.jpg"], (window, vm, suffix) =>
        {
            Assert.True(vm.HasMultipleFiles);
            Assert.Equal(AppHost.Get<ILocalizer>()["crop.save_next"], vm.SaveText);

            _ = vm.CropAndSaveAsync();
            WaitFor(() => vm.CurrentIndex == 1 && !vm.IsBusy);
            _ = vm.SkipAsync();
            // The animated GIF can't be cropped: it fails on its own and the fourth file opens.
            WaitFor(() => vm.CurrentIndex == 3 && !vm.IsBusy && vm.PreviewImage is not null);

            Assert.Equal(
                [CropItemState.Done, CropItemState.Skipped, CropItemState.Failed, CropItemState.Current, CropItemState.Waiting],
                vm.Queue.Select(q => q.State));
            Assert.All(vm.Queue.Take(2).Append(vm.Queue[3]), q => Assert.NotNull(q.Thumbnail));
            Assert.Null(vm.Queue[4].Thumbnail);
            Assert.True(vm.HasStatusNotice);

            window.CropCanvas.NormalizedCropRect = new Rect(0.12, 0.2, 0.6, 0.55);
            Settle(window);
            Save(window, $"crop-multi-{suffix}.png");

            // Several problems: the summary stays until closed, with a slice per file.
            _ = vm.CropAndSaveAsync();
            WaitFor(() => vm.CurrentIndex == 4 && !vm.IsBusy);
            _ = vm.CropAndSaveAsync();
            WaitFor(() => vm.IsFinished);
            Assert.Equal((3, 1, 1), (vm.DoneCount, vm.SkippedCount, vm.FailedCount));
            Settle(window);
            Save(window, $"crop-summary-{suffix}.png");
        });
    }

    [AvaloniaFact]
    public void Dragging_shows_the_grid_and_the_size_chip_follows_the_frame()
    {
        WithOverlay("en", false, ["harbour.jpg"], (window, vm, _suffix) =>
        {
            var canvas = window.CropCanvas;
            var image = canvas.CalculateImageBounds();
            var origin = canvas.TranslatePoint(default, window)!.Value;
            var corner = canvas.GetScreenCropRect(image).BottomRight + (Vector)origin;
            window.MouseDown(corner, MouseButton.Left);
            window.MouseMove(corner - new Vector(image.Width * 0.3, image.Height * 0.25));
            Pump();
            canvas.FinishAnimations();
            var size = CropMath.PixelSize(canvas.NormalizedCropRect, 1600, 1000);
            Assert.InRange(size.Width, 1100, 1140);
            Assert.Equal($"{size.Width} × {size.Height} px", vm.DimensionsText);
            Save(window, "crop-drag-en.png");
            window.MouseUp(corner, MouseButton.Left);
        });
    }

    [AvaloniaFact]
    public void A_frame_moved_near_the_middle_snaps_to_the_centre_lines()
    {
        WithOverlay("en", false, ["harbour.jpg"], (window, vm, _suffix) =>
        {
            var canvas = window.CropCanvas;
            canvas.NormalizedCropRect = new Rect(0.1, 0.1, 0.5, 0.5); // centre at 35 %, 35 %
            Settle(window);
            var image = canvas.CalculateImageBounds();
            var origin = canvas.TranslatePoint(default, window)!.Value;
            var start = canvas.GetScreenCropRect(image).Center + (Vector)origin;
            // To 3 screen pixels short of the picture's centre on both axes: inside the snap distance.
            var target = image.Center + (Vector)origin - new Vector(3, 3);
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(start + (target - start) / 2);
            window.MouseMove(target);
            Pump();
            Save(window, "crop-snap-en.png");
            window.MouseUp(target, MouseButton.Left);

            var frame = canvas.NormalizedCropRect;
            Assert.Equal(0.5, frame.X + frame.Width / 2, 3);
            Assert.Equal(0.5, frame.Y + frame.Height / 2, 3);
        });
    }

    [AvaloniaFact]
    public void Keys_nudge_the_frame_open_the_dial_reset_and_close()
    {
        WithOverlay("en", false, ["harbour.jpg"], (window, vm, _suffix) =>
        {
            var canvas = window.CropCanvas;
            canvas.NormalizedCropRect = new Rect(0.25, 0.25, 0.5, 0.5); // 400, 250, 800 × 500
            Press(window, Key.Right);
            Press(window, Key.Right);
            Press(window, Key.Down, RawInputModifiers.Shift);
            Assert.Equal(new Rect(402 / 1600.0, 260 / 1000.0, 0.5, 0.5), canvas.NormalizedCropRect);
            Assert.Equal(canvas.NormalizedCropRect, vm.CurrentCropRect);

            // Stops at the picture's edge.
            for (var i = 0; i < 50; i++)
                Press(window, Key.Left, RawInputModifiers.Shift);
            Assert.Equal(0, canvas.NormalizedCropRect.X);

            // A opens the dial: arrows then go round its slices instead of moving the frame, Esc closes only the dial.
            Press(window, Key.A);
            Assert.True(vm.IsAspectDialOpen);
            Pump();
            var before = canvas.NormalizedCropRect;
            Press(window, Key.Right);
            Assert.Equal(before, canvas.NormalizedCropRect);
            Press(window, Key.Escape);
            Assert.False(vm.IsAspectDialOpen);
            Assert.True(window.IsVisible);

            // X swaps a fixed ratio; R resets the ratio and the frame.
            vm.SetAspect("16:9");
            Press(window, Key.X);
            Assert.True(vm.IsPortrait);
            Assert.Equal(9.0 / 16.0, canvas.AspectRatio, 6);
            Press(window, Key.R);
            Assert.Equal("free", vm.SelectedAspect);
            Assert.False(vm.IsPortrait);
            Assert.Equal(0, canvas.AspectRatio);
            Assert.Equal(new Rect(0, 0, 1, 1), canvas.NormalizedCropRect);

            var closed = false;
            window.Closed += (_, _) => closed = true;
            Press(window, Key.Escape);
            Assert.True(closed);
        });
    }

    [AvaloniaTheory]
    [InlineData("en", false)]
    [InlineData("en", true)]
    [InlineData("ar", false)]
    public void A_over_the_picture_opens_the_dial_around_the_pointer(string language, bool dark)
    {
        WithOverlay(language, dark, ["harbour.jpg", "two.jpg"], (window, vm, suffix) =>
        {
            var canvas = window.CropCanvas;
            var dial = window.AspectDial;
            var origin = (Vector)canvas.TranslatePoint(default, window)!.Value;
            var image = canvas.CalculateImageBounds().Translate(origin);
            Point DialCentre() => dial.TranslatePoint(new Point(dial.Bounds.Width / 2, dial.Bounds.Height / 2), window)!.Value;

            // No pointer over the stage yet (the keyboard): over its button, as before.
            Press(window, Key.A);
            Settle(window);
            Assert.True(vm.IsAspectDialOpen);
            var button = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "DialButton");
            var buttonTop = button.TranslatePoint(new Point(button.Bounds.Width / 2, 0), window)!.Value;
            Assert.Equal(buttonTop.X, DialCentre().X, 0.5);
            Assert.Equal(buttonTop.Y - CropMath.DialGap, dial.TranslatePoint(new Point(0, dial.Bounds.Height), window)!.Value.Y, 0.5);
            Press(window, Key.Escape);
            Assert.False(vm.IsAspectDialOpen);
            Assert.True(window.IsVisible);

            // The pointer over the picture: A opens the dial around it.
            var at = new Point(image.X + image.Width * 0.62, image.Y + image.Height * 0.4);
            window.MouseMove(at);
            Press(window, Key.A);
            Settle(window);
            Assert.True(vm.IsAspectDialOpen);
            Assert.Equal(at.X, DialCentre().X, 0.5);
            Assert.Equal(at.Y, DialCentre().Y, 0.5);
            Save(window, $"crop-dial-pointer-{suffix}.png");

            // The arrows go round the slices, not the frame; A again closes it.
            var frame = canvas.NormalizedCropRect;
            Press(window, Key.Right);
            Assert.Equal(frame, canvas.NormalizedCropRect);
            Press(window, Key.A);
            Assert.False(vm.IsAspectDialOpen);

            // Picked with a click: the slice under the pointer.
            Press(window, Key.A);
            Settle(window);
            var square = dial.Segments.ToList().FindIndex(s => s.Mode == "1:1");
            var onSquare = dial.TranslatePoint(DonutGeometry.PointAt(new Point(dial.Bounds.Width / 2, dial.Bounds.Height / 2),
                (dial.InnerRadius + dial.OuterRadius) / 2, DonutGeometry.MidAngle(square, dial.Segments.Count())), window)!.Value;
            window.MouseMove(onSquare);
            window.MouseDown(onSquare, MouseButton.Left);
            window.MouseUp(onSquare, MouseButton.Left);
            Pump();
            Assert.Equal("1:1", vm.SelectedAspect);
            Assert.False(vm.IsAspectDialOpen);

            // Near a corner of the window: moved in until the whole dial shows.
            var corner = new Point(window.Bounds.Width - 30, 30);
            window.MouseMove(corner);
            Press(window, Key.A);
            Settle(window);
            var stage = dial.GetVisualParent<Visual>()!;
            var bounds = new Rect(dial.TranslatePoint(default, stage)!.Value, dial.Bounds.Size);
            Assert.True(new Rect(stage.Bounds.Size).Deflate(CropMath.DialMargin - 0.5).Contains(bounds), $"{bounds} outside {stage.Bounds}");
            Press(window, Key.Escape);

            // The toolbar button opens it over the button again, whatever A did before.
            window.MouseMove(at);
            vm.ToggleAspectDial();
            Settle(window);
            Assert.Equal(buttonTop.X, DialCentre().X, 0.5);
        });
    }

    [AvaloniaFact]
    public void Fixed_ratios_follow_the_pictures_orientation_and_swap()
    {
        WithOverlay("en", false, ["tall.jpg"], (window, vm, _suffix) =>
        {
            Assert.True(vm.IsPortrait); // a portrait picture starts with the portrait forms
            vm.SetAspect("4:3");
            Assert.Equal(3.0 / 4.0, vm.AspectRatio, 6);
            Assert.Equal("3:4", vm.AspectLabel);
            Assert.True(vm.CanSwapOrientation);

            vm.SwapOrientation();
            Assert.False(vm.IsPortrait);
            Assert.Equal(4.0 / 3.0, window.CropCanvas.AspectRatio, 6);
            Assert.Equal("4:3", vm.AspectLabel);
            var frame = window.CropCanvas.NormalizedCropRect;
            Assert.Equal(4.0 / 3.0, frame.Width * 1000 / (frame.Height * 1600), 2);

            // Square, free and the picture's own ratio have no second form.
            vm.SetAspect("1:1");
            Assert.False(vm.CanSwapOrientation);
            vm.SwapOrientation();
            Assert.Equal(1.0, vm.AspectRatio);
            vm.SetAspect("original");
            Assert.Equal(1000.0 / 1600.0, vm.AspectRatio, 6);
            Assert.Equal("Original", vm.AspectLabel);
        });
    }

    [AvaloniaFact]
    public void Every_language_fits_its_longest_one_word_ratio_name_on_a_slice_without_breaking_it()
    {
        var dial = new AspectDial();
        var width = RingSegment.LabelWidth(dial.InnerRadius, dial.OuterRadius, CropMath.AspectModes.Count);
        var i18n = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Filee.App", "Assets", "i18n");
        foreach (var language in LocalizationService.Languages.Select(l => l.Code))
        {
            var strings = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(i18n, language + ".json")))!;
            var typeface = new Avalonia.Media.Typeface(SoftPalette.FontFamilyFor(language), Avalonia.Media.FontStyle.Normal, Avalonia.Media.FontWeight.SemiBold);
            foreach (var key in new[] { "crop.aspect_free", "crop.aspect_original" })
            {
                if (!strings.TryGetValue(key, out var label))
                    continue;
                var longest = label.Split(' ').Max(word => new Avalonia.Media.FormattedText(word, System.Globalization.CultureInfo.InvariantCulture,
                    Avalonia.Media.FlowDirection.LeftToRight, typeface, 12, Avalonia.Media.Brushes.Black).Width);
                Assert.True(12 * width / longest >= RingSegment.MinLabelSize, $"{language} {key} \"{label}\" needs {12 * width / longest:F2}");
            }
        }
    }

    [AvaloniaFact]
    public void An_upright_picture_crops_at_instagrams_4_to_5_from_the_5_to_4_slice()
    {
        WithOverlay("en", false, ["tall.jpg"], (window, vm, _suffix) =>
        {
            Settle(window);
            var slice = window.AspectDial.Segments.Single(s => s.Mode == "5:4");
            slice.Command!.Execute(slice.CommandParameter);
            Settle(window);
            Assert.Equal("4:5", vm.AspectLabel);
            Assert.Equal(0.8, window.CropCanvas.AspectRatio, 6);
            var frame = window.CropCanvas.NormalizedCropRect;
            Assert.Equal(0.8, frame.Width * 1000 / (frame.Height * 1600), 2);
            vm.ToggleAspectDial();
            Settle(window);
            RenderTests.Save(window, "crop-dial-4-5.png");
        });
    }

    [AvaloniaFact]
    public void The_next_file_starts_free_and_full_in_view_and_canvas_alike()
    {
        WithOverlay("en", false, ["harbour.jpg", "tall.jpg"], (window, vm, _suffix) =>
        {
            vm.SetAspect("16:9");
            vm.SwapOrientation();
            _ = vm.CropAndSaveAsync();
            WaitFor(() => vm.CurrentIndex == 1 && !vm.IsBusy && vm.PreviewImage is not null);
            Pump();

            Assert.Equal("free", vm.SelectedAspect);
            Assert.Equal(0, vm.AspectRatio);
            Assert.Equal(0, window.CropCanvas.AspectRatio);
            Assert.True(vm.IsPortrait); // the second picture is upright
            Assert.Equal(new Rect(0, 0, 1, 1), window.CropCanvas.NormalizedCropRect);
            Assert.Equal(new Rect(0, 0, 1, 1), vm.CurrentCropRect);
            Assert.Equal("1000 × 1600 px", vm.DimensionsText);
            Assert.Equal(AppHost.Get<ILocalizer>()["crop.save"], vm.SaveText);
        });
    }

    // ───────────────────────── Helpers ─────────────────────────

    private static void WithOverlay(string language, bool dark, string[] names, Action<CropOverlayWindow, CropOverlayViewModel, string> act)
    {
        TestServices.EnsureInitialized(language);
        var theme = AppHost.Get<ThemeService>();
        if (dark)
            theme.Apply(new ThemeSettings { Mode = ThemeMode.Dark }, language);
        var folder = Directory.CreateTempSubdirectory("filee-crop-overlay-").FullName;
        CropOverlayWindow? window = null;
        try
        {
            var files = names.Select(name => Picture(folder, name)).ToList();
            var preset = BuiltInData.CreatePresets().Single(p => p.Id == "tool-crop");
            var vm = new CropOverlayViewModel(files, preset, AppHost.Get<ILocalizer>(), AppHost.Get<UserDataStore>());
            window = new CropOverlayWindow { DataContext = vm, Width = 1040, Height = 720 };
            window.Show();
            WaitFor(() => vm.PreviewImage is not null && !vm.IsBusy);
            Pump();
            window.CropCanvas.FinishAnimations();
            act(window, vm, $"{language}-{(dark ? "dark" : "light")}");
        }
        finally
        {
            if (window?.IsVisible == true)
                window.Close();
            if (dark)
                theme.Apply(new ThemeSettings(), language);
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    /// <summary>A landscape (1600 × 1000), an upright picture ("tall", 1000 × 1600) or a two-frame GIF ("…gif").</summary>
    private static string Picture(string folder, string name)
    {
        var path = Path.Combine(folder, name);
        if (name.EndsWith(".gif", StringComparison.Ordinal))
        {
            using var frames = new MagickImageCollection();
            frames.Add(new MagickImage(MagickColors.Coral, 40, 40));
            frames.Add(new MagickImage(MagickColors.Teal, 40, 40));
            frames.Write(path, MagickFormat.Gif);
            return path;
        }
        var tall = name.StartsWith("tall", StringComparison.Ordinal);
        var hue = name.Sum(c => c) % 5;
        string[] skies = ["#8EC5FC-#F8D2A6", "#A1C4FD-#C2E9FB", "#FBC2EB-#A6C1EE", "#84FAB0-#8FD3F4", "#FFECD2-#FCB69F"];
        using var image = new MagickImage($"gradient:{skies[hue]}", tall ? 1000u : 1600u, tall ? 1600u : 1000u);
        var w = (double)image.Width;
        var h = (double)image.Height;
        new Drawables()
            .FillColor(new MagickColor("#FFF2C2")).Circle(w * 0.68, h * 0.36, w * 0.68, h * 0.36 + Math.Min(w, h) * 0.1)
            .FillColor(new MagickColor("#4F7CAC")).Rectangle(0, h * 0.64, w, h)
            .FillColor(new MagickColor("#6C9A5B")).Polygon(new PointD(0, h * 0.7), new PointD(w * 0.24, h * 0.47), new PointD(w * 0.48, h * 0.69), new PointD(0, h * 0.76))
            .FillColor(new MagickColor("#3F6E4A")).Polygon(new PointD(w * 0.38, h * 0.7), new PointD(w * 0.62, h * 0.52), new PointD(w, h * 0.7), new PointD(w, h * 0.76), new PointD(w * 0.38, h * 0.76))
            .Draw(image);
        image.Write(path, MagickFormat.Jpeg);
        return path;
    }

    private static void Settle(CropOverlayWindow window)
    {
        Pump();
        window.SettleForCapture();
        Pump();
    }

    private static void Press(CropOverlayWindow window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }

    private static void WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        Assert.True(condition());
    }
}
