// Resolves a view for a view model by naming convention:
//   Filee.App.ViewModels.Pages.HomePageViewModel  →  Filee.App.Views.Pages.HomePage

using Avalonia.Controls;
using Avalonia.Controls.Templates;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Filee.App;

public sealed class ViewLocator : IDataTemplate
{
    public Control? Build(object? data)
    {
        if (data is null)
            return null;
        var name = data.GetType().FullName!
            .Replace(".ViewModels.", ".Views.", StringComparison.Ordinal)
            .Replace("ViewModel", "", StringComparison.Ordinal);
        var type = Type.GetType(name);
        return type is null
            ? new TextBlock { Text = "View not found: " + name }
            : (Control)Activator.CreateInstance(type)!;
    }

    public bool Match(object? data) => data is ObservableObject && data.GetType().Name.EndsWith("PageViewModel", StringComparison.Ordinal);
}
