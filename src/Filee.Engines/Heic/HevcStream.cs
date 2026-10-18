// Splits the raw HEVC stream FFmpeg writes (Annex B, NAL units behind start codes) into its parameter sets and one
// coded picture per tile, and builds the decoder configuration record (hvcC) that HEIF stores them in.

using System.Buffers.Binary;

namespace Filee.Engines.Heic;

/// <summary>An all-intra HEVC stream, split the way HEIF stores it.</summary>
/// <param name="Configuration">Payload of the <c>hvcC</c> box: profile, chroma format, bit depth and VPS/SPS/PPS.</param>
/// <param name="Pictures">Each coded picture as length-prefixed (4 bytes, big-endian) NAL units, ready for <c>mdat</c>.</param>
/// <param name="BitDepth">Luma bit depth (8 for Main, 10 for Main 10).</param>
internal sealed record HevcStream(byte[] Configuration, IReadOnlyList<byte[]> Pictures, int BitDepth)
{
    private const int IdrWithLeadingPictures = 19, IdrWithoutLeadingPictures = 20, Vps = 32, Sps = 33, Pps = 34;

    /// <summary>
    /// Splits an Annex B stream. Every picture must be an IDR picture; the parameter sets x265 repeats before each
    /// one must not change, because all tiles share one hvcC.
    /// </summary>
    /// <exception cref="InvalidDataException">The stream lacks parameter sets or pictures, changes them, holds a
    /// picture that is not an IDR picture, or uses sub-layers.</exception>
    public static HevcStream Parse(byte[] annexB)
    {
        byte[]? vps = null, sps = null, pps = null;
        var pictures = new List<List<byte[]>>();
        foreach (var unit in Units(annexB))
        {
            var type = (unit[0] >> 1) & 0x3F;
            switch (type)
            {
                case Vps: vps = Same(vps, unit); break;
                case Sps: sps = Same(sps, unit); break;
                case Pps: pps = Same(pps, unit); break;
                case < IdrWithLeadingPictures or > IdrWithoutLeadingPictures and < 32:
                    throw new InvalidDataException($"The HEVC stream holds a picture that is not an IDR picture (NAL unit type {type}).");
                case < 32:
                    // Video coding layer: a slice with first_slice_segment_in_pic_flag starts the next picture.
                    if ((unit[2] & 0x80) != 0 || pictures.Count == 0)
                        pictures.Add([]);
                    pictures[^1].Add(unit);
                    break;
                default:
                    // SEI, access unit delimiters, end of stream: HEIF has no use for them.
                    break;
            }
        }
        if (vps is null || sps is null || pps is null || pictures.Count == 0)
            throw new InvalidDataException("The HEVC stream has no parameter sets or no picture.");

        var (config, depth) = BuildConfiguration(vps, sps, pps);
        return new HevcStream(config, pictures.Select(LengthPrefixed).ToList(), depth);
    }

    private static byte[] Same(byte[]? known, byte[] unit) =>
        known is null || known.AsSpan().SequenceEqual(unit)
            ? unit
            : throw new InvalidDataException("The HEVC stream changes its parameter sets between pictures.");

    /// <summary>NAL units of an Annex B stream, without their start codes.</summary>
    internal static List<byte[]> Units(byte[] data)
    {
        var starts = new List<int>();
        for (var i = 0; i + 2 < data.Length; i++)
        {
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
            {
                starts.Add(i + 3);
                i += 2;
            }
        }
        var units = new List<byte[]>();
        for (var k = 0; k < starts.Count; k++)
        {
            var end = k + 1 < starts.Count ? starts[k + 1] - 3 : data.Length;
            // A four-byte start code leaves one zero byte before the next unit; NAL units never end in zero bytes.
            while (end > starts[k] && data[end - 1] == 0)
                end--;
            if (end - starts[k] >= 3)
                units.Add(data[starts[k]..end]);
        }
        return units;
    }

