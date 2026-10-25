namespace TcgDex.Tests.Models;

using System.Collections;
using TcgDex.Models;

/// <summary>
/// The wrappers behind value equality (DECISIONS #40): they must still be the read-only collections the models
/// declare, and compare by contents.
/// </summary>
[TestFixture]
public sealed class ValueCollectionTests
{
    // ----- ValueList -----

    [Test]
    public void AList_FromNull_IsTheSharedEmptyList()
    {
        ValueList<string>.From(null).ShouldBeSameAs(ValueList<string>.Empty);
        ValueList<string>.Empty.Count.ShouldBe(0);
    }

    [Test]
    public void AList_FromAValueList_IsReturnedAsItIs_NotWrappedAgain()
    {
        IReadOnlyList<string> wrapped = ValueList<string>.From(new List<string> { "a" });

        ValueList<string>.From(wrapped).ShouldBeSameAs(wrapped);
    }

    [Test]
    public void AList_ReadsThroughToTheListItWraps()
    {
        List<string> source = ["a", "b"];
        IReadOnlyList<string> wrapped = ValueList<string>.From(source);

        wrapped.Count.ShouldBe(2);
        wrapped[1].ShouldBe("b");
        wrapped.ShouldBe(["a", "b"]);

        // The legacy, non-generic enumerator is part of the contract too.
        IEnumerator legacy = ((IEnumerable)wrapped).GetEnumerator();
        legacy.MoveNext().ShouldBeTrue();
        legacy.Current.ShouldBe("a");
    }

    [Test]
    public void AList_IsEqualToItself_AndNotToNullOrToAnotherType()
    {
        IReadOnlyList<string> list = ValueList<string>.From(["a"]);

        list.Equals(list).ShouldBeTrue();
        list.Equals((object?)null).ShouldBeFalse();
        list.Equals("a").ShouldBeFalse();
        ((ValueList<string>)list).Equals((ValueList<string>?)null).ShouldBeFalse();
    }

    [Test]
    public void AList_HoldingNulls_StillHashesAndComparesAndPrints()
    {
        IReadOnlyList<string?> first = ValueList<string?>.From(["a", null]);
        IReadOnlyList<string?> second = ValueList<string?>.From(["a", null]);

        // Equals, not ShouldBe: Shouldly compares two lists item by item and would never call the wrapper.
        first.Equals(second).ShouldBeTrue();
        first.GetHashCode().ShouldBe(second.GetHashCode());
        first.ToString().ShouldBe("[a, ]");
    }

    [Test]
    public void AList_WithTheSameItemsInADifferentOrder_HasADifferentHash()
    {
        // Not guaranteed by the contract, but the fold is order-sensitive on purpose: a plain sum would make
        // [a, b] and [b, a] collide, which these two short lists show it does not.
        ValueList<string>.From(["a", "b"]).GetHashCode()
            .ShouldNotBe(ValueList<string>.From(["b", "a"]).GetHashCode());
    }

    // ----- ValueDictionary -----

    private static IReadOnlyDictionary<string, int> Dictionary(params (string Key, int Value)[] entries)
        => ValueDictionary<string, int>.From(entries.ToDictionary(entry => entry.Key, entry => entry.Value));

    [Test]
    public void ADictionary_FromNull_IsTheSharedEmptyDictionary()
    {
        ValueDictionary<string, int>.From(null).ShouldBeSameAs(ValueDictionary<string, int>.Empty);
        ValueDictionary<string, int>.Empty.Count.ShouldBe(0);
    }

    [Test]
    public void ADictionary_FromAValueDictionary_IsReturnedAsItIs()
    {
        IReadOnlyDictionary<string, int> wrapped = Dictionary(("a", 1));

        ValueDictionary<string, int>.From(wrapped).ShouldBeSameAs(wrapped);
    }

    [Test]
    public void ADictionary_ReadsThroughToTheDictionaryItWraps()
    {
        IReadOnlyDictionary<string, int> wrapped = Dictionary(("a", 1), ("b", 2));

        wrapped.Count.ShouldBe(2);
        wrapped["b"].ShouldBe(2);
        wrapped.ContainsKey("a").ShouldBeTrue();
        wrapped.ContainsKey("z").ShouldBeFalse();
        wrapped.TryGetValue("a", out int found).ShouldBeTrue();
        found.ShouldBe(1);
        wrapped.TryGetValue("z", out _).ShouldBeFalse();
        wrapped.Keys.OrderBy(key => key).ShouldBe(["a", "b"]);
        wrapped.Values.OrderBy(value => value).ShouldBe([1, 2]);
        wrapped.OrderBy(entry => entry.Key).Select(entry => entry.Value).ShouldBe([1, 2]);

        IEnumerator legacy = ((IEnumerable)wrapped).GetEnumerator();
        legacy.MoveNext().ShouldBeTrue();
    }

    [Test]
    public void ADictionary_IsEqualToItself_AndNotToNullOrToAnotherType()
    {
        IReadOnlyDictionary<string, int> dictionary = Dictionary(("a", 1));

        dictionary.Equals(dictionary).ShouldBeTrue();
        dictionary.Equals((object?)null).ShouldBeFalse();
        dictionary.Equals("a").ShouldBeFalse();
        ((ValueDictionary<string, int>)dictionary).Equals((ValueDictionary<string, int>?)null).ShouldBeFalse();
    }

    [Test]
    public void ADictionary_IsEqualByEntries_InAnyOrder()
    {
        IReadOnlyDictionary<string, int> first = Dictionary(("a", 1), ("b", 2));
        IReadOnlyDictionary<string, int> second = Dictionary(("b", 2), ("a", 1));

        // Equals, not ShouldBe: Shouldly compares two dictionaries entry by entry in enumeration order.
        first.Equals(second).ShouldBeTrue();
        first.GetHashCode().ShouldBe(second.GetHashCode());
    }

    [Test]
    public void ADictionary_DiffersByCountByKeyAndByValue()
    {
        IReadOnlyDictionary<string, int> one = Dictionary(("a", 1));

        one.Equals(Dictionary(("a", 1), ("b", 2))).ShouldBeFalse();
        one.Equals(Dictionary(("b", 1))).ShouldBeFalse();
        one.Equals(Dictionary(("a", 2))).ShouldBeFalse();
        one.Equals(Dictionary(("a", 1))).ShouldBeTrue();
    }

    [Test]
    public void ADictionary_HoldingANullValue_StillHashesAndComparesAndPrints()
    {
        IReadOnlyDictionary<string, string?> first = ValueDictionary<string, string?>.From(new Dictionary<string, string?> { ["a"] = null });
        IReadOnlyDictionary<string, string?> second = ValueDictionary<string, string?>.From(new Dictionary<string, string?> { ["a"] = null });

        first.Equals(second).ShouldBeTrue();
        first.GetHashCode().ShouldBe(second.GetHashCode());
        first.ToString().ShouldBe("{a: }");
    }

    [Test]
    public void ADictionary_PrintsItsEntries()
    {
        Dictionary(("a", 1), ("b", 2)).ToString().ShouldBeOneOf("{a: 1, b: 2}", "{b: 2, a: 1}");
    }
}
