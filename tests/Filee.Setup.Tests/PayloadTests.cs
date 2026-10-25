// The payload appended to Setup.exe: written, found, verified and unpacked; damage and tricks are refused before
// anything is installed.

using System.Buffers.Binary;
using Filee.Setup.Payload;

namespace Filee.Setup.Tests;

public class PayloadTests
{
    [Fact]
    public void The_app_comes_back_byte_for_byte()
    {
        using var bench = new SetupBench(app: (temp, _) => temp.Write(@"app\data\big.bin", new string('z', 3_000_000)));
        using var payload = SetupPayload.Open(bench.SetupExe)!;
        Assert.Equal("2.0.0", payload.Manifest.Version);
        Assert.Contains(payload.Manifest.Files, f => f.Path == "Filee.exe");
        Assert.Contains(payload.Manifest.Files, f => f.Path == @"engines\rhwp\rhwp.exe");
        Assert.Equal("pandoc", Assert.Single(payload.Manifest.Engines).Id);
        Assert.Equal(".filee-component", payload.Manifest.EngineMarker);

        var progress = new List<double>();
        payload.Verify(new SyncProgress(progress.Add), TestContext.Current.CancellationToken);
        Assert.Equal(1, progress[^1], 3);

        var target = bench.Temp.Sub("out");
        long written = 0;
        payload.ExtractTo(target, new Installation.SyncProgress<long>(b => written = b), TestContext.Current.CancellationToken);
        Assert.Equal(payload.Manifest.TotalSize, written);
        foreach (var file in Directory.EnumerateFiles(bench.AppFolder, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(bench.AppFolder, file);
            Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(Path.Combine(target, relative)));
        }
        // The same files compress well together: the payload is far smaller than the app.
        Assert.True(payload.CompressedLength < payload.Manifest.TotalSize / 10);
    }

    [Theory]
    [InlineData("kernel32.dll")]
    [InlineData("cmd.exe")]
    [InlineData("random")]
    public void The_x86_filter_gives_back_the_exact_bytes(string file)
    {
        var original = file == "random"
            ? System.Security.Cryptography.RandomNumberGenerator.GetBytes(1_000_000)
            : File.ReadAllBytes(Path.Combine(Environment.SystemDirectory, file));
        var data = (byte[])original.Clone();
        X86Filter.Encode(data);
        if (file != "random")
            Assert.False(data.AsSpan().SequenceEqual(original));
        X86Filter.Decode(data);
        Assert.Equal(original, data);
        // Short buffers stay as they are.
        var tiny = new byte[] { 0xE8, 1, 2, 3 };
        X86Filter.Encode(tiny);
        Assert.Equal(new byte[] { 0xE8, 1, 2, 3 }, tiny);
    }

    [Fact]
    public void Programs_go_through_the_x86_filter()
    {
        using var bench = new SetupBench(app: (temp, _) => File.Copy(Path.Combine(Environment.SystemDirectory, "kernel32.dll"), temp.Sub(@"app\native.dll")));
        using var payload = SetupPayload.Open(bench.SetupExe)!;
        Assert.Equal(PayloadFile.X86, payload.Manifest.Files.Single(f => f.Path == "native.dll").Filter);
        Assert.Equal("", payload.Manifest.Files.Single(f => f.Path == @"cli\filee.cmd").Filter);
        var target = bench.Temp.Sub("out");
        payload.ExtractTo(target, cancellation: TestContext.Current.CancellationToken);
        Assert.Equal(File.ReadAllBytes(bench.Temp.Sub(@"app\native.dll")), File.ReadAllBytes(Path.Combine(target, "native.dll")));
    }

