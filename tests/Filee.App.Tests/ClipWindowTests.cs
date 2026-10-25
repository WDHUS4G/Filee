// The "Cut a clip" window: the clip a video opens with, the handles moved by the timeline, the keys and the quick
// lengths, the large picture of the active handle, and the conversion that Save starts. Frames come from a stand-in
// for FFmpeg that paints a picture for each time, so these tests run without it (RenderTests has its screenshots).

using System.Collections.Concurrent;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Filee.App.Controls;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.Views;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Core.Settings;
using Filee.Engines.Media;
using ImageMagick;
using ImageMagick.Drawing;
using static Filee.App.Tests.RenderTests;

namespace Filee.App.Tests;

public class ClipWindowTests
{
    /// <summary>A 5:12.4 video, as long as the highlights the tool is for come from.</summary>
    private const double Length = 312.4;

    [AvaloniaFact]
    public void A_video_opens_on_its_first_30_seconds_with_the_timeline_filled()
    {
        WithClipWindow("en", false, ["match.mp4"], (window, vm, frames, _) =>
        {
            Assert.Equal((0.0, 30.0), (vm.Start, vm.End));
            Assert.Equal(("0:00.0", "0:30.0", "0:30.0", "5:12.4"), (vm.StartText, vm.EndText, vm.LengthText, vm.DurationText));
            Assert.Equal(ClipHandle.Start, vm.ActiveHandle);
            Assert.Equal("Start 0:00.0", vm.PreviewLabel);
            Assert.Equal(ClipWindowViewModel.ThumbnailCount, vm.Thumbnails.Count);
            Assert.All(vm.Thumbnails, Assert.NotNull);
            Assert.Same(vm.Thumbnails, window.ClipTimeline.Thumbnails);
            // The large picture is the start's own frame, larger than a timeline picture.
            Assert.Contains(0.0, frames.Times);
            Assert.True(vm.Preview!.PixelSize.Height > vm.Thumbnails[0]!.PixelSize.Height);
            Assert.False(vm.HasMultipleFiles);
            Assert.Equal(AppHost.Get<ILocalizer>()["clip.save"], vm.SaveText);
            Assert.True(vm.IsFast);
        });
    }

    [AvaloniaFact]
    public void A_short_video_opens_whole_and_offers_only_the_lengths_it_has()
    {
        WithClipWindow("en", false, ["short.mp4"], (_, vm, _, _) =>
        {
            Assert.Equal((0.0, 20.0), (vm.Start, vm.End));
            Assert.True(vm.Can15);
            Assert.False(vm.Can30);
            Assert.False(vm.Can60);
        }, duration: 20);
    }

    [AvaloniaFact]
    public void Handles_stay_inside_the_video_and_in_order()
    {
        WithClipWindow("en", false, ["match.mp4"], (_, vm, _, _) =>
        {
            vm.MoveHandle(ClipHandle.End, 999);
            Assert.Equal(Length, vm.End);
            Assert.Equal(ClipHandle.End, vm.ActiveHandle);

            vm.MoveHandle(ClipHandle.Start, 400);
            Assert.Equal(Length - ClipRange.MinLength, vm.Start, 6);
            Assert.Equal(ClipHandle.Start, vm.ActiveHandle);

            vm.MoveHandle(ClipHandle.Start, -10);
            Assert.Equal(0, vm.Start);
            vm.MoveHandle(ClipHandle.End, 0);
            Assert.Equal(ClipRange.MinLength, vm.End, 6);

            // The whole clip moves without changing length, and stops at the ends.
            vm.MoveHandle(ClipHandle.End, 20);
            vm.MoveClip(300);
            Assert.Equal((Length - 20, Length), (vm.Start, vm.End));
            vm.MoveClip(-5);
            Assert.Equal((0.0, 20.0), (vm.Start, vm.End));
        });
    }

    [AvaloniaFact]
    public void A_handle_moved_past_the_other_takes_the_clip_along()
    {
        WithClipWindow("en", false, ["match.mp4"], (_, vm, _, _) =>
        {
            // Put the start on the moment: the 30-second clip follows.
            vm.MoveHandle(ClipHandle.Start, 100);
            Assert.Equal((100.0, 130.0), (vm.Start, vm.End));
            // Within the clip, only that end moves.
            vm.MoveHandle(ClipHandle.Start, 110);
            Assert.Equal((110.0, 130.0), (vm.Start, vm.End));
            // The end before the start: the start goes back with it.
            vm.MoveHandle(ClipHandle.End, 50);
            Assert.Equal((30.0, 50.0), (vm.Start, vm.End));
            // As far as the video goes.
            vm.MoveHandle(ClipHandle.Start, 305);
            Assert.Equal((305.0, Length), (vm.Start, vm.End));
            vm.MoveHandle(ClipHandle.End, 3);
            Assert.Equal((0.0, 3.0), (vm.Start, vm.End));
        });
    }

    [AvaloniaFact]
    public void Quick_lengths_run_from_the_start_or_end_with_the_video()
    {
        WithClipWindow("en", false, ["match.mp4"], (_, vm, _, _) =>
        {
            vm.MoveHandle(ClipHandle.Start, 100);
            vm.QuickLengthCommand.Execute("60");
            Assert.Equal((100.0, 160.0), (vm.Start, vm.End));
            Assert.Equal(ClipHandle.End, vm.ActiveHandle); // to see where it now ends
            vm.QuickLengthCommand.Execute("15");
            Assert.Equal((100.0, 115.0), (vm.Start, vm.End));

            // Near the end: the video's last 60 seconds.
            vm.MoveHandle(ClipHandle.End, Length);
            vm.MoveHandle(ClipHandle.Start, 300);
            vm.SetLength(60);
            Assert.Equal(Length - 60, vm.Start, 6);
            Assert.Equal(Length, vm.End);
            Assert.Equal("1:00.0", vm.LengthText);
        });
    }

