namespace TcgDex.Tests;

using TcgDex;

/// <summary>
/// The argument-guard helpers every public entry point routes through.
/// </summary>
/// <remarks>
/// <para>
/// Tested directly rather than only through the call sites, because the call
/// sites cannot distinguish what these throw. Existing tests assert
/// <see cref="ArgumentException"/>, and <see cref="ArgumentNullException"/>
/// derives from it — so deleting the null check entirely left every one of them
/// passing, since a null string then falls through to the whitespace branch and
/// throws the base type instead.
/// </para>
/// <para>
/// The distinction is worth keeping. A caller catching
/// <c>ArgumentNullException</c> specifically, which is ordinary defensive code,
/// would silently stop catching it.
/// </para>
/// </remarks>
[TestFixture]
public sealed class GuardTests
{
    [Test]
    public void NotNull_WithNull_ThrowsNamingTheArgument()
    {
        object? value = null;

        ArgumentNullException exception = Should.Throw<ArgumentNullException>(() => Guard.NotNull(value));

        // The name comes from CallerArgumentExpression, so it is the caller's
        // own expression text rather than the parameter name inside Guard.
        exception.ParamName.ShouldBe("value");
    }

    [Test]
    public void NotNull_WithAValue_DoesNotThrow()
    {
        Should.NotThrow(() => Guard.NotNull(new object()));
    }

    [Test]
    public void NotNullOrWhiteSpace_WithNull_ThrowsArgumentNullException_NotTheBaseType()
    {
        string? value = null;

        ArgumentNullException exception = Should.Throw<ArgumentNullException>(() => Guard.NotNullOrWhiteSpace(value));

        exception.ParamName.ShouldBe("value");
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("\t")]
    public void NotNullOrWhiteSpace_WithBlank_ThrowsArgumentExceptionExplainingWhy(string value)
    {
        ArgumentException exception = Should.Throw<ArgumentException>(() => Guard.NotNullOrWhiteSpace(value));

        // Not ArgumentNullException: the value is present, just useless. And
        // the message has to say which of the two happened, or the caller
        // cannot tell an omitted argument from a blank one.
        exception.ShouldNotBeOfType<ArgumentNullException>();
        exception.Message.ShouldContain("empty");
        exception.Message.ShouldContain("whitespace");
    }

    [Test]
    public void NotNullOrWhiteSpace_WithText_DoesNotThrow()
    {
        Should.NotThrow(() => Guard.NotNullOrWhiteSpace("swsh3-136"));
    }

    [TestCase(0)]
    [TestCase(-1)]
    [TestCase(int.MinValue)]
    public void NotLessThan_BelowTheMinimum_ThrowsStatingTheMinimum(int value)
    {
        ArgumentOutOfRangeException exception = Should.Throw<ArgumentOutOfRangeException>(() => Guard.NotLessThan(value, 1));

        exception.ParamName.ShouldBe("value");
        exception.ActualValue.ShouldBe(value);

        // The minimum is the actionable half of the message — "must be greater
        // than or equal to 1" tells the caller what to pass, "out of range"
        // does not.
        exception.Message.ShouldContain("1");
    }

    [TestCase(1)]
    [TestCase(int.MaxValue)]
    public void NotLessThan_AtOrAboveTheMinimum_DoesNotThrow(int value)
    {
        // The boundary included: a value exactly equal to the minimum is
        // acceptable, which `<` versus `<=` decides.
        Should.NotThrow(() => Guard.NotLessThan(value, 1));
    }
}
