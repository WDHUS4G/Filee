// Hardware video encoding: which encoders are tried where, their options (no GPU or FFmpeg needed), the software retry
// when a hardware encode fails, and the time limit of FFmpeg's quick queries. Real hardware encodes run only on a
// computer with a working GPU encoder and Filee's FFmpeg; CI has neither, so those tests skip there.

using System.Diagnostics;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Infrastructure;
using Filee.Engines.Media;
using Microsoft.Extensions.Logging;

namespace Filee.Engines.Tests;

[Collection(nameof(FfmpegCollection))]
public class HardwareEncodingTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static readonly (string Ffmpeg, string Ffprobe)? Tools = FfmpegConverter.Locate();

    private static readonly VideoStreamInfo Hd = new(1920, 1080, 1, 0, 29.97, "h264", "yuv444p");

    private static readonly MediaInfo HdClip = new(TimeSpan.FromSeconds(10), Hd, new AudioStreamInfo(2, 48000, "aac"), null);

    private static readonly MediaInfo HdrClip = new(TimeSpan.FromSeconds(10),
        new VideoStreamInfo(3840, 2160, 1, 0, 30, "hevc", "yuv420p10le", new VideoColor("bt2020", "smpte2084", "bt2020nc", "tv")),
        new AudioStreamInfo(2, 48000, "aac"), null);

    private static string After(IReadOnlyList<string> args, string option) => args[args.ToList().IndexOf(option) + 1];

    private static IReadOnlyList<string> Args(string target, MediaOptions options, HardwareEncoder? hardware, MediaInfo? info = null) =>
        MediaEncoding.Plan("in.mov", "out." + target, target, options, info ?? HdClip, "work", hardware: hardware).Single().Arguments;

    // ───────────────────────── Which encoders, in which order ─────────────────────────

    [Fact]
    public void Each_system_tries_its_own_encoders_best_first()
    {
        Assert.Equal(
            [new HardwareEncoder("hevc_nvenc", HardwareApi.Nvenc), new("hevc_qsv", HardwareApi.Qsv), new("hevc_amf", HardwareApi.Amf)],
            HardwareVideo.Candidates("hevc", HostPlatform.Windows, encoders: null, vaapiDevice: false));
        Assert.Equal(
            [new HardwareEncoder("h264_videotoolbox", HardwareApi.VideoToolbox),
             new("h264_videotoolbox", HardwareApi.VideoToolbox, HardwareRateControl.Bitrate)],
            HardwareVideo.Candidates("h264", HostPlatform.MacOS, encoders: null, vaapiDevice: false));
        Assert.Equal([new HardwareEncoder("h264_nvenc", HardwareApi.Nvenc)],
            HardwareVideo.Candidates("h264", HostPlatform.Linux, encoders: null, vaapiDevice: false));
        Assert.Equal([new HardwareEncoder("h264_nvenc", HardwareApi.Nvenc), new("h264_vaapi", HardwareApi.Vaapi)],
            HardwareVideo.Candidates("h264", HostPlatform.Linux, encoders: null, vaapiDevice: true));
        Assert.Empty(HardwareVideo.Candidates("h264", HostPlatform.Other, encoders: null, vaapiDevice: true));
        Assert.Empty(HardwareVideo.Candidates("vp9", HostPlatform.Windows, encoders: null, vaapiDevice: false));

        // Only what this FFmpeg was built with: a build without NVENC goes straight to Quick Sync.
        Assert.Equal([new HardwareEncoder("h264_qsv", HardwareApi.Qsv), new("h264_amf", HardwareApi.Amf)],
            HardwareVideo.Candidates("h264", HostPlatform.Windows, new HashSet<string> { "libx264", "h264_qsv", "h264_amf", "hevc_nvenc" }, false));
        Assert.Empty(HardwareVideo.Candidates("hevc", HostPlatform.MacOS, new HashSet<string> { "libx265" }, false));

        Assert.Equal("hevc", new HardwareEncoder("hevc_videotoolbox", HardwareApi.VideoToolbox).Codec);
        Assert.Equal("h264", new HardwareEncoder("h264_qsv", HardwareApi.Qsv).Codec);
        Assert.Equal("av1", new HardwareEncoder("av1_qsv", HardwareApi.Qsv).Codec);
    }

    [Fact]
    public void Av1_tries_nvidia_intel_and_amd_and_stays_on_the_cpu_on_a_mac()
    {
        Assert.Equal(
            [new HardwareEncoder("av1_nvenc", HardwareApi.Nvenc), new("av1_qsv", HardwareApi.Qsv), new("av1_amf", HardwareApi.Amf)],
            HardwareVideo.Candidates("av1", HostPlatform.Windows, encoders: null, vaapiDevice: false));
        Assert.Equal([new HardwareEncoder("av1_nvenc", HardwareApi.Nvenc), new("av1_vaapi", HardwareApi.Vaapi)],
            HardwareVideo.Candidates("av1", HostPlatform.Linux, encoders: null, vaapiDevice: true));
        Assert.Equal([new HardwareEncoder("av1_nvenc", HardwareApi.Nvenc)],
            HardwareVideo.Candidates("av1", HostPlatform.Linux, encoders: null, vaapiDevice: false));
        // VideoToolbox has no AV1 encoder: SVT-AV1 encodes, even with an FFmpeg that lists one by that name.
        Assert.Empty(HardwareVideo.Candidates("av1", HostPlatform.MacOS, encoders: null, vaapiDevice: false));
        Assert.Empty(HardwareVideo.Candidates("av1", HostPlatform.MacOS, new HashSet<string> { "av1_videotoolbox" }, false));
        Assert.Empty(HardwareVideo.Candidates("av1", HostPlatform.Other, encoders: null, vaapiDevice: true));

        // Only what this FFmpeg lists: an older build with H.264/HEVC Quick Sync but no av1_qsv tries nothing.
        Assert.Equal([new HardwareEncoder("av1_qsv", HardwareApi.Qsv)],
            HardwareVideo.Candidates("av1", HostPlatform.Windows, new HashSet<string> { "libsvtav1", "av1_qsv", "hevc_nvenc", "h264_amf" }, false));
        Assert.Empty(HardwareVideo.Candidates("av1", HostPlatform.Windows, new HashSet<string> { "libsvtav1", "h264_qsv", "hevc_qsv" }, false));
    }

    [Fact]
    public void Only_h264_hevc_and_av1_encodes_ask_for_a_hardware_encoder()
    {
        var on = new MediaOptions { HardwareEncoding = true };
        var av1 = new MediaOptions { HardwareEncoding = true, VideoCodec = VideoCodec.Av1 };
        Assert.Equal("h264", MediaEncoding.HardwareCodec("mp4", on, HdClip));
        Assert.Equal("h264", MediaEncoding.HardwareCodec("ts", on, HdClip));
        Assert.Equal("hevc", MediaEncoding.HardwareCodec("mkv", new MediaOptions { HardwareEncoding = true, VideoCodec = VideoCodec.Hevc }, HdClip));
        Assert.Equal("av1", MediaEncoding.HardwareCodec("mp4", av1, HdClip));
        Assert.Equal("av1", MediaEncoding.HardwareCodec("m4v", av1, HdClip));
        Assert.Equal("av1", MediaEncoding.HardwareCodec("mkv", av1, HdClip));
        Assert.Equal("av1", MediaEncoding.HardwareCodec("webm", av1, HdClip));
        // Off, another codec, or a video that is only rewrapped.
        Assert.Null(MediaEncoding.HardwareCodec("mp4", new MediaOptions(), HdClip));
        Assert.Null(MediaEncoding.HardwareCodec("mp4", new MediaOptions { VideoCodec = VideoCodec.Av1 }, HdClip));
        Assert.Null(MediaEncoding.HardwareCodec("webm", on, HdClip));
        Assert.Null(MediaEncoding.HardwareCodec("avi", on, HdClip));
        Assert.Equal("h264", MediaEncoding.HardwareCodec("mov", av1, HdClip)); // MOV has no AV1: H.264 as usual
        Assert.Null(MediaEncoding.HardwareCodec("mp4", av1, HdClip with { Video = Hd with { Codec = "av1", PixelFormat = "yuv420p" } }));
        Assert.Null(MediaEncoding.HardwareCodec("mp3", on, HdClip));
        var playable = HdClip with { Video = Hd with { PixelFormat = "yuv420p" } };
        Assert.Null(MediaEncoding.HardwareCodec("mp4", on, playable));
        Assert.Equal("h264", MediaEncoding.HardwareCodec("mp4", new MediaOptions { HardwareEncoding = true, Quality = MediaQuality.Small }, playable));

        // The editor's list of H.264 targets is the encoder table's.
        Assert.Equal(MediaEncoding.VideoTargets.Where(t => t.Value.VideoCodec == "libx264").Select(t => t.Key).Order(), MediaCodecs.H264Targets.Order());
    }

    // ───────────────────────── Options per encoder ─────────────────────────

    [Fact]
    public void Quality_levels_map_to_each_encoders_rate_control()
    {
        IReadOnlyList<string> Codec(HardwareEncoder encoder, string target, MediaQuality quality, VideoStreamInfo? video = null, int maxHeight = 0) =>
            HardwareVideo.CodecArguments(encoder, target, quality, video ?? Hd, maxHeight);

        Assert.Equal(["-c:v", "h264_nvenc", "-preset", "p5", "-rc", "vbr", "-cq", "24", "-b:v", "0", "-profile:v", "high", "-pix_fmt", "nv12"],
            Codec(new("h264_nvenc", HardwareApi.Nvenc), "mp4", MediaQuality.Balanced));
        Assert.Equal(["-c:v", "hevc_qsv", "-preset", "medium", "-global_quality", "23", "-profile:v", "main", "-pix_fmt", "nv12", "-tag:v", "hvc1"],
            Codec(new("hevc_qsv", HardwareApi.Qsv), "mp4", MediaQuality.High));
        Assert.Equal(["-c:v", "h264_amf", "-quality", "balanced", "-rc", "cqp", "-qp_i", "27", "-qp_p", "29", "-qp_b", "29", "-profile:v", "high", "-pix_fmt", "nv12"],
            Codec(new("h264_amf", HardwareApi.Amf), "mkv", MediaQuality.Small));
        Assert.Equal(["-c:v", "hevc_amf", "-quality", "balanced", "-rc", "cqp", "-qp_i", "25", "-qp_p", "27", "-profile:v", "main", "-pix_fmt", "nv12"],
            Codec(new("hevc_amf", HardwareApi.Amf), "mkv", MediaQuality.Balanced));
        Assert.Equal(["-c:v", "h264_videotoolbox", "-q:v", "60", "-profile:v", "high", "-pix_fmt", "nv12"],
            Codec(new("h264_videotoolbox", HardwareApi.VideoToolbox), "mov", MediaQuality.Balanced));
        Assert.Equal(["-c:v", "hevc_vaapi", "-rc_mode", "CQP", "-qp", "31", "-profile:v", "main"],
            Codec(new("hevc_vaapi", HardwareApi.Vaapi), "mkv", MediaQuality.Small));

        // Intel Macs: a bitrate from the output picture, 1080p30 H.264 Balanced ≈ 5.6 Mbit/s; less when scaled to 720p.
        var bitrate = new HardwareEncoder("h264_videotoolbox", HardwareApi.VideoToolbox, HardwareRateControl.Bitrate);
        Assert.Equal(["-c:v", "h264_videotoolbox", "-b:v", "5593k", "-profile:v", "high", "-pix_fmt", "nv12"],
            Codec(bitrate, "mp4", MediaQuality.Balanced));
        Assert.Equal("2486k", After(Codec(bitrate, "mp4", MediaQuality.Balanced, maxHeight: 720), "-b:v"));
        Assert.True(HardwareVideo.BitrateKbps("hevc", MediaQuality.High, Hd, 0) < HardwareVideo.BitrateKbps("h264", MediaQuality.High, Hd, 0));
        Assert.True(HardwareVideo.BitrateKbps("h264", MediaQuality.Small, Hd, 0) < HardwareVideo.BitrateKbps("h264", MediaQuality.Balanced, Hd, 0));
        Assert.Equal(300, HardwareVideo.BitrateKbps("hevc", MediaQuality.Small, new VideoStreamInfo(64, 64, 1, 0, 5), 0));

        // Coarser than the software CRF by one, finer for High than for Small.
        Assert.Equal([19, 24, 29], new[] { MediaQuality.High, MediaQuality.Balanced, MediaQuality.Small }.Select(q => HardwareVideo.Quantizer("h264", q)));
        Assert.Equal([23, 27, 31], new[] { MediaQuality.High, MediaQuality.Balanced, MediaQuality.Small }.Select(q => HardwareVideo.Quantizer("hevc", q)));
    }

    [Fact]
    public void Av1_gets_each_encoders_own_quality_scale()
    {
        IReadOnlyList<string> Codec(string name, HardwareApi api, string target, MediaQuality quality) =>
            HardwareVideo.CodecArguments(new HardwareEncoder(name, api), target, quality, Hd, 0);

        // NVENC: AV1's 0–63 quantizer, two above SVT-AV1's CRF 26/32/40.
        Assert.Equal(["-c:v", "av1_nvenc", "-preset", "p5", "-rc", "vbr", "-cq", "34", "-b:v", "0", "-pix_fmt", "nv12"],
            Codec("av1_nvenc", HardwareApi.Nvenc, "mp4", MediaQuality.Balanced));
        // Quick Sync: ICQ is 1–51 for AV1 as for HEVC.
        Assert.Equal(["-c:v", "av1_qsv", "-preset", "medium", "-global_quality", "23", "-pix_fmt", "nv12"],
            Codec("av1_qsv", HardwareApi.Qsv, "mkv", MediaQuality.High));
        // AMF and VA-API: the 0–255 quantizer index, four times the quantizer.
        Assert.Equal(["-c:v", "av1_amf", "-quality", "balanced", "-rc", "cqp", "-qp_i", "160", "-qp_p", "168", "-qp_b", "168", "-pix_fmt", "nv12"],
            Codec("av1_amf", HardwareApi.Amf, "webm", MediaQuality.Small));
        Assert.Equal(["-c:v", "av1_vaapi", "-rc_mode", "CQP", "-global_quality", "136"],
            Codec("av1_vaapi", HardwareApi.Vaapi, "mkv", MediaQuality.Balanced));

        Assert.Equal([28, 34, 42], new[] { MediaQuality.High, MediaQuality.Balanced, MediaQuality.Small }.Select(HardwareVideo.Av1Quantizer));
        Assert.Equal([112, 136, 168], new[] { MediaQuality.High, MediaQuality.Balanced, MediaQuality.Small }.Select(HardwareVideo.Av1QIndex));
        Assert.Equal([23, 27, 31], new[] { MediaQuality.High, MediaQuality.Balanced, MediaQuality.Small }.Select(q => HardwareVideo.Quantizer("av1", q)));
        // Every value inside the range each encoder accepts (NVENC -cq 0–63, AMF/VA-API 0–255, Quick Sync ICQ 1–51).
        foreach (var quality in new[] { MediaQuality.High, MediaQuality.Balanced, MediaQuality.Small })
        {
            Assert.InRange(HardwareVideo.Av1Quantizer(quality), 1, 63);
            Assert.InRange(HardwareVideo.Av1QIndex(quality) - 8, 0, 255);
            Assert.InRange(HardwareVideo.Av1QIndex(quality), 0, 255);
            Assert.InRange(HardwareVideo.Quantizer("av1", quality), 1, 51);
        }

        // No profile (each encoder picks Main; av1_nvenc has no -profile) and no tag, in MP4 as in MKV and WebM.
        foreach (var (name, api) in new[] { ("av1_nvenc", HardwareApi.Nvenc), ("av1_qsv", HardwareApi.Qsv), ("av1_amf", HardwareApi.Amf), ("av1_vaapi", HardwareApi.Vaapi) })
        {
            foreach (var target in new[] { "mp4", "m4v", "mkv", "webm" })
            {
                var args = Codec(name, api, target, MediaQuality.Balanced);
                Assert.DoesNotContain("-profile:v", args);
                Assert.DoesNotContain("-tag:v", args);
                Assert.DoesNotContain("-qp", args);
            }
        }
    }

    [Fact]
    public void Ten_bit_and_hdr_stay_ten_bit_on_hevc_and_h264_stays_eight_bit()
    {
        var hdr = HdrClip.Video!;
        var nvenc = HardwareVideo.CodecArguments(new("hevc_nvenc", HardwareApi.Nvenc), "mp4", MediaQuality.Balanced, hdr, 0);
        Assert.Equal(("main10", "p010le", "hvc1"), (After(nvenc, "-profile:v"), After(nvenc, "-pix_fmt"), After(nvenc, "-tag:v")));
        Assert.Equal(("bt2020", "smpte2084", "bt2020nc"), (After(nvenc, "-color_primaries"), After(nvenc, "-color_trc"), After(nvenc, "-colorspace")));

        // H.264 is 8-bit 4:2:0 whatever the source, as with x264.
        var h264 = HardwareVideo.CodecArguments(new("h264_qsv", HardwareApi.Qsv), "mp4", MediaQuality.Balanced, hdr, 0);
        Assert.Equal(("high", "nv12"), (After(h264, "-profile:v"), After(h264, "-pix_fmt")));
        Assert.DoesNotContain("-color_trc", h264);
        Assert.False(HardwareVideo.TenBit("h264", hdr));
        Assert.True(HardwareVideo.TenBit("hevc", hdr));
        Assert.True(HardwareVideo.TenBit("hevc", Hd with { PixelFormat = "p010le" }));
        Assert.False(HardwareVideo.TenBit("hevc", Hd));

        // AV1 keeps 10 bits as SVT-AV1 does: P010 with the colour tags, no profile to name.
        var av1 = HardwareVideo.CodecArguments(new("av1_qsv", HardwareApi.Qsv), "mp4", MediaQuality.Balanced, hdr, 0);
        Assert.Equal("p010le", After(av1, "-pix_fmt"));
        Assert.Equal(("bt2020", "smpte2084", "bt2020nc"), (After(av1, "-color_primaries"), After(av1, "-color_trc"), After(av1, "-colorspace")));
        Assert.DoesNotContain("-profile:v", av1);
        Assert.True(HardwareVideo.TenBit("av1", hdr));
        Assert.False(HardwareVideo.TenBit("av1", Hd));
        Assert.Equal("nv12", After(HardwareVideo.CodecArguments(new("av1_nvenc", HardwareApi.Nvenc), "mkv", MediaQuality.Balanced, Hd, 0), "-pix_fmt"));

        // VA-API: 10-bit frames are uploaded as P010.
        var vaapi = new HardwareEncoder("hevc_vaapi", HardwareApi.Vaapi);
        Assert.Equal(",format=p010le,hwupload", HardwareVideo.FilterSuffix(vaapi, tenBit: true));
        Assert.Equal(",format=nv12,hwupload", HardwareVideo.FilterSuffix(vaapi, tenBit: false));
        Assert.Equal("", HardwareVideo.FilterSuffix(new("hevc_nvenc", HardwareApi.Nvenc), tenBit: true));
    }

    [Fact]
    public void The_plan_uses_the_hardware_encoder_only_where_it_applies()
    {
        var on = new MediaOptions { HardwareEncoding = true };
        var nvenc = new HardwareEncoder("h264_nvenc", HardwareApi.Nvenc);
        var mp4 = Args("mp4", on, nvenc);
        Assert.Equal(("h264_nvenc", "24", "copy"), (After(mp4, "-c:v"), After(mp4, "-cq"), After(mp4, "-c:a"))); // the AAC sound is kept as it is
        Assert.Equal("scale=w='trunc(iw/2)*2':h='trunc(ih/2)*2'", After(mp4, "-vf"));
        Assert.Equal("+faststart", After(mp4, "-movflags"));
        Assert.DoesNotContain("-crf", mp4);

        // HEVC keeps the hvc1 tag in MP4 and MOV, and the 10 bits of an HDR source.
        var hevcOptions = new MediaOptions { HardwareEncoding = true, VideoCodec = VideoCodec.Hevc, MaxHeight = 1080 };
        var hevc = Args("mov", hevcOptions, new HardwareEncoder("hevc_videotoolbox", HardwareApi.VideoToolbox), HdrClip);
        Assert.Equal(("hevc_videotoolbox", "hvc1", "p010le", "main10"), (After(hevc, "-c:v"), After(hevc, "-tag:v"), After(hevc, "-pix_fmt"), After(hevc, "-profile:v")));
        Assert.DoesNotContain("-tag:v", Args("mkv", hevcOptions, new HardwareEncoder("hevc_nvenc", HardwareApi.Nvenc), HdrClip));

        // VA-API: the device before the input, the upload after the scaling.
        var vaapi = Args("mkv", on, new HardwareEncoder("h264_vaapi", HardwareApi.Vaapi));
        Assert.True(vaapi.ToList().IndexOf("-vaapi_device") < vaapi.ToList().IndexOf("-i"));
        Assert.Equal(HardwareVideo.VaapiDevice, After(vaapi, "-vaapi_device"));
        Assert.Equal("scale=w='trunc(iw/2)*2':h='trunc(ih/2)*2',format=nv12,hwupload", After(vaapi, "-vf"));
        Assert.DoesNotContain("-pix_fmt", vaapi);

        // Software when the preset doesn't ask, when the encoder is for another codec, and the copy rules stay.
        Assert.Equal("libx264", After(Args("mp4", new MediaOptions(), nvenc), "-c:v"));
        Assert.Equal("libx265", After(Args("mp4", new MediaOptions { HardwareEncoding = true, VideoCodec = VideoCodec.Hevc }, nvenc), "-c:v"));
        Assert.Equal("libvpx-vp9", After(Args("webm", on, nvenc), "-c:v"));
        var phone = HdClip with { Video = new VideoStreamInfo(1920, 1080, 1, 0, 30, "h264", "yuv420p") };
        Assert.Equal("copy", After(Args("mp4", on, nvenc, phone), "-c:v"));
        // A missing x265 doesn't matter while the hardware encoder writes HEVC.
        Assert.Equal("hevc_nvenc", After(MediaEncoding.Plan("in.mov", "out.mkv", "mkv", new MediaOptions { HardwareEncoding = true, VideoCodec = VideoCodec.Hevc },
            HdClip, "work", encoders: new HashSet<string> { "aac" }, hardware: new HardwareEncoder("hevc_nvenc", HardwareApi.Nvenc)).Single().Arguments, "-c:v"));
    }

    [Fact]
    public void The_plan_writes_av1_on_the_gpu_into_mp4_mkv_and_webm()
    {
        var av1 = new MediaOptions { HardwareEncoding = true, VideoCodec = VideoCodec.Av1 };
        var qsv = new HardwareEncoder("av1_qsv", HardwareApi.Qsv);
        var mp4 = Args("mp4", av1, qsv);
        Assert.Equal(("av1_qsv", "27", "nv12", "+faststart"), (After(mp4, "-c:v"), After(mp4, "-global_quality"), After(mp4, "-pix_fmt"), After(mp4, "-movflags")));
        Assert.DoesNotContain("-tag:v", mp4);
        Assert.DoesNotContain("-crf", mp4);

        // WebM: the GPU writes the video, the sound becomes Opus as usual.
        var webm = Args("webm", av1, new HardwareEncoder("av1_nvenc", HardwareApi.Nvenc));
        Assert.Equal(("av1_nvenc", "34", "libopus", "webm"), (After(webm, "-c:v"), After(webm, "-cq"), After(webm, "-c:a"), After(webm, "-f")));

        // 10-bit HDR stays 10-bit; VA-API uploads it as P010.
        var vaapi = Args("mkv", av1, new HardwareEncoder("av1_vaapi", HardwareApi.Vaapi), HdrClip);
        Assert.Equal(("av1_vaapi", "136", "scale=w='trunc(iw/2)*2':h='trunc(ih/2)*2',format=p010le,hwupload"),
            (After(vaapi, "-c:v"), After(vaapi, "-global_quality"), After(vaapi, "-vf")));
        Assert.True(vaapi.ToList().IndexOf("-vaapi_device") < vaapi.ToList().IndexOf("-i"));

        // Without a working encoder (or with one for another codec) SVT-AV1 encodes as before.
        Assert.Equal("libsvtav1", After(Args("mp4", av1, null), "-c:v"));
        Assert.Equal("libsvtav1", After(Args("mp4", av1, new HardwareEncoder("hevc_qsv", HardwareApi.Qsv)), "-c:v"));
        Assert.Equal("h264_qsv", After(Args("mp4", new MediaOptions { HardwareEncoding = true }, new HardwareEncoder("h264_qsv", HardwareApi.Qsv)), "-c:v"));
        Assert.Equal("libx264", After(Args("mp4", new MediaOptions { HardwareEncoding = true }, qsv), "-c:v"));
        // A missing SVT-AV1 doesn't matter while the hardware encoder writes AV1.
        Assert.Equal("av1_amf", After(MediaEncoding.Plan("in.mov", "out.mkv", "mkv", av1, HdClip, "work", encoders: new HashSet<string> { "aac" },
            hardware: new HardwareEncoder("av1_amf", HardwareApi.Amf)).Single().Arguments, "-c:v"));
    }

    [Fact]
    public void The_probe_is_a_tiny_test_encode_with_the_real_options()
    {
        var qsv = HardwareVideo.ProbeArguments(new HardwareEncoder("hevc_qsv", HardwareApi.Qsv), tenBit: true);
        Assert.Equal(["-f", "lavfi", "-i", "testsrc=size=256x144:rate=30:duration=0.1"], qsv.SkipWhile(a => a != "-f").Take(4));
        Assert.Equal(("hevc_qsv", "p010le", "main10", "27"), (After(qsv, "-c:v"), After(qsv, "-pix_fmt"), After(qsv, "-profile:v"), After(qsv, "-global_quality")));
        Assert.Equal(["-f", "null", "-"], qsv.TakeLast(3));

        var vaapi = HardwareVideo.ProbeArguments(new HardwareEncoder("h264_vaapi", HardwareApi.Vaapi), tenBit: false);
        Assert.True(vaapi.IndexOf("-vaapi_device") < vaapi.IndexOf("-i"));
        Assert.Equal("null,format=nv12,hwupload", After(vaapi, "-vf"));

        var intelMac = HardwareVideo.ProbeArguments(new HardwareEncoder("h264_videotoolbox", HardwareApi.VideoToolbox, HardwareRateControl.Bitrate), false);
        Assert.Contains("-b:v", intelMac);
        Assert.DoesNotContain("-q:v", intelMac);

        // AV1 is probed with its own options, 10-bit too: a chip without AV1 (or without 10-bit AV1) fails here.
        var av1 = HardwareVideo.ProbeArguments(new HardwareEncoder("av1_qsv", HardwareApi.Qsv), tenBit: true);
        Assert.Equal(("av1_qsv", "p010le", "27"), (After(av1, "-c:v"), After(av1, "-pix_fmt"), After(av1, "-global_quality")));
        Assert.Equal(["-f", "null", "-"], av1.TakeLast(3));
    }

    // ───────────────────────── Software retry ─────────────────────────

    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries)
                Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    private static IReadOnlyList<FfmpegPass> FakePlan(HardwareEncoder? encoder) =>
        [new FfmpegPass(["-c:v", encoder?.Name ?? "libx264"], 1)];

    private static Task Fallback(HardwareEncoder? hardware, Func<IReadOnlyList<FfmpegPass>, Task> run, string output, ILogger logger,
        CancellationToken cancellationToken) =>
        FfmpegConverter.RunWithFallbackAsync(FakePlan(hardware), hardware, FakePlan, run, output, logger, cancellationToken);

    [Fact]
    public async Task A_failing_hardware_encode_is_redone_in_software_and_logged()
    {
        var output = Path.Combine(fx.NewFolder(), "out.mp4");
        var runs = new List<string>();
        var logger = new ListLogger();
        Task Run(IReadOnlyList<FfmpegPass> passes)
        {
            var encoder = After(passes[0].Arguments, "-c:v");
            runs.Add(encoder);
            if (encoder != "h264_nvenc")
                return Task.CompletedTask;
            File.WriteAllText(output, "half-written");
            throw new InvalidOperationException("FFmpeg failed (exit code -22): No capable devices found");
        }

        await Fallback(new HardwareEncoder("h264_nvenc", HardwareApi.Nvenc), Run, output, logger, CancellationToken.None);

        Assert.Equal(["h264_nvenc", "libx264"], runs);
        Assert.False(File.Exists(output)); // the partial hardware output is gone before the retry
        var (level, message) = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, level);
        Assert.Contains("h264_nvenc", message);
        Assert.Contains("No capable devices found", message);
    }

    [Fact]
    public async Task Success_cancellation_and_software_failures_are_not_retried()
    {
        var output = Path.Combine(fx.NewFolder(), "out.mp4");
        var logger = new ListLogger();
        var nvenc = new HardwareEncoder("h264_nvenc", HardwareApi.Nvenc);

        var runs = 0;
        await Fallback(nvenc, _ => { runs++; return Task.CompletedTask; }, output, logger, CancellationToken.None);
        Assert.Equal(1, runs);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        runs = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Fallback(nvenc,
            _ => { runs++; throw new OperationCanceledException(cancelled.Token); }, output, logger, cancelled.Token));
        Assert.Equal(1, runs);

        // Without hardware a failure is the user's error message, as before.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Fallback(null,
            _ => throw new InvalidOperationException("FFmpeg failed"), output, logger, CancellationToken.None));
        Assert.Equal("FFmpeg failed", error.Message);
        // And when the software retry fails too, its error is the one reported.
        error = await Assert.ThrowsAsync<InvalidOperationException>(() => Fallback(nvenc,
            passes => throw new InvalidOperationException("failed with " + After(passes[0].Arguments, "-c:v")), output, logger, CancellationToken.None));
        Assert.Equal("failed with libx264", error.Message);
        Assert.DoesNotContain(logger.Entries, e => e.Level > LogLevel.Warning);
    }

    [Fact]
    public async Task An_encoder_ffmpeg_does_not_have_falls_back_to_x264_in_a_real_conversion()
    {
        Assert.SkipUnless(Tools is not null, "FFmpeg is not installed (pwsh build/fetch-engines.ps1 -Only ffmpeg)");
        var folder = fx.NewFolder();
        var source = await MakeClipAsync(folder, "clip.mov");
        var logger = new ListLogger();
        var converter = new FfmpegConverter(logger)
        {
            SelectHardware = (_, codec, _, _, _) => Task.FromResult<HardwareEncoder?>(new HardwareEncoder($"{codec}_filee_missing", HardwareApi.Nvenc)),
        };
        Assert.True(converter.GetStatus().IsAvailable);

        var outputs = await converter.ConvertAsync(Step(source, "mov", "mkv", folder, new MediaOptions { HardwareEncoding = true, Quality = MediaQuality.Small }),
            null, CancellationToken.None);

        Assert.Equal("h264", await CodecOfAsync(Assert.Single(outputs)));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("h264_filee_missing", StringComparison.Ordinal));
    }

    /// <summary>Runs only where a GPU encoder works (e.g. a laptop with Intel graphics); CI has none.</summary>
    [Theory]
    [InlineData(VideoCodec.Default, "h264")]
    [InlineData(VideoCodec.Hevc, "hevc")]
    [InlineData(VideoCodec.Av1, "av1")]
    public async Task A_working_hardware_encoder_writes_the_video(VideoCodec videoCodec, string codec)
    {
        Assert.SkipUnless(Tools is not null, "FFmpeg is not installed (pwsh build/fetch-engines.ps1 -Only ffmpeg)");
        var ffmpeg = Tools!.Value.Ffmpeg;
        var logger = new ListLogger();
        var hardware = await HardwareProbe.SelectAsync(ffmpeg, codec, false, FfmpegConverter.EncodersOf(ffmpeg), logger, CancellationToken.None);
        Assert.SkipUnless(hardware is not null, $"No {codec} hardware encoder works on this computer");

        var folder = fx.NewFolder();
        var source = await MakeClipAsync(folder, "clip.mov");
        var converter = new FfmpegConverter(logger);
        converter.GetStatus();
        var outputs = await converter.ConvertAsync(Step(source, "mov", "mp4", folder,
            new MediaOptions { HardwareEncoding = true, Quality = MediaQuality.Small, VideoCodec = videoCodec }), null, CancellationToken.None);

        Assert.Equal(codec, await CodecOfAsync(Assert.Single(outputs)));
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning); // no software retry
    }

    /// <summary>
    /// With the real probe: where a chip encodes AV1 the GPU writes it, elsewhere (CI, an older chip such as Intel Iris
    /// Xe) the probes fail quietly and SVT-AV1 does. Either way the file is AV1 and no hardware encode had to be redone.
    /// </summary>
    [Fact]
    public async Task An_av1_preset_with_hardware_encoding_writes_av1_with_or_without_a_capable_chip()
    {
        Assert.SkipUnless(Tools is not null, "FFmpeg is not installed (pwsh build/fetch-engines.ps1 -Only ffmpeg)");
        var folder = fx.NewFolder();
        var source = await MakeClipAsync(folder, "clip.mov");
        var logger = new ListLogger();
        var converter = new FfmpegConverter(logger);
        Assert.True(converter.GetStatus().IsAvailable);

        var outputs = await converter.ConvertAsync(Step(source, "mov", "mp4", folder,
            new MediaOptions { HardwareEncoding = true, Quality = MediaQuality.Small, VideoCodec = VideoCodec.Av1 }), null, CancellationToken.None);

        Assert.Equal("av1", await CodecOfAsync(Assert.Single(outputs)));
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    // ───────────────────────── Quick queries ─────────────────────────

    [Fact]
    public void Version_and_encoder_queries_give_up_on_an_ffmpeg_that_hangs()
    {
        var hanging = HangingProgram(fx.NewFolder());
        var watch = Stopwatch.StartNew();
        Assert.Throws<InvalidOperationException>(() => FfmpegConverter.VersionOf(hanging, TimeSpan.FromSeconds(1)));
        Assert.Null(FfmpegConverter.EncodersOf(hanging, TimeSpan.FromSeconds(1)));
        // Before, both read the output to its end first and waited as long as the program hung (here a minute).
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"took {watch.Elapsed}");
    }

    [Fact]
    public void Encoder_lists_are_read()
    {
        var encoders = FfmpegConverter.ParseEncoders(
            "Encoders:\n V..... = Video\n ------\n V....D libx264              libx264 H.264 / AVC\n" +
            " V..... h264_qsv             H.264 (Intel Quick Sync Video acceleration) (codec h264)\r\n A....D aac                  AAC\n");
        Assert.Contains("libx264", encoders);
        Assert.Contains("h264_qsv", encoders);
        Assert.Contains("aac", encoders);
        Assert.DoesNotContain("Video", encoders);
    }

    /// <summary>A program that writes nothing and runs for a minute, keeping its output open (a hung FFmpeg).</summary>
    private static string HangingProgram(string folder)
    {
        if (OperatingSystem.IsWindows())
        {
            var script = Path.Combine(folder, "hanging-ffmpeg.cmd");
            File.WriteAllText(script, "@ping -n 60 127.0.0.1 >nul\r\n");
            return script;
        }
        var shell = Path.Combine(folder, "hanging-ffmpeg");
        File.WriteAllText(shell, "#!/bin/sh\nsleep 60\n");
        File.SetUnixFileMode(shell, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return shell;
    }

    // ───────────────────────── Helpers ─────────────────────────

    private sealed class FolderAllocator(string folder) : IOutputAllocator
    {
        public string? Allocate(string extension, string? suffix = null) => Path.Combine(folder, $"out{suffix}.{extension}");
    }

    private static ConversionStep Step(string input, string from, string to, string folder, MediaOptions media) =>
        new(input, from, to, new Preset { TargetFormat = to, Media = media }, new FolderAllocator(folder), folder);

    /// <summary>A one-second 320×240 test pattern with a tone, in a yuv444p H.264 that every target re-encodes.</summary>
    private static async Task<string> MakeClipAsync(string folder, string name)
    {
        var path = Path.Combine(folder, name);
        var result = await ProcessRunner.RunAsync(Tools!.Value.Ffmpeg,
            ["-hide_banner", "-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=25:duration=1",
             "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100:duration=1", "-c:v", "libx264", "-pix_fmt", "yuv444p", path],
            TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.True(result.ExitCode == 0, result.StandardError);
        return path;
    }

    private static async Task<string> CodecOfAsync(string file)
    {
        var result = await ProcessRunner.RunAsync(Tools!.Value.Ffprobe,
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=codec_name", "-of", "default=nw=1:nk=1", file],
            TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.True(result.ExitCode == 0, result.StandardError);
        return result.StandardOutput.Trim();
    }
}