    [AvaloniaFact]
    public void Arrow_keys_move_the_active_handle_and_s_and_e_pick_it()
    {
        WithClipWindow("en", false, ["match.mp4"], (window, vm, _, _) =>
        {
            Press(window, Key.Right);
            Assert.Equal(0.1, vm.Start, 6);
            Press(window, Key.Right, RawInputModifiers.Shift);
            Assert.Equal(1.1, vm.Start, 6);
            Press(window, Key.Left);
            Assert.Equal(1.0, vm.Start, 6);

            Press(window, Key.E);
            Assert.Equal(ClipHandle.End, vm.ActiveHandle);
            Assert.True(vm.IsEndActive);
            Press(window, Key.Left, RawInputModifiers.Shift);
            Assert.Equal(29.0, vm.End, 6);
            Assert.Equal("End 0:29.0", vm.PreviewLabel);

            Press(window, Key.S);
            Assert.Equal(ClipHandle.Start, vm.ActiveHandle);
            // Ctrl + arrow is no nudge.
            Press(window, Key.Right, RawInputModifiers.Control);
            Assert.Equal(1.0, vm.Start, 6);
        });
    }

    [AvaloniaFact]
    public void Right_to_left_languages_keep_time_running_left_to_right()
    {
        WithClipWindow("ar", false, ["match.mp4"], (window, vm, _, _) =>
        {
            Assert.Equal(FlowDirection.RightToLeft, window.FlowDirection);
            Assert.Equal(FlowDirection.LeftToRight, window.ClipTimeline.FlowDirection);
            Press(window, Key.Right);
            Assert.Equal(0.1, vm.Start, 6); // later, as on the timeline
            var timeline = window.ClipTimeline;
            Assert.True(timeline.XOf(vm.End) > timeline.XOf(vm.Start));
        });
    }

    [AvaloniaFact]
    public void Dragging_a_handle_on_the_timeline_moves_that_end_and_shows_its_frame()
    {
        WithClipWindow("en", false, ["match.mp4"], (window, vm, frames, _) =>
        {
            var timeline = window.ClipTimeline;
            var origin = timeline.TranslatePoint(default, window)!.Value;
            var y = timeline.Bounds.Height / 2;
            var grab = new Point(timeline.XOf(vm.End) + 6, y) + (Vector)origin;
            var drop = new Point(timeline.XOf(120), y) + (Vector)origin;

            window.MouseDown(grab, MouseButton.Left);
            window.MouseMove(drop);
            window.MouseUp(drop, MouseButton.Left);
            Pump();

            Assert.Equal(ClipHandle.End, vm.ActiveHandle);
            Assert.InRange(vm.End, 117, 123);
            Assert.Equal(0, vm.Start);
            // The end's frame: the clip's last one, just before the end.
            WaitFor(() => frames.Times.Any(t => Math.Abs(t - (vm.End - 1 / 30.0)) < 1e-6));

            // Dragging the middle moves the whole clip.
            var length = vm.End - vm.Start;
            var middle = new Point(timeline.XOf(vm.Start + length / 2), y) + (Vector)origin;
            window.MouseDown(middle, MouseButton.Left);
            window.MouseMove(middle + new Vector(timeline.XOf(60) - timeline.XOf(0), 0));
            window.MouseUp(middle, MouseButton.Left);
            Pump();
            Assert.InRange(vm.Start, 57, 63);
            Assert.Equal(length, vm.End - vm.Start, 6);

            // Pressing beside the clip brings the nearer handle there.
            var before = new Point(timeline.XOf(20), y) + (Vector)origin;
            window.MouseDown(before, MouseButton.Left);
            window.MouseUp(before, MouseButton.Left);
            Pump();
            Assert.Equal(ClipHandle.Start, vm.ActiveHandle);
            Assert.InRange(vm.Start, 17, 23);
        });
    }

    [AvaloniaFact]
    public void Save_starts_the_conversion_of_the_clip_and_closes()
    {
        WithClipWindow("en", false, ["match.mp4"], (window, vm, _, exports) =>
        {
            vm.MoveHandle(ClipHandle.Start, 65.3);
            vm.QuickLengthCommand.Execute("30");
            vm.IsFast = false;
            Press(window, Key.Enter);

            var (path, preset) = Assert.Single(exports);
            Assert.EndsWith("match.mp4", path);
            Assert.Equal(PresetKind.Convert, preset.Kind);
            Assert.Equal("mkv", preset.TargetFormat); // what targetFor says for the file
            var part = Assert.Single(preset.Media.Clip!.Parts);
            Assert.Equal(65.3, part.Start, 6);
            Assert.Equal(95.3, part.End, 6);
            Assert.True(preset.Media.ExactCut);
            Assert.Equal("{name}_clip", preset.Output.FileNamePattern);
            Assert.Equal("preset.clip", preset.NameKey);
            WaitFor(() => !window.IsVisible);
        });
    }

    [AvaloniaFact]
    public void Fast_or_exact_is_remembered_in_the_tool()
    {
        TestServices.EnsureInitialized("en");
        var store = AppHost.Get<UserDataStore>();
        var saved = store.FindPreset("tool-clip")!;
        try
        {
            WithClipWindow("en", false, ["match.mp4"], (_, vm, _, _) =>
            {
                vm.IsFast = false;
                Assert.True(saved.Media.ExactCut);
            }, tool: saved);
            // Opened again: exact, as left.
            WithClipWindow("en", false, ["match.mp4"], (_, vm, _, _) =>
            {
                Assert.False(vm.IsFast);
                vm.IsFast = true;
            }, tool: saved);
            Assert.False(saved.Media.ExactCut);
        }
        finally
        {
            saved.Media.ExactCut = false;
        }
    }

