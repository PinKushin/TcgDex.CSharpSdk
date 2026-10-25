namespace TcgDex.Models;

using System.Collections;

/// <summary>
/// A read-only dictionary that compares by its entries, so a model that holds one compares by value.
/// </summary>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
/// <remarks>
/// The reason, and why it wraps rather than copies, are the same as for <see cref="ValueList{T}"/>. Two
/// dictionaries are equal when they hold the same keys with equal values, in any order.
/// </remarks>
internal sealed class ValueDictionary<TKey, TValue> : IReadOnlyDictionary<TKey, TValue>, IEquatable<ValueDictionary<TKey, TValue>>
    where TKey : notnull
{
    /// <summary>The dictionary every model starts from, and what a null becomes.</summary>
    internal static readonly ValueDictionary<TKey, TValue> Empty = new(new Dictionary<TKey, TValue>());

    private readonly IReadOnlyDictionary<TKey, TValue> _items;

    private ValueDictionary(IReadOnlyDictionary<TKey, TValue> items)
    {
        _items = items;
    }

    /// <inheritdoc />
    public int Count => _items.Count;

    /// <inheritdoc />
    public IEnumerable<TKey> Keys => _items.Keys;

    /// <inheritdoc />
    public IEnumerable<TValue> Values => _items.Values;

    /// <inheritdoc />
    public TValue this[TKey key] => _items[key];

    /// <summary>
    /// Wraps a dictionary, or returns the shared empty one for null.
    /// </summary>
    /// <param name="items">The entries, or <see langword="null"/>.</param>
    /// <returns>A dictionary that compares by its entries.</returns>
    internal static IReadOnlyDictionary<TKey, TValue> From(IReadOnlyDictionary<TKey, TValue>? items)
        => items switch
        {
            null => Empty,
            ValueDictionary<TKey, TValue> already => already,
            _ => new ValueDictionary<TKey, TValue>(items),
        };

    /// <inheritdoc />
    public bool ContainsKey(TKey key) => _items.ContainsKey(key);

    /// <inheritdoc />
    public bool TryGetValue(
        TKey key,
#if !NETSTANDARD2_0
        [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)]
#endif
        out TValue value)
        => _items.TryGetValue(key, out value);

    /// <inheritdoc />
    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => _items.GetEnumerator();

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <inheritdoc />
    public bool Equals(ValueDictionary<TKey, TValue>? other)
    {
        if (other is null || other._items.Count != _items.Count)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        EqualityComparer<TValue> comparer = EqualityComparer<TValue>.Default;

        foreach (KeyValuePair<TKey, TValue> entry in _items)
        {
            if (!other._items.TryGetValue(entry.Key, out TValue? theirs) || !comparer.Equals(entry.Value, theirs))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as ValueDictionary<TKey, TValue>);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        EqualityComparer<TValue> comparer = EqualityComparer<TValue>.Default;
        int hash = 0;

        foreach (KeyValuePair<TKey, TValue> entry in _items)
        {
            // Summed, not folded: the same entries in a different order are equal, so must hash alike.
            int valueHash = entry.Value is null ? 0 : comparer.GetHashCode(entry.Value);
            hash = unchecked(hash + ((entry.Key.GetHashCode() * 31) ^ valueHash));
        }

        return hash;
    }

    /// <summary>Renders the entries as <c>{key: value, ...}</c>.</summary>
    /// <returns>The entries in braces.</returns>
    public override string ToString()
        => "{" + string.Join(", ", _items.Select(entry => entry.Key + ": " + entry.Value)) + "}";
}
