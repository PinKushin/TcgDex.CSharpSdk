namespace TcgDex.Models;

using System.Collections;

/// <summary>
/// A read-only list that compares by its items, so a model that holds one compares by value.
/// </summary>
/// <typeparam name="T">The item type.</typeparam>
/// <remarks>
/// <para>
/// A record's synthesised equality compares each property with the default comparer for its declared type, and
/// for <c>IReadOnlyList&lt;T&gt;</c> that is reference equality, so two cards deserialized from the same JSON were
/// never equal (DECISIONS #40). The models keep their declared <c>IReadOnlyList&lt;T&gt;</c> property types, so the
/// public API does not change; the setter stores one of these, and the record's own equality then does the right
/// thing for every collection, including any property added later, without a hand-written <c>Equals</c> to keep in
/// step.
/// </para>
/// <para>
/// It wraps the list it is given rather than copying it, so it adds one small object per collection and
/// behaves exactly as the underlying list does. That includes aliasing: a model never copied the list it was
/// handed, and still does not, so editing a <c>List&lt;T&gt;</c> after giving it to a model changes the model, and
/// now changes its equality and hash too. Treat a list as given away. A copy would remove the hazard at the price
/// of a second array per collection on every deserialization, and was not taken (DECISIONS #40).
/// </para>
/// <para>
/// <c>Equals</c>, <c>GetHashCode</c> and <c>ToString</c> walk every item, and every nested model's own comparison in
/// turn, so they cost time proportional to the card's contents. Keying a cache on a whole <c>Card</c> pays that on
/// each lookup; key on <c>Card.Id</c> instead.
/// </para>
/// </remarks>
internal sealed class ValueList<T> : IReadOnlyList<T>, IEquatable<ValueList<T>>
{
    /// <summary>The list every model starts from, and what a null becomes.</summary>
    internal static readonly ValueList<T> Empty = new([]);

    private readonly IReadOnlyList<T> _items;

    private ValueList(IReadOnlyList<T> items)
    {
        _items = items;
    }

    /// <summary>Gets the number of items.</summary>
    public int Count => _items.Count;

    /// <summary>Gets the item at an index.</summary>
    /// <param name="index">The zero-based index.</param>
    public T this[int index] => _items[index];

    /// <summary>
    /// Wraps a list, or returns the shared empty list for null.
    /// </summary>
    /// <param name="items">The items, or <see langword="null"/>.</param>
    /// <returns>A list that compares by its items.</returns>
    internal static IReadOnlyList<T> From(IReadOnlyList<T>? items)
        => items switch
        {
            null => Empty,
            ValueList<T> already => already,
            _ => new ValueList<T>(items),
        };

    /// <inheritdoc />
    public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <inheritdoc />
    public bool Equals(ValueList<T>? other)
    {
        if (other is null || other._items.Count != _items.Count)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        EqualityComparer<T> comparer = EqualityComparer<T>.Default;

        for (int index = 0; index < _items.Count; index++)
        {
            if (!comparer.Equals(_items[index], other._items[index]))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as ValueList<T>);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        EqualityComparer<T> comparer = EqualityComparer<T>.Default;
        int hash = _items.Count;

        foreach (T item in _items)
        {
            // Order matters, so each item is folded into the running value rather than summed.
            hash = unchecked((hash * 31) + (item is null ? 0 : comparer.GetHashCode(item)));
        }

        return hash;
    }

    /// <summary>Renders the items as <c>[a, b]</c>, which is what a model's own text shows for the property.</summary>
    /// <returns>The items in brackets.</returns>
    public override string ToString() => "[" + string.Join(", ", _items) + "]";
}
