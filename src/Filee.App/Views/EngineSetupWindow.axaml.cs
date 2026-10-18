using Avalonia.Controls;
using Filee.App.Services;
using Filee.App.ViewModels;

namespace Filee.App.Views;

public partial class EngineSetupWindow : Window
{
    public EngineSetupWindow()
    {
        InitializeComponent();
        Motion.Track(this);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is EngineSetupViewModel vm)
                vm.CloseRequested += (_, _) => Close();
        };
    }
}