    [Fact]
    public void Large_apps_are_packed_in_several_segments()
    {
        using var bench = new SetupBench(app: (temp, _) =>
        {
            temp.Write(@"app\a.bin", new string('a', (int)(SetupPayload.FastSegmentSize + 1000)));
            temp.Write(@"app\b.bin", new string('b', 2_000_000));
        });
        using var payload = SetupPayload.Open(bench.SetupExe)!;
        Assert.True(payload.Manifest.Segments.Count >= 2);
        Assert.Equal(payload.CompressedLength, payload.Manifest.Segments.Sum());
        payload.Verify(cancellation: TestContext.Current.CancellationToken);
        var target = bench.Temp.Sub("out");
        payload.ExtractTo(target, cancellation: TestContext.Current.CancellationToken);
        foreach (var file in Directory.EnumerateFiles(bench.AppFolder, "*", SearchOption.AllDirectories))
            Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(Path.Combine(target, Path.GetRelativePath(bench.AppFolder, file))));
    }

    [Fact]
    public void The_uninstaller_is_the_program_without_its_payload()
    {
        using var bench = new SetupBench();
        using var payload = SetupPayload.Open(bench.SetupExe)!;
        var uninstaller = bench.Temp.Sub("uninstall.exe");
        payload.CopyProgramTo(uninstaller);
        Assert.Equal(File.ReadAllBytes(bench.Temp.Sub("program.exe")), File.ReadAllBytes(uninstaller));
        Assert.Null(SetupPayload.Open(uninstaller));
    }

    [Fact]
    public void A_program_without_payload_has_none()
    {
        using var temp = new TempFolder();
        Assert.Null(SetupPayload.Open(temp.Write("plain.exe", "MZ nothing appended")));
        Assert.Null(SetupPayload.Open(temp.Write("empty.exe", "")));
    }

    [Fact]
    public void A_damaged_payload_is_found_before_anything_is_unpacked()
    {
        using var bench = new SetupBench();
        long offset;
        using (var payload = SetupPayload.Open(bench.SetupExe)!)
            offset = payload.Offset;
        var bytes = File.ReadAllBytes(bench.SetupExe);
        bytes[offset + 10] ^= 0xFF;
        File.WriteAllBytes(bench.SetupExe, bytes);
        using var damaged = SetupPayload.Open(bench.SetupExe)!;
        Assert.Throws<InvalidDataException>(() => damaged.Verify(cancellation: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void A_damaged_manifest_or_trailer_is_refused()
    {
        using var bench = new SetupBench();
        var bytes = File.ReadAllBytes(bench.SetupExe);
        var manifestLength = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(bytes.Length - SetupPayload.TrailerLength + 32));
        var changed = (byte[])bytes.Clone();
        changed[bytes.Length - SetupPayload.TrailerLength - manifestLength + 5] ^= 0x01;
        File.WriteAllBytes(bench.SetupExe, changed);
        Assert.Throws<InvalidDataException>(() => SetupPayload.Open(bench.SetupExe));

        changed = (byte[])bytes.Clone();
        BinaryPrimitives.WriteInt64LittleEndian(changed.AsSpan(bytes.Length - SetupPayload.TrailerLength + 16), 7);
        File.WriteAllBytes(bench.SetupExe, changed);
        Assert.Throws<InvalidDataException>(() => SetupPayload.Open(bench.SetupExe));
    }

    [Fact]
    public void A_file_that_does_not_match_its_checksum_stops_unpacking()
    {
        using var bench = new SetupBench();
        // Same layout, but the manifest claims another hash for one file (and is re-sealed): Verify passes, ExtractTo must not.
        var bytes = File.ReadAllBytes(bench.SetupExe);
        var trailer = bytes.AsSpan(bytes.Length - SetupPayload.TrailerLength);
        var manifestLength = (int)BinaryPrimitives.ReadInt64LittleEndian(trailer[24..].Slice(8));
        var manifestStart = bytes.Length - SetupPayload.TrailerLength - manifestLength;
        var json = System.Text.Encoding.UTF8.GetString(bytes, manifestStart, manifestLength);
        var manifest = PayloadManifest.FromJson(bytes.AsSpan(manifestStart, manifestLength));
        var victim = manifest.Files.First(f => f.Path == "Filee.exe");
        var forged = json.Replace(victim.Sha256, new string('0', 64), StringComparison.Ordinal);
        Assert.Equal(json.Length, forged.Length);
        var forgedBytes = System.Text.Encoding.UTF8.GetBytes(forged);
        forgedBytes.CopyTo(bytes, manifestStart);
        System.Security.Cryptography.SHA256.HashData(forgedBytes).CopyTo(bytes, bytes.Length - 32);
        File.WriteAllBytes(bench.SetupExe, bytes);

        using var payload = SetupPayload.Open(bench.SetupExe)!;
        payload.Verify(cancellation: TestContext.Current.CancellationToken);
        var error = Assert.Throws<InvalidDataException>(() => payload.ExtractTo(bench.Temp.Sub("out"), cancellation: TestContext.Current.CancellationToken));
        Assert.Contains("Filee.exe", error.Message);
    }

    [Theory]
    [InlineData(@"..\evil.dll")]
    [InlineData(@"C:\Windows\evil.dll")]
    [InlineData(@"\evil.dll")]
    [InlineData(@"a\..\..\evil.dll")]
    [InlineData("file.txt:stream")]
    [InlineData("")]
    public void Paths_must_stay_inside_the_install_folder(string path) =>
        Assert.Throws<InvalidDataException>(() => PayloadManifest.CheckPath(path));

    [Fact]
    public void The_payload_is_found_in_front_of_an_authenticode_signature()
    {
        using var temp = new TempFolder();
        // A minimal PE32+ header whose certificate table (security directory) is the last 512 bytes of the file, as
        // signtool leaves a signed Setup: the payload sits right before it.
        var app = temp.Sub("app");
        temp.Write(@"app\Filee.exe", "Filee");
        var program = temp.Sub("program.exe");
        File.WriteAllBytes(program, MinimalPe(0, 0));
        var unsigned = temp.Sub("unsigned.exe");
        SetupPayload.Write(program, app, unsigned, "2.0.0", [], "", fast: true);

        var body = File.ReadAllBytes(unsigned);
        var signed = new byte[body.Length + 512];
        body.CopyTo(signed, 0);
        var pe = BinaryPrimitives.ReadInt32LittleEndian(signed.AsSpan(0x3C));
        var security = pe + 4 + 20 + 112 + 4 * 8;
        BinaryPrimitives.WriteUInt32LittleEndian(signed.AsSpan(security), (uint)body.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(signed.AsSpan(security + 4), 512);
        File.WriteAllBytes(temp.Sub("signed.exe"), signed);

        using var payload = SetupPayload.Open(temp.Sub("signed.exe"))!;
        payload.Verify(cancellation: TestContext.Current.CancellationToken);
        Assert.Equal("Filee.exe", Assert.Single(payload.Manifest.Files).Path);
    }

    [Fact]
    public void A_signed_program_becomes_a_signed_uninstaller()
    {
        // A release signs the Setup program, then Setup.exe: Setup.exe's header no longer points to the program's
        // signature (its own goes at the end), and the uninstaller copied out of it points to it again.
        using var temp = new TempFolder();
        temp.Write(@"app\Filee.exe", "Filee");
        var program = temp.Sub("program.exe");
        var bytes = MinimalPe(0, 0);
        var pe = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0x3C));
        var security = pe + 4 + 20 + 112 + 32;
        var signed = new byte[bytes.Length + 64];
        bytes.CopyTo(signed, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(signed.AsSpan(security), (uint)bytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(signed.AsSpan(security + 4), 64);
        File.WriteAllBytes(program, signed);

        var setup = temp.Sub("setup.exe");
        SetupPayload.Write(program, temp.Sub("app"), setup, "2.0.0", [], "", fast: true);
        var written = File.ReadAllBytes(setup);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt64LittleEndian(written.AsSpan(security)));
        // signtool adds the certificate table at the end without padding in front of it.
        Assert.Equal(0, written.Length % 8);

        using var payload = SetupPayload.Open(setup)!;
        Assert.Equal(new ProgramSignature(bytes.Length, 64), payload.Manifest.Signature);
        payload.Verify(cancellation: TestContext.Current.CancellationToken);
        var uninstaller = temp.Sub("uninstall.exe");
        payload.CopyProgramTo(uninstaller);
        Assert.Equal(signed, File.ReadAllBytes(uninstaller));
    }

    /// <summary>DOS header + PE32+ headers without sections (enough for the overlay and import readers).</summary>
    internal static byte[] MinimalPe(uint importRva, ushort sections, int size = 0x400)
    {
        var bytes = new byte[size];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x3C), 0x40);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x40), 0x00004550);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x44), 0x8664);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x46), sections);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x54), 240);
        var optional = 0x58;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(optional), 0x20B);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(optional + 108), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(optional + 112 + 8), importRva);
        return bytes;
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
