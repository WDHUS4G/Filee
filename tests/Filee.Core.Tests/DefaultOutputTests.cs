using Filee.Core.Presets;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

/// <summary>The default save location in Settings → General and the presets that follow it.</summary>
public class DefaultOutputTests
{
    [Fact]
    public void New_and_built_in_presets_follow_the_default()
    {
        Assert.Equal(OutputLocation.Default, new Preset().Output.Location);
        // Only presets that need their own place keep it (PDF split pages go into a folder per document).
        Assert.All(BuiltInData.CreatePresets().Where(p => p.Id != "pdf-split"),
            p => Assert.Equal(OutputLocation.Default, p.Output.Location));
    }

    [Fact]
    public void Default_starts_as_the_source_folder_like_before()
    {
        var settings = new AppSettings();
        var preset = new Preset { TargetFormat = "png", Output = { FileNamePattern = "{name}_x" } };

        var used = settings.WithDefaultOutput(preset);

        Assert.Equal(OutputLocation.SameFolder, used.Output.Location);
        Assert.Equal("{name}_x", used.Output.FileNamePattern);
        Assert.Equal(OutputLocation.Default, preset.Output.Location); // the saved preset is not changed
    }

    [Fact]
    public void Presets_on_default_save_to_the_folder_from_settings()
    {
        var settings = new AppSettings
        {
            DefaultOutput = { Location = OutputLocation.CustomFolder, CustomFolder = Path.Combine("D:", "Converted") },
        };
        var preset = new Preset { TargetFormat = "png", Output = { Conflict = ConflictPolicy.Overwrite, KeepDates = true } };

        var used = settings.WithDefaultOutput(preset);
        var path = OutputPathResolver.Resolve(used.Output,
            new OutputPathResolver.Tokens(Path.Combine("C:", "Users", "me", "Documents", "a.jpg"), "PNG", 1, DateTime.Now),
            "png", _ => false);

        Assert.Equal(Path.Combine("D:", "Converted", "a.png"), path);
        Assert.Equal(ConflictPolicy.Overwrite, used.Output.Conflict);
        Assert.True(used.Output.KeepDates);
        Assert.Equal(preset.Id, used.Id);
    }

    [Fact]
    public void Default_subfolder_is_next_to_the_source()
    {
        var settings = new AppSettings { DefaultOutput = { Location = OutputLocation.Subfolder, SubfolderName = "out" } };

        var used = settings.WithDefaultOutput(new Preset { TargetFormat = "png" });

        Assert.Equal(OutputLocation.Subfolder, used.Output.Location);
        Assert.Equal("out", used.Output.SubfolderName);
    }

    [Fact]
    public void Keep_file_dates_turns_dates_on_for_every_preset()
    {
        var settings = new AppSettings { KeepFileDates = true };
        var own = new Preset { Output = { Location = OutputLocation.SameFolder } };

        var applied = settings.WithDefaultOutput(own);

        Assert.True(applied.Output.KeepDates);
        Assert.False(own.Output.KeepDates); // a copy: the saved preset is unchanged
        Assert.Same(own, new AppSettings().WithDefaultOutput(own));
    }

    [Fact]
    public void Presets_with_their_own_location_ignore_the_default()
    {
        var settings = new AppSettings { DefaultOutput = { Location = OutputLocation.CustomFolder, CustomFolder = @"D:\x" } };
        var own = new Preset { Output = { Location = OutputLocation.SameFolder } };

        Assert.Same(own, settings.WithDefaultOutput(own));
    }

    [Fact]
    public void Default_without_settings_means_the_source_folder()
    {
        // The command line resolves presets without Settings → General.
        using var dir = new TempDir();
        var source = dir.File("a.jpg");

        var path = OutputPathResolver.Resolve(new OutputRule(), new OutputPathResolver.Tokens(source, "PNG", 1, DateTime.Now), "png", File.Exists);

        Assert.Equal(Path.Combine(dir.Path, "a.png"), path);
    }

    [Fact]
    public void Version_7_presets_on_the_source_folder_follow_the_default()
    {
        // As written by 1.3.1: "same folder" was the only default, a user preset chose a fixed folder.
        var presets = BuiltInData.CreatePresets();
        foreach (var preset in presets.Where(p => p.Output.Location == OutputLocation.Default))
            preset.Output.Location = OutputLocation.SameFolder;
        presets.Add(new Preset { Id = "mine", Output = { Location = OutputLocation.CustomFolder, CustomFolder = @"D:\out" } });
        var profiles = BuiltInData.CreateProfiles();

        Assert.True(SettingsMigrations.ApplyToLibrary(7, presets, profiles));

        Assert.Equal(OutputLocation.Default, presets.Single(p => p.Id == "to-png").Output.Location);
        Assert.Equal(OutputLocation.Subfolder, presets.Single(p => p.Id == "pdf-split").Output.Location);
        Assert.Equal(OutputLocation.CustomFolder, presets.Single(p => p.Id == "mine").Output.Location);
        Assert.False(SettingsMigrations.ApplyToLibrary(7, presets, profiles)); // once only
    }

    [Fact]
    public void Default_save_location_survives_a_restart()
    {
        using var dir = new TempDir();
        var store = new UserDataStore(dir.Path);
        store.Load();
        store.Settings.DefaultOutput.Location = OutputLocation.CustomFolder;
        store.Settings.DefaultOutput.CustomFolder = @"D:\Converted";
        store.SaveSettings();

        var next = new UserDataStore(dir.Path);
        next.Load();

        Assert.Equal(OutputLocation.CustomFolder, next.Settings.DefaultOutput.Location);
        Assert.Equal(@"D:\Converted", next.Settings.DefaultOutput.CustomFolder);
    }
}
