using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlObserver.Observability;
using SqlObserver.Server;

namespace SqlObserver.ApiContractTests;

public sealed class RuntimeDiagnosticsTests
{
    [Fact]
    public async Task FailureCorrelationConnectsResponseLogAndTraceWithoutLeakingInputsOrMetricIdentities()
    {
        const string secret = "Password=do-not-log; SELECT private_customer FROM orders";
        var logger = new CapturingLogger();
        var observations = new ConcurrentQueue<(string Name, Dictionary<string, object?> Tags)>();
        var activities = new ConcurrentQueue<Activity>();
        using var traces = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ObservabilityContract.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => activities.Enqueue(activity),
        };
        ActivitySource.AddActivityListener(traces);
        using var metrics = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == ObservabilityContract.MeterName)
                    listener.EnableMeasurementEvents(instrument);
            },
        };
        metrics.SetMeasurementEventCallback<long>((instrument, _, tags, _) => observations.Enqueue((instrument.Name, tags.ToArray().ToDictionary())));
        metrics.SetMeasurementEventCallback<double>((instrument, _, tags, _) => observations.Enqueue((instrument.Name, tags.ToArray().ToDictionary())));
        metrics.Start();
        using var services = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        using var body = new MemoryStream();
        context.Response.Body = body;
        context.Request.Method = "GET";
        context.Request.Path = "/api/v1/observation-targets/secret-target/health";
        context.Request.QueryString = new QueryString("?password=do-not-log");
        context.Request.Headers["X-Correlation-ID"] = secret;
        context.SetEndpoint(new RouteEndpoint(_ => Task.CompletedTask,
            RoutePatternFactory.Parse("/api/v1/observation-targets/{instanceId:guid}/health"), 0,
            EndpointMetadataCollection.Empty, "Health"));
        var middleware = new SafeApiExceptionMiddleware(_ => throw new IOException(secret), logger);

        await middleware.InvokeAsync(context);

        Assert.Equal(500, context.Response.StatusCode);
        string correlation = context.Response.Headers["X-Correlation-ID"].ToString();
        Assert.True(Guid.TryParseExact(correlation, "D", out _));
        body.Position = 0;
        using var reader = new StreamReader(body);
        string response = await reader.ReadToEndAsync();
        Assert.Contains(correlation, response, StringComparison.Ordinal);
        var logged = Assert.Single(logger.Messages);
        Assert.Null(logged.Exception);
        Assert.Contains(correlation, logged.Text, StringComparison.Ordinal);
        Assert.Contains("dependency_failure", logged.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("do-not-log", logged.Text + response, StringComparison.Ordinal);
        Assert.DoesNotContain("private_customer", logged.Text + response, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-target", logged.Text + response, StringComparison.Ordinal);
        Assert.Contains(activities.SelectMany(activity => activity.Events), entry => entry.Tags.Any(tag => tag.Key == "correlation.id" && Equals(tag.Value, correlation)));
        foreach (string name in new[] { "sqlobserver.http.request.duration", "sqlobserver.http.request.errors" })
        {
            var measured = observations.First(item => item.Name == name);
            Assert.Equal("/api/v1/observation-targets/{instanceId:guid}/health", measured.Tags["http.route"]);
            Assert.DoesNotContain(measured.Tags, tag => tag.Key.Contains("correlation", StringComparison.Ordinal));
            Assert.DoesNotContain(measured.Tags.Values, value => value?.ToString()?.Contains("secret-target", StringComparison.Ordinal) == true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EndpointFailureStatusRetainsACopyableCorrelationReference(bool endpointSuppliesReference)
    {
        var logger = new CapturingLogger();
        var context = new DefaultHttpContext();
        var reference = Guid.NewGuid().ToString("D");
        context.Request.Headers["X-Correlation-ID"] = endpointSuppliesReference ? Guid.NewGuid().ToString("D") : reference;
        var middleware = new SafeApiExceptionMiddleware(http =>
        {
            http.Response.StatusCode = 503;
            if (endpointSuppliesReference) http.Response.Headers["X-Correlation-ID"] = reference;
            return Task.CompletedTask;
        }, logger);
        await middleware.InvokeAsync(context);
        Assert.Equal(reference, context.Response.Headers["X-Correlation-ID"]);
        Assert.Contains(reference, Assert.Single(logger.Messages).Text, StringComparison.Ordinal);
    }

    private sealed class CapturingLogger : ILogger<SafeApiExceptionMiddleware>
    {
        public List<(string Text, Exception? Exception)> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add((formatter(state, exception), exception));
    }
}
