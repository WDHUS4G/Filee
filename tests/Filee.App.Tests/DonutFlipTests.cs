// Tab flips the open donut to the back face of its profile: in click mode as a key of the focused window, in drag
// mode through the global hook. Arrows still move between slices, an empty or unusable back face doesn't flip, the
// back face greys out what can't convert the files like the front, and "Reduce animations" swaps the faces at once.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Filee.App.Controls;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.ViewModels.Pages;
using Filee.App.Views;
using Filee.Core.Localization;
using Filee.Core.Platform;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using SharpHook.Data;

namespace Filee.App.Tests;

[Collection("Motion")]
public class DonutFlipTests
{
    private static readonly string[] Photos = [@"C:\photos\a.jpg", @"C:\photos\b.jpg"];

    private static UserDataStore Store => AppHost.Get<UserDataStore>();

    private static ToolbarProfile Images => Store.Profiles.Single(p => p.Id == "images");

    /// <summary>The image donut's back face, as the library has it now.</summary>
    private static List<string> ImageBack => Images.BackPresetIds!.Where(id => Store.FindPreset(id) is not null).ToList();

    /// <summary>
    /// Runs <paramref name="test"/> with animations on or off and the image donut's back face as given (null: the
    /// built-in one); everything is put back afterwards.
    /// </summary>
    private static void With(bool animations, List<string>? back, Action test, string language = "en", string? flipKey = null,
        bool? wheel = null)
    {
        TestServices.EnsureInitialized(language);
        var images = Images;
        var (front, before) = (images.PresetIds.ToList(), images.BackPresetIds?.ToList());
        var donut = Store.Settings.Donut;
        var (keyBefore, wheelBefore) = (donut.FlipKey, donut.FlipWithWheel);
        try
        {
            if (back is not null)
                images.BackPresetIds = back;
            donut.FlipKey = flipKey ?? DonutSettings.DefaultFlipKey;
            donut.FlipWithWheel = wheel ?? true;
            Motion.Configure(!animations, () => false);
            test();
        }
        finally
        {
            Motion.Configure(false, () => false);
            images.PresetIds = front;
            images.BackPresetIds = before;
            (donut.FlipKey, donut.FlipWithWheel) = (keyBefore, wheelBefore);
        }
    }

    /// <summary>The donut for <paramref name="files"/>, opened like the selection shortcut opens it.</summary>
    private static (RadialViewModel Vm, RadialWindow Window, DonutMenu Donut) OpenClickMode(params string[] files)
    {
        var vm = AppHost.Get<RadialViewModel>();
        vm.Load(files.Length > 0 ? files : Photos, clickMode: true);
        vm.FitToThisDonut();
        var window = new RadialWindow { DataContext = vm };
        HintLikeTheController(vm, window.DonutControl);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (vm, window, window.DonutControl);
    }

    /// <summary>The hub says what the slice under the pointer or the keyboard does, as RadialController has it.</summary>
    private static void HintLikeTheController(RadialViewModel vm, DonutMenu donut) =>
        donut.ActiveIndexChanged += (_, index) => vm.ShowHint(index, donut.ActiveSubIndex, donut.IsCenterHot);

    private static (DonutMenu Donut, Avalonia.Controls.Window Window) Wave(int front = 6, int back = 4)
    {
        TestServices.EnsureInitialized();
        Motion.Configure(false, () => false);
        DonutItem[] Face(int count, string prefix) =>
            Enumerable.Range(0, count).Select(i => new DonutItem { Id = $"{prefix}{i}", Label = $"{prefix}{i}" }).ToArray();
        var first = Face(front, "front");
        var second = Face(back, "back");
        var donut = new DonutMenu { Items = first, CanFlip = true };
        var backShown = false;
        donut.FaceSwapping += (_, _) => donut.Items = (backShown = !backShown) ? second : first;
        var window = new Avalonia.Controls.Window { Content = donut, SizeToContent = Avalonia.Controls.SizeToContent.WidthAndHeight };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (donut, window);
    }

    private static Point At(DonutMenu donut, double angle) =>
        DonutGeometry.PointAt(new Point(donut.Bounds.Width / 2, donut.Bounds.Height / 2), donut.OuterRadius * 0.7, angle);