    [AvaloniaFact]
    public void Several_videos_follow_each_other_and_ones_that_cant_be_read_are_listed()
    {
        WithClipWindow("en", false, ["one.mp4", "broken.mp4", "three.mp4"], (window, vm, frames, exports) =>
        {
            Assert.True(vm.HasMultipleFiles);
            Assert.Equal("1 of 3", vm.CounterText);
            Assert.Equal(AppHost.Get<ILocalizer>()["clip.save_next"], vm.SaveText);

            _ = vm.SaveAsync();
            // The second one can't be read: noted, and the third opens.
            WaitFor(() => vm.CurrentIndex == 2 && !vm.IsLoading && vm.Thumbnails.All(t => t is not null));
            Assert.True(vm.HasStatusNotice);
            Assert.Contains("broken.mp4", vm.StatusNotice);
            Assert.Equal(AppHost.Get<ILocalizer>()["clip.save"], vm.SaveText);

            _ = vm.SkipAsync();
            WaitFor(() => vm.IsFinished);
            Assert.Single(exports);
            Assert.Single(vm.Problems);
            Assert.Equal("1 of 3 failed", vm.Summary);
            Assert.True(window.IsVisible); // stays on what went wrong until closed
            Press(window, Key.Escape);
            WaitFor(() => !window.IsVisible);
        });
    }

    // ───────────────────────── Editing ─────────────────────────

    [AvaloniaFact]
    public void The_blade_cuts_at_the_playhead_and_delete_closes_up_with_the_magnet()
    {
        WithClipWindow("en", false, ["match.mp4"], (window, vm, _, exports) =>
        {
            vm.MovePlayhead(10);
            Press(window, Key.C);
            vm.MovePlayhead(20);
            Press(window, Key.C);
            Assert.Equal(3, vm.Pieces.Count);
            Assert.Equal(2, vm.SelectedIndex); // the piece right of the last cut

            // The middle piece out: a gap stays, with a button to close it.
            vm.MovePlayhead(15);
            Assert.Equal(1, vm.CurrentPiece);
            Assert.Equal("Delete", vm.DeleteText);
            Press(window, Key.Delete);
            Assert.Equal(ClipPieceState.Blank, vm.Pieces[1].State);
            Assert.True(vm.HasGaps);
            Assert.Equal("0:30.0", vm.LengthText);
            Assert.Equal("Close gap", vm.DeleteText);
            Assert.False(vm.CanFast); // two stretches: encoded
            Assert.True(vm.CanUndo);

            // Delete on the gap closes it; again puts the piece back.
            Press(window, Key.Delete);
            Assert.Equal(ClipPieceState.Removed, vm.Pieces[1].State);
            Assert.Equal("0:20.0", vm.LengthText);
            Assert.Equal("Put back", vm.DeleteText);
            Press(window, Key.Delete);
            Assert.True(vm.Pieces[1].IsKept);

            // Shift+Delete closes up at once; the gap button closes one gap.
            Press(window, Key.Delete, RawInputModifiers.Shift);
            Assert.Equal(ClipPieceState.Removed, vm.Pieces[1].State);
            Press(window, Key.Z, RawInputModifiers.Control);
            Press(window, Key.Delete);
            Assert.True(vm.HasGaps);
            vm.CloseGapsCommand.Execute(null);
            Assert.False(vm.HasGaps);
            Assert.Equal("0:20.0", vm.LengthText);

            // Undo with Ctrl+Z, redo with Ctrl+Y.
            Press(window, Key.Z, RawInputModifiers.Control);
            Assert.True(vm.HasGaps);
            Press(window, Key.Y, RawInputModifiers.Control);
            Assert.False(vm.HasGaps);

            // Saving writes the two pieces.
            Press(window, Key.Enter);
            var (_, preset) = Assert.Single(exports);
            Assert.Equal([(0.0, 10.0), (20.0, 30.0)], preset.Media.Clip!.Parts.Select(p => (p.Start, p.End)));
        });
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("pt-PT")]
    public void A_cut_needs_the_playhead_inside_the_clip(string language)
    {
        WithClipWindow(language, false, ["match.mp4"], (window, vm, _, _) =>
        {
            vm.MovePlayhead(100);
            Press(window, Key.C);
            Assert.Single(vm.Pieces);
            Assert.Equal(AppHost.Get<ILocalizer>()["clip.cannot_cut"], vm.Message);
            Assert.True(vm.HasMessage);
            // The only piece can't be cut out.
            vm.MovePlayhead(5);
            Press(window, Key.Delete);
            Assert.Equal(AppHost.Get<ILocalizer>()["clip.last_piece"], vm.Message);
            Assert.True(vm.Pieces[0].IsKept);
        });
    }

    [AvaloniaFact]
    public void Volume_speed_and_fades_change_the_piece_and_the_clip()
    {
        WithClipWindow("en", false, ["match.mp4"], (window, vm, _, exports) =>
        {
            vm.MovePlayhead(10);
            vm.Split();
            vm.SelectedVolume = 1.5;
            Assert.Equal("150 %", vm.VolumeText);
            vm.SelectedSpeed = vm.Speeds.Single(s => s.Value == 2);
            Assert.Equal((1.5, 2.0), (vm.Pieces[1].Volume, vm.Pieces[1].Speed));
            Assert.Equal((1.0, 1.0), (vm.Pieces[0].Volume, vm.Pieces[0].Speed));
            Assert.Equal("0:20.0", vm.LengthText); // 10 s + 20 s at double speed

            // Another piece shows its own values.
            vm.MovePlayhead(5);
            Assert.Equal(1, vm.SelectedVolume);
            Assert.Equal(1, vm.SelectedSpeed!.Value);

            vm.HasFadeIn = true;
            vm.HasFadeOut = true;
            Assert.True(window.ClipTimeline.FadeIn && window.ClipTimeline.FadeOut);
            Assert.False(vm.CanFast);

            Press(window, Key.Enter);
            var clip = Assert.Single(exports).Preset.Media.Clip!;
            Assert.Equal((ClipWindowViewModel.FadeSeconds, ClipWindowViewModel.FadeSeconds), (clip.FadeIn, clip.FadeOut));
            Assert.Equal([(1.0, 1.0), (1.5, 2.0)], clip.Parts.Select(p => (p.Volume, p.Speed)));
        });
    }

