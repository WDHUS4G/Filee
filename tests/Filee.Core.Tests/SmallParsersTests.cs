using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Presets;

namespace Filee.Core.Tests;

public class PageRangeTests
{
    [Theory]
    [InlineData("", 5, new[] { 0, 1, 2, 3, 4 })]
    [InlineData("2", 5, new[] { 1 })]
    [InlineData("2-4", 5, new[] { 1, 2, 3 })]
    [InlineData("4-", 5, new[] { 3, 4 })]
    [InlineData("-2", 5, new[] { 0, 1 })]
    [InlineData("5,1, 3", 5, new[] { 0, 2, 4 })]
    [InlineData("4-2", 5, new[] { 1, 2, 3 })]
    [InlineData("9-12", 5, new[] { 0, 1, 2, 3, 4 })] // nothing valid → everything
    [InlineData("x, 2", 5, new[] { 1 })]
    public void Parses_ranges(string expression, int pages, int[] expected) =>
        Assert.Equal(expected, PageRange.Parse(expression, pages));
}

public class FormatRegistryTests
{
    [Theory]
    [InlineData("a.JPEG", "jpg")]
    [InlineData("b.tif", "tiff")]
    [InlineData("c.HWPX", "hwpx")]
    [InlineData(".md", "md")]
    [InlineData("DSCF0001.HIF", "heic")] // Fujifilm's HEIF
    [InlineData("anim.apng", "apng")]
    [InlineData("scan.dicom", "dcm")]
    [InlineData("texture.rgb", "sgi")]
    public void Detects_formats(string path, string id) => Assert.Equal(id, FormatRegistry.Detect(path)?.Id);

    [Fact]
    public void Unknown_extension_is_null() => Assert.Null(FormatRegistry.Detect("x.xyz"));

    [Fact]
    public void Extensions_are_unique() =>
        Assert.Equal(
            FormatRegistry.Known.SelectMany(f => f.Extensions).Count(),
            FormatRegistry.Known.SelectMany(f => f.Extensions).Distinct(StringComparer.OrdinalIgnoreCase).Count());
}

public class BuiltInDataTests
{
    [Fact]
    public void Profiles_only_reference_existing_presets()
    {
        var ids = BuiltInData.CreatePresets().Select(p => p.Id).ToHashSet();
        foreach (var profile in BuiltInData.CreateProfiles())
            Assert.All(profile.PresetIds, id => Assert.Contains(id, ids));
    }

    [Theory]
    [InlineData("\"left90\"", ImageRotation.Left90)]
    [InlineData("\"Rotate180\"", ImageRotation.Rotate180)]
    [InlineData("3", ImageRotation.Rotate180)]
    [InlineData("\"Left90, Rotate180\"", ImageRotation.None)] // flags OR'd into another value
    [InlineData("\"2\"", ImageRotation.None)] // a number in a string isn't a name
    [InlineData("\"Sideways\"", ImageRotation.None)]
    [InlineData("null", ImageRotation.None)]
    public void Saved_enum_values_are_read_by_name_and_anything_else_is_the_default(string json, ImageRotation expected)
    {
        var options = new System.Text.Json.JsonSerializerOptions { Converters = { new LenientEnumJsonConverter<ImageRotation>() } };
        Assert.Equal(expected, System.Text.Json.JsonSerializer.Deserialize<ImageRotation>(json, options));
    }

    [Fact]
    public void A_group_clone_keeps_its_kind_and_members()
    {
        var group = new Preset { Kind = PresetKind.Group, SubPresetIds = ["to-png", "to-jpg"] };
        var clone = group.Clone();
        Assert.True(clone.IsGroup);
        Assert.Equal(["to-png", "to-jpg"], clone.SubPresetIds);
        clone.SubPresetIds.Add("to-webp");
        Assert.Equal(2, group.SubPresetIds.Count); // a copy, not the same list
        Assert.False(new Preset { SubPresetIds = ["to-png"] }.IsGroup); // members alone don't make a group
    }

    [Fact]
    public void Built_in_groups_fan_out_existing_conversions_on_no_default_donut()
    {
        var presets = BuiltInData.CreatePresets().ToDictionary(p => p.Id);
        var groups = presets.Values.Where(p => p.IsGroup).ToList();
        Assert.NotEmpty(groups);
        foreach (var group in groups)
        {
            Assert.InRange(group.SubPresetIds.Count, 1, Preset.MaxSubPresets);
            Assert.All(group.SubPresetIds, id => Assert.False(presets[id].IsGroup, id));
        }
        Assert.DoesNotContain(BuiltInData.CreateProfiles(), p => p.PresetIds.Any(id => presets[id].IsGroup));
    }

    [Fact]
    public void Preset_ids_are_unique()
    {
        var presets = BuiltInData.CreatePresets();
        Assert.Equal(presets.Count, presets.Select(p => p.Id).Distinct().Count());
    }

    [Fact]
    public void Exactly_one_fallback_profile() =>
        Assert.Single(BuiltInData.CreateProfiles(), p => p.IsFallback);

    [Fact]
    public void Preset_targets_are_known_formats()
    {
        foreach (var preset in BuiltInData.CreatePresets())
        {
            if (preset.IsGroup)
                Assert.Equal(Preset.GroupTarget, preset.TargetFormat); // none an older Filee could run it with
            else
                Assert.True(preset.TargetFormat == BuiltInData.SameAsSource || FormatRegistry.FindById(preset.TargetFormat) is not null, preset.Id);
        }
    }
}
