// What the clip window reads of a video: its length and pictures of its frames, the loudness of its sound and where
// it is silent, and the clip played with its sound. Filee's FFmpeg does it; tests stand in for it.

using Filee.Core.Presets;
using Filee.Engines.Media;

namespace Filee.App.Services;

/// <summary>Reads a video for the clip window and plays clips of it.</summary>
public interface IVideoFrameSource
{
    /// <summary>The video's length and size; throws when it can't be read.</summary>
    Task<VideoSummary> ProbeAsync(string path, CancellationToken cancellationToken);

    /// <summary>Writes the frame at <paramref name="seconds"/> as a JPEG; false when there is none.</summary>
    Task<bool> WriteFrameAsync(string path, double seconds, string output, int maxHeight, CancellationToken cancellationToken);

    /// <summary>
    /// The loudness of the sound from <paramref name="start"/> to <paramref name="end"/> seconds in
    /// <paramref name="count"/> steps (0 to 1); empty without sound.
    /// </summary>
    Task<float[]> ReadLoudnessAsync(string path, double start, double end, int count, string workFolder, CancellationToken cancellationToken);

    /// <summary>How long the sound of a file plays (music for the clip); throws when it has none.</summary>
    Task<double> ReadSoundLengthAsync(string path, CancellationToken cancellationToken);

    /// <summary>The silent stretches between <paramref name="start"/> and <paramref name="end"/>.</summary>
    Task<IReadOnlyList<ClipRange>> FindSilencesAsync(string path, double start, double end, CancellationToken cancellationToken);

    /// <summary>
    /// Plays <paramref name="clip"/> with its sound: each picture to <paramref name="onFrame"/> (BGRA pixels, width,
    /// height, seconds into the clip) on a worker thread, when it is due. Ends when played or cancelled.
    /// </summary>
    Task PlayAsync(string path, ClipEdit clip, int height, Action<byte[], int, int, double> onFrame, CancellationToken cancellationToken);
}

/// <summary>All of it with Filee's FFmpeg (<see cref="VideoFrames"/>, <see cref="ClipPreview"/>).</summary>
public sealed class FfmpegFrameSource : IVideoFrameSource
{
    public Task<VideoSummary> ProbeAsync(string path, CancellationToken cancellationToken) =>
        VideoFrames.ProbeAsync(path, cancellationToken);

    public Task<bool> WriteFrameAsync(string path, double seconds, string output, int maxHeight, CancellationToken cancellationToken) =>
        VideoFrames.WriteFrameAsync(path, seconds, output, maxHeight, cancellationToken);

    public Task<float[]> ReadLoudnessAsync(string path, double start, double end, int count, string workFolder, CancellationToken cancellationToken) =>
        ClipPreview.ReadLoudnessAsync(path, start, end, count, workFolder, cancellationToken);

    public Task<double> ReadSoundLengthAsync(string path, CancellationToken cancellationToken) =>
        ClipPreview.ReadSoundLengthAsync(path, cancellationToken);

    public Task<IReadOnlyList<ClipRange>> FindSilencesAsync(string path, double start, double end, CancellationToken cancellationToken) =>
        ClipPreview.FindSilencesAsync(path, start, end, cancellationToken);

    public Task PlayAsync(string path, ClipEdit clip, int height, Action<byte[], int, int, double> onFrame, CancellationToken cancellationToken) =>
        ClipPreview.PlayAsync(path, clip, height, onFrame, cancellationToken);
}
