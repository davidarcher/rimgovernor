namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// Adapts the bridge's persistent <see cref="Table{TValue}"/> to the facts store's <see cref="IKeyedRows"/>, so a big
/// keyed section is published as a table version and the recorder diffs it against the last it saw without walking the
/// rows both versions share (Go's <c>Table</c> implemented the interface itself; the Facts project cannot see the bridge).
/// </summary>
internal sealed class KeyedTableRows<T>(Table<T> table) : IKeyedRows
    where T : notnull
{
    public Table<T> Table { get; } = table;

    public void Rows(Action<string, object> row)
    {
        ArgumentNullException.ThrowIfNull(row);
        foreach (var pair in Table.All())
        {
            row(pair.Key, pair.Value);
        }
    }

    public void ChangedSince(object? previous, Action<string, object> changed, Action<string> removed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        ArgumentNullException.ThrowIfNull(removed);
        if (previous is not KeyedTableRows<T> prev)
        {
            Rows(changed);
            return;
        }
        Table.Changed(prev.Table, (id, row) => changed(id, row), removed);
    }
}
