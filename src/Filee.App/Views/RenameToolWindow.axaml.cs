using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Filee.App.Services;
using Filee.App.ViewModels;

namespace Filee.App.Views;

/// <summary>Modal editor of a watch folder's rename steps. Returns true from ShowDialog when the user saved.</summary>
public partial class RenameToolWindow : Window
{
    public RenameToolWindow()
    {
        InitializeComponent();
        Motion.Track(this);
    }

    /// <summary>Double-clicking a ready-made replacement adds it as a step, like its + button.</summary>
    private void OnRecipeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is RenameRecipeViewModel recipe)
            recipe.AddCommand.Execute(null);
    }

    private void OnInsertToken(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is string token && DataContext is RenameToolViewModel tool)
            tool.InsertToken(token);
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        if (DataContext is RenameToolViewModel { IsValid: true })
            Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
