// How Avalonia draws Filee's windows on Windows. The donut, the toast and the notices are transparent windows; a report
// of one standing on a dark rectangle (issue #269) needs to say which way they were drawn, so the log gets it once per
// start, with the graphics adapters, and FILEE_RENDERING picks another way without a new build.
//
// Avalonia's defaults: ANGLE (OpenGL ES on Direct3D 11) composed through WinUI composition, else DirectComposition, else
// drawn into the window's redirection surface; software rendering when Direct3D 11 can't start. Every one of these
// keeps per-pixel transparency (a redirection surface through DwmEnableBlurBehindWindow with an empty region), so
// Filee keeps the defaults and only changes them when the variable asks.

using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Platform;

namespace Filee.App.Services;

internal static class WindowsRendering
{
    public const string Variable = "FILEE_RENDERING";

    /// <summary>FILEE_RENDERING's values besides "auto" (or unset): Avalonia's defaults.</summary>
    public static readonly IReadOnlyList<string> Choices = ["dcomp", "redirection", "software"];

    /// <summary>The variable's value in lower case, or null for unset, empty and "auto".</summary>
    public static string? Normalize(string? value)
    {
        var choice = value?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(choice) || choice == "auto" ? null : choice;
    }

    /// <summary>
    /// Avalonia's Windows options for <paramref name="choice"/> (from <see cref="Normalize"/>): "dcomp" skips WinUI
    /// composition, "redirection" draws with Direct3D into the redirection surface, "software" without the GPU. Unknown
    /// values keep the defaults. <paramref name="adapters"/> hears which graphics adapters Direct3D found and which one
    /// it uses (the first, as without the callback). Not on ARM64: there the callback would turn off Avalonia's own
    /// choice of adapter.
    /// </summary>
    public static Win32PlatformOptions Options(string? choice, Action<string>? adapters)
    {
        var options = new Win32PlatformOptions();
        switch (choice)
        {
            case "dcomp":
                options.CompositionMode = [Win32CompositionMode.DirectComposition, Win32CompositionMode.RedirectionSurface];
                break;
            case "redirection":
                options.CompositionMode = [Win32CompositionMode.RedirectionSurface];
                break;
            case "software":
                options.RenderingMode = [Win32RenderingMode.Software];
                break;
        }
        if (adapters is not null && RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
            options.GraphicsAdapterSelectionCallback = list =>
            {
                adapters(DescribeAdapters(list.Select(a => a.Description).ToList(), 0));
                return 0;
            };
        return options;
    }

    /// <summary>"[0] intel(r) uhd graphics (used), [1] nvidia geforce rtx 4060 laptop gpu".</summary>
    public static string DescribeAdapters(IReadOnlyList<string?> names, int used) =>
        names.Count == 0
            ? "none"
            : string.Join(", ", names.Select((name, i) => $"[{i}] {name ?? "(no name)"}{(i == used ? " (used)" : "")}"));

    /// <summary>
    /// "Rendering: AngleEgl with WinUIComposition (FILEE_RENDERING not set)", from what Avalonia registered at start.
    /// </summary>
    public static string Describe(string? variable)
    {
        var locate = Locator();
        if (locate is null)
            return $"Rendering: unknown, Avalonia's services can't be read ({Asked(variable)})";
        var graphics = locate(typeof(IPlatformGraphics));
        var factoryType = Type.GetType("Avalonia.Win32.IWindowsSurfaceFactory, Avalonia.Win32", throwOnError: false);
        var factory = factoryType is null ? null : locate(factoryType);
        return $"Rendering: {RenderingName(graphics?.GetType().Name)} with " +
               $"{CompositionName(graphics is null ? null : factory?.GetType().Name, factoryType is not null)} ({Asked(variable)})";
    }

    private static string Asked(string? variable) => Normalize(variable) switch
    {
        null => $"{Variable} not set",
        var choice when Choices.Contains(choice) => $"{Variable}={choice}",
        var choice => $"{Variable}={choice} is unknown and ignored",
    };

    /// <summary>
    /// Avalonia's service locator, and the surface factory a composition mode registers in it, are left out of its
    /// reference assemblies: both are reached by name. Null when that no longer works.
    /// </summary>
    private static Func<Type, object?>? Locator()
    {
        try
        {
            var current = typeof(AvaloniaObject).Assembly.GetType("Avalonia.AvaloniaLocator")
                ?.GetProperty("Current")?.GetValue(null);
            var getService = current?.GetType().GetMethod("GetService", [typeof(Type)]);
            return getService is null ? null : type => getService.Invoke(current, [type]);
        }
        catch (Exception ex) when (ex is System.Reflection.TargetInvocationException or MemberAccessException)
        {
            return null;
        }
    }

    /// <summary>Avalonia's rendering mode for the type name of its platform graphics (null: software).</summary>
    public static string RenderingName(string? graphicsType) => graphicsType switch
    {
        null => "Software",
        "D3D11AngleWin32PlatformGraphics" => "AngleEgl",
        "D3D9AngleWin32PlatformGraphics" => "AngleEgl (Direct3D 9)",
        "WglPlatformOpenGlInterface" => "Wgl",
        _ when graphicsType.Contains("Vulkan", StringComparison.Ordinal) => "Vulkan",
        _ => graphicsType,
    };

    /// <summary>Avalonia's composition mode for the type name of its window surface factory (null: none registered).</summary>
    public static string CompositionName(string? factoryType, bool lookedUp = true) => factoryType switch
    {
        _ when !lookedUp => "an unknown composition mode",
        null => "RedirectionSurface",
        "WinUiCompositorConnection" => "WinUIComposition",
        "DirectCompositionConnection" => "DirectComposition",
        "DxgiConnection" => "LowLatencyDxgiSwapChain",
        _ => factoryType,
    };
}
