// Archive engine: conversions between archive formats (names incl. Korean, folders, contents and times survive),
// Extract to a folder, "Compress into one archive", Filee's own ALZ / EGG / lzip / bzip2 readers, and the safety
// checks (zip slip, links, passwords). Tests that need the bundled 7-Zip skip when engines/7zip is missing
// (pwsh build/fetch-engines.ps1 -Only 7zip).

using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Engines.Archives;
using static Filee.Engines.Tests.ArchiveBuilders;

namespace Filee.Engines.Tests;

public class ArchiveTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void SkipWithout7Zip()
    {
        var engine = fx.Converters.Single(c => c.Id == "archive");
        Assert.SkipUnless(fx.Catalog.StatusOf(engine).IsAvailable, "7-Zip not found (pwsh build/fetch-engines.ps1 -Only 7zip)");
    }

    private async Task<ConversionJob> ConvertOkAsync(string source, Preset preset)
    {
        var job = await fx.ConvertAsync([source], preset);
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        return job;
    }

    private static Preset Extract => BuiltInData.CreatePresets().Single(p => p.Id == "extract");

    // ───────── Routes ─────────

    [Theory]
    [InlineData("zip")]
    [InlineData("jar")]
    [InlineData("7z")]
    [InlineData("rar")]
    [InlineData("tgz")]
    [InlineData("tbz2")]
    [InlineData("txz")]
    [InlineData("tar7z")]
    [InlineData("tz")]
    [InlineData("iso")]
    [InlineData("dmg")]
    [InlineData("cab")]
    [InlineData("rpm")]
    [InlineData("deb")]
    [InlineData("lha")]
    [InlineData("arj")]
    [InlineData("alz")]
    [InlineData("egg")]
    [InlineData("lz")]
    [InlineData("tlz")]
    public void Every_readable_archive_converts_in_one_step(string from)
    {
        var planner = fx.Catalog.CreatePlanner(["archive"]);
        foreach (var to in new[] { "zip", "7z", "tar", "tgz", "tbz2", "txz", FormatRegistry.Folder })
        {
            var route = planner.Plan(from, to);
            Assert.NotNull(route);
            Assert.Equal("archive", Assert.Single(route.Steps).Converter.Id);
        }
    }

    [Fact]
    public void Single_file_compressors_convert_among_each_other_but_archives_do_not_shrink_to_them()
    {
        var planner = fx.Catalog.CreatePlanner(["archive"]);
        Assert.Single(planner.Plan("gz", "xz")!.Steps);
        Assert.Single(planner.Plan("lzma", "bz2")!.Steps);
        Assert.Single(planner.Plan("lz", "gz")!.Steps);
        Assert.Null(planner.Plan("zip", "gz"));  // several files do not fit into one .gz
        Assert.Null(planner.Plan("zip", "rar")); // RAR cannot be written
        Assert.Null(planner.Plan("lzo", "zip")); // no LZO reader
        // Every format the edges name exists in the registry.
        foreach (var edge in fx.Converters.Single(c => c.Id == "archive").Edges)
        {
            Assert.NotNull(FormatRegistry.FindById(edge.From));
            Assert.NotNull(FormatRegistry.FindById(edge.To));
        }
    }

    // ───────── Archive → archive ─────────

    [Fact]
    public async Task Zip_7z_tgz_tar_zip_round_trip_keeps_names_folders_contents_and_times()
    {
        SkipWithout7Zip();
        var dir = fx.NewFolder();
        var tree = MakeTree(dir, "프로젝트");
        var expected = Snapshot(tree);
        var zip = Path.Combine(dir, "프로젝트.zip");
        ZipFile.CreateFromDirectory(tree, zip, CompressionLevel.Optimal, includeBaseDirectory: true, Encoding.UTF8);

        var current = zip;
        foreach (var target in new[] { "7z", "tgz", "tar", "tbz2", "txz", "zip" })
        {
            var job = await ConvertOkAsync(current, new Preset { TargetFormat = target });
            current = job.Outputs.Single();
            Assert.EndsWith("." + FormatRegistry.Get(target).PrimaryExtension, current);
        }

        // The last ZIP read by .NET's own reader: same tree below the "프로젝트" folder, UTF-8 names flagged.
        var check = Path.Combine(dir, "check");
        ZipFile.ExtractToDirectory(current, check);
        Assert.Equal(expected, Snapshot(Path.Combine(check, "프로젝트")));
        var time = File.GetLastWriteTime(Path.Combine(check, "프로젝트", "readme.txt"));
        Assert.True(Math.Abs((time - KnownTime).TotalSeconds) <= 2, $"time {time} instead of {KnownTime}");
    }

    [Fact]
    public async Task Store_and_maximum_levels_are_applied()
    {
        SkipWithout7Zip();
        var dir = fx.NewFolder();
        var text = Path.Combine(dir, "big.txt");
        await File.WriteAllBytesAsync(text, Words(300_000), Ct);
        var source = Path.Combine(dir, "source.zip");
        using (var zip = ZipFile.Open(source, ZipArchiveMode.Create))
            zip.CreateEntryFromFile(text, "big.txt");

        var stored = await ConvertOkAsync(source, new Preset { TargetFormat = "7z", Archive = { Level = ArchiveLevel.Store }, Output = { FileNamePattern = "{name}_store" } });
        var max = await ConvertOkAsync(source, new Preset { TargetFormat = "7z", Archive = { Level = ArchiveLevel.Maximum }, Output = { FileNamePattern = "{name}_max" } });
        Assert.True(new FileInfo(stored.Outputs.Single()).Length > 300_000);
        Assert.True(new FileInfo(max.Outputs.Single()).Length < 100_000);
    }

    [Fact]
    public async Task Single_file_streams_convert_gz_xz_bz2()
    {
        SkipWithout7Zip();
        var dir = fx.NewFolder();
        var data = Words(50_000);
        var csv = Path.Combine(dir, "data.csv");
        await File.WriteAllBytesAsync(csv, data, Ct);
        var gz = Path.Combine(dir, "data.csv.gz");
        await Run7zAsync("a", "-tgzip", gz, csv);
        File.Delete(csv);

        var xz = (await ConvertOkAsync(gz, new Preset { TargetFormat = "xz" })).Outputs.Single();
        var bz2 = (await ConvertOkAsync(xz, new Preset { TargetFormat = "bz2" })).Outputs.Single();
        Assert.EndsWith("data.csv.bz2", bz2);

        var folder = (await ConvertOkAsync(bz2, Extract)).Outputs.Single();
        Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(folder, "data.csv"), Ct));
    }

    // ───────── Extract ─────────

    [Fact]
    public async Task Extract_does_not_nest_a_single_top_folder_and_renames_on_conflict()
    {
        SkipWithout7Zip();
        var dir = fx.NewFolder();
        var tree = MakeTree(dir, "photos");
        var expected = Snapshot(tree);
        var zip = Path.Combine(dir, "사진.zip");
        ZipFile.CreateFromDirectory(tree, zip, CompressionLevel.Fastest, includeBaseDirectory: true, Encoding.UTF8);
        Directory.Delete(tree, true);

        var first = await ConvertOkAsync(zip, Extract);
        var folder = first.Outputs.Single();
        Assert.Equal(Path.Combine(dir, "사진"), folder);
        Assert.Equal(expected, Snapshot(folder)); // photos/… became 사진/…, not 사진/photos/…

        var second = await ConvertOkAsync(zip, Extract);
        Assert.Equal(Path.Combine(dir, "사진 (2)"), second.Outputs.Single());
        Assert.Equal(expected, Snapshot(second.Outputs.Single()));

        // Nothing else was left next to the archive (no staging folders).
        Assert.Equal(["사진", "사진 (2)", "사진.zip"], Directory.GetFileSystemEntries(dir).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Extract_keeps_several_top_entries_in_a_folder_named_after_the_archive_and_unpacks_tar_gz_fully()
    {
        SkipWithout7Zip();
        var dir = fx.NewFolder();
        var tree = MakeTree(dir, "tree");
        var tgz = Path.Combine(dir, "backup.tar.gz");
        await using (var file = File.Create(tgz))
        await using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
            await TarFile.CreateFromDirectoryAsync(tree, gzip, includeBaseDirectory: false, Ct);

        var folder = (await ConvertOkAsync(tgz, Extract)).Outputs.Single();
        Assert.Equal(Path.Combine(dir, "backup"), folder);
        Assert.Equal(Snapshot(tree), Snapshot(folder)); // files, not a lone backup.tar
    }

    [Fact]
    public async Task Extract_with_overwrite_merges_into_the_existing_folder_and_skip_leaves_it_alone()
    {
        SkipWithout7Zip();
        var dir = fx.NewFolder();
        var tree = MakeTree(dir, "data");
        var zip = Path.Combine(dir, "data.zip");
        ZipFile.CreateFromDirectory(tree, zip, CompressionLevel.Fastest, includeBaseDirectory: false, Encoding.UTF8);
        Directory.Delete(tree, true);
        var target = Directory.CreateDirectory(Path.Combine(dir, "data")).FullName;
        await File.WriteAllTextAsync(Path.Combine(target, "readme.txt"), "old", Ct);
        await File.WriteAllTextAsync(Path.Combine(target, "mine.txt"), "keep", Ct);

        var overwrite = Extract;
        overwrite.Output.Conflict = ConflictPolicy.Overwrite;
        Assert.Equal(target, (await ConvertOkAsync(zip, overwrite)).Outputs.Single());
        Assert.Equal("Filee archive test\n", await File.ReadAllTextAsync(Path.Combine(target, "readme.txt"), Ct));
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(target, "mine.txt"), Ct));
        Assert.True(File.Exists(Path.Combine(target, "docs", "보고서", "data.csv")));

        var skip = Extract;
        skip.Output.Conflict = ConflictPolicy.Skip;
        var skipped = await fx.ConvertAsync([zip], skip);
        Assert.Equal(FileState.Skipped, skipped.Files[0].State);
        Assert.Empty(skipped.Outputs);
    }

    [Fact]
    public async Task Tar_symbolic_links_are_not_unpacked()
    {
        SkipWithout7Zip();
        var dir = fx.NewFolder();
        var tar = Path.Combine(dir, "links.tar");
        await using (var stream = File.Create(tar))
        await using (var writer = new TarWriter(stream, TarEntryFormat.Pax))
        {
            await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, "real.txt") { DataStream = new MemoryStream("real"u8.ToArray()) }, Ct);
            await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.SymbolicLink, "escape") { LinkName = "../../../../Windows/win.ini" }, Ct);
            await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.SymbolicLink, "inside") { LinkName = "real.txt" }, Ct);
        }

        var folder = (await ConvertOkAsync(tar, Extract)).Outputs.Single();
        Assert.Equal(["real.txt"], Directory.GetFileSystemEntries(folder).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Zip_slip_entries_are_rejected()
    {
        SkipWithout7Zip();
        var dir = fx.NewFolder();
        var inner = Directory.CreateDirectory(Path.Combine(dir, "inner")).FullName;
        var zip = Path.Combine(inner, "evil.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(archive.CreateEntry("good.txt").Open()))
                w.Write("good");
            using (var w = new StreamWriter(archive.CreateEntry("../../evil.txt").Open()))
                w.Write("evil");
        }

        var job = await fx.ConvertAsync([zip], Extract);

        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("outside the target folder", job.Files[0].ErrorDetail);
        Assert.False(File.Exists(Path.Combine(dir, "evil.txt")));
        Assert.Equal(["evil.zip"], Directory.GetFileSystemEntries(inner).Select(Path.GetFileName)); // no half-extracted folder
    }

    [Fact]
    public async Task Password_protected_archives_fail_with_a_clear_message()
    {
        SkipWithout7Zip();
        var dir = fx.NewFolder();
        var secret = Path.Combine(dir, "secret.txt");
        await File.WriteAllTextAsync(secret, "secret", Ct);
        var zip = Path.Combine(dir, "locked.zip");
        var sevenZip = Path.Combine(dir, "locked.7z");
        await Run7zAsync("a", "-tzip", "-ppassword", zip, secret);
        await Run7zAsync("a", "-t7z", "-ppassword", "-mhe=on", sevenZip, secret);

        foreach (var archive in new[] { zip, sevenZip })
        {
            var job = await fx.ConvertAsync([archive], new Preset { TargetFormat = "tgz" });
            Assert.Equal(JobState.Failed, job.State);
            Assert.Contains("password-protected", job.Files[0].ErrorDetail);
        }
    }

    [Fact]
    public async Task Damaged_and_fake_archives_fail_cleanly()
    {
        SkipWithout7Zip();
        var dir = fx.NewFolder();
        var fake = Path.Combine(dir, "fake.zip");
        await File.WriteAllTextAsync(fake, "this is not a zip", Ct);

        var job = await fx.ConvertAsync([fake], Extract);

        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("not a valid archive", job.Files[0].ErrorDetail);
        Assert.Equal(["fake.zip"], Directory.GetFileSystemEntries(dir).Select(Path.GetFileName));
    }

    // ───────── Compress into one archive ─────────

    [Fact]
    public async Task Zip_all_packs_any_files_as_they_are()
    {
        var dir = fx.NewFolder();
        var a = Directory.CreateDirectory(Path.Combine(dir, "a")).FullName;
        var b = Directory.CreateDirectory(Path.Combine(dir, "b")).FullName;
        var report = Path.Combine(a, "보고서.txt");
        await File.WriteAllTextAsync(report, "보고서", Ct);
        var unknown = Path.Combine(a, "data.xyz");
        await File.WriteAllBytesAsync(unknown, Noise(1000, 1), Ct);
        var image = EngineFixture.MakeImage(a, "photo.png", ImageMagick.MagickFormat.Png);
        var sameName = Path.Combine(b, "보고서.txt");
        await File.WriteAllTextAsync(sameName, "다른 보고서", Ct);

        var zipAll = BuiltInData.CreatePresets().Single(p => p.Id == "zip-all");
        var job = await fx.ConvertAsync([report, unknown, image, sameName], zipAll);

        Assert.Equal(JobState.Completed, job.State);
        var output = job.Outputs.Single();
        Assert.Equal(Path.Combine(a, "보고서_files.zip"), output);
        using var zip = ZipFile.OpenRead(output);
        Assert.Equal(["data.xyz", "photo.png", "보고서 (2).txt", "보고서.txt"], zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
        using (var reader = new StreamReader(zip.GetEntry("보고서 (2).txt")!.Open()))
            Assert.Equal("다른 보고서", await reader.ReadToEndAsync(Ct));
        Assert.Equal(await File.ReadAllBytesAsync(image, Ct), ReadAll(zip.GetEntry("photo.png")!));
        Assert.True(File.Exists(report)); // sources untouched
    }

    [Fact]
    public async Task Zip_all_works_for_a_single_file_and_honours_the_level()
    {
        var dir = fx.NewFolder();
        var text = Path.Combine(dir, "notes.md");
        await File.WriteAllBytesAsync(text, Words(100_000), Ct);
        var preset = BuiltInData.CreatePresets().Single(p => p.Id == "zip-all");
        preset.Archive.Level = ArchiveLevel.Store;

        var job = await fx.ConvertAsync([text], preset);

        Assert.Equal(JobState.Completed, job.State);
        using var zip = ZipFile.OpenRead(job.Outputs.Single());
        var entry = Assert.Single(zip.Entries);
        Assert.Equal("notes.md", entry.Name);
        Assert.Equal(entry.Length, entry.CompressedLength);
    }

    [Fact]
    public async Task Zip_all_packs_a_folder_from_the_command_line_with_its_content()
    {
        var dir = fx.NewFolder();
        var tree = MakeTree(dir, "tree");

        var job = await fx.ConvertAsync([tree], BuiltInData.CreatePresets().Single(p => p.Id == "zip-all"));

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal(Path.Combine(dir, "tree_files.zip"), job.Outputs.Single());
        var check = Path.Combine(dir, "check");
        ZipFile.ExtractToDirectory(job.Outputs.Single(), check);
        Assert.Equal(Snapshot(tree), Snapshot(Path.Combine(check, "tree")));
    }

    [Theory]
    [InlineData("7z")]
    [InlineData("tgz")]
    [InlineData("txz")]
    public async Task Combine_into_other_archive_formats(string format)
    {
        SkipWithout7Zip();
        var dir = fx.NewFolder();
        var one = Path.Combine(dir, "하나.txt");
        var two = Path.Combine(Directory.CreateDirectory(Path.Combine(dir, "sub")).FullName, "하나.txt");
        await File.WriteAllTextAsync(one, "1", Ct);
        await File.WriteAllTextAsync(two, "2", Ct);

        var job = await fx.ConvertAsync([one, two], new Preset { TargetFormat = format, Archive = { CombineIntoOne = true } });
        Assert.Equal(JobState.Completed, job.State);

        var folder = (await ConvertOkAsync(job.Outputs.Single(), Extract)).Outputs.Single();
        Assert.Equal("1", await File.ReadAllTextAsync(Path.Combine(folder, "하나.txt"), Ct));
        Assert.Equal("2", await File.ReadAllTextAsync(Path.Combine(folder, "하나 (2).txt"), Ct));
    }

    // ───────── Filee's own readers ─────────

    [Fact]
    public async Task Alz_with_stored_deflated_and_bzip2_entries_and_korean_names()
    {
        SkipWithout7Zip();
        var dir = fx.NewFolder();
        var big = Words(2_000_000); // three 900k bzip2 blocks
        var small = Encoding.UTF8.GetBytes("알집 테스트 ALZ");
        var alz = Path.Combine(dir, "알집.alz");
        WriteAlz(alz,
        [
            new("폴더\\", null),
            new("폴더\\저장.txt", small, Method: 0),
            new("압축.txt", Words(10_000), Method: 2),
            new("큰 파일.txt", big, Method: 1, Packed: ToAlzBzip2(await Bzip2Async(big, dir))),
        ]);

        var folder = (await ConvertOkAsync(alz, Extract)).Outputs.Single();

        Assert.Equal(small, await File.ReadAllBytesAsync(Path.Combine(folder, "폴더", "저장.txt"), Ct));
        Assert.Equal(Words(10_000), await File.ReadAllBytesAsync(Path.Combine(folder, "압축.txt"), Ct));
        Assert.Equal(big, await File.ReadAllBytesAsync(Path.Combine(folder, "큰 파일.txt"), Ct));
        Assert.Equal(KnownTime, File.GetLastWriteTime(Path.Combine(folder, "압축.txt")));

        var zip = (await ConvertOkAsync(alz, new Preset { TargetFormat = "zip" })).Outputs.Single();
        using var archive = ZipFile.OpenRead(zip);
        Assert.Contains(archive.Entries, e => e.FullName == "폴더/저장.txt");
    }

    [Fact]
    public void Alz_reader_refuses_escaping_and_encrypted_entries_without_writing_outside()
    {
        var dir = fx.NewFolder();
        var target = Directory.CreateDirectory(Path.Combine(dir, "out")).FullName;
        var slip = Path.Combine(dir, "slip.alz");
        WriteAlz(slip, [new("ok.txt", [1]), new("..\\..\\evil.txt", [2])]);
        var locked = Path.Combine(dir, "locked.alz");
        WriteAlz(locked, [new("secret.txt", [3], Encrypted: true)]);

        var error = Assert.Throws<InvalidDataException>(() => AlzReader.Extract(slip, target, Ct));
        Assert.Contains("outside the target folder", error.Message);
        Assert.False(File.Exists(Path.Combine(dir, "evil.txt")));
        Assert.Contains("password-protected", Assert.Throws<InvalidDataException>(() => AlzReader.Extract(locked, target, Ct)).Message);
    }

    [Fact]
    public async Task Egg_with_store_deflate_bzip2_lzma_and_multi_block_entries()
    {
        SkipWithout7Zip();
        var dir = fx.NewFolder();
        var text = Words(30_000);
        var part1 = Words(5_000);
        var part2 = Noise(7_000, 3);
        var egg = Path.Combine(dir, "에그.egg");
        WriteEgg(egg,
        [
            new("문서/", true),
            new("문서/stored.txt", false, new EggBlock(text, 0)),
            new("deflate.txt", false, new EggBlock(text, 1)),
            new("bzip2.txt", false, new EggBlock(text, 2, await Bzip2Async(text, dir))),
            new("lzma.txt", false, new EggBlock(text, 4, EggLzma(await RawLzmaAsync(text, endMarker: false, dir)))),
            new("두 블록.bin", false, new EggBlock(part1, 1), new EggBlock(part2, 0)),
        ]);

        var folder = (await ConvertOkAsync(egg, Extract)).Outputs.Single();

        foreach (var name in new[] { "문서/stored.txt", "deflate.txt", "bzip2.txt", "lzma.txt" })
            Assert.Equal(text, await File.ReadAllBytesAsync(Path.Combine(folder, name), Ct));
        byte[] joined = [.. part1, .. part2];
        Assert.Equal(joined, await File.ReadAllBytesAsync(Path.Combine(folder, "두 블록.bin"), Ct));
        Assert.Equal(KnownTime, File.GetLastWriteTime(Path.Combine(folder, "deflate.txt")));
    }

    [Fact]
    public async Task Solid_egg_archives_fail_with_a_clear_message()
    {
        SkipWithout7Zip();
        var dir = fx.NewFolder();
        var egg = Path.Combine(dir, "solid.egg");
        WriteEgg(egg, [new("a.txt", false, new EggBlock([1, 2, 3], 0))], solid: true);

        var job = await fx.ConvertAsync([egg], Extract);

        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("solid", job.Files[0].ErrorDetail);
    }

    [Fact]
    public async Task Lzip_files_and_tar_lz_are_unpacked()
    {
        SkipWithout7Zip();
        var dir = fx.NewFolder();
        var first = Words(40_000);
        var second = Noise(3_000, 9);
        var lz = Path.Combine(dir, "log.txt.lz");
        // Two members, as written by plzip or "cat a.lz b.lz".
        byte[] members = [.. await LzipAsync(first, dir), .. await LzipAsync(second, dir)];
        await File.WriteAllBytesAsync(lz, members, Ct);

        var folder = (await ConvertOkAsync(lz, Extract)).Outputs.Single();
        byte[] both = [.. first, .. second];
        Assert.Equal(both, await File.ReadAllBytesAsync(Path.Combine(folder, "log.txt"), Ct));

        var tree = MakeTree(dir, "tree");
        using var tar = new MemoryStream();
        await TarFile.CreateFromDirectoryAsync(tree, tar, includeBaseDirectory: false, Ct);
        var tarLz = Path.Combine(dir, "backup.tar.lz");
        await File.WriteAllBytesAsync(tarLz, await LzipAsync(tar.ToArray(), dir), Ct);

        var unpacked = (await ConvertOkAsync(tarLz, Extract)).Outputs.Single();
        Assert.Equal(Snapshot(tree), Snapshot(unpacked));
        var zip = (await ConvertOkAsync(tarLz, new Preset { TargetFormat = "zip" })).Outputs.Single();
        Assert.EndsWith("backup.zip", zip);
    }

    [Fact]
    public async Task Bzip2_decoder_reads_standard_multi_block_streams()
    {
        Assert.SkipUnless(SevenZipExe is not null, "7-Zip not found");
        var dir = fx.NewFolder();
        var data = Words(2_500_000);
        var bz2 = await Bzip2Async(data, dir);

        using var output = new MemoryStream();
        Bzip2Decoder.Decompress(new MemoryStream(bz2), output, alzFraming: false, long.MaxValue, Ct);
        Assert.Equal(data, output.ToArray());

        // A flipped bit is caught by the block checksum instead of producing wrong data.
        bz2[bz2.Length / 2] ^= 0x10;
        Assert.ThrowsAny<InvalidDataException>(() =>
        {
            Bzip2Decoder.Decompress(new MemoryStream(bz2), new MemoryStream(), alzFraming: false, long.MaxValue, Ct);
        });
    }

    // ───────── Helpers ─────────

    [Theory]
    [InlineData("../evil.txt", true)]
    [InlineData("a/../../evil.txt", true)]
    [InlineData("..\\evil.txt", true)]
    [InlineData("/etc/passwd", true)]
    [InlineData("\\abs.txt", true)]
    [InlineData("C:\\drive.txt", true)]
    [InlineData("a/b/c.txt", false)]
    [InlineData("한글/파일..txt", false)]
    public void Unsafe_entry_paths_are_recognised(string path, bool unsafePath) =>
        Assert.Equal(unsafePath, ArchiveSafety.IsUnsafePath(path));

    [Fact]
    public void Entry_names_are_made_valid_windows_names_inside_the_root()
    {
        var root = fx.NewFolder();
        Assert.Equal(Path.Combine(root, "폴더", "파일.txt"), ArchiveSafety.ResolveEntryPath(root, @"폴더\파일.txt", "x.alz"));
        Assert.Equal(Path.Combine(root, "a_b", "_CON.txt"), ArchiveSafety.ResolveEntryPath(root, "a|b/CON.txt", "x.alz"));
        Assert.Equal(Path.Combine(root, "file.txt_stream"), ArchiveSafety.ResolveEntryPath(root, "file.txt:stream", "x.alz")); // no NTFS streams
        Assert.Equal(Path.Combine(root, "dir", "file_"), ArchiveSafety.ResolveEntryPath(root, "./dir//file?", "x.alz"));
    }

    [Fact]
    public void Listing_parser_reads_7zip_technical_output()
    {
        const string output = """
            7-Zip 26.03 (x64)

            Listing archive: x.tar

            --
            Path = x.tar
            Type = tar

            ----------
            Path = dir\file.txt
            Folder = -
            Size = 7
            Mode = -rw-r--r--
            Symbolic Link =
            Hard Link =

            Path = dir\link
            Folder = -
            Size = 27
            Mode = lrw-r--r--
            Symbolic Link = ../../x
            Hard Link =

            Path = dir
            Folder = +
            Size = 0
            Encrypted = +

            """;
        var entries = SevenZip.ParseListing(output);
        Assert.Equal(["dir\\file.txt", "dir\\link", "dir"], entries.Select(e => e.Path));
        Assert.False(entries[0].IsSymbolicLink);
        Assert.True(entries[1].IsSymbolicLink);
        Assert.True(entries[2].IsFolder);
        Assert.True(entries[2].IsEncrypted);
        Assert.Equal(7, entries[0].Size);
    }

    [Fact]
    public void Clashing_names_in_one_archive_are_numbered()
    {
        var root = Path.GetTempPath();
        var items = ArchiveConverter.UniqueEntries([Path.Combine(root, "a", "x.txt"), Path.Combine(root, "b", "X.txt"),
            Path.Combine(root, "c", "x.txt"), Path.Combine(root, "d", "y")]);
        Assert.Equal(["x.txt", "X (2).txt", "x (3).txt", "y"], items.Select(i => i.EntryName));
    }

    [Theory]
    [InlineData(0x14, 1u << 20)]
    [InlineData(0xD3, (1u << 19) - 6 * (1u << 15))] // 2^19 minus 6/16
    [InlineData(0x0C, 4096u)]
    public void Lzip_dictionary_sizes(int coded, uint expected) =>
        Assert.Equal(expected, LzipReader.DictionarySize(coded));

    private static byte[] ReadAll(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }
}
