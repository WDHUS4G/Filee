// The clip window's editing without the window (ClipCutter) and what it hands the conversion (ClipEdit): the blade,
// pieces cut out with and without the magnet, volume and speed, silences taken out, undo, and the clip's length and
// times as played.

using System.Text.Json;
using Filee.Core.Presets;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class ClipCutterTests
{
    /// <summary>A 5:12.4 video with the 30 seconds from 1:00 chosen.</summary>
    private static ClipCutter Cutter() => new(312.4, new ClipRange(60, 90));

    [Fact]
    public void A_new_clip_is_one_piece_as_recorded()
    {
        var cutter = Cutter();
        var piece = Assert.Single(cutter.Pieces);
        Assert.Equal(new ClipPiece(60, 90), piece);
        Assert.Equal(30, cutter.Length);
        Assert.False(cutter.CanUndo);
        var edit = cutter.ToEdit();
        Assert.True(edit.IsPlainCut);
        Assert.Equal((60.0, 90.0), (edit.Start, edit.End));
    }

    [Fact]
    public void The_blade_cuts_a_piece_in_two_but_not_next_to_a_cut()
    {
        var cutter = Cutter();
        Assert.True(cutter.Split(70));
        Assert.Equal([new ClipPiece(60, 70), new ClipPiece(70, 90)], cutter.Pieces);
        Assert.False(cutter.Split(70.05)); // too close to the cut
        Assert.False(cutter.Split(95)); // outside the clip
        Assert.False(cutter.Split(60)); // at the start
        Assert.Equal(0, cutter.IndexAt(65));
        Assert.Equal(1, cutter.IndexAt(70));
        Assert.Equal(-1, cutter.IndexAt(50));
        // Two pieces as recorded, one after the other: still one stretch to write, copied as it is.
        Assert.True(cutter.ToEdit().IsPlainCut);
    }

    [Fact]
    public void With_the_magnet_a_removed_piece_closes_up_without_it_a_pause_stays()
    {
        var cutter = Cutter();
        cutter.Split(70);
        cutter.Split(80);

        Assert.True(cutter.Remove(1, closeUp: true));
        Assert.Equal(20, cutter.Length);
        var edit = cutter.ToEdit();
        Assert.Equal([(60.0, 70.0, false), (80.0, 90.0, false)], edit.Parts.Select(p => (p.Start, p.End, p.Blank)));
        Assert.False(edit.IsPlainCut);

        Assert.True(cutter.Remove(1, closeUp: false));
        Assert.True(cutter.HasGaps);
        Assert.Equal(30, cutter.Length);
        Assert.Equal([false, true, false], cutter.ToEdit().Parts.Select(p => p.Blank));

        Assert.Equal(1, cutter.CloseGaps());
        Assert.False(cutter.HasGaps);
        Assert.Equal(20, cutter.Length);

        Assert.True(cutter.Restore(1));
        Assert.Equal(30, cutter.Length);
    }

    [Fact]
    public void The_last_piece_that_plays_cant_be_removed()
    {
        var cutter = Cutter();
        Assert.False(cutter.Remove(0, closeUp: true));
        cutter.Split(75);
        Assert.True(cutter.Remove(0, closeUp: true));
        Assert.False(cutter.Remove(1, closeUp: true));
        Assert.False(cutter.Remove(1, closeUp: false));
        Assert.Single(cutter.ToEdit().Parts);
    }

    [Fact]
    public void Volume_and_speed_belong_to_a_piece_and_speed_changes_the_length()
    {
        var cutter = Cutter();
        cutter.Split(70);
        cutter.SetSpeed(1, 2);
        cutter.SetVolume(0, 0.5);
        Assert.Equal(10 + 10, cutter.Length);
        cutter.SetSpeed(1, 99);
        Assert.Equal(ClipPart.MaxSpeed, cutter.Pieces[1].Speed);
        cutter.SetVolume(0, -1);
        Assert.Equal(0, cutter.Pieces[0].Volume);
        cutter.SetVolume(0, 9);
        Assert.Equal(ClipPart.MaxVolume, cutter.Pieces[0].Volume);

        var edit = cutter.ToEdit();
        Assert.Equal([(ClipPart.MaxVolume, 1.0), (1.0, ClipPart.MaxSpeed)], edit.Parts.Select(p => (p.Volume, p.Speed)));
        Assert.Equal(10 + 20 / ClipPart.MaxSpeed, edit.Length, 6);
    }

    [Fact]
    public void Moving_the_ends_keeps_the_cuts_inside_and_drops_what_falls_out()
    {
        var cutter = Cutter();
        cutter.Split(70);
        cutter.Split(80);
        cutter.SetVolume(2, 2);

        cutter.MoveStart(72);
        Assert.Equal([(72.0, 80.0), (80.0, 90.0)], cutter.Pieces.Select(p => (p.Start, p.End)));
        cutter.MoveEnd(100);
        Assert.Equal((80.0, 100.0, 2.0), (cutter.Pieces[1].Start, cutter.Pieces[1].End, cutter.Pieces[1].Volume));
        // An end left as a sliver joins its neighbour.
        cutter.MoveEnd(80.02);
        Assert.Equal([(72.0, 80.02)], cutter.Pieces.Select(p => (p.Start, Math.Round(p.End, 2))));

        // Past the other end: the clip goes along, as far as the video goes.
        var moving = Cutter();
        moving.Split(70);
        moving.MoveStart(300);
        Assert.Equal((300.0, 312.4), (moving.Start, moving.End));
        Assert.Equal(2, moving.Pieces.Count); // the cut came along: 300 + 10 = 310
        Assert.Equal(310, moving.Pieces[0].End, 6);
        moving.MoveEnd(3);
        Assert.Equal((0.0, 3.0), (moving.Start, moving.End));
    }

    [Fact]
    public void Moving_the_clip_and_quick_lengths_stay_in_the_video()
    {
        var cutter = Cutter();
        cutter.Split(70);
        cutter.Move(300);
        Assert.Equal((312.4 - 30, 312.4), (cutter.Start, cutter.End));
        Assert.Equal(cutter.Start + 10, cutter.Pieces[0].End, 6);
        cutter.Move(-4);
        Assert.Equal((0.0, 30.0), (cutter.Start, cutter.End));

        cutter.SetLength(60);
        Assert.Equal((0.0, 60.0), (cutter.Start, cutter.End));
        cutter.Move(290);
        cutter.SetLength(60);
        Assert.Equal((312.4 - 60, 312.4), (cutter.Start, cutter.End));
    }

    [Fact]
    public void Silences_are_taken_out_keeping_a_little_next_to_sound()
    {
        var cutter = Cutter();
        var removed = cutter.RemoveSilences([new ClipRange(55, 62), new ClipRange(70, 73), new ClipRange(80, 80.3), new ClipRange(88, 95)]);

        Assert.Equal(3, removed); // the 0.3-second pause stays
        var edit = cutter.ToEdit();
        var p = ClipCutter.SilencePadding;
        Assert.Equal([(62 - p, 70 + p), (73 - p, 88 + p)], edit.Parts.Select(x => (Math.Round(x.Start, 6), Math.Round(x.End, 6))));
        // At the clip's ends the silence goes entirely (no sound next to it there).
        Assert.Equal(30 - (2 - p) - (3 - 2 * p) - (2 - p), cutter.Length, 6);

        // One undo puts them all back.
        Assert.True(cutter.Undo());
        Assert.Equal(30, cutter.Length);
        Assert.True(cutter.Redo());
        Assert.Equal(edit.Length, cutter.Length, 6);

        // All silent: something stays.
        var quiet = Cutter();
        Assert.Equal(0, quiet.RemoveSilences([new ClipRange(0, 312.4)]));
        Assert.Equal(30, quiet.Length);
    }

    [Fact]
    public void Every_change_can_be_undone_and_redone()
    {
        var cutter = Cutter();
        var changes = 0;
        cutter.Changed += () => changes++;
        cutter.Split(70);
        cutter.Remove(0, closeUp: true);
        cutter.FadeIn = 1;
        cutter.FadeOut = 99;
        Assert.Equal(ClipEdit.MaxFade, cutter.FadeOut);
        Assert.Equal(4, changes);

        Assert.True(cutter.Undo());
        Assert.Equal(0, cutter.FadeOut);
        Assert.True(cutter.Undo());
        Assert.True(cutter.Undo());
        Assert.Equal(30, cutter.Length);
        Assert.True(cutter.Undo());
        Assert.Single(cutter.Pieces);
        Assert.False(cutter.Undo());
        Assert.True(cutter.Redo());
        Assert.Equal(2, cutter.Pieces.Count);
        // A volume slider dragged is one step.
        cutter.SetVolume(1, 0.9);
        cutter.SetVolume(1, 0.8);
        cutter.SetVolume(1, 0.7);
        Assert.True(cutter.Undo());
        Assert.Equal(1, cutter.Pieces[1].Volume);
        // A new change after undoing forgets what could be redone.
        cutter.SetVolume(0, 2);
        Assert.False(cutter.CanRedo);
        // Moving to where it is already is no change.
        changes = 0;
        cutter.MoveStart(cutter.Start);
        cutter.SetVolume(0, 2);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void A_pieces_sound_can_be_muted_while_its_picture_plays()
    {
        var cutter = Cutter();
        cutter.Split(70);
        cutter.SetMuted(1, true);
        Assert.True(cutter.Pieces[1].Muted);
        var edit = cutter.ToEdit();
        Assert.Equal([false, true], edit.Parts.Select(p => p.Muted));
        Assert.Equal(0, edit.Parts[1].PlayVolume);
        Assert.Equal(1, edit.Parts[1].Volume); // kept for unmuting
        Assert.False(edit.IsPlainCut);
        Assert.Equal(30, cutter.Length); // the picture still plays
        cutter.SetMuted(1, false);
        Assert.True(cutter.ToEdit().IsPlainCut);
    }

    [Fact]
    public void A_gap_is_closed_on_its_own()
    {
        var cutter = Cutter();
        cutter.Split(70);
        cutter.Split(80);
        cutter.Remove(0, closeUp: false);
        cutter.Remove(1, closeUp: false);
        Assert.True(cutter.CloseGap(1));
        Assert.Equal([ClipPieceState.Blank, ClipPieceState.Removed, ClipPieceState.Kept], cutter.Pieces.Select(p => p.State));
        Assert.False(cutter.CloseGap(2)); // not a gap
        Assert.Equal(20, cutter.Length);
    }

    [Fact]
    public void Dragging_an_edge_out_takes_back_what_was_cut_next_to_it()
    {
        var cutter = Cutter();
        cutter.Split(70);
        cutter.Split(80);
        cutter.Remove(1, closeUp: true); // [60-70][70-80 out][80-90]
        Assert.Equal(20, cutter.Length);

        // A little back.
        cutter.TrimEdge(0, end: true, 74);
        Assert.Equal([(60.0, 74.0, true), (74.0, 80.0, false), (80.0, 90.0, true)], cutter.Pieces.Select(p => (p.Start, p.End, p.IsKept)));
        Assert.Equal(24, cutter.Length);
        // All of it back: the piece cut out is used up, the cut lands on the next piece.
        cutter.TrimEdge(0, end: true, 80);
        Assert.Equal([(60.0, 80.0), (80.0, 90.0)], cutter.Pieces.Select(p => (p.Start, p.End)));
        Assert.Equal(30, cutter.Length);
        // The whole drag is one undo step.
        Assert.True(cutter.Undo());
        Assert.Equal(20, cutter.Length);

        // The next piece's start pulled back to the left.
        cutter.TrimEdge(2, end: false, 76);
        Assert.Equal([(60.0, 70.0, true), (70.0, 76.0, false), (76.0, 90.0, true)], cutter.Pieces.Select(p => (p.Start, p.End, p.IsKept)));
    }

    [Fact]
    public void Dragging_an_edge_in_cuts_that_much_out()
    {
        var cutter = Cutter();
        cutter.Split(70); // [60-70][70-90], both play
        cutter.TrimEdge(0, end: true, 65);
        Assert.Equal([(60.0, 65.0, true), (65.0, 70.0, false), (70.0, 90.0, true)], cutter.Pieces.Select(p => (p.Start, p.End, p.IsKept)));
        Assert.Equal(25, cutter.Length);

        var other = Cutter();
        other.Split(70);
        other.TrimEdge(1, end: false, 75);
        Assert.Equal([(60.0, 70.0, true), (70.0, 75.0, false), (75.0, 90.0, true)], other.Pieces.Select(p => (p.Start, p.End, p.IsKept)));
        // Never shorter than the least length.
        other.TrimEdge(2, end: false, 95);
        Assert.Equal(90 - ClipRange.MinLength, other.Pieces[^1].Start, 6);
    }

    [Fact]
    public void The_clips_outer_edges_reach_further_into_the_video()
    {
        var cutter = Cutter();
        cutter.Split(70);
        Assert.Equal(100, cutter.TrimEdge(1, end: true, 100));
        Assert.Equal(50, cutter.TrimEdge(0, end: false, 50));
        Assert.Equal((50.0, 100.0), (cutter.Start, cutter.End));
        Assert.Equal(312.4, cutter.TrimEdge(1, end: true, 999));
    }

    [Fact]
    public void Music_goes_under_the_clip_and_moves_and_changes_volume_with_undo()
    {
        var cutter = Cutter();
        cutter.SetMusic(new ClipMusic { Path = "song.mp3", Length = 200 });
        Assert.Equal(ClipMusic.DefaultVolume, cutter.Music!.Volume);
        cutter.MoveMusic(5);
        cutter.MoveMusic(8);
        cutter.SetMusicVolume(0.3);
        cutter.SetMusicVolume(0.25);
        cutter.MoveMusic(999);
        Assert.Equal(30 - ClipRange.MinLength, cutter.Music!.Offset, 6);
        var edit = cutter.ToEdit();
        Assert.Equal(("song.mp3", 0.25), (edit.Music!.Path, edit.Music.Volume));
        Assert.False(edit.IsPlainCut);

        Assert.True(cutter.Undo()); // the last move
        Assert.Equal(8, cutter.Music!.Offset);
        Assert.True(cutter.Undo()); // the volume drag at once
        Assert.Equal(ClipMusic.DefaultVolume, cutter.Music!.Volume);
        Assert.True(cutter.Undo()); // both moves at once
        Assert.Equal(0, cutter.Music!.Offset);
        Assert.True(cutter.Undo());
        Assert.Null(cutter.Music);
    }

    [Fact]
    public void Music_plays_while_both_last_and_keeps_its_place_when_playing_from_later()
    {
        var music = new ClipMusic { Path = "song.mp3", Offset = 4, From = 10, Length = 20 };
        Assert.Equal(10, music.PlayLength(30)); // the song ends first
        Assert.Equal(6, music.PlayLength(10)); // the clip ends first

        var edit = ClipEdit.Of(new ClipRange(0, 30));
        edit.Music = music;
        var later = edit.From(7);
        Assert.Equal((0.0, 13.0), (later.Music!.Offset, later.Music.From));
        var sooner = edit.From(1);
        Assert.Equal((3.0, 10.0), (sooner.Music!.Offset, sooner.Music.From));

        // Music past the clip's end isn't written.
        edit.Music = new ClipMusic { Path = "song.mp3", Offset = 40, Length = 20 };
        Assert.Null(edit.Clamped(100).Music);
        edit.Music = new ClipMusic { Path = "song.mp3", Offset = -3, Volume = 9, Length = 20 };
        Assert.Equal((0.0, 2.0), (edit.Clamped(100).Music!.Offset, edit.Clamped(100).Music!.Volume));
    }

    [Fact]
    public void Several_changes_in_a_batch_are_one_undo_step()
    {
        var cutter = Cutter();
        cutter.Split(70);
        cutter.Split(80);
        var told = 0;
        cutter.Changed += () => told++;
        cutter.Batch(() =>
        {
            cutter.Remove(0, closeUp: false);
            cutter.Remove(2, closeUp: false);
            cutter.SetVolume(1, 1.5);
        });
        Assert.Equal(1, told);
        Assert.Equal([ClipPieceState.Blank, ClipPieceState.Kept, ClipPieceState.Blank], cutter.Pieces.Select(p => p.State));
        Assert.True(cutter.Undo());
        Assert.All(cutter.Pieces, p => Assert.True(p.IsKept && p.Volume == 1));
        Assert.Equal(2, cutter.Pieces.Count(p => p.Start > 60)); // the cuts stay: they were made before
        // A batch that changes nothing leaves nothing to undo.
        var steps = 0;
        while (cutter.Undo())
            steps++;
        cutter.Batch(() => cutter.SetVolume(0, 1));
        Assert.False(cutter.CanUndo);
        Assert.Equal(2, steps);
    }

    // ───────────────────────── The edit ─────────────────────────

    private static ClipEdit Edit() => new()
    {
        Parts =
        [
            new ClipPart { Start = 10, End = 20 },
            new ClipPart { Start = 20, End = 24, Blank = true },
            new ClipPart { Start = 30, End = 40, Speed = 2 },
        ],
        FadeIn = 1,
        FadeOut = 2,
    };

    [Fact]
    public void A_clip_plays_its_parts_pauses_and_speeds()
    {
        var edit = Edit();
        Assert.Equal(10 + 4 + 5, edit.Length);
        Assert.Equal((10.0, 40.0), (edit.Start, edit.End));
        Assert.False(edit.IsPlainCut);

        Assert.Equal(15, edit.SourceTimeAt(5));
        Assert.Equal(22, edit.SourceTimeAt(12)); // in the pause
        Assert.Equal(34, edit.SourceTimeAt(16)); // two seconds of video a second
        Assert.Equal(40, edit.SourceTimeAt(99));
        Assert.Equal(5, edit.ClipTimeAt(15));
        Assert.Equal(16, edit.ClipTimeAt(34));
        Assert.Equal(14, edit.ClipTimeAt(27)); // cut out: where the next part starts
    }

    [Fact]
    public void Playing_from_later_starts_in_the_part_then_playing()
    {
        var rest = Edit().From(16);
        var part = Assert.Single(rest.Parts);
        Assert.Equal((34.0, 40.0, 2.0), (part.Start, part.End, part.Speed));
        Assert.Equal(3, rest.Length);
        Assert.Equal(0, rest.FadeIn);
        Assert.Equal(2, rest.FadeOut);
        Assert.Equal(0.5, Edit().From(0.5).FadeIn); // still fading in
        Assert.Equal(Edit().Length, Edit().From(0).Length);
    }

    [Fact]
    public void Clamping_keeps_parts_in_the_video_in_order_and_drops_pauses_at_the_ends()
    {
        var edit = new ClipEdit
        {
            Parts =
            [
                new ClipPart { Start = 0, End = 2, Blank = true },
                new ClipPart { Start = 5, End = 8, Speed = 99, Volume = double.NaN },
                new ClipPart { Start = 7, End = 12 }, // overlaps the one before
                new ClipPart { Start = 12, End = 12.05 }, // too short
                new ClipPart { Start = 290, End = 400 }, // past the end
                new ClipPart { Start = 400, End = 410, Blank = true },
            ],
            FadeIn = 99,
            FadeOut = -3,
        }.Clamped(300);

        Assert.Equal([(5.0, 8.0), (8.0, 12.0), (290.0, 300.0)], edit.Parts.Select(p => (p.Start, p.End)));
        Assert.Equal((ClipPart.MaxSpeed, 1.0), (edit.Parts[0].Speed, edit.Parts[0].Volume));
        Assert.Equal(Math.Min(ClipEdit.MaxFade, edit.Length / 2), edit.FadeIn); // at most half the clip
        Assert.Equal(0, edit.FadeOut);
        // Fades are at most half the clip.
        var shortClip = ClipEdit.Of(new ClipRange(0, 4));
        shortClip.FadeIn = 5;
        Assert.Equal(2, shortClip.Clamped(10).FadeIn);
        Assert.Throws<InvalidOperationException>(() => ClipEdit.Of(new ClipRange(400, 410)).Clamped(300));
    }

    [Fact]
    public void A_clip_survives_saving_with_its_conversion()
    {
        var conversion = ClipTool.ConversionOf(BuiltInData.CreatePresets().Single(p => p.Id == "tool-clip"), "mp4", Edit());
        var json = JsonSerializer.Serialize(new List<Preset> { conversion }, FileeJsonContext.Default.ListPreset);
        var read = JsonSerializer.Deserialize(json, FileeJsonContext.Default.ListPreset)!.Single().Media.Clip!;
        Assert.Equal(Edit().Parts.Select(p => (p.Start, p.End, p.Blank, p.Speed)), read.Parts.Select(p => (p.Start, p.End, p.Blank, p.Speed)));
        Assert.Equal((1.0, 2.0), (read.FadeIn, read.FadeOut));
        // A copy of the preset is a copy of its clip too.
        var copy = conversion.Clone();
        copy.Media.Clip!.Parts[0].Start = 0;
        Assert.Equal(10, conversion.Media.Clip!.Parts[0].Start);
    }
}
