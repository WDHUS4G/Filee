// How Filee draws its transparent windows on Windows (issue #269): the FILEE_RENDERING escape hatch, the log line
// describing what Avalonia picked, Avalonia's own graphics errors in Filee's log, and backdrops Filee never asked for.

using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Logging;
using Avalonia.Platform;
using Filee.App.Services;
using Filee.Platform.Windows;
using Microsoft.Extensions.Logging;

namespace Filee.App.Tests;

public class WindowsRenderingTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("  ", null)]
    [InlineData(" Auto ", null)]
    [InlineData(" DComp ", "dcomp")]
    [InlineData("SOFTWARE", "software")]
    [InlineData("vulkan", "vulkan")]
    public void Normalize_trims_lowers_and_reads_auto_as_unset(string? value, string? expected) =>
        Assert.Equal(expected, WindowsRendering.Normalize(value));

    [Theory]
    [InlineData(null)]
    [InlineData("vulkan")]
    public void Unset_and_unknown_values_keep_Avalonias_defaults(string? choice)
    {
        var defaults = new Win32PlatformOptions();
        var options = WindowsRendering.Options(choice, adapters: null);
        Assert.Equal(defaults.RenderingMode, options.RenderingMode);
        Assert.Equal(defaults.CompositionMode, options.CompositionMode);
        Assert.Null(options.GraphicsAdapterSelectionCallback);
    }

    [Fact]
    public void Defaults_keep_transparency_in_every_fallback()
    {
        // The reason Filee doesn't reorder them: WinUI composition, DirectComposition and the redirection surface all
        // keep per-pixel alpha; the low-latency swap chain (which doesn't) isn't in the list.
        var defaults = new Win32PlatformOptions();
        Assert.DoesNotContain(Win32CompositionMode.LowLatencyDxgiSwapChain, defaults.CompositionMode);
        Assert.Equal(Win32CompositionMode.RedirectionSurface, defaults.CompositionMode[^1]);
        Assert.Equal(Win32RenderingMode.Software, defaults.RenderingMode[^1]);
    }

    [Fact]
    public void Dcomp_skips_WinUI_composition()
    {
        var options = WindowsRendering.Options("dcomp", adapters: null);
        Assert.Equal([Win32CompositionMode.DirectComposition, Win32CompositionMode.RedirectionSurface], options.CompositionMode);
        Assert.Equal(new Win32PlatformOptions().RenderingMode, options.RenderingMode);
    }

    [Fact]
    public void Redirection_draws_with_the_GPU_into_the_redirection_surface()
    {
        var options = WindowsRendering.Options("redirection", adapters: null);
        Assert.Equal([Win32CompositionMode.RedirectionSurface], options.CompositionMode);
        Assert.Equal(new Win32PlatformOptions().RenderingMode, options.RenderingMode);
    }

    [Fact]
    public void Software_leaves_the_GPU_out()
    {
        var options = WindowsRendering.Options("software", adapters: null);
        Assert.Equal([Win32RenderingMode.Software], options.RenderingMode);
    }

    [Fact]
    public void Every_documented_choice_changes_something()
    {
        var defaults = new Win32PlatformOptions();
        foreach (var choice in WindowsRendering.Choices)
        {
            var options = WindowsRendering.Options(choice, adapters: null);
            Assert.False(options.RenderingMode.SequenceEqual(defaults.RenderingMode)
                         && options.CompositionMode.SequenceEqual(defaults.CompositionMode), choice);
        }
    }

    [Fact]
    public void The_adapter_callback_reports_the_list_and_keeps_the_first_adapter()
    {
        var reported = new List<string>();
        var options = WindowsRendering.Options(null, reported.Add);
        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
        {
            // Avalonia picks around Adreno GPUs itself there; a callback would turn that off.
            Assert.Null(options.GraphicsAdapterSelectionCallback);
            return;
        }
        var chosen = options.GraphicsAdapterSelectionCallback!(
        [
            new PlatformGraphicsDeviceAdapterDescription { Description = "intel(r) uhd graphics" },
            new PlatformGraphicsDeviceAdapterDescription { Description = "nvidia geforce rtx 4060 laptop gpu" },
        ]);
        Assert.Equal(0, chosen);
        Assert.Equal(["[0] intel(r) uhd graphics (used), [1] nvidia geforce rtx 4060 laptop gpu"], reported);
    }

    [Fact]
    public void Adapters_without_names_still_read()
    {
        Assert.Equal("none", WindowsRendering.DescribeAdapters([], 0));
        Assert.Equal("[0] (no name) (used)", WindowsRendering.DescribeAdapters([null], 0));
    }

    [Theory]
    [InlineData(null, "Software")]
    [InlineData("D3D11AngleWin32PlatformGraphics", "AngleEgl")]
    [InlineData("WglPlatformOpenGlInterface", "Wgl")]
    [InlineData("VulkanPlatformGraphics", "Vulkan")]
    [InlineData("SomethingNew", "SomethingNew")]
    public void Rendering_names_follow_Avalonias_options(string? type, string expected) =>
        Assert.Equal(expected, WindowsRendering.RenderingName(type));

    [Theory]
    [InlineData(null, "RedirectionSurface")]
    [InlineData("WinUiCompositorConnection", "WinUIComposition")]
    [InlineData("DirectCompositionConnection", "DirectComposition")]
    [InlineData("DxgiConnection", "LowLatencyDxgiSwapChain")]
    public void Composition_names_follow_Avalonias_options(string? type, string expected) =>
        Assert.Equal(expected, WindowsRendering.CompositionName(type));

    [Fact]
    public void A_composition_mode_that_cant_be_looked_up_is_not_called_a_redirection_surface() =>
        Assert.Equal("an unknown composition mode", WindowsRendering.CompositionName(null, lookedUp: false));

    [AvaloniaFact]
    public void The_log_line_says_what_was_asked()
    {
        Assert.StartsWith("Rendering: ", WindowsRendering.Describe(null));
        Assert.EndsWith("(FILEE_RENDERING not set)", WindowsRendering.Describe(null));
        Assert.EndsWith("(FILEE_RENDERING=software)", WindowsRendering.Describe(" Software"));
        Assert.EndsWith("(FILEE_RENDERING=opengl is unknown and ignored)", WindowsRendering.Describe("opengl"));
    }
}

