// The items of a list that match the search box above it, kept in step with the list as it changes. Items are added
// and removed one by one (not cleared and refilled), so a ListBox keeps its selection while it stays visible.

using System.Collections.ObjectModel;

namespace Filee.App.ViewModels;

public sealed class FilteredList<T> : ObservableCollection<T> where T : class
{
    private readonly ObservableCollection<T> _source;
    private readonly Func<T, IEnumerable<string?>> _texts;
    private string _query = "";

    /// <param name="source">The full list.</param>
    /// <param name="texts">The texts of an item the query is looked for in (name, format, extensions …).</param>
    public FilteredList(ObservableCollection<T> source, Func<T, IEnumerable<string?>> texts)
    {
        _source = source;
        _texts = texts;
        source.CollectionChanged += (_, _) => Refresh();
        Refresh();
    }

    /// <summary>What was typed; empty shows everything. Matches any part of a text, ignoring case.</summary>
    public string Query
    {
        get => _query;
        set
        {
            var query = value?.Trim() ?? "";
            if (query == _query)
                return;
            _query = query;
            Refresh();
        }
    }

    public bool Matches(T item) =>
        _query.Length == 0 || _texts(item).Any(text => text?.Contains(_query, StringComparison.CurrentCultureIgnoreCase) == true);

    /// <summary>Brings the list in line with the source and the query (also after an item's text changed).</summary>
    public void Refresh()
    {
        var wanted = _source.Where(Matches).ToList();
        for (var i = Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(this[i]))
                RemoveAt(i);
        }
        for (var i = 0; i < wanted.Count; i++)
        {
            if (i < Count && ReferenceEquals(this[i], wanted[i]))
                continue;
            var at = IndexOf(wanted[i]);
            if (at >= 0)
                Move(at, i);
            else
                Insert(i, wanted[i]);
        }
    }
}