    [AvaloniaFact]
    public void Remove_silence_cuts_the_silent_stretches_out_and_says_how_many()
    {
        WithClipWindow("en", false, ["match.mp4"], (_, vm, frames, _) =>
        {
            frames.Silences.AddRange([new ClipRange(5, 8), new ClipRange(20, 24)]);
            _ = vm.RemoveSilenceAsync();
            WaitFor(() => !vm.IsFindingSilence);

            Assert.Equal(2, vm.Pieces.Count(p => p.State == ClipPieceState.Removed));
            Assert.Equal(AppHost.Get<ILocalizer>().Format("clip.silence_removed", 2), vm.Message);
            vm.Undo();
            Assert.Single(vm.Pieces);

            frames.Silences.Clear();
            _ = vm.RemoveSilenceAsync();
            WaitFor(() => !vm.IsFindingSilence);
            Assert.Equal(AppHost.Get<ILocalizer>()["clip.no_silence"], vm.Message);
        });
    }

    [AvaloniaFact]
    public void Space_plays_the_clip_as_it_will_be_written_from_the_playhead()
    {
        WithClipWindow("en", false, ["match.mp4"], (window, vm, frames, _) =>
        {
            vm.MovePlayhead(10);
            vm.Split();
            vm.MovePlayhead(5);
            vm.Delete(closeUp: true); // the first ten seconds out
            vm.MovePlayhead(12);

            Press(window, Key.Space);
            WaitFor(() => frames.Played.Count == 1 && !vm.IsPlaying);
            var played = frames.Played[0];
            // From the playhead, as the clip plays: 2 s into the second piece.
            Assert.Equal((12.0, 30.0), (played.Parts[0].Start, played.Parts[^1].End));
            Assert.Equal(ClipHandle.Playhead, vm.ActiveHandle);
            // The playhead followed the pictures (12 at 25 a second).
            Assert.Equal(12 + 11 / 25.0, vm.Playhead, 3);
            Assert.StartsWith("Playhead", vm.PreviewLabel);

            // Esc stops playing first, and only then closes.
            frames.HoldPlaying = new TaskCompletionSource();
            Press(window, Key.Space);
            WaitFor(() => vm.IsPlaying && frames.Played.Count == 2);
            Assert.Equal("Pause (Space)", vm.PlayToolTip);
            Press(window, Key.Escape);
            Assert.False(vm.IsPlaying);
            Assert.True(window.IsVisible);
            // Editing while playing stops it too.
            Press(window, Key.Space);
            WaitFor(() => vm.IsPlaying);
            vm.MoveHandle(ClipHandle.End, 25);
            Assert.False(vm.IsPlaying);
        });
    }

    [AvaloniaFact]
    public void Clicking_the_ruler_or_the_clip_puts_the_playhead_there_and_the_magnet_snaps_it()
    {
        WithClipWindow("en", false, ["match.mp4"], (window, vm, _, _) =>
        {
            vm.MovePlayhead(10);
            vm.Split();
            var timeline = window.ClipTimeline;
            var origin = timeline.TranslatePoint(default, window)!.Value;

            // The ruler, a little beside the cut: snapped onto it.
            var ruler = new Point(timeline.XOf(10) + 4, 6) + (Vector)origin;
            window.MouseDown(ruler, MouseButton.Left);
            window.MouseUp(ruler, MouseButton.Left);
            Pump();
            Assert.Equal(10, vm.Playhead);
            Assert.Equal(ClipHandle.Playhead, vm.ActiveHandle);

            // Without the magnet it stays where pressed.
            vm.IsMagnet = false;
            window.MouseDown(ruler, MouseButton.Left);
            window.MouseUp(ruler, MouseButton.Left);
            Pump();
            Assert.True(vm.Playhead > 10.5, vm.Playhead.ToString());

            // A click (no drag) in the clip: the playhead, and the piece there selected.
            var inClip = new Point(timeline.XOf(25), timeline.Bounds.Height - 10) + (Vector)origin;
            window.MouseDown(inClip, MouseButton.Left);
            window.MouseUp(inClip, MouseButton.Left);
            Pump();
            Assert.InRange(vm.Playhead, 24, 26);
            Assert.Equal(1, vm.SelectedIndex);
            Assert.Equal((0.0, 30.0), (vm.Start, vm.End)); // not moved
        });
    }

    [AvaloniaFact]
    public void Zooming_shows_the_clip_close_up_and_stays_put_while_a_handle_is_dragged()
    {
        WithClipWindow("en", false, ["match.mp4"], (window, vm, _, _) =>
        {
            vm.MoveHandle(ClipHandle.Start, 100);
            Assert.Equal((0.0, Length), (vm.ViewStart, vm.ViewEnd));
            Press(window, Key.Z);
            Assert.True(vm.IsZoomed);
            // The 30-second clip with a little room either side.
            Assert.Equal((100 - 4.5, 130 + 4.5), (vm.ViewStart, vm.ViewEnd));
            Pump();
            var timeline = window.ClipTimeline;
            Assert.True(timeline.XOf(130) - timeline.XOf(100) > timeline.Bounds.Width * 0.7);
            Assert.Equal("1:35.5", vm.ViewStartText);

            // Dragging the end to the edge of the view: the view doesn't move under the pointer.
            var origin = timeline.TranslatePoint(default, window)!.Value;
            var y = timeline.Bounds.Height / 2;
            var grab = new Point(timeline.XOf(130) + 6, y) + (Vector)origin;
            var edge = new Point(timeline.Bounds.Width + 40, y) + (Vector)origin;
            window.MouseDown(grab, MouseButton.Left);
            window.MouseMove(edge);
            window.MouseUp(edge, MouseButton.Left);
            Pump();
            Assert.InRange(vm.End, 134, 134.5); // at the edge, less where the handle was grabbed
            Assert.Equal((95.5, 134.5), (vm.ViewStart, vm.ViewEnd));
            // A key past it fits the view again.
            Press(window, Key.Right, RawInputModifiers.Shift);
            Assert.True(vm.ViewEnd > vm.End);

            Press(window, Key.Z);
            Assert.Equal((0.0, Length), (vm.ViewStart, vm.ViewEnd));
        });
    }

