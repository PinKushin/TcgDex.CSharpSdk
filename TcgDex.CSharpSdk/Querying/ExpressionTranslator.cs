namespace TcgDex.Querying;

using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;

/// <summary>
/// Translates a predicate expression into the API's filter parameters.
/// </summary>
/// <remarks>
/// <para>
/// The tree is <em>walked</em>, never compiled. <see cref="Expression"/>.Compile
/// emits IL at runtime, which Native AOT cannot do, so captured variables are
/// read directly from their closure instead. Walking is also cheaper — there is
/// no code generation at all.
/// </para>
/// <para>
/// Only the operators the API actually has are supported. A predicate with no
/// counterpart is rejected with a message naming it, rather than being
/// approximated into a filter that would quietly return the wrong cards.
/// </para>
/// </remarks>
internal static class ExpressionTranslator
{
    internal static IReadOnlyList<QueryFilter> Translate<T>(Expression<Func<T, bool>> predicate)
    {
        Guard.NotNull(predicate);

        List<QueryFilter> filters = new();
        Visit(predicate.Body, predicate.Parameters[0], filters);
        return filters;
    }

    /// <summary>
    /// Resolves the API field name a sort selector refers to.
    /// </summary>
    /// <param name="selector">A selector such as <c>c =&gt; c.Name</c>.</param>
    /// <returns>The API field name, for example <c>name</c>.</returns>
    /// <exception cref="NotSupportedException">
    /// The selector is not a direct property of the model.
    /// </exception>
    internal static string SortFieldName<T, TKey>(Expression<Func<T, TKey>> selector)
    {
        Guard.NotNull(selector);

        if (!TryGetMember(selector.Body, selector.Parameters[0], out MemberExpression? member))
        {
            throw new NotSupportedException(
                $"Sorting requires a direct property of the model, but got '{selector.Body}'.");
        }

        return FieldName(member);
    }

    private static void Visit(Expression node, ParameterExpression parameter, List<QueryFilter> filters)
    {
        switch (node)
        {
            case BinaryExpression { NodeType: ExpressionType.AndAlso } and_:
                Visit(and_.Left, parameter, filters);
                Visit(and_.Right, parameter, filters);
                return;

            case BinaryExpression { NodeType: ExpressionType.OrElse } or_:
                filters.Add(TranslateOr(or_, parameter));
                return;

            case BinaryExpression binary:
                filters.Add(TranslateComparison(binary, parameter));
                return;

            case UnaryExpression { NodeType: ExpressionType.Not } not:
                filters.Add(Negate(TranslateSingle(not.Operand, parameter), not.Operand));
                return;

            case MethodCallExpression call:
                filters.Add(TranslateMethodCall(call, parameter));
                return;

            default:
                throw Unsupported(node);
        }
    }

    private static QueryFilter TranslateSingle(Expression node, ParameterExpression parameter)
        => node switch
        {
            BinaryExpression binary => TranslateComparison(binary, parameter),
            MethodCallExpression call => TranslateMethodCall(call, parameter),
            _ => throw Unsupported(node),
        };

    /// <summary>
    /// Merges an OR into a single multi-valued filter.
    /// </summary>
    /// <remarks>
    /// The API expresses OR as <c>field=eq:a|b</c>, which only works within one
    /// field. An OR spanning two fields has no representation, so it fails here
    /// rather than silently dropping half the predicate.
    /// </remarks>
    private static QueryFilter TranslateOr(BinaryExpression node, ParameterExpression parameter)
    {
        QueryFilter left = TranslateSingle(node.Left, parameter);
        QueryFilter right = TranslateSingle(node.Right, parameter);

        if (!string.Equals(left.Field, right.Field, StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                $"The TCGdex API can only combine alternatives within a single field, but this " +
                $"predicate ORs '{left.Field}' with '{right.Field}'. Issue one query per field " +
                $"and combine the results, or narrow the predicate.");
        }

        if (left.Operator != right.Operator)
        {
            throw new NotSupportedException(
                $"Both sides of an OR on '{left.Field}' must use the same operator, but got " +
                $"{left.Operator} and {right.Operator}.");
        }

        return left with { Values = [.. left.Values, .. right.Values] };
    }

    private static QueryFilter TranslateComparison(BinaryExpression node, ParameterExpression parameter)
    {
        (MemberExpression? member, object? value, bool flipped) = OrientOperands(node, parameter);
        string field = FieldName(member);

        // A comparison against null is the API's presence check, not an
        // equality test.
        if (value is null)
        {
            return node.NodeType switch
            {
                ExpressionType.Equal => new QueryFilter(field, QueryOperator.Null, []),
                ExpressionType.NotEqual => new QueryFilter(field, QueryOperator.NotNull, []),
                _ => throw Unsupported(node),
            };
        }

        QueryOperator comparison = node.NodeType switch
        {
            ExpressionType.Equal => QueryOperator.Equal,
            ExpressionType.NotEqual => QueryOperator.NotEqual,
            ExpressionType.GreaterThan => flipped ? QueryOperator.LessThan : QueryOperator.GreaterThan,
            ExpressionType.GreaterThanOrEqual => flipped ? QueryOperator.LessThanOrEqual : QueryOperator.GreaterThanOrEqual,
            ExpressionType.LessThan => flipped ? QueryOperator.GreaterThan : QueryOperator.LessThan,
            ExpressionType.LessThanOrEqual => flipped ? QueryOperator.GreaterThanOrEqual : QueryOperator.LessThanOrEqual,
            _ => throw Unsupported(node),
        };

        return new QueryFilter(field, comparison, [Format(value)]);
    }

