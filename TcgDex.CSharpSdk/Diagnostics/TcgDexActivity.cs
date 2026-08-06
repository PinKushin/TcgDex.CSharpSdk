namespace TcgDex.Diagnostics;

using System.Diagnostics;

/// <summary>
/// Distributed-tracing spans for SDK operations.
/// </summary>
/// <remarks>
/// <para>
/// Exposed as a plain <see cref="System.Diagnostics.ActivitySource"/>, which is
/// .NET's built-in tracing primitive and what OpenTelemetry consumes. Subscribing
/// is one line in the consumer's application:
/// </para>
/// <code>
/// builder.Services.AddOpenTelemetry()
///     .WithTracing(t => t.AddSource(TcgDexActivity.SourceName));
/// </code>
/// <para>
/// When nothing subscribes, starting an activity returns <see langword="null"/>
/// and the cost is a null check — so this is free for consumers who do not want
/// it.
/// </para>
/// <para>
/// Spans cover <em>SDK operations</em> ("get a card"), not raw HTTP.
/// <c>HttpClient</c> already emits its own spans, so a request made through
/// <c>AddTcgDex</c> nests naturally underneath these rather than duplicating
/// them.
/// </para>
/// </remarks>
public static class TcgDexActivity
{
    /// <summary>
    /// The activity source name to subscribe to. Stable across versions.
    /// </summary>
    public const string SourceName = "TcgDex.CSharpSdk";

    /// <summary>The source SDK operations are recorded on.</summary>
    internal static ActivitySource Source { get; } = new(SourceName, ThisAssemblyVersion);

    /// <summary>
    /// Starts a client span for an SDK operation.
    /// </summary>
    /// <param name="operation">The operation name, for example <c>Cards.GetAsync</c>.</param>
    /// <returns>The activity, or <see langword="null"/> when nothing is listening.</returns>
    internal static Activity? Start(string operation)
        => Source.StartActivity(operation, ActivityKind.Client);

    /// <summary>
    /// Records a failure on the current span, following OpenTelemetry
    /// conventions so it surfaces as an error in any compliant backend.
    /// </summary>
    /// <param name="activity">The span, which may be null when nothing is listening.</param>
    /// <param name="exception">The failure.</param>
    internal static void RecordFailure(Activity? activity, Exception exception)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetStatus(ActivityStatusCode.Error, exception.Message);
        activity.AddTag("error.type", exception.GetType().FullName);
    }

    private const string ThisAssemblyVersion = "0.1.0";
}