    // ───────────────────────── The sequence ─────────────────────────

    [AvaloniaFact]
    public void The_sequence_shows_the_clip_as_it_plays_and_its_gap_button_closes_the_gap()
    {
        WithClipWindow("en", false, ["match.mp4"], (window, vm, _, _) =>
        {
            vm.MovePlayhead(10);
            vm.Split();
            vm.MovePlayhead(20);
            vm.Split();
            vm.MovePlayhead(15);
            vm.Delete(closeUp: false);
            Pump();
            var sequence = window.ClipSequence;
            Assert.Equal(ClipWindowViewModel.DetailCount, vm.DetailThumbnails.Count);
            Assert.Equal((0.0, 30.0), (vm.DetailStart, vm.DetailEnd));
            // Three spans: the pieces and the gap between them.
            Assert.Equal([(0, 0.0, 10.0), (1, 10.0, 20.0), (2, 20.0, 30.0)], sequence.Layout());

            // The gap's button (in the middle of the gap) closes it: the last piece moves up.
            var origin = sequence.TranslatePoint(default, window)!.Value;
            var button = new Point(sequence.XOf(15), (sequence.VideoLane.Top + sequence.SoundLane.Bottom) / 2) + (Vector)origin;
            window.MouseDown(button, MouseButton.Left);
            window.MouseUp(button, MouseButton.Left);
            Pump();
            Assert.Equal(ClipPieceState.Removed, vm.Pieces[1].State);
            Assert.Equal([(0, 0.0, 10.0), (2, 10.0, 20.0)], sequence.Layout());

            // A click in the sequence puts the playhead there, in the video where that moment comes from.
            var click = new Point(sequence.XOf(15), sequence.VideoLane.Center.Y) + (Vector)origin;
            window.MouseDown(click, MouseButton.Left);
            window.MouseUp(click, MouseButton.Left);
            Pump();
            Assert.InRange(vm.Playhead, 24.5, 25.5);
            Assert.InRange(vm.SequencePlayhead, 14.5, 15.5);
            Assert.Equal(2, vm.SelectedIndex);
        });
    }

    [AvaloniaFact]
    public void Dragging_a_pieces_edge_takes_back_what_was_cut_off()
    {
        WithClipWindow("en", false, ["match.mp4"], (window, vm, _, _) =>
        {
            vm.MovePlayhead(10);
            vm.Split();
            vm.MovePlayhead(20);
            vm.Split();
            vm.MovePlayhead(15);
            vm.Delete(closeUp: true); // [0-10][10-20 out][20-30]
            Pump();
            var sequence = window.ClipSequence;
            var origin = sequence.TranslatePoint(default, window)!.Value;
            var scale = sequence.Content.Width / 20; // pixels a second at the start of the drag
            var edge = new Point(sequence.XOf(10) - 2, sequence.VideoLane.Center.Y) + (Vector)origin;

            window.MouseDown(edge, MouseButton.Left);
            window.MouseMove(edge + new Vector(4 * scale, 0));
            window.MouseUp(edge + new Vector(4 * scale, 0), MouseButton.Left);
            Pump();

            // Four seconds of the piece cut out are back, and the clip is four seconds longer.
            Assert.InRange(vm.Pieces[0].End, 13.6, 14.4);
            Assert.Equal(ClipPieceState.Removed, vm.Pieces[1].State);
            Assert.InRange(vm.Length, 23.6, 24.4);
            Assert.Equal(ClipHandle.Playhead, vm.ActiveHandle); // the frame at the edge shows
            // One undo takes the whole drag back.
            vm.Undo();
            Assert.Equal(10, vm.Pieces[0].End);
        });
    }

    [AvaloniaFact]
    public void A1_is_the_pieces_sound_delete_there_mutes_it_and_its_line_sets_the_volume()
    {
        WithClipWindow("en", false, ["match.mp4"], (window, vm, _, exports) =>
        {
            vm.MovePlayhead(10);
            vm.Split();
            Pump();
            var sequence = window.ClipSequence;
            var origin = sequence.TranslatePoint(default, window)!.Value;
            var sound = new Point(sequence.XOf(5), sequence.SoundLane.Top + 6) + (Vector)origin;
            window.MouseDown(sound, MouseButton.Left);
            window.MouseUp(sound, MouseButton.Left);
            Pump();
            Assert.Equal((ClipLane.Audio, 0), (vm.SelectedLane, vm.SelectedIndex));
            Assert.Equal("Mute sound", vm.DeleteText);
            Press(window, Key.Delete);
            Assert.True(vm.Pieces[0].Muted);
            Assert.True(vm.IsCurrentMuted);
            Assert.Equal("0:30.0", vm.LengthText); // the picture still plays
            Assert.Equal("Unmute", vm.DeleteText);
            vm.IsCurrentMuted = false;
            Assert.False(vm.Pieces[0].Muted);

            // The volume line of the second piece, dragged up: louder.
            var line = new Point(sequence.XOf(20), sequence.SoundLane.Center.Y) + (Vector)origin;
            window.MouseDown(line, MouseButton.Left);
            window.MouseMove(line - new Vector(0, sequence.SoundLane.Height / 4));
            window.MouseUp(line - new Vector(0, sequence.SoundLane.Height / 4), MouseButton.Left);
            Pump();
            Assert.InRange(vm.Pieces[1].Volume, 1.4, 1.6);

            vm.IsCurrentMuted = true; // the second piece is the current one now
            Press(window, Key.Enter);
            var clip = Assert.Single(exports).Preset.Media.Clip!;
            Assert.Equal([false, true], clip.Parts.Select(p => p.Muted));
        });
    }

