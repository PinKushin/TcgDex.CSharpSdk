// Modern C# lowers several language features onto BCL types that netstandard2.0
// does not define. Declaring them here is the supported way to use those
// features on older targets: the compiler only requires that the type exists
// with the right name, shape and accessibility — it never calls into it.
//
// These are internal, so they cannot collide with a consumer's own copies and
// never appear in the public API. The whole file compiles away on net8.0+,
// where the real types come from the shared framework.
//
// Nothing here changes behaviour. If any of it did, the two modern targets
// would disagree with netstandard2.0 at runtime, and the shared test suite
// would be proving something different on each.

#if NETSTANDARD2_0

namespace System.Runtime.CompilerServices
{
    /// <summary>
    /// Marker the compiler requires to emit an <c>init</c> accessor. Its
    /// presence is the entire contract; the type is never instantiated.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
    internal static class IsExternalInit
    {
    }

    /// <summary>
    /// Applied by the compiler to types and members that carry the
    /// <c>required</c> modifier.
    /// </summary>
    [AttributeUsage(
        AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Field | AttributeTargets.Property,
        AllowMultiple = false,
        Inherited = false)]
    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
    internal sealed class RequiredMemberAttribute : Attribute
    {
    }

    /// <summary>
    /// Emitted alongside <see cref="RequiredMemberAttribute"/> so that a
    /// compiler which does not understand the feature refuses the assembly
    /// rather than silently ignoring the requirement.
    /// </summary>
    [AttributeUsage(AttributeTargets.All, AllowMultiple = true, Inherited = false)]
    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
    internal sealed class CompilerFeatureRequiredAttribute : Attribute
    {
        public CompilerFeatureRequiredAttribute(string featureName) => FeatureName = featureName;

        public string FeatureName { get; }

        public bool IsOptional { get; init; }
    }
}

namespace System.Runtime.CompilerServices
{
    /// <summary>
    /// Lets a parameter capture the caller's source text for another argument,
    /// which is how the guard helpers report a parameter name without every
    /// call site repeating it.
    /// </summary>
    [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
    internal sealed class CallerArgumentExpressionAttribute : Attribute
    {
        public CallerArgumentExpressionAttribute(string parameterName) => ParameterName = parameterName;

        public string ParameterName { get; }
    }
}

namespace System.Diagnostics.CodeAnalysis
{
    /// <summary>
    /// Tells the compiler a constructor sets every <c>required</c> member, so
    /// callers need not.
    /// </summary>
    [AttributeUsage(AttributeTargets.Constructor, AllowMultiple = false, Inherited = false)]
    [ExcludeFromCodeCoverage]
    internal sealed class SetsRequiredMembersAttribute : Attribute
    {
    }
}

#endif
