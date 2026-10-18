// End-to-end tests of the "filee" command: real engines, a temporary settings folder, files in a temporary folder.

using System.Text.Json;
using Filee.Core.Presets;

namespace Filee.Cli.Tests;

public sealed class CliTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("filee-cli-").FullName;
    private readonly string _settings = Directory.CreateTempSubdirectory("filee-cli-settings-").FullName;

    public void Dispose()
    {
        foreach (var dir in new[] { _folder, _settings })
            try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    private string Png(string name)
    {
        var path = Path.Combine(_folder, name);
        using var image = new ImageMagick.MagickImage(ImageMagick.MagickColors.Orange, 40, 30);
        image.Write(path, ImageMagick.MagickFormat.Png);
        return path;
    }

    private async Task<(int Code, string Output, string Errors)> Run(params string[] args)
    {
        var output = new StringWriter();
        var errors = new StringWriter();
        var code = await Cli.RunAsync(args, output, errors, TestContext.Current.CancellationToken, () => CliHost.Create(_settings));
        return (code, output.ToString(), errors.ToString());
    }

    [Fact]
    public async Task Converts_files_and_wildcards_into_an_output_folder()
    {
        Png("a.png");
        Png("b.png");
        var outFolder = Path.Combine(_folder, "out");

        var (code, output, errors) = await Run("convert", Path.Combine(_folder, "*.png"), "--to", "jpg", "-o", outFolder, "--quality", "70");

        Assert.Equal(Cli.Success, code);
        Assert.Equal("", errors);
        Assert.Contains("Converted 2 of 2", output);
        Assert.True(File.Exists(Path.Combine(outFolder, "a.jpg")));
        Assert.True(File.Exists(Path.Combine(outFolder, "b.jpg")));
    }

    [Fact]
    public async Task Keep_dates_copies_the_dates_of_the_originals()
    {
        var source = Png("old.png");
        var modified = new DateTime(2018, 5, 4, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(source, modified);

        var (code, _, _) = await Run("convert", source, "--to", "jpg", "--keep-dates");

        Assert.Equal(Cli.Success, code);
        Assert.Equal(modified, File.GetLastWriteTimeUtc(Path.Combine(_folder, "old.jpg")));
    }

    [Fact]
    public async Task Json_output_lists_every_file()
    {
        var source = Png("a.png");

        var (code, output, _) = await Run("convert", source, "--to", ".webp", "--json");

        Assert.Equal(Cli.Success, code);
        using var json = JsonDocument.Parse(output);
        Assert.Equal(1, json.RootElement.GetProperty("converted").GetInt32());
        var file = json.RootElement.GetProperty("files")[0];
        Assert.Equal("done", file.GetProperty("state").GetString());
        Assert.EndsWith("a.webp", file.GetProperty("outputs")[0].GetString());
    }

    [Fact]
    public async Task A_folder_converts_the_files_filee_knows()
    {
        Png("a.png");
        File.WriteAllText(Path.Combine(_folder, "notes.unknownformat"), "x");

        var (code, output, _) = await Run("convert", _folder, "--preset", BuiltInData.CreatePresets().First(p => p.TargetFormat == "jpg").Id);

        Assert.Equal(Cli.Success, code);
        Assert.Contains("Converted 1 of 1", output);
    }

    [Fact]
    public async Task A_file_that_cannot_be_converted_fails_with_exit_code_1()
    {
        var text = Path.Combine(_folder, "notes.txt");
        File.WriteAllText(text, "hello");

        var (code, output, _) = await Run("convert", text, "--to", "mp3");

        Assert.Equal(Cli.SomeFailed, code);
        Assert.Contains("fail", output);
    }

    [Theory]
    [InlineData(new[] { "convert", "a.png" }, "--to")]
    [InlineData(new[] { "convert", "a.png", "--to", "jpg", "--preset", "x" }, "--to")]
    [InlineData(new[] { "convert", "a.png", "--to", "nosuchformat" }, "unknown format")]
    [InlineData(new[] { "convert", "a.png", "--preset", "nosuchpreset" }, "no preset")]
    [InlineData(new[] { "convert", "a.png", "--preset", "rotate" }, "rotate-left, rotate-right, rotate-180")]
    [InlineData(new[] { "convert", "--to", "jpg" }, "at least one file")]
    [InlineData(new[] { "convert", "a.png", "--to", "jpg", "--quality", "300" }, "--quality")]
    [InlineData(new[] { "frobnicate" }, "Unknown command")]
    public async Task Wrong_arguments_explain_themselves_with_exit_code_2(string[] args, string message)
    {
        Png("a.png");
        var withPaths = args.Select(a => a == "a.png" ? Path.Combine(_folder, a) : a).ToArray();

        var (code, _, errors) = await Run(withPaths);

        Assert.Equal(Cli.UsageError, code);
        Assert.Contains(message, errors, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_missing_input_is_reported()
    {
        var (code, _, errors) = await Run("convert", Path.Combine(_folder, "missing.png"), "--to", "jpg");

        Assert.Equal(Cli.UsageError, code);
        Assert.Contains("not found", errors);
    }

    [Fact]
    public async Task Watch_needs_an_existing_folder()
    {
        var (code, _, errors) = await Run("watch", Path.Combine(_folder, "nope"), "--to", "jpg");

        Assert.Equal(Cli.UsageError, code);
        Assert.Contains("not found", errors, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Watch_converts_files_that_arrive_until_cancelled()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var output = new StringWriter();
        var watching = Cli.RunAsync(["watch", _folder, "--to", "jpg", "--settle", "0.2"], TextWriter.Synchronized(output), TextWriter.Null,
            stop.Token, () => CliHost.Create(_settings));

        await Task.Delay(500, TestContext.Current.CancellationToken);
        Png("arrived.png");
        var converted = Path.Combine(_folder, "converted", "arrived.jpg");
        for (var i = 0; i < 100 && !File.Exists(converted); i++)
            await Task.Delay(100, TestContext.Current.CancellationToken);
        stop.Cancel();

        Assert.Equal(Cli.Success, await watching);
        Assert.True(File.Exists(converted), output.ToString());
    }

    [Fact]
    public async Task Formats_and_presets_are_listed()
    {
        var (code, output, _) = await Run("formats", "heic");
        Assert.Equal(Cli.Success, code);
        Assert.Contains("jpg", output);

        (code, output, _) = await Run("presets");
        Assert.Equal(Cli.Success, code);
        Assert.Contains("to-png", output);
    }

    /// <summary>Adds presets to the settings folder the CLI runs with.</summary>
    private void AddPresets(params Preset[] presets)
    {
        var store = new Filee.Core.Settings.UserDataStore(_settings);
        store.Load();
        store.Presets.AddRange(presets);
        store.SaveLibrary();
    }

    [Fact]
    public async Task A_group_is_never_run_listed_or_in_the_way_of_a_conversion_of_the_same_name()
    {
        AddPresets(
            new Preset { Id = "web-group", Name = "Web", Kind = PresetKind.Group, SubPresetIds = ["to-png", "to-webp"] },
            new Preset { Id = "empty-group", Name = "Empty", Kind = PresetKind.Group },
            new Preset { Id = "my-web", Name = "Web", TargetFormat = "jpg" });
        var png = Png("a.png");

        // "Web" is both a group and a conversion: the conversion runs.
        var (code, _, errors) = await Run("convert", png, "--preset", "Web");
        Assert.Equal(Cli.Success, code);
        Assert.True(File.Exists(Path.Combine(_folder, "a.jpg")), errors);

        // A group by its id says what it holds; one with nothing left says so instead of "Use one of: ".
        (code, _, errors) = await Run("convert", png, "--preset", "web-group");
        Assert.Equal(Cli.UsageError, code);
        Assert.Contains("Use one of: to-png, to-webp", errors);
        (code, _, errors) = await Run("convert", png, "--preset", "Empty");
        Assert.Equal(Cli.UsageError, code);
        Assert.Contains("no presets left", errors);

        var (_, list, _) = await Run("presets");
        Assert.Contains("my-web", list);
        Assert.DoesNotContain("web-group", list);
    }

    [Fact]
    public async Task Data_files_convert_to_each_other()
    {
        var json = Path.Combine(_folder, "config.json");
        await File.WriteAllTextAsync(json, """{"name": "Filee", "ports": [80, 443]}""", TestContext.Current.CancellationToken);

        var (code, output, errors) = await Run("convert", json, "--to", "yaml");

        Assert.Equal(Cli.Success, code);
        Assert.Equal("", errors);
        Assert.Equal("name: Filee\nports:\n  - 80\n  - 443\n", await File.ReadAllTextAsync(Path.Combine(_folder, "config.yaml"), TestContext.Current.CancellationToken));

        (code, output, _) = await Run("formats", "json");
        Assert.Equal(Cli.Success, code);
        Assert.Contains("yaml, toml, xml", output);
    }

    [Fact]
    public async Task Help_and_version_need_no_engines()
    {
        var (code, output, _) = await Run("help", "convert");
        Assert.Equal(Cli.Success, code);
        Assert.Contains("--to", output);

        (code, output, _) = await Run("--version");
        Assert.Equal(Cli.Success, code);
        Assert.StartsWith("filee ", output);
    }

    [Fact]
    public async Task Case_distinct_inputs_are_both_converted_and_exact_duplicates_are_removed()
    {
        var lower = Png("photo.png");
        if (File.Exists(Path.Combine(_folder, "PHOTO.png")))
        {
            Assert.Skip("The temporary volume is case-insensitive.");
            return;
        }
        var upper = Png("PHOTO.png");
        var outputFolder = Path.Combine(_folder, "out");

        var (code, output, errors) = await Run("convert", lower, upper, lower, "--to", "jpg", "-o", outputFolder, "--json");

        Assert.Equal(Cli.Success, code);
        Assert.Equal("", errors);
        using var json = JsonDocument.Parse(output);
        Assert.Equal(2, json.RootElement.GetProperty("converted").GetInt32());
        Assert.Equal(2, json.RootElement.GetProperty("files").GetArrayLength());
        var outputPaths = json.RootElement.GetProperty("files").EnumerateArray()
            .Select(file => file.GetProperty("outputs")[0].GetString()!).ToArray();
        Assert.Equal(2, outputPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(outputPaths, path => Assert.True(File.Exists(path)));
    }

    [Fact]
    public async Task Unicode_spaces_and_quotes_stay_in_a_single_input_argument()
    {
        var name = OperatingSystem.IsWindows() ? "한글 report's 100%.png" : "한글 report's \"quoted\" 100%.png";
        var source = Png(name);
        var outputFolder = Path.Combine(_folder, "결과 output's");

        var (code, output, errors) = await Run("convert", source, "--to", "jpg", "-o", outputFolder, "--json");

        Assert.Equal(Cli.Success, code);
        Assert.Equal("", errors);
        using var json = JsonDocument.Parse(output);
        Assert.Equal(1, json.RootElement.GetProperty("converted").GetInt32());
        var result = json.RootElement.GetProperty("files")[0].GetProperty("outputs")[0].GetString();
        Assert.NotNull(result);
        Assert.Equal(outputFolder, Path.GetDirectoryName(result));
        Assert.Contains("한글 report's", Path.GetFileName(result));
        Assert.True(File.Exists(result));
        Assert.True(File.Exists(source));
    }
}