    [AvaloniaFact]
    public void Music_goes_on_A2_moves_along_and_is_written_with_the_clip()
    {
        WithClipWindow("en", false, ["match.mp4"], (window, vm, frames, exports) =>
        {
            _ = vm.AddMusicAsync(Path.Combine("music", "song.mp3"));
            WaitFor(() => vm.HasMusic && vm.MusicLoudness.Count > 0);
            Assert.True(vm.IsMusicSelected);
            Assert.Equal("Remove music", vm.DeleteText);
            Assert.Equal(ClipMusic.DefaultVolume, vm.SelectedVolume);
            vm.SelectedVolume = 0.4; // the slider is the music's now
            Assert.Equal(0.4, vm.Music!.Volume);
            Assert.False(vm.CanFast);
            Pump();

            // Dragged five seconds along.
            var sequence = window.ClipSequence;
            var origin = sequence.TranslatePoint(default, window)!.Value;
            var scale = sequence.Content.Width / 30;
            var block = new Point(sequence.XOf(10), sequence.MusicLane.Top + 6) + (Vector)origin;
            window.MouseDown(block, MouseButton.Left);
            window.MouseMove(block + new Vector(5 * scale, 0));
            window.MouseUp(block + new Vector(5 * scale, 0), MouseButton.Left);
            Pump();
            Assert.InRange(vm.Music!.Offset, 4.7, 5.3);

            // Playing plays it too; saving writes it.
            Press(window, Key.Space);
            WaitFor(() => frames.Played.Count == 1 && !vm.IsPlaying);
            Assert.Equal("song.mp3", Path.GetFileName(frames.Played[0].Music!.Path));
            Press(window, Key.Enter);
            var music = Assert.Single(exports).Preset.Media.Clip!.Music!;
            Assert.Equal((0.4, 40.0), (music.Volume, music.Length));
        });
    }

    [AvaloniaFact]
    public void Music_is_taken_away_with_delete_and_a_file_without_sound_is_refused()
    {
        WithClipWindow("en", false, ["match.mp4"], (window, vm, _, _) =>
        {
            _ = vm.AddMusicAsync(Path.Combine("music", "silent.mp4"));
            WaitFor(() => vm.HasMessage);
            Assert.False(vm.HasMusic);
            Assert.Equal(AppHost.Get<ILocalizer>()["clip.no_music"], vm.Message);

            _ = vm.AddMusicAsync(Path.Combine("music", "song.mp3"));
            WaitFor(() => vm.HasMusic);
            Press(window, Key.Delete);
            Assert.False(vm.HasMusic);
            Assert.Equal(ClipLane.Video, vm.SelectedLane);
            vm.Undo();
            Assert.True(vm.HasMusic);
        });
    }

    // ───────────────────────── Several pieces, keys, busy ─────────────────────────

    /// <summary>The clip cut at 6, 12, 18 and 24 s: five pieces of six seconds.</summary>
    private static void CutInFive(ClipWindowViewModel vm)
    {
        foreach (var at in new[] { 6.0, 12, 18, 24 })
        {
            vm.MovePlayhead(at);
            vm.Split();
        }
    }

    [AvaloniaFact]
    public void Ctrl_and_shift_clicks_select_several_pieces_and_delete_cuts_them_all_out_at_once()
    {
        WithClipWindow("en", false, ["match.mp4"], (window, vm, _, _) =>
        {
            CutInFive(vm);
            vm.SelectPiece(1, ClipLane.Video, ClipSelect.Only);
            vm.SelectPiece(3, ClipLane.Video, ClipSelect.Toggle);
            Assert.Equal([1, 3], vm.SelectedIndices);
            Assert.Equal("2 selected", vm.SelectionText);
            Assert.Equal("Delete (2)", vm.DeleteText);

            Press(window, Key.Delete);
            Assert.Equal([false, true, false, true, false], vm.Pieces.Select(p => p.State == ClipPieceState.Blank));
            Assert.Equal("Close gap (2)", vm.DeleteText);
            // Delete again closes both gaps; one undo puts them back as gaps.
            Press(window, Key.Delete);
            Assert.Equal("0:18.0", vm.LengthText);
            vm.Undo();
            Assert.Equal("0:30.0", vm.LengthText);
            vm.Undo();
            Assert.All(vm.Pieces, p => Assert.True(p.IsKept));

            // Shift: a run of pieces; Ctrl again takes one out.
            vm.SelectPiece(0, ClipLane.Video, ClipSelect.Only);
            vm.SelectPiece(3, ClipLane.Video, ClipSelect.Range);
            Assert.Equal([0, 1, 2, 3], vm.SelectedIndices);
            vm.SelectPiece(2, ClipLane.Video, ClipSelect.Toggle);
            Assert.Equal([0, 1, 3], vm.SelectedIndices);

            // The volume and the speed go to all of them, one undo step each.
            vm.SelectedVolume = 0.5;
            vm.SelectedVolume = 0.4;
            Assert.Equal([0.4, 0.4, 1, 0.4, 1], vm.Pieces.Select(p => p.Volume));
            vm.SelectedSpeed = vm.Speeds.Single(x => x.Value == 2);
            Assert.Equal([2.0, 2, 1, 2, 1], vm.Pieces.Select(p => p.Speed));
            vm.Undo();
            vm.Undo();
            Assert.All(vm.Pieces, p => Assert.Equal((1.0, 1.0), (p.Volume, p.Speed)));

            // Ctrl+A everything, Esc back to one, Esc again closes.
            Press(window, Key.A, RawInputModifiers.Control);
            Assert.Equal([0, 1, 2, 3, 4], vm.SelectedIndices);
            Press(window, Key.Escape);
            Assert.Single(vm.SelectedIndices);
            Assert.True(window.IsVisible);
        });
    }