    private static byte[] PixelAt(DonutMenu donut, Point at)
    {
        using var image = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)donut.Bounds.Width, (int)donut.Bounds.Height));
        image.Render(donut);
        var pixel = new byte[4];
        var pin = System.Runtime.InteropServices.GCHandle.Alloc(pixel, System.Runtime.InteropServices.GCHandleType.Pinned);
        try { image.CopyPixels(new PixelRect((int)at.X, (int)at.Y, 1, 1), pin.AddrOfPinnedObject(), 4, 4); }
        finally { pin.Free(); }
        // Always B, G, R, A: macOS renders RGBA where Windows and Linux render BGRA.
        if (image.Format == Avalonia.Platform.PixelFormat.Rgba8888)
            (pixel[0], pixel[2]) = (pixel[2], pixel[0]);
        return pixel;
    }

    private static byte AlphaAt(DonutMenu donut, Point at) => PixelAt(donut, at)[3];

    [AvaloniaTheory]
    [InlineData(0.01)]
    [InlineData(0.1)]
    public void T1_drop_during_the_wave_uses_the_new_face_preset(double elapsed) => With(true, null, () =>
    {
        var provider = new TriggerServiceLifecycleTests.DelayedProvider();
        provider.AllowExit.Set();
        using var triggers = TriggerServiceLifecycleTests.Create(provider, () => { });
        var vm = AppHost.Get<RadialViewModel>();
        var radial = new RadialController(vm, AppHost.Get<ConversionService>(), AppHost.Get<WindowService>(), Store,
            AppHost.Get<IPlatformServices>(), triggers, NullLogger<RadialController>.Instance);
        var folder = Directory.CreateTempSubdirectory("filee-wave-drop-");
        var conversions = AppHost.Get<ConversionService>();
        try
        {
            var file = Path.Combine(folder.FullName, "photo.jpg");
            File.WriteAllBytes(file, [0xFF, 0xD8, 0xFF]);
            radial.ShowForDrag(400, 400);
            var window = radial.Window!;
            RenderTests.Pump();
            var data = new DataTransfer();
            data.Add(DataTransferItem.CreateFile(window.StorageProvider.TryGetFileFromPathAsync(file).GetAwaiter().GetResult()!));
            var donut = window.DonutControl;
            var at = At(donut, DonutGeometry.MidAngle(7, ImageBack.Count)); // "to-gif", not the crop tool
            var inWindow = donut.TranslatePoint(at, window)!.Value;
            window.DragDrop(inWindow, RawDragEventType.DragEnter, data, DragDropEffects.Copy, RawInputModifiers.Control);
            Dispatcher.UIThread.RunJobs();
            var frontPreset = vm.PresetAt(donut.HitTest(at).Index, -1)!;
            Assert.True(donut.Flip());
            donut.Advance(elapsed);
            var preset = vm.PresetAt(donut.HitTest(at).Index, -1)!;
            Assert.Contains(preset.Id, ImageBack);
            Assert.NotEqual(frontPreset.Id, preset.Id);
            var before = conversions.Jobs.Select(j => j.Job.Id).ToHashSet();
            window.DragDrop(inWindow, RawDragEventType.Drop, data, DragDropEffects.Copy, RawInputModifiers.Control);
            var job = Assert.Single(conversions.Jobs, j => !before.Contains(j.Job.Id));
            Assert.Equal(preset.Id, job.Job.Preset.Id);
            job.CancelCommand.Execute(null);
            radial.Close(animate: false);
            window.Close();
        }
        finally
        {
            radial.Close(animate: false);
            folder.Delete(recursive: true);
        }
    });

    [AvaloniaFact]
    public void T2_spoke_transforms_origin_priority_and_shadow_are_staggered()
    {
        var (donut, window) = Wave();
        try
        {
            donut.SetExternalHit(donut.HitTest(At(donut, Math.PI / 6)), At(donut, Math.PI / 6));
            Assert.True(donut.Flip());
            Assert.Equal(Math.PI / 6, donut.FlipOrigin!.Value, 8);
            Assert.Equal(1, donut.ShadowCoverage, 8);
            donut.Advance(0.02);
            Assert.InRange(donut.SliceFlipWidth(true, 0), 0.01, 0.99); // off-centre origin has no delay
            Assert.Equal(1, donut.SliceFlipWidth(true, 3));
            var center = new Point(donut.Bounds.Width / 2, donut.Bounds.Height / 2);
            var top = donut.SliceFlipTransform(true, 0);
            var right = donut.SliceFlipTransform(true, 1);
            var topWidth = donut.SliceFlipWidth(true, 0);
            var rightWidth = donut.SliceFlipWidth(true, 1);
            Assert.Equal(topWidth, (new Point(center.X + 1, center.Y) * top).X - (center * top).X, 6);
            Assert.Equal(-1, (new Point(center.X, center.Y - 1) * top).Y - (center * top).Y, 6);
            var rightTangent = new Vector(Math.Cos(Math.PI / 3), Math.Sin(Math.PI / 3));
            var basePoint = center * right;
            var tangentPoint = (center + rightTangent) * right;
            Assert.Equal(rightWidth, new Vector(tangentPoint.X - basePoint.X, tangentPoint.Y - basePoint.Y).Length, 6);
            Assert.InRange(donut.ShadowCoverage, 0, 0.999);
            donut.Advance(DonutMenu.FlipDuration);
            Assert.False(donut.HasOldFace);
            Assert.All(Enumerable.Range(0, 4), i => Assert.Equal(1, donut.SliceFlipWidth(false, i)));
            Assert.Equal(1, donut.ShadowCoverage, 8);
        }
        finally { window.Close(); }
        var (four, fourWindow) = Wave(4, 4);
        try
        {
            four.Flip();
            four.Advance(0.06);
            var center = new Point(four.Bounds.Width / 2, four.Bounds.Height / 2);
            var transform = four.SliceFlipTransform(true, 1);
            var transformed = center * transform;
            var radial = (center + new Vector(1, 0)) * transform;
            var tangent = (center + new Vector(0, 1)) * transform;
            Assert.Equal(1, new Vector(radial.X - transformed.X, radial.Y - transformed.Y).Length, 6);
            Assert.Equal(four.SliceFlipWidth(true, 1),
                new Vector(tangent.X - transformed.X, tangent.Y - transformed.Y).Length, 6);
        }
        finally { fourWindow.Close(); }
        foreach (var source in new[] { "pointer", "keyboard", "none" })
        {
            var (other, host) = Wave();
            try
            {
                if (source == "pointer")
                    host.MouseMove(At(other, Math.PI));
                if (source == "keyboard")
                {
                    for (var i = 0; i < 4; i++)
                        other.HandleKey(Key.Right);
                }
                other.Flip();
                Assert.Equal(source == "none" ? 0 : Math.PI, other.FlipOrigin!.Value, 6);
            }
            finally { host.Close(); }
        }
        var (centred, centredHost) = Wave();
        try
        {
            centredHost.MouseMove(At(centred, Math.PI));
            var hub = new Point(centred.Bounds.Width / 2, centred.Bounds.Height / 2);
            centred.SetExternalHit(centred.HitTest(hub), hub);
            centred.Flip();
            Assert.Equal(0, centred.FlipOrigin!.Value, 6); // a drag exactly on the centre is rule 4, not the pointer
        }
        finally { centredHost.Close(); }
    }

    [AvaloniaFact]
    public void T4_a_fresh_drag_highlight_is_kept_as_it_was_drawn()
    {
        var (donut, window) = Wave();
        try
        {
            donut.SliceBrush = Avalonia.Media.Brushes.Blue;
            donut.AccentBrush = Avalonia.Media.Brushes.Red;
            donut.SliceOpacity = 1;
            donut.SliceBorderBrush = null;
            var at = At(donut, Math.PI / 3);
            donut.SetExternalHit(donut.HitTest(at), at);
            var probe = At(donut, Math.PI / 3 + 0.35);
            var before = PixelAt(donut, probe);
            Assert.True(before[0] > before[2], "The highlight has not lit up yet");
            Assert.True(donut.Flip());
            var after = PixelAt(donut, probe);
            Assert.All(Enumerable.Range(0, 4), c => Assert.InRange(after[c] - before[c], -2, 2));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void T3_unequal_faces_wait_for_overlapping_old_slices()
    {
        foreach (var (front, back) in new[] { (6, 4), (1, 6), (6, 1) })
        {
            var (donut, window) = Wave(front, back);
            try
            {
                Assert.True(donut.Flip());
                donut.Advance(0.061);
                if (front == 1)
                {
                    Assert.Equal(0, donut.SliceFlipWidth(true, 0));
                    Assert.True(donut.SliceFlipWidth(false, 0) > 0);
                    Assert.Equal(0, donut.SliceFlipWidth(false, 1));
                }
                if (back == 1)
                    Assert.Equal(0, donut.SliceFlipWidth(false, 0));
                if (front == 6 && back == 4)
                {
                    Assert.True(donut.SliceFlipWidth(true, 1) > 0);
                    Assert.Equal(0, donut.SliceFlipWidth(false, 1));
                    donut.Advance(0.039);
                    Assert.Equal(0, donut.SliceFlipWidth(true, 1));
                    Assert.True(donut.SliceFlipWidth(true, 2) > 0);
                    Assert.Equal(0, donut.SliceFlipWidth(false, 1));
                    donut.Advance(0.014);
                    Assert.Equal(0, donut.SliceFlipWidth(true, 2));
                    Assert.True(donut.SliceFlipWidth(false, 1) > 0);
                }
                Assert.True(donut.ShadowCoverage <= 1);
                donut.Advance(DonutMenu.FlipDuration);
                Assert.All(Enumerable.Range(0, back), i => Assert.Equal(1, donut.SliceFlipWidth(false, i)));
            }
            finally { window.Close(); }
        }
        var (pointed, pointedWindow) = Wave(6, 1);
        try
        {
            pointed.SetExternalHit(pointed.HitTest(At(pointed, Math.PI)), At(pointed, Math.PI));
            pointed.Flip();
            pointed.Advance(0.061);
            Assert.True(pointed.SliceFlipWidth(false, 0) > 0);
            Assert.True(pointed.SliceFlipWidth(true, 0) > 0);
        }
        finally { pointedWindow.Close(); }
        var (layered, layeredWindow) = Wave(6, 1);
        try
        {
            layered.SliceBrush = Avalonia.Media.Brushes.Blue;
            layered.AccentBrush = Avalonia.Media.Brushes.Red;
            layered.SliceOpacity = 1;
            layered.SliceBorderBrush = null;
            layered.SetExternalHit(layered.HitTest(At(layered, 0)), At(layered, 0));
            layered.Flip();
            layered.Advance(0.13);
            Assert.True(layered.SliceFlipWidth(true, 3) > 0);
            Assert.True(layered.SliceFlipWidth(false, 0) > 0);
            var oldOnTop = PixelAt(layered, At(layered, Math.PI));
            layered.FinishAnimations();
            var newFace = PixelAt(layered, At(layered, Math.PI));
            Assert.True(oldOnTop[0] > oldOnTop[2], "The old blue slice must cover the new red ring");
            Assert.True(newFace[2] > newFace[0], "The new highlighted ring must be red at the same point");
        }
        finally { layeredWindow.Close(); }
        var (keyboard, keyboardWindow) = Wave(6, 1);
        try
        {
            keyboard.HandleKey(Key.Right);
            keyboard.HandleKey(Key.Right); // 60 degrees on the old face
            keyboard.Flip();
            Assert.Equal(Math.PI / 3, keyboard.FlipOrigin!.Value, 8);
            keyboard.Advance(0.13);
            Assert.Equal(0, keyboard.SliceFlipWidth(false, 0));
            keyboard.Advance(0.011);
            Assert.True(keyboard.SliceFlipWidth(false, 0) > 0);
        }
        finally { keyboardWindow.Close(); }
    }

    [AvaloniaFact]
    public void T4_moving_a_drag_retargets_the_old_and_new_slices()
    {
        var (donut, window) = Wave(6, 1);
        try
        {
            donut.SliceBrush = Avalonia.Media.Brushes.Blue;
            donut.AccentBrush = Avalonia.Media.Brushes.Red;
            donut.SliceOpacity = 1;
            donut.SliceBorderBrush = null;
            donut.SetExternalHit(donut.HitTest(At(donut, 0)), At(donut, 0));
            donut.FinishAnimations(); // the drag highlight is fully lit before the flip keeps it
            donut.Flip();
            Assert.Equal("back0", donut.Items![0].Id);
            Assert.True(donut.HasOldFace);
            donut.Advance(0.01);
            var paintAt = DonutGeometry.PointAt(new Point(donut.Bounds.Width / 2, donut.Bounds.Height / 2),
                donut.OuterRadius * 0.9, 0);
            var oldHighlight = PixelAt(donut, paintAt);
            donut.SetExternalHit(donut.HitTest(At(donut, Math.PI)), At(donut, Math.PI));
            var clearedHighlight = PixelAt(donut, paintAt);
            Assert.True(oldHighlight[2] > oldHighlight[0], $"The old highlighted slice starts red: {string.Join(',', oldHighlight)}");
            Assert.True(clearedHighlight[0] > clearedHighlight[2], $"Moving the drag clears the old red highlight: {string.Join(',', clearedHighlight)}");
            donut.Advance(0.061);
            Assert.Equal(0, donut.SliceFlipWidth(true, 3));
            Assert.True(donut.SliceFlipWidth(false, 0) > 0);
            Assert.True(donut.SliceFlipWidth(true, 1) > 0);
            var newHighlight = PixelAt(donut, DonutGeometry.PointAt(
                new Point(donut.Bounds.Width / 2, donut.Bounds.Height / 2), donut.OuterRadius * 0.9, Math.PI));
            Assert.True(newHighlight[2] > 50 && newHighlight[0] < 255,
                $"The new highlighted slice is visible under the drag: {string.Join(',', newHighlight)}");
        }
        finally { window.Close(); }
        var (pointer, pointerWindow) = Wave(6, 1);
        try
        {
            pointer.SliceBrush = Avalonia.Media.Brushes.Blue;
            pointer.AccentBrush = Avalonia.Media.Brushes.Red;
            pointer.SliceOpacity = 1;
            pointer.SliceBorderBrush = null;
            pointerWindow.MouseMove(At(pointer, 0));
            pointer.Flip();
            pointer.Advance(0.01);
            pointerWindow.MouseMove(At(pointer, Math.PI));
            pointer.Advance(0.061);
            Assert.Equal(0, pointer.SliceFlipWidth(true, 3));
            Assert.True(pointer.SliceFlipWidth(false, 0) > 0);
            var belowPointer = PixelAt(pointer, DonutGeometry.PointAt(
                new Point(pointer.Bounds.Width / 2, pointer.Bounds.Height / 2), pointer.OuterRadius * 0.9, Math.PI));
            Assert.True(belowPointer[2] > 50 && belowPointer[0] < 255,
                $"The pointed new ring is visible after the old slice folds: {string.Join(',', belowPointer)}");
        }
        finally { pointerWindow.Close(); }
        var (keyboard, keyboardWindow) = Wave();
        try
        {
            keyboard.Flip();
            keyboard.Advance(0.01);
            Assert.Equal(0, keyboard.SliceFlipWidth(false, 1));
            keyboard.HandleKey(Key.Right);
            keyboard.HandleKey(Key.Right);
            keyboard.Advance(0.001);
            Assert.True(keyboard.SliceFlipWidth(false, 1) > 0);
            keyboard.Advance(0.061);
            Assert.Equal(0, keyboard.SliceFlipWidth(true, 2));
        }
        finally { keyboardWindow.Close(); }
        With(animations: true, back: null, () =>
        {
            var (vm, live, control) = OpenClickMode();
            control.HandleKey(Key.Right);
            control.HandleKey(Key.Right);
            Assert.Equal(1, control.ActiveIndex);
            control.Flip();
            Assert.True(vm.IsBackFace);
            Assert.Equal(-1, control.ActiveIndex);
            Assert.Equal(AppHost.Get<ILocalizer>()["donut.close_hint"], vm.CenterSubtitle);
            live.MouseMove(control.TranslatePoint(At(control, 0), live)!.Value);
            Assert.Equal(0, control.ActiveIndex);
            Assert.Contains(vm.Items[0].Label, vm.CenterSubtitle);
            live.Close();
        });
    }

    [AvaloniaFact]
    public void Drag_entering_a_slice_on_the_same_spoke_pulls_the_wave_forward()
    {
        var (donut, window) = Wave(6, 1);
        try
        {
            donut.SetExternalHit(donut.HitTest(At(donut, 0)), At(donut, 0));
            donut.Flip();
            donut.Advance(0.01);
            var outside = DonutGeometry.PointAt(
                new Point(donut.Bounds.Width / 2, donut.Bounds.Height / 2), donut.OuterRadius + 40, Math.PI);
            donut.SetExternalHit(donut.HitTest(outside), outside);
            Assert.Equal(1, donut.SliceFlipWidth(true, 3));
            var inside = At(donut, Math.PI);
            donut.SetExternalHit(donut.HitTest(inside), inside);
            donut.Advance(0.061);
            Assert.Equal(0, donut.SliceFlipWidth(true, 3));
            Assert.True(donut.SliceFlipWidth(false, 0) > 0);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void T5_group_ring_is_retained_only_for_drawing()
    {
        var (donut, window) = Wave();
        try
        {
            donut.ReserveSubRing = true;
            Dispatcher.UIThread.RunJobs();
            donut.SliceBrush = Avalonia.Media.Brushes.Blue;
            donut.AccentBrush = Avalonia.Media.Brushes.Red;
            donut.SliceOpacity = 1;
            donut.SliceBorderBrush = null;
            donut.Items = [new DonutItem { Id = "group", Label = "Group",
                Children = [new DonutItem { Id = "child", Label = "Child" },
                    new DonutItem { Id = "other", Label = "Other" }] },
                .. donut.Items!.Skip(1)];
            donut.SetExternalHit(donut.HitTest(At(donut, 0)), At(donut, 0));
            donut.ExpandPendingGroup();
            donut.Advance(0.04);
            var sub = donut.SubSliceCenter(0, 0);
            Assert.Equal(DonutHitKind.SubSegment, donut.HitTest(sub).Kind);
            donut.SetExternalHit(donut.HitTest(sub), sub);
            var center = new Point(donut.Bounds.Width / 2, donut.Bounds.Height / 2);
            var sample = center + (sub - center) * ((donut.OuterRadius + 23) / (donut.OuterRadius + 36));
            donut.FinishAnimations();
            var highlighted = PixelAt(donut, sample);
            Assert.True(highlighted[2] - highlighted[0] >= 32, $"The selected sub-slice is red: {string.Join(',', highlighted)}");
            donut.Flip();
            Assert.True(donut.HasOldGroupRing);
            Assert.NotEqual(DonutHitKind.SubSegment, donut.HitTest(sub).Kind);
            var retained = PixelAt(donut, sample);
            Assert.All(Enumerable.Range(0, 4), i => Assert.InRange((int)retained[i] - highlighted[i], -2, 2));
            Assert.True(retained[2] - retained[0] >= 32, $"The snapshot retains the selected sub-slice highlight: {string.Join(',', retained)}");
            donut.Advance(0.061);
            Assert.Equal(0, donut.SliceFlipWidth(true, 0));
            Assert.Equal(0, AlphaAt(donut, sample));
            donut.Advance(DonutMenu.FlipDuration);
            Assert.False(donut.HasOldGroupRing);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void T6_finishing_cancelling_and_closing_release_old_drawings()
    {
        for (var scenario = 0; scenario < 4; scenario++)
        {
            var (donut, window) = Wave();
            try
            {
                donut.Flip();
                Assert.True(donut.HasOldFace);
                switch (scenario)
                {
                    case 0: Motion.Configure(true, () => false); donut.Advance(0); break;
                    case 1: donut.FinishAnimations(); break;
                    case 2: donut.Items = [new DonutItem { Id = "new", Label = "New" }]; break;
                    case 3: donut.PlayCloseAnimation(() => { }); break;
                }
                Assert.False(donut.HasOldFace);
                Assert.False(donut.HasOldGroupRing);
            }
            finally { window.Close(); Motion.Configure(false, () => false); }
        }
    }

    [AvaloniaFact]
    public void T7_editor_flip_in_starts_half_open_and_ends_in_point_fourteen_seconds()
    {
        TestServices.EnsureInitialized();
        Motion.Configure(false, () => false);
        var originalBack = Images.BackPresetIds?.ToList();
        using var main = new MainWindowViewModel(AppHost.Services, AppHost.Get<ILocalizer>(), AppHost.Get<UpdateService>());
        main.Navigate("toolbar");
        var window = new MainWindow { DataContext = main, Width = 1080, Height = 1000 };
        try
        {
            window.Show();
            var page = (ToolbarPageViewModel)main.CurrentPage!;
            page.SelectedProfile = page.Profiles.Single(p => p.Profile.Id == "images");
            RenderTests.Pump();
            Motion.Configure(false, () => false);
            var donut = window.GetVisualDescendants().OfType<DonutMenu>().Single();
            var faces = window.GetVisualDescendants().OfType<Avalonia.Controls.Border>()
                .Single(b => b.Name == "FaceSwitch").GetVisualDescendants().OfType<Avalonia.Controls.RadioButton>().ToList();
            faces[1].IsChecked = true;
            Assert.True(page.EditingBack);
            Assert.Equal(ImageBack, page.DonutItems.Select(i => i.Id));
            Assert.True(donut.IsFlipping);
            Assert.False(donut.HasOldFace);
            Assert.Equal(0, donut.FlipOrigin);
            Assert.True(donut.SliceFlipWidth(false, 0) > 0);
            Assert.Equal(0, donut.SliceFlipWidth(false, 2));
            donut.Advance(DonutMenu.FlipDuration - DonutMenu.SliceTurn / 2);
            Assert.False(donut.IsFlipping);
            Assert.Equal(1, donut.SliceFlipWidth(false, 2));
            donut.AccentBrush = Avalonia.Media.Brushes.Red;
            var button = DonutGeometry.PointAt(new Point(donut.Bounds.Width / 2, donut.Bounds.Height / 2),
                donut.OuterRadius - 14 + 7, 0);
            var buttonEdge = new Point(button.X + 8, button.Y);
            var beforeHover = PixelAt(donut, buttonEdge);
            window.MouseMove(donut.TranslatePoint(At(donut, 0), window)!.Value);
            var drawnButton = PixelAt(donut, buttonEdge);
            Assert.NotEqual(beforeHover, drawnButton);
            Assert.True(drawnButton[2] > drawnButton[0], "The edit button is drawn after the switch completes");
            var beforeFront = Images.PresetIds.ToList();
            var beforeBack = ImageBack;
            page.RemoveAt(page.SliceCount - 1);
            RenderTests.Pump();
            var chip = window.GetVisualDescendants().OfType<Avalonia.Controls.Border>()
                .Single(b => b.Classes.Contains("chip") && b.DataContext is PaletteChip { PresetId: "to-psd" });
            chip.BringIntoView();
            RenderTests.Pump();
            var from = chip.TranslatePoint(new Point(chip.Bounds.Width / 2, chip.Bounds.Height / 2), window)!.Value;
            var to = donut.TranslatePoint(new Point(donut.Bounds.Width / 2, donut.Bounds.Height / 2), window)!.Value;
            window.MouseDown(from, Avalonia.Input.MouseButton.Left);
            window.MouseMove(to);
            window.MouseUp(to, Avalonia.Input.MouseButton.Left);
            RenderTests.Pump();
            Assert.Contains("to-psd", Images.BackPresetIds!);
            Assert.Equal(beforeFront, Images.PresetIds);
            Assert.Equal(beforeBack.Count, Images.BackPresetIds!.Count);
            Motion.Configure(true, () => false);
            faces[0].IsChecked = true;
            Assert.False(donut.IsFlipping);
            Assert.Equal(1, donut.SliceFlipWidth(false, 0));
        }
        finally
        {
            window.Close();
            Images.BackPresetIds = originalBack;
            Store.SaveLibrary();
            Motion.Configure(false, () => false);
        }
    }

    [AvaloniaFact]
    public void T8_wedge_cache_does_not_grow_on_the_second_wave()
    {
        var (donut, window) = Wave();
        try
        {
            donut.Flip();
            using (var image = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height)))
                image.Render(window);
            donut.FinishAnimations();
            using (var image = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height)))
                image.Render(window);
            donut.Flip();
            donut.FinishAnimations();
            using (var image = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height)))
                image.Render(window);
            var cached = donut.CachedWedgeCount;
            donut.Flip();
            foreach (var dt in new[] { 0.02, 0.03, 0.04, 0.05, 0.06 })
            {
                donut.Advance(dt);
                using var image = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
                image.Render(window);
                Assert.Equal(cached, donut.CachedWedgeCount);
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Tab_flips_to_the_back_and_shift_tab_back_to_the_front_in_click_mode() => With(animations: true, back: null, () =>
    {
        var loc = AppHost.Get<ILocalizer>();
        var (vm, window, donut) = OpenClickMode();
        var front = vm.Items.Select(i => i.Id).ToList();
        Assert.True(vm.CanFlip);
        Assert.Equal(loc.Format("donut.flip_to_back", "Tab"), vm.FlipHint);
        Assert.Equal(vm.FlipHint, donut.CenterHint);

        Assert.True(donut.HandleKey(Key.Tab));
        Assert.True(donut.IsFlipping);
        Assert.True(vm.IsBackFace);
        Assert.Equal(ImageBack, vm.Items.Select(i => i.Id));
        donut.Advance(DonutMenu.FlipDuration * 0.2);
        Assert.InRange(donut.SliceFlipWidth(true, 0), 0.01, 0.99);

        donut.Advance(DonutMenu.FlipDuration * 0.3);
        Assert.True(vm.IsBackFace);
        Assert.Equal(ImageBack, vm.Items.Select(i => i.Id));
        Assert.Equal(loc.Format("donut.flip_to_front", "Tab"), donut.CenterHint);
        Assert.Equal(loc["donut.close_hint"], vm.CenterSubtitle);
        donut.Advance(DonutMenu.FlipDuration);
        Assert.False(donut.IsFlipping);
        Assert.Equal(1, donut.SliceFlipWidth(false, 0));
        Assert.False(donut.HasOldFace);

        Assert.True(donut.HandleKey(Key.Tab, KeyModifiers.Shift));
        donut.Advance(DonutMenu.FlipDuration);
        Assert.False(vm.IsBackFace);
        Assert.Equal(front, vm.Items.Select(i => i.Id));
        window.Close();
    });

    [AvaloniaFact]
    public void The_window_hands_tab_to_the_donut() => With(animations: false, back: null, () =>
    {
        var (vm, window, _) = OpenClickMode();
        window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsBackFace);
        Assert.True(window.IsVisible);
        window.Close();
    });

    [AvaloniaFact]
    public void Tab_pressed_again_during_a_flip_flips_once_more_after_it() => With(animations: true, back: null, () =>
    {
        var (vm, window, donut) = OpenClickMode();
        donut.HandleKey(Key.Tab);
        donut.Advance(0.02);
        donut.HandleKey(Key.Tab); // before the first turn ended
        for (var i = 0; i < 40; i++)
            donut.Advance(1 / 60.0);
        Assert.False(donut.IsFlipping);
        Assert.False(vm.IsBackFace); // turned over twice
        window.Close();
    });

    [AvaloniaFact]
    public void Arrows_still_move_between_slices_and_start_over_on_the_other_face() => With(animations: false, back: null, () =>
    {
        var (vm, window, donut) = OpenClickMode();
        Assert.True(donut.HandleKey(Key.Right));
        Assert.True(donut.HandleKey(Key.Right));
        Assert.Equal(1, donut.ActiveIndex);
        Assert.Contains(vm.Items[1].Label, vm.CenterSubtitle);

        Assert.True(donut.HandleKey(Key.Tab));
        Assert.True(vm.IsBackFace);
        Assert.Equal(-1, donut.ActiveIndex); // the selection starts over on the back
        Assert.Equal(AppHost.Get<ILocalizer>()["donut.close_hint"], vm.CenterSubtitle);

        Assert.True(donut.HandleKey(Key.Left)); // wraps to the last slice of the back
        Assert.Equal(vm.Items.Count - 1, donut.ActiveIndex);
        Assert.Equal(ImageBack[^1], vm.Items[donut.ActiveIndex].Id);
        Assert.Equal(ImageBack[^1], vm.PresetAt(donut.ActiveIndex, -1)!.Id); // what Enter converts with
        window.Close();
    });

    [AvaloniaFact]
    public void With_animations_off_the_faces_swap_at_once() => With(animations: false, back: null, () =>
    {
        var (vm, window, donut) = OpenClickMode();
        Assert.True(donut.Flip());
        Assert.False(donut.IsFlipping);
        Assert.False(donut.HasOldFace);
        Assert.Equal(1, donut.SliceFlipWidth(false, 0));
        Assert.True(vm.IsBackFace);
        Assert.False(donut.Advance(0)); // nothing left to animate
        window.Close();
    });

    [AvaloniaFact]
    public void A_donut_without_a_back_face_does_not_flip() => With(animations: true, back: [], () =>
    {
        var (vm, window, donut) = OpenClickMode();
        Assert.False(vm.CanFlip);
        Assert.Null(vm.FlipHint);
        Assert.False(donut.CanFlip);

        Assert.True(donut.HandleKey(Key.Tab)); // still Tab's: it doesn't move the focus off the donut
        Assert.False(donut.IsFlipping);
        Assert.False(donut.Flip());
        Assert.False(vm.IsBackFace);
        Assert.False(vm.ToggleFace());
        window.Close();
    });

    [AvaloniaFact]
    public void The_back_face_greys_out_what_cannot_convert_the_files_and_one_with_nothing_left_does_not_flip()
    {
        var group = new Preset
        {
            Id = "flip-test-sounds",
            Name = "Sounds",
            Kind = PresetKind.Group,
            TargetFormat = Preset.GroupTarget,
            SubPresetIds = ["to-wav", "to-flac"],
        };
        With(animations: false, back: ["to-wav", "flip-test-sounds"], () =>
        {
            Store.Presets.Add(group);
            try
            {
                // Pictures can't become sounds: nothing on the back is for them, so there is nothing to flip to.
                var (vm, window, donut) = OpenClickMode();
                Assert.False(vm.CanFlip);
                Assert.Null(vm.FlipHint);
                Assert.False(donut.Flip());
                window.Close();

                // One slice for them is enough; the others are greyed out with the reason, as on the front.
                Images.BackPresetIds = ["to-wav", "flip-test-sounds", "to-gif"];
                (vm, window, donut) = OpenClickMode();
                Assert.True(vm.CanFlip);
                Assert.True(donut.Flip());
                Assert.Equal(["to-wav", "flip-test-sounds", "to-gif"], vm.Items.Select(i => i.Id));
                Assert.False(vm.Items[0].IsEnabled);
                Assert.NotNull(vm.Items[0].DisabledReason);
                Assert.True(vm.Items[1].IsGroup);
                Assert.False(vm.Items[1].IsEnabled); // every slice of its ring is greyed out
                Assert.All(vm.Items[1].Children!, child => Assert.False(child.IsEnabled));
                Assert.True(vm.Items[2].IsEnabled);
                Assert.Null(vm.PresetAt(0, -1));
                Assert.Equal("to-gif", vm.PresetAt(2, -1)!.Id);
                window.Close();
            }
            finally
            {
                Store.Presets.Remove(group);
            }
        });
    }

    [AvaloniaFact]
    public void An_open_group_ring_folds_before_the_donut_turns() => With(animations: true, back: null, () =>
    {
        Images.PresetIds = [.. Images.PresetIds, "rotate"];
        var (vm, window, donut) = OpenClickMode();
        Assert.True(vm.ReserveSubRing);
        var group = vm.Items.Count - 1;
        donut.SetExternalHit(new DonutHit(DonutHitKind.Segment, group));
        donut.ExpandPendingGroup();
        var sub = donut.SubSliceCenter(group, 1);
        Assert.Equal(DonutHitKind.SubSegment, donut.HitTest(sub).Kind);
        donut.Advance(0.04);

        donut.HandleKey(Key.Tab);
        Assert.NotEqual(DonutHitKind.SubSegment, donut.HitTest(sub).Kind);
        Assert.True(vm.IsBackFace); // only the drawing keeps the old ring
        Assert.True(donut.HasOldGroupRing);
        donut.Advance(DonutMenu.FlipDuration);
        Assert.True(vm.IsBackFace);
        Assert.False(donut.HasOldGroupRing);
        window.Close();
    });

    [AvaloniaFact]
    public void A_drag_resting_on_a_slice_lights_up_the_slice_there_on_the_other_face() => With(animations: false, back: null, () =>
    {
        var vm = AppHost.Get<RadialViewModel>();
        vm.ResetForDrag();
        var window = new RadialWindow { DataContext = vm };
        HintLikeTheController(vm, window.DonutControl);
        window.Show();
        vm.Load(Photos, clickMode: false);
        Dispatcher.UIThread.RunJobs();
        var donut = window.DonutControl;
        var n = vm.Items.Count;
        var center = new Point(donut.Bounds.Width / 2, donut.Bounds.Height / 2);
        var at = DonutGeometry.PointAt(center, vm.OuterRadius * (1 + vm.HoleRatio) / 2, DonutGeometry.MidAngle(5, n));
        donut.SetExternalHit(donut.HitTest(at), at);
        Assert.Equal(5, donut.ActiveIndex);

        Assert.True(donut.Flip());

        var back = vm.Items.Count;
        var expected = DonutGeometry.HitTest(center, at, vm.OuterRadius * vm.HoleRatio, vm.OuterRadius, back, 7).Index;
        Assert.Equal(expected, donut.ActiveIndex);
        // What that slice does with these files, or why it can't (an engine to download).
        var there = vm.Items[expected];
        Assert.Equal(there.IsEnabled ? AppHost.Get<ILocalizer>().Format("donut.drop_to", there.Label) : there.DisabledReason, vm.CenterSubtitle);
        window.Close();
    });

    /// <summary>
    /// Drag mode: the donut's window has no keyboard focus during the OS drag, so Tab comes from the global hook (here a
    /// stand-in for libuiohook): it flips the donut while files are on it, and is left alone once the donut is gone.
    /// </summary>
    [AvaloniaFact]
    public void In_drag_mode_tab_from_the_global_hook_flips_the_donut() => With(animations: false, back: null, () =>
    {
        var provider = new TriggerServiceLifecycleTests.DelayedProvider();
        provider.AllowExit.Set();
        using var triggers = TriggerServiceLifecycleTests.Create(provider, () => { });
        triggers.Start();
        Assert.True(SpinWait.SpinUntil(() => provider.Runs == 1, 2000));
        var vm = AppHost.Get<RadialViewModel>();
        var radial = new RadialController(vm, AppHost.Get<ConversionService>(), AppHost.Get<WindowService>(), Store,
            AppHost.Get<IPlatformServices>(), triggers, NullLogger<RadialController>.Instance);
        var folder = Directory.CreateTempSubdirectory("filee-flip-");
        try
        {
            var file = Path.Combine(folder.FullName, "photo.jpg");
            File.WriteAllBytes(file, [0xFF, 0xD8, 0xFF]);

            radial.ShowForDrag(400, 400);
            var window = radial.Window!;
            provider.PostKey(EventType.KeyPressed, KeyCode.VcTab); // before files: nothing to flip, not even watched
            provider.PostKey(EventType.KeyReleased, KeyCode.VcTab);
            Assert.False(TriggerServiceLifecycleTests.Settle(() => triggers.WatchFlipKey));

            var data = new DataTransfer();
            var item = window.StorageProvider.TryGetFileFromPathAsync(file).GetAwaiter().GetResult();
            Assert.NotNull(item);
            data.Add(DataTransferItem.CreateFile(item));
            var at = new Point(window.Bounds.Width / 2 + 60, window.Bounds.Height / 2);
            window.DragDrop(at, RawDragEventType.DragEnter, data, DragDropEffects.Copy, RawInputModifiers.Control);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(new[] { file }, vm.Files);
            Assert.False(vm.IsBackFace);
            Assert.True(triggers.WatchFlipKey);

            provider.PostKey(EventType.KeyPressed, KeyCode.VcTab, EventMask.LeftCtrl);
            // #504: macOS can hand the donut's window the same press as well; it must not flip a second time.
            window.KeyPress(Key.Tab, RawInputModifiers.Control, PhysicalKey.Tab, null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(TriggerServiceLifecycleTests.Settle(() => vm.IsBackFace));
            Assert.Equal(ImageBack, vm.Items.Select(i => i.Id));
            provider.PostKey(EventType.KeyReleased, KeyCode.VcTab, EventMask.LeftCtrl);
            provider.PostKey(EventType.KeyPressed, KeyCode.VcTab, EventMask.LeftCtrl);
            Assert.False(TriggerServiceLifecycleTests.Settle(() => vm.IsBackFace));

            // The drag moved on: Tab is the user's again.
            radial.Close(animate: false);
            Assert.False(triggers.WatchFlipKey);
            provider.PostKey(EventType.KeyReleased, KeyCode.VcTab);
            provider.PostKey(EventType.KeyPressed, KeyCode.VcTab);
            Assert.False(TriggerServiceLifecycleTests.Settle(() => vm.IsBackFace));
            window.Close();
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    });

    /// <summary>Another flip key from Settings → Shortcuts: it flips, Tab no longer does, and the hub names it.</summary>
    [AvaloniaFact]
    public void A_flip_key_from_the_settings_flips_in_click_mode_and_tab_no_longer_does() =>
        With(animations: false, back: null, flipKey: "F2", test: () =>
        {
            var loc = AppHost.Get<ILocalizer>();
            var (vm, window, donut) = OpenClickMode();
            Assert.Equal("F2", donut.FlipKey);
            Assert.Equal(loc.Format("donut.flip_to_back", "F2"), vm.FlipHint);
            Assert.False(vm.FlipHintWheel);

            Assert.False(donut.HandleKey(Key.Tab)); // the window's again
            Assert.False(vm.IsBackFace);
            Assert.True(donut.HandleKey(Key.F2, KeyModifiers.Shift));
            Assert.True(vm.IsBackFace);
            Assert.Equal(loc.Format("donut.flip_to_front", "F2"), vm.FlipHint);
            window.Close();
        });

    /// <summary>No key, the wheel on: the hub shows a mouse; neither: no hint and the donut doesn't flip by input.</summary>
    [AvaloniaFact]
    public void The_hub_shows_a_mouse_when_only_the_wheel_flips_and_nothing_when_nothing_does() =>
        With(animations: false, back: null, flipKey: "", wheel: true, test: () =>
        {
            var (vm, window, donut) = OpenClickMode();
            Assert.Null(donut.FlipKey);
            Assert.True(vm.FlipHintWheel);
            Assert.Equal(AppHost.Get<ILocalizer>().Format("donut.flip_to_back", "").Trim(), vm.FlipHint);
            Assert.False(donut.HandleKey(Key.Tab));
            RenderTests.Pump();
            RenderTests.Save(window, "donut-front-hint-wheel-en.png");
            window.Close();

            Store.Settings.Donut.FlipWithWheel = false;
            (vm, window, donut) = OpenClickMode();
            Assert.True(vm.CanFlip);
            Assert.Null(vm.FlipHint);
            Assert.False(vm.FlipHintWheel);
            Assert.False(donut.FlipWithWheel);
            window.MouseWheel(new Point(window.Bounds.Width / 2, window.Bounds.Height / 2), new Vector(0, -1));
            Dispatcher.UIThread.RunJobs();
            Assert.False(vm.IsBackFace);
            window.Close();
        });

    /// <summary>
    /// One flip per turn of the wheel: the notches of one turn (and a touchpad's many small steps) come close together,
    /// and a fast scroll mustn't flip back and forth. A pause starts a new turn.
    /// </summary>
    [AvaloniaFact]
    public void The_wheel_flips_once_per_turn() => With(animations: false, back: null, test: () =>
    {
        var (vm, window, donut) = OpenClickMode();
        Assert.True(donut.FlipWithWheel);
        Assert.True(donut.FlipFromWheel(10_000));
        Assert.True(vm.IsBackFace);
        Assert.False(donut.FlipFromWheel(10_040)); // the same turn
        Assert.False(donut.FlipFromWheel(10_200));
        Assert.False(donut.FlipFromWheel(10_450)); // still: 250 ms after the last notch
        Assert.True(vm.IsBackFace);
        Assert.True(donut.FlipFromWheel(10_450 + DonutMenu.WheelQuietMs)); // a new turn after a pause
        Assert.False(vm.IsBackFace);

        // Click mode: the wheel over the donut, as the window gets it.
        var over = new Point(window.Bounds.Width / 2, window.Bounds.Height / 2);
        Thread.Sleep((int)DonutMenu.WheelQuietMs + 50); // a new turn by the clock the window's wheel uses
        window.MouseWheel(over, new Vector(0, -1));
        window.MouseWheel(over, new Vector(0, -1));
        window.MouseWheel(over, new Vector(0, 1));
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsBackFace);
        window.Close();
    });

    /// <summary>Drag mode: the wheel and another flip key come from the global hook while the drag donut shows files.</summary>
    [AvaloniaFact]
    public void In_drag_mode_the_wheel_and_the_set_key_from_the_global_hook_flip_the_donut() =>
        With(animations: false, back: null, flipKey: "F2", test: () =>
        {
            var provider = new TriggerServiceLifecycleTests.DelayedProvider();
            provider.AllowExit.Set();
            using var triggers = TriggerServiceLifecycleTests.Create(provider, () => { });
            triggers.Apply(Store.Settings);
            triggers.Start();
            Assert.True(SpinWait.SpinUntil(() => provider.Runs == 1, 2000));
            var vm = AppHost.Get<RadialViewModel>();
            var radial = new RadialController(vm, AppHost.Get<ConversionService>(), AppHost.Get<WindowService>(), Store,
                AppHost.Get<IPlatformServices>(), triggers, NullLogger<RadialController>.Instance);
            var folder = Directory.CreateTempSubdirectory("filee-flip-");
            try
            {
                var file = Path.Combine(folder.FullName, "photo.jpg");
                File.WriteAllBytes(file, [0xFF, 0xD8, 0xFF]);
                radial.ShowForDrag(400, 400);
                var window = radial.Window!;
                RenderTests.Pump(); // laid out: the drag enters where the donut is
                var data = new DataTransfer();
                data.Add(DataTransferItem.CreateFile(window.StorageProvider.TryGetFileFromPathAsync(file).GetAwaiter().GetResult()!));
                window.DragDrop(new Point(window.Bounds.Width / 2 + 60, window.Bounds.Height / 2), RawDragEventType.DragEnter, data,
                    DragDropEffects.Copy, RawInputModifiers.Control);
                Dispatcher.UIThread.RunJobs();
                Assert.True(triggers.WatchFlipKey);

                // Tab isn't the flip key now; F2 is.
                provider.PostKey(EventType.KeyPressed, KeyCode.VcTab, EventMask.LeftCtrl);
                Assert.False(TriggerServiceLifecycleTests.Settle(() => vm.IsBackFace));
                provider.PostKey(EventType.KeyPressed, KeyCode.VcF2, EventMask.LeftCtrl);
                Assert.True(TriggerServiceLifecycleTests.Settle(() => vm.IsBackFace));
                provider.PostKey(EventType.KeyReleased, KeyCode.VcF2, EventMask.LeftCtrl);

                // A fast turn of the wheel, many notches: one flip. Sideways scrolling doesn't flip.
                Thread.Sleep((int)DonutMenu.WheelQuietMs + 50);
                for (var i = 0; i < 6; i++)
                    provider.PostWheel(-120);
                Assert.False(TriggerServiceLifecycleTests.Settle(() => vm.IsBackFace));
                Thread.Sleep((int)DonutMenu.WheelQuietMs + 50);
                provider.PostWheel(120, MouseWheelScrollDirection.Horizontal);
                Assert.False(TriggerServiceLifecycleTests.Settle(() => vm.IsBackFace));
                Thread.Sleep((int)DonutMenu.WheelQuietMs + 50);
                provider.PostWheel(120);
                Assert.True(TriggerServiceLifecycleTests.Settle(() => vm.IsBackFace));

                // The wheel switched off in the settings: it is left alone.
                Store.Settings.Donut.FlipWithWheel = false;
                triggers.Apply(Store.Settings);
                Thread.Sleep((int)DonutMenu.WheelQuietMs + 50);
                provider.PostWheel(-120);
                Assert.True(TriggerServiceLifecycleTests.Settle(() => vm.IsBackFace));

                radial.Close(animate: false);
                window.Close();
            }
            finally
            {
                folder.Delete(recursive: true);
            }
        });

    /// <summary>
    /// Settings → Shortcuts records the flip key: modifiers alone wait for a key; Esc, Enter, Space, the arrows and the
    /// key of a keyboard shortcut are refused with the reason; another key is saved. "Use Tab", "Clear" and the wheel
    /// switch are saved too.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("en", false)]
    [InlineData("ko", false)]
    [InlineData("ja", true)]
    [InlineData("ru", false)]
    [InlineData("ar", true)]
    public void The_flip_key_is_recorded_and_checked_in_the_shortcut_settings(string language, bool dark) =>
        With(animations: false, back: null, language: language, test: () =>
        {
            var loc = AppHost.Get<ILocalizer>();
            var theme = AppHost.Get<ThemeService>();
            var triggersBefore = Store.Settings.Triggers.Select(t => t.Clone()).ToList();
            Avalonia.Controls.Window? window = null;
            try
            {
                Store.Settings.Triggers.Add(new TriggerGesture { Kind = TriggerKind.KeyChord, Modifiers = Filee.Core.Settings.ModifierKeys.Ctrl, Key = "K" });
                if (dark)
                    theme.Apply(new ThemeSettings { Mode = ThemeMode.Dark }, language);
                var page = new TriggersPageViewModel(Store, loc);
                var flip = page.Flip;
                Assert.Equal("Tab", flip.KeyText);
                Assert.True(flip.FlipWithWheel);

                flip.IsRecording = true;
                Assert.Equal(loc["triggers.recording_key"], flip.KeyText);
                Assert.False(flip.Record(Key.LeftCtrl)); // waits for a real key
                Assert.True(flip.IsRecording);
                foreach (var refused in new[] { Key.Escape, Key.Enter, Key.Space, Key.Left, Key.K })
                {
                    flip.IsRecording = true;
                    Assert.True(flip.Record(refused));
                    Assert.False(flip.IsRecording);
                    Assert.NotNull(flip.Error);
                    Assert.Equal("Tab", Store.Settings.Donut.FlipKey);
                }
                Assert.Equal(loc.Format("triggers.flip_key_taken", "K"), flip.Error);

                window = new Avalonia.Controls.Window
                {
                    Content = new Filee.App.Views.Pages.TriggersPage { DataContext = page },
                    Width = 820,
                    Height = 1500,
                };
                window.Show();
                RenderTests.Pump();
                Assert.Single(window.GetVisualDescendants().OfType<Avalonia.Controls.Border>(), b => b.Name == "FlipCard");
                RenderTests.Save(window, $"triggers-flip-refused-{language}{(dark ? "-dark" : "")}.png");

                // Recorded on the button itself, as the user does it.
                var recorder = window.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().Single(b => b.Name == "FlipRecorder");
                flip.IsRecording = true;
                recorder.Focus();
                window.KeyPress(Key.F2, RawInputModifiers.Control, PhysicalKey.F2, null);
                Dispatcher.UIThread.RunJobs();
                Assert.False(flip.IsRecording);
                Assert.Null(flip.Error);
                Assert.Equal("F2", Store.Settings.Donut.FlipKey);
                Assert.Equal("F2", flip.KeyText);
                Assert.Equal("F2", new UserDataStoreProbe(TestServices.DataDirectory).FlipKey); // saved
                RenderTests.Pump();
                RenderTests.Save(window, $"triggers-flip-{language}{(dark ? "-dark" : "")}.png");

                var wheel = window.GetVisualDescendants().OfType<Avalonia.Controls.ToggleSwitch>().Single(t => t.Name == "FlipWheel");
                wheel.IsChecked = false;
                Assert.False(Store.Settings.Donut.FlipWithWheel);
                flip.ClearKeyCommand.Execute(null);
                Assert.Equal("", Store.Settings.Donut.FlipKey);
                Assert.Equal("—", flip.KeyText);
                flip.ResetKeyCommand.Execute(null);
                Assert.Equal("Tab", Store.Settings.Donut.FlipKey);
            }
            finally
            {
                window?.Close();
                if (dark)
                    theme.Apply(new ThemeSettings(), language);
                Store.Settings.Triggers.Clear();
                Store.Settings.Triggers.AddRange(triggersBefore);
                Store.Settings.Donut.FlipWithWheel = true;
                Store.SaveSettings();
            }
        });

    /// <summary>The settings as saved on disk, read by a second store.</summary>
    private sealed class UserDataStoreProbe(string directory)
    {
        public string FlipKey { get; } = Load(directory).Settings.Donut.FlipKey;

        private static UserDataStore Load(string directory)
        {
            var store = new UserDataStore(directory);
            store.Load();
            return store;
        }
    }

    /// <summary>The image donut's back face, light and dark, and the ring halfway through its turn.</summary>
    [AvaloniaTheory]
    [InlineData("en", false)]
    [InlineData("ko", false)]
    [InlineData("ko", true)]
    [InlineData("zh-CN", false)]
    [InlineData("ja", false)]
    [InlineData("ru", false)]
    [InlineData("ar", true)]
    [InlineData("pt-BR", false)]
    [InlineData("fr", false)]
    public void The_back_face_renders(string language, bool dark) => With(animations: true, back: null, () =>
    {
        var theme = AppHost.Get<ThemeService>();
        try
        {
            if (dark)
                theme.Apply(new ThemeSettings { Mode = ThemeMode.Dark, CornerRadius = 16 }, language);
            // Applying a theme follows the system's animation setting again, and build servers report it off.
            Motion.Configure(false, () => false);
            var (vm, window, donut) = OpenClickMode(@"C:\photos\a.jpg", @"C:\photos\b.jpg", @"C:\photos\c.jpg");
            var suffix = $"{language}{(dark ? "-dark" : "")}";
            donut.FinishAnimations();
            RenderTests.Pump();
            RenderTests.Save(window, $"donut-front-hint-{suffix}.png");

            // Drawn right here, not by the render loop: that would play the rest of the turn first.
            donut.HandleKey(Key.Tab);
            donut.Advance(DonutMenu.FlipDuration * 0.3);
            Assert.InRange(donut.SliceFlipWidth(false, 0), 0.01, 0.99);
            var size = new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height);
            using (var turning = new Avalonia.Media.Imaging.RenderTargetBitmap(size))
            {
                turning.Render(window);
                using var png = File.Create(Path.Combine(TestServices.ScreenshotDirectory, $"donut-flip-midway-{suffix}.png"));
                turning.Save(png, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }

            donut.FinishAnimations();
            Assert.True(vm.IsBackFace);
            donut.SetExternalHighlight(2);
            RenderTests.Pump();
            Assert.Equal(1, donut.SliceFlipWidth(false, 0));
            RenderTests.Save(window, $"donut-back-{suffix}.png");
            window.Close();
        }
        finally
        {
            if (dark)
                theme.Apply(new ThemeSettings(), language);
        }
    }, language);

    /// <summary>The PDF back face exposes Edit as a slice, then fans its six tools out around that slice.</summary>
    [AvaloniaFact]
    public void The_pdf_edit_group_renders_as_a_subring() => With(animations: false, back: null, () =>
    {
        var theme = AppHost.Get<ThemeService>();
        try
        {
            theme.Apply(new ThemeSettings { Mode = ThemeMode.Dark, CornerRadius = 16 }, "en");
            Motion.Configure(true, () => false);
            var (vm, window, donut) = OpenClickMode(@"C:\docs\report.pdf");

            Assert.True(donut.HandleKey(Key.Tab));
            Assert.True(vm.IsBackFace);
            Assert.Equal("pdf-edit", vm.Items[0].Id);
            Assert.Equal(6, vm.Items[0].Children!.Count);

            donut.SetExternalHit(new DonutHit(DonutHitKind.Segment, 0));
            donut.ExpandPendingGroup();
            donut.SetExternalHit(new DonutHit(DonutHitKind.SubSegment, 0, 3));
            RenderTests.Pump();
            RenderTests.Save(window, "donut-pdf-edit-en.png");

            Assert.Equal(DonutHitKind.SubSegment, donut.HitTest(donut.SubSliceCenter(0, 3)).Kind);
            window.Close();
        }
        finally
        {
            theme.Apply(new ThemeSettings(), "en");
        }
    });

    /// <summary>
    /// The editor's Front / Back switch: the donut shows and edits the back face, the front stays as it was, and
    /// "Reset to default" puts the built-in back face back.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("en", false)]
    [InlineData("ko", false)]
    [InlineData("ko", true)]
    [InlineData("zh-CN", false)]
    [InlineData("ja", false)]
    [InlineData("ru", false)]
    [InlineData("ar", true)]
    public void The_editor_switches_between_the_front_and_the_back(string language, bool dark) => With(animations: false, back: null, () =>
    {
        var theme = AppHost.Get<ThemeService>();
        var snapshot = Store.Profiles.Select(p => p.Clone()).ToList();
        Avalonia.Controls.Window? window = null;
        try
        {
            if (dark)
                theme.Apply(new ThemeSettings { Mode = ThemeMode.Dark }, language);
            using var main = new MainWindowViewModel(AppHost.Services, AppHost.Get<ILocalizer>(), AppHost.Get<UpdateService>());
            main.Navigate("toolbar");
            window = new MainWindow { DataContext = main, Width = 1080, Height = 1000 };
            window.Show();
            var page = (ToolbarPageViewModel)main.CurrentPage!;
            page.SelectedProfile = page.Profiles.Single(p => p.Profile.Id == "images");
            RenderTests.Pump();
            var front = Images.PresetIds.ToList();
            var switcher = window.GetVisualDescendants().OfType<Avalonia.Controls.Border>().Single(b => b.Name == "FaceSwitch");
            var faces = switcher.GetVisualDescendants().OfType<Avalonia.Controls.RadioButton>().ToList();
            Assert.Equal(2, faces.Count);
            Assert.True(faces[0].IsChecked);
            Assert.Equal(front, page.DonutItems.Select(i => i.Id));

            // The switch shows the back face, and every edit goes there.
            faces[1].IsChecked = true;
            RenderTests.Pump();
            Assert.True(page.EditingBack);
            Assert.False(faces[0].IsChecked);
            Assert.Equal(ImageBack, page.DonutItems.Select(i => i.Id));
            Assert.Equal(ImageBack.Count, page.BackCount);
            Assert.DoesNotContain(page.Palette, chip => ImageBack.Contains(chip.PresetId));
            Assert.DoesNotContain(page.Palette, chip => front.Contains(chip.PresetId)); // on the front, so not offered for the back (#266)
            Assert.Contains(AppHost.Get<ILocalizer>()["toolbar.face_back"], page.CenterSubtitle);
            RenderTests.Save(window, $"toolbar-back-face-{language}{(dark ? "-dark" : "")}.png");

            var back = ImageBack;
            // The built-in back face is full (settings schema 37): a slice goes before another comes in.
            page.RemoveAt(page.SliceCount - 1);
            Assert.True(page.Insert("to-psd", 0));
            page.Move(0, 2);
            List<string> edited = [.. back[..2], "to-psd", .. back[2..^1]];
            Assert.Equal(edited, Images.BackPresetIds);
            Assert.Equal(front, Images.PresetIds); // the front as it was
            Assert.Equal(edited, page.DonutItems.Select(i => i.Id));

            page.Reset(page.SelectedProfile!);
            Assert.Equal(BuiltInData.CreateProfiles().Single(p => p.Id == "images").BackPresetIds, Images.BackPresetIds);
            Assert.Equal(Images.BackPresetIds, page.DonutItems.Select(i => i.Id)); // still on the back

            faces[0].IsChecked = true;
            RenderTests.Pump();
            Assert.False(page.EditingBack);
            Assert.Equal(front, page.DonutItems.Select(i => i.Id));
        }
        finally
        {
            window?.Close();
            if (dark)
                theme.Apply(new ThemeSettings(), language);
            Store.Profiles.Clear();
            Store.Profiles.AddRange(snapshot);
            Store.SaveLibrary();
        }
    }, language);
}
