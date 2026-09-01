namespace FluxReader.Core.Services;

public static class SelectedItemPreserver
{
    public static IReadOnlyList<T> Preserve<T, TKey>(
        IReadOnlyList<T> items,
        T selectedItem,
        int selectedIndex,
        Func<T, TKey> keySelector)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(selectedItem);
        ArgumentNullException.ThrowIfNull(keySelector);

        var selectedKey = keySelector(selectedItem);
        if (items.Any(item => EqualityComparer<TKey>.Default.Equals(keySelector(item), selectedKey)))
        {
            return items;
        }

        var preservedItems = items.ToList();
        preservedItems.Insert(Math.Clamp(selectedIndex, 0, preservedItems.Count), selectedItem);
        return preservedItems;
    }
}
