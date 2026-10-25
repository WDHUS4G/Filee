// The order of a merge into one PDF (issue #268): the files start sorted by name the way people count ("page 2" before
// "page 10"), and the user moves them before the merge starts. Moving a row changes only this copy of the selection.

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.Core.Platform;
using Filee.Core.Web;

namespace Filee.App.ViewModels;

/// <summary>One file of the merge: its name, and the folder it is in (none for a file from a browser, not saved yet).</summary>
public sealed class MergeSource(string path)
{
    public string Path { get; } = path;
    public string Name => System.IO.Path.GetFileName(Path);
    public string Folder => WebDrop.IsPlaceholder(Path) ? "" : System.IO.Path.GetDirectoryName(Path) ?? "";
}

public sealed partial class MergeOrderViewModel : ObservableObject
{
    public MergeOrderViewModel(IReadOnlyList<string> files)
    {
        Files = new(files
            .OrderBy(file => System.IO.Path.GetFileName(file), NaturalComparer.Instance)
            .ThenBy(file => file, NaturalComparer.Instance)
            .Select(file => new MergeSource(file)));
        SelectedFile = Files.FirstOrDefault();
    }

    public ObservableCollection<MergeSource> Files { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MoveUpCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveDownCommand))]
    private MergeSource? _selectedFile;

    /// <summary>The paths in the order they are merged.</summary>
    public IReadOnlyList<string> OrderedFiles => Files.Select(file => file.Path).ToArray();

    private bool CanMoveUp() => SelectedFile is not null && Files.IndexOf(SelectedFile) > 0;
    private bool CanMoveDown() => SelectedFile is not null && Files.IndexOf(SelectedFile) is >= 0 and var index && index < Files.Count - 1;

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp() => Move(-1);

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown() => Move(1);

    private void Move(int direction)
    {
        if (SelectedFile is not { } selected)
            return;
        var index = Files.IndexOf(selected);
        var target = index + direction;
        if (index < 0 || target < 0 || target >= Files.Count)
            return;
        Files.Move(index, target);
        // The list box can drop its selection while it moves the row.
        SelectedFile = selected;
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
    }
}