    /// <summary>
    /// Puts the member on the left and the value on the right, reporting whether
    /// they were swapped so the caller can flip the operator.
    /// </summary>
    /// <remarks><c>100 &lt; c.Hp</c> must become <c>hp=gt:100</c>, not <c>hp=lt:100</c>.</remarks>
    private static (MemberExpression Member, object? Value, bool Flipped) OrientOperands(
        BinaryExpression node,
        ParameterExpression parameter)
    {
        if (TryGetMember(node.Left, parameter, out MemberExpression? leftMember))
        {
            return (leftMember, Evaluate(node.Right), false);
        }

        if (TryGetMember(node.Right, parameter, out MemberExpression? rightMember))
        {
            return (rightMember, Evaluate(node.Left), true);
        }

        throw Unsupported(node);
    }

    private static QueryFilter TranslateMethodCall(MethodCallExpression node, ParameterExpression parameter)
    {
        if (node.Object is null
            || !TryGetMember(node.Object, parameter, out MemberExpression? member)
            || node.Arguments.Count != 1)
        {
            throw Unsupported(node);
        }

        string field = FieldName(member);

        // Taken as written. QueryFilter escapes it exactly once; unescaping here
        // first turned a literal `%2A` into a `*` wildcard matching every card.
        // A pattern rather than string.IsNullOrEmpty, whose netstandard2.0
        // signature carries no NotNullWhen.
        if (Evaluate(node.Arguments[0])?.ToString() is not { Length: > 0 } value)
        {
            throw new NotSupportedException(
                $"'{node.Method.Name}' needs a non-empty value to build a filter for '{field}'.");
        }

        return node.Method.Name switch
        {
            nameof(string.Contains) => new QueryFilter(field, QueryOperator.Like, [value]),
            nameof(string.StartsWith) => new QueryFilter(field, QueryOperator.Like, [value + "*"]),
            nameof(string.EndsWith) => new QueryFilter(field, QueryOperator.Like, ["*" + value]),
            nameof(string.Equals) => new QueryFilter(field, QueryOperator.Equal, [value]),
            _ => throw Unsupported(node),
        };
    }

    private static QueryFilter Negate(QueryFilter filter, Expression source)
        => filter.Operator switch
        {
            QueryOperator.Like => filter with { Operator = QueryOperator.NotLike },
            QueryOperator.Equal => filter with { Operator = QueryOperator.NotEqual },
            QueryOperator.NotEqual => filter with { Operator = QueryOperator.Equal },
            QueryOperator.Null => filter with { Operator = QueryOperator.NotNull },
            QueryOperator.NotNull => filter with { Operator = QueryOperator.Null },
            _ => throw Unsupported(source),
        };

    /// <summary>
    /// Recognises a direct property of the queried model, unwrapping the
    /// conversions the compiler inserts for nullable comparisons.
    /// </summary>
    private static bool TryGetMember(
        Expression expression,
        ParameterExpression parameter,
        out MemberExpression member)
    {
        Expression current = expression;

        while (current is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert)
        {
            current = convert.Operand;
        }

        // Only a property read straight off the lambda parameter is filterable.
        // `c.Set.Name` and `c.Name.Length` have no API counterpart.
        if (current is MemberExpression { Member: PropertyInfo } candidate
            && candidate.Expression == parameter)
        {
            member = candidate;
            return true;
        }

        member = null!;
        return false;
    }

    private static string FieldName(MemberExpression member)
    {
        string name = member.Member.Name;

        return string.Concat(char.ToLowerInvariant(name[0]), name.AsSpan(1).ToString());
    }

