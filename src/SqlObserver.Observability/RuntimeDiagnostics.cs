using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace SqlObserver.Observability;

/// <summary>Product-owned runtime signals contain only bounded categories, never provider messages or input values.</summary>
public static class RuntimeDiagnostics
{
    private static readonly Meter Metrics = new(ObservabilityContract.MeterName);
    private static readonly ActivitySource Activities = new(ObservabilityContract.ActivitySourceName);
    private static readonly Histogram<double> RequestDuration = Metrics.CreateHistogram<double>("sqlobserver.http.request.duration", "ms");
    private static readonly Counter<long> RequestErrors = Metrics.CreateCounter<long>("sqlobserver.http.request.errors");
    private static readonly Counter<long> AnalyticsFailures = Metrics.CreateCounter<long>("sqlobserver.analytics.failures");
    private static readonly Histogram<double> AnalyticsQueueAge = Metrics.CreateHistogram<double>("sqlobserver.analytics.queue.age", "s");

    public static string FailureCategory(Exception exception) => exception switch
    {
        OperationCanceledException => "cancelled",
        TimeoutException => "timeout",
        UnauthorizedAccessException => "authorization",
        InvalidDataException or ArgumentException => "invalid_contract",
        InvalidOperationException => "state_conflict",
        _ => "dependency_failure",
    };

    public static Activity? StartRequest() => Activities.StartActivity("http.request", ActivityKind.Server);

    public static void RecordRequest(Activity? activity, string method, string route, int statusCode,
        string category, string correlationId, double durationMilliseconds)
    {
        // The host supplies its registered route template; neither a raw URL nor identifiers are used as labels.
        route = route.Length <= ObservabilityContract.MaximumAttributeValueLength
            ? route : route[..ObservabilityContract.MaximumAttributeValueLength];
        var tags = new TagList { { "http.request.method", method }, { "http.route", route }, { "http.response.status_code", statusCode } };
        RequestDuration.Record(Math.Max(0, durationMilliseconds), tags);
        if (statusCode < 400) return;
        tags.Add("failure.category", category);
        RequestErrors.Add(1, tags);
        activity?.SetStatus(ActivityStatusCode.Error);
        activity?.AddEvent(new ActivityEvent("request.failed", tags: new ActivityTagsCollection
        {
            { "failure.category", category }, { "correlation.id", correlationId },
            { "http.response.status_code", statusCode }, { "http.route", route },
        }));
    }

    public static void RecordAnalyticsFailure(string phase, string category) =>
        AnalyticsFailures.Add(1, new TagList { { "phase", phase }, { "failure.category", category } });

    public static void RecordAnalyticsQueueAge(string jobKind, TimeSpan age) =>
        AnalyticsQueueAge.Record(Math.Max(0, age.TotalSeconds), new TagList { { "job.kind", jobKind } });
}
