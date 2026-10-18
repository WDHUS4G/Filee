// The METADATA_BLOCK_PICTURE comment Ogg files carry their album art in (no FFmpeg needed).

using System.Buffers.Binary;
using System.Text;
using Filee.Engines.Media;

namespace Filee.Engines.Tests;

public class CoverArtTests
{
    [Fact]
    public void Picture_block_is_a_flac_front_cover_in_base64()
    {
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];

        var block = Convert.FromBase64String(CoverArt.PictureBlock(jpeg, "image/jpeg", 300, 200));

        int At(int offset) => BinaryPrimitives.ReadInt32BigEndian(block.AsSpan(offset));
        Assert.Equal(3, At(0)); // front cover
        Assert.Equal(10, At(4));
        Assert.Equal("image/jpeg", Encoding.ASCII.GetString(block, 8, 10));
        Assert.Equal(0, At(18)); // no description
        Assert.Equal((300, 200, 24, 0), (At(22), At(26), At(30), At(34)));
        Assert.Equal(jpeg.Length, At(38));
        Assert.Equal(jpeg, block[42..]);
    }

    [Fact]
    public void Metadata_values_escape_ffmetadata_specials() =>
        Assert.Equal(@"a\=b\;c\#d\\e", CoverArt.Escape(@"a=b;c#d\e"));

    [Fact]
    public void Only_jpeg_and_png_covers_are_used()
    {
        Assert.Equal("image/jpeg", CoverArt.MimeOf([0xFF, 0xD8, 0xFF, 0xDB, 0]));
        Assert.Equal("image/png", CoverArt.MimeOf([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0]));
        Assert.Null(CoverArt.MimeOf([(byte)'B', (byte)'M', 0, 0, 0]));
    }
}