    /// <summary>
    /// Reads a value out of the tree by interpreting it, without compiling it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A captured local becomes a field on a compiler-generated closure, so it
    /// is read reflectively. Beyond that, the common value shapes are
    /// interpreted: array elements, indexers (<c>list[i]</c>), parameterless
    /// instance methods (<c>name.Trim()</c>), conversions and user-defined
    /// conversion operators. Each is a
    /// metadata read or a <see cref="MethodBase.Invoke(object, object[])"/> of a
    /// method the caller's code already references — never code generation —
    /// so it stays valid under Native AOT, unlike
    /// <see cref="LambdaExpression.Compile()"/> (DECISIONS #4).
    /// </para>
    /// <para>
    /// Static method calls and methods taking arguments stay unsupported: hoist
    /// them into a local first.
    /// </para>
    /// </remarks>
    private static object? Evaluate(Expression expression)
    {
        switch (expression)
        {
            case ConstantExpression constant:
                return constant.Value;

            // A user-defined implicit or explicit operator is a static method of
            // one argument; invoking it is what the cast means.
            case UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked, Method: not null } convert:
                {
                    // A lifted conversion of a null operand is null without calling
                    // the operator; reflection would pass default(S) instead.
                    object? operand = Evaluate(convert.Operand);
                    return operand is null && convert.IsLifted
                        ? null
                        : Invoke(convert.Method, instance: null, [operand]);
                }

            case UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert:
                return ConvertValue(Evaluate(convert.Operand), convert);

            case MemberExpression { Expression: null } staticMember:
                return ReadMember(staticMember.Member, instance: null);

            case MemberExpression member:
                return ReadMember(member.Member, Receiver(member.Expression, expression));

            case BinaryExpression { NodeType: ExpressionType.ArrayIndex } index:
                return ((Array)Receiver(index.Left, expression)).GetValue(Convert.ToInt64(Evaluate(index.Right), CultureInfo.InvariantCulture));

            case MethodCallExpression { Object: not null } call
                when call.Arguments.Count == 0 || call.Method.IsSpecialName && string.Equals(call.Method.Name, "get_Item", StringComparison.Ordinal):
                return Invoke(call.Method, Receiver(call.Object, expression), call.Arguments.Select(Evaluate).ToArray());

            default:
                throw Unsupported(expression);
        }
    }

    /// <summary>Evaluates the object a member, index or call is applied to, rejecting null.</summary>
    private static object Receiver(Expression receiver, Expression node)
        => Evaluate(receiver) ?? throw new NotSupportedException(
            $"Cannot evaluate '{node}' while building a filter: '{receiver}' is null.");

    /// <summary>Invokes a method, surfacing the caller's own exception rather than the reflection wrapper.</summary>
    private static object? Invoke(MethodInfo method, object? instance, object?[] arguments)
    {
        try
        {
            return method.Invoke(instance, arguments);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    /// <summary>
    /// Applies a conversion with C# cast semantics for the numeric cases a
    /// filter value can take: a floating value cast to an integral type is
    /// truncated, not rounded.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// The value does not fit the target type (<c>(byte)300</c>,
    /// <c>(int)double.NaN</c>), so filter-building errors keep one documented
    /// type; the conversion's own exception is the inner exception.
    /// </exception>
    private static object? ConvertValue(object? value, UnaryExpression convert)
    {
        Type target = Nullable.GetUnderlyingType(convert.Type) ?? convert.Type;

        if (value is null || target.IsInstanceOfType(value) || value is not IConvertible)
        {
            return value;
        }

        try
        {
            if (value is double or float or decimal && target.IsPrimitive && target != typeof(double) && target != typeof(float))
            {
                value = decimal.Truncate(Convert.ToDecimal(value, CultureInfo.InvariantCulture));
            }

            return Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is OverflowException or InvalidCastException or ArgumentException or FormatException)
        {
            throw new NotSupportedException(
                $"Cannot evaluate '{convert}' while building a filter: {exception.Message}",
                exception);
        }
    }

    private static object? ReadMember(MemberInfo member, object? instance)
        => member switch
        {
            FieldInfo field => field.GetValue(instance),
            PropertyInfo property => Invoke(property.GetMethod!, instance, []),
            _ => throw new NotSupportedException(
                $"Cannot read a value from '{member.Name}' while building a filter."),
        };

    /// <summary>
    /// Renders a filter value as the API expects to receive it.
    /// </summary>
    /// <param name="value">The value pulled out of the expression.</param>
    /// <returns>The text to place after the operator.</returns>
    /// <remarks>
    /// <para>
    /// Invariant culture is not optional: a machine with a comma decimal
    /// separator would otherwise emit a filter the API cannot parse.
    /// </para>
    /// <para>
    /// Internal rather than private so it can be tested directly. No model
    /// property is currently a <see cref="bool"/>, so the boolean branch is
    /// unreachable through a predicate — but leaving it out would make the
    /// first such property silently emit <c>"True"</c> instead of
    /// <c>"true"</c>.
    /// </para>
    /// </remarks>
    internal static string Format(object value)
        => value switch
        {
            string text => text,
            bool flag => flag ? "true" : "false",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };

    private static NotSupportedException Unsupported(Expression node)
        => new(
            $"The TCGdex API has no filter matching '{node}'. Supported forms are: " +
            "equality and inequality, the numeric comparisons < <= > >=, null and " +
            "non-null checks, string Contains/StartsWith/EndsWith (optionally negated), " +
            "&& between fields, and || within a single field.");
}