    [AvaloniaFact]
    public void Dragging_across_pieces_selects_them()
    {
        WithClipWindow("en", false, ["match.mp4"], (window, vm, _, _) =>
        {
            CutInFive(vm);
            Pump();
            var sequence = window.ClipSequence;
            var origin = sequence.TranslatePoint(default, window)!.Value;
            var from = new Point(sequence.XOf(7), sequence.VideoLane.Center.Y) + (Vector)origin;
            var to = new Point(sequence.XOf(20), sequence.VideoLane.Center.Y) + (Vector)origin;
            window.MouseDown(from, MouseButton.Left);
            window.MouseMove(from + new Vector(10, 0));
            window.MouseMove(to);
            window.MouseUp(to, MouseButton.Left);
            Pump();
            // 7 s to 20 s goes across the second, third and fourth pieces.
            Assert.Equal([1, 2, 3], vm.SelectedIndices);
            Assert.Equal(ClipLane.Video, vm.SelectedLane);

            // On A1: Delete mutes all of them.
            vm.SelectAcross(0, 11, ClipLane.Audio);
            Assert.Equal([0, 1], vm.SelectedIndices);
            Press(window, Key.Delete);
            Assert.Equal([true, true, false, false, false], vm.Pieces.Select(p => p.Muted));
        });
    }