public class AvaloniaLogForwarderTests
{
    private sealed class ListSink : ILogSink
    {
        public List<string> Messages { get; } = [];

        public bool IsEnabled(LogEventLevel level, string area) => level >= LogEventLevel.Warning;

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate) => Messages.Add(messageTemplate);

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues) =>
            Messages.Add(messageTemplate);
    }

    [Theory]
    [InlineData(LogEventLevel.Error, "OpenGL", true)]
    [InlineData(LogEventLevel.Warning, "WinUIComposition", true)]
    [InlineData(LogEventLevel.Information, "OpenGL", false)]
    [InlineData(LogEventLevel.Error, LogArea.Win32Platform, true)]
    [InlineData(LogEventLevel.Warning, LogArea.Win32Platform, false)]
    [InlineData(LogEventLevel.Error, LogArea.Binding, false)]
    public void Only_graphics_start_up_problems_are_forwarded(LogEventLevel level, string area, bool forwarded) =>
        Assert.Equal(forwarded, AvaloniaLogForwarder.Forwards(level, area));

    [Theory]
    [InlineData("Unable to load ANGLE: {0}", "Unable to load ANGLE: boom")]
    [InlineData("Unsupported data format {Format}", "Unsupported data format boom")]
    [InlineData("No holes", "No holes")]
    public void Templates_get_their_values(string template, string expected) =>
        Assert.Equal(expected, AvaloniaLogForwarder.Format(template, ["boom"]));

    [Fact]
    public void Holes_without_a_value_stay_as_they_are() =>
        Assert.Equal("a {0} b {1}", AvaloniaLogForwarder.Format("a {0} b {1}", []));

    [Fact]
    public void Forwards_to_the_log_and_still_to_the_sink_before_it()
    {
        var inner = new ListSink();
        var log = new ListLogger();
        var sink = new AvaloniaLogForwarder(inner, log);

        sink.Log(LogEventLevel.Error, "OpenGL", null, "Unable to initialize ANGLE-based rendering with DirectX11 : {0}", "E_FAIL");
        sink.Log(LogEventLevel.Warning, LogArea.Binding, null, "A binding failed");

        Assert.Equal(2, inner.Messages.Count);
        var line = Assert.Single(log.Lines);
        Assert.Equal(LogLevel.Error, line.Level);
        Assert.Equal("Avalonia [OpenGL]: Unable to initialize ANGLE-based rendering with DirectX11 : E_FAIL", line.Message);
        Assert.True(sink.IsEnabled(LogEventLevel.Warning, "WinUIComposition"));
        Assert.False(sink.IsEnabled(LogEventLevel.Information, "OpenGL"));
    }

    [Fact]
    public void A_repeating_error_fills_only_a_few_lines()
    {
        var log = new ListLogger();
        var sink = new AvaloniaLogForwarder(null, log);
        for (var i = 0; i < AvaloniaLogForwarder.MaxLines * 3; i++)
            sink.Log(LogEventLevel.Error, LogArea.Win32Platform, null, "Failed to wait for vblank");
        Assert.Equal(AvaloniaLogForwarder.MaxLines, log.Lines.Count);
    }
}

public class WindowBackdropStateTests
{
    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    public void Only_a_material_counts_as_foreign(int backdrop, bool foreign) =>
        Assert.Equal(foreign, new WindowBackdropState(Redirected: false, Layered: false, backdrop).HasForeignMaterial);

    [Fact]
    public void Reads_as_a_log_line()
    {
        Assert.Equal("composition surface, system backdrop auto", new WindowBackdropState(false, false, 0).ToString());
        Assert.Equal("redirection surface, layered, system backdrop Acrylic", new WindowBackdropState(true, true, 3).ToString());
        Assert.Equal("composition surface, system backdrop unknown", new WindowBackdropState(false, false, -1).ToString());
    }
}
