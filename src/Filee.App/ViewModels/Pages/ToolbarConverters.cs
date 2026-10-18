using Avalonia.Data.Converters;

namespace Filee.App.ViewModels.Pages;

/// <summary>Converters used by the donut editor (slider percent → 0..1 fraction).</summary>
public static class ToolbarConverters
{
    public static readonly IValueConverter Percent = new FuncValueConverter<double, double>(v => v / 100.0);
}