    [AvaloniaFact]
    public void Space_plays_whatever_button_was_clicked_last()
    {
        WithClipWindow("en", false, ["match.mp4"], (window, vm, frames, _) =>
        {
            // Click maximize (and back), the magnet and the zoom: Space still plays, it presses none of them.
            foreach (var name in new[] { "MaximizeButton", "MaximizeButton", "MagnetToggle", "ZoomToggle" })
            {
                var button = window.FindControl<Control>(name)!;
                var centre = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
                window.MouseDown(centre, MouseButton.Left);
                window.MouseUp(centre, MouseButton.Left);
                Pump();
            }
            var state = window.WindowState;
            var magnet = vm.IsMagnet;
            var zoom = vm.IsZoomed;

            Press(window, Key.Space);
            window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.None, null);
            WaitFor(() => frames.Played.Count == 1);
            Assert.Equal((state, magnet, zoom), (window.WindowState, vm.IsMagnet, vm.IsZoomed));
        });
    }

    [AvaloniaFact]
    public void A_button_reached_with_Tab_is_pressed_by_Space_and_Enter()
    {
        WithClipWindow("en", false, ["match.mp4"], (window, vm, frames, _) =>
        {
            var magnet = window.FindControl<Avalonia.Controls.Primitives.ToggleButton>("MagnetToggle")!;
            Assert.True(magnet.Focusable);
            var before = vm.IsMagnet;
            magnet.Focus(NavigationMethod.Tab);
            Pump();

            Press(window, Key.Space);
            window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.None, null);
            Pump();
            Assert.NotEqual(before, vm.IsMagnet);
            Assert.Empty(frames.Played);

            Press(window, Key.Enter);
            window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
            Pump();
            Assert.Equal(before, vm.IsMagnet);
            Assert.False(vm.IsFinished);

            // The volume slider reached with Tab: the arrows move it, not the playhead.
            var slider = window.FindControl<Slider>("VolumeSlider")!;
            var playhead = vm.Playhead;
            slider.Focus(NavigationMethod.Tab);
            var volume = slider.Value;
            Press(window, Key.Right);
            Assert.True(slider.Value > volume);
            Assert.Equal(playhead, vm.Playhead);
        });
    }

    [AvaloniaFact]
    public void The_window_says_it_is_preparing_until_the_clip_starts_playing()
    {
        WithClipWindow("en", false, ["match.mp4"], (window, vm, frames, _) =>
        {
            frames.HoldStart = new TaskCompletionSource();
            Press(window, Key.Space);
            WaitFor(() => vm.IsBuffering);
            Assert.True(vm.IsBusy);
            Assert.Equal("Preparing…", vm.BusyText);
            frames.HoldStart.SetResult();
            WaitFor(() => !vm.IsBuffering);
            WaitFor(() => !vm.IsPlaying);
            Assert.False(vm.IsBusy);
            Assert.Equal("", vm.BusyText);
        });
    }

    // ───────────────────────── Helpers ─────────────────────────

    /// <summary>
    /// Opens the window on made-up videos (a file name with "broken" can't be read) of <paramref name="duration"/>
    /// seconds, waits until the timeline and the large picture are there, and runs <paramref name="act"/> with the
    /// window, its model, the frames taken and the conversions started.
    /// </summary>
    internal static void WithClipWindow(string language, bool dark, string[] names,
        Action<ClipWindow, ClipWindowViewModel, PaintedFrames, List<(string Path, Preset Preset)>> act, double duration = Length, Preset? tool = null)
    {
        TestServices.EnsureInitialized(language);
        var theme = AppHost.Get<ThemeService>();
        if (dark)
            theme.Apply(new ThemeSettings { Mode = ThemeMode.Dark }, language);
        var folder = Directory.CreateTempSubdirectory("filee-clip-window-").FullName;
        ClipWindow? window = null;
        try
        {
            var files = names.Select(name => Path.Combine(folder, name)).ToList();
            foreach (var file in files)
                File.WriteAllBytes(file, [0]);
            var frames = new PaintedFrames(duration);
            var exports = new List<(string, Preset)>();
            tool ??= BuiltInData.CreatePresets().Single(p => p.Id == "tool-clip");
            var vm = new ClipWindowViewModel(files, tool, AppHost.Get<ILocalizer>(), AppHost.Get<UserDataStore>(), frames,
                _ => "mkv", (path, preset) => exports.Add((path, preset)));
            window = new ClipWindow { DataContext = vm, Width = 1240, Height = 880 };
            window.Show();
            WaitFor(() => !vm.IsLoading && vm.Thumbnails.All(t => t is not null) && frames.Times.Contains(vm.PreviewTime)
                && vm.DetailThumbnails.Count == ClipWindowViewModel.DetailCount);
            Pump();
            act(window, vm, frames, exports);
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

    internal static void Press(ClipWindow window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }

    internal static void WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        Assert.True(condition());
    }

    /// <summary>
    /// Stands in for FFmpeg: a 16:9 video of a sunny day whose sky changes colour over time and whose sun crosses the
    /// picture, so each frame looks different.
    /// </summary>
    internal sealed class PaintedFrames(double duration) : IVideoFrameSource
    {
        private readonly ConcurrentQueue<double> _times = new();

        /// <summary>Every time a frame was taken at.</summary>
        public IReadOnlyCollection<double> Times => _times;

        /// <summary>What "Remove silence" finds.</summary>
        public List<ClipRange> Silences { get; } = [];

        /// <summary>The clips played, as asked for.</summary>
        public List<ClipEdit> Played { get; } = [];

        /// <summary>Pictures a played clip gives before it ends (unless stopped).</summary>
        public int PlayedFrames { get; set; } = 12;

        /// <summary>A played clip waits here before it ends, when set (to stop it while it plays).</summary>
        public TaskCompletionSource? HoldPlaying { get; set; }

        /// <summary>A played clip waits here before its first picture, when set (FFmpeg starting up).</summary>
        public TaskCompletionSource? HoldStart { get; set; }

        /// <summary>How long a music file plays; a name with "silent" has no sound.</summary>
        public Task<double> ReadSoundLengthAsync(string path, CancellationToken cancellationToken) =>
            Path.GetFileName(path).Contains("silent", StringComparison.Ordinal)
                ? Task.FromException<double>(new InvalidOperationException("The file has no sound."))
                : Task.FromResult(40.0);

        public Task<float[]> ReadLoudnessAsync(string path, double start, double end, int count, string workFolder, CancellationToken cancellationToken) =>
            Task.FromResult(Enumerable.Range(0, count).Select(i => (float)(0.35 + 0.3 * Math.Sin(i / 3.0) + 0.3 * Math.Sin(i / 17.0)) is var v
                ? (float)Math.Clamp(i % 40 < 6 ? 0.03 : v, 0, 1) : 0).ToArray());

        public Task<IReadOnlyList<ClipRange>> FindSilencesAsync(string path, double start, double end, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ClipRange>>([.. Silences]);

        public async Task PlayAsync(string path, ClipEdit clip, int height, Action<byte[], int, int, double> onFrame, CancellationToken cancellationToken)
        {
            Played.Add(clip);
            if (HoldStart is { } start)
                await start.Task.WaitAsync(cancellationToken);
            var width = height * 16 / 9;
            var pixels = new byte[width * height * 4];
            for (var n = 0; n < PlayedFrames && !cancellationToken.IsCancellationRequested; n++)
            {
                Array.Fill(pixels, (byte)(40 + n * 10));
                onFrame(pixels, width, height, n / 25.0);
                await Task.Yield();
            }
            if (HoldPlaying is { } hold)
                await hold.Task.WaitAsync(cancellationToken).ContinueWith(_ => { }, TaskScheduler.Default);
        }

        public Task<VideoSummary> ProbeAsync(string path, CancellationToken cancellationToken) =>
            Path.GetFileName(path).Contains("broken", StringComparison.Ordinal)
                ? Task.FromException<VideoSummary>(new InvalidOperationException("Invalid data found when processing input"))
                : Task.FromResult(new VideoSummary(duration, 1920, 1080, 30));

        public Task<bool> WriteFrameAsync(string path, double seconds, string output, int maxHeight, CancellationToken cancellationToken)
        {
            _times.Enqueue(seconds);
            var height = (uint)maxHeight;
            var width = height * 16 / 9;
            var t = Math.Clamp(seconds / duration, 0, 1);
            var top = Hsl(200 + 160 * t, 0.65, 0.55);
            var bottom = Hsl(30 + 20 * t, 0.85, 0.75);
            using var image = new MagickImage($"gradient:{top}-{bottom}", width, height);
            new Drawables()
                .FillColor(new MagickColor("#FFE9A8"))
                .Circle(width * (0.1 + 0.8 * t), height * 0.35, width * (0.1 + 0.8 * t) + height * 0.08, height * 0.35)
                .FillColor(new MagickColor("#2F6B3A"))
                .Rectangle(0, height * 0.72, width, height)
                .FillColor(new MagickColor("#F4F1E8"))
                .Rectangle(width * 0.48, height * 0.72, width * 0.52, height)
                .Draw(image);
            image.Write(output, MagickFormat.Jpeg);
            return Task.FromResult(true);
        }

        private static string Hsl(double hue, double saturation, double lightness)
        {
            var c = (1 - Math.Abs(2 * lightness - 1)) * saturation;
            var h = (hue % 360 + 360) % 360 / 60;
            var x = c * (1 - Math.Abs(h % 2 - 1));
            var (r, g, b) = h switch
            {
                < 1 => (c, x, 0.0),
                < 2 => (x, c, 0.0),
                < 3 => (0.0, c, x),
                < 4 => (0.0, x, c),
                < 5 => (x, 0.0, c),
                _ => (c, 0.0, x),
            };
            var m = lightness - c / 2;
            return $"#{(int)Math.Round((r + m) * 255):X2}{(int)Math.Round((g + m) * 255):X2}{(int)Math.Round((b + m) * 255):X2}";
        }
    }
}