    private static byte[] LengthPrefixed(List<byte[]> units)
    {
        var result = new byte[units.Sum(u => 4 + u.Length)];
        var at = 0;
        foreach (var unit in units)
        {
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(at), (uint)unit.Length);
            unit.CopyTo(result, at + 4);
            at += 4 + unit.Length;
        }
        return result;
    }

    /// <summary>The HEVCDecoderConfigurationRecord (ISO/IEC 14496-15, 8.3.3) with one VPS, SPS and PPS.</summary>
    private static (byte[] Configuration, int BitDepth) BuildConfiguration(byte[] vps, byte[] sps, byte[] pps)
    {
        var info = SequenceInfo.Read(sps);
        using var output = new MemoryStream();
        output.WriteByte(1); // configurationVersion
        output.Write(info.ProfileTierLevel);
        Write16(output, 0xF000); // reserved + min_spatial_segmentation_idc 0
        output.WriteByte(0xFC); // reserved + parallelismType 0 (unknown)
        output.WriteByte((byte)(0xFC | info.ChromaFormat));
        output.WriteByte((byte)(0xF8 | (info.LumaBitDepth - 8)));
        output.WriteByte((byte)(0xF8 | (info.ChromaBitDepth - 8)));
        Write16(output, 0); // avgFrameRate unknown
        // constantFrameRate 0, numTemporalLayers, temporalIdNested, lengthSizeMinusOne 3 (4-byte lengths)
        output.WriteByte((byte)((info.TemporalLayers << 3) | (info.TemporalIdNested ? 0x04 : 0) | 0x03));
        output.WriteByte(3); // numOfArrays
        foreach (var (type, unit) in new[] { (Vps, vps), (Sps, sps), (Pps, pps) })
        {
            output.WriteByte((byte)(0x80 | type)); // array_completeness: these are all the sets there are
            Write16(output, 1);
            Write16(output, unit.Length);
            output.Write(unit);
        }
        return (output.ToArray(), info.LumaBitDepth);
    }

    private static void Write16(Stream stream, int value)
    {
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    /// <summary>The fields of a sequence parameter set (H.265, 7.3.2.2) that hvcC repeats.</summary>
    private sealed record SequenceInfo(byte[] ProfileTierLevel, int ChromaFormat, int LumaBitDepth, int ChromaBitDepth,
        int TemporalLayers, bool TemporalIdNested)
    {
        public static SequenceInfo Read(byte[] sps)
        {
            var bits = new BitReader(Unescape(sps.AsSpan(2))); // after the two-byte NAL unit header
            bits.Read(4); // sps_video_parameter_set_id
            var subLayers = bits.Read(3);
            var nested = bits.Read(1) == 1;
            if (subLayers != 0)
                throw new InvalidDataException("HEVC streams with temporal sub-layers are not supported.");
            // general_profile_space … general_level_idc: 12 bytes, copied as they are.
            var ptl = new byte[12];
            for (var i = 0; i < ptl.Length; i++)
                ptl[i] = (byte)bits.Read(8);
            bits.ReadExpGolomb(); // sps_seq_parameter_set_id
            var chroma = bits.ReadExpGolomb();
            if (chroma == 3)
                bits.Read(1); // separate_colour_plane_flag
            bits.ReadExpGolomb(); // pic_width_in_luma_samples
            bits.ReadExpGolomb(); // pic_height_in_luma_samples
            if (bits.Read(1) == 1) // conformance_window_flag
            {
                for (var i = 0; i < 4; i++)
                    bits.ReadExpGolomb();
            }
            var luma = bits.ReadExpGolomb() + 8;
            var chromaDepth = bits.ReadExpGolomb() + 8;
            return new SequenceInfo(ptl, chroma, luma, chromaDepth, subLayers + 1, nested);
        }

        /// <summary>Removes emulation prevention bytes (00 00 03 → 00 00).</summary>
        private static byte[] Unescape(ReadOnlySpan<byte> data)
        {
            var result = new List<byte>(data.Length);
            var zeros = 0;
            foreach (var b in data)
            {
                if (zeros >= 2 && b == 3)
                {
                    zeros = 0;
                    continue;
                }
                result.Add(b);
                zeros = b == 0 ? zeros + 1 : 0;
            }
            return [.. result];
        }
    }

    private sealed class BitReader(byte[] data)
    {
        private int _position;

        public int Read(int count)
        {
            var value = 0;
            for (var i = 0; i < count; i++, _position++)
            {
                if (_position >> 3 >= data.Length)
                    throw new InvalidDataException("The HEVC sequence parameter set is truncated.");
                value = (value << 1) | ((data[_position >> 3] >> (7 - (_position & 7))) & 1);
            }
            return value;
        }

        public int ReadExpGolomb()
        {
            var zeros = 0;
            while (Read(1) == 0)
            {
                if (++zeros > 30)
                    throw new InvalidDataException("The HEVC sequence parameter set is malformed.");
            }
            return (1 << zeros) - 1 + Read(zeros);
        }
    }
}
