// Tests for Material You theme: dynamic tonal palette generation, theme dictionary application,
// softness adherence, theme.json overrides, monochrome contrast, and preset selection.

using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Filee.App.Services;
using Filee.App.ViewModels.Pages;
using Filee.Core.Localization;
using Filee.Core.Platform;
using Filee.Core.Settings;

namespace Filee.App.Tests;

public class MaterialYouThemeTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public void ThemeSettings_persists_ThemeStyle()
    {
        var settings = new AppSettings
        {
            Theme = new ThemeSettings
            {
                Style = ThemeStyle.MaterialYou,
                Accent = "#7F77DD",
            },
        };

        var json = JsonSerializer.Serialize(settings, JsonOptions);
        Assert.Contains("\"style\"", json);
        Assert.Contains("MaterialYou", json);

        var restored = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
        Assert.NotNull(restored);
        Assert.Equal(ThemeStyle.MaterialYou, restored.Theme.Style);
        Assert.Equal("#7F77DD", restored.Theme.Accent);
    }

    [Fact]
    public void Palette_generates_distinct_tonal_roles_for_light_and_dark()
    {
        var seed = Color.Parse("#7F77DD");
        var light = MaterialYouPalette.Generate(seed, isDark: false);
        var dark = MaterialYouPalette.Generate(seed, isDark: true);

        // Light mode: surfaces are bright, text is dark
        Assert.True(light.Page.R > 230 && light.Page.G > 230 && light.Page.B > 230);
        Assert.True(light.Surface.R >= 250 && light.Surface.G >= 250 && light.Surface.B >= 250);
        Assert.True(light.TextPrimary.R < 60 && light.TextPrimary.G < 60 && light.TextPrimary.B < 60);

        // Light mode accent roles: faint/soft are light backgrounds, strong is dark foreground
        Assert.True(MaterialYouPalette.Luminance(light.AccentFaint) > 0.85);
        Assert.True(MaterialYouPalette.Luminance(light.AccentStrong) < 0.25);

        // Dark mode: surfaces are deep dark, text is bright
        Assert.True(dark.Page.R < 40 && dark.Page.G < 40 && dark.Page.B < 40);
        Assert.True(dark.Surface.R < 50 && dark.Surface.G < 50 && dark.Surface.B < 50);
        Assert.True(dark.TextPrimary.R > 200 && dark.TextPrimary.G > 200 && dark.TextPrimary.B > 200);

        // Dark mode accent roles: faint is dark background, strong is bright foreground (for drop zone contrast!)
        Assert.True(MaterialYouPalette.Luminance(dark.AccentFaint) < 0.20);
        Assert.True(MaterialYouPalette.Luminance(dark.AccentStrong) > 0.65);
    }

    [Fact]
    public void Palette_generates_proper_monochrome_contrast()
    {
        var charcoal = Color.Parse("#1F1E1D");
        Assert.True(MaterialYouPalette.IsMonochrome(charcoal));

        var light = MaterialYouPalette.Generate(charcoal, isDark: false);
        var dark = MaterialYouPalette.Generate(charcoal, isDark: true);

        // Light mode monochrome: accent is dark charcoal, text on accent is white
        Assert.True(MaterialYouPalette.Luminance(light.Accent) < 0.15);
        Assert.Equal(Colors.White, light.OnAccent);

        // Dark mode monochrome: accent is silvery white, text on accent is dark charcoal
        Assert.True(MaterialYouPalette.Luminance(dark.Accent) > 0.80);
        Assert.Equal(Color.Parse("#1F1E1D"), dark.OnAccent);
    }

    [AvaloniaFact]
    public void ThemeService_applies_MaterialYou_to_theme_dictionaries()
    {
        TestServices.EnsureInitialized("en");
        var themeService = AppHost.Get<ThemeService>();
        var theme = new ThemeSettings
        {
            Style = ThemeStyle.MaterialYou,
            Accent = "#7F77DD",
            CornerRadius = 20,
        };

        try
        {
            themeService.Apply(theme, "en");

            Assert.Equal(ThemeStyle.MaterialYou, themeService.CurrentStyle);
            Assert.True(themeService.IsMaterialYou);

            var app = Application.Current!;
            Assert.True(app.Resources.ThemeDictionaries.ContainsKey(ThemeVariant.Light));
            Assert.True(app.Resources.ThemeDictionaries.ContainsKey(ThemeVariant.Dark));

            var lightDict = (ResourceDictionary)app.Resources.ThemeDictionaries[ThemeVariant.Light];
            var darkDict = (ResourceDictionary)app.Resources.ThemeDictionaries[ThemeVariant.Dark];

            // All standard theme brushes exist in both variants
            string[] requiredKeys =
            [
                "PageBrush", "SurfaceBrush", "SurfaceAltBrush", "SubtleBorderBrush",
                "TextPrimaryBrush", "TextSecondaryBrush", "DangerBrush", "SuccessBrush", "WarningBrush",
                "CardShadow", "AccentBrush", "AccentSoftBrush", "AccentFaintBrush", "AccentStrongBrush", "OnAccentBrush"
            ];

            foreach (var key in requiredKeys)
            {
                Assert.True(lightDict.ContainsKey(key), $"Missing key {key} in Light dictionary");
                Assert.True(darkDict.ContainsKey(key), $"Missing key {key} in Dark dictionary");
            }

            // Drop zone in dark mode uses AccentFaintBrush (dark background) and AccentStrongBrush (bright foreground)
            var darkFaint = Assert.IsType<SolidColorBrush>(darkDict["AccentFaintBrush"]);
            var darkStrong = Assert.IsType<SolidColorBrush>(darkDict["AccentStrongBrush"]);
            Assert.True(MaterialYouPalette.Luminance(darkFaint.Color) < 0.25);
            Assert.True(MaterialYouPalette.Luminance(darkStrong.Color) > 0.60);

            // Softness corner radii are applied and follow theme.CornerRadius
            var softRadius = Assert.IsType<CornerRadius>(app.Resources["SoftRadius"]);
            Assert.Equal(20, softRadius.TopLeft);
        }
        finally
        {
            themeService.Apply(new ThemeSettings(), "en");
        }
    }

    [AvaloniaFact]
    public void ThemeService_respects_softness_corner_radius()
    {
        TestServices.EnsureInitialized("en");
        var themeService = AppHost.Get<ThemeService>();
        var app = Application.Current!;

        // Softness = 8
        themeService.Apply(new ThemeSettings { Style = ThemeStyle.MaterialYou, CornerRadius = 8 }, "en");
        Assert.Equal(8, ((CornerRadius)app.Resources["SoftRadius"]!).TopLeft);
        Assert.Equal(4, ((CornerRadius)app.Resources["SoftRadiusSmall"]!).TopLeft);

        // Softness = 28
        themeService.Apply(new ThemeSettings { Style = ThemeStyle.MaterialYou, CornerRadius = 28 }, "en");
        Assert.Equal(28, ((CornerRadius)app.Resources["SoftRadius"]!).TopLeft);
        Assert.Equal(15, ((CornerRadius)app.Resources["SoftRadiusSmall"]!).TopLeft);

        // Reset
        themeService.Apply(new ThemeSettings(), "en");
    }

    [AvaloniaFact]
    public void ThemeService_respects_custom_theme_json_overrides()
    {
        TestServices.EnsureInitialized("en");
        var themeService = AppHost.Get<ThemeService>();
        var app = Application.Current!;

        var custom = new CustomTheme
        {
            Light = new Dictionary<string, string>
            {
                ["surface"] = "#123456",
            },
            Dark = new Dictionary<string, string>
            {
                ["surface"] = "#654321",
            },
        };

        try
        {
            themeService.Apply(new ThemeSettings { Style = ThemeStyle.MaterialYou }, "en", custom);

            var lightDict = (ResourceDictionary)app.Resources.ThemeDictionaries[ThemeVariant.Light];
            var darkDict = (ResourceDictionary)app.Resources.ThemeDictionaries[ThemeVariant.Dark];

            var lightSurface = Assert.IsType<SolidColorBrush>(lightDict["SurfaceBrush"]);
            Assert.Equal(Color.Parse("#123456"), lightSurface.Color);

            var darkSurface = Assert.IsType<SolidColorBrush>(darkDict["SurfaceBrush"]);
            Assert.Equal(Color.Parse("#654321"), darkSurface.Color);

            // Plain buttons follow the theme's surface (#524), not the Soft look's white.
            Assert.Same(lightSurface, lightDict["ButtonBackground"]);
            Assert.Same(darkSurface, darkDict["ButtonBackground"]);
            Assert.Same(lightDict["SurfaceAltBrush"], lightDict["ButtonBackgroundPointerOver"]);
        }
        finally
        {
            themeService.Apply(new ThemeSettings(), "en");
        }
    }

    [AvaloniaFact]
    public void Plain_buttons_are_the_surface_with_a_border_unlike_a_disabled_box()
    {
        TestServices.EnsureInitialized("en");
        var themeService = AppHost.Get<ThemeService>();
        try
        {
            foreach (var style in new[] { ThemeStyle.Soft, ThemeStyle.MaterialYou })
            {
                themeService.Apply(new ThemeSettings { Style = style }, "en");
                foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                {
                    Assert.Equal(ResolveBrush("SurfaceBrush", variant).Color, ResolveBrush("ButtonBackground", variant).Color);
                    Assert.Equal(ResolveBrush("SurfaceAltBrush", variant).Color, ResolveBrush("ButtonBackgroundPointerOver", variant).Color);
                    Assert.True(ResolveBrush("ButtonBorderBrush", variant).Color.A > 0);
                }
            }
        }
        finally
        {
            themeService.Apply(new ThemeSettings(), "en");
        }
    }

    [AvaloniaFact]
    public void ThemePageViewModel_switches_styles_and_presets()
    {
        TestServices.EnsureInitialized("en");
        var directory = Directory.CreateTempSubdirectory("filee-vm-test-").FullName;
        var store = new UserDataStore(directory);
        store.Load();

        try
        {
            var vm = new ThemePageViewModel(store, AppHost.Get<ILocalizer>(), AppHost.Get<IPlatformServices>());

            // Default style is Soft
            Assert.False(vm.IsMaterialYou);

            // Switch to Material You
            vm.SetStyleCommand.Execute(ThemeStyle.MaterialYou);
            Assert.True(vm.IsMaterialYou);
            Assert.Equal(ThemeStyle.MaterialYou, store.Settings.Theme.Style);

            // Switch preset
            var preset = vm.PastelPresets.First(p => p.Key == "lavender");
            vm.PickPresetCommand.Execute(preset);
            Assert.Equal("lavender", store.Settings.Theme.PresetKey);
            Assert.Equal(preset.Color, vm.Accent);
            Assert.Equal(store.Settings.Theme.Accent, vm.AccentHex);

            // Picking a custom swatch clears PresetKey
            vm.PickSwatchCommand.Execute(Colors.Teal);
            Assert.Null(store.Settings.Theme.PresetKey);
            Assert.Equal(Colors.Teal, vm.Accent);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [AvaloniaFact]
    public void Changing_accent_after_preset_clears_preset_key_and_applies_swatch()
    {
        TestServices.EnsureInitialized("en");
        var directory = Directory.CreateTempSubdirectory("filee-preset-swatch-test-").FullName;
        try
        {
            var store = new UserDataStore(directory);
            store.Load();
            var loc = AppHost.Get<ILocalizer>();
            var platform = AppHost.Get<IPlatformServices>();
            var themeService = AppHost.Get<ThemeService>();
            var vm = new ThemePageViewModel(store, loc, platform);

            // 1. Pick a preset
            var preset = vm.PastelPresets.First(p => p.Key == "lavender");
            vm.PickPresetCommand.Execute(preset);
            Assert.Equal("lavender", store.Settings.Theme.PresetKey);
            Assert.Equal(preset.Color, vm.Accent);

            // 2. Direct swatch selection by binding Accent (as Swatch ListBox and ColorPicker do)
            var teal = Color.Parse("#008080");
            vm.Accent = teal;
            Assert.Null(store.Settings.Theme.PresetKey);
            Assert.Equal(teal, vm.Accent);
            Assert.Equal("#008080", store.Settings.Theme.Accent);

            // 3. ThemeService applying the settings must use the swatch accent, not preset
            themeService.Apply(store.Settings.Theme, "en");
            var res = Application.Current!.Resources;
            Assert.Equal(teal, ((SolidColorBrush)res["AccentBrush"]!).Color);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [AvaloniaFact]
    public void Soft_style_preserves_custom_grey_swatch_without_monochrome_conversion()
    {
        TestServices.EnsureInitialized("en");
        var themeService = AppHost.Get<ThemeService>();
        var grey = Color.Parse("#5F5E5A");

        try
        {
            themeService.Apply(new ThemeSettings { Style = ThemeStyle.Soft, Accent = "#5F5E5A" }, "en");
            var res = Application.Current!.Resources;

            // In Soft style without monochrome preset, accent must stay #5F5E5A exactly as on main
            Assert.Equal(grey, ((SolidColorBrush)res["AccentBrush"]!).Color);
        }
        finally
        {
            themeService.Apply(new ThemeSettings(), "en");
        }
    }

    [AvaloniaFact]
    public void Controls_resolve_MaterialYou_dark_accent_roles_at_runtime_without_top_level_shadowing()
    {
        TestServices.EnsureInitialized("en");
        var themeService = AppHost.Get<ThemeService>();
        var seed = Color.Parse("#7F77DD");

        try
        {
            themeService.Apply(new ThemeSettings { Style = ThemeStyle.MaterialYou, Accent = "#7F77DD" }, "en");

            var darkWindow = new Window();
            var found = darkWindow.TryFindResource("AccentStrongBrush", ThemeVariant.Dark, out var strongResource);
            Assert.True(found, "TryFindResource AccentStrongBrush failed");
            var strongBrush = Assert.IsType<SolidColorBrush>(strongResource);

            var foundSoft = darkWindow.TryFindResource("AccentSoftBrush", ThemeVariant.Dark, out var softResource);
            Assert.True(foundSoft, "TryFindResource AccentSoftBrush failed");
            var softBrush = Assert.IsType<SolidColorBrush>(softResource);

            // Old SoftPalette.ApplyAccent wrote Shade(#7F77DD, -0.25) which is #5F59A6 (luminance ~0.11)
            var oldShadowedColor = SoftPalette.Shade(seed, -0.25);
            Assert.NotEqual(oldShadowedColor, strongBrush.Color);

            // Material You Dark AccentStrong is onPrimaryContainer (bright foreground, luminance > 0.65)
            Assert.True(MaterialYouPalette.Luminance(strongBrush.Color) > 0.65,
                $"Expected bright AccentStrongBrush in dark mode, got luminance {MaterialYouPalette.Luminance(strongBrush.Color)}");

            // Material You Dark AccentSoft is primaryContainer (opaque tonal background, not 18% alpha)
            Assert.Equal(255, softBrush.Color.A);
            Assert.True(MaterialYouPalette.Luminance(softBrush.Color) < 0.35);
        }
        finally
        {
            themeService.Apply(new ThemeSettings(), "en");
        }
    }

    [AvaloniaFact]
    public void Controls_resolve_monochrome_contrast_in_dark_mode()
    {
        TestServices.EnsureInitialized("en");
        var themeService = AppHost.Get<ThemeService>();

        try
        {
            // Apply monochrome in both Soft and Material You styles
            foreach (var style in new[] { ThemeStyle.Soft, ThemeStyle.MaterialYou })
            {
                themeService.Apply(new ThemeSettings { Style = style, Accent = "#1F1E1D", PresetKey = "monochrome" }, "en");

                var accentBrush = ResolveBrush("AccentBrush", ThemeVariant.Dark);
                var onAccentBrush = ResolveBrush("OnAccentBrush", ThemeVariant.Dark);

                // In dark mode, monochrome accent must resolve to silvery white (#F1EFE8), NOT dark charcoal (#1F1E1D)
                Assert.True(MaterialYouPalette.Luminance(accentBrush.Color) > 0.80,
                    $"{style}: Expected silvery white in dark mode, got {accentBrush.Color}");
                Assert.True(MaterialYouPalette.Luminance(onAccentBrush.Color) < 0.20,
                    $"{style}: Expected dark text on accent in dark mode, got {onAccentBrush.Color}");

                var lightAccentBrush = ResolveBrush("AccentBrush", ThemeVariant.Light);
                var lightOnAccentBrush = ResolveBrush("OnAccentBrush", ThemeVariant.Light);

                // In light mode, monochrome accent must resolve to dark charcoal (#1F1E1D)
                Assert.True(MaterialYouPalette.Luminance(lightAccentBrush.Color) < 0.20,
                    $"{style}: Expected dark charcoal in light mode, got {lightAccentBrush.Color}");
                Assert.Equal(Colors.White, lightOnAccentBrush.Color);
            }
        }
        finally
        {
            themeService.Apply(new ThemeSettings(), "en");
        }
    }

    [AvaloniaFact]
    public void Preset_pair_switches_between_light_and_dark_under_system_mode()
    {
        TestServices.EnsureInitialized("en");
        var themeService = AppHost.Get<ThemeService>();

        try
        {
            // Apply mint preset under System mode
            themeService.Apply(new ThemeSettings { Mode = ThemeMode.System, PresetKey = "mint" }, "en");

            var lightBrush = ResolveBrush("AccentBrush", ThemeVariant.Light);
            var darkBrush = ResolveBrush("AccentBrush", ThemeVariant.Dark);

            var mintPreset = ThemeService.Presets.First(p => p.Key == "mint");
            Assert.Equal(mintPreset.LightColor, lightBrush.Color);
            Assert.Equal(mintPreset.DarkColor, darkBrush.Color);
        }
        finally
        {
            themeService.Apply(new ThemeSettings(), "en");
        }
    }

    private static SolidColorBrush ResolveBrush(string key, ThemeVariant variant)
    {
        var window = new Window();
        Assert.True(window.TryFindResource(key, variant, out var value), $"Failed to resolve resource {key} for variant {variant}");
        return Assert.IsType<SolidColorBrush>(value);
    }

    [AvaloniaFact]
    public void Softness_derives_SoftRadiusPill_consistently()
    {
        TestServices.EnsureInitialized("en");
        var app = Application.Current!;

        // Softness = 0 (sharp corners) -> SoftRadiusPill is 0
        SoftPalette.ApplyCornerRadius(app.Resources, 0);
        Assert.Equal(0, ((CornerRadius)app.Resources["SoftRadiusPill"]!).TopLeft);

        // Softness = 16 (default) -> SoftRadiusPill is round(16 * 2.5) = 40
        SoftPalette.ApplyCornerRadius(app.Resources, 16);
        Assert.Equal(40, ((CornerRadius)app.Resources["SoftRadiusPill"]!).TopLeft);
    }
}
